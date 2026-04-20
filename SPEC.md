# he.qwertykeys.com — reverse-engineered HID protocol

Reverse-engineered from the shipped JS bundle captured via
`he.qwertykeys.com.har` (entry `/assets/index-BLg4-D7h.js`, ~1.1 MB).
The site is a **VIA-derived** web configurator (`definition_hash` pattern,
`navigator.hid` transport, VIA opcodes 0x01–0x15) with a QwertyKeys-specific
extension namespace stacked on top (base opcodes `0xD0`, `0xD1`, `0xBF`).

All device I/O happens client-side in the browser over **WebHID**. No
server API is involved in profile switching or any other performance
setting.

---

## 1. Transport

### 1.1. Device filter

```js
navigator.hid.requestDevice({ filters: [{ usagePage: 0xFF60, usage: 0x61 }] })
```

`0xFF60 / 0x61` is the standard **QMK raw-HID** usage tuple, identical
to the fingerprint used by upstream VIA and QMK Toolbox. On first
connect the site calls `navigator.hid.getDevices()` and, if empty,
falls back to `requestDevice` to trigger the browser picker. Each
matched device is given a random UUID (`crypto.randomUUID()`) cached in
a module-level `cache$2` keyed by that id.

### 1.2. Wire format

Every command is a single **32-byte output report on report ID `0`**:

```js
async send(a) {
  const buf = new Uint8Array(32);     // always 32 bytes, zero-padded
  a.forEach((b, i) => buf[i] = b);
  await this._device._HIDDevice.sendReport(0, buf);
  // awaits the matching reply from an msgQueue keyed by header bytes
}
```

Replies echo the request header (opcode + sub-opcode + first arg
bytes) and return the payload afterwards. The client identifies replies
by prefix-matching against outstanding requests stored in
`msgQueue` / `waitQueue`.

Writes are retried up to **3 times** with zero delay via a `useRetry`
helper. Commands are serialised **per device** through a `sendQueue`
so one slow call cannot interleave with another on the same HID
handle.

Byte helpers used throughout:

- `shiftFrom16Bit(n)` → `[hi, lo]`
- `shiftTo16Bit([hi, lo])` → `n`

All multi-byte values are **big-endian**.

---

## 2. Base VIA opcodes used

Implemented on `class HIDAPI`. These match upstream VIA verbatim —
nothing has been renumbered.

| Opcode | Method                        | Upstream VIA name                          |
|-------:|-------------------------------|--------------------------------------------|
| `0x01` | `getProtocolVersion()`        | `id_get_protocol_version`                  |
| `0x02` | `getKeyboardValue()`          | `id_get_keyboard_value`                    |
| `0x03` | `setKeyboardValue()`          | `id_set_keyboard_value`                    |
| `0x05` | `setKey(row, col, …)`         | `id_dynamic_keymap_set_keycode`            |
| `0x07` | `setCustomMenuValue(…)`       | `id_custom_set_value`                      |
| `0x08` | `getCustomMenuValue(…)` / `getPerKeyRGBMatrix(…)` | `id_custom_get_value`        |
| `0x09` | `commitCustomMenu(ch)`        | `id_custom_save`                           |
| `0x0C` | `getMacroCount()`             | `id_dynamic_keymap_macro_get_count`        |
| `0x0D` | `getMacroBufferSize()`        | `id_dynamic_keymap_macro_get_buffer_size`  |
| `0x0E` | `getMacroBytes(offset, len)`  | `id_dynamic_keymap_macro_get_buffer`       |
| `0x0F` | `setMacroBytes(…)`            | `id_dynamic_keymap_macro_set_buffer`       |
| `0x10` | `resetMacros()`               | `id_dynamic_keymap_macro_reset`            |
| `0x11` | `getLayerCount()`             | `id_dynamic_keymap_get_layer_count`        |
| `0x12` | `getKeymapBuffer(…)`          | `id_dynamic_keymap_get_buffer`             |
| `0x13` | `writeRawMatrix(…)`           | `id_dynamic_keymap_set_buffer`             |
| `0x14` | `getEncoderValue(l, i, cw)`   | `id_dynamic_keymap_get_encoder`            |
| `0x15` | `setEncoderValue(l, i, cw, v)`| `id_dynamic_keymap_set_encoder`            |

