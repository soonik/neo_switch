# NeoSwitch

Foreground-app–driven profile switcher for QwertyKeys keyboards on
Windows, built on the HID protocol reverse-engineered from
`he.qwertykeys.com` (see [`SPEC.md`](./SPEC.md) and [`PLAN.md`](./PLAN.md)).

## Status

| Milestone | Deliverable | Status |
|---|---|---|
| M0 | UI mockup + plan | ✅ done — `mockup/neo-switch.html`, `PLAN.md` |
| M1 | `KeyboardClient` + CLI prototype | ✅ done — `neoswitch list/info/get/switch/probe/raw` |
| M2 | Foreground watcher + rule engine | ✅ done — `neoswitch watch` + DebouncedSwitcher + modifier gate |
| **M3** | **WinForms tray UI** | **🚧 in progress** — `NeoSwitch.App` |
| M4 | Hot-plug + start-with-Windows | |
| M5 | Packaged installer | |

## Repo layout

```
NeoSwitch.sln
src/
  NeoSwitch.Core/          class library — HID protocol + foreground hook
    Protocol.cs            opcode constants (mirrors SPEC §3.2)
    KeyboardClient.cs      HidSharp wrapper; Switch/Get/LoadAllProfiles
    ProfileInfo.cs         data types
    ForegroundWatcher.cs   SetWinEventHook-backed foreground tracker (Win)
    RuleEngine.cs          watchedExes → target profile decision
    DebouncedSwitcher.cs   coalesces HID writes; modifier-gate before commit
    ModifierKeys.cs        GetAsyncKeyState wrapper (Alt/Ctrl/Shift/Win)
  NeoSwitch.Cli/           console prototype
    Program.cs             list | info | get | switch | probe | raw | watch
  NeoSwitch.App/           WinForms tray app (M3)
    Program.cs             STAThread entry; runs TrayContext
    TrayContext.cs         NotifyIcon host + application lifetime
    MainForm.cs            main window (watched apps + profile mapping)
    RuntimeController.cs   pipeline wiring exposed as observable state
    SettingsStore.cs       %APPDATA%\NeoSwitch\config.json
    Settings.cs            persisted user configuration
mockup/neo-switch.html     interactive UI mockup for the tray app
switch-profile.html        one-file WebHID reproducer (browser-only)
SPEC.md                    protocol spec
PLAN.md                    architecture + milestones
```

## Build

Requires **.NET 8 SDK** on Windows.

```powershell
dotnet restore
dotnet build -c Release
```

## CLI usage (M1)

```powershell
# List detected QwertyKeys (raw-HID) devices
dotnet run --project src/NeoSwitch.Cli -- list

# Show connected keyboard + all profiles
dotnet run --project src/NeoSwitch.Cli -- info

# Print the current profile index
dotnet run --project src/NeoSwitch.Cli -- get

# Switch to profile 1
dotnet run --project src/NeoSwitch.Cli -- switch 1

# Disambiguate when multiple raw-HID devices match
dotnet run --project src/NeoSwitch.Cli -- list                  # shows [0], [1], ...
dotnet run --project src/NeoSwitch.Cli -- --device 0 switch 1   # pick by index
dotnet run --project src/NeoSwitch.Cli -- --vid 0x1ea7 --pid 0x0907 switch 0

# Diagnostics (added after the M1 timeout incident)
dotnet run --project src/NeoSwitch.Cli -- list --all            # every HID device on the system
dotnet run --project src/NeoSwitch.Cli -- probe                 # VIA 0x01 then D0 B0
dotnet run --project src/NeoSwitch.Cli -- -v raw D0 B6          # verbose hex TX/RX for one command
```

## CLI usage (M2 — foreground-driven switching)

`watch` installs a `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` hook and flips
the active profile whenever the foreground app enters/leaves the watched set.
Redundant switches (same target two times in a row) are skipped, and the
actual HID write is debounced by `--switch-delay <ms>` (default **200 ms**)
so that modifier keys held during an Alt+Tab gesture have time to release
before the firmware re-initialises its key matrix — without this, key-up
events for Alt/Tab were being lost mid-switch and stuck in the OS.

