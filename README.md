# NeoSwitch

Foreground-app–driven profile switcher for QwertyKeys keyboards on
Windows, built on the HID protocol reverse-engineered from
`he.qwertykeys.com` (see [`SPEC.md`](./SPEC.md) and [`PLAN.md`](./PLAN.md)).

## Status

| Milestone | Deliverable | Status |
|---|---|---|
| M0 | UI mockup + plan | ✅ done — `mockup/neo-switch.html`, `PLAN.md` |
| **M1** | **`KeyboardClient` + CLI prototype** | **🚧 in progress** |
| M2 | Foreground watcher + rule engine | |
| M3 | WinForms tray UI | |
| M4 | Hot-plug + start-with-Windows | |
| M5 | Packaged installer | |

## Repo layout

```
NeoSwitch.sln
src/
  NeoSwitch.Core/          class library — HID protocol + profile API
    Protocol.cs            opcode constants (mirrors SPEC §3.2)
    KeyboardClient.cs      HidSharp wrapper; Switch/Get/LoadAllProfiles
    ProfileInfo.cs         data types
  NeoSwitch.Cli/           console prototype
    Program.cs             list | info | get | switch <idx>
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

# Disambiguate by USB ids when multiple devices match
dotnet run --project src/NeoSwitch.Cli -- --vid 0x1ea7 --pid 0x0907 switch 0
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
