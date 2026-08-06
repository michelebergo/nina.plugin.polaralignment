# OAPA Beta — v2.2.7.0-rc14

**A backlash that costs a different amount in each direction is now compensated per direction — and no longer blocks Apply.** Plus **microstepping is settable per axis**, which is the cheapest speed you can buy on a high-reduction platform. If your calibration has been ending with a slippage warning and a greyed-out Apply, this is the build to retry on — and the reason is probably not what the old message told you. **Please don't redistribute firmware or plugin — still in beta.**

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

### What the calibration does now

Both figures are kept, applied and used. Each axis has a **Backlash +** and a **Backlash -** field, and every compensation move is planned with the play of the direction *that move travels*. Apply is no longer blocked.

That is what makes the arrival exact on a directional axis, and it is worth seeing why. A Unidirectional plan overshoots past the target and comes back, so it pays one transition on the way out and the other on the way back. Those two cancel only if each leg is given its own number. With a single averaged value they do not cancel: the residual left behind is the entire spread between the two figures — about **39 arcminutes per reversal** on the rig above. The correction loop then had to chase that residual, which usually meant reversing again, and paying it again. That is the bouncing several of you have described in the fine phase, and it was not only the excursion time.

Equal values mean a symmetric axis and behave exactly as a single value did.

### How to tell directional backlash from real slipping

A single calibration cannot tell you. Repeatability is a property of one measurement repeated, and each transition is measured once per run.

So: **run the calibration twice** and compare each figure with itself.

- Each figure comes back close to its previous value → the axis is simply directional. Normal, especially on altitude. Nothing to fix.
- The *same* figure jumps between runs → something really is slipping. Check grub screws, belt tension and friction, with the real payload mounted.

The warning on screen now says this too.

### One correction to earlier advice

The FAQ used to say that Unidirectional mode keeps the play out of the loop entirely, whatever its value. That was true only when the two reversals cost the same — which is exactly the assumption this release removes. The FAQ has been corrected.

### Microstepping, per axis

New in the Motor Settings, and the cheapest speed available on a high-reduction platform.

Steps per arcminute scale exactly with the microstep setting, so going from 16 to 4 makes an axis **four times faster at the same step rate** — and gives back torque, because microstepping trades torque for smoothness. A platform at 1000 steps per arcminute drops to 250, which is still two orders of magnitude finer than a plate solve can measure. On the rig in the example above that turns a 29-second backlash excursion into about 7 seconds.

Two things happen automatically, and both matter:

- The **calibration factor is rescaled** when you change the setting. Leaving a stale factor behind would make every commanded move wrong by that same ratio, and on a short-travel platform the first move would drive an axis into its end stop. Re-run the Self-Calibration afterwards anyway to confirm.
- The setting is **re-sent on every connection**, because the controller forgets it at power-off and would otherwise fall back to its own default with your factor still scaled for something else.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL.

## Test plan

1. **If Apply has been greyed out on your rig, run the self-calibration and apply it.** Tell me what the two backlash figures were.
2. **Run the calibration a second time** and send me both sets of figures. That comparison is the only thing that can separate a directional axis from a slipping one, and I would like the data from several rigs.
3. **Time the correction phase**, start to tolerance, and compare it with what you were getting on rc13.2. If your axis is directional this is where the change should show, and I want to know whether it does.
4. **If your platform is slow, try dropping the microstepping** — 16 to 8, or 16 to 4 — then re-run the Self-Calibration and time a run again. Tell me the before and after, and whether the motion feels stronger or rougher.
5. Everything from the rc13, rc13.1 and rc13.2 test plans still applies.

## Known and being worked on

- The calibration does not know your platform's travel limits, so an escalating measurement leg can run an axis into its end stop. Keep the STOP button in reach during a calibration on a short-travel platform.
- The two backlash figures are measured once each per calibration, so a single run still cannot prove repeatability. Running it twice is the workaround; making the calibration do that itself costs sky time and is not decided yet.

Clear skies!
