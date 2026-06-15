using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Text.Json;

namespace Horus.Protocols
{
    public class Hysteria2Protocol : IVpnProtocol
    {
        private readonly IProcessRunner _runner;
        private readonly IVpnPlatformService _vpn;

        private ProcessHandle? _handle;
        private string? _configPath;
        private CancellationTokenSource? _stdoutCts;

        public Hysteria2Protocol(IProcessRunner runner, IVpnPlatformService vpn)
        {
            _runner = runner;
            _vpn = vpn;

        }

        public ProtocolType Type => ProtocolType.Hysteria2;
        public ProtocolConfig Config => new Hysteria2Config();

        public event EventHandler<VpnStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<TrafficStatisticsEventArgs>? StatisticsUpdated;
        public event EventHandler<ProtocolErrorEventArgs>? ErrorOccurred;

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

            try
            {
                _handle = await _runner.StartAsync("hysteria.so", ["-c", _configPath], null);
                await WaitForReadyAsync(ct);
                _stdoutCts = new CancellationTokenSource();
                _ = SubscribeToStdoutAsync(_handle, _stdoutCts.Token);
                StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Connected, null));
            }
            catch
            {
                StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Disconnected, "Failed to start"));
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            _stdoutCts?.Cancel();
            _stdoutCts = null;

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
            Task.FromResult(config is Hysteria2Config h && !string.IsNullOrEmpty(h.RenderedConfig));

        public void ApplyParams(IDictionary<string, object> values) { }

        private static async Task<string> WriteConfigFileAsync(Hysteria2Config cfg, CancellationToken ct)
        {
            var path = Path.Combine(Path.GetTempPath(), "hysteria2.yaml");
            var yaml = cfg.RenderedConfig ?? cfg.ToConfig();
            await File.WriteAllTextAsync(path, yaml, ct);
            return path;
        }

        private async Task WaitForReadyAsync(CancellationToken ct)
        {
            if (_handle?.ProcessRef is not { } proc) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            while (!timeout.Token.IsCancellationRequested)
            {
                if (proc.HasExited)
                    throw new InvalidOperationException("Hysteria2 process exited unexpectedly.");

                var line = await proc.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) break;

                if (line.Contains("client started") || line.Contains("started") || line.Contains("socks5"))
                    return;
            }
        }

        private async Task SubscribeToStdoutAsync(ProcessHandle handle, CancellationToken ct)
        {
            if (handle.ProcessRef is not { } proc) return;

            try
            {
                while (!ct.IsCancellationRequested && !proc.HasExited)
                {
                    var line = await proc.StandardOutput.ReadLineAsync(ct);
                    if (line is null) break;

                    if (TryParseStats(out long up, out long down, out long totalUp, out long totalDown))
                        StatisticsUpdated?.Invoke(this, new TrafficStatisticsEventArgs(up, down, totalUp, totalDown));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new ProtocolErrorEventArgs("STDOUT", ex.Message, false));
            }
        }

        private bool TryParseStats(out long up, out long down, out long totalUp, out long totalDown)
        {
            up = down = totalUp = totalDown = 0;
            try
            {
                long[] stats = _vpn.GetTunnelStats();
                up = stats[0];
                down = stats[2];
                totalUp = stats[1];
                totalDown = stats[3];
                return true;
            }
            catch { return false; }
        }
    }
}
