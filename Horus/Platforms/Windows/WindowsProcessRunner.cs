using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Horus.Platforms.Windows
{
    [SupportedOSPlatform("windows")]
    public class WindowsProcessRunner : IProcessRunner
    {
        private readonly IBinaryUpdaterService _updater;

        public WindowsProcessRunner(IBinaryUpdaterService updater)
        {
            _updater = updater;
        }

        public async Task<ProcessHandle> StartAsync(
            string executable, string[] args, string? workDir)
        {
            var path = ResolveExecutablePath(executable);

            var psi = new ProcessStartInfo(path, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir ?? Path.GetDirectoryName(path) ?? string.Empty
            };

            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start process: {path}");

            // Give the process a moment to initialize
            await Task.Delay(100);

            if (proc.HasExited)
                throw new InvalidOperationException(
                    $"Process {executable} exited immediately with code {proc.ExitCode}.");

            return new ProcessHandle(proc.Id, proc);
        }

        public async Task StopAsync(ProcessHandle handle)
        {
            if (handle.ProcessRef is not Process proc) return;
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync();
                }
            }
            finally
            {
                proc.Dispose();
            }
        }

        private string ResolveExecutablePath(string name)
        {
            // 1. Try user-installed updated binary
            var updatedPath = _updater.GetInstalledBinaryPath(
                name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(updatedPath) && File.Exists(updatedPath))
                return updatedPath;

            // 2. Try bundled binary next to the app
            var bundled = Path.Combine(AppContext.BaseDirectory, name.EndsWith(".exe") ? name : name + ".exe");
            if (File.Exists(bundled))
                return bundled;

            throw new FileNotFoundException(
                $"Binary '{name}' not found. Try updating from Settings.", name);
        }
    }
}
