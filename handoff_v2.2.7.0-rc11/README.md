# OAPA Beta — v2.2.7.0-rc11

A small quality release on top of rc10, driven by a tester's question that turned out to be a UI wart. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2).** If you're on rc10, only the DLL needs replacing. If you're coming from rc9 or older, flash `firmware/oapa.ino` too (it brings the STOP command, honored move speeds and cooler hold defaults).

## What's new vs rc10

### Azimuth backlash, finally where you'd look for it

A tester asked "was there a backlash value for the az axis?" — and the honest answer was "yes, but hidden": it lived only in the plugin **options** page as "Azimuth backlash compensation" (the original TPPA setting), while altitude had its own field in the motor panel. Now **both motor panels look the same**: the Azimuth Motor Settings panel has its own **Backlash (')** field next to the backlash mode, with the same provenance label (*manual / calibrated*) and the same 0–90' validation. The options field still exists — same value, two views.

Also fixed: the unit label on that options field claimed "steps" — the value is and always was in **arcminutes**.

As before: you normally never type these values — **Self-Calibration measures both axes and Apply fills everything in**, including the recommended mode per axis.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. (Only if coming from rc9 or older) flash `firmware/oapa.ino` to the controller.
6. Start NINA → the Azimuth Motor Settings panel now shows a **Backlash (')** field — that's how you know rc11 is loaded.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL. If that happens, just redo the swap.

## Test plan

1. Check both motor panels show Backlash (') + Backlash mode side by side, with the small provenance hint next to values set by calibration.
2. Edit the azimuth backlash in the panel, then check the options page — the "Azimuth backlash compensation" field shows the same value (and vice versa).
3. Everything from the rc10 test plan still applies (driver currents in the log, STOP, move speeds, calibration with payload mounted).

Clear skies! 🔭
