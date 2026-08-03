# OAPA Beta — v2.2.7.0-rc12

The UX round: three small things the field asked for in a single day, plus a fix for a firmware source that refused to compile on some machines. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — but if you had trouble compiling the `.ino` from an earlier package, take the one in this zip: the source itself was the problem, not your setup.

## What's new vs rc11

### 1. Apply says what it wants

When a calibration would replace values you typed by hand, Apply asks for confirmation instead of overwriting them — but until now it asked only in the status line. In the field that reads as "nothing happened": a tester pressed Apply once, moved on, and ran the whole night on the previous night's numbers without knowing. The button now **changes to "Apply again to confirm"** while it waits, and goes back to "Apply" once you confirm or discard.

### 2. The azimuth backlash is no longer in two places

It never was a duplicate — it was one value shown twice, which is why editing either one updated the other. Now, with OAPA selected, it lives **only in the Azimuth Motor Settings panel**, next to its backlash mode, exactly like altitude. The plugin options keep the field for the Avalon UPAS system, which has no motor panel of its own.

### 3. Speed now tells you what it means

The field is labeled **"Speed (steps/s)"** and shows the physical rate next to it, derived from your calibration factor — e.g. `~ 74.5 '/s`. The same step rate is a very different sky speed on each axis: on one tester's rig, 1000 steps/s is about **74 '/s in azimuth but only 8.6 '/s in altitude**. The hint makes that visible at a glance instead of surprising you mid-run. (The hint stays empty until a calibration factor exists — inventing a reading from the default would be worse than showing none.)

### 4. The firmware source compiles everywhere now

One tester could not compile the shipped `.ino` (`stray '\255' in program`) while two others compiled the same file fine. The source carried a UTF-8 byte-order mark and a few non-ASCII characters in its comments; whether they survive the zip → Windows unzip → Arduino IDE trip depends on the local environment. The file is now **pure ASCII with no BOM** — comments only, so the compiled firmware is byte-for-byte the same behaviour and still reports **1.2.2**.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Firmware: nothing to do if you already flashed 1.2.2. Coming from rc9 or older, flash `firmware/oapa.ino`.
6. Start NINA → the Speed field reads "Speed (steps/s)" — that's how you know rc12 is loaded.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL.

## Test plan

1. Type a backlash value by hand, run Self-Calibration, press **Apply once**: the button must change to "Apply again to confirm" and nothing may change until you press it again.
2. Check the azimuth backlash appears in the motor panel only (and that the plugin options no longer show it while OAPA is selected).
3. Look at the speed hint on both axes — the two numbers should differ roughly by the ratio of your two calibration factors.
4. Everything from the rc10/rc11 plans still applies (driver currents in the log, STOP, calibration with payload mounted).

Clear skies! 🔭
