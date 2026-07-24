# OAPA Beta — v2.2.7.0-rc4

Changes driven directly by the rc3 field test. **Please don't redistribute firmware or plugin — still in beta.**

> Firmware unchanged — only the DLL needs replacing. Your calibration stays valid.

## What's new vs rc3

### 1. Max move is now automatic (your suggestion, implemented)

The per-cycle correction limit **auto-scales with the measured error**: 80% of the current total error, floor 5', ceiling from the "Max correction per cycle" setting.

- Large initial error → big corrections immediately, no manual tuning, no restart
- As the error shrinks, the limit shrinks with it → gentle final approach
- The setting you change in Options is now exactly what you asked about: a **pure safety ceiling** (default 30'). Leave it alone unless you want to force gentler behaviour.

Your rc3 run 2 scenario (default 5' vs a large polar error → restart) can no longer happen.

### 2. Backlash clearing guard on the fine approach

Your log from the halted run showed the cause: with 5' of azimuth backlash compensation, every direction change fired a ±5' out-and-back clearing excursion around sub-arcminute nudges — injecting more error than it removed (1.5' → 6' oscillation, which is what tripped the runaway guard). Now the clearing is **skipped when the commanded nudge is smaller than the compensation**; the adaptive controller absorbs the slop implicitly. Manual moves still always clear.

### 3. For the record: the guard did fire 😉

19:56:42 in your log: "Automated adjustments halted: total error increased for 3 consecutive measurements (now 6.22')". It caught exactly the oscillation described above — in a natural run, not the artificial test.

## Test plan (short)

1. Leave "Max correction per cycle" at default (30). Start TPPA from a deliberately large error (degrees). Expected: first corrections in the 20-30' range without touching anything, tapering as it converges.
2. Watch the fine phase (< 2'): expected no more ±5' clearing excursions on X (log will say "skipping backlash clearing" instead), smoother convergence to tolerance.
3. Send back the log + iteration count as usual.

Also worth noting from your rc3 data: the two calibrations agreed within ~4% (X 14.91 vs 14.31, backlash 5.05' vs 5.12') — the altitude-dependence fix is confirmed reproducible. Thanks for the excellent testing!
