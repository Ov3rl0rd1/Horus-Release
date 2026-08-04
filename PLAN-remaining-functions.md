# Horus — v2 redesign: what's wired, what's placeholder, and the plan to finish

This document accompanies the v2 UI redesign (dark-purple + gold, per the Claude Design
handoff). It records what the new UI connects to the real system, what is intentionally a
placeholder, and a concrete plan for turning each placeholder into a working feature.

---

## 1. Architecture of the new UI (orientation for the next dev)

The redesign **replaces MAUI Shell** with a small custom navigation stack so Android
(bottom tabs + onboarding) and Windows (left sidebar + account card) can diverge while
sharing one set of screen views.

| Piece | File | Role |
|---|---|---|
| Screen enum | `Presentation/Navigation/AppScreen.cs` | The set of top-level screens |
| Router | `Presentation/Navigation/Navigator.cs` | `CurrentScreen` + `Go(screen)` (singleton) |
| Alerts | `Presentation/Navigation/Dialog.cs` | `DisplayAlert`/action-sheet without Shell |
| Shared state | `Presentation/ViewModels/AppSession.cs` | Selected server (Home ↔ Servers) |
| Root/brain | `Presentation/ViewModels/ShellViewModel.cs` + `View/RootPage.xaml` | Screen switching + adaptive chrome (sidebar vs bottom tabs via `OnIdiom`) |
| Screens | `Presentation/View/Screens/*.xaml` | One `ContentView` per screen, all shared across platforms |

Chrome is chosen at runtime: `IsDesktop` → sidebar, otherwise → bottom tab bar.
To add a screen: add to `AppScreen`, add an `Is…` flag + nav command on `ShellViewModel`,
add a `ContentView`, and wire it in `RootPage.xaml`.

**To finish the intended look:** drop these exact font files into `Resources/Fonts/`
(already registered in `MauiProgram.cs`; the UI references these families and falls back to
the system font until the files are present):
`Manrope-Regular.ttf`, `Manrope-SemiBold.ttf`, `Manrope-Bold.ttf`, `Unbounded-Bold.ttf`,
`Unbounded-ExtraBold.ttf`. Weights map to `Manrope` / `ManropeSemiBold` / `ManropeBold` /
`Unbounded` / `UnboundedExtraBold` string resources in `App.xaml`.

**DEBUG bypass (for local UI work):** set `AppConfiguration.UseDevBypass = true` in a Debug
build to walk the whole UI without the API — login/register sign in a fake user
(`AuthService.DevSignIn`) and the Home connect button simulates a session
(`MainViewModel.SimulateConnectAsync`). It defaults to **false** so debug builds exercise the
real API; the mock server catalogue in `SubscriptionService` still kicks in whenever
`/servers/best` is unreachable. All of it is under `#if DEBUG` and compiles out of Release.

---

## 2. Already wired to the real backend ✅

- **Login** → `IAuthService.LoginAsync` → `POST /auth/login`.
- **Register** → `IAuthService.RegisterAsync` → `POST /auth/register` (creates the account + session).
- **Logout** → `IAuthService.LogoutAsync` + clears storage.
- **Session restore** on startup → `TryRestoreSessionAsync`.
- **Connect / Disconnect** → `VpnManager.ConnectAsync/DisconnectAsync` (with the existing
  protocol-fallback, TUN, routing and traffic-monitor pipeline).
- **Server list + selection** → `ISubscriptionService.GetAvailableServersAsync` →
  `GET /servers/`. Search, "Автовыбор" (least-loaded), and recommended/all split are live.
- **Live traffic** (down/up speed, session duration) → `ITrafficMonitorService`.
- **Subscription days-left / renew banner** → derived from the persisted `User.expiresAt`.
- **Split-tunneling app list** → `ISplitTunnelingService.GetAvailableEntriesAsync` +
  `SetSelectedEntriesAsync` + `ApplyAsync` (per-platform support flag respected).

---

## 3. Placeholders and the plan to make each real 🚧

Each item below is UI-complete but not connected to a backend/OS capability yet. Search
the code for the noted `TODO`/comment markers.

### 3.1 Email confirmation (6-digit code) — **done**
`POST /auth/register` mails the code (202, no session); `AuthFlowViewModel.DoConfirmAsync`
posts it to `POST /auth/verify`, which issues the session. "Отправить код ещё раз" calls
`POST /auth/resend-code`.

