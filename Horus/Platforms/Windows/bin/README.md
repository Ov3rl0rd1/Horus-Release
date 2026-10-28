# Windows native components

Both files are copied next to `Horus.exe` at build time. A missing one fails the **build**
(`GuardWindowsNativeCore` in `Horus.csproj`) rather than producing a package that starts and
then cannot connect, and both are checked again at **startup** (`NativeDependencies`).

| File | Put in | Copied to | Provides |
|---|---|---|---|
| `xray.dll` | `Platforms/Windows/bin/` | next to `Horus.exe` | The VPN core, **including the TUN**. |
| `wintun.dll` | `Platforms/Windows/bin/Native/` | next to `Horus.exe` | TUN adapter driver (signed by WireGuard). |

`xray.dll` must be the **x64** build of the fork (`Xray-core-RTC`, `libxray/`), from a commit
that has the embedded-TUN changes (`fork/patches/0007-tun-embedded-lifecycle.patch`) — an older
core still runs, but without live rule reloads, outbound swaps or the connection list, and the
app logs that it is falling back to restarts. Build it with the fork's
`.github/workflows/build-lib.yml`, or locally with the same flags:

```bash
GOOS=windows GOARCH=amd64 CGO_ENABLED=1 CC=x86_64-w64-mingw32-gcc \
CGO_CFLAGS="-O2 -fstack-protector-strong" \
go build -buildmode=c-shared -trimpath -buildvcs=false \
  -ldflags "-X github.com/xtls/xray-core/core.build=$(git describe --always --dirty) -s -w -buildid= -extldflags=-Wl,--nxcompat,--dynamicbase,--high-entropy-va" \
  -o xray.dll ./libxray
```

The shipped `xray.dll` is fork commit **d813c77d** — the fork rebased onto upstream v26.10.10, release
`lib-from-v26.10.10` (the core logs it at start: `Xray 26.10.10 … d813c77`). It carries the
embedded-TUN lifecycle, live controls, outbound binding by route metric, the gVisor NIC
attached only after its handlers, and an adapter GUID that is not still held by the previous
adapter's device (`proxy/tun/fork_open_windows.go` — a reconnect used to fail with wintun's
"problem code: 0x1F" until a reboot). When replacing it, keep this line current — the version string
is the only way to tell from a user's log which core they ran.

**`wintun.dll` must sit beside `Horus.exe`, not in a subfolder.** The core loads it with
`LOAD_LIBRARY_SEARCH_APPLICATION_DIR`, which is the directory of the process's executable — not
of `xray.dll`. Put anywhere else, the adapter is never created.

## How the tunnel works

The core owns the TUN in-process: its `tun` inbound creates the wintun adapter, assigns
`198.18.0.1/30` (and `fdfe:dcba:9876::1/126`), installs `0.0.0.0/1` + `128.0.0.0/1` (and the
IPv6 halves) on it, and points the adapter's DNS at `198.18.0.2`. The routes live on the
adapter, so they vanish with it — a crash cannot strand them. The core pins every socket it
opens to the physical interface (`autoOutboundsInterface`), so its own connection to the node
never enters the tunnel; when no physical interface exists it refuses the socket instead of
letting it loop. It also keeps DNS from leaving by any other interface
(`autoSystemWfpBlockLeak: ["dns"]`), so Windows does not ask the router's resolver in parallel;
the filters live in a dynamic WFP session, which Windows deletes with the process. See
`Platforms/Windows/Tunnel/WindowsTunnelConfig.cs`.

Nothing the tunnel changes in the system outlives the process. Builds before this one bound DNS
with a persistent NRPT rule instead, and an app that died with the VPN on (a crash, a shutdown)
left every lookup on the machine pointed at `198.18.0.2` — after a reboot nothing resolved until
Horus was started again. The app removes such a rule at start.

What the app still does itself: wait for the adapter, read its counters for the speed graph.

| | Android | Windows |
|---|---|---|
| TUN bridge | hev-socks5-tunnel, in-process, fd from `VpnService` | none — the core's `tun` inbound |
| Loop prevention | the app's UID excluded from the TUN | the core binds its sockets to the physical interface |
| Split tunneling | per-UID in `VpnService` | the core's `process` routing rule |
| Traffic counters | `hev_socks5_tunnel_stats` | adapter counters (`GetIfEntry2`) |
| Privileges | user grants VPN consent | process must be **elevated** |

## What used to be here

`hev-socks5-tunnel.exe` + `msys-2.0.dll` (the bridge, run as a child process because the
Cygwin runtime could not live inside a CLR process) and `WinDivert.dll` + `WinDivert64.sys`
(an attempt at per-process routing). Both are gone: the bridge's job is the core's now, and the
WinDivert approach never steered a single connection — its P/Invoke named an export the DLL
does not have. Leaving a WinDivert driver on gamers' machines also invited trouble with
anti-cheat software.

## Testing on a machine with a system-wide proxy client

A redirector such as **Proxifier** hooks outbound TCP at the WFP layer, *before* the route
table is consulted. With one running, TCP never reaches the Horus adapter no matter how the
routes look. Exclude `Horus.exe` and the destinations under test, or stop the redirector,
before concluding anything about the tunnel.
