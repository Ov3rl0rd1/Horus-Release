<#
.SYNOPSIS
    Builds libhev_socks.so — the TUN bridge — and drops it into Platforms/Android/lib.

.DESCRIPTION
    Upstream publishes Android *executables*, not the shared library, so this is built from
    source every time; there is no stock binary to download.

    Three deviations from a plain `ndk-build`, all deliberate:

    1. src/hev-jni.c is removed before building. It is the Java-facing half of the library
       and Horus does not use it — the tunnel is driven through the plain C API
       (hev_socks5_tunnel_main_from_str / _quit / _stats) from HevSocksTunnel.cs.
       Leaving it in is not merely redundant, it is fatal: .NET Android loads the library
       with System.loadLibrary during runtime startup, so its JNI_OnLoad always runs, and
       that function looks up a Java class by name and — since upstream 64cc609 — returns
       JNI_ERR when it is missing. The pending ClassNotFoundException then aborts the
       process before the app draws a frame. Building without it removes the whole question,
       and with it any constraint on the app id or the class name.

       It cannot be switched off with a compiler flag: the guard is `#ifdef ANDROID` and
       ndk-build appends its own -DANDROID after APP_CFLAGS, so -UANDROID loses.

    2. The checkout's symlinked headers are materialised as copies. Git on Windows writes
       symlinks as one-line text files unless core.symlinks is on, and the build then fails
       with `unknown type name 'HevRBTree'`.

    3. Everything in hev-patches/ is applied, in filename order. Today that is two:

       0001 caps the log file. Upstream's logger appends forever, and a tunnel that stays up
       for weeks with verbose logging on has nothing to stop it filling the device.

       0002 adds hev_socks5_tunnel_set_fd, which moves a running tunnel onto a new TUN
       descriptor. Android's VpnService can hand over an interface without dropping it —
       establish() a second time — but only if whatever pumps packets can follow. Without
       this a rebuild has to stop and start the tunnel, and on Android that means starting a
       foreground service from the background: restricted on 12+, refusable in Doze, and
       therefore a reconnect that can fail outright while the screen is off.

       Patches are kept here rather than in a fork so that moving to a newer upstream is a
       one-line change to -Commit; a patch that stops applying is a deliberate signal to
       re-read it against the new layout, which is exactly the review a fork silently skips.

.PARAMETER Ndk
    Path to an Android NDK. Defaults to $env:ANDROID_NDK_HOME.

.PARAMETER Commit
    Upstream commit to build. Pinned so the shipped binary is reproducible.
#>
[CmdletBinding()]
param(
    [string]$Ndk = $env:ANDROID_NDK_HOME,
    [string]$Commit = 'f6ab377c9bad8093a0489cda274f1adbc1bf2b45',
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'horus-hev-build'),
    [string]$PatchDir = (Join-Path $PSScriptRoot 'hev-patches')
)

$ErrorActionPreference = 'Stop'

if (-not $Ndk -or -not (Test-Path (Join-Path $Ndk 'ndk-build.cmd'))) {
    throw "Android NDK not found. Pass -Ndk or set ANDROID_NDK_HOME."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$libRoot = Join-Path $repoRoot 'Horus\Platforms\Android\lib'
$src = Join-Path $WorkDir 'hev-socks5-tunnel'

if (Test-Path $src) { Remove-Item $src -Recurse -Force }
New-Item -ItemType Directory -Force $WorkDir | Out-Null

Write-Host "Cloning hev-socks5-tunnel @ $($Commit.Substring(0,7))"
git clone --quiet --recursive https://github.com/heiher/hev-socks5-tunnel $src
git -C $src checkout --quiet $Commit
git -C $src submodule --quiet update --init --recursive

# (2) Materialise symlinked headers.
$repos = @('.') + (git -C $src submodule --quiet foreach --recursive 'echo $displaypath')
$fixed = 0
foreach ($sub in $repos) {
    $path = Join-Path $src $sub
    foreach ($entry in (& git -C $path ls-files -s | Where-Object { $_ -match '^120000' })) {
        $rel = ($entry -split "`t")[1]
        $link = Join-Path $path $rel
        $target = Join-Path (Split-Path $link -Parent) ((Get-Content $link -Raw).Trim())
        if (Test-Path $target) { Copy-Item $target $link -Force; $fixed++ }
        else { throw "Unresolved symlink: $sub/$rel" }
    }
}
Write-Host "Materialised $fixed symlinked headers"

# (3) Apply the local patches, in filename order.
#
# --whitespace=error-all rather than the default 'warn': these are our own patches against a
# pinned commit, so a whitespace mismatch means the patch no longer matches the source it was
# written for, and silently fixing it up would hide that.
if (Test-Path $PatchDir) {
    $patches = Get-ChildItem -Path $PatchDir -Filter '*.patch' | Sort-Object Name
    foreach ($patch in $patches) {
        Write-Host "Applying $($patch.Name)"
        & git -C $src apply --whitespace=error-all $patch.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to apply $($patch.Name). Upstream layout has probably moved; re-read the patch against commit $Commit."
        }
    }
    if ($patches.Count -eq 0) { Write-Host "No patches in $PatchDir" }
}
else {
    Write-Warning "Patch directory not found: $PatchDir"
}

