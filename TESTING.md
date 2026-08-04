# Horus — closed test

Two documents in one: the checklist to send testers (§1, in Russian, copy-paste it), and
the notes you need on your side (§2 onward).

---

## 1. Инструкция для тестировщика

> Скопируйте этот раздел и отправьте вместе со ссылкой на APK.

Спасибо, что помогаете тестировать Horus. Займёт минут 10.
Если что-то пошло не так на любом шаге — **сразу переходите к шагу 12**, не продолжайте.

| № | Что сделать | Что должно получиться |
|---|---|---|
| 1 | Если Horus уже стоял — удалите его | — |
| 2 | Откройте ссылку, скачайте и установите APK. Разрешите браузеру установку из неизвестных источников | Может появиться предупреждение Play Protect: **«Подробнее» → «Всё равно установить»**. Это нормально для приложения не из магазина |
| 3 | Откройте приложение → Настройки → **Версия** | Что-то вроде `0.9.0 (1)` — пришлите мне это число |
| 4 | Войдите под логином, который я прислал | Откроется главный экран |
| 5 | Настройки → **Подписка** | Дата, а не «не активна» |
| 6 | Настройки → **Ядро VPN** | Строка с версией. Если написано «not found» — сообщите мне сразу |
| 7 | Главный экран → **ПОДКЛЮЧИТЬ**, разрешите запрос Android на VPN | Статус меняется на **ЗАЩИЩЕНО** |
| 8 | Опустите шторку уведомлений | Значок **ключа** в статус-баре и уведомление «Horus VPN» |
| 9 | **Откройте Chrome** (не приложение!) и зайдите на `<ваша страница проверки>` | Зелёная надпись «Защищено» и страна **не ваша** |
| 10 | В Chrome откройте VK, YouTube, Google. Включите видео на минуту | Всё грузится. Заметили тормоза — запомните время |
| 11 | Заблокируйте телефон на 10 минут, разблокируйте | Всё ещё ЗАЩИЩЕНО, сайты открываются |
| 12 | Нажмите **ОТКЛЮЧИТЬ** | Значок ключа пропал, страница проверки показывает вашу страну |
| 13 | **Если что-то не сработало:** Настройки → **Собрать логи** → отправьте архив мне в Telegram | — |
| 14 | Напишите: что делали, примерно во сколько, и модель телефона | — |

**Что пока не работает — не сообщайте об этом:**
- переключение Wi-Fi ↔ мобильный интернет требует ручного переподключения;
- переключатели «Kill Switch», «Автоподключение», «Автозапуск» пока ничего не делают;
- оплата не подключена — доступ выдаю вручную;
- пинг у серверов не показывается;
- выбор конкретного сервера пока не влияет на подключение — сервер выбирает сервер API.

**Если приложение само закрывается** при подключении — это важно, обязательно сообщите.

---

## 2. Why step 9 says "in Chrome"

The app's own UID is deliberately excluded from the tunnel (`HorusVpnTunnelService.ApplySplitTunneling`),
because xray-core runs in-process and would otherwise route its own traffic back into the
TUN. So the app's API calls — including `GET /whoami` — always egress on the real address,
connected or not. **Checking the IP from inside the app proves nothing.** Any verification
has to come from another app or through the SOCKS5 proxy.

## 3. Before you hand out a build

```
# 1. Turn off the debuggable-release override
#    Set DebuggableRelease to false in Horus/Horus.local.props, or delete the file.

# 2. Set up signing once (see Horus/Horus.signing.local.props.example)
$env:HORUS_KS_PASS = "…"; $env:HORUS_KEY_PASS = "…"

# 3. Build. This FAILS if unsigned, debuggable, not Release, or .aab.
dotnet publish Horus/Horus.csproj -f net10.0-android -c Release `
  -p:HorusDistribution=true -p:ApplicationVersion=2 -p:ApplicationDisplayVersion=0.9.2

# 4. Verify the artifact before uploading
apksigner verify --print-certs <apk>          # SHA-256 must match your key, every time
aapt dump badging <apk>                       # package, versionCode, sdkVersion
unzip -l <apk> | grep -E "libxray|libhev"     # both must be present
aapt dump xmltree <apk> AndroidManifest.xml | grep -i debuggable   # must print nothing

# 5. Publish the APK's SHA-256 next to the download link, and git tag the commit.
```

Bump `ApplicationVersion` for **every** build you hand out — Settings shows it, and it is
the only way to know which build a report came from.

## 4. Reading a diagnostics archive

`Собрать логи` produces `horus_<timestamp>.zip` containing:

| Entry | What it tells you |
|---|---|
| `report.json` → `context` | app version, ABI, Android version, model, connectivity, core version, **preflight IPs**, which protocol was tried |
| `report.json` → `errors` | recorded exceptions (sanitized) |
| `session.log` | rolling connect timeline from `VpnManager` |
| `xray.log` | the core's own error log — the proxy half |
| `hev.log` | hev-socks5-tunnel — the TUN half |

**Read the preflight pair first.** It splits the problem before you read anything else:

| direct | proxied | Meaning |
|---|---|---|
| real IP | server IP | Proxy chain is fine. The fault is in the TUN half → read `hev.log` |
| real IP | same real IP | xray egressed via `freedom`, or the outbound silently fell back |
| real IP | `—` | Outbound never carried traffic: wrong schema, bad credential, node down → read `xray.log` |
| `—` | `—` | The device had no connectivity at all |

Preflight needs `GET /ip` on the API (plain-text caller IP, no auth). Until that exists both
values are `—` and this triage is unavailable — it is the single highest-value backend
change for the test phase.

## 5. Granting a tester access

Payment is deliberately not implemented; the sheet now says so instead of faking success.
Grant access directly:

```
PUT /admin/users/{username}/subscription
{ "expires_at": "2026-11-04T00:00:00Z" }
```

The client re-reads `/whoami` before it opens the payment sheet, so a grant is picked up on
the next connect attempt without a restart.

## 6. Stage 0 verification (yours, before any tester sees it)

Run on one real arm64 device:

1. Settings → Ядро VPN shows a real version string.
2. Connect → the archive's preflight pair shows **direct = your ISP IP, proxied = foreign**.
3. Status bar shows the key icon + notification.
4. In **Chrome**, `ipleak.net`: IPv4 = server; IPv6 empty or server, **never** your ISP; DNS at the exit.
5. `adb shell run-as com.horus.vpn cat cache/logs/hev.log` and `cache/logs/xray.log` both have content.
6. Speed counters move while a video plays and stop when it stops.
7. A 60-second YouTube video plays without stalling.
8. Disconnect → key icon gone, Chrome shows your real IP.
9. **Reconnect twice without restarting the app** — proves `XrayStop` runs before `XrayStart`.

If (2) passes and (4) fails, the fault is entirely in the TUN half. If (2) fails, it is
xray/server. That split is the point of the preflight.
