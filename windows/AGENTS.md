# Windows app: notes for agents

Port of the Mac menu bar app to a Windows tray app. Same idea, same data: recognise a router you've
joined before, else find the nearest stores by location and try their phone-number passwords.
User-facing text is Taiwan Mandarin, like the Mac app. Code comments are English.

## Layout

| Path | What |
| --- | --- |
| `src/Program.cs` | Entry point, single instance, install / update / uninstall |
| `src/TrayApp.cs` | Tray icon, native menu, timers, notifications |
| `src/Joiner.cs` | Finding the store, trying passwords, joining (port of `app/Sources/Joiner.swift`) |
| `src/Wlan.cs` | Windows Native Wifi API (`wlanapi.dll`) |
| `src/Stores.cs` | Store list, weekly refresh, location (`System.Device` `GeoCoordinateWatcher`) |
| `src/Tracker.cs` | Minutes per day and store (`usage.json`), stats page |
| `src/Dialogs.cs` | 輸入密碼… and 搜尋門市… windows |
| `src/Common.cs` | Paths, log, JSON, `config.json` |
| `src/app.manifest` | asInvoker, DPI aware, common controls v6 |
| `stats.html` | Stats page template (port of `StatsView.swift`) |
| `build.ps1`, `test.ps1` | Build and offline tests |
| `tests/Tests.cs` | Offline checks |

## Build and test

```
powershell -ExecutionPolicy Bypass -File windows\build.ps1 [-Version 1.2.3]   # → windows\dist\LouisaWiFi.exe
powershell -ExecutionPolicy Bypass -File windows\test.ps1
```

- Uses the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`,
  .NET Framework 4.8). Nothing to install, and `windows-latest` on GitHub Actions has it.
- **That compiler only understands C# 5.** No `$"…"` interpolation, `?.`, `nameof`, `=>` members,
  auto-property initializers, `out var`, tuples or local functions. `build.ps1` fails loudly if you slip.
- Run it with Windows PowerShell 5.1 (`powershell`, not `pwsh`). Any `.ps1` containing non-ASCII
  characters must be saved as UTF-8 **with BOM**, or 5.1 misreads it.
- Version: `-Version`, else the latest `v*` tag. It goes into a generated `build\Version.cs`.
- CI: `.github/workflows/ci.yml` builds and runs `test.ps1`. `release.yml` builds with the tag's version
  after the Mac job creates the release, and uploads `LouisaWiFi-Windows.exe` to it. So the weekly
  store-data patch releases rebuild the Windows app too.

`test.ps1` covers what doesn't need hardware: store data, address parsing, rejecting a broken
`stores.json`, distances, reading the JSON files, and that every menu list fits on screen.
Wi-Fi, location and the tray need a real Windows laptop:

- Run without installing, with throwaway data:
  `$env:LOUISAWIFI_DATA = "$env:TEMP\lw"; windows\dist\LouisaWiFi.exe --portable`
  (quit any installed copy first; they share a single-instance mutex).
- Fake a store with a phone hotspot named `LouisaCoffee` whose password is the nearest store's first
  candidate (the 用密碼連線 submenu shows it). Check: it joins on its own within seconds; after turning
  the hotspot off it stops (log: `LouisaCoffee no longer nearby` or `使用者已連上 …，停止嘗試`) and
  never kicks you off a network you pick yourself.
- Log: `%APPDATA%\LouisaWiFi\LouisaWiFi.log`. Windows' own view: Event Viewer →
  Microsoft-Windows-WLAN-AutoConfig/Operational (events 8000-8003).

## Files and paths it depends on

- **`data/stores.json`**: built into the exe, and re-downloaded weekly from
  `https://raw.githubusercontent.com/madeyexz/louisa-wifi/main/data/stores.json` (`App.StoresUrl` in
  `Common.cs`) into `%APPDATA%\LouisaWiFi\stores.json`. **Moving the file or renaming its fields breaks
  the refresh for installed copies.** A download is only used if it parses to at least 100 stores that
  each have a `name` and non-empty `passwords`. Otherwise the current list is kept. Fields used:
  `name`, `address`, `phone`, `lat`, `lng` (0 = unknown), `passwords`. City and district are read from
  `address`: the first word (臺 → 台), and the second word cut at the first 區 (else 鄉/鎮/市).
