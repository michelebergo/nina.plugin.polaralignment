# OAPA Build — v2.2.7.0-rc1 (Hand-off, rebased on official 2.2.6.3)

This folder contains a **release-candidate** build of the *Three Point Polar Alignment* plugin for N.I.N.A. It is a different beast from the previous hand-off (v2.2.9.0): all our sub-arcminute work has been **rebased on top of Stefan Berg's official 2.2.6.3**, which brings his own major rework of the correction loop.

> **Why the version went "down" (2.2.9.0 → 2.2.7.0):** our previous builds used a private numbering that collided with Stefan's official releases (his 2.2.6.x are different changes). This build follows the official line: 2.2.6.3 (official) + our changes = **2.2.7.0-rc1**.

> **Firmware is unchanged.** No re-flash needed.

> **Important:** if this field test is positive, this exact code will be submitted as a **pull request to the official plugin repository** (isbeorn/nina.plugin.polaralignment). Your test results will be quoted as validation evidence, so please be thorough with §4.

---

## What's in this folder

```
handoff_v2.2.7.0-rc1/
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
2. Back up the existing `NINA.Plugins.PolarAlignment.dll` (rename to `.dll.bak`).
3. Copy `plugin\NINA.Plugins.PolarAlignment.dll` from this folder into that directory.
4. Start NINA. Under *Options → Plugins* the version should read **2.2.7.0**.

**After installing, re-run Self-Calibration once** (§4.2) — it now also measures backlash, which gets applied to the correction loop.

---

## 2. What changed vs the v2.2.9.0 build you have

### 2.1 From Stefan's official 2.2.6.x (new to you)

- **Self-learning correction controller.** The fixed-gain nudge logic is gone. The plugin now learns your hardware's actual response (sign, gain, axis coupling) from small probe moves, then computes each correction from the learned model. It automatically discards the model if a move makes things worse and re-probes. This replaces both the old "0.75 gain single axis" logic *and* the adaptive-gain/dead-band logic from the v2.2.9.0 build.
- **Locked reference star tracking**: star detection runs once, then the star is projected between frames (re-detected only after 120 s, a 0.5° field shift, or an off-image projection) → faster continuous loop.
- **Experimental continuous error estimator** (Options toggle, legacy calculation remains the default).
- **Reworked Options page** with tabs, tooltips, and a run checklist.
- Stability fixes: no more crash when star detection returns an empty list; clean cancellation during ASTAP solving.

### 2.2 From our work (kept, now on top of the official base)

- **Self-Calibration with 45' lever + automatic backlash measurement** — same as the v2.2.9.0 build: 4 legs per axis, factors from the clean legs, backlash from the reversal leg, Apply persists everything.
- **Alignment Tolerance accepts decimals** (`0.5` = 30").
- **Auto-finish requires 2 consecutive solves** below tolerance, motors held still in between.
- Fast reconnect via remembered COM port, per-axis backlash clearing (OAPA Y), ratio-aware motion tolerances.

### 2.3 What is intentionally gone (vs v2.2.9.0)

- The fixed adaptive gain (0.9/0.6) and the 0.15' dead-band: superseded by the learned controller, which handles gain and minimum-move logic on its own (minimum move 0.05 units, max 5 units per cycle).

---

## 3. Expected behaviour differences you will notice

- The first 1-2 automated moves are **small probe moves** — the plugin is learning your hardware. Don't be alarmed if the first move looks timid or even slightly wrong: that's the identification phase.
- After the probes, corrections should be decisive and well-aimed, including diagonal (both-axis) corrections.
- If a correction makes the error worse, the plugin logs a model reset and re-probes instead of blindly reversing direction.

---

## 4. Field test plan

### 4.1 Tolerance field (1 min, no sky)

1. Sequencer → TPPA instruction → type `0.5` in Alignment Tolerance, Tab out. **Expected:** value sticks, no validation error.
2. Set `0` with automated adjustments on. **Expected:** validation message about needing a non-zero tolerance (decimals supported).

### 4.2 Self-Calibration (~5 min, clear sky)

1. OAPA dock → connect → **Calibrate**.
2. **Expected:** per axis: priming, forward leg, reversal leg, reverse leg (4 solves); axis returns to start; result panel shows factors **and** X/Y backlash values.
3. Click **Apply** → verify the backlash values appear in Options → OAPA backlash compensation fields.

### 4.3 Full alignment to 0.5' (~10-15 min, clear sky) — THE key test

1. Alignment Tolerance = `0.5`, automated adjustments ON.
2. Run the full TPPA procedure.
3. **Watch for:**
   - Initial small probe moves, then progressively confident corrections.
   - Log lines mentioning the plan reason for each move.
   - When the error first dips below 0.5': "awaiting confirmation solve (1/2)" and **no motor movement** before the confirming solve.
   - Completion only after two agreeing solves.
4. **Success criterion:** completes at ≤ 0.5' total error, in a reasonable number of iterations (target: fewer than ~10 correction cycles after the three-point measurement).
5. **Bonus test:** repeat with tolerance `0.35` and report where it plateaus.

### 4.4 What to send back (needed for the upstream PR)

- NINA log from `%localappdata%\NINA\Logs\` (the whole session).
- Self-Calibration result screenshot (factors + backlash).
- Final Az/Alt/Total error and number of correction iterations.
- Any moment where the automated correction seemed to fight itself (error increasing for 2+ consecutive moves).

---

## 5. Reverting

Delete the new DLL, rename `.dll.bak` back, restart NINA. Settings are preserved. If you applied a calibration, the discovered factors/backlash stay — they are compatible with the old build.

---

## 6. Next step if the test is positive

This branch will be submitted as a PR to **isbeorn/nina.plugin.polaralignment** containing: Self-Calibration with backlash measurement, the decimal tolerance fix, and the 2-solve confirmation. Your field results (log + numbers from §4.4) will accompany the PR as validation evidence.
