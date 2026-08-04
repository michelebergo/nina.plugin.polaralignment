# OAPA Beta — v2.2.7.0-rc13

The visibility round: the panel now shows you the alignment error it's correcting, the connect-time log tells you what parameters the plugin is using to compute its moves, and the log stops looking like it contradicts itself when you toggle automated adjustments mid-run. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash this round.

## What's new vs rc12

### 1. The panel shows the live alignment error

Above the manual controls, the OAPA panel now has a small readout: **azimuth, altitude and total error**. It's fed by the same measurements the alignment window uses, so a nudge and its effect on the error are visible without switching windows. The values are **live**, not a snapshot — if 90 seconds pass without a new measurement (run stopped, window closed, no solves), the readout falls back to em dashes instead of showing a stale number.

### 2. Connect-time log reports the parameters the plugin is computing moves with

When the controller connects, the log now lists both axes' **gear ratio, backlash, backlash mode and speed**, plus the **sky rate that speed implies**. These are the plugin-side values the panel already shows you — logging them makes a support log self-sufficient: the `$J=` step counts that follow can be read back as arcminutes without guessing the ratio, no cross-referencing the panel required.

### 3. Toggling automated adjustments mid-run is now logged

Turning automated adjustments on or off during a run now writes a log line. Previously the log's own header could say one thing (adjustments on) while the actual behavior for part of the run was the opposite, with nothing in between explaining the switch.

### 4. Three new FAQ entries

Covering where the OAPA instruction belongs in an advanced sequence, confirming that no manual pre-correction is required regardless of the starting error, and which backlash mode to pick.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Firmware: nothing to do — still 1.2.2.
6. Start NINA → open the OAPA panel: the new error readout at the top (showing em dashes until a measurement arrives) is how you know rc13 is loaded.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL.

## Test plan

1. Start an alignment run (manual or automated) and watch the panel's error readout: it should update as solves come in and track the same azimuth/altitude/total numbers the alignment window reports.
2. Stop the run (or close the alignment window) and wait: within about **90 seconds**, the readout should fall back to em dashes rather than keep showing the last number.
3. Reconnect the controller and check the log's connect lines: each axis should report its **real** gear ratio, backlash, backlash mode and speed (and implied sky rate) — not defaults or the other axis's values.
4. If you have a rig you have **not yet calibrated**, please paste the connect-time log line here **verbatim** — that is the fastest way to confirm the sky-rate figure stays suppressed until a real gear ratio exists.
5. Everything from the rc10/rc11/rc12 plans still applies (driver currents in the log, STOP, calibration with payload mounted, backlash in the motor panel only, speed hint).

Clear skies!
