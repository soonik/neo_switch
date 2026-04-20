# NeoSwitch — Windows tray app plan

Foreground-app–driven keyboard profile switcher for QwertyKeys
boards, built on the HID protocol reverse-engineered in
[SPEC.md](./SPEC.md).

## 1. Product requirements (from the ask)

1. Maintain a user-editable **list of executables** (`*.exe`).
2. When any listed exe becomes the foreground window → switch the
   keyboard to **profile A** ("foreground profile").
3. When the foreground window is not in the list → switch to
   **profile B** ("background profile").
4. Lives in the system tray; **double-click the tray icon** brings the
   main window to the foreground.
5. Closing the main window minimises to tray; **Exit** via the tray
   menu is the only way to fully quit.

## 2. Tech choice

**C# .NET 8 + WinForms**, single-file self-contained publish.

Why:

- First-class `NotifyIcon` for tray + context menus.
- `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` via P/Invoke gives
  **event-driven** foreground detection with no polling.
- `HidSharp` (MIT) handles raw-HID read/write on Windows and maps 1-1
  to the 32-byte report-ID-0 protocol used by the site.
- `System.Text.Json` for the settings file.
- Single `.exe` output, no runtime install, no admin rights needed
  (the `0xFF60 / 0x61` usage page is user-accessible HID).

Rejected alternatives:

- **Electron / Tauri**: extra 100+ MB for a tray utility, and
  Electron's WebHID support on Windows is cumbersome for a background
  service.
- **Python + pystray**: works but tray polish, single-exe packaging,
  and foreground-event hooking are all rougher.
- **Native C++ / Win32**: leanest, but no benefit here over C# for
  this scope.

## 3. Architecture

```
 ┌───────────────────────────┐      ┌───────────────────────────┐
 │  ForegroundWatcher        │      │  KeyboardClient           │
 │  SetWinEventHook          │◄────►│  HidSharp                 │
 │  → ForegroundAppChanged   │      │  SwitchProfile(byte idx)  │
 └──────────────┬────────────┘      └──────────────┬────────────┘
                │                                   │
                ▼                                   ▲
          ┌────────────────────────────────────────────────────┐
          │  RuleEngine                                        │
          │  - List<string> watchedExes (lower-cased basenames)│
          │  - byte foregroundProfileIdx, backgroundProfileIdx │
          │  - OnForegroundChanged(exe) → target profile       │
          │  - Debounce + skip if == lastSentIdx               │
          └────────────────────────────────────────────────────┘
                                │
                                ▼
          ┌────────────────────────────────────────────────────┐
          │  Settings (JSON at %APPDATA%\NeoSwitch\config.json)│
          │  MainForm  +  TrayIcon  +  StartupRegistrar        │
          └────────────────────────────────────────────────────┘
```

### 3.1. KeyboardClient

Mirrors the `HIDAPI` class from the site bundle. Core operations:

```
OpenFirstMatching()  → enumerate HID devices, pick one whose
                       Collection exposes usagePage=0xFF60, usage=0x61.

SwitchProfile(idx)   → Write( [0x00, 0xD0, 0xB1, idx, 0x00…] )
                       (1 byte report ID + 32 data bytes)

GetCurrentIdx()      → Write [0xD0, 0xB0]; await Read; return data[2]
GetProfileCount()    → Write [0xD0, 0xB6]; await Read; return data[2]
```

- Writes use `HidStream.Write`. Reads use `HidStream.Read` with a 1 s
  timeout; unsolicited replies are matched by first-3-bytes prefix,
  matching the bundle's `msgQueue` discipline.
- Retries writes up to 3× on `IOException` to tolerate transient
  device-busy errors (same retry policy as the web client).
- Device disconnect / reconnect is handled by listening to
  `DeviceList.Local.Changed` and reopening when the VID/PID returns.

### 3.2. ForegroundWatcher

```cs
SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _callback,
                0, 0, WINEVENT_OUTOFCONTEXT);
```

Callback receives `hwnd` → `GetWindowThreadProcessId` → `OpenProcess` →
`QueryFullProcessImageName` → basename. A single event gives both the
"app became foreground" and (implicitly) "previous app went
background" signals — one event per transition is enough for the
rule engine.

