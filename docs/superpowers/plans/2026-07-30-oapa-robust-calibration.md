# OAPA Robust Calibration (S0-S6) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans style, inline. Steps use checkbox syntax.

**Goal:** Replace the fixed 4-leg calibration with a staged, self-scaling sequence that survives huge backlash (gilas case) and wrong initial ratios, detects slippage, and flags true directional asymmetry.

**Architecture:** All logic in `OapaCalibrationService` (staged sequence) + pure helpers in `OapaCalibrationGeometry`. Public API `CalibrateAxisWithAutoReverse` unchanged. VM only surfaces new outcome fields and blocks Apply on slippage. No base-VM/UPAS changes.

**Branch:** `feat/oapa-robust-calibration` (on top of `feat/oapa-self-calibration` @387a9e4, suite 91 green).

## Locked decisions (Michele 2026-07-30)

- Single ratio per axis = mean(k_fwd, k_rev); asymmetry >10% on large clean legs → flag + report both, no directional model.
- Slippage (|B1−B2| > max(20%, noise-equiv)) → verdict SLIPPAGE: Apply blocked, honest diagnosis with numbers.
- Fine-phase closing margin stays 0.1' (rc8 value, no change needed).

## Units glossary

- `k` = physical axis arcmin per logical commanded arcmin (FakeAxis.responseScale). Final Ratio = currentRatio / k.
- All displacement measurements via `SignedAxisDisplacementArcmin` between consecutive solves (physical axis arcmin).
- Backlash B in physical axis arcmin.

## Stage spec

- **S0 noise**: 2 solves, no motion. noise = |disp(s0a,s0b)|; threshold T = max(5*noise, 0.25'). baseline = s0b. Existing zenith guard before motion.
- **S1 engage**: probe logical P=5'; up to 4 attempts: move +P, solve, d=disp(prev,now); |d|≥T → engaged, k0=|d|/P. Else P×=3 (5,15,45,135). Exhausted → honest error ("axis does not move measurably") + best-effort restore.
- **S2 forward ratio**: N1 = clamp(8'/k0, 1..90') logical. Two legs +N1 (post-engage ⇒ clean). directionConsistent from leg1 signed disp vs +command. spread=|d1−d2|/max >10% → third leg, k_fwd = median/N1; else mean/N1.
- **S3 backlash escalation** (anti-gilas): M=N1; up to 3 iterations: move −M, solve, expected=M*k_fwd, B1=max(0,expected−|d|). B1 ≤ 0.5*expected → done. Else M ← M + 2*B1/k_fwd, re-engage move +M + solve, repeat. Cap per-leg physical ≤ 90'.
- **S4 reverse ratio**: axis engaged reverse: two legs −N1, k_rev = mean(|e|)/N1. asym=|k_fwd−k_rev|/max >10% → Asymmetric flag (report both ratios). k=(k_fwd+k_rev)/2; Ratio=currentRatio/k.
- **S5 slippage**: rev→fwd transition: move +M_final, solve, B2=max(0,M*k−|d|). If max(B1,B2) < 2T → B=0, no slippage. Else |B1−B2| > max(0.2*max(B1,B2), 2T) → SlippageDetected. B=(B1+B2)/2.
- **S6 close**: existing CloseLoopAgainstBaseline refactored to take measured signed response per logical unit; closing clamp raised to 3*calibrationStep (staged residuals can exceed one step). BestEffortRestore on exception unchanged (movedArcmin tracked across all stages).
- Budget guard: solve count ≤ 20/axis → else honest abort + restore. Nominal ~9-12.

## Constants (service)

InitialProbeArcmin=5, EngageEscalationFactor=3, MaxEngageAttempts=4, DetectionFloorArcmin=0.25, TargetCleanLegPhysicalArcmin=8, CleanLegSpreadThreshold=0.10, BacklashLegFraction=0.5, MaxBacklashEscalations=3, AsymmetryFlagThreshold=0.10 (replaces 0.20 geometry const for this path), SlippageRelativeThreshold=0.20, MaxSolvesPerAxis=20, MaxLegPhysicalArcmin=90.

## Outcome extensions

`AxisCalibrationResult`/`AxisCalibrationOutcome` += NoiseSigmaArcmin, ForwardRatio, ReverseRatio, SlippageDetected. VM: `CalibrationSlippageDetected` observable; `CanApply() => HasCalibrationResult && !CalibrationSlippageDetected`; slippage message in CalibrationConsistencyMessage with B1/B2 numbers.

## Tasks

- [x] Task 1: RobustFakeAxis (seeded noise, per-reversal backlash sequence, direction-dependent scale) + 11 tests; 4 behavioral RED verified on the fixed-leg service (gilas backlash, asymmetry report, slippage, noise).
- [x] Task 2: staged sequence S0-S5 in OapaCalibrationService; ComputeAxisCalibration + fixed-leg geometry helpers removed with tests (invariants moved to sequence-level); Result fields added.
- [x] Task 3: S6 iterative closing + iterative BestEffortRestore + solve budget (20) + leg cap (90' physical). All preexisting service tests green unchanged.
- [x] Task 4: VM Apply gate on slippage (CanApplyCalibration), slippage/asymmetry messages with values, internal solver boundary for tests; 3 production-path VM tests via FakeRig.
- [x] Task 5: 94/94, commits 76f29c4 + 181760c, branch pushed. Version bump/changelog deferred to PR opening (after #16/#17 merge).

Status notes (update as you go):
- 2026-07-30: plan written, branch created.
- 2026-07-31: A complete on feat/oapa-robust-calibration (76f29c4 staged sequence, 181760c VM gate). 94/94.
- 2026-07-31: B complete on feat/oapa-backlash-modes (a820d86 integration merge #16(A)+#17 with the Y-clearing/TryFineNudge composition done, 0e1152a modes). 137/137. Base-VM hook: virtual ExecuteRelativeMove (legacy default, OAPA mode plan); sub-compensation skip superseded and removed; Apply sets recommended mode (Michele's decision). NO PRs opened yet - Michele: wait for #16/#17 merge first.
- 2026-07-31: C complete on feat/oapa-parameter-provenance (f90f1db, stacked on B). 146/146. Provenance Default/Manual/Calibrated per parameter (4 settings), clamps (backlash 0-90', factor 1-100000, kills the 20600 case), two-step Apply over manual values with named replacements, provenance labels in the panel.
- 2026-07-31: D2 complete on feat/oapa-ux-quickfixes (b0b2c2c, XAML only): manual controls visible-but-disabled with explanation, first-movement checklist expander, refraction hint always visible (option a: default stays OFF - Michele did not object to the recommendation). E complete on feat/oapa-precision-finish (69d1469): opt-in PrecisionFinishMode, ErrorEstimateAverager (window 4, activation <2', reset on every automated move), controller keeps raw estimates, both estimator paths filtered. 152/152.
- rc9 PERIMETER COMPLETE. Branch stack for future PRs (NONE opened - waiting for Stefan to merge #16/#17): D1 fix/backlash-unit-label -> A feat/oapa-robust-calibration -> B feat/oapa-backlash-modes (incl. #16+#17 integration merge) -> C feat/oapa-parameter-provenance -> D2 feat/oapa-ux-quickfixes -> E feat/oapa-precision-finish. Remaining before beta rc9 package: version/changelog per branch at PR time, beta build from the top of the stack.
