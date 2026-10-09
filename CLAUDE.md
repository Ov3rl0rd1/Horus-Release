# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Horus is a cross-platform VPN client built with .NET MAUI targeting Android, iOS, macOS, and Windows. It tunnels through a **custom xray-core build** that carries three outbounds: VLESS/REALITY, Hysteria2 and olcRTC. The project is in early development — some service logic is still stubbed.

## Where the rest of the knowledge lives

Four skills carry the operational detail so it does not have to be re-derived:
`workflow` (report conventions, the library repos, git and distribution rules), `verify`
(build targets, warning baselines, branch CI, what each test contract guards), `device-test`
(installing alongside the production app, adb recipes, Doze testing, the restore checklist) and
`windows-tunnel` (how the Windows tunnel works and how to test it without a Windows machine:
the core bench, the hosted-runner end-to-end test, the UI type-check).

Longer-form analysis is in **`docs/`** — gitignored (`/docs`) but readable, and worth reading
before re-investigating anything: `VPN-STABILITY-RESEARCH.md` (teardown of NekoBox and
RethinkDNS), `HORUS-VPN-IMPROVEMENT-PLAN.md`, `HORUS-BACKGROUND-FIX.md`, and the
`HORUS-DEVICE-TEST-*.md` records of what was actually observed on hardware. Reports are written
in Russian; code and comments in English.

## Build & Run Commands

```bash
# Build for a specific platform (run from solution root)
dotnet build -f net10.0-android
dotnet build -f net10.0-windows10.0.19041.0
dotnet build -f net10.0-ios
dotnet build -f net10.0-maccatalyst

# Run on Android (device/emulator must be connected)
dotnet run -f net10.0-android

# Run on Windows
dotnet run -f net10.0-windows10.0.19041.0

# Run tests
dotnet test Horus.Tests/Horus.Tests.csproj

# Build an APK to hand to a tester (fails loudly unless signed, Release and non-debuggable)
dotnet publish Horus/Horus.csproj -f net10.0-android -c Release \
  -p:HorusDistribution=true -p:ApplicationVersion=<N> -p:ApplicationDisplayVersion=0.9.<N>
```

Releases are `release.yml`, run by hand; it builds and tags exactly the commit it is
dispatched from — `main` for a release, any other branch yields a pre-release. Working branches
(`claude/**`, `dev`) get `branch-ci.yml`: tests, a Windows type-check on Linux, the Android build,
the Windows build plus a rehearsal of the release's Windows job (publish, payload check, MSI), and
an end-to-end tunnel test on a hosted Windows runner. The solution file is
`Horus.slnx`. Expected baselines and what each test contract guards: see the `verify` skill.

Distribution is **direct APK**, never `.aab`. The app id `com.horus.vpn` is *not* structurally
fixed: `packaging/android/build-hev.ps1` deletes `src/hev-jni.c` before building, which removes
the JNI class lookup that used to pin it (and which returned `JNI_ERR` and aborted startup when
the class was missing). Device testing therefore installs alongside the production build under
`com.horus.vpn.test` — see the `device-test` skill.

The native libraries are not built by `dotnet build`; both are committed binaries.
`libhev_socks.so` (Android only) comes from `packaging/android/build-hev.ps1`, which clones a
pinned upstream commit, applies every patch in `packaging/android/hev-patches/` in filename
order and asserts the resulting symbols. xray-core lives in a separate repo
(`C:\X-ray-custom\Xray-core-RTC`) built by its own GitHub Actions workflow; the commit the
shipped `xray.dll` came from is recorded in `Horus/Platforms/Windows/bin/README.md`.

## Architecture

Clean Architecture with MVVM, enforced by folder boundaries:

