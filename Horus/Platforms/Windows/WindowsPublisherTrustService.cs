using Horus.Domain.Interfaces;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// Reads the Authenticode certificate off the running executable and reports whether the
    /// machine trusts it.
    ///
    /// The certificate is taken from the binary itself rather than shipped as a separate
    /// file, which means the app can only ever offer to trust the exact thing it is already
    /// running — there is no second artefact that could disagree with the first.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsPublisherTrustService : IPublisherTrustService
    {
        private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
        private const string ScriptName = "install-certificate.ps1";

        private readonly X509Certificate2? _certificate;

        public WindowsPublisherTrustService()
        {
            _certificate = LoadSigningCertificate();
        }

        public string? Thumbprint => _certificate?.Thumbprint;

        public bool NeedsTrust => _certificate is not null && !IsTrusted(_certificate);

        public async Task<bool> InstallAsync()
        {
            if (_certificate is null) return false;

            var script = Path.Combine(AppContext.BaseDirectory, ScriptName);
            if (!File.Exists(script))
                throw new FileNotFoundException(
                    $"Не найден {ScriptName} рядом с приложением — переустановите Horus.", script);

            // Pinned to what we are actually running, so the script cannot be pointed at
            // some other certificate. The app already runs elevated, so no second prompt.
            var psi = new ProcessStartInfo("powershell",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" " +
                $"-ExpectedThumbprint {_certificate.Thumbprint}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить установку сертификата.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{(await stderr).Trim()} {(await stdout).Trim()}".Trim());

            // The store write succeeding is not the answer; the chain validating is.
            return IsTrusted(_certificate);
        }

        private static X509Certificate2? LoadSigningCertificate()
        {
            try
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path)) return null;

                // Throws when the file carries no signature at all — an unsigned build,
                // which is a normal state for a local dev build and not worth reporting.
                return new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            }
            catch (CryptographicException) { return null; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Horus] publisher certificate: {ex.Message}");
                return null;
            }
        }

        private static bool IsTrusted(X509Certificate2 certificate)
        {
            try
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                // Require the code-signing EKU: a chain that validates for some other
                // purpose says nothing about whether this build is trusted to run.
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigningOid));

                return chain.Build(certificate);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Horus] chain check: {ex.Message}");
                return false;
            }
        }
    }
}