### VIA opcodes **not** used by this client

Confirmed absent from the bundle — the client chooses alternative
strategies:

| Upstream VIA opcode | Alternative used here |
|----|----|
| `0x04` `dynamic_keymap_get_keycode` | bulk-reads via `0x12 get_buffer` |
| `0x06` `dynamic_keymap_reset` | not exposed |
| `0x0A` `eeprom_reset` | not exposed |
| `0x0B` `bootloader_jump` | replaced by `0xD1 0x22` reboot path |

---

## 3. QwertyKeys custom extensions

Three custom base opcodes are defined outside the VIA range:

| Base | Purpose | Access |
|----:|---|---|
| `0xBF` | CDC support probe | single byte reply |
| `0xD0` | **Actuation / Performance** namespace (profiles, APC, RT, Adv-Keys, calibration, self-test) | `actuationCommand(sub, args)` |
| `0xD1` | **Tab** namespace (per-pixel RGB, on-device display/firmware upload, reboot) | direct `send(0xD1, …)` |

### 3.1. The actuation wrapper

```js
async actuationCommand(sub, args = []) {
  return await this.send(0xD0, [sub, ...args]);
}
```

So every command in this section is a 32-byte report whose first three
bytes are `D0 <sub> <arg0>…`.

### 3.2. Profile namespace (0xB0 – 0xB6)

This is the group that implements the "switch profile" feature on the
site. `PROFILE_NAME_SIZE` is fixed at **28 bytes** (UTF-8, null-padded).

| Sub-op | Method | Request bytes after `D0` | Reply payload |
|---:|---|---|---|
| `0xB0` | `getProfileIdx()` | `B0` | `idx` |
| **`0xB1`** | **`setProfileIdx(idx)`** | **`B1 idx`** | (ack) |
| `0xB2` | `getProfileName(i)` | `B2 i` | 28 bytes UTF-8 |
| `0xB3` | `setProfileName(i, name[28])` | `B3 i name…` | (ack) |
| `0xB4` | `getProfileColor(i)` | `B4 i` | `r g b` |
| `0xB5` | `setProfileColor(i, r, g, b)` | `B5 i r g b` | (ack) |
| `0xB6` | `getProfileCount()` | `B6` | `count` |

High-level Redux thunk (`configure` slice):

```js
switchProfile = (i) => async (dispatch, getState) => {
  if (getSwitching(state)) return;                 // debounce
  dispatch(setSwitching(true));
  dispatch(clearSelectedKeys());
  await getActuationApi(hidapi).setProfileIdx(i);  // D0 B1 <i>
  dispatch(setProfileIdx(i));
  dispatch(setSwitching(false));
};
```

`loadProfiles` runs on device connect: it issues `B6` to learn the
count, then `Promise.all` of `B2/B4` per profile to fetch names and
colors. `saveProfile(i, p)` sends `B3` then `B5`. These are the only
writes required to fully define a profile slot.

### 3.3. Actuation Point / Rapid Trigger / precision

Per-key physical switch tuning. These write to the *currently active*
profile.

| Sub-op | Method | Purpose |
|---:|---|---|
| `0x90` | `getTestActDisplay()` | live actuation overlay enable flag |
| `0x92` | `getVenturePrecisionEnabled()` | read precision flag |
| `0x93` | `setVenturePrecisionEnabled(b)` | write precision flag |
| `0xA0` | `getActPrecision()` | read precision value |
| `0xA8` | `setRTEnabled(b)` | RT on/off |
| `0xA9` | `getAPKeySize()` | number of AP entries |
| `0xAA` | `getAPData(idx)` | read one AP entry (paged) |
| `0xAB` | `setAPValue(row, col, apData, dzData, axisID)` | write AP for one key |
| `0xAC` | `getAPDefault()` | factory default AP |
| `0xAD` | `getTestActuation()` | live max/min actuation snapshot |
| `0xAF` | `setSwitchRules(rules[])` | per-axis physical model |
| `0xC1` | `getRTKeySize()` | number of RT entries |
| `0xC2` | `getRTData(idx)` | read one RT entry |
| `0xC3` | `setRTValue(row, col, rtData)` | write RT for one key |
| `0xC4` | `getRTDefault()` | factory default RT |

