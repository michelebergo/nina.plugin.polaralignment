# OAPA Beta — v2.2.7.0-rc17.4

**Three fixes: the session survives a USB-serial dropout, automated adjustments pause themselves when the controller stops responding, and the legacy backlash clearing now physically moves the mechanism.** Everything in rc17.3 and earlier stays. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash. **No re-calibration required.**

## What changed

**1. Serial dropout recovery.** If the USB-serial link dies mid-move (a stalling stepper makes exactly the kind of electrical noise that re-enumerates USB adapters), the plugin now reopens the port on a bounded schedule (1s/2s/3s) and repeats the interrupted transaction. A single dropout recovers invisibly; a controller that is really gone still fails, with the story in the log.

**2. Self-pausing corrections.** Three consecutive failed move commands pause the automated adjustments with a clear notification — check the USB cable and power, then re-enable to resume. No more correction loop grinding errors at a dead port. The error display stays active for manual adjustment.

**3. Legacy backlash clearing.** After a direction change, the old clearing sequence was a zero-sum pair (−B, +B): both legs were consumed by the very play they were supposed to clear — net physical motion zero. It is now a single further move in the new direction, recovering exactly what the reversal lost. (Identified by **Stefan Berg** in upstream review; affects the Avalon UPAS path and absolute moves — OAPA relative nudges already went through the backlash modes.)

## Install / update

1. Close NINA completely.
2. `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → **Unblock** if shown.
5. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta.**

Clear skies!
