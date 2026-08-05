# OAPA Beta — v2.2.7.0-rc14

**A backlash that costs a different amount in each direction no longer blocks Apply.** If your calibration has been ending with a slippage warning and a greyed-out Apply, this is the build to retry on — and the reason is probably not what the old message told you. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash. Everything in rc13, rc13.1 and rc13.2 is still here.

## What's new vs rc13.2

### The calibration was asking the wrong question

The self-calibration measures the play twice: once when the axis reverses one way, once when it reverses the other. Until now, any disagreement between those two numbers was treated as proof that the mechanics were slipping, and Apply was blocked.

They are two different physical quantities, not two samples of one.

Think of an altitude axis carrying a mount and a telescope. Moving down, the load crosses the play on its own — gravity does the work and the motor barely loses any motion. Moving up, the motor has to drive across the whole play and *then* lift. The two directions genuinely cost different amounts, and there is nothing wrong with the mechanism.

The field evidence is unambiguous. One tester's altitude axis, calibrated twice in the same night 40 minutes apart:

| transition | first run | second run |
|---|---|---|
| one way | 53.4' | 59.9' |
| the other way | 16.2' | 15.9' |

Three and a half times apart from each other — and each one repeating to within a few percent. A slipping clutch does not repeat to 2%. That rig was told its mechanics were unrepeatable, and its owner had to type every value in by hand, which meant losing the automatic mode selection and the overwrite protection that come with Apply.

### What the calibration says now

The disagreement is reported as **direction-dependent backlash**, with both figures shown, and **Apply stays enabled**.

Being honest about what this does and does not fix: the applied value is still the average of the two, so it is inexact for both directions. Every reversal keeps a residual, and the fine phase will need a few extra cycles. That is an imperfect compensation, not an invalid one — and it never justified withholding the **calibration factor**, which is the more valuable half of the result and is not affected by the backlash at all.

### How to tell directional backlash from real slipping

A single calibration cannot tell you. Repeatability is a property of one measurement repeated, and each transition is measured once per run.

So: **run the calibration twice** and compare each figure with itself.

- Each figure comes back close to its previous value → the axis is simply directional. Normal, especially on altitude. Nothing to fix.
- The *same* figure jumps between runs → something really is slipping. Check grub screws, belt tension and friction, with the real payload mounted.

The warning on screen now says this too.

### One correction to earlier advice

The FAQ used to say that Unidirectional mode keeps the play out of the loop entirely, whatever its value. That is true only when the two reversals cost the same. Its two legs pay one transition each, and those cancel only if they are equal — on a strongly directional axis a residual is left behind, roughly half the difference between the two figures. Unidirectional is still the better choice for a large backlash; it just is not immune. The FAQ has been corrected.

A future build will keep the two values separate and let each leg compensate its own direction, which removes the residual on both Unidirectional and Full. This build does not do that yet.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL.

## Test plan

1. **If Apply has been greyed out on your rig, run the self-calibration and apply it.** That is the whole point of this build. Tell me what the two backlash figures were.
2. **Run the calibration a second time** and send me both sets of figures. That comparison is the only thing that can separate a directional axis from a slipping one, and I would like the data from several rigs.
3. If your axis is directional, expect the fine phase to bounce and to need extra cycles. Time it if you can — how long from the start of the correction phase to the tolerance being reached.
4. Compare backlash modes on a directional axis if you have the patience: **Unidirectional** and **Full** fail differently there, and I am interested in which one you find less annoying in practice.
5. Everything from the rc13, rc13.1 and rc13.2 test plans still applies.

## Known and being worked on

- Per-direction backlash compensation (removes the residual described above) — designed, not in this build.
- The calibration does not know your platform's travel limits, so an escalating measurement leg can run an axis into its end stop. Keep the STOP button in reach during a calibration on a short-travel platform.
- Microstepping is not exposed by the plugin yet. On a very high-reduction axis, lowering it in the firmware is still the single biggest speed win available.

Clear skies!