```powershell
# switch to profile 1 when any of these is foreground, 0 otherwise
dotnet run --project src/NeoSwitch.Cli -- watch `
  --app valorant.exe --app cs2.exe `
  --fg 1 --bg 0

# observe the decisions without touching the keyboard
dotnet run --project src/NeoSwitch.Cli -- watch --app notepad.exe --dry-run

# tune the debounce if 200 ms isn't enough cushion for fast alt-tab chains
dotnet run --project src/NeoSwitch.Cli -- watch --app cs2.exe --switch-delay 300
```

Output (one `SWITCH` line per foreground transition + one `SENT` line per
debounced HID write):

```
connected: QwertyKeys / QK75 Max  (profiles=4, current=0)
watching 2 app(s): valorant.exe, cs2.exe
fg profile = 1   bg profile = 0   switch delay = 200ms

[14:32:07.412] SWITCH * fg=valorant.exe                    -> profile 1
[14:32:07.620]   SENT profile 1
[14:34:15.880] SWITCH   fg=explorer.exe                    -> profile 0
[14:34:16.087]   SENT profile 0
[14:34:22.140]          fg=chrome.exe                      -> profile 0
[14:35:01.330] SWITCH * fg=cs2.exe                         -> profile 1
[14:35:01.538]   SENT profile 1
```

`SWITCH` = decision changed (HID write scheduled). `SENT` = HID write
actually committed `--switch-delay` ms later. `*` = foreground exe is in
the watched set. If the user alt-tabs through several apps faster than
the debounce window, only the final target is committed. Ctrl+C to stop.

## Tray app (M3)

Same pipeline as `neoswitch watch`, wrapped in a WinForms tray app.

```powershell
dotnet run --project src/NeoSwitch.App
```

On launch, `NeoSwitch.exe` connects the first raw-HID keyboard it finds
(configurable via `VendorId`/`ProductId` in the config file), installs
the foreground hook, and minimises to the system tray. Configuration is
persisted to `%APPDATA%\NeoSwitch\config.json`.

- **Double-click** the tray icon → open the main window.
- **Main window**: left pane lists watched apps (Add from file…, Add
  running process…, Remove); right pane has `Foreground profile` /
  `Background profile` steppers, `Switch delay` / `Gate timeout`, and a
  `Pause switching` checkbox.
- **Close (×)** hides the window to the tray; the pipeline keeps running.
- **Tray right-click** → Open / Pause switching / Reconnect keyboard /
  Exit (the only way to fully quit).

Published single-file exe once M3 is stable:

```powershell
dotnet publish src/NeoSwitch.App -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish/
publish/NeoSwitch.exe
```

Published single-file exe (once built):

```powershell
dotnet publish src/NeoSwitch.Cli -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true -p:PublishTrimmed=false `
  -o publish/
publish/neoswitch.exe info
```

## How M1 works

- Enumerates HID devices via `HidSharp` and filters by the QMK
  raw-HID fingerprint (usage page `0xFF60`, usage `0x61`).
- Opens the first match and issues **one** 32-byte output report on
  report ID `0`:

  ```
  D0 B1 <idx>  ← switch to profile <idx>
  D0 B0        ← query current profile index
  D0 B6        ← query profile count
  D0 B2 <i>    ← query profile <i> name (UTF-8, 28 bytes)
  D0 B4 <i>    ← query profile <i> LED color (r,g,b)
  ```

- Read replies are matched by the echoed `[base, subOp]` header; up
  to 8 unsolicited reports are skipped per round-trip (mirrors the
  `msgQueue` discipline in the web bundle).

## Troubleshooting

- **"no matching keyboard found"** — make sure the keyboard is plugged
  in and that the firmware exposes the raw-HID interface (`neoswitch
  list` will show it). Some firmware variants put raw-HID on a
  second interface; HidSharp will show it as a separate entry.
- **"Another app may hold the handle"** — the official QwertyKeys web
  configurator (`he.qwertykeys.com`) holds the interface exclusively
  while connected. Close the browser tab and retry.
- **Multiple QwertyKeys connected** — disambiguate with
  `--vid 0xNNNN --pid 0xNNNN` (IDs shown by `neoswitch list`).
