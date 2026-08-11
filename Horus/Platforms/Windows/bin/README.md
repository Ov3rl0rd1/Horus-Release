# Windows native components

Everything here is copied next to `Horus.exe` at build time. Missing required files fail
the **build** (`GuardWindowsNativeCore` in `Horus.csproj`) rather than producing a package
that starts and then cannot connect, and are checked again at **startup**
(`NativeDependencies`) in case a file went missing after install.

## Required

| File | Put in | Copied to | Provides |
|---|---|---|---|
| `xray.dll` | `Platforms/Windows/bin/` | next to `Horus.exe` | The VPN core. Serves SOCKS5 on `127.0.0.1:1080`. |
| `hev-socks5-tunnel.exe` | `Platforms/Windows/bin/Native/` | `Resources/Native/` | The TUN bridge — carries the system's traffic into that SOCKS5 inbound. |
| `msys-2.0.dll` | `Platforms/Windows/bin/Native/` | `Resources/Native/` | Runtime the bridge is built against. |
| `wintun.dll` | `Platforms/Windows/bin/Native/` | `Resources/Native/` | TUN adapter driver (signed by WireGuard). |

`xray.dll` comes from `libxray.zip` at the solution root → `windows/x64/xray.dll`. It must be
the **x64** build: the app runs as x64 and a 32-bit DLL will exist but fail to load.

**The three bridge files must stay in one directory.** hev loads wintun with
`LOAD_LIBRARY_SEARCH_APPLICATION_DIR`, which means the directory of *hev's own exe* — not the
app's. Splitting them produces "адаптер не появился" with no further explanation.

## Optional

| File | Put in | Provides |
|---|---|---|
| `WinDivert.dll` | `Platforms/Windows/bin/Native/` | Per-process split tunneling |
| `WinDivert64.sys` | `Platforms/Windows/bin/Native/` | WinDivert kernel driver (pairs with the DLL) |

Dropping both in flips `WindowsSplitTunnelingService.IsSupported` to true and reveals the
Split tunneling row in Settings. Without them the row stays hidden, because `ApplyAsync`
silently does nothing and a screen of switches that change nothing is worse than no screen.

`.h` and `.lib` files in `Native/` are for rebuilding and are deliberately **not** copied to
the output — the csproj globs only `*.exe`, `*.dll` and `*.sys`.

## How the Windows tunnel differs from Android

Same binary lineage, two different hosting models:

| | Android | Windows |
|---|---|---|
| hev runs | in-process, via `[DllImport]` | as a **child process** |
| TUN device | fd handed over from `VpnService` | hev creates a wintun adapter itself (`tun_fd = -1`) |
| Loop prevention | app's own UID excluded from the TUN | `/32` host route to the node via the physical gateway |
| Traffic counters | `hev_socks5_tunnel_stats` | adapter counters (`GetIPStatistics`) |
| Privileges | user grants VPN consent | process must be **elevated** |

The child-process split is forced. hev's Windows port is guarded by `#if defined(__MSYS__)`
— a Cygwin build linked against `msys-2.0.dll`. It loads fine inside a .NET process and then
dies with an access violation on the first real call, because the Cygwin runtime is not
initialised for a CLR-created thread. Out of process it behaves perfectly.

## Rebuilding the bridge

Upstream: <https://github.com/heiher/hev-socks5-tunnel> (built from `2.17.0`, `f6ab377`).

```
winget install MSYS2.MSYS2
C:\msys64\usr\bin\bash -lc "pacman -S --needed gcc make git"
# then, inside the MSYS (not MINGW64) shell:
export MSYS=winsymlinks:native
git clone --recursive https://github.com/heiher/hev-socks5-tunnel && cd hev-socks5-tunnel
make -j$(nproc)          # → bin/hev-socks5-tunnel.exe
```

`MSYS=winsymlinks:native` is not optional: the repo keeps its public headers as symlinks, and
a checkout without it leaves them as one-line text files, which fails the build with
`unknown type name 'HevRBTree'`.

`wintun.dll` is vendored in the clone at `third-part/wintun/bin/`. `msys-2.0.dll` comes from
<https://github.com/heiher/msys2/releases>, matching what upstream ships in its own release.

## Testing on a machine with a system-wide proxy client

A redirector such as **Proxifier** hooks outbound TCP at the WFP layer, *before* the route
table is consulted. With one running, TCP never reaches the Horus adapter no matter how the
routes look — the tunnel appears up and carries only UDP and ICMP. Exclude `Horus.exe` and
the destinations under test, or stop the redirector, before concluding anything about the
tunnel.