# (1) Drop the JNI layer.
$jni = Join-Path $src 'src\hev-jni.c'
if (-not (Test-Path $jni)) { throw "src/hev-jni.c not found - upstream layout changed, re-check this script." }
Remove-Item $jni -Force
Write-Host "Removed src/hev-jni.c"

# APP_ABI overrides Application.mk, which still lists armeabi-v7a and x86. The app ships a
# core for neither, so building them was half the build time for output nothing copies.
#
# Each argument is built as one string: PowerShell would otherwise split `KEY=(expr)` into
# two arguments and ndk-build would see the key with an empty value.
$buildScript = Join-Path $src 'Android.mk'
$applicationMk = Join-Path $src 'Application.mk'
$libsOut = Join-Path $WorkDir 'libs'
$objOut = Join-Path $WorkDir 'obj'

& (Join-Path $Ndk 'ndk-build.cmd') `
    "NDK_PROJECT_PATH=$src" `
    "APP_BUILD_SCRIPT=$buildScript" `
    "NDK_APPLICATION_MK=$applicationMk" `
    "APP_MODULES=hev-socks5-tunnel" `
    "APP_ABI=arm64-v8a x86_64" `
    "NDK_LIBS_OUT=$libsOut" `
    "NDK_OUT=$objOut" `
    -j8
if ($LASTEXITCODE -ne 0) { throw "ndk-build failed ($LASTEXITCODE)" }

# Only the ABIs the app ships a core for; see Platforms/Android/lib/README.md.
foreach ($abi in @('arm64-v8a', 'x86_64')) {
    $built = Join-Path $WorkDir "libs\$abi\libhev-socks5-tunnel.so"
    $dest = Join-Path $libRoot "$abi\libhev_socks.so"

    $strings = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($built))
    if ($strings -match 'JNI_OnLoad') { throw "$abi still contains JNI_OnLoad - the removal did not take." }
    foreach ($symbol in 'hev_socks5_tunnel_main_from_str', 'hev_socks5_tunnel_quit', 'hev_socks5_tunnel_stats') {
        if ($strings -notmatch $symbol) { throw "$abi is missing $symbol" }
    }

    # Both patches are invisible at runtime until the moment they matter, which is exactly
    # the moment nobody is watching: a log only overruns after weeks, and a missing fd swap
    # just makes reconnects quietly slower. Assert each one landed, so a patch that applied
    # but did not take is caught here rather than months later.
    if ($strings -notmatch 'log-max-size') { throw "$abi does not carry the log-max-size patch" }
    foreach ($symbol in 'hev_socks5_tunnel_set_fd') {
        if ($strings -notmatch $symbol) { throw "$abi is missing $symbol - the fd hot-swap patch did not take" }
    }

    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $built $dest -Force
    "{0,-12} {1,9:N0} bytes -> {2}" -f $abi, (Get-Item $dest).Length, $dest
}

Write-Host "Done."
