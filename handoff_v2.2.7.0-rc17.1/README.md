# OAPA Beta — v2.2.7.0-rc17.1

**One measurement fix on top of rc17: the calibration no longer mistakes the sky's own rotation for platform motion.** This build is the release-candidate track. Everything in rc17 (always-approach-up) and rc16.1 stays. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash. **No re-calibration required** — your stored values stay valid; the next calibration you run will simply be a little more accurate.

## What this fixes

While the mount tracks, the field's RA/Dec stays put but its **Alt/Az drifts with sidereal time**. The calibration transformed every sample to Alt/Az at that sample's own wall-clock time — so a few minutes of sky rotation leaked into every displacement comparison as if the platform had moved. You have seen the symptom in your logs: the calibration's closing move against its minutes-old starting point kept reporting `closing iterations exhausted; residual 0.6'` (or several arcminutes on a slow rig) — a phantom residual that no amount of iterating could remove, because it was the sky, not the axis.

rc17.1 freezes one reference epoch per calibration pass (the first solve) and expresses every sample in it. A measured displacement now means **axis motion and nothing else**.

Credit where due: this was spotted by **Stefan Berg** while reviewing the upstream self-calibration PR.

## What you should see

1. **The closing residual after calibration should drop sharply** — this is the sharpest test of the build. If you were seeing `closing iterations exhausted`, tell me what the residual reads now.
2. On slow rigs (long calibrations), the measured responses and backlash figures get slightly cleaner — a re-calibration is worth it at your convenience, not urgent.
3. Everything from the rc17 test plan still applies (final legs always positive; first correction after a long pause; STOP mid-excursion recovery).

## Install / update

1. Close NINA completely.
2. `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → **Unblock** if shown.
5. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta.**

Clear skies!
