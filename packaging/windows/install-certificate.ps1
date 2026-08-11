<#
.SYNOPSIS
    Makes this machine trust the certificate Horus is signed with.

.DESCRIPTION
    Horus is signed with a self-signed certificate. Nothing trusts such a certificate by
    default, so Windows reports the app as coming from an unknown publisher even though the
    signature itself is intact. This adds the publisher certificate to the machine's trust
    stores, after which the signature validates and the publisher name appears in the UAC
    prompt and in the file's Digital Signatures tab.

    The certificate is read out of the signed file itself rather than shipped separately, so
    it cannot install trust for anything other than the exact binary you already have.

    Read this before running it: trusting a code-signing certificate means this machine will
    accept *anything* signed with the matching private key as coming from a known publisher.
    That is the point, and it is also the risk — it is only sensible if you got Horus from a
    source you trust and the thumbprint below matches the one the project publishes.

.PARAMETER File
    The signed file to take the certificate from. Defaults to Horus.exe beside this script.

.PARAMETER ExpectedThumbprint
    If given, refuses to install anything else. Use the thumbprint published with the release.

.PARAMETER Uninstall
    Removes the certificate instead of installing it.
#>
[CmdletBinding()]
param(
    [string]$File,
    [string]$ExpectedThumbprint,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

# LocalMachine, not CurrentUser: the app runs elevated, and a per-user trust decision would
# not apply to the elevated process that actually needs it.
$stores = @('Root', 'TrustedPublisher')

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator rights are required to change the machine's trust stores. " +
              "Right-click this script and choose 'Run as administrator'."
    }
}

# Deliberately .NET types rather than Get-AuthenticodeSignature / Get-ChildItem Cert:\.
# Those live in Microsoft.PowerShell.Security, and module autoloading fails when the script
# is launched from a process with a trimmed environment — which is exactly how the app runs
# it. The same call also has to work for a user double-clicking the script, so depending on
# nothing beyond the framework is the only version that works in both.
function Get-SigningCertificate {
    param([string]$Path)

    if (-not (Test-Path $Path)) { throw "Not found: $Path" }

    try {
        $raw = [Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($Path)
        return [Security.Cryptography.X509Certificates.X509Certificate2]::new($raw)
    }
    catch [Security.Cryptography.CryptographicException] {
        throw "$Path is not signed, so there is no certificate to trust. " +
              "This build was produced without signing."
    }
}

# Mirrors the check the app itself makes, so the button and the script never disagree about
# whether the job is done.
function Test-Trusted {
    param([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = 'NoCheck'
        $chain.ChainPolicy.ApplicationPolicy.Add(
            [Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null   # Code Signing
        return $chain.Build($Certificate)
    }
    finally { $chain.Dispose() }
}

Assert-Elevated

if (-not $File) { $File = Join-Path $PSScriptRoot 'Horus.exe' }
$cert = Get-SigningCertificate -Path $File

if ($ExpectedThumbprint -and $cert.Thumbprint -ne ($ExpectedThumbprint -replace '\s', '')) {
    throw "Thumbprint mismatch. Expected $ExpectedThumbprint but $File is signed with $($cert.Thumbprint). Not installing."
}

Write-Host "Publisher  : $($cert.Subject)"
Write-Host "Thumbprint : $($cert.Thumbprint)"
Write-Host "Valid until: $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host ""

foreach ($storeName in $stores) {
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        $storeName, [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $store.Open('ReadWrite')
    try {
        $existing = $store.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint

        if ($Uninstall) {
            if ($existing) { $store.Remove($cert); Write-Host "removed from LocalMachine\$storeName" }
            else { Write-Host "not present in LocalMachine\$storeName" }
        }
        elseif ($existing) {
            Write-Host "already trusted in LocalMachine\$storeName"
        }
        else {
            $store.Add($cert)
            Write-Host "added to LocalMachine\$storeName"
        }
    }
    finally { $store.Close() }
}

Write-Host ""

# Report the outcome that actually matters, rather than assuming the store writes were
# enough — an expired certificate installs fine and still fails to validate.
$trusted = Test-Trusted -Certificate $cert

if ($Uninstall) {
    Write-Host "Certificate removed. Trusted now: $trusted"
    exit 0
}

if ($trusted) {
    Write-Host "Done - $([IO.Path]::GetFileName($File)) now has a trusted publisher." -ForegroundColor Green
    exit 0
}

Write-Warning "Certificate installed, but the chain still does not validate."
Write-Warning "That usually means it has expired: valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))."
exit 1
