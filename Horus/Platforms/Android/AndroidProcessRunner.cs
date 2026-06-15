using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Diagnostics;

namespace Horus.Platforms.Android
{
    public class AndroidProcessRunner : IProcessRunner
    {
        private const string BinaryName = "hysteria.so";

        private string? _binaryPath;

        private string EnsureBinaryAsync()
        {
            if (_binaryPath != null && File.Exists(_binaryPath))
                return _binaryPath;

            var context = global::Android.App.Application.Context;
            var filesDir = context.ApplicationInfo!.NativeLibraryDir!;
            var dest = Path.Combine(filesDir, BinaryName);

            if (!File.Exists(dest))
            {
                throw new FileNotFoundException(
                        $"Raw resource '{BinaryName}' not found in APK. " +
                        "Bundle the hysteria2 ARM64 binary as Native Library.");
            }

            _binaryPath = dest;
            return dest;
        }

        public async Task<ProcessHandle> StartAsync(string executable, string[] args, string? workDir = null)
        {
            var binaryPath = executable == BinaryName
                ? EnsureBinaryAsync()
                : executable;

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

            return new ProcessHandle
            {
                Pid = process.Id,
                ProcessRef = process
            };
        }

        public Task StopAsync(ProcessHandle handle)
        {
            try
            {
                if (handle.ProcessRef is { HasExited: false } proc)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
            }
            catch { /* process may already be dead */ }
            return Task.CompletedTask;
        }
    }
}
