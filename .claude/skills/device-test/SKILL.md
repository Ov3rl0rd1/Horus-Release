---
name: device-test
description: Build, install and drive the Horus Android app on the connected phone over adb — test package id, VPN-slot conflict with the production build, Doze/screen-off testing, log locations, UI tap coordinates, and the restore checklist. Use whenever asked to test on the device, reproduce a tunnel bug on hardware, or check that a change actually works on Android.
---

# Testing Horus on the connected device

## The device

Infinix X6833B, **Android 14 (SDK 34)**, screen **1080×2460**. One device, `adb devices`
shows it as `102742534L002792`. Everything below assumes it is plugged in and authorised.

Both packages are in `dumpsys deviceidle whitelist` and standby bucket 5 (EXEMPTED), so
battery optimisation is **not** a variable in test results — do not blame it without checking.

## Install a test build

The production app `com.horus.vpn` is installed and signed with the real key, so a debug build
of the same id cannot replace it. Override the id instead — nothing in the app depends on it
(`PackageId` only names the Java class; the tunnel excludes itself via the runtime
`PackageName`, and hev's JNI layer is removed at build time):

```bash
dotnet build Horus/Horus.csproj -f net10.0-android -c Debug -t:Install \
  -p:ApplicationId=com.horus.vpn.test -p:ApplicationTitle="Horus Test" \
  -p:EmbedAssembliesIntoApk=true
```

`EmbedAssembliesIntoApk=true` is **required**. Without it Fast Deployment puts assemblies in
`files/.__override__`, and the app dies at startup with *"No assemblies found"*.

## Only one app may hold the VPN slot

Android grants the VPN to one app. The production build takes it back and revokes the test app
about 11 s in, which looks exactly like a tunnel bug. Disable it for the session and
**re-enable it at the end** — data is preserved either way:

```bash
adb shell 'pm disable-user --user 0 com.horus.vpn'   # start of testing
adb shell 'pm enable com.horus.vpn'                  # ALWAYS, at the end
```

Verify the restore: `dumpsys package com.horus.vpn | grep enabled=` → `enabled=1`.

## Shell quirks (Git Bash on Windows)

- **Every `adb shell` command with an absolute device path needs `export MSYS_NO_PATHCONV=1`**,
  or Git Bash rewrites `/data/data/...` into `C:/Program Files/...`.
- Redirects belong **inside** `run-as`, not outside:
  `adb shell 'run-as com.horus.vpn.test sh -c "wc -l < /data/data/.../hev.log"'`.
  Without `sh -c` the redirect runs on the host and fails with "Permission denied".

## Logs

`adb logcat` — the app tags everything `HorusDiag`:

```bash
adb logcat -c                                        # clear before an experiment
adb logcat -d -v time | grep -E "HorusDiag" | tail -30
```

On-device files, under `run-as com.horus.vpn.test /data/data/com.horus.vpn.test/cache/logs/`:

| file | what |
|---|---|
| `events.jsonl` | the app's structured log, survives process death, spans sessions |
| `hev.log` | the bridge — **one line per SOCKS5 session, the best proof traffic flows** |
| `xray.log` | the core |
| `crash.log` | never truncated on connect |
| `*.prev` | the previous session's copy |

`hev.log` is at level `warn` by default and stays empty. Turn on **Настройки → Подробные логи**
and reconnect to get `info`, which is what prints per-session lines and
`socks5 tunnel adopted fd N`.

## Driving the UI

`monkey` to launch, `input tap` to press. Coordinates for 1080×2460 (a screenshot is
`878×2000`, so multiply screenshot coords by **1.23**):

| target | tap |
|---|---|
| Connect / Disconnect (the big button) | `539 732` |
| Tabs: Home / Servers / Settings | `182 2405` / `540 2405` / `899 2405` |
| Settings → Лимитное соединение (page at top) | `908 1074` |
| Settings → Подробные логи (after one swipe up) | `908 1357` |

```bash
adb shell 'monkey -p com.horus.vpn.test -c android.intent.category.LAUNCHER 1'
adb shell 'input tap 539 732'
adb shell 'input swipe 540 1800 540 700 300'    # scroll settings down
```

**Wait ~9 s after launching before tapping** — an earlier tap lands on the splash and is lost.
The button is a toggle: if the app auto-restored the tunnel, tapping *disconnects*. Read the
log before assuming a tap failed.

Screenshot, then read the PNG with the Read tool — it is the fastest way to see real state:

```bash
adb exec-out screencap -p > "$SCRATCH/screen.png"
```

## Checking the tunnel from the system's side

```bash
# The VPN network agent: metered flag, validation, underlying networks
adb shell 'dumpsys connectivity' | grep -E "ni\{VPN CONNECTED" \
  | grep -oE "InterfaceName: [a-z0-9]+|Capabilities: [A-Z_&]+|underlying\{[^}]*\}"

# Foreground service type actually granted — 00000400 = SYSTEM_EXEMPTED, 40000000 = SPECIAL_USE
adb shell 'dumpsys activity services com.horus.vpn.test' | grep -E "types=|isForeground"

adb shell 'ip -o addr show tun0'
```

`NOT_METERED` in the capability list is the check that matters for background traffic; it is
absent whenever the active network is cellular, which is correct, so test it on Wi-Fi.

**ICMP does not cross the tunnel** — SOCKS5 carries TCP and UDP only. A failing
`ping 1.1.1.1` proves nothing. Use `VALIDATED` on tun0 (Android's own HTTP probe) or new lines
in `hev.log` instead. The device has no `curl`, `wget` or `nc`.

## Screen-off / Doze test

The one that matters for background music and notifications:

```bash
adb shell 'input keyevent 26'                # screen off
adb shell 'dumpsys battery unplug'           # Doze needs "on battery"
adb shell 'dumpsys deviceidle force-idle'    # -> "Now forced in to deep idle mode"
adb shell 'dumpsys deviceidle get deep'      # -> IDLE
# ... wait 15+ minutes ...
adb shell 'dumpsys deviceidle unforce'; adb shell 'dumpsys battery reset'
```

Pass criteria: process alive, `tun0` present, and **`hev.log` grew** — new sessions during
idle are proof that background apps got through. Ports `:5222` and `:443` to Meta/Google are
push channels; seeing them is the direct analogue of "notifications arrive".

Do not use `sleep` chains to wait; use an until-loop on a deadline.

## Faults worth injecting

| what | command | expected |
|---|---|---|
| handover | `svc wifi disable` / `enable` | `handover=True`, `reset pooled session(s)`, **no rebuild** |
| total outage | `svc wifi disable; svc data disable` | rides it out, no recovery needed |
| managed exception | `am crash com.horus.vpn.test` | `[crash] AppDomain: …`, process survives |
| process death | `run-as com.horus.vpn.test kill -9 $(adb shell pidof com.horus.vpn.test)` | tunnel gone; **the sticky service does not restart on this ROM** — reopening the app must restore it |

`cmd connectivity airplane-mode enable` silently does nothing here; use `svc` instead.

## Restore checklist

1. `adb shell 'pm enable com.horus.vpn'` — verify `enabled=1`
2. `adb shell 'dumpsys deviceidle unforce'` and `adb shell 'dumpsys battery reset'`
3. Put any preference you flipped back (Лимитное соединение defaults to **off**)
4. Never uninstall `com.horus.vpn.test` — its session and caches make the next round faster
