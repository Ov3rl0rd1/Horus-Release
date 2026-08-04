# Native binaries

## `libxray.so` — the VPN core (required)

The custom xray-core build carrying the **VLESS**, **Hysteria2** and **olcRTC**
outbounds. Drop one per ABI:

```
Platforms/Android/lib/arm64-v8a/libxray.so
Platforms/Android/lib/armeabi-v7a/libxray.so
Platforms/Android/lib/x86_64/libxray.so
```

It must be named `lib*.so`. Android only extracts files matching that pattern from
the APK's `lib/` directory into `ApplicationInfo.NativeLibraryDir`, and only files
there are executable — a plain `xray.so` is packaged but never unpacked.

The app invokes it as `libxray.so run -c <config.json>` (see
`Protocols/XrayProtocol.cs`). The csproj entries are `Condition="Exists(...)"`, so
the project still builds without them; connecting then fails with a
"binary not found" error from `AndroidProcessRunner`.

Windows uses the same core as `Platforms/Windows/bin/xray.exe`, copied next to the
app on build.

## `libhev_socks.so` — the TUN bridge (committed)

[hev-socks5-tunnel](https://github.com/heiher/hev-socks5-tunnel). Pumps packets
between the Android TUN fd and xray's SOCKS5 inbound on `127.0.0.1:1080`. That port
is hardcoded in `HevSocksTunnel.HEV_SOCKS5_TUNNEL_CONFIG` and must match
`XrayConfig.SocksPort`.
