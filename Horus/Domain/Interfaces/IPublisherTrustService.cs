namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Whether the operating system trusts the certificate this build is signed with, and a
    /// way to make it do so.
    ///
    /// Horus is signed with a self-signed certificate, which nothing trusts by default: the
    /// signature is intact but the chain terminates in a root the machine has never heard
    /// of, so Windows presents the app as coming from an unknown publisher. Installing the
    /// certificate is a one-time action, and it is the user's to take — it means the machine
    /// will accept anything signed with that key — so the app offers it rather than doing it
    /// silently.
    /// </summary>
    public interface IPublisherTrustService
    {
        /// <summary>
        /// True only when there is something worth offering: the build is signed and the
        /// signature does not currently validate. An unsigned build has no certificate to
        /// install, and a trusted one needs nothing — both are silent.
        /// </summary>
        bool NeedsTrust { get; }

        /// <summary>Fingerprint of the signing certificate, or null when unsigned.</summary>
        string? Thumbprint { get; }

        /// <summary>
        /// Installs the certificate and re-checks. Returns true only when the signature
        /// actually validates afterwards — installing into a store is not the same as the
        /// result being trusted, and an expired certificate does the first without the second.
        /// </summary>
        Task<bool> InstallAsync();
    }
}
