---
name: windows-tunnel
description: How the Windows tunnel is built and how to test a change to it without a Windows machine — the namespace bench for the core, the hosted-runner end-to-end test (tools/WinTunnelTest), the one-minute UI type-check, which to use for which question, and the traps that cost time before. Use for any change under Platforms/Windows, to the xray fork's TUN or libxray, for a "the tunnel drops / crashes / split tunneling does not work" report, or before rebuilding xray.dll.
---

# The Windows tunnel, and how to test it

## What runs

The core (`xray.dll`, the fork's `libxray`) runs **in-process and owns the TUN**: its `tun`
inbound creates the wintun adapter, installs `0/1 + 128/1` on it, answers DNS at
`198.18.0.2`, and pins every socket it opens to the physical interface
(`autoOutboundsInterface`, `IP_UNICAST_IF`). There is no hev, no route.exe, no WinDivert.

| Piece | File | Job |
|---|---|---|
| Config shaping | `Platforms/Windows/Tunnel/WindowsTunnelConfig.cs` | TUN inbound, DNS, routing lead rules, process rules for split tunnelling |
| Controller | `Platforms/Windows/WindowsVpnController.cs` | prove candidates on SOCKS, start with TUN, health loop, recovery, live reload |
| Network | `WindowsNetworkMonitor.cs`, `Tunnel/NetworkPath.cs` | act only when the path the core is bound to is gone |
| Health | `Tunnel/ProxyStallDetector.cs` | judge from the core's per-connection bytes, never from adapter counters |
| Live API | `Tunnel/XrayLive.cs` | `XrayReloadRouting`, `XrayReplaceOutbound`, `XrayConnections`, `XrayCloseConnections` |
| Apps screen | `Views/AppsView*.cs`, `Tunnel/AppTraffic.cs`, `WindowsConnectionMonitor.cs` | per-app route from the core's own records; restart = close in the core |
| Home | `Views/DesktopHomeView.cs`, `TrafficHistory.cs`, `Tunnel/NodeLatencyMonitor.cs` | real graph, ping to the node |

Windows screens enter the shared shell through `IPlatformScreens` (registered only under
`#if WINDOWS`); `PlatformSeparationTests` keeps it that way.

## Which test answers which question

Run them cheapest first. Each line says what it **cannot** see, too.

| Question | Tool | Cost | Blind to |
|---|---|---|---|
| Pure logic: config shape, matchers, path decisions, stall verdicts, latency sampling, graph axis, per-app grouping | `dotnet test Horus.Tests` | seconds | everything native |
| Does the Windows C# compile? | `dotnet build tools/WinUiTypeCheck` (Linux OK) | ~1 min | XAML, rendering, WinRT behaviour |
| How does the core behave under a player's load — drops, stalls, resets, restarts, swaps, data races? | fork `fork/bench/` (Linux, root) | minutes | Windows itself |
| Does it work on Windows — adapter, routes, binding, process rules, DNS, live API, stop/start? | `tools/WinTunnelTest` on the `windows-tunnel` CI job | ~1 min run, push needed | real user networks, UI |
| Does the MAUI head build and ship its payload? | `windows` CI job | ~10 min | behaviour |
| What does the user's machine do? | the user's logs (below) | a round trip | — |

### Unit tests

`Horus.Tests` links pure files (`<Compile Include … Link=…>`); a new pure type needs a link
line. Anything that only needs .NET (sockets included) can be linked: `PhysicalProbe` and
`NodeLatencyMonitor` are tested against loopback listeners and scripted probes.

### WinUiTypeCheck

Compiles `Horus/**/*.cs` with `WINDOWS` defined against MAUI's net10.0 reference assemblies.
It found, before any Windows build: a nested class named `Drawable` hiding
`GraphicsView.Drawable`, and `View` resolving to the namespace `Horus.Presentation.View`
inside `Horus.Presentation.*` (write `Microsoft.Maui.Controls.View` there). When you compile
another `.xaml.cs`, add its generated fields to `XamlStubs.cs`.

### The core bench (Xray-core-RTC `fork/bench/`)

Two namespaces (player PC / internet), the core embedded through libxray's own `api.go` and
`live.go`, and `load` reporting game-stream stalls, broken long sessions, failed connects.
`fork/bench/README.md` has setup and scenarios. Use it **before** changing the client when the
symptom is "the connection drops": it tells core problems from client problems.

- `HARGS="-reset 10s"` / `-restart 7s` reproduce what the client's recovery does to sessions.
- `race-live.sh` is the race detector over the live API with restarts. A clean run has 0–1
  reports (the 1 is upstream Hysteria2 `udpSessionManager.closed`). Dozens mean a regression.
- The race build needs `-gcflags=all=-d=checkptr=0` (upstream VLESS Vision trips checkptr).
- `pkill -f` matches your own shell's command line; use `pkill -x name`.
- `curl --socks5` silently ignores the proxy when `NO_PROXY` matches: `env -u NO_PROXY -u no_proxy curl …`.

### WinTunnelTest (real Windows, hosted runner)

`tools/WinTunnelTest/Program.cs` drives the real `xray.dll` with the app's own config code
linked in, against a VLESS node run by a second copy of itself (its egress pinned to the
physical NIC). Hosted runners are administrators, so the adapter can be created. Each check
prints `PASS/FAIL name — detail`; the exit code is the number of failures.

To add a check: write it next to the related ones, make the detail say what was measured, and
push. Read the result with the GitHub MCP tools: `actions_list` (runs on the branch) →
`list_workflow_jobs` → `get_job_logs` with the `windows-tunnel` job id. Things that look like
failures and are not: an SRV name retired upstream returns NXDOMAIN *through the proxy* (that is
forwarding working — pick a live record); the node's log is locked until it exits (read with
`FileShare.ReadWrite` after).

