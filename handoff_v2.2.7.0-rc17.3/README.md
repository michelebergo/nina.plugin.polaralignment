# OAPA Beta — v2.2.7.0-rc17.3

**Two safety fixes on the release-candidate track: a mechanically failing axis can no longer poison the calibration factor, and the calibration now has a hard travel budget.** Everything in rc17.2 and earlier stays. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash. **No re-calibration required** — but if an earlier pass on your rig ever produced a factor far from plausible, re-run Self-Calibration + Apply once.

## What changed

**1. The factor now comes from the direction that actually works.** A factor error is a scale: it affects both directions identically. So when the two directions respond very differently (more than 2× apart), the weaker one is measuring a malfunction — a motor stalling against gravity, slip, binding — not the scale. Previously the factor was the mean of the two, which blended a measurement with a malfunction: one field rig got a factor 1.7× too large, and recalibrating on top of it compounded the error to 3.6×. Now the stronger direction alone provides the factor, and the panel tells you what to check: **run current and speed of that axis** — a motor without torque margin loses steps against gravity.

**2. Travel budget.** The calibration may never take the axis more than **3° from its starting point** — measured on the plate solves, not on the commanded moves, so the protection holds even when the stored factor is wrong. Exceeding the budget aborts the pass with a clear message and drives the axis back to where it started. No single measuring leg can exceed 135' either.

## Test plan

1. Run a normal Self-Calibration — nothing should look different on a healthy rig.
2. If your rig has the "weak direction" symptom (one direction barely responds): the calibration should now come back with a plausible factor and a message pointing at run current/speed, instead of a factor several times too large.
3. Everything from the rc17.2 test plan still applies.

## Install / update

1. Close NINA completely.
2. `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → **Unblock** if shown.
5. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta.**

Clear skies!
