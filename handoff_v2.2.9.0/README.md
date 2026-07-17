# OAPA Build — v2.2.9.0 (Hand-off)

This folder contains a **pre-release** build of the *Three Point Polar Alignment* plugin for N.I.N.A. This release directly addresses your report: **the procedure could not settle below 1 arcminute, and the Alignment Tolerance field would not accept decimals** (and rejected 0 with "needs to be greater than 0").

> **Firmware is unchanged** since v2.2.5.1. The `firmware/oapa.ino` in this folder is included only for completeness; if you already flashed v2.2.5.1 or later firmware you do **not** need to re-flash.

---

## What's in this folder

```
handoff_v2.2.9.0/
├── README.md                    ← this file
├── plugin/
│   ├── NINA.Plugins.PolarAlignment.dll
│   └── Changelog.md
└── firmware/
    └── oapa.ino                 (unchanged, for reference only)
```

---

## 1. Installation

**Close N.I.N.A. before replacing the DLL.**

1. Open the NINA plugin folder in Explorer:
   ```
   %localappdata%\NINA\Plugins\3.0.0\Three Point Polar Alignment\
   ```
2. Make a backup of the existing `NINA.Plugins.PolarAlignment.dll` (rename to `.dll.bak`).
3. Copy `plugin\NINA.Plugins.PolarAlignment.dll` from this folder into that directory.
4. Start NINA. Under *Options → Plugins* the version should read **2.2.9.0**.

All existing settings are preserved. **Important:** after installing, please **re-run Self-Calibration once** (see §3.2) — the new calibration also measures your mount's backlash, which is essential for sub-arcminute convergence.

---

## 2. What's new in 2.2.9.0

### 2.1 Alignment Tolerance accepts decimals (your report)

The tolerance field silently ate the decimal separator as you typed (a WPF binding re-parsed the text on every keystroke), so only whole arcminutes could be entered. Fixed. You can now type **`0.5`** (= 30 arcseconds) and the value commits when the field loses focus.

- `0` still (intentionally) blocks auto-align: with tolerance 0 the loop would never declare success. The validation message now explains that decimals are supported.
- New pre-flight warning if you set the tolerance below ~0.23': that's below the correction dead-band and the loop may not be able to converge (see 2.3).

### 2.2 Self-Calibration: 10× longer lever + automatic backlash measurement

- Calibration legs are now **45'** instead of 5'. Plate-solve noise drops from ~5-10% to <1% of the measurement → far more accurate calibration factors.
- The routine now also **measures your mechanical backlash automatically** (from how much the direction-reversal leg comes up short) and shows it in the result panel.
- Clicking **Apply** persists both the factors *and* the measured backlash into the OAPA backlash compensation settings. Backlash compensation previously defaulted to 0, which is the main reason the loop oscillated around 0.5-1.5' — every direction change lost tens of arcseconds.
- Each axis moves at most ±1.5° on the sky during calibration and returns exactly to its starting position.

### 2.3 Smarter correction loop

- **Both axes are corrected in every iteration** (previously one per iteration) → roughly half the exposure/solve/settle cycles to converge.
- **Adaptive gain**: 90% of the error is corrected while far from the pole (> 2'), 60% on final approach — fast initially, no overshoot at the end.
- **Dead-band 0.15' per axis**: below that, plate-solve noise dominates and the motors stop chasing it.
- Spurious direction flips near the target are eliminated (the reversal heuristic now also requires the error to worsen by more than the noise floor).

### 2.4 Auto-finish requires 2 consecutive confirmations

A single lucky plate solve below tolerance no longer ends the procedure. Two consecutive solves must agree, with motors held still in between. This prevents "finished at 0.4', actual error 1.2'" outcomes.

---

## 3. Test plan

### 3.1 Tolerance field (no sky needed, 1 minute)

1. Install per §1, start NINA, verify version **2.2.9.0**.
2. Add a *Three Point Polar Alignment* instruction in the sequencer.
3. Type `0.5` in **Alignment Tolerance**, press Tab. **Expected:** the field keeps `0.5` and shows no validation error.
4. Set it to `0` with automated adjustments enabled. **Expected:** the instruction shows the issue "set an alignment tolerance greater than zero - decimal values like 0.5 arcmin are supported".
5. Set it to `0.1`. **Expected:** a warning that the value is below the dead-band and may never converge.

### 3.2 Re-calibration (clear sky, ~5 minutes)

1. Open the OAPA dock in the *Imaging* tab, connect, click **Calibrate**.
2. **Expected:** the routine performs 4 moves + 4 solves per axis (status shows "priming", "forward leg", "reversal leg", "reverse leg"). Each axis ends where it started.
3. **Expected result panel:** discovered X/Y factors **and** two new lines: *Discovered X backlash (arcmin)* / *Discovered Y backlash (arcmin)*. Typical values for your hardware: factors close to what you had, backlash somewhere between 0 and a few arcmin.
4. Click **Apply**. **Expected toast:** "Calibration factors and backlash compensation updated". Verify in Options that the OAPA backlash compensation fields now hold the measured values.

### 3.3 The main event: sub-arcminute alignment (clear sky, ~10 minutes)

1. Set Alignment Tolerance to **`0.5`**.
2. Run the full TPPA procedure with automated adjustments enabled.
3. **Expected behaviour changes vs v2.2.8.x:**
   - Both axes move in each correction iteration (log shows "Nudging along X axis…" *and* "Nudging along Y axis…" per cycle).
   - Big corrections early, small ones near the end.
   - When the total error first drops below 0.5', the log shows "awaiting confirmation solve (1/2)" and the motors stay still.
   - The procedure completes only after the second confirming solve.
4. **Success criterion:** total error at completion ≤ 0.5' and stable (the two confirmation solves agree).

If the loop stalls without converging below ~1' even after re-calibration, please capture the log — the interesting lines contain `Calculated Error`, `Nudging along`, `Reversing`, and `dead-band`.

---

## 4. Reverting

To roll back to v2.2.8.1:
- Delete the v2.2.9.0 DLL and rename the `.dll.bak` back to `NINA.Plugins.PolarAlignment.dll`.
- Restart NINA.

Note: if you clicked Apply after a v2.2.9.0 calibration, the backlash compensation settings keep the measured values after rollback. That is harmless (v2.2.8.x also honours them), and arguably an improvement.

---

## 5. Support

If anything looks wrong during testing, please send back:
- The NINA log file from `%localappdata%\NINA\Logs\` (most recent).
- A screenshot of the Self-Calibration result panel (factors + backlash values).
- The final reported Az/Alt/Total error and the tolerance you used.