- **`Domain/`** — Pure contracts and models; no implementation. `Interfaces/` holds service contracts; `Models/` holds enums and data classes; `Events/EventsArgs.cs` defines all event argument types.
- **`Application/`** — Service implementations (singletons). `VpnManager.cs` is the central orchestrator that coordinates protocol, platform, auth, and subscription services.
- **`Presentation/`** — MVVM UI. `View/` holds XAML pages; `ViewModels/` uses CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).
- **`Protocols/`** — The VPN core. xray-core is linked as a **C shared library** (`libxray.so` / `xray.dll`) and runs **in-process**: `XrayInterop` is the P/Invoke surface (`XrayStart`/`XrayStop`/`XrayTest`/`XrayVersion`), and `XrayProtocol` is the single `IVpnProtocol` on top of it. `ShareLinkParser` turns the `vless://` / `hysteria2://` links the API returns into `ShareLink`s, and `XrayConfigBuilder` renders those into an xray config. `ProtocolType` names an *outbound*, not a separate binary.
- **`Platforms/`** — Platform-specific code, and on Windows most of the client. **Android** runs hev-socks5-tunnel in-process (`HevSocksTunnel` P/Invokes it and hands over the `VpnService` TUN fd; YAML from `Protocols/HevTunnelConfig.cs`) in front of the core's SOCKS5 inbound. **Windows** has no bridge: the core's own `tun` inbound creates the wintun adapter in-process (`Platforms/Windows/Tunnel/WindowsTunnelConfig.cs`), and Windows has its own controller (`WindowsVpnController`), its own screens (`Platforms/Windows/Views/`) and its own settings (`WindowsPreferences`). Binaries live under `Platforms/Android/lib/<abi>/` and `Platforms/Windows/bin/` — see the READMEs there.

### Dependency Injection

All services are registered in `MauiProgram.cs` as singletons. Platform services (`IVpnPlatformService`, `ISplitTunnelingService`, `INetworkMonitor`, …) are registered conditionally per-platform using `#if ANDROID` / `#if WINDOWS` guards. Who runs the VPN is `IVpnController`: `WindowsVpnController` on Windows, `VpnManager` everywhere else (Android code also resolves `VpnManager` directly). `IPlatformScreens` is registered only on Windows; without it `RootPage` shows the shared screens, which is how Android is unaffected by the Windows UI (`PlatformSeparationTests`).

### Key flow

`MainViewModel` → `IVpnController.ConnectAsync()` (`VpnManager` on Android) → `ConnectionCache` or, on a miss, `IApiService.GetServerConnectionAsync()` (`GET /servers/connect` — the **API** picks and binds the server, and returns one share link per protocol) → `XrayProtocol.ConnectAsync()` (`XrayTest` then `XrayStart`) → **preflight** (egress IP fetched directly and through the SOCKS5 proxy) → `IVpnPlatformService` (create TUN) → `ITrafficMonitorService` (1 Hz counter poll).

Connect falls back **Hysteria2 → VLESS → olcRTC**, skipping protocols the node didn't publish. A fallback re-renders the config with a different `proxy` outbound; `XrayStop` must run before each retry because `XrayStart` fails while an instance exists.

Endpoints are cached on the device (`ConnectionCache`, 24 h cap) and tried before the API, so a reconnect costs no round trip. Nothing can tell whether stored keys are still valid except by trying them, so invalidation is **by failure**: the cache is dropped only when every endpoint in it failed to connect — the symptom of the account being re-bound from another device or the node re-provisioning. A user switching servers is caught earlier, because `select` changes the id and a record with the wrong id is discarded on read.

### Two invariants that silently kill the tunnel

1. **Anything the config routes `direct` must have a real way out, or it re-enters the tunnel.** xray runs in-process and has no socket-protect hook, so each platform substitutes its own escape hatch. Android excludes the app's UID (`HorusVpnTunnelService.ApplySplitTunneling`), which covers everything at once. On Windows the core binds every socket it opens to the physical interface (`autoOutboundsInterface` → `IP_UNICAST_IF`, fork `proxy/tun/fork_bind.go`), so its connection to the node and every `direct` rule leave by the NIC whatever the route table says; with no physical interface it refuses the socket rather than letting it loop. No host routes are installed.

   Multicast and broadcast go to `blackhole` (`XrayConfigBuilder.DropRanges`) — forwarding them costs a session per packet, and Windows chatters on a fresh interface (~1000 sessions in 3 seconds when they were `direct`). On Windows so does everything addressed to the TUN's own `/30` except DNS to the resolver.

   Consequence on Android: the app's API traffic bypasses the VPN, so **`/whoami` reports the real IP while connected and cannot verify the tunnel** — verify from another app or through the SOCKS5 proxy. On Windows the app's own sockets are not pinned, so `/whoami` goes through the tunnel and is meaningful there.
