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

        private string _activeOfferId = string.Empty;
        private XrayConfig? _lastConfig;

        public XrayProtocol(IVpnPlatformService vpn)
        {
            _vpn = vpn;
        }

        /// <summary>Which outbound the running instance is proxying through.</summary>
        public string ActiveOfferId => _activeOfferId;

        public ProtocolConfig Config => _lastConfig
            ?? throw new InvalidOperationException("xray has not been configured yet.");

        /// <summary>Version string of the linked core. Cheap probe that separates
        /// "library missing" from "library present but failing to start".</summary>
        public static string CoreVersion => XrayInterop.Version();

        /// <summary>True while the core reports a live instance.</summary>
        public static bool IsCoreRunning => XrayInterop.IsRunning();

        /// <summary>
        /// Drops pooled transport sessions so the next request dials fresh ones, and reports
        /// how many went. Returns -1 when the core could not be asked.
        ///
        /// <para>Called on a network handover, where the sessions are already dead and only
        /// the transport does not know it yet. Far cheaper than the reconnect it replaces:
        /// the instance and the TUN stay up.</para>
        /// </summary>
        public static int ResetConnections()
        {
            if (!IsCoreRunning) return 0;

            var closed = XrayInterop.ResetConnections();
            if (closed >= 0) Diag.Info("xray", $"reset {closed} pooled session(s)");
            else Diag.Warn("xray", "reset connections unsupported by this core build");
            return closed;
        }

        /// <summary>Hands freed memory back to the OS. Best-effort and asynchronous.</summary>
        public static void ForceGc() => XrayInterop.ForceGc();

        /// <summary>
        /// Pauses or resumes the core's background housekeeping, tracking Doze.
        ///
        /// <para>Logged at info rather than trace because the wiring is easy to get wrong
        /// and impossible to verify from the outside: two lines a day is a cheap way to
        /// prove from a bug report that the idle signal is reaching the core.</para>
        /// </summary>
        public static void SetPaused(bool paused)
        {
            if (!IsCoreRunning) return;

            var ok = paused ? XrayInterop.Sleep() : XrayInterop.Wake();
            if (ok) Diag.Info("xray", paused ? "housekeeping paused (doze)" : "housekeeping resumed");
            else Diag.Warn("xray", "sleep/wake unsupported by this core build");
        }

        /// <summary>Whether the core has housekeeping paused; null when it cannot say.</summary>
        public static bool? IsPaused => XrayInterop.IsPaused();

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

        /// <summary>
        /// Whether something is already listening on the SOCKS5 port. Only ever used to
        /// explain a start failure, never to pre-empt one: a platform that cannot
        /// enumerate listeners must not be able to block an otherwise fine connect.
        /// </summary>
        private static bool IsPortTaken(int port)
        {
            try
            {
                return System.Net.NetworkInformation.IPGlobalProperties
                    .GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(e => e.Port == port);
            }
            catch { return false; }
        }

        public Task ConnectAsync(ProtocolConfig config, CancellationToken ct = default)
        {
            if (config is not XrayConfig xrayConfig)
                throw new ArgumentException("Expected XrayConfig.", nameof(config));

            _lastConfig = xrayConfig;
            _activeOfferId = config.OfferId;

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
                OutputReceived?.Invoke(this, $"[xray] Config accepted ({_activeOfferId}).");

                try
                {
                    XrayInterop.Start(json);
                }
                catch (Exception ex) when (IsPortTaken(xrayConfig.SocksPort))
                {
                    // Far more likely on desktop than on Android, where every app gets its
                    // own network namespace for loopback purposes. Another VPN client
                    // parked on the same conventional port produces a core start failure
                    // whose text says nothing about ports.
                    throw new InvalidOperationException(
                        $"Порт {xrayConfig.SocksPort} уже занят другой программой — " +
                        "скорее всего, другим VPN-клиентом. Закройте его и попробуйте снова. " +
                        $"({ex.Message})", ex);
                }

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
            // The outbound is the node's, so there is nothing here worth re-validating
            // field by field — the core is the authority on its own schema, and Test() is
            // the cheap way to ask it.
            if (config is not XrayConfig c || string.IsNullOrEmpty(c.OfferId))
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
