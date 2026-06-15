using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Diagnostics;

namespace Horus.Protocols
{
    public class Hysteria2Protocol : IVpnProtocol
    {
        private readonly IProcessRunner _runner;
        private readonly IVpnPlatformService _vpn;

        private ProcessHandle? _handle;
        private string? _configPath;

        // Signals that the binary is ready (SOCKS5 proxy is listening)
        private TaskCompletionSource<bool>? _readyTcs;

        // Cancels the output-draining background tasks on disconnect
        private CancellationTokenSource? _outputCts;

        public ProtocolType Type => ProtocolType.Hysteria2;
        public ProtocolConfig Config => new Hysteria2Config();

        public event EventHandler<VpnStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<TrafficStatisticsEventArgs>? StatisticsUpdated;
        public event EventHandler<ProtocolErrorEventArgs>? ErrorOccurred;
        public event EventHandler<string>? OutputReceived;

        public Hysteria2Protocol(IProcessRunner runner, IVpnPlatformService vpn)
        {
            _runner = runner;
            _vpn = vpn;
        }

        public IReadOnlyList<ProtocolParam> GetEditableParams() =>
        [
            new ProtocolParam
            {
                Key = "hopInterval",
                Label = "UDP Port Hopping",
                ParamType = ParamType.Bool,
                DefaultValue = false
            }
        ];

        public async Task ConnectAsync(ProtocolConfig config, CancellationToken ct = default)
        {
            if (config is not Hysteria2Config h2cfg)
                throw new ArgumentException("Expected Hysteria2Config.");

            StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Connecting, null));

            _configPath = await WriteConfigFileAsync(h2cfg, ct);
            _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _outputCts = new CancellationTokenSource();

            try
            {
                _handle = await _runner.StartAsync("hysteria.so", ["-c", _configPath], null);

                if (_handle.ProcessRef is not Process proc)
                    throw new InvalidOperationException("Process runner did not return a Process reference.");

                // Drain stdout and stderr in parallel background tasks.
                // CRITICAL: both streams must be consumed concurrently to avoid pipe-buffer deadlock.
                _ = DrainStreamAsync(proc.StandardOutput, "OUT", _outputCts.Token);
                _ = DrainStreamAsync(proc.StandardError, "ERR", _outputCts.Token);

                await WaitForReadyAsync(ct);

                StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Connected, null));
            }
            catch
            {
                _outputCts?.Cancel();
                StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Disconnected, "Failed to start"));
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            _outputCts?.Cancel();
            _outputCts = null;
            _readyTcs = null;

            if (_handle != null)
            {
                await _runner.StopAsync(_handle);
                _handle = null;
            }

            if (_configPath != null && File.Exists(_configPath))
            {
                File.Delete(_configPath);
                _configPath = null;
            }

            StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Disconnected, null));
        }

        public Task<bool> ValidateConfigAsync(ProtocolConfig config) =>
            Task.FromResult(config is Hysteria2Config h && !string.IsNullOrEmpty(h.RenderedConfig ?? h.Server));

        public void ApplyParams(IDictionary<string, object> values) { }

        // ── Private ──────────────────────────────────────────────────────────

        private static async Task<string> WriteConfigFileAsync(Hysteria2Config cfg, CancellationToken ct)
        {
            var path = Path.Combine(Path.GetTempPath(), $"hysteria2_{Guid.NewGuid():N}.yaml");
            await File.WriteAllTextAsync(path, cfg.RenderedConfig ?? cfg.ToConfig(), ct);
            return path;
        }

        /// <summary>
        /// Waits up to 20 seconds for the "ready" signal coming from DrainStreamAsync.
        /// If the timeout fires and the process is still alive, we assume it's running
        /// (some hysteria2 builds may not print the exact expected strings).
        /// </summary>
        private async Task WaitForReadyAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await _readyTcs!.Task.WaitAsync(timeout.Token);
                OutputReceived?.Invoke(this, "[Hysteria2] Ready signal received.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — check if the process is still alive
                if (_handle?.ProcessRef is Process { HasExited: true } p)
                    throw new InvalidOperationException(
                        $"Hysteria2 process exited (code {p.ExitCode}) before becoming ready.");

                // Process is alive; assume SOCKS5 is up (some versions log to JSON or different strings)
                OutputReceived?.Invoke(this, "[Hysteria2] Timeout waiting for ready signal — proceeding anyway.");
            }
        }

        /// <summary>
        /// Drains a process output stream (stdout or stderr) on a background thread.
        /// Every line is fired through <see cref="OutputReceived"/> and checked for the ready signal.
        /// </summary>
        private async Task DrainStreamAsync(StreamReader reader, string streamLabel, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break; // stream closed / process exited

                    // Forward raw line to subscribers (admin log, error reporting, etc.)
                    OutputReceived?.Invoke(this, $"[{streamLabel}] {line}");

                    // Signal readiness
                    if (_readyTcs is { Task.IsCompleted: false })
                    {
                        if (IsReadyLine(line))
                            _readyTcs.TrySetResult(true);
                    }

                    // Parse stats from platform layer (unchanged from original)
                    TryEmitStats();
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this,
                    new ProtocolErrorEventArgs($"DRAIN-{streamLabel}", ex.Message, false));
            }
            finally
            {
                // If the process exited unexpectedly and nobody set the TCS yet, unblock WaitForReadyAsync
                _readyTcs?.TrySetException(
                    new InvalidOperationException("Hysteria2 process output stream closed unexpectedly."));
            }
        }

        private static bool IsReadyLine(string line)
        {
            // Hysteria2 v2 logs JSON: {"level":"info","msg":"client started",...}
            // Hysteria2 v1 logs plain text
            return line.Contains("client started", StringComparison.OrdinalIgnoreCase)
                || line.Contains("\"started\"", StringComparison.OrdinalIgnoreCase)
                || line.Contains("socks5", StringComparison.OrdinalIgnoreCase)
                || line.Contains("listening", StringComparison.OrdinalIgnoreCase)
                || line.Contains("udp session started", StringComparison.OrdinalIgnoreCase);
        }

        private void TryEmitStats()
        {
            try
            {
                long[] stats = _vpn.GetTunnelStats();
                if (stats.Length >= 4 && (stats[0] | stats[1] | stats[2] | stats[3]) != 0)
                    StatisticsUpdated?.Invoke(this,
                        new TrafficStatisticsEventArgs(stats[0], stats[2], stats[1], stats[3]));
            }
            catch { /* GetTunnelStats is best-effort */ }
        }
    }
}