### 3.3. RuleEngine

Pure function of `(foregroundExe, watchedSet, fgIdx, bgIdx)` →
`targetIdx`. The engine holds `lastSentIdx` and only calls
`KeyboardClient.SwitchProfile` when the target differs, which matches
the bundle's `if (getSwitching(state)) return` + `setProfileIdx`
pattern and avoids hammering the HID pipe when a user alt-tabs
between two non-watched apps.

### 3.4. Settings

`%APPDATA%\NeoSwitch\config.json`:

```json
{
  "watchedApps": ["valorant.exe", "cs2.exe"],
  "foregroundProfile": 1,
  "backgroundProfile": 0,
  "preferredDevice": { "vendorId": 12345, "productId": 6789 },
  "startWithWindows": true,
  "pauseSwitching": false
}
```

`StartWithWindows` is toggled by writing a value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. No admin needed.

### 3.5. UI surface

- **Tray icon** (`NotifyIcon`):
  - Double-click → show + activate main window.
  - Left/right click → context menu.
  - Context menu: `Open`, `Pause switching` (toggle), separator,
    `Reconnect keyboard`, `Exit`.
  - Tooltip shows `Connected: <product>  |  Active: profile N`.
- **Main window** (`Form1`):
  - Header strip: connected keyboard + current profile + Reconnect
    button.
  - Left pane: watched-apps list (`ListView` with columns "App",
    "Path"), buttons `Add from file…`, `Add from running…`, `Remove`.
  - Right pane: two `NumericUpDown` profile selectors ("Foreground
    profile" / "Background profile"), previewing profile names fetched
    via `0xD0 0xB2` when the device is connected.
  - Footer: `Start with Windows`, `Pause switching`, status line with
    last detected foreground exe and last profile sent.
  - Closing the window via ✕ hides to tray (`FormClosing` →
    `e.Cancel = true; Hide();`). Genuine quit goes through tray menu.

## 4. Milestones

| # | Deliverable | Exit criteria |
|---|---|---|
| M0 | UI mockup (`mockup/neo-switch.html`) — **this PR** | Reviewer signs off on layout & flow |
| M1 | `KeyboardClient` + CLI prototype | `neo-switch-cli switch 0/1` changes profile on real hardware |
| M2 | `ForegroundWatcher` + `RuleEngine` as a headless service | Tailing log shows correct target-profile decisions when alt-tabbing |
| M3 | Tray + WinForms UI wired to M1+M2 | Feature-complete for the 4 requirements, manual QA pass |
| M4 | Polish: device hot-plug, settings schema v1, start-with-Windows | Survives a `net stop/start` of USB, restart reconnects |
| M5 | Single-file `publish` + signed MSIX (optional) | Double-click install on a clean Win11 box |

## 5. Risks & mitigations

- **Multiple QwertyKeys devices connected.** Expose a "Keyboard" drop-
  down in the header so the user picks; persist `preferredDevice`.
- **App name collisions** (two `chrome.exe` instances, one PWA
  profile). Match on basename only for v1; allow full-path rules
  later.
- **Rapid alt-tab thrashing.** Debounce foreground events by 150 ms
  and skip sends that would repeat `lastSentIdx`.
- **HID handle exclusivity.** If the stock QwertyKeys configurator is
  open, WebHID holds the interface. Detect the
  `SharingViolation`/`AccessDenied`, surface it in the status line,
  and offer Reconnect.
- **Protocol drift.** Future QwertyKeys firmware could renumber sub-
  opcodes. Keep the opcode table in one C# file (`Protocol.cs`)
  directly mirroring SPEC.md §3.2 so a firmware bump = one-file patch.

## 6. Out of scope for v1

- Per-app profile rules (beyond the two-profile foreground/background
  split) — add a `Map<exe, profileIdx>` in v1.1.
- Writing advanced-key (AT / SOCD / RT) configs — reserved for v2.
- macOS / Linux builds — HID + foreground-app APIs are platform-
  specific; parking for now.

## 7. Draft UI

Static mockup (open in a browser):

    mockup/neo-switch.html

It renders the main window and tray menu side-by-side so layout +
copy can be reviewed before any C# is written.
