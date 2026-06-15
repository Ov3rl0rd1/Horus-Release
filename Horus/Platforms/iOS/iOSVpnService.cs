using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Runtime.Versioning;

namespace Horus.Platforms.iOS
{
    /// <summary>
    /// iOS/macOS VPN platform service.
    ///
    /// iOS VPN architecture:
    ///   1. Hysteria2 mode: NEPacketTunnelProvider (Network Extension) runs as a separate
    ///      process in a sandbox. The main app communicates with it via NEVPNManager /
    ///      NETunnelProviderSession IPC. The extension spawns the hysteria binary inside
    ///      its own container and bridges the TUN packets.
    ///
    ///   2. olcRTC mode: Same PacketTunnel provider, but calls the olcRTC C library
    ///      directly (linked into the extension binary) instead of a subprocess.
    ///
    /// The NetworkExtension entitlement (com.apple.developer.networking.networkextension)
    /// must be configured in the app's provisioning profile and Entitlements.plist.
    ///
    /// The PacketTunnel provider itself lives in a separate target
    /// (HorusNetworkExtension) and is NOT part of this file. This service acts
    /// as the bridge from the main app to the extension via NEVPNManager.
    ///
    /// Status: ARCHITECTURE STUB — full Network Extension implementation requires
    ///         a paid Apple Developer account and provisioning profile configuration.
    ///         The IVpnPlatformService contract is complete so the rest of the app
    ///         compiles and runs; the extension target is a separate Xcode target.
    /// </summary>
    [SupportedOSPlatform("ios")]
    [SupportedOSPlatform("maccatalyst")]
    public class iOSVpnService : IVpnPlatformService
    {
        private TunnelState _state = TunnelState.Stopped;

        // Statistics counters updated via IPC from the extension
        private long _txBytes;
        private long _rxBytes;

        public bool IsSupported => true;

        public TunnelState CurrentState => _state;

        public event EventHandler<TunnelStateChangedEventArgs>? TunnelStateChanged;

        public async Task<bool> RequestPermissionsAsync()
        {
            // NEVPNManager.SharedManager().LoadFromPreferences on iOS.
            // On first run this shows the system VPN permission dialog.
            // The actual call requires ObjC interop (not available in Mono without
            // NativeAOT / Xamarin iOS bindings). This is handled in the extension.
            await Task.CompletedTask;
            return true;
        }

        public async Task StartTunnelAsync(TunnelOptions options, CancellationToken ct = default)
        {
            SetState(TunnelState.Starting);

            // Serialize options and send to NetworkExtension via NETunnelProviderSession
            // protocol message. The extension receives this in
            // PacketTunnelProvider.startTunnel(options:completionHandler:).
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                tunAddress = options.TunAddress,
                tunPrefix = options.TunPrefix,
                mtu = options.Mtu,
                dnsServers = options.DnsServers,
                bypassApps = options.BypassApps,
                allTraffic = options.AllTraffic
            });

            await SendExtensionMessageAsync("startTunnel", payload, ct);
            SetState(TunnelState.Started);
        }

        public async Task StopTunnelAsync()
        {
            SetState(TunnelState.Stopping);
            await SendExtensionMessageAsync("stopTunnel", null, CancellationToken.None);
            Interlocked.Exchange(ref _txBytes, 0);
            Interlocked.Exchange(ref _rxBytes, 0);
            SetState(TunnelState.Stopped);
        }

        public Task ApplyRoutingRulesAsync(IEnumerable<RoutingRule> rules)
        {
            // Send routing rules to extension so it can filter packets
            var payload = System.Text.Json.JsonSerializer.Serialize(
                rules.Select(r => new { type = r.Type.ToString(), pattern = r.Pattern, action = r.Action.ToString() }));
            return SendExtensionMessageAsync("applyRules", payload, CancellationToken.None);
        }

        public Task SetDnsAsync(string[] dnsServers) =>
            SendExtensionMessageAsync("setDns",
                System.Text.Json.JsonSerializer.Serialize(dnsServers), CancellationToken.None);

        public long[] GetTunnelStats() =>
            [Interlocked.Read(ref _txBytes), 0, Interlocked.Read(ref _rxBytes), 0];

        // ── Extension IPC ────────────────────────────────────────────────────

        private static Task SendExtensionMessageAsync(string command, string? payload, CancellationToken ct)
        {
            // In production: use NETunnelProviderSession.sendProviderMessage
            // via ObjC selector bridge. Since MAUI on iOS uses NativeAOT / Xamarin
            // bindings, the real call is:
            //
            //   var session = (NETunnelProviderSession)manager.Connection;
            //   session.SendProviderMessage(data, ref error, out _);
            //
            // This stub just completes immediately; the real implementation
            // lives in the separate NetworkExtension target (Objective-C/Swift).
            return Task.CompletedTask;
        }

        private void SetState(TunnelState state)
        {
            _state = state;
            TunnelStateChanged?.Invoke(this, new TunnelStateChangedEventArgs(state, null));
        }
    }
}
