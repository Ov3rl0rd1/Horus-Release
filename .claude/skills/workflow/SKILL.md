---
name: workflow
description: How this project is worked on — where analysis and test reports go, what language they are written in, which repos hold the native libraries, git and distribution conventions, and what must never be touched. Use at the start of any multi-step task on Horus, and before writing a report or committing anything.
---

# Working on Horus

## Reports and analysis

Substantial work ends in a `.md` file, not only in chat. Existing ones live in **`docs/`**,
which is in `.gitignore` (`/docs`) but perfectly readable — read them before re-deriving
anything:

| file | what it holds |
|---|---|
| `VPN-STABILITY-RESEARCH.md` | ~100 KB teardown of NekoBox and RethinkDNS — settings, manifests, the non-obvious decisions. The reference for "how do the good clients do it" |
| `HORUS-VPN-IMPROVEMENT-PLAN.md` | the phased plan derived from that research, with `🔧 [LIB]` markers on anything needing a library rebuild |
| `HORUS-BACKGROUND-FIX.md` | the screen-off/Doze round: root causes and fixes |
| `HORUS-DEVICE-TEST-*.md` | what was actually observed on hardware, per round |

Write new reports straight into `docs/`.

**Reports and all user-facing strings are in Russian. Code, identifiers, comments and commit
messages are in English.** Keep that split.

A report is worth more for what it says *did not* work than for what did. State what was not
implemented, what could not be verified and why, and mark anything speculative — the standing
instruction is to flag controversial points rather than smooth them over.

## The native libraries can be changed

Both are forks under the user's control, so "the library would have to change" is a design
option, not a blocker:

- **xray-core** — `C:\X-ray-custom\Xray-core-RTC`, carrying olcRTC support and a per-client
  bandwidth ceiling (10 MB/s). Built by GitHub Actions, not locally.
- **hev-socks5-tunnel** — upstream is unmodified; changes live as patches in
  `packaging/android/hev-patches/`, applied by `build-hev.ps1` at build time. Patches rather
  than a fork so that moving to newer upstream is a one-line change, and a patch that stops
  applying is a deliberate signal to re-read it.

When adding a native entry point, make the managed side degrade gracefully — catch
`EntryPointNotFoundException` and fall back — so the app keeps working against an unpatched
binary and simply gets faster once the patched one lands.

## Git

- Branch `dev`; PRs target `main`.
- **Do not commit or push unless asked.** The user commits their own work; a session's changes
  are often reviewed and reset.
- Never modify anything matched by `.gitignore` — that includes `docs/`, `*.local.props`, and
  all signing material.

## Distribution

Direct **APK**, never `.aab`:

```bash
dotnet publish Horus/Horus.csproj -f net10.0-android -c Release \
  -p:HorusDistribution=true -p:ApplicationVersion=<N> -p:ApplicationDisplayVersion=0.9.<N>
```

`HorusDistribution=true` makes the build fail loudly unless it is signed, Release and
non-debuggable. Bump `ApplicationVersion` for every build handed to a tester.

For testing on the device use a different application id instead — see the `device-test` skill.

## Working style the user has asked for

- Do the analysis before proposing changes, and say which findings depend on assumptions.
- Ask as many questions as needed when genuinely blocked; otherwise decide and proceed.
- Test-only code changes are fine — they get reset — but never touch gitignored files.
- The production app on the phone holds a real session and real data. Disabling it during a
  test is fine; destroying its data is not.
- When something turns out to be a misreading rather than a bug, say so plainly and move on.
