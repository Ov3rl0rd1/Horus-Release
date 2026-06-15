# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Horus is a cross-platform VPN client built with .NET MAUI targeting Android, iOS, macOS, and Windows. It uses the Hysteria2/QUIC protocol. The project is in early development — most service logic exists as stubs with `NotImplementedException`.

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

# Run tests (if any test project is added)
dotnet test
```

No custom build scripts exist — all builds go through the standard .NET CLI. The solution file is `Horus.slnx`.

## Architecture

Clean Architecture with MVVM, enforced by folder boundaries:

- **`Domain/`** — Pure contracts and models; no implementation. `Interfaces/` holds service contracts; `Models/` holds enums and data classes; `Events/EventsArgs.cs` defines all event argument types.
- **`Application/`** — Service implementations (singletons). `VpnManager.cs` is the central orchestrator that coordinates protocol, platform, auth, and subscription services.
- **`Presentation/`** — MVVM UI. `View/` holds XAML pages; `ViewModels/` uses CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).
- **`Protocols/`** — VPN protocol implementations. `ProtocolFactory` creates the right `IVpnProtocol` based on `ProtocolType` enum. `Hysteria2Protocol` generates YAML config and runs the binary via `IProcessRunner`.
- **`Platforms/`** — Platform-specific code. Only Android is actively developed (`AndroidVpnService` extends Android's `VpnService`; `AndroidProcessRunner` runs the Hysteria2 binary process).

### Dependency Injection

All services are registered in `MauiProgram.cs` as singletons. Platform services (`IVpnPlatformService`, `IProcessRunner`) are registered conditionally per-platform using `#if ANDROID` / `#if WINDOWS` guards. ViewModels are registered as transient.

### Key flow

`MainViewModel` → `VpnManager.ConnectAsync()` → `IAuthService` (validate token) → `ISubscriptionService` (get server) → `IVpnProtocol.ConnectAsync()` → `IVpnPlatformService` (create TUN) + `IProcessRunner` (start Hysteria2 binary) → `ITrafficMonitorService` (stats loop).

### Protocol config

`Hysteria2Config` serializes to YAML written to a temp file. The binary reads this file. Config includes server address, auth token, TLS/QUIC settings, and a SOCKS5 proxy address that `AndroidVpnService` uses to bridge traffic through the TUN interface.

### API

Base URL is `https://localhost:7083` (configured in `ApiService.cs`). Endpoints: `POST /login`, `GET /servers`, `GET /servers/{id}/connect`. Responses are deserialized case-insensitively. JWT tokens are parsed with `System.IdentityModel.Tokens.Jwt`.

## Implementation Status

The skeleton is complete; business logic is not. When implementing a service, check `Domain/Interfaces/` for the contract, `Application/` for the stub, and `MauiProgram.cs` to confirm registration. Most `Application/` services throw `NotImplementedException` — replace these rather than adding new files.

## UI / Styling

All colors, typography, and spacing are defined as `StaticResource` in `Presentation/View/App.xaml`. The palette uses deep purples (`DeepVoid`, `NightPurple`) with neon accents (`NeonCyan #00E5FF`, `NeonViolet #BF5FFF`, `NeonGreen #39FF9F`). Status-specific colors follow the pattern `Connected*`, `Disconnected*`, `Connecting*`. Always use these resources rather than inline hex values.

Navigation is shell-based (`AppShell.xaml`) with two tabs: Home (`MainPage`) and Settings (`SettingsPage`). `AuthPage` is pushed modally when the user is not authenticated.
