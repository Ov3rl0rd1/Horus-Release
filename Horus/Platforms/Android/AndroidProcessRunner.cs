using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Diagnostics;

namespace Horus.Platforms.Android
{
    public class AndroidProcessRunner : IProcessRunner
    {
        private const string BinaryName = "hysteria.so";

        private readonly IBinaryUpdaterService _updater;
        private string? _cachedBinaryPath;

        public AndroidProcessRunner(IBinaryUpdaterService updater)
        {
            _updater = updater;
        }

        private string ResolveBinaryPath(string executable)
        {
            // 1. Check for a user-installed updated binary (from GitHub Releases)
            var updatedPath = _updater.GetInstalledBinaryPath(
                executable.Replace(".so", "", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(updatedPath) && File.Exists(updatedPath))
                return updatedPath;

            // 2. Fall back to bundled native library in the APK
            if (_cachedBinaryPath != null && File.Exists(_cachedBinaryPath))
                return _cachedBinaryPath;

            var context = global::Android.App.Application.Context;
            var nativeLibDir = context.ApplicationInfo!.NativeLibraryDir!;
            var bundledPath = Path.Combine(nativeLibDir, executable);

            if (!File.Exists(bundledPath))
                throw new FileNotFoundException(
                    $"Binary '{executable}' not found in native library directory. " +
                    "Please ensure the binary is bundled in the APK.", bundledPath);

            _cachedBinaryPath = bundledPath;
            return bundledPath;
        }

        public async Task<ProcessHandle> StartAsync(string executable, string[] args, string? workDir = null)
        {
            var binaryPath = ResolveBinaryPath(executable);

            var psi = new ProcessStartInfo
            {
                FileName = binaryPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir ?? string.Empty
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            var process = new Process { StartInfo = psi };
            process.Start();

            // Brief pause to let process initialize
            await Task.Delay(50);

            return new ProcessHandle(process.Id, process);
        }

        public Task StopAsync(ProcessHandle handle)
        {
            try
            {
                if (handle.ProcessRef is Process { HasExited: false } proc)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                    proc.Dispose();
                }
            }
            catch { /* process may already be dead */ }
            return Task.CompletedTask;
        }
    }
}
