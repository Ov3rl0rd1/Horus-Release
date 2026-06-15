using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Protocols
{
    /// <summary>
    /// Protocol adapter for olcRTC (https://github.com/openlibrecommunity/olcrtc).
    /// olcRTC tunnels traffic via WebRTC data channels over Yandex Telemost / WB Stream
    /// to bypass whitelist-based blocking.
    ///
    /// Platform integration:
    ///   Android  — binary process (olcrtc binary) + hev-socks5-tunnel for TUN bridging
    ///   Windows  — binary process + WinDivert/WinTun
    ///   iOS/macOS — NetworkExtension PacketTunnel provider calling olcRTC C library
    ///
    /// Status: ARCHITECTURE STUB — binary integration pending library release.
    ///         The IVpnProtocol contract is fully implemented so VpnManager can
    ///         switch to this protocol transparently once the binary is available.
    /// </summary>
    public class OlcRtcProtocol : IVpnProtocol
    {
        private readonly IProcessRunner _runner;
        private readonly IVpnPlatformService _vpn;

        private ProcessHandle? _handle;
        private string? _configPath;
        private CancellationTokenSource? _monitorCts;

        public OlcRtcProtocol(IProcessRunner runner, IVpnPlatformService vpn)
        {
            _runner = runner;
            _vpn = vpn;
        }

        public ProtocolType Type => ProtocolType.OlcRtc;
        public ProtocolConfig Config => new OlcRtcConfig();

        public event EventHandler<VpnStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<TrafficStatisticsEventArgs>? StatisticsUpdated;
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<ProtocolErrorEventArgs>? ErrorOccurred;

        public IReadOnlyList<ProtocolParam> GetEditableParams() =>
        [
            new ProtocolParam
            {
                Key = "relayMode",
                Label = "Relay Mode",
                ParamType = ParamType.Select,
                DefaultValue = "auto",
                Options = ["auto", "telemost", "wb_stream"]
            },
            new ProtocolParam
            {
                Key = "stunServer",
                Label = "STUN Server",
                ParamType = ParamType.String,
                DefaultValue = "stun:stun.l.google.com:19302"
            }
        ];

        public async Task ConnectAsync(ProtocolConfig config, CancellationToken ct = default)
        {
            if (config is not OlcRtcConfig olcCfg)
                throw new ArgumentException("Expected OlcRtcConfig.");

            StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Connecting, null));

            _configPath = await WriteConfigAsync(olcCfg, ct);

            try
            {
                var binaryName = GetPlatformBinaryName();
                _handle = await _runner.StartAsync(binaryName, ["--config", _configPath], null);

                // _monitorCts must exist before WaitForReadyAsync so the drain tasks
                // it spawns can be cancelled on disconnect.
                _monitorCts = new CancellationTokenSource();
                await WaitForReadyAsync(ct);
                // DrainStreamAsync tasks are already running via WaitForReadyAsync.

                StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Connected, null));
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Disconnected, ex.Message));
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            _monitorCts?.Cancel();
            _monitorCts = null;

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
            Task.FromResult(config is OlcRtcConfig c && !string.IsNullOrEmpty(c.SignalServer));

        public void ApplyParams(IDictionary<string, object> values)
        {
            // Parameters applied at next ConnectAsync call via config object
        }

        // ── Private helpers ─────────────────────────────────────────────────

        private static async Task<string> WriteConfigAsync(OlcRtcConfig cfg, CancellationToken ct)
        {
            var path = Path.Combine(Path.GetTempPath(), $"olcrtc_{Guid.NewGuid():N}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
            });
            await File.WriteAllTextAsync(path, json, ct);
            return path;
        }

        private async Task WaitForReadyAsync(CancellationToken ct)
        {
            if (_handle?.ProcessRef is not System.Diagnostics.Process proc) return;

            var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Drain both streams in parallel so neither pipe deadlocks
            _ = DrainStreamAsync(proc.StandardOutput, "OUT", readyTcs, _monitorCts!.Token);
            _ = DrainStreamAsync(proc.StandardError, "ERR", readyTcs, _monitorCts!.Token);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await readyTcs.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (proc.HasExited)
                    throw new InvalidOperationException($"olcRTC process exited before becoming ready.");
                OutputReceived?.Invoke(this, "[olcRTC] Timeout — proceeding anyway.");
            }
        }

        private async Task DrainStreamAsync(
            System.IO.StreamReader reader,
            string label,
            TaskCompletionSource<bool> readyTcs,
            CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;

                    OutputReceived?.Invoke(this, $"[{label}] {line}");

                    if (!readyTcs.Task.IsCompleted &&
                        (line.Contains("ready") || line.Contains("connected") || line.Contains("socks5")))
                        readyTcs.TrySetResult(true);

                    var stats = _vpn.GetTunnelStats();
                    if (stats.Length >= 4 && (stats[0] | stats[1] | stats[2] | stats[3]) != 0)
                        StatisticsUpdated?.Invoke(this,
                            new TrafficStatisticsEventArgs(stats[0], stats[2], stats[1], stats[3]));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new ProtocolErrorEventArgs($"DRAIN-{label}", ex.Message, false));
            }
            finally
            {
                readyTcs.TrySetException(new InvalidOperationException("olcRTC output stream closed."));
            }
        }

        private static string GetPlatformBinaryName()
        {
#if WINDOWS
            return "olcrtc.exe";
#elif ANDROID
            return "olcrtc.so";
#else
            return "olcrtc";
#endif
        }
    }

    // ── Config ───────────────────────────────────────────────────────────────

    public class OlcRtcConfig : ProtocolConfig
    {
        public override ProtocolType ProtocolType => ProtocolType.OlcRtc;

        public string SignalServer { get; set; } = string.Empty;
        public string StunServer { get; set; } = "stun:stun.l.google.com:19302";
        public string RelayMode { get; set; } = "auto"; // auto | telemost | wb_stream
        public string Socks5Address { get; set; } = "127.0.0.1:1081";

        public override string ToConfig()
        {
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                signal_server = SignalServer,
                stun_server = StunServer,
                relay_mode = RelayMode,
                socks5 = new { listen = Socks5Address }
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
    }
}
