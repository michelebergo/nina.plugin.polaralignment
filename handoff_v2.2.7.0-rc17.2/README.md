# OAPA Beta — v2.2.7.0-rc17.2

**Robustness release on the release-candidate track: a calibration that fails or gets cancelled can no longer leave the platform silently displaced, and a calibration that could not verifiably return to its starting position now says so.** Everything in rc17.1 (sidereal fix), rc17 (always-approach-up) and rc16.1 stays. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash. **No re-calibration required.**

## What changed

Three honesty fixes around the calibration's ending, all found by **Stefan Berg** reviewing the upstream self-calibration PR:

1. **The restore now always runs when the axis has physically moved.** Previously it was skipped if the *commanded* total happened to be zero at the moment of a failure — but with backlash the commanded sum returns to zero while the mechanism is still off its baseline. Narrow window, real consequence: a mid-calibration failure could leave your polar alignment shifted without a word.
2. **"Measured" and "back at the start" are separate claims now.** If the closing moves fail, can't be verified, or leave a residual above tolerance, you keep the measured factors — they're valid — but the panel, the notification, and the log all tell you the platform did **not** verifiably return to its starting position, and by how much. Re-check your alignment before imaging in that case.
3. **Cancelling stays a cancellation.** Hitting STOP during the closing phase still drives the axis back, but the run reports "Cancelled" instead of dressing up as a success.

## Install / update

1. Close NINA completely.
2. `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → **Unblock** if shown.
5. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta.**

## Test plan

1. Run a normal Self-Calibration: the result line now ends with the restore verdict (`restored: X=True (0.12'), Y=True (0.31')`).
2. If you feel adventurous: hit STOP mid-calibration — the axis should drive back to where it started and the panel should say "Cancelled".
3. Everything from the rc17.1 test plan still applies (closing residuals should be visibly smaller than pre-rc17.1 builds).

Clear skies!
