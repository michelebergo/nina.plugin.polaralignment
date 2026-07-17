# OAPA Build — v2.2.8.0 (Hand-off)

This folder contains a **pre-release** build of the *Three Point Polar Alignment* plugin for N.I.N.A. The single change on top of v2.2.7.0 is a **UI rename for clarity** — no functional behaviour change.

It is not published to the official NINA plugin repository — install it manually to validate the new wording before a public release.

> **Firmware is unchanged** since v2.2.5.1. The `firmware/oapa.ino` in this folder is included only for completeness; if you already flashed the v2.2.5.1, v2.2.6.0, or v2.2.7.0 firmware you do **not** need to re-flash.

---

## What's in this folder

```
handoff_v2.2.8.0/
├── README.md                    ← this guide
├── plugin/
│   ├── NINA.Plugins.PolarAlignment.dll   (v2.2.8.0)
│   └── Changelog.md
└── firmware/
    └── oapa.ino                 ← unchanged from v2.2.5.1, included for completeness
```

---

## 1. Update the plugin

> ⚠️ **Close N.I.N.A. before replacing the DLL.**

1. Open the NINA plugin folder in Explorer:
   ```
   %localappdata%\NINA\Plugins\3.0.0\Three Point Polar Alignment\
   ```
2. Make a backup of the existing `NINA.Plugins.PolarAlignment.dll` (rename it to `.dll.bak`).
3. Copy the new `plugin\NINA.Plugins.PolarAlignment.dll` from this folder into that directory.
4. Start NINA. The plugin version under *Options → Plugins* should read **2.2.8.0**.

**All existing settings are preserved.** The internal settings keys (`OAPAXGearRatio`, `OAPAYGearRatio`, `AvalonXGearRatio`, `AvalonYGearRatio`, reverse-axis flags, COM port, motor currents, OAPA backlash, etc.) are unchanged — only the user-facing label has been renamed, so the values you saved on v2.2.7.0 will still be there.

---

## 2. Firmware

Unchanged since v2.2.5.1. Skip unless you are coming from an older firmware.

---

## 3. What's new in 2.2.8.0

### 3.1 Renamed "Gear Ratio" → "Calibration Factor" (UI only)

The previous label was misleading. The value the plugin stored under the name `Gear Ratio` is **not a mechanical gear ratio** (like a harmonic reducer's `1:N` or a ballscrew's `mm/rev`). It is a **software calibration constant** — specifically, the amount of G-code command (in millimetres, in `G21` mm-mode) the plugin sends per **arcminute of polar-axis movement**. It bundles together the contributions of:

- the mechanical reduction chain (harmonic / belt / ballscrew / etc.),
- the GRBL controller configuration (`$100` steps per mm),
- the motor microstep multiplier,
- and the lever geometry of the polar-alignment wedge that translates actuator motion into polar-axis tilt.

A typical OAPA-class unit converges around `~6.4` (X) / `~6.3` (Y). A custom build (e.g. 1:100 harmonic + ballscrew + belt) will land at a different number — that is correct, not a bug.

**What changed in the UI:**

| Where | v2.2.7.0 | v2.2.8.0 |
|---|---|---|
| Self-Calibration button | "Calibrate gear ratios" | **"Calibrate"** |
| Self-Calibration result rows | "Discovered/Current X (Y) ratio" | **"Discovered/Current X (Y) factor"** |
| Toast after Calibrate | "Calibration done. X ratio: …, Y ratio: …" | **"Calibration done. X factor: …, Y factor: …"** |
| Toast after Apply | "Calibration applied to gear ratios" | **"Calibration factors updated"** |
| Azimuth / Altitude motor settings panel | "GearRatio" textbox label | **"Calibration Factor"** |
| Options panel | "GearRatio" textbox label | **"Calibration Factor"** |
| FAQ.md / handoff README | "gear ratio settings" | **"calibration factor settings"** |

**What did NOT change:**

