# check-payload.ps1 <publish dir>
#
# The files a Windows build cannot work without, checked in a `dotnet publish` output before
# it is zipped or wrapped in an MSI. One list, used by both the release workflow and branch CI,
# so the check a branch passes is the check its release will run.
#
#   Horus.exe                 the app
#   xray.dll                  the VPN core, which owns the TUN in-process
#   wintun.dll                the TUN driver library; must sit beside Horus.exe, because the
#                             core loads it from the application directory
#   install-certificate.ps1   run by the "Установить" button, reads the certificate out of
#                             Horus.exe, so it must sit beside it too
param(
    [Parameter(Mandatory = $true)][string]$PublishDir
)

$ErrorActionPreference = 'Stop'

$required = @('Horus.exe', 'xray.dll', 'wintun.dll', 'install-certificate.ps1')
$missing = $required | Where-Object { -not (Test-Path (Join-Path $PublishDir $_)) }

if ($missing) {
    # Where did they go? A file that exists somewhere else in the output means the project
    # put it in the wrong place; a file that exists nowhere means it was never copied.
    $found = foreach ($name in $missing) {
        Get-ChildItem -Path $PublishDir -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
            ForEach-Object { "  $name found at $($_.FullName.Substring((Resolve-Path $PublishDir).Path.Length + 1))" }
    }
    $message = "Missing from the publish output ($PublishDir):`n  " + ($missing -join "`n  ")
    if ($found) { $message += "`nElsewhere in the output:`n" + ($found -join "`n") }
    throw $message
}

Write-Host "payload OK - $((Get-ChildItem $PublishDir -Recurse -File).Count) files"
