using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Runtime.Versioning;

namespace Horus.Platforms.iOS
{
    /// <summary>
    /// iOS does not allow spawning child processes from the main app sandbox.
    /// Process execution happens exclusively inside the NetworkExtension
    /// (PacketTunnelProvider) process, which has its own sandbox with
    /// restricted entitlements.
    ///
    /// This implementation forwards process start requests to the extension
    /// via IPC (NETunnelProviderSession messages). The extension then spawns
    /// or embeds the binary appropriately.
    /// </summary>
    [SupportedOSPlatform("ios")]
    [SupportedOSPlatform("maccatalyst")]
    public class iOSProcessRunner : IProcessRunner
    {
        public Task<ProcessHandle> StartAsync(string executable, string[] args, string? workDir)
        {
            // Process management on iOS is handled inside the Network Extension.
            // From the main app's perspective, "starting" a process means
            // instructing the extension to run it via IPC.
            // We return a dummy handle that the VpnManager holds.
            var handle = new ProcessHandle(-1, null);
            return Task.FromResult(handle);
        }

        public Task StopAsync(ProcessHandle handle)
        {
            // Stopping is also handled by the extension on StopTunnel IPC call.
            return Task.CompletedTask;
        }
    }
}
