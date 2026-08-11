# OAPA Beta — v2.2.7.0-rc17

**Unidirectional mode now truly is unidirectional: every altitude move arrives travelling up, against gravity, and the axis always rests with its gears loaded on the same flank.** Suggested by a beta tester — thank you. Everything in rc16.1 stays. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash. No re-calibration required for this one: the change is in how moves are planned, not in what is measured.

## What changed and why

Until now the final-approach direction of a Unidirectional move followed the axis' history: downward corrections finished moving up, but upward corrections after a reversal finished moving down, and an interrupted move or a manual nudge could flip a rig into the mirrored regime — where *every* final approach is downward — and keep it there for the rest of the night. A field log from 2026-08-11 shows exactly that happening right after a cancelled move.

rc17 pins the regime. In Unidirectional mode:

- **Downward moves always overshoot below the target and come back up** — including repeated downward moves that previously went direct.
- **Upward moves approach directly**; an upward move that finds the axis engaged downward (after an interruption or a manual nudge) re-engages in one compensated leg.
- After **every** move the axis rests loaded upward, against gravity.

Three things follow:

1. **No settling into slack.** A gravity-loaded axis that arrives moving down rests on the free flank, and the mechanism can creep through its own play afterwards — one tester watched that happen. Arriving loaded makes the at-rest position mechanically defined.
2. **Every arrival is identical** — same flank, same direction. A compensation error then shows up as a *constant* offset, which the adaptive correction loop learns and removes; the old alternating-direction error was invisible to it.
3. **One transition is what matters.** The final positioning only ever crosses the entering-up play, which is exactly the quantity your calibration measures best.

Cost: a downward move that is not a reversal now pays the out-and-back excursion (about twice the entering-up play, plus margin) — a few seconds on a typical axis.

Soft and Full modes are unchanged.

## Install / update

1. Close NINA completely.
2. `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → **Unblock** if shown.
5. Firmware: nothing to do — still 1.2.2. Calibration: your stored values remain valid.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta.**

## Test plan

1. **Run an alignment as usual.** In the log, every Unidirectional altitude plan should now end with a positive leg — including consecutive downward corrections.
2. **The first correction after a long tracking pause** is the sharpest test of the loaded-at-rest property: it should land exactly, with no sign of the axis having crept during the pause.
3. **Cancel a move mid-plan on purpose** (STOP during an excursion), then continue: the next moves must recover cleanly — the mirrored-regime trap this release removes.
4. Everything from the rc16.1 test plan still applies.

Clear skies!