Actuation values are 16-bit quantities on the wire. The client scales
between UI units and on-device counts with:

```
ACTUATION_MULTIPLE = (MAX_KB_ACTUATION - MIN_KB_ACTUATION)
                   / (MAX_ACTUATION    - MIN_ACTUATION);
```

### 3.4. Advanced-Keys namespace (paired get/set sub-opcodes)

Advanced keys are stored in a banked list per feature. Each feature
uses a `(count_op, read_op, write_op)` triple under `0xD0`. `read_op`
takes a 16-bit index and returns one bank; `write_op` takes `[bank_id,
…feature_specific_payload]`.

| Feature  | count | read | write | Payload shape (client → device) |
|---|---:|---:|---:|---|
| Enabled list | — | — | `0x01` | (query-only helper returning set of flags) |
| Reset | — | — | `0x0F` | `0F` (factory-resets all adv-key data) |
| **AT** (Adv Tap / "auto-trigger") | `0x10` | `0x11` | `0x12` | `12 bank rows… startHi startLo repeatHi repeatLo` |
| **SOCD** | `0x20` | `0x21` | `0x22` | `22 bank priority row col row col` |
| **RS** (Rappy Snappy / last-win) | `0x2A` | `0x2B` | `0x2C` | `2C bank row col row col` |
| **END** (End-of-stroke macro) | `0x60` | `0x61` | `0x62` | `62 bank row col pressHi pressLo releaseHi releaseLo delayHi delayLo` |
| **MT** (Mod-Tap) | `0x70` | `0x71` | `0x72` | `72 bank row col tapHi tapLo holdHi holdLo delayHi delayLo` |
| **TGL** (Toggle) | `0x80` | `0x81` | `0x82` | `82 bank row col keyHi keyLo delayHi delayLo` |
| **DKS** (Dynamic Key Stroke) | — | — | `0xD0` | `getDKSActuation` read-only probe; write path not in client |
| **MPT** (Multi-point / 3-stage) | `0xD8` | `0xD9`* | `0xDA`* `0xDB` | `DB bank row col (keyHi keyLo actHi actLo)×3` |

\* Inferred from `getAdvKeyBytes(217, 218)` pair — `0xD9`/`0xDA` are
the count/read half.

Generic read helper:

```js
async getAdvKeyBytes(countOp, readOp) {
  const n = await this.getAdvKeyCount(countOp);           // D0 <countOp>  -> 16-bit count
  const banks = await Promise.all(
    range(n).map(i => this.actuationCommand(readOp,
                                             shiftFrom16Bit(i)))
  );
  return banks;   // each bank's first 4 bytes are header (D0, readOp, idxHi, idxLo)
}
```

### 3.5. Calibration & self-test (0x9A–0xA5)

| Sub-op | Method |
|---:|---|
| `0x9A` | `mftestStart()` — manufacturing test |
| `0x9B` | `mftestInfo()` |
| `0xA1` | `getSelfCheckResult()` |
| `0xA2` | `selfCheck()` |
| `0xA3` | `calibrationStart()` |
| `0xA4` | `calibrationEnd()` |
| `0xA5` | `getCalibrationInfo()` |

### 3.6. Tab namespace (`0xD1`)

Not wrapped by `actuationCommand` — the client calls `this.send(0xD1, …)`
directly.

| Bytes after `D1` | Purpose |
|---|---|
| `30 r g b chan` | `setMatrixLighting` — per-key RGB channel write |
| `11 offsHi offsLo len data…` | `setTabFirmwareBuffer` — streamed firmware upload |
| `(header)` | `setTabFirmwareInfo` — flash prelude |
| `20 filename[20]` | `setTabFile` — select file on an on-device display |
| `22` | reboot / post-upload jump (used in the firmware flow) |
| `34` | observed in the reboot path (`await this.send(0xD1, [0x22])` fallback) |