2. **Android: the core's SOCKS5 inbound and hev's `socks5.port` must agree** — a mismatch establishes a tunnel that carries nothing. The port is not fixed at 1080: `SocksPortAllocator` picks the first free port from there, and the single chosen value flows through `XrayConfig.SocksPort` → `TunnelOptions.SocksPort` → `HevTunnelConfig.Build`; `Horus.Tests/SocksPortContractTests.cs` asserts the pair matches across the allocator's range. Windows has no bridge (a test keeps it that way); its SOCKS inbound only serves the preflight that proves a candidate before the TUN starts.

### Keeping a match alive on Windows

Players were dropped from game servers mid-match. Each rule below was measured on the bench
(Xray-core-RTC `fork/bench/`) or on a hosted Windows runner (`tools/WinTunnelTest`), and each is
silent when broken — see the `windows-tunnel` skill for how to re-check them.

1. **A network event resets nothing unless the path the core is bound to is gone.** A session
   reset on Hysteria2 drops every TCP session it carries (bench: 16 of 16 long sessions), and the
   old monitor reset on any adapter appearing. `NetworkPathSelector.Classify`: a better path
   appearing is left alone; only `Replaced`/`Restored` act.
2. **Recovery never restarts the core while something cheaper works**: reset sessions → probe →
   swap the proxy outbound in place (`XrayReplaceOutbound`) → hold. A restart removes the adapter
   and every connection on the machine. The tunnel is torn down only after repeated failed
   recoveries, and not at all with the kill switch on.
3. **Health is judged from the core's per-connection bytes through the proxy**
   (`ProxyStallDetector`), never from adapter counters or a TCP probe through the TUN: the TUN's
   gVisor stack completes handshakes and ACKs locally, so both look healthy while the proxy is dead.
   For the same reason latency is measured from sockets pinned to the physical interface
   (`PhysicalProbe`) — unpinned, every server "answers" in 1 ms.
4. **Rule changes apply live** (`XrayReloadRouting`), so changing split tunnelling or site rules
   never interrupts anything; open connections keep their route until the user restarts that
   app's connections from the «Приложения» screen.

**Testing the Windows tunnel behind a system-wide proxy client** (Proxifier and friends): those hook outbound TCP at the WFP layer *before* routing, so TCP never reaches the Horus adapter and the tunnel appears to carry only UDP and ICMP. Exclude `Horus.exe` or stop the redirector before drawing conclusions.

### Staying alive on Android with the screen off

The tunnel dying "some time after the screen went off" was never one bug. Four rules, each
verified on device (23.08.2026) and each silent when broken:

1. **The tunnel must not be metered, and nothing may make it metered.** Android treats a VPN
   as metered until told otherwise, and background restrictions in Doze hang off exactly that
   — music does not fetch the next track, apps do not fetch notification content. So
   `builder.SetMetered(false)` (API 29+, from `UserPreferences.MeteredConnection`, default
   off). But `setMetered` is only half: `Vpn.applyUnderlyingCapabilities` ORs meteredness
   across every network named by `setUnderlyingNetworks`, so naming a metered cellular network
   alongside Wi-Fi drags the whole tunnel back to metered. **`ApplyUnderlyingNetwork` therefore
   always passes `null`** — the documented "track the system default network". Do not
   reintroduce an explicit array; `AndroidNetworkMonitor` still computes its ranking, but only
   for handover detection and transport reporting. Check with
   `dumpsys connectivity | grep "ni{VPN CONNECTED"` — `NOT_METERED` must be in the capability
   list on Wi-Fi.