### 3.2 Password reset — **partly done**
`AuthFlowViewModel.DoResetAsync` calls `POST /auth/reset-request`; the API always answers
202 so the UI reveals nothing about whether the address exists.
- **Still open:** the app never handles the emailed link. `IApiService` already exposes
  `IsResetTokenValidAsync` / `ConfirmPasswordResetAsync` — what's missing is a deep link
  (`horus://reset?token=…`) or an in-app "enter token + new password" screen to call them.

### 3.3 Payment / subscription — `PaymentViewModel`
Currently: static plans; `PayAsync` simulates success after a delay.
- **Backend:** integrate a provider (card + СБП/SBP). `POST /billing/checkout { planId, method }`
  → returns a payment URL / SBP QR / client secret; handle the provider webhook to extend
  the subscription; expose `GET /subscription` for status.
- **Client:** `IBillingService` (create checkout, poll/confirm) + models for plans/prices
  (move the hard-coded plans server-side).
- **UI:** wire card fields / SBP QR to the provider; on success call
  `ISubscriptionService.CheckSubscriptionAsync` and refresh the account card + renew banner.
- Also gate connect on the **real** subscription (today `MainViewModel.ToggleConnectAsync`
  opens payment when `SubDaysLeft <= 0`, computed from `expiresAt`).

### 3.4 Per-server ping / latency — `ServerInfo.PingMs`
The UI now shows a **ping pill (ms) with green/amber/red color** on Servers, the Home
server card, and the Recommended lists, and sorts "Автовыбор" + recommendations by ping.
The ping VALUES are still placeholders: real servers have `PingMs == null` (pill hidden),
and the DEBUG mock catalogue carries fixed pings for the demo.
- **To make real:** either measure client-side (ping/TCP-connect each host, cache RTT) or
  have `GET /servers/` return a measured `ping_ms`, then set `ServerInfo.PingMs`.

### 3.5 Public IP display — **done**
`MainViewModel.RefreshAccountAsync` reads the egress IP from `GET /whoami` (called on every
Home entry from `ShellViewModel`) and binds it to the IP card, which masks it while connected.

### 3.6 Kill Switch / Auto-connect / Auto-start — `SettingsViewModel`
Currently: toggles are in-memory only (not enforced, not persisted).
- **Persist** each toggle via `IStorageService`.
- **Kill Switch:** Android → VpnService always-on/lockdown; Windows → WFP/firewall block
  when the tunnel drops.
- **Auto-connect:** connect on app launch when enabled (hook in `App.OnStart`/`ShellViewModel.Initialize`).
- **Auto-start:** Android boot receiver; Windows startup registration (registry/Startup).

### 3.7 Split tunneling enforcement — Settings/Split screen
The list + per-app toggles are wired to `ISplitTunnelingService`, but confirm the selection
is actually applied to the live tunnel:
- **Android:** `VpnService.Builder.addDisallowedApplication` (Blacklist) / `addAllowedApplication`
  for the selected packages; re-apply on connect.
- **Windows:** app-based split tunneling needs WFP — larger effort; keep the mode disabled
  until implemented (`ISplitTunnelingService.IsSupported`).

### 3.8 Reconnect on server change
When the user picks a different server while connected, reconnect automatically
(`VpnManager.ReconnectAsync`) instead of only updating `AppSession`.

### 3.9 Speed graph — done
The Windows desktop Home has a live `SpeedGraphView` (bars = speed, scrolling right→left)
fed by `MainViewModel.SpeedLevel` (normalized from `ITrafficMonitorService`, or the DEBUG
simulator). No further work needed unless a phone-Home graph is wanted too.

---

## 4. Suggested order of work

1. **Fonts** (drop the two `.ttf` files) — completes the visual identity, ~5 min.
2. **Public IP** (`/whoami`) and **per-server ping** — quick wins, high perceived value.
3. **Payment** end-to-end (unblocks the paid-only connect flow).
4. **Email verify** + **password reset**.
5. **Kill Switch / Auto-connect / Auto-start** persistence + enforcement.
6. **Split-tunneling** enforcement on Android; Windows WFP later.

---

## 5. Notes / cleanup left for later

- Old v1 pages (AppShell/MainPage/AuthPage/RegisterPage/SettingsPage) and their view-models
  were removed. Legacy v1 color tokens/styles still live in `Presentation/View/App.xaml`
  (kept to avoid churn) and can be pruned once nothing references them.
- `Colors.xaml`/`Styles.xaml` under `Resources/Styles/` are the default template files and
  are not merged into `App.xaml` — safe to ignore or delete.
