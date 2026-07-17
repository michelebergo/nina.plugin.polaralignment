# OAPA Self-Calibration Build — v2.2.7.0 (Hand-off)

This folder contains a **pre-release** build of the *Three Point Polar Alignment* plugin for N.I.N.A. with two new behaviours on top of v2.2.6.0:

1. **Self-calibration auto-fixes wrong Reverse Az / Reverse Alt flags.**
2. **Avalon UPAS no longer applies any altitude backlash compensation** (it was symmetric in 2.2.6.0; OAPA still gets full Y-axis backlash).

It is not published to the official NINA plugin repository — install it manually to validate the changes before a public release.

> **Firmware is unchanged** since v2.2.5.1. The `firmware/oapa.ino` in this folder is included only for completeness; if you already flashed the v2.2.5.1 (or 2.2.6.0) firmware you do **not** need to re-flash.

---

## What's in this folder

```
handoff_v2.2.7.0/
├── README.md                    ← this guide
├── plugin/
│   ├── NINA.Plugins.PolarAlignment.dll   (v2.2.7.0)
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
4. Start NINA. The plugin version under *Options → Plugins* should read **2.2.7.0**.

Your existing settings (calibration factors, reverse axis flags, COM port, motor currents, OAPA backlash, etc.) are preserved.

> Note on the Avalon altitude backlash setting from 2.2.6.0: that key has been removed from the plugin's settings file. Any value you may have entered for it will be ignored from this version on. Avalon X-axis backlash is unaffected.

---

## 2. Firmware

Unchanged since v2.2.5.1. Skip unless you are coming from an older firmware. Re-flash procedure is identical to the v2.2.6.0 hand-off.

---

## 3. What's new in 2.2.7.0

### 3.1 Auto-correct of the Reverse Az / Reverse Alt flags

In v2.2.6.0, when self-calibration ran with the Reverse flag set the wrong way for an axis, the discovered ratio came out **inflated** (e.g. ~19 / ~59 instead of the real ~6.4 / ~6.3) and the panel showed:

> Direction consistency: WARNING (X=fail, Y=fail). Reverse flag may be wrong.

The user then had to flip the flag manually in Options and re-run.

**In 2.2.7.0 the routine does this for you.** Per axis:

1. Run the normal calibration with the current Reverse flag.
2. If the direction-consistency check passes → done.
3. If it fails → log "auto-flipping", show *"X (Azimuth): auto-flipping Reverse and retrying..."* in the status text, and re-run the calibration **with the flag flipped (in memory only)**.
4. If the retry passes → the flipped flag is **persisted** (the checkbox in *Options* updates immediately) and the corrected ratio is reported.
5. If the retry still fails → original flag is restored and the panel shows:
   > Direction consistency: WARNING ... Auto-flip did not resolve it; check wiring.

When the auto-flip succeeds the consistency message is explicit so nothing is hidden:

> Direction consistency: OK (Reverse Az auto-corrected)
> Direction consistency: OK (Reverse Az auto-corrected, Reverse Alt auto-corrected)

Total runtime in the worst case (both axes need a flip) is roughly **2× the v2.2.6.0 calibration time**. Healthy axes are unchanged.

### 3.2 Avalon altitude backlash compensation removed

For the Avalon UPAS, `YBacklashCompensation` is now a fixed `0` and the *Options* row "Altitude backlash compensation" is hidden when Avalon is the selected system. Avalon's `ClearBacklash` is therefore a no-op on the Y axis, matching pre-2.2.6.0 behaviour.

OAPA still has its own `Altitude backlash compensation` field (separate setting `OAPAYBacklashCompensation`, default `0`) which **is** applied on Y-axis direction reversals.

### 3.3 Everything else from 2.2.6.0

The 2.2.6.0 self-calibration feature itself (panel, capture/move/plate-solve loop, median over 2 samples per axis, Apply / Discard buttons) is unchanged. See the previous hand-off README for that part.

---

## 4. Test plan

### 4.1 Avalon regression (5 min — bench, no sky needed)

Goal: confirm Avalon altitude motion is unaffected by the backlash removal.

1. In *Options*, select the Avalon polar alignment system.
2. Confirm there is **no** "Altitude backlash compensation" row visible (Azimuth backlash row is still present and editable).
3. Connect the Avalon mount.
4. Manually jog the altitude axis a few times in alternating directions.
   - Movements should be the commanded magnitude (no extra "compensation" sub-move on direction change).
   - Azimuth jogs should still apply the configured X backlash.
5. Reload the plugin / restart NINA — the Avalon settings panel should still come up correctly with no error in the log.

### 4.2 OAPA self-calibration with the Reverse flag intentionally wrong (15-30 min — clear sky)

Goal: confirm the auto-flip kicks in, persists the flag, and lands on a sensible ratio.

Pre-conditions:
- OAPA controller connected, plate-solver configured.
- Slew to a star-rich field (zenith works well).
- Note your **current working** values for: `Reverse Azimuth`, `Reverse Altitude`, the `Calibration Factor` field for X and the one for Y (these are the values stored under the legacy setting keys `XGearRatio` / `YGearRatio`).

Steps:
1. **Force a wrong state on purpose** for the X axis: in *Options*, toggle `Reverse Azimuth` to the opposite of its working value.
2. Open the OAPA dock → *Self-Calibration* → click **Calibrate**.
3. Watch the status text. You should see, in order:
   - `X (Azimuth): baseline solve...`
   - `X (Azimuth): sample 1/2 +5.0'` ... etc.
   - When the first X pass completes inconsistent, status switches to `X (Azimuth): auto-flipping Reverse and retrying...`
   - X then re-runs.
   - Y proceeds normally (since you only forced X wrong).
4. When the routine finishes:
   - **`Reverse Azimuth` checkbox in Options should now be back to its working value** (the auto-flip persisted it).
   - Discovered X ratio should be within ~15-20% of your working X.
   - Discovered Y ratio should be within ~15-20% of your working Y.
   - Consistency message should read: `Direction consistency: OK (Reverse Az auto-corrected)`.
5. Click **Apply** and run a normal three-point polar alignment cycle to confirm the mount converges.

Repeat with `Reverse Altitude` toggled wrong, then with **both** toggled wrong, to exercise all three paths.

### 4.3 OAPA self-calibration with hardware that genuinely cannot be calibrated (optional, edge case)

If you have a way to provoke a real direction failure that **flipping the flag does not fix** (e.g. unplugged motor, mechanical slip), run the calibration and confirm:
- The status shows the auto-flip attempt.
- The final consistency message is: `Direction consistency: WARNING ... Auto-flip did not resolve it; check wiring.`
- Both Reverse flags in Options are **unchanged** (restored to their pre-calibration state).

---

## 5. Reverting

To roll back to v2.2.6.0:
- Delete the new DLL and rename the `.dll.bak` back to `NINA.Plugins.PolarAlignment.dll`.
- Restart NINA.

User settings are unaffected by the rollback. The Avalon altitude backlash setting that 2.2.6.0 had does not get restored automatically (the key was deleted), but its default is `0` so behaviour is identical to a fresh setup.

Firmware does not need to be reverted.

---

## 6. Support

If anything misbehaves during testing, please send back:

- The NINA log file from `%localappdata%\NINA\Logs\` (the most recent one).
- A screenshot of the OAPA panel after the run (current ratios, discovered ratios, status text, consistency message).
- A note of the Reverse Az / Reverse Alt checkbox states **before** and **after** the calibration.
- A short description of what you commanded and what happened.
