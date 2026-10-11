# Packaging and releases

`.github/workflows/release.yml` builds and publishes everything. It is **manual only**
(`workflow_dispatch`) and builds and tags exactly the commit it is dispatched from — the branch
picked in "Use workflow from". Run it from `main` for a release. From any other branch it builds
that branch and marks the release as a pre-release; it still takes the `v<version>` tag and the
APKs carry the `version_code`, so a later release from `main` needs a new version and a higher
code. The in-app updater installs pre-releases too, so the `draft` input — not the branch — is
what keeps a build away from users until you publish it.

It used to check out `main` whatever branch it ran from. GitHub takes the workflow file itself
from the chosen branch, so a run from `dev` checked dev's expectations against main's code and
failed with "Missing from the publish output: publish/win-x64/wintun.dll".

Inputs:

| Input | Example | Notes |
|---|---|---|
| `version` | `0.9.1` | Display version. Also the MSI `ProductVersion` and the release tag (`v0.9.1`). |
| `version_code` | `2` | Android `versionCode`. **Must increase with every build** — Android refuses to install an APK whose code is not higher than the installed one. |
| `platforms` | `both` | `both`, `windows` or `android`. |
| `draft` | `true` | Leave on until you have checked the artifacts. |

## What comes out

| Artifact | Size | Notes |
|---|---|---|
| `Horus-<v>-win-x64.msi` | ~108 MB | Per-machine install into `Program Files`, Start-menu shortcut, upgrades in place. |
| `Horus-<v>-win-x64-portable.zip` | ~111 MB | Unpack and run `Horus.exe`. Same payload, no installer. |
| `Horus-<v>-android-arm64-v8a.apk` | ~36 MB | Every current phone. |
| `Horus-<v>-android-x86_64.apk` | ~38 MB | Emulators. |
| `SHA256SUMS.txt` | | Publish this next to the downloads. |

Windows is x64 only: the app is x64, a 32-bit build cannot load the x64 core, and Windows on
ARM runs x64 under emulation. A real arm64 target would need an arm64 build of the xray fork and
the arm64 `wintun.dll` (upstream wintun publishes one; the fork's build workflow does not build
arm64 Windows yet).

The Windows payload is checked by `packaging/windows/check-payload.ps1` before it is zipped or
packaged: `Horus.exe`, `xray.dll`, `wintun.dll` and `install-certificate.ps1` must sit together at
the root. Branch CI runs the same publish, the same check and the same MSI build on every push to
`dev` and `claude/**`, so a layout change fails there rather than at release time.

Android ships one APK per ABI rather than a universal one because `libxray.so` is ~53 MB per
architecture. The set comes from what `Platforms/Android/lib/` actually has a core for —
`armeabi-v7a` has the bridge but no core, so it is not built.

## Secrets to create

Android signing is required; the workflow fails early and says so if the keystore secret is
missing, because the alternative is shipping an APK signed with a throwaway key that can
never update an installed copy.

| Secret | How to produce it |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | `base64 -w0 horus-release.keystore` (PowerShell: `[Convert]::ToBase64String([IO.File]::ReadAllBytes('horus-release.keystore'))`) |
| `ANDROID_KEYSTORE_PASSWORD` | store password |
| `ANDROID_KEY_ALIAS` | e.g. `horus` |
| `ANDROID_KEY_PASSWORD` | key password |

If you do not have a keystore yet:

```
keytool -genkeypair -v -storetype PKCS12 \
  -keystore horus-release.keystore -alias horus \
  -keyalg RSA -keysize 4096 -validity 10000
```

**Back that file up in two places before using it.** With direct-APK distribution there is no
Play App Signing to fall back on: lose the key and every existing user has to uninstall and
reinstall to ever get another update.

## Windows code signing

Signing runs when the `WINDOWS_CERT_PFX_BASE64` secret is set; without it the workflow produces
working but unsigned artifacts rather than failing. `packaging/windows/new-signing-cert.ps1`
creates the self-signed certificate and prints the three secrets: `WINDOWS_CERT_PFX_BASE64`,
`WINDOWS_CERT_PASSWORD` and `WINDOWS_CERT_THUMBPRINT` (optional — when set, the workflow checks
the signature it produced carries that certificate). A self-signed certificate is trusted nowhere
by default: `install-certificate.ps1`, shipped beside `Horus.exe` and run by the app's
«Установить» button, makes a machine trust it. Unsigned or not, the published SHA-256 sums are
what a tester can check a download against.

## Elevation

`Horus.exe` carries `requestedExecutionLevel level="requireAdministrator"`, so Windows shows
a UAC prompt at launch. Creating the wintun adapter genuinely needs administrator rights, so
the alternative is a launch that looks fine until the user presses Connect.

## Running the same steps locally

```powershell
dotnet publish Horus/Horus.csproj -f net10.0-windows10.0.19041.0 -c Release -r win-x64 `
  -p:SelfContained=true -p:UseMonoRuntime=false -o publish/win-x64

dotnet tool install --global wix --version 5.*
./packaging/windows/check-payload.ps1 publish/win-x64
wix build -arch x64 -pdbtype none -d Version=0.9.1 -d PublishDir="$(Resolve-Path publish/win-x64)" `
  packaging/windows/Horus.wxs -o dist/Horus-0.9.1-win-x64.msi
```

```powershell
dotnet publish Horus/Horus.csproj -f net10.0-android -c Release -r android-arm64 `
  -p:HorusDistribution=true -p:AndroidPackageFormat=apk
```

`HorusDistribution=true` turns the release checklist into build errors — Release config, not
debuggable, really signed, apk rather than aab.
