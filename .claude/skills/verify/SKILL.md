---
name: verify
description: Build and static-test Horus after a change — correct target frameworks, the test suite, expected warning baselines, and how to tell a new warning from the existing noise. Use before reporting any code change as done, and whenever asked to "check it builds" or "run the tests".
---

# Verifying a change to Horus

Run it before calling anything finished.

## The commands

```bash
dotnet test Horus.Tests/Horus.Tests.csproj
dotnet build tools/WinUiTypeCheck/WinUiTypeCheck.csproj      # Windows C#, on any OS, ~1 min
dotnet build Horus/Horus.csproj -f net10.0-android -c Debug
dotnet build Horus/Horus.csproj -f net10.0-windows10.0.19041.0 -c Debug   # Windows only
```

Android alone is not enough: `Application/`, `Presentation/` and `Protocols/` are shared, and
the Windows head has its own controller and screens behind `IVpnController` and
`IPlatformScreens`. A change on one side must compile on the other.

## Branch CI

`.github/workflows/branch-ci.yml` runs on every push to `claude/**` and `dev`: `tests`,
`windows-typecheck`, `android` (build + warning codes), `windows` (MAUI Debug build, then a
rehearsal of the release's Windows job: the same Release self-contained publish, the same
`packaging/windows/check-payload.ps1`, the same MSI build) and `windows-tunnel` (`tools/WinTunnelTest` on a real Windows runner — see the `windows-tunnel`
skill). A session without a Windows machine or the Android workload pushes and reads the jobs
with the GitHub MCP tools (`actions_list` → `list_workflow_jobs` → `get_job_logs`). The
`windows` job spends about eight of its ten minutes installing the workload; the type-check job
answers "does it compile" first.

`release.yml` builds **the commit it is dispatched from** and tags that commit; run it from
`main` for a release, from anything else it publishes a pre-release. It used to check out `main`
whatever the branch, so a run from `dev` checked dev's expectations against main's code and
failed with "Missing from the publish output: publish/win-x64/wintun.dll".

## Baselines (09.10.2026, from CI)

| target | expected |
|---|---|
| Android Debug | 39 warnings, 0 errors |
| Windows Debug | 129 warnings, 0 errors |
| WinUiTypeCheck | builds, warnings are the shared MVVM-toolkit ones |
| tests | **351 passed**, 6 skipped (live-API and bench export, need env vars), 0 failed |

The counts are noisy by design (obsolete MAUI `Frame`, `CA1416` platform-availability,
`CS0067` unused events on the stub services). What matters is that no **new kind** appears —
compare the warning codes, not the totals:

```bash
dotnet build Horus/Horus.csproj -f net10.0-android -c Debug -v m 2>&1 \
  | grep -oE "warning [A-Z]+[0-9]+" | sort -u
```

## Target frameworks

The Windows TFM is **`net10.0-windows10.0.19041.0`**. Building `…17763.0` fails with
`NETSDK1005: Assets file … doesn't have a target`, which reads like a restore problem and is
not one — it is the wrong TFM. `Horus.csproj:14-16` is the source of truth.

`dotnet restore` will not fix `NETSDK1005`; check the TFM string first.

## What the test suite actually guards

`Horus.Tests` is contract tests, not coverage. The ones that catch real regressions:

- `SocksPortContractTests` — the SOCKS port chosen by `SocksPortAllocator` must match what
  `HevTunnelConfig.Build` writes into the bridge YAML, across the allocator's whole range, and
  Android may not re-inline its own copy. A mismatch produces a tunnel that carries nothing.
  Windows has no bridge at all, and a test keeps it that way.
- `WindowsTunnelConfigTests` — the config the Windows core runs: TUN inbound, DNS hijack,
  lead routing rules, process matchers for split tunnelling.
- `NetworkPathTests`, `ProxyStallDetectorTests` — which network events and which traffic
  patterns the Windows controller may act on. Acting on the wrong one resets sessions.
- `AppTrafficTests`, `WindowsTelemetryTests` — the applications screen's per-app routes and
  "needs restart" rule; the home graph's axis and window; the node ping (a closed port's
  silence is not loss); the server-ping hook.
- `PlatformSeparationTests` — Windows-only hooks (`IPlatformScreens`, `LatencyProbe.Connector`)
  are filled only under `#if WINDOWS`, so Android keeps the shared UI and default behaviour.
- `HevLogCapContractTests` — the `log-max-size` key the patched bridge expects.
- `ConnectResponseTests` — the shape of `GET /servers/connect`.

When touching the port, the YAML generator or the API models, expect these to fail first and
treat that as the tests working.

Test-project files are linked with `<Compile Include>` rather than a project reference. Adding
a type the tests need means adding a link in `Horus.Tests.csproj`, otherwise the failure is a
confusing "type not found" in code you did not touch.

## Native libraries

Neither library is built by `dotnet build`; both are committed binaries under
`Horus/Platforms/Android/lib/<abi>/` and `Horus/Platforms/Windows/bin/`.

- **hev-socks5-tunnel** (Android only): `packaging/android/build-hev.ps1` (needs `ANDROID_NDK_HOME`, currently
  `E:\NVPACK\android-ndk-r27d`). Clones upstream at a pinned commit, applies everything in
  `packaging/android/hev-patches/` in filename order, drops `src/hev-jni.c`, builds arm64-v8a
  and x86_64 only, and asserts every expected symbol is in the output. If a patch stops
  applying, that is the intended signal to re-read it against the new upstream — do not
  `--whitespace=fix` around it.
- **xray-core**: separate repo at `C:\X-ray-custom\Xray-core-RTC`, built by its own GitHub
  Actions workflow `.github/workflows/build-lib.yml` (arm64-v8a, x86_64, windows/x64). The
  Windows `xray.dll` can also be cross-built on Linux with mingw (`windows-tunnel` skill); the
  fork commit it came from is recorded in `Horus/Platforms/Windows/bin/README.md`.

A patch that applies cleanly still may not compile. Build it before claiming it works — a
`git diff`-generated hunk can swallow an adjacent line and produce valid-looking, invalid C.

## Before saying it is done

State the actual numbers. If something was skipped or could not be verified on this machine,
say so explicitly rather than implying the whole set passed.
