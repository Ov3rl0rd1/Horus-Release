# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Horus is a cross-platform VPN client built with .NET MAUI targeting Android, iOS, macOS, and Windows. It tunnels through a **custom xray-core build** that carries three outbounds: VLESS/REALITY, Hysteria2 and olcRTC. The project is in early development — some service logic is still stubbed.

## Build & Run Commands

```bash
# Build for a specific platform (run from solution root)
dotnet build -f net10.0-android
dotnet build -f net10.0-windows10.0.17763.0
dotnet build -f net10.0-ios
dotnet build -f net10.0-maccatalyst

# Run on Android (device/emulator must be connected)
dotnet run -f net10.0-android

# Run on Windows
dotnet run -f net10.0-windows10.0.17763.0

# Run tests
dotnet test Horus.Tests/Horus.Tests.csproj

# Build an APK to hand to a tester (fails loudly unless signed, Release and non-debuggable)
dotnet publish Horus/Horus.csproj -f net10.0-android -c Release \
  -p:HorusDistribution=true -p:ApplicationVersion=<N> -p:ApplicationDisplayVersion=0.9.<N>
```

No CI exists — all builds go through the standard .NET CLI. The solution file is `Horus.slnx`.

Distribution is **direct APK**, never `.aab`: `AndroidProcessRunner`-era concerns aside, the
app id `com.horus.vpn` is fixed because `libhev_socks.so` resolves its JNI entry points
against `com/horus/vpn/VPNService`.

## Architecture

Clean Architecture with MVVM, enforced by folder boundaries:

- **`Domain/`** — Pure contracts and models; no implementation. `Interfaces/` holds service contracts; `Models/` holds enums and data classes; `Events/EventsArgs.cs` defines all event argument types.
- **`Application/`** — Service implementations (singletons). `VpnManager.cs` is the central orchestrator that coordinates protocol, platform, auth, and subscription services.
- **`Presentation/`** — MVVM UI. `View/` holds XAML pages; `ViewModels/` uses CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).
- **`Protocols/`** — The VPN core. xray-core is linked as a **C shared library** (`libxray.so` / `xray.dll`) and runs **in-process**: `XrayInterop` is the P/Invoke surface (`XrayStart`/`XrayStop`/`XrayTest`/`XrayVersion`), and `XrayProtocol` is the single `IVpnProtocol` on top of it. `ShareLinkParser` turns the `vless://` / `hysteria2://` links the API returns into `ShareLink`s, and `XrayConfigBuilder` renders those into an xray config. `ProtocolType` names an *outbound*, not a separate binary.
- **`Platforms/`** — Platform-specific code. Android and Windows both carry a real tunnel, using the *same* hev-socks5-tunnel in two hosting models: Android runs it in-process (`HevSocksTunnel` P/Invokes it and hands over the `VpnService` TUN fd), Windows runs it as a **child process** that creates its own wintun adapter (`WindowsVpnService`). Its YAML comes from one shared generator, `Protocols/HevTunnelConfig.cs`. Binaries live under `Platforms/Android/lib/<abi>/` and `Platforms/Windows/bin/` — see the READMEs there.

### Dependency Injection

All services are registered in `MauiProgram.cs` as singletons. Platform services (`IVpnPlatformService`, `IProcessRunner`) are registered conditionally per-platform using `#if ANDROID` / `#if WINDOWS` guards. ViewModels are registered as transient.

### Key flow

`MainViewModel` → `VpnManager.ConnectAsync()` → `IApiService.GetServerConnectionAsync()` (`GET /servers/connect` — the **API** picks and binds the server, and returns one share link per protocol) → `XrayProtocol.ConnectAsync()` (`XrayTest` then `XrayStart`) → **preflight** (egress IP fetched directly and through the SOCKS5 proxy) → `IVpnPlatformService` (create TUN) → `ITrafficMonitorService` (1 Hz counter poll).

Connect falls back **Hysteria2 → VLESS → olcRTC**, skipping protocols the node didn't publish. A fallback re-renders the config with a different `proxy` outbound; `XrayStop` must run before each retry because `XrayStart` fails while an instance exists.

### Two invariants that silently kill the tunnel

1. **Anything the config routes `direct` must have a real way out, or it re-enters the tunnel.** xray runs in-process and has no socket-protect hook, so each platform substitutes its own escape hatch: Android excludes the app's UID (`HorusVpnTunnelService.ApplySplitTunneling`), which covers everything at once. Windows has no such notion — its `direct` outbound is simply the OS route table, so once a default route points at the TUN, a "direct" rule is not direct at all. `WindowsVpnService` therefore installs a `/32` host route via the physical gateway for every address in `TunnelOptions.BypassIps`, which must contain:
   - **the node** (`TunnelOptions.NodeAddress`) — without it the transport carries itself and deadlocks; a failed DNS pre-resolution must abort the connect rather than proceed;
   - **the resolvers** the config sends down `direct` (`XrayConfigBuilder.ResolverIps`) — without them every DNS query loops, and the tunnel connects but resolves nothing, which presents as "no site opens".

   Multicast and broadcast are the third case and are handled differently: they go to `blackhole` (`XrayConfigBuilder.DropRanges`), because a host route per group is meaningless and forwarding them costs a SOCKS5 session per packet. Windows chatters on a fresh interface, so leaving them on `direct` amplified into ~1000 sessions in 3 seconds.

   Consequence on Android: the app's API traffic bypasses the VPN, so **`/whoami` reports the real IP while connected and cannot verify the tunnel** — verify from another app or through the SOCKS5 proxy. On Windows only the node and the resolvers are excluded, so `/whoami` is meaningful there.
