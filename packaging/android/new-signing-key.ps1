<#
.SYNOPSIS
    Creates the Android release keystore and prints the four GitHub secrets.

.DESCRIPTION
    Uses keytool, which ships with the JDK the Android build already needs — no extra tool,
    and it produces exactly the PKCS#12 keystore that .NET Android's signing properties
    expect.

    Run once. Back the .keystore file and its passwords up somewhere off this machine before
    you ship anything signed with it: direct-APK distribution has no Play App Signing to
    recover from, so losing the key means every existing user must uninstall and reinstall
    to ever get another update.

.PARAMETER Alias
    Key alias inside the keystore.

.PARAMETER Years
    Validity. Ten thousand days is the conventional choice; anything that expires during the
    app's life makes updates impossible.

.PARAMETER OutputDirectory
    Where to write horus-release.keystore. Keep it out of the repo.
#>
[CmdletBinding()]
param(
    [string]$Alias = 'horus',
    [string]$DistinguishedName = 'CN=Horus VPN, O=Horus, C=RU',
    [int]$Days = 10000,
    [string]$OutputDirectory = (Join-Path $HOME '.horus'),
    [SecureString]$StorePassword,
    [SecureString]$KeyPassword
)

$ErrorActionPreference = 'Stop'

$keytool = (Get-Command keytool -ErrorAction SilentlyContinue)?.Source
if (-not $keytool -and $env:JAVA_HOME) {
    $candidate = Join-Path $env:JAVA_HOME 'bin\keytool.exe'
    if (Test-Path $candidate) { $keytool = $candidate }
}
if (-not $keytool) {
    throw "keytool not found. Install a JDK (Temurin 17 is what CI uses) or set JAVA_HOME."
}

if (-not $StorePassword) { $StorePassword = Read-Host -AsSecureString "Keystore password" }
if (-not $KeyPassword) { $KeyPassword = Read-Host -AsSecureString "Key password (Enter to reuse the keystore password)" }
if ($KeyPassword.Length -eq 0) { $KeyPassword = $StorePassword }

function ToPlain([SecureString]$s) {
    [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))
}

$storePlain = ToPlain $StorePassword
$keyPlain = ToPlain $KeyPassword
if ($storePlain.Length -lt 6) { throw "keytool requires a password of at least 6 characters." }

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$keystore = Join-Path $OutputDirectory 'horus-release.keystore'

if (Test-Path $keystore) {
    throw "$keystore already exists. A second key cannot update apps signed with the first " +
          "one — move the old file aside deliberately if that is really what you want."
}

& $keytool -genkeypair -v `
    -storetype PKCS12 `
    -keystore $keystore `
    -alias $Alias `
    -keyalg RSA -keysize 4096 `
    -validity $Days `
    -dname $DistinguishedName `
    -storepass $storePlain `
    -keypass $keyPlain
if ($LASTEXITCODE -ne 0) { throw "keytool failed ($LASTEXITCODE)" }

$base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($keystore))
$base64 | Set-Content (Join-Path $OutputDirectory 'keystore-base64.txt') -NoNewline

Write-Host ""
Write-Host "Keystore created: $keystore" -ForegroundColor Green
Write-Host ""
Write-Host "GitHub secrets" -ForegroundColor Cyan
Write-Host "  ANDROID_KEYSTORE_BASE64    (written to $OutputDirectory\keystore-base64.txt - paste its contents)"
Write-Host "  ANDROID_KEYSTORE_PASSWORD  $storePlain"
Write-Host "  ANDROID_KEY_ALIAS          $Alias"
Write-Host "  ANDROID_KEY_PASSWORD       $keyPlain"
Write-Host ""
Write-Host "Fingerprint, for checking a downloaded APK with 'apksigner verify --print-certs':"
& $keytool -list -v -keystore $keystore -alias $Alias -storepass $storePlain |
    Select-String 'SHA256:' | ForEach-Object { "  $($_.Line.Trim())" }

Write-Host ""
Write-Host "Back up $keystore and both passwords off this machine." -ForegroundColor Yellow
