<#
.SYNOPSIS
    Creates the self-signed code-signing certificate the release workflow signs with.

.DESCRIPTION
    Run this once, on your own machine, and keep the .pfx somewhere safe.

    It prints three values for GitHub → Settings → Secrets and variables → Actions:

      WINDOWS_CERT_PFX_BASE64   the certificate *and its private key*
      WINDOWS_CERT_PASSWORD     the password protecting that file
      WINDOWS_CERT_THUMBPRINT   the fingerprint, for pinning

    The thumbprint alone cannot sign anything — it is a fingerprint, not a key — so the
    runner needs the .pfx as well. The thumbprint is still worth storing: the workflow
    checks the signature it produced actually carries this certificate, which catches a
    stale or wrong secret before anything ships.

    Treat the .pfx and its password like the Android keystore. Whoever holds them can sign
    code that every machine which trusted this certificate will accept.

    A self-signed certificate is not trusted anywhere by default. install-certificate.ps1
    is what makes a client machine accept it; it ships next to the app.

.PARAMETER Subject
    Common name shown as the publisher in the UAC prompt and in file properties.

.PARAMETER Years
    Validity. Signatures are timestamped by the workflow, so they stay valid after the
    certificate itself expires — but a fresh install would stop trusting new builds, so
    do not make this short.

.PARAMETER OutputDirectory
    Where to write horus-codesign.pfx and horus-codesign.cer. Keep it out of the repo.
#>
[CmdletBinding()]
param(
    [string]$Subject = 'Horus VPN',
    [int]$Years = 10,
    [string]$OutputDirectory = (Join-Path $HOME '.horus'),
    [SecureString]$Password
)

$ErrorActionPreference = 'Stop'

if (-not $Password) {
    $Password = Read-Host -AsSecureString "Password to protect the .pfx (you will need it as a GitHub secret)"
}
if ($Password.Length -eq 0) { throw "An empty password is not acceptable for a signing key." }

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$pfxPath = Join-Path $OutputDirectory 'horus-codesign.pfx'
$cerPath = Join-Path $OutputDirectory 'horus-codesign.cer'

if (Test-Path $pfxPath) {
    throw "$pfxPath already exists. Generating a second certificate means clients that " +
          "trusted the first one will reject builds signed with the new one — move the old " +
          "file aside deliberately if that is what you want."
}

Write-Host "Creating a code-signing certificate for '$Subject'..."
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=$Subject" `
    -FriendlyName "$Subject code signing" `
    -KeyAlgorithm RSA `
    -KeyLength 4096 `
    -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears($Years) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')   # EKU: Code Signing, and only that

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $Password | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

$base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath))
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))

Write-Host ""
Write-Host "Certificate created." -ForegroundColor Green
Write-Host "  private key + certificate : $pfxPath"
Write-Host "  certificate only (public) : $cerPath"
Write-Host "  valid until               : $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host ""
Write-Host "GitHub secrets" -ForegroundColor Cyan
Write-Host "  WINDOWS_CERT_THUMBPRINT   $($cert.Thumbprint)"
Write-Host "  WINDOWS_CERT_PASSWORD     $plain"
Write-Host "  WINDOWS_CERT_PFX_BASE64   (written to $OutputDirectory\pfx-base64.txt - paste its contents)"
$base64 | Set-Content (Join-Path $OutputDirectory 'pfx-base64.txt') -NoNewline

Write-Host ""
Write-Host "Back up $pfxPath and the password somewhere off this machine." -ForegroundColor Yellow
Write-Host "Losing them means every client has to trust a new certificate by hand."
