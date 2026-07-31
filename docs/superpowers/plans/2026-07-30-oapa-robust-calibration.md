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

- [ ] Task 1: FakeAxis extensions (noise seeded deterministic, per-reversal backlash variation for slippage, direction-dependent responseScale for asymmetry) + gilas regression test written and RED against current service.
- [ ] Task 2: staged sequence in OapaCalibrationService (S0-S5), Result fields; adapt existing service tests (declare changes); suite green incl. gilas.
- [ ] Task 3: S6 closing refactor + budget guard + excursion caps; restore tests green.
- [ ] Task 4: VM Apply-block on slippage + slippage/asymmetry messages + tests.
- [ ] Task 5: full suite, commit per task, push branch. Version bump/changelog deferred to PR opening.

Status notes (update as you go):
- 2026-07-30: plan written, branch created. Nothing implemented yet.