### 3.7. Misc (`0xBF`)

| Opcode | Method | Purpose |
|---:|---|---|
| `0xBF` | `getCDCSupport()` | returns whether the device exposes a USB-CDC channel for bulk firmware updates |

---

## 4. Redux wiring (client side)

The configurator uses `@reduxjs/toolkit` with the following slices:

- **`device`** — `deviceId`, `hidapi` handle, product info, supported
  `vpId` list fetched from `/configs/supported_kbs.json`.
- **`configure`** — `menu`, `macroMode`, `profileMap[deviceId] →
  Profile[]`, `profileIdx`, `axCfg`, `keyMenu`, `pfmMenu`, `switching`,
  `rebooting`.
- **`actuation`** — per-key AP/RT/axis map keyed by
  `(devicePath, keymapIdx)`.

Relevant thunks, all in one file in the bundle:

| Thunk | Effect |
|---|---|
| `loadProfiles()` | `B6` → `B2/B4` × count → `B0` |
| `saveProfile(i, p)` | `B3` then `B5` |
| **`switchProfile(i)`** | sets `switching=true`, clears key selection, sends `B1 i`, sets `profileIdx=i`, clears `switching` |
| `updateDz(keys, dz)` / `updateAxis(keys, ax)` | recomputes AP, calls `setAPValue` per selected key |
| `updateAxisRule()` | re-emits `setSwitchRules` with current switch list |

### 4.1. Keyboard definition loading

The HTML ships a `<script id="definition_hash" data-hash="…">` tag.
`TabStorage` (thin wrapper over `localStorage`) caches the set of
supported device ids keyed by this hash; on every load:

```js
const cached   = tabStorage.get('definition').hash;
const shipped  = document.getElementById('definition_hash').dataset.hash;
if (shipped === cached && cached !== '') return tabStorage.get('definition').supportedIds;

const ids = await (await fetch('/configs/supported_kbs.json',
                               { cache: 'reload' })).json();
tabStorage.set('definition', { hash: shipped, supportedIds: ids, definitions: {} });
```

Per-device VIA definitions (`.json`) are fetched on demand when a
matching device is connected and then stored in the same `TabStorage`
blob.

---

## 5. Diff against upstream VIA

### What is unchanged
- WebHID transport contract (usage page `0xFF60`, usage `0x61`,
  report ID `0`, 32-byte reports, big-endian 16-bit values).
- Base VIA opcodes `0x01–0x15` with unchanged semantics and field
  layouts.
- Definition-hash + supported-devices manifest idea.

### What is added (QwertyKeys-specific)
- **`0xD0` "actuation" namespace** (44 sub-commands observed) covering
  profiles (B0–B6), APC/RT (A0–C4), advanced keys (low byte ranges
  grouped by feature), calibration, manufacturing test.
- **`0xD1` "tab" namespace** for per-pixel RGB writes, on-device
  display file uploads, streamed firmware upload and reboot.
- **`0xBF`** CDC probe.
- Redux slice `configure.profileIdx` + `configure.profileMap` and the
  `switchProfile` thunk, which together turn a UI click into a single
  `0xD0 0xB1 <idx>` HID report.

### What is removed vs upstream VIA
- `0x04` (per-key keycode read) — client uses `0x12` buffer reads.
- `0x06` (keymap reset), `0x0A` (EEPROM reset), `0x0B` (bootloader
  jump) — either not exposed or replaced by the `0xD1` reboot path.

---

## 6. Minimal standalone profile switcher

See `switch-profile.html` in this repo. It reproduces the full switch
path in ~60 lines of JavaScript:

1. `navigator.hid.requestDevice({ filters:[{ usagePage:0xFF60, usage:0x61 }] })`
2. `device.sendReport(0, <32-byte buffer starting with D0 B1 idx>)`
3. Read back the echoed reply from the next `inputreport` event.

That single `D0 B1 <idx>` report is the entire "switch keyboard profile"
operation — everything else on the site is UI, caching, and Redux
bookkeeping around it.