### The user's machine

What WinTunnelTest cannot reproduce — Wi-Fi roaming, sleep/resume, anti-cheat drivers, a
Proxifier-style WFP redirector, a real ISP throttling QUIC — needs the user. Ask for:
the archive from Настройки → Диагностика → «Собрать логи» (the `logs` folder under the app's
cache directory, `DiagnosticPaths`: `xray.log`, `crash.log`, and `native-stderr.log.prev`,
which is the only place a Go fatal error or a .NET fail-fast of the *previous* session lands), the
«Состояние» screen text, Event Viewer → Windows Logs → Application (Application Error 1000 /
.NET Runtime 1026 for `Horus.exe`), and the core version line (`Xray 26.9.9 … <commit>`).

## Rebuilding xray.dll

```bash
cd Xray-core-RTC   # committed, not dirty: the commit id is in every user's log
GOOS=windows GOARCH=amd64 CGO_ENABLED=1 CC=x86_64-w64-mingw32-gcc CGO_CFLAGS="-O2 -fstack-protector-strong" \
go build -buildmode=c-shared -trimpath -buildvcs=false \
  -ldflags "-X github.com/xtls/xray-core/core.build=$(git describe --always --dirty) -s -w -buildid= -extldflags=-Wl,--nxcompat,--dynamicbase,--high-entropy-va" \
  -o xray.dll ./libxray
x86_64-w64-mingw32-objdump -p xray.dll | grep -E "Xray(Start|ReloadRouting|ReplaceOutbound|Connections)"
```

Copy to `Horus/Platforms/Windows/bin/`, update the commit line in `bin/README.md`, push, and
let `windows-tunnel` run against it. Fork changes go through `bash fork/bin/fork export`
(and `--check`); new overlay files go in `fork/manifest.txt`.

## Traps, each of which cost a round before

- **The TUN answers TCP handshakes itself** (gVisor `CreateEndpoint` before the dial). Anything
  that measures *through* the tunnel measures nothing: a TCP ping says ~1 ms for every server,
  adapter byte counters grow while the proxy is dead. Measure with a socket pinned to the
  physical interface (`PhysicalProbe`), judge health from the core's per-connection counters.
- **`XrayResetConnections` on Hysteria2 drops every TCP session it carries.** Never call it on a
  network event that did not take away the path the core is bound to.
- **Recovery must not restart the core** while a swap (`XrayReplaceOutbound`) can do: a restart
  removes the adapter and every connection on the machine with it.
- `IP_UNICAST_IF` takes the interface index in **network** byte order for IPv4, host order for IPv6.
- `wintun.dll` must sit beside `Horus.exe`; the core loads it from the application directory.
- WinRT `FileOpenPicker` fails in an elevated process — Horus is always elevated; use
  `GetOpenFileNameW` (`ExePicker`).
- A Windows UDP socket that receives ICMP port unreachable reports `WSAECONNRESET` on the next
  receive; anything that makes the TUN answer "unreachable" can look like a game disconnect.
