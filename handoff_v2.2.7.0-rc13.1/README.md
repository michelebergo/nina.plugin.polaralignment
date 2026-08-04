# OAPA Beta — v2.2.7.0-rc13.1

A one-fix release on top of rc13: **the motor speed dropdown now goes up to 3000 steps/s**, which is what the firmware has accepted all along. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash. Everything in rc13 is still here; this only widens one dropdown.

## What's new vs rc13

### The speed dropdown was capping you at a third of the available range

The list offered 100 to 1000 steps/s. The firmware accepts **50 to 3000**.

That gap was harmless for most of the plugin's life, because until firmware 1.2.1 the speed value was parsed and thrown away — every move ran at a fixed internal rate no matter what the panel said. rc10 made the firmware honour it. The dropdown, inherited unchanged from before OAPA existed, quietly became a ceiling instead of a preference, and nobody went back to widen it.

It matters most on a heavily reduced axis. On a rig whose altitude runs at 1000 steps per arcminute, 1000 steps/s is **0.6 arcmin per second** — a single backlash-compensated reversal of 39 arcmin then takes over a minute, and a calibration takes half an hour. At 3000 that becomes 3 arcmin per second, and there is no way to reach it from the panel, because the dropdown cannot be typed into.

New values available: **1250, 1500, 1750, 2000, 2500, 3000**.

The speed hint added in rc12 already told you the physical rate next to the field. It was diagnosing a problem the panel would not then let you fix.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Firmware: nothing to do — still 1.2.2.
6. Start NINA → open the OAPA panel → the speed dropdown should now list values above 1000.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL.

## Test plan

1. Open the speed dropdown on both axes and confirm the new values are there, up to 3000.
2. Raise the speed on your most heavily reduced axis and watch the hint beside the field — the physical rate should scale with it.
3. Make a manual nudge at the higher speed and confirm the axis still stops where it should, with no missed steps and no stall. If an axis loses position or stalls at 3000, drop back a step and tell me the value where it becomes reliable — that number is useful.
4. Everything from the rc13 test plan still applies (error readout tracking the alignment window, falling back to dashes after ~90 s, connect lines reporting real per-axis parameters).

Clear skies!
