# OAPA Beta — v2.2.7.0-rc16

**If your calibration factors came out different every time — or your altitude corrections overshot and oscillated — depending on where the scope was pointing, this build is the fix.** The calibration now corrects the geometry of its own pointing, so the measured factor describes your axis instead of the patch of sky it was measured through. Everything in rc15 stays. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash.

> **Please re-run the Self-Calibration and Apply once per axis after installing.** Your stored factors keep behaving exactly as before until you do; the re-calibration is what adopts the corrected geometry. If you always calibrate pointing near the pole, your factors were already right and you will simply get the same numbers back — that, too, is worth knowing.

## What was wrong

The altitude adjuster tilts the whole rig about a horizontal east-west axis. A field at azimuth A only shows **cos(A)** of that tilt in its altitude: full toward north or south, nothing due east or west. The calibration read the raw altitude displacement of the field, so a factor measured away from the meridian came out inflated by exactly 1/|cos(A)|.

This is not hypothetical. One rig calibrated three sessions in a row pointing 20-40° from due east, and its altitude factor came out **97.5, then 202.7, then 255.0** steps per arcminute — for a mechanism whose true factor, measured from its own correction responses in the very same logs, was **73-85 the whole time**. The three "inconsistent" numbers were one healthy axis seen through three different pointings. With a factor three times too large, every correction overshoots threefold: the error changes sign each cycle, the alignment oscillates between 12' and 15', and it never closes. Every consistency check passed, because the measurements were perfectly consistent — with each other, at that pointing.

The azimuth axis had the mirror-image defect, milder and better hidden: the measured displacement was converted to an on-sky angle, which scaled every azimuth factor by cos(field altitude). An under-gain still converges — each cycle removes half the error instead of all of it — so nobody saw it; it just cost roughly twice the iterations. One rig's nine-minute correction phase should have been about four.

## What rc16 does

- **Altitude displacements are divided by the signed projection cos(field azimuth).** The factor now comes out the same wherever you calibrate. The *sign* matters too: south of east/west the same axis motion moves the field's altitude the other way, and before this release that flipped the Reverse flag depending on which side of the sky you calibrated on. Now Reverse describes your wiring again; if your flag was set from a far-side calibration, the auto-flip will quietly put it right on your first re-calibration.
- **Pointings too close to due east/west are refused before any motion**, with a message that says where to point instead. Below |cos(A)| = 0.35 — further than about 69° from the meridian or from north — there is no altitude signal left to measure, only amplified noise.
- **Azimuth displacements use the azimuth coordinate directly**, which is what a base rotation actually changes, at any altitude. Azimuth factors calibrated at high field altitudes will come out larger than before — that is the correction, not a regression.
- **The calibration log line records the pointing and its projection** (`field alt=…/az=…, proj=…`), so a factor measured through bad geometry is visible in the log right where it is born.

## Where to point while calibrating

Anywhere reasonably toward the **meridian or the pole**. Both axes are happy near the pole (the classic pointing) or at any comfortable altitude near the meridian. Avoid due east/west (no altitude signal — now refused) and the zenith (no usable azimuth signal — already refused). The FAQ has the full geometry.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Start NINA, connect, and **run the Self-Calibration and Apply, once per axis**.
6. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL.

## Test plan

1. **Re-calibrate and Apply**, and tell me the factors. If you used to calibrate near the pole they should match your old ones; if you calibrated elsewhere, expect the altitude factor to drop toward what your axis really delivers and the azimuth factor to rise somewhat.
2. **Calibrate twice from two different pointings** (say, near the pole and near the meridian at mid-altitude) and compare: the factors should now come back the same. This is the sharpest test of this release.
3. If you get the new **"too close to due east/west" refusal**, that is the guard working — slew toward the meridian and re-run, and tell me the message read clearly.
4. **Run a full alignment from a large error and time the correction phase.** Azimuth should converge in noticeably fewer cycles than rc15 on rigs that calibrate at mid-to-high field altitudes.
5. Everything from the rc15 test plan still applies, including sending the log if a calibration reports a measurement as unusable.

## Known and being worked on

- The calibration still does not know your platform's travel limits, so an escalating measurement leg can run an axis into its end stop. Keep the STOP button in reach when calibrating a short-travel platform.
- Each transition is still measured once per calibration, so a single run cannot give the backlash difference an uncertainty of its own.
- An axis with a real but modest asymmetry — below the threshold that declares it directional — keeps a residual equal to that real asymmetry.

Clear skies!
