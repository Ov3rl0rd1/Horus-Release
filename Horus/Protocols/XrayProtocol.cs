using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Protocols
{
    /// <summary>
    /// Drives the bundled xray-core, which is linked as a <b>shared library</b> and runs
    /// in this process — not as a child process. One instance serves every protocol the
    /// node offers: VLESS/REALITY, Hysteria2 and olcRTC are outbounds inside the generated
    /// config, selected by the <see cref="XrayConfig.Link"/> handed in.
    ///
    /// Traffic reaches it through the SOCKS5 inbound on 127.0.0.1:1080, which the
    /// per-platform TUN bridge dials.
    /// </summary>
    public class XrayProtocol : IVpnProtocol
    {
        private readonly IVpnPlatformService _vpn;

        private ProtocolType _activeType = ProtocolType.Vless;
        private XrayConfig? _lastConfig;

        public XrayProtocol(IVpnPlatformService vpn)
        {
            _vpn = vpn;
        }

        /// <summary>Which outbound the running instance is proxying through.</summary>
        public ProtocolType Type => _activeType;

        public ProtocolConfig Config => _lastConfig
            ?? throw new InvalidOperationException("xray has not been configured yet.");

        /// <summary>Version string of the linked core. Cheap probe that separates
        /// "library missing" from "library present but failing to start".</summary>
        public static string CoreVersion => XrayInterop.Version();

        /// <summary>True while the core reports a live instance.</summary>
        public static bool IsCoreRunning => XrayInterop.IsRunning();

        public event EventHandler<VpnStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<TrafficStatisticsEventArgs>? StatisticsUpdated;
        public event EventHandler<ProtocolErrorEventArgs>? ErrorOccurred;
        public event EventHandler<string>? OutputReceived;

        public IReadOnlyList<ProtocolParam> GetEditableParams() =>
        [
            new ProtocolParam
            {
                Key = "logLevel",
                Label = "Core log level",
                ParamType = ParamType.Select,
                DefaultValue = "warning",
                Options = ["none", "error", "warning", "info", "debug"]
            }
        ];

        public Task ConnectAsync(ProtocolConfig config, CancellationToken ct = default)
        {
            if (config is not XrayConfig xrayConfig)
                throw new ArgumentException("Expected XrayConfig.", nameof(config));

            _lastConfig = xrayConfig;
            _activeType = xrayConfig.Link.Protocol;

            StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Connecting, null));

            try
            {
                // A previous instance blocks Start, and the fallback loop reaches here
                // once per protocol — so always clear first.
                XrayInterop.Stop();

                var json = xrayConfig.ToConfig();

                // Validate before starting: a wrong outbound schema surfaces here as a
                // precise parser message rather than as an opaque start failure.
                XrayInterop.Test(json);
                OutputReceived?.Invoke(this, $"[xray] Config accepted ({_activeType}).");

                XrayInterop.Start(json);
                OutputReceived?.Invoke(this,
                    $"[xray] Started {CoreVersion}; SOCKS5 on {xrayConfig.SocksAddress}:{xrayConfig.SocksPort}.");

                StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Connected, null));
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                XrayInterop.Stop();
                ErrorOccurred?.Invoke(this, new ProtocolErrorEventArgs("XRAY-START", ex.Message, true));
                StatusChanged?.Invoke(this,
                    new VpnStatusChangedEventArgs(VpnState.Disconnected, "Failed to start xray"));
                throw;
            }
        }

        public Task DisconnectAsync()
        {
            XrayInterop.Stop();

            // The rendered config held live credentials; drop the reference with it.
            _lastConfig = null;

            StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(VpnState.Disconnected, null));
            return Task.CompletedTask;
        }

        public Task<bool> ValidateConfigAsync(ProtocolConfig config)
        {
            if (config is not XrayConfig c
                || string.IsNullOrEmpty(c.Link.Host)
                || string.IsNullOrEmpty(c.Link.Credential))
                return Task.FromResult(false);

            try
            {
                XrayInterop.Test(c.ToConfig());
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public void ApplyParams(IDictionary<string, object> values)
        {
            if (_lastConfig is null) return;
            if (values.TryGetValue("logLevel", out var level) && level is string s)
                _lastConfig.LogLevel = s;
        }

        /// <summary>
        /// Reads the platform tunnel counters. Kept for callers that want a one-off
        /// sample; the live 1 Hz feed comes from <c>TrafficMonitorService</c>.
        /// </summary>
        public void EmitStatsSample()
        {
            try
            {
                long[] stats = _vpn.GetTunnelStats();
                if (stats.Length >= 4)
                    StatisticsUpdated?.Invoke(this,
                        new TrafficStatisticsEventArgs(stats[0], stats[2], stats[1], stats[3]));
            }
            catch { /* GetTunnelStats is best-effort */ }
        }
    }
}
