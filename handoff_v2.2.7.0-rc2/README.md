# OAPA Build — v2.2.7.0-rc2 (Hand-off)

Thanks for the rc1 field test — excellent data! Your **0.27' (16") at target 0.35** proves the approach; and your az-runaway report on run 2 exposed a genuine systematic bug that rc2 fixes. Details below.

> Same base as rc1 (official 2.2.6.3 + our work). Firmware unchanged. Version still reads **2.2.7.0** in NINA — check the DLL date or the calibration behaviour (§2.1) to confirm rc2 is active.

---

## 1. Installation

Same as rc1: close NINA, replace `NINA.Plugins.PolarAlignment.dll` in
`%localappdata%\NINA\Plugins\3.0.0\Three Point Polar Alignment\`, restart.

**Then re-run Self-Calibration — mandatory this time** (see §2.1 for why your current calibration values are wrong).

---

## 2. What your test found, and what rc2 does about it

### 2.1 The run-2 azimuth runaway — root cause found (thanks to your numbers!)

Your two calibrations gave wildly different X values (factor **52.34** vs **16.46**, backlash **11.71'** vs **1.23'**) while Y stayed consistent (133.67 vs 128.85). That pattern was the smoking gun:

**A base rotation of θ in azimuth moves a field at altitude h by only θ·cos(h).** Your first calibration ran with the scope near the zenith (field at ~79° altitude, cos = 0.19), so the sky barely moved and the factor/backlash came out ~5× inflated. Run 2 then fired an 11.7' backlash-clearing sequence on every direction change — that's what pushed the azimuth away.

**rc2 corrects all azimuth measurements by cos(field altitude)**, so the discovered factor and backlash no longer depend on where the scope points. It also refuses to calibrate the azimuth axis when the field is above ~75° altitude and tells you to point toward the pole.

### 2.2 Calibration no longer moves before it can solve

Your first three calibration attempts failed on an unsolvable field near the pole (Dec 89°51', "Not enough stars") — and each attempt had already moved the axis by the priming step before failing. rc2 does a **baseline solve before any motion** (fail = zero cost) and, on any mid-sequence failure, **drives the axis back** to where it started.

### 2.3 Unreliable-measurement warning

Your X legs disagreed by 54% (forward 18.44' vs reverse 11.96') — a sign the measurement itself was bad. rc2 flags any leg mismatch above 20% in the result panel: *"discovered values may be unreliable, re-run pointing at a lower-altitude, star-rich field."*

### 2.4 Runaway guard in the correction loop

Even with good calibration, if automated corrections make the total error worse for **3 consecutive measurements**, rc2 halts the motors with an error notification instead of chasing the error away from the pole. The error display stays live for manual adjustment.

---

## 3. Re-test plan (focused, ~20 min of sky time)

1. **Re-run Self-Calibration pointing roughly toward the celestial pole** (the region TPPA uses anyway). Expected: X factor near **16.5** and X backlash near **1.2'** — i.e. matching your *second* rc1 calibration, now reproducible from any starting field. Try it once at a mid-altitude field too: values should now come out the same (±few %).
2. Try calibrating with the scope near the zenith. Expected: refusal with a clear message, axis does not move.
3. Full alignment, tolerance 0.35': expected same-or-better convergence as your 16-solve run.
4. (If you're brave) Restore a deliberately wrong backlash (e.g. 10') in Options and run: expected the runaway guard halts motors within 3 worsening cycles with the "Automated adjustments halted" notification. Then re-Apply the good calibration.

Send back the same artifacts as last time (log, calibration screenshot, final numbers). If §3.1 and §3.3 pass, this goes into the PR to the official repository with your results attached.

---

## 4. Reverting

Same as rc1 — restore the `.dll.bak`. If you applied an rc2 calibration, note the values are in *axis units* now: they remain correct for rc2+ but would over-correct on rc1/older builds pointing away from the pole.