2. **The core's SOCKS5 inbound and hev's `socks5.port` must agree** — a mismatch establishes a tunnel that carries nothing. The port is no longer fixed at 1080: `SocksPortAllocator` picks the first free port from there (desktop machines routinely already have something on 1080), and the single chosen value flows through `XrayConfig.SocksPort` → `TunnelOptions.SocksPort` → `HevTunnelConfig.Build`. One generator, shared by both hosts; `Horus.Tests/SocksPortContractTests.cs` asserts the generated pair matches across the allocator's range and that neither host has re-inlined its own copy.

**Testing the Windows tunnel behind a system-wide proxy client** (Proxifier and friends): those hook outbound TCP at the WFP layer *before* routing, so TCP never reaches the Horus adapter and the tunnel appears to carry only UDP and ICMP. Exclude `Horus.exe` or stop the redirector before drawing conclusions.

### Protocol config

`XrayConfigBuilder` renders one SOCKS5 inbound on `127.0.0.1:1080` (dialled by hev-socks5-tunnel), the selected proxy outbound, plus `freedom`/`blackhole`. Routing keeps private/loopback ranges direct and avoids `geoip:`/`geosite:` predicates so no `.dat` assets are needed (otherwise `XraySetAssetPath` would be required before `XrayStart`). Because the core is a library with no usable stdout, its log is routed to a file via `log.error` — see `DiagnosticPaths`.

**The fork's protocol names are not the usual ones.** Hysteria2 is registered as `hysteria` (both `"protocol"` and `streamSettings.network`) — `hysteria2` is not a valid transport and yields `Config: unknown transport protocol: hysteria2`. Its auth password lives on the *transport* (`hysteriaSettings.auth`), not the outbound, and `settings` is flat `{version:2, address, port}` rather than a `servers[]` array. Salamander obfuscation and UDP port hopping are **finalmask** features (`streamSettings.finalmask.udp[]` and `.quicParams.udpHop`), not hysteria ones. ALPN must include `h3`. Source of truth: `infra/conf/hysteria.go`, `infra/conf/transport_method.go` and `test-configs/server.json` in the core fork.

`xhttp` is an alias for `splithttp` and still needs an `xhttpSettings` object with `path`/`mode`.

`XrayTest` validates a config without starting it, so a schema mismatch surfaces as a parser message rather than a timeout. To check a change against the real core: `xray.exe run -test -c config.json`.

### API

HorusAPI v1, base URL from `appsettings.json`. Auth is a **custom session scheme**, not JWT: `POST /auth/login` or `/auth/verify` returns a session token, replayed by `HttpAuthHandler` in the `X-Session-Key` header.

- Registration does **not** sign you in — `POST /auth/register` mails a 6-digit code (202), and `POST /auth/verify` exchanges it for the session.
- `expiresAt` on a login response is the **session** expiry. The **subscription** expiry comes from `GET /whoami`, which also returns the egress IP. These are stored separately in `StorageService`.
- `GET /servers/best` is the catalogue; `GET /servers/connect` takes no id.

Endpoints the old API had and v1 does not: `/geo/*`, `/routing-rules`, `/logs/error`. `GeoDataService`, `RoutingService` and `ErrorReportingService` are local-only as a result — error reports fall back to a mailto with a zip archive.

## Implementation Status

Auth, servers, connect and the xray pipeline are wired to the real backend. Still placeholder: payments (`PaymentViewModel` — no billing endpoints exist yet), per-server ping (`ServerInfo.PingMs` is always null for real servers), and the kill-switch/auto-connect toggles in Settings. See `PLAN-remaining-functions.md`.

## UI / Styling

All colors, typography, and spacing are defined as `StaticResource` in `Presentation/View/App.xaml`. The palette uses deep purples (`DeepVoid`, `NightPurple`) with neon accents (`NeonCyan #00E5FF`, `NeonViolet #BF5FFF`, `NeonGreen #39FF9F`). Status-specific colors follow the pattern `Connected*`, `Disconnected*`, `Connecting*`. Always use these resources rather than inline hex values.

Navigation is shell-based (`AppShell.xaml`) with two tabs: Home (`MainPage`) and Settings (`SettingsPage`). `AuthPage` is pushed modally when the user is not authenticated.
