# OAPA Beta — v2.2.7.0-rc5

One targeted fix on top of rc4, driven by field feedback. **Please don't redistribute firmware or plugin — still in beta.**

> Firmware unchanged — only the DLL needs replacing. Your calibration stays valid.

## What's new vs rc4

### Runaway guard now pauses the alignment

In rc4 (and earlier), when the runaway guard halted automated adjustments it only showed an error toast — easy to miss — while the capture/solve loop kept running as if nothing had happened. From the outside it looked like TPPA looping forever.

Now when the guard trips, the alignment **pauses automatically**:

- The halt is unmissable — the loop visibly stops.
- **Resume** keeps the error display live for manual adjustment (automated corrections stay off).
- Or stop, re-run the Self-Calibration, and restart.

## Reminder: what rc4 brought (also included here)

1. **Auto-scaled max move** — the per-cycle correction limit follows the measured error (80% of current total error, floor 5'). The "Max correction per cycle" setting is now a pure safety ceiling (default 30'). No manual tuning, no restarts on large errors.
2. **Backlash clearing guard** — clearing is skipped when the commanded nudge is smaller than the compensation, eliminating the out-and-back excursions that caused error oscillation in the fine phase.

## Test plan (short)

1. Leave everything at defaults. Start TPPA from a deliberately large error (degrees). Expected: 20-30' corrections immediately, tapering automatically as it converges.
2. Watch the fine phase (< 2'): no more clearing excursions, smoother convergence to tolerance.
3. If the guard ever fires, the alignment should now pause on its own — no manual stop needed.
4. Send back the log + iteration count as usual.
