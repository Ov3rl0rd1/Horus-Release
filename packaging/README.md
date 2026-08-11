# Packaging and releases

`.github/workflows/release.yml` builds and publishes everything. It is **manual only**
(`workflow_dispatch`) and always builds from `main`.

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
| `Horus-<v>-win-x64.msi` | ~87 MB | Per-machine install into `Program Files`, Start-menu shortcut, upgrades in place. |
| `Horus-<v>-win-x64-portable.zip` | ~111 MB | Unpack and run `Horus.exe`. Same payload, no installer. |
| `Horus-<v>-android-arm64-v8a.apk` | ~36 MB | Every current phone. |
| `Horus-<v>-android-x86_64.apk` | ~38 MB | Emulators. |
| `SHA256SUMS.txt` | | Publish this next to the downloads. |

Windows is x64 only: the app is x64, a 32-bit build cannot load the x64 core, and Windows on
ARM runs x64 under emulation. A real arm64 target would need arm64 builds of xray, wintun and
the MSYS-based bridge, which upstream does not publish.

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

Not configured — there is no certificate yet, so the MSI and the exe are unsigned and
SmartScreen will warn on first run. The `Sign` step is written and disabled with `if: false`;
add `WINDOWS_CERT_PFX_BASE64` and `WINDOWS_CERT_PASSWORD` and remove that line to enable it.
Until then, the published SHA-256 sums are the only integrity check a tester has, which is
why the release notes point at them.

## Elevation

`Horus.exe` carries `requestedExecutionLevel level="requireAdministrator"`, so Windows shows
a UAC prompt at launch. Creating the wintun adapter genuinely needs administrator rights, so
the alternative is a launch that looks fine until the user presses Connect.

## Running the same steps locally

```powershell
dotnet publish Horus/Horus.csproj -f net10.0-windows10.0.19041.0 -c Release -r win-x64 `
  -p:SelfContained=true -p:UseMonoRuntime=false -o publish/win-x64

dotnet tool install --global wix --version 5.*
wix build -arch x64 -d Version=0.9.1 -d PublishDir="$(Resolve-Path publish/win-x64)" `
  packaging/windows/Horus.wxs -o dist/Horus-0.9.1-win-x64.msi
```

```powershell
dotnet publish Horus/Horus.csproj -f net10.0-android -c Release -r android-arm64 `
  -p:HorusDistribution=true -p:AndroidPackageFormat=apk
```

`HorusDistribution=true` turns the release checklist into build errors — Release config, not
debuggable, really signed, apk rather than aab.