2. **`foregroundServiceType="systemExempted"` needs `FOREGROUND_SERVICE_SYSTEM_EXEMPTED`.**
   Declaring the type without the permission is not a milder request: `startForeground`
   rejects it, `TryStartForeground` falls through to `specialUse`, and the exemption is never
   granted — invisibly, because the fallback keeps the tunnel working. Verify at the system
   level: `dumpsys activity services … | grep types=` must show `00000400`.
3. **Never destroy the service to rebuild the tunnel.** Rebuilding used to mean
   `StopForeground` + `StopSelf` + a fresh service, and starting a foreground service *from
   the background* is restricted on Android 12+ and refusable in Doze — so a rebuild that
   happened while the screen was off could simply never come back. `CreateTunnel` now has
   three branches: identical options → touch nothing; new descriptor → `establish()` again and
   hand the fd to the bridge (`HevSocksTunnel.Rebind`, closing the old fd *after*); no tunnel →
   normal build. `OnDestroy` runs only on a real stop or a revoke.
4. **Do not believe the platform's verdict about your own tunnel.** Losing
   `NET_CAPABILITY_VALIDATED` means Android's probe *through the VPN* failed, and in Doze that
   probe is deferred like any other background request. `OnTunnelSuspect` cross-checks against
   `TunnelHealthMonitor.LastCarriedAtMs` and ignores the report when traffic flowed recently
   (3 min — wider than RethinkDNS's 30 s because counters are sampled every 90 s with the
   screen off).

Two consequences worth knowing before debugging:

- **The sticky service does not come back after process death** on the test device (Infinix,
  Android 14): `OnStartCommand` returns `Sticky`, the app is in the deviceidle whitelist and
  standby bucket 5, and Android still schedules no restart. The working safety net is
  `App.OnStart` → `VpnManager.TryRestoreOrAutoConnectAsync()`, which reconnects when the user
  next opens the app.
- **That safety net depends on startup ordering.** `ShellViewModel.EnsureStartedAsync` must
  make its *second* caller await the first's work, not return early — `App.OnStart` and
  `RootPage.OnAppearing` race, and a plain "already started" flag let the loser run
  `TryRestoreOrAutoConnectAsync` against an unauthenticated manager, which gives up with "no
  session" and never retries. Anything gated on `_auth.IsAuthenticated` at startup has this
  hazard.

### Protocol config

`XrayConfigBuilder` renders one SOCKS5 inbound on `127.0.0.1:1080` (dialled by hev-socks5-tunnel on Android), the selected proxy outbound, plus `freedom`/`blackhole`. On Windows `WindowsTunnelConfig.Build` reshapes that shared config: the `tun` inbound first, DNS answered by the core and forwarded through the proxy, lead routing rules, and the split-tunnel `process` rules. Routing keeps private/loopback ranges direct and avoids `geoip:`/`geosite:` predicates so no `.dat` assets are needed (otherwise `XraySetAssetPath` would be required before `XrayStart`). Because the core is a library with no usable stdout, its log is routed to a file via `log.error` — see `DiagnosticPaths`.

**The fork's protocol names are not the usual ones.** Hysteria2 is registered as `hysteria` (both `"protocol"` and `streamSettings.network`) — `hysteria2` is not a valid transport and yields `Config: unknown transport protocol: hysteria2`. Its auth password lives on the *transport* (`hysteriaSettings.auth`), not the outbound, and `settings` is flat `{version:2, address, port}` rather than a `servers[]` array. Salamander obfuscation and UDP port hopping are **finalmask** features (`streamSettings.finalmask.udp[]` and `.quicParams.udpHop`), not hysteria ones. ALPN must include `h3`. Source of truth: `infra/conf/hysteria.go`, `infra/conf/transport_method.go` and `test-configs/server.json` in the core fork.

`xhttp` is an alias for `splithttp` and still needs an `xhttpSettings` object with `path`/`mode`.

