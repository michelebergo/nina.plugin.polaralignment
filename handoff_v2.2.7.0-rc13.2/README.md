# OAPA Beta — v2.2.7.0-rc13.2

**The self-calibration now waits for the axis to stop moving before it measures.** It never did, and on a stiff or high-friction platform that alone can make a perfectly repeatable rig look unrepeatable. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash. Everything in rc13 and rc13.1 is still here.

## What's new vs rc13.1

### The calibration was measuring a moving target

The correction loop has always waited between commanding a move and taking the next measurement — that is the **Automated adjustment settle time** in the plugin options, 2 seconds by default. The self-calibration never did: it went straight from the move to the capture.

That is fine on a mechanism that actually stops when the controller says it has stopped. It is not fine on a stiff or high-friction axis, which keeps relaxing for a moment afterwards — one tester watched the position readout creep for about a second after every stop, describing it as the axis storing energy and letting it go.

Solving into that relaxation measures a position that is not final yet. Three things follow, and they compound:

- the measured response comes out **short**, so the calibration factor is wrong
- the measured backlash includes elastic energy that has not been released yet
- and the two backlash measurements **disagree with each other** — which is exactly the condition that makes the calibration report slippage and leave **Apply greyed out**

So a rig could be told its mechanics were not repeatable when the real problem was that the measurement was taken too early.

The calibration now uses the same settle time as the correction loop. If your platform is slow to come to rest, raise **Automated adjustment settle time** in the plugin options — it now affects both.

One deliberate exception: the first step measures the plate-solve noise with the axis at rest. Nothing has moved, so it does not wait.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL.

## Test plan

1. **If your Apply has been greyed out with a slippage warning, this is the build to retry on.** Run the self-calibration again, unchanged, and see whether the two backlash figures now agree well enough for Apply to enable.
2. Watch the status line during the calibration: it should visibly pause between each move and the capture that follows.
3. The calibration will take longer than before — roughly two seconds per measurement, and there are of the order of a dozen per axis. That is the point, not a regression. If it now takes uncomfortably long, tell me and we will look at whether the settle can be shortened for your rig rather than removed.
4. If your axis is slow to come to rest, try raising **Automated adjustment settle time** in the plugin options and report whether the measured backlash becomes more consistent between runs.
5. Everything from the rc13 and rc13.1 test plans still applies.

Clear skies!
