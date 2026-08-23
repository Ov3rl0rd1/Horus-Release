---
name: verify
description: Build and static-test Horus after a change — correct target frameworks, the test suite, expected warning baselines, and how to tell a new warning from the existing noise. Use before reporting any code change as done, and whenever asked to "check it builds" or "run the tests".
---

# Verifying a change to Horus

No CI exists. This is the whole safety net, so run it before calling anything finished.

## The four commands

```bash
dotnet build Horus/Horus.csproj -f net10.0-android -c Debug
dotnet build Horus/Horus.csproj -f net10.0-android -c Release
dotnet build Horus/Horus.csproj -f net10.0-windows10.0.19041.0 -c Debug
dotnet test Horus.Tests/Horus.Tests.csproj
```

Android alone is not enough: `Protocols/` and `Application/` are shared, and the Windows host
is the one that breaks silently — it uses the same `HevTunnelConfig` generator through a
different hosting model.

## Baselines (23.08.2026)

| target | expected |
|---|---|
| Android Debug | 34 warnings, 0 errors |
| Android Release | 35 warnings, 0 errors |
| Windows Debug | 129 warnings, 0 errors |
| tests | **143 passed**, 0 failed |

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
  neither host may re-inline its own copy. A mismatch produces a tunnel that carries nothing.
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

- **hev-socks5-tunnel**: `packaging/android/build-hev.ps1` (needs `ANDROID_NDK_HOME`, currently
  `E:\NVPACK\android-ndk-r27d`). Clones upstream at a pinned commit, applies everything in
  `packaging/android/hev-patches/` in filename order, drops `src/hev-jni.c`, builds arm64-v8a
  and x86_64 only, and asserts every expected symbol is in the output. If a patch stops
  applying, that is the intended signal to re-read it against the new upstream — do not
  `--whitespace=fix` around it.
- **xray-core**: separate repo at `C:\X-ray-custom\Xray-core-RTC`, built by its own GitHub
  Actions workflow `.github/workflows/build-lib.yml` (arm64-v8a, x86_64, windows/x64).

A patch that applies cleanly still may not compile. Build it before claiming it works — a
`git diff`-generated hunk can swallow an adjacent line and produce valid-looking, invalid C.

## Before saying it is done

State the actual numbers. If something was skipped or could not be verified on this machine,
say so explicitly rather than implying the whole set passed.
