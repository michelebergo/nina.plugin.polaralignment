# OAPA Beta — v2.2.7.0-rc10

Everything from rc9 (robust self-calibration, slippage detection, backlash modes, parameter protection, precision finish) **plus a bench-testing session's worth of hard fixes**: motor currents that actually reach the driver, a real STOP button, and movement speed that finally does what the field says. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware updated: 1.2.2 — reflash recommended.** The plugin fixes work with 1.2.0 too, but the STOP button and the movement speed setting need the new firmware.

## Why this release exists

A bench session with a brand-new build surfaced three things at once. First, the motor **run/hold current settings never reached the driver**: the plugin and the firmware disagreed on the command grammar, the firmware politely answered "ok" to commands it didn't understand, and every motor ran at the 600 mA default — weak, whatever you typed. Second, once a wrong target was entered there was **no way to stop** a move. Third, the movement **speed setting was silently ignored** — the firmware ran every move at a fixed profile. All three are fixed, and a new class of test now pins the plugin's wire commands against the firmware's parser so "acknowledged but ignored" can't happen again.

## What's new vs rc9

### 1. Motor currents that actually apply

- Run (mA) and Hold % now reach the driver — set them in the motor panels and the torque changes for real.
- The stored values are **re-applied automatically on every connection** (the controller forgets them at power-off; before, they were only sent when you edited the field while connected).
- Every driver command is logged with the firmware's response, so the NINA log shows exactly what was applied:
  `Driver config: set Y run current -> CY1200 (response: ok)`

For a typical NEMA 17 (e.g. 1.5 A rated): Run 1000–1200 mA, Hold 20–40% is a good starting point.

### 2. STOP button

Below the manual controls: **■ STOP** decelerates both axes to a controlled halt (positions stay truthful — no lost steps). It is deliberately available even while automated adjustments are running or a manual move with a mistyped target is underway — exactly the moments you need it. Requires firmware 1.2.1; on older firmware it is safely ignored.

### 3. Movement speed is honored

The speed you set for manual moves is now applied per move (clamped to a safe range in the firmware). With high calibration factors this matters a lot — a "small" move can be millions of steps, and before rc10 it always ran at the same fixed rate no matter what you configured.

### 4. One field lesson for new builds

If a motor runs **rough, noisy and weak**: check the coil pairing at the connector before anything else. Each of the driver's two output pairs must connect to one motor coil (verify with a continuity tester: the two wires of a coil show a few ohms between them). Adjacent connector pins belonging to *different* coils produce exactly this symptom — and no current setting will fix it.

## Firmware

**1.2.2** — flash `firmware/oapa.ino` (Arduino IDE or PlatformIO, same procedure as always: close any serial monitor afterwards or the COM port stays busy). Changes: the jog feed rate is applied per move (was ignored), the new `!` command stops both axes, and the default **hold current is lowered to 25%** (was 50%) — the hold current flows continuously from power-on, including before the plugin connects, and was keeping motors warm at rest. Protocol otherwise unchanged; older plugins keep working.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Flash `firmware/oapa.ino` to the controller.
6. Start NINA → the OAPA panel shows the **■ STOP** button below the manual controls — that's how you know rc10 is loaded.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL. If that happens, just redo the swap.

## Test plan

1. Connect and check the NINA log (`%LOCALAPPDATA%\NINA\Logs`): right after the firmware version line you should see four `Driver config: apply stored driver configuration` lines with `response: ok`.
2. Set a run current, feel the holding torque change at standstill (fingers on the shaft at 100 vs 1200 mA — the difference is unmistakable). Don't watch your bench supply instead: the driver is a switching regulator, so the supply current barely moves even when the coil current doubles.
3. Start a long manual move and press **STOP** — both axes must decelerate to a halt and the move must end without an error.
4. Change the manual movement speed and verify the axis actually moves faster/slower.
5. Everything from the rc9 test plan still applies (self-calibration with payload mounted, backlash modes, precision finish).

Clear skies — and as always, the logs are gold.
