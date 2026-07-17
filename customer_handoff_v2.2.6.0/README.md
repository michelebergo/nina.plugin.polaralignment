# OAPA Self-Calibration Build — v2.2.6.0 (Customer Hand-off)

This folder contains a **pre-release** build of the *Three Point Polar Alignment* plugin for N.I.N.A. with the new **OAPA self-calibration** feature, plus the matching firmware for the OAPA controller (FYSETC E4 / ESP32).

It is not published to the official NINA plugin repository — it is meant to be installed manually so you can validate the new self-calibration flow before a public release.

> **Firmware is unchanged** since v2.2.5.1. The `firmware/oapa.ino` in this folder is included only for completeness; if you already flashed the v2.2.5.1 firmware you do **not** need to re-flash.

---

## What's in this folder

```
customer_handoff_v2.2.6.0/
├── README.md                    ← this guide
├── plugin/
│   ├── NINA.Plugins.PolarAlignment.dll
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
4. Start NINA. The plugin version under *Options → Plugins* should read **2.2.6.0**.

Your existing settings (gear ratios, reverse axis flags, COM port, motor currents, etc.) are preserved — they live in the user profile, not in the DLL.

---

## 2. Firmware (no action needed if already on v2.2.5.1)

The firmware is **identical** to the v2.2.5.1 hand-off. Skip this section unless you are coming from an older firmware (pre-2.2.5.1) or you want to re-flash for any reason.

If you do need to (re-)flash, follow the same procedure as before:

1. Install the **ESP32 board package** in the Arduino IDE (Tools → Board → Boards Manager → "esp32" by Espressif).
2. Install the libraries: **TMCStepper** by teemuatlut, **AccelStepper** by Mike McCauley.
3. Open `firmware/oapa.ino`.
4. Select board **ESP32 Arduino → ESP32 Dev Module** and the correct **COM port**.
5. Click **Upload**.
6. Open the Serial Monitor at **115200 baud** and send `$$` to confirm the controller responds.

---

## 3. What's new in 2.2.6.0 — OAPA self-calibration

The OAPA dock now has a new **Self-Calibration** panel (only visible when the controller is connected). It runs a fully automated routine that **discovers the real azimuth and altitude gear ratios** by moving the OAPA in small known steps and measuring how far the sky actually moves with plate-solving.

This is the easiest way to nail down the gear ratio for a custom or unknown reduction (e.g. a harmonic reducer of unknown exact ratio, or a hand-built setup).

### Prerequisites

- Camera, mount and **plate solver** all configured and working (the routine uses the same plate-solver settings as the *Plate Solving* options page).
- Mount pointing somewhere with enough stars to plate-solve reliably (zenith / near-zenith works well).
- OAPA controller connected (the panel is only visible when the controller is online).
- Telescope reasonably close to the field of view of the camera so the initial blind/coarse solve succeeds.

### How to run it

1. Slew to a target field that plate-solves cleanly.
2. Open the **OAPA** dock and locate the **Self-Calibration** group.
3. Click **Calibrate gear ratios**.
4. The routine will, for each axis (X = Azimuth, Y = Altitude):
   - Plate-solve the baseline.
   - Move +5 arcmin (in the OAPA's UI frame), plate-solve.
   - Move -10 arcmin (overshoot to the negative side), plate-solve.
   - Move +5 arcmin to return to the baseline.
   - Repeat 2 times.
5. The **status text** updates live so you can follow what is happening.
6. When done, the panel shows the **Discovered X ratio** and **Discovered Y ratio** alongside the **current** values.
7. A **direction-consistency** message is shown:
   - `Direction consistency: OK` — the +/- moves landed on opposite sides of the baseline as expected. The reverse-axis flag is correct.
   - `Direction consistency: WARNING` — one axis did not move the way it was expected to. The discovered magnitude is still usable, but you should toggle the corresponding *Reverse Azimuth* / *Reverse Altitude* checkbox before applying.
8. Click **Apply** to commit the discovered ratios to the plugin settings (they auto-persist), or **Discard** to keep the current values.

Total runtime is approximately **5 minutes** with a typical plate-solve time of ~10–15 s.

### Notes & limitations (v1)

- The routine measures the **magnitude** of the gear ratio, not the sign. The `Reverse Azimuth` / `Reverse Altitude` flags are **not** modified — use the consistency message and your own observation to decide whether to toggle them.
- Manual jogging is blocked while the routine is running (same `IsNotMoving` gate as the existing manual moves).
- If a plate-solve fails, the routine retries up to 2 times before aborting the run and showing an error notification.
- Only the OAPA system is touched. AAPA / Avalon UI is unchanged.

---

## 4. Verifying the fix-up release

Recommended smoke test on a clear evening:

1. Connect mount + camera + OAPA controller in NINA.
2. Slew to a field with plenty of stars (e.g. near zenith).
3. Open the OAPA dock and run **Self-Calibration**.
4. Check the discovered X/Y ratios are within roughly ±15% of what you expected for your hardware.
5. Compare the consistency message with what you know about your setup (do the reverse flags need a flip?).
6. Apply (or discard) and run a normal three-point polar alignment cycle. With the corrected ratios, the automated adjustment phase should converge faster (fewer hunting steps).

---

## Reverting

To roll back to v2.2.5.1:

- Delete the new DLL and rename the `.dll.bak` back to `NINA.Plugins.PolarAlignment.dll`.
- Restart NINA.

User settings (gear ratios, reverse flags, motor currents, COM port) are unaffected by the rollback.

Firmware does not need to be reverted — there is no firmware change in 2.2.6.0.

---

## Support

If anything misbehaves during testing, please send back:

- The NINA log file from `%localappdata%\NINA\Logs\` (the most recent one).
- A screenshot or note of the OAPA panel state (gear ratios before/after, reverse flags, COM port, discovered values, status & consistency messages).
- A short description of what you commanded and what happened.