`XrayTest` validates a config without starting it, so a schema mismatch surfaces as a parser message rather than a timeout. To check a change against the real core: `xray.exe run -test -c config.json`.

### API

HorusAPI v1, base URL from `appsettings.json`. Auth is a **custom session scheme**, not JWT: `POST /auth/login` or `/auth/verify` returns a session token, replayed by `HttpAuthHandler` in the `X-Session-Key` header.

- Registration does **not** sign you in — `POST /auth/register` mails a 6-digit code (202, with a `pendingToken`), and `POST /auth/verify` exchanges it for the session.
- **An unconfirmed account is not a dead end.** `/auth/login` answers `403 code=email_unverified` *plus* a pending ticket, the masked address and the code/resend countdowns (`PendingVerification`). The app goes to the confirm screen with the ticket, and `/auth/verify` / `/auth/resend-code` send `pending_token` — someone who signed in by **username** was never told which address to quote. `attemptsLeft` comes back on a wrong code only on the ticket path.
- A session has **no expiry of its own**. `expiresAt` on a login response is the **subscription** end (null for none) — never sign out on it: a lapsed subscriber must still be able to sign in and renew. `GET /whoami` returns the same date plus the egress IP and `lastConnectedAt`.
- `GET /servers` lists ping candidates (`max_reservations` is the hard cap, `max_clients` only a soft one); `POST /servers/select` binds; `GET /servers/connect` takes no id and returns `{server, outbounds[]}` for the bound node.
- **The API's `message` is English and written for developers; the app never shows it.** `ReadErrorAsync` maps the `code` to Russian through `ErrorText.Explain` (pure, pinned by `ErrorTextTests`) and logs the English one. A refusal without a code falls back to the caller's own Russian text — which is why sign-up checks `AccountRules` (copied from the API's username/password rules) before sending.
- `Horus.Tests/ApiContractTests.cs` runs the app's real `ApiService` against a live HorusAPI; skipped unless `HORUS_API_URL` is set (see the class comment for the other variables).

Endpoints the old API had and v1 does not: `/geo/*`, `/routing-rules`, `/logs/error`. `GeoDataService`, `RoutingService` and `ErrorReportingService` are local-only as a result — error reports fall back to a mailto with a zip archive.

### Subscriptions are bought on the site

There is no payment UI in the app. Every "Оформить / Продлить подписку" goes through
`Presentation/Navigation/SubscriptionPage`, which opens `{ApiBaseUrl}/pay` in the external
browser (`OfferAsync` asks first when the user did not ask for it — tapped connect without a
subscription, or the API answered `subscription_expired`). The site already has the whole flow:
tariffs, the auto-payment consent, promo and partner codes, the bank and waiting for it. The
browser does not share the app's session, so the first visit signs in on the site. Back in the
app nothing needs doing: `AccountSync` refreshes on foreground and polls every 20 s while the
subscription is inactive.

## Implementation Status

Auth, servers, connect and the xray pipeline are wired to the real backend; buying a subscription is the site's (see above). Server ping is a TCP handshake per node (`LatencyProbe`; pinned to the physical interface on Windows). The shared Settings kill-switch toggle is still a placeholder; on Windows it is replaced by a working one (`WindowsSettingsSection`). See `docs/PLAN-remaining-functions.md`.

## UI / Styling

All colors, typography, and spacing are defined as `StaticResource` in `Presentation/View/App.xaml`. The palette uses deep purples (`DeepVoid`, `NightPurple`) with neon accents (`NeonCyan #00E5FF`, `NeonViolet #BF5FFF`, `NeonGreen #39FF9F`). Status-specific colors follow the pattern `Connected*`, `Disconnected*`, `Connecting*`. Always use these resources rather than inline hex values.

Navigation is a custom root page (`RootPage`, screens switched by `Navigator`): sidebar on desktop, bottom tabs on phones. Windows adds its own Home, an «Приложения» screen and a Settings section through `IPlatformScreens`; they are built in C# under `Platforms/Windows/Views/` and take every colour and style from `App.xaml` through `Ui`.
