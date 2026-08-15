using System.Runtime.InteropServices;
using System.Text;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Windows.Update
{
    /// <summary>
    /// Replaces the running build, in whichever way it was installed.
    ///
    /// <para><b>A running app cannot replace its own files</b>, so neither path happens
    /// in-process. A small PowerShell helper is written to the temp directory, launched
    /// detached, and waits for this process to exit before doing anything. The app then
    /// closes itself. The helper installs and relaunches. PowerShell rather than a batch
    /// file because <c>Wait-Process</c> and <c>Expand-Archive</c> are both built in and both
    /// have to be got exactly right — a batch file polling <c>tasklist</c> is the kind of
    /// thing that works until a locale changes.</para>
    ///
    /// <para><b>MSI or portable is detected, not assumed.</b> Running <c>msiexec</c> against
    /// a portable copy would install a second one in Program Files and leave the user with
    /// two; unpacking a zip over an MSI install would work but desynchronise the installed
    /// product's version. The MSI's own registration is the authority: its upgrade code is
    /// looked up, and the answer is only trusted when the registered install location is
    /// where this executable is actually running from.</para>
    /// </summary>
    public sealed class WindowsUpdateInstaller : IUpdateInstaller
    {
        /// <summary>From <c>packaging/windows/Horus.wxs</c>. Changing it there breaks detection here.</summary>
        private const string UpgradeCode = "{6D3F2A91-4F1E-4C8A-9C2B-7A5E0D8F31B4}";

        private const int ErrorSuccess = 0;

        private readonly Lazy<bool> _isMsi;

        public WindowsUpdateInstaller() => _isMsi = new Lazy<bool>(DetectMsiInstall);

        public bool IsSupported => OperatingSystem.IsWindows() && InstallDirectory is not null;

        /// <summary>The app exits so the helper can replace its files.</summary>
        public bool TerminatesProcess => true;

        public string? AssetSuffix => _isMsi.Value ? "-win-x64.msi" : "-win-x64-portable.zip";

        private static string? InstallDirectory
        {
            get
            {
                var exe = Environment.ProcessPath;
                return string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
            }
        }

        public Task InstallAsync(string payloadPath, AppVersion version, CancellationToken ct)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot locate the running executable.");
            var dir = Path.GetDirectoryName(exe)
                ?? throw new InvalidOperationException("Cannot locate the install directory.");

            var script = Path.Combine(Path.GetTempPath(), $"horus-update-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(script, HelperScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var arguments =
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
                $"-ProcessId {Environment.ProcessId} " +
                $"-Payload \"{payloadPath}\" " +
                $"-Exe \"{exe}\" " +
                $"-Mode {(_isMsi.Value ? "msi" : "portable")} " +
                $"-Dir \"{dir}\"";

            using (var process = new System.Diagnostics.Process())
            {
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                };
                process.Start();
            }

            // Quit so the helper can proceed. Anything holding a file in the install
            // directory open would make the copy fail, and this process is the only such
            // thing — the tunnel was already stopped by the caller.
            MainThread.BeginInvokeOnMainThread(() =>
                Microsoft.Maui.Controls.Application.Current?.Quit());

            return Task.CompletedTask;
        }

        // ── MSI detection ───────────────────────────────────────────────────

        private static bool DetectMsiInstall()
        {
            try
            {
                var product = new StringBuilder(39);
                if (MsiEnumRelatedProducts(UpgradeCode, 0, 0, product) != ErrorSuccess) return false;

                var size = (uint)260;
                var location = new StringBuilder((int)size);
                if (MsiGetProductInfo(product.ToString(), "InstallLocation", location, ref size) != ErrorSuccess)
                    return false;

                var installed = location.ToString();
                var running = InstallDirectory;
                if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(running)) return false;

                // A registered product plus a portable copy running elsewhere is a real
                // combination; only the copy that lives where the MSI put it may be
                // updated through msiexec.
                return string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(running)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Unable to tell — the safer default is to treat it as portable, which
                // replaces files in place instead of creating a second installation.
                return false;
            }
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiEnumRelatedProducts(
            string upgradeCode, int reserved, int index, StringBuilder productCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiGetProductInfo(
            string product, string property, StringBuilder value, ref uint valueSize);

        // ── The helper ──────────────────────────────────────────────────────

        /// <summary>
        /// ASCII only, on purpose: powershell.exe reads a BOM-less .ps1 as ANSI, and a
        /// stray non-ASCII character in a comment is enough to break tokenisation. The file
        /// is written with a BOM as well, so both halves of that trap are closed.
        /// </summary>
        private const string HelperScript = """
            param(
                [Parameter(Mandatory)][int]$ProcessId,
                [Parameter(Mandatory)][string]$Payload,
                [Parameter(Mandatory)][string]$Exe,
                [Parameter(Mandatory)][ValidateSet('msi','portable')][string]$Mode,
                [Parameter(Mandatory)][string]$Dir
            )
            $ErrorActionPreference = 'Stop'

            # The app quits right after launching this. Wait for it, because neither msiexec
            # nor a file copy can replace an executable that is still mapped.
            try { Wait-Process -Id $ProcessId -Timeout 120 } catch { }
            Start-Sleep -Seconds 2

            try {
                if ($Mode -eq 'msi') {
                    $p = Start-Process msiexec -ArgumentList '/i', "`"$Payload`"", '/qn', '/norestart' -Wait -PassThru
                    # 3010 is "success, reboot required" and is not a failure.
                    if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) { throw "msiexec exited $($p.ExitCode)" }
                } else {
                    $stage = Join-Path $env:TEMP ("horus-stage-" + [guid]::NewGuid().ToString('N'))
                    Expand-Archive -LiteralPath $Payload -DestinationPath $stage -Force
                    Copy-Item -Path (Join-Path $stage '*') -Destination $Dir -Recurse -Force
                    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
                }
            } catch {
                # Leave the old build in place and relaunch it. A half-updated install that
                # will not start is far worse than a missed update.
                Write-Error $_
            }

            try { Start-Process -FilePath $Exe } catch { }
            Remove-Item $Payload -Force -ErrorAction SilentlyContinue
            Remove-Item $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """;
    }
}
