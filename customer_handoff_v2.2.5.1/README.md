# OAPA Hotfix Build — v2.2.5.1 (Customer Hand-off)

This folder contains a **pre-release** build of the *Three Point Polar Alignment* plugin for N.I.N.A. and the matching firmware for the OAPA controller (FYSETC E4 / ESP32).

It is not published to the official NINA plugin repository — it is meant to be installed manually to validate the high-gear-ratio fixes before a public release.

---

## What's in this folder

```
customer_handoff_v2.2.5.1/
├── README.md                ← this guide
├── RELEASE_NOTES.md         ← what changed and what's next
├── plugin/
│   ├── NINA.Plugins.PolarAlignment.dll
│   └── Changelog.md
└── firmware/
    └── oapa.ino                     ← Arduino sketch (compile & upload from Arduino IDE)
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
4. Start NINA. The plugin version under *Options → Plugins* should read **2.2.5.1**.

Your existing settings (gear ratios, reverse axis flags, COM port, etc.) are preserved — they live in the user profile, not in the DLL.

---

## 2. Update the firmware (OAPA controller, FYSETC E4 / ESP32)

The firmware is shipped as the Arduino sketch `firmware/oapa.ino`. Compile and upload it with the Arduino IDE (the same way the controller was originally flashed).

### Steps (Arduino IDE)

1. Install the **ESP32 board package** (Tools → Board → Boards Manager → "esp32" by Espressif).
2. Install the required libraries (Tools → Manage Libraries):
   - **TMCStepper** by teemuatlut
   - **AccelStepper** by Mike McCauley
3. Open `firmware/oapa.ino`.
4. Select the board: **Tools → Board → ESP32 Arduino → ESP32 Dev Module** (FYSETC E4 v1.3 is compatible).
5. Select the correct **COM port** (Tools → Port).
6. Click **Upload**.

### Verify

After the upload finishes, open the Arduino Serial Monitor at **115200 baud**. Send:

```
$$
```

You should get the GRBL-style settings dump back. Send `?` to read the current status.

> Tip: keep a copy of the previous `oapa.ino` if you want an easy rollback path.

---

## 3. Configure your gear ratio

In NINA → *Imaging tab → Polar Alignment dock → OAPA panel*:

1. Set **Azimuth gear ratio** to your real value (e.g. `100` for a 100:1 harmonic reducer).
2. Set **Altitude gear ratio** to its real value.
3. Leave the *reverse* checkboxes at their previous values — change only if a calibration test shows the axis moves the wrong way.

No restart is required after changing the ratio.

---

## 4. Verifying the fix

The two symptoms that this build targets are:

| Symptom (before)                                                        | Expected behavior (after)                                          |
|-------------------------------------------------------------------------|--------------------------------------------------------------------|
| `Motor appears stuck at position …` after a few seconds                 | Move completes; timeout now scales with how big the move is.        |
| Small commanded moves at 100:1 produce no actual rotation               | Sub-step values are now rounded, not truncated → motor moves.       |
| Plate-solved azimuth correction undershoots / oscillates around target  | Target is rounded to nearest step → no sub-step hunting.            |

### Suggested smoke test

1. Connect the mount + camera in NINA as usual.
2. Open the OAPA panel and command a **small manual jog** of, say, **5 arcmin** on the azimuth axis.
3. Confirm the motor visibly turns and reports completion (no "stuck" warning).
4. Run a normal three-point polar alignment cycle and confirm corrections converge.

If the axis moves in the **wrong direction**, toggle the corresponding *Reverse Azimuth* / *Reverse Altitude* checkbox in the OAPA panel.

---

## Reverting

To roll back:

- **Plugin:** delete the new DLL and rename the `.dll.bak` back to `NINA.Plugins.PolarAlignment.dll`.
- **Firmware:** open your previous `oapa.ino` in the Arduino IDE and re-upload it.

User settings are unaffected by either rollback.

---

## Support

If anything misbehaves during testing, please send back:

- The NINA log file from `%localappdata%\NINA\Logs\` (the most recent one).
- The contents of the OAPA panel (gear ratios, reverse flags, COM port).
- A short description of what you commanded and what happened.