- The internal storage keys (`OAPAXGearRatio`, `OAPAYGearRatio`, `AvalonXGearRatio`, `AvalonYGearRatio`) are unchanged — your saved values are preserved across the upgrade.
- The C# property names (`XGearRatio`, `YGearRatio`) are unchanged — developer-facing only.
- All motion behaviour, all self-calibration math, all backlash compensation logic, and the auto-flip retry from v2.2.7.0 are identical.

### 3.2 Everything else from 2.2.7.0

The 2.2.7.0 feature set (self-calibration with auto-flip of Reverse Az / Reverse Alt, separate Y-axis backlash for OAPA, Y-axis backlash removed for Avalon UPAS) is unchanged. See the v2.2.7.0 hand-off README for that part.

---

## 4. Test plan

### 4.1 Smoke test (2 min — no sky needed, no hardware needed)

Goal: confirm the labels in the UI now read "Calibration Factor" everywhere and that your previous settings survived the upgrade.

1. Install the new DLL per §1 and start NINA.
2. Go to **Options → Plugins → Three Point Polar Alignment** and confirm the plugin version is **2.2.8.0**.
3. Scroll through the OAPA / AAPA / Avalon sections of the *Options* panel. Every label that used to say "GearRatio" should now read **"Calibration Factor"**.
4. Verify the X and Y values are the same numbers you had on v2.2.7.0. (If you had calibrated and applied on v2.2.7.0, the saved values should still be there.)
5. Open the OAPA dock panel in the *Imaging* tab. The Azimuth / Altitude motor settings panels at the top should both say **"Calibration Factor"**, not "GearRatio".

If all of the above looks right, the rename worked cleanly and you can move on to the regression test.

### 4.2 Self-Calibration regression test (15–30 min — clear sky)

Goal: confirm the calibration math is unchanged from v2.2.7.0 — the values you discover should be the same as before, only the label changed.

Pre-conditions:
- OAPA controller connected, plate-solver configured.
- Slew to a star-rich field (zenith works well).
- Your saved values for `Reverse Azimuth`, `Reverse Altitude`, X Calibration Factor, Y Calibration Factor (the previous "X/Y Gear Ratio") are loaded.

Steps:
1. Open the OAPA dock → *Self-Calibration* section.
2. Confirm the button reads **"Calibrate"** (not "Calibrate gear ratios").
3. Click **Calibrate**.
4. While the routine runs, watch the status line. The wording is unchanged from v2.2.7.0 (`X (Azimuth): sample 1/2 +5.0'`, etc.).
5. When the routine finishes, the result panel should show:
   - **Discovered X factor** and **Discovered Y factor** in bold, alongside **Current X factor** and **Current Y factor**.
   - The Direction consistency line should read `OK` (or `OK (Reverse Az auto-corrected …)` if v2.2.7.0's auto-flip kicks in for you).
   - The toast in the bottom-right corner should read `Calibration done. X factor: …, Y factor: …`.
6. Compare the discovered values against your previous v2.2.7.0 results: they should be within run-to-run noise of each other (typically within a few percent, since the underlying math has not changed).
7. Click **Apply**. The toast should read `Calibration factors updated`.

### 4.3 End-to-end (optional)

Run a normal three-point polar alignment cycle to confirm the mount still converges. Convergence behaviour is unchanged from v2.2.7.0.

---

## 5. Reverting

To roll back to v2.2.7.0:
- Delete the v2.2.8.0 DLL and rename the `.dll.bak` back to `NINA.Plugins.PolarAlignment.dll`.
- Restart NINA.

User settings are unaffected by the rollback (same storage keys). Firmware does not need to be reverted.

---

## 6. Support

If anything looks wrong during testing, please send back:

- The NINA log file from `%localappdata%\NINA\Logs\` (the most recent one).
- A screenshot of the OAPA panel (showing the new labels).
- A screenshot of *Options → Plugins → Three Point Polar Alignment* showing the version is 2.2.8.0.
- A note of the X and Y values your previous v2.2.7.0 install had, and the values now visible on v2.2.8.0 (they should match).