- **`app/AppIcon.png`**: exe icon (16-256 px `.ico`) and tray icon (greyed in code when not connected).
- **`windows/stats.html`**: the app replaces the literal `/*__USAGE__*/{}` with `usage.json`.
- **`%APPDATA%\LouisaWiFi\`** (`LOUISAWIFI_DATA` overrides it):
  - `learned.json`: BSSID → `{store, password}`. Same shape as the Mac app.
  - `usage.json`: `"yyyy-MM-dd"` → store → seconds. Same as the Mac app.
  - `config.json`: `autoConnect`, `onlyKnown`, `hideIcon`, `welcomed`, `locationAsked`.
  - `LouisaWiFi.log`: rotates to `.old.log` at 1 MB. It never logs coordinates, because people paste logs into issues.
- **Install** (per user, no admin): `%LOCALAPPDATA%\Programs\LouisaWiFi\LouisaWiFi.exe`. It also creates:
  - the Start menu shortcut `Louisa Wi-Fi.lnk`
  - the startup entry `HKCU\…\Run\LouisaWiFi`
  - the Settings → Apps entry `HKCU\…\Uninstall\LouisaWiFi`
  Running a downloaded exe offers to install or update. `--uninstall` removes it all, and asks before deleting the data folder.
- **Single instance**: mutex `Local\LouisaWiFi`. Event `Local\LouisaWiFi.Show` (a second launch shows a
  hidden icon) and `Local\LouisaWiFi.Quit` (the installer stops a running copy before replacing it).
- **WLAN profile `LouisaCoffee`**: all-user (falls back to per-user), `connectionMode` **manual**. The
  app decides when to join, so Windows never joins with the previous store's password or from a stale
  list. Profiles left as automatic are switched to manual at startup.

## Mac ↔ Windows

| Mac | Windows |
| --- | --- |
| `Joiner.swift` serial queue + `epoch` interrupt | `Joiner.cs` worker thread + `epoch` |
| `CWWiFiClient` / CoreWLAN | `Wlan.cs` (`WlanGetNetworkBssList`, `WlanSetProfile`, `WlanConnect`, notifications) |
| `Locator.swift` | `Locator` in `Stores.cs` (keeps the last fix for 2 h) |
| `Tracker.swift`, `StatsView.swift` | `Tracker.cs`, `stats.html` (opens in the browser) |
| `MenuController.swift` | `TrayApp.BuildMenu` (rebuilt on every open, like `menuNeedsUpdate`) |
| `Updater.swift` | none yet: re-download from Releases; the installer updates in place |
| link change + 15 s timer + location updates | WLAN notifications + 15 s timer + moved > 100 m |

Intentional differences:
- Before trying passwords automatically, it does a fresh scan. Windows keeps networks in its list for about a minute after they disappear.
- Between passwords it stops if the user joined another network themselves (connection mode not "auto"),
  or, when connecting automatically, if the computer got online elsewhere.
  Failures, and the user taking over, snooze **every** LouisaCoffee BSSID in range for 10 minutes.
- When on a Wi-Fi with no internet for 30 s and Louisa is nearby, it switches. Mac never leaves a network.
- 搜尋門市… (search by name, address or phone).
- Menu: native Win32 menu (`ContextMenu`, Windows 11 look). It follows dark mode through undocumented `uxtheme.dll` #135/#136.
  No item tooltips. Distances sit in the shortcut column (`\t`).
  Windows menus can't scroll with the mouse wheel, so a city longer than the screen fits (`MenuCapacity`)
  gets a district level: 台北市 → 大安區 → stores.
- Weekly `stores.json` download (the Mac app gets new data through updates).

## Gotchas

- Native Wifi API, not `netsh`: `netsh` output is localised. Struct offsets are hard-coded in `Wlan.cs`
  (`WLAN_BSS_ENTRY` is 360 bytes, `WLAN_AVAILABLE_NETWORK` 628, connection attributes as commented).
- Windows 11 24H2+: `WlanGetNetworkBssList` returns `ERROR_ACCESS_DENIED` without location access.
  The app shows `Joiner.LocationBlockedText` instead of silently finding nothing.
- `[TargetFramework(".NETFramework,Version=v4.8")]` in `Common.cs` is required. Without it, csc-built exes
  use old TLS defaults and the GitHub download fails.
- `GeoCoordinateWatcher` sometimes sticks at NoData after start. `Locator.Poll` restarts it, backing off from 30 s up to 10 min.
- Left-click opens the menu through `NotifyIcon.ShowContextMenu` (private, via reflection). Right-click works without it.
- The exe isn't code-signed, so SmartScreen warns on first run. The README tells users how to proceed.
