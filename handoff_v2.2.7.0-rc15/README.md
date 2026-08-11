# OAPA Beta — v2.2.7.0-rc15

**If your alignment corrects nicely down to a certain error and then simply stops improving, this build is the fix — and the bug was mine, introduced in rc14.** Everything else in rc14 stays: the per-direction backlash, the microstepping, the unblocked Apply. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash.

> **Please re-run the Self-Calibration and Apply after installing.** rc15 resets the per-direction backlash your rig has stored, for the reason below. Until you re-calibrate, both axes behave symmetrically — which is correct, just not as sharp as it can be.

## What went wrong in rc14

rc14 started measuring the play separately in each direction, which is right: an axis carrying its load against gravity really does cost different amounts to reverse each way. What it got wrong was **when** to use the two figures separately.

A Unidirectional reversal is two legs — overshoot past the target, come back. Written out, the axis travels:

```
move − outward + back
```

The missing part is supposed to be eaten by the mechanism's own play. So if the two configured figures differ, and the mechanism does **not** actually lose that difference, the gap between them is added to **every single reversal**. Two consequences, and they are unforgiving:

- the axis **cannot be corrected by less than the gap**;
- a request **smaller than the gap moves the axis the wrong way**.

rc14 wrote both measured figures into the pair unconditionally — including on axes where the calibration had *already decided* the two directions do not differ, and printed `directional=false` in the very same log line. On two rigs that produced a gap of 9.3' and 7.3', and both of them stalled at exactly their own gap. One log walks right down through the threshold:

| requested | delivered |
|---|---|
| -9.68' | -0.3' |
| -9.49' | -0.13' |
| -9.33' | -0.07' |
| -9.22' | **+0.03'** ← wrong way |
| -9.16' | **+0.13'** ← wrong way |

That rig's configured pair was 54.34' and 45.02'. The difference is 9.32'.

## Why a single value never had this problem

Here is the part worth keeping, because it is not obvious. With one value used for both legs, the *magnitude* of the backlash cancels against itself: the plan lands on target **no matter how much play the mechanism really has**. An overestimated symmetric value costs you excursion time and nothing else. That robustness is exactly what splitting the pair gives up.

So the trade is:

- averaging a genuinely directional pair can only ever be wrong by **the asymmetry the axis actually has**, which physics bounds;
- splitting an unestablished pair is wrong by **the measurement error**, which nothing bounds.

**A wrong difference is worse than a wrong average.** rc15 therefore splits the pair only when the calibration has established that the difference is real, and uses the mean when it has not. On a rig with a genuinely directional axis nothing changes; on the rigs that stalled, the floor disappears.

The threshold deciding this was already in the code and already correct on every field case we have — it just wasn't being read by the next stage.

## Also in this build

**A backlash transition that comes back impossible is now refused instead of applied.** If a reversal travels *further* than the response predicts — a preload letting go, a stick-slip release — the old code clamped it to a plausible-looking "no play this way", and paired against a real value in the other direction that zero became tens of arcminutes of compensation made of nothing. One calibration in the field reported `0.00'/27.21'` and offered it. Now the pair is reported as zero on both directions and the calibration says so.

**A calibration whose two directions respond differently by more than a factor of two is now called out as unusable.** Field case: `forward 0.860 / reverse 0.102` on an altitude axis. The mean of those is not a compromise, it is wrong for both — and that pass produced a calibration factor three times what the axis actually delivered during the corrections that followed, with no warning at all.

**The engaged direction of each axis now survives a new run.** The alignment instruction builds a fresh controller object every time it executes — one session shows four of them — while the mechanism obviously holds still. Every run used to start out assuming both axes were engaged in the positive direction. On an axis with tens of arcminutes of play, that turns the first correction of a run into a whole injected backlash: one log shows a commanded -2.47' producing a 43' swing.

**Every message now prints both directions.** The panel, the notification and the log all said "the measured backlash: 54.34'" while 54.34'/45.02' was what got applied. That single half-truth is the reason this took three field sessions to find instead of one minute. The plan line also reports its own net travel now:

```
OAPA backlash mode Unidirectional on XAxis: move -9.33' planned as [-68.44, 68.43] (net -0.01')
```

A net that drifts away from the requested move, or flips its sign, means the configured pair is asking the mechanism for play it does not have. That is the whole diagnosis, in one line.

## One correction to earlier advice

The rc14 notes and the FAQ said that lowering the microstepping "gives back torque". That is only true at constant shaft speed. If you use the change to go four times faster on the sky, the motor also spins four times faster, and stepper torque falls with speed — so treat *faster* and *stronger* as two separate experiments: change the microstepping first while keeping the same sky speed, and only then raise the step rate. Coarser microstepping is also coarser motion, so if your axis is prone to stick-slip it can make that worse rather than better. Watch the leg-to-leg spread the calibration reports, before and after. FAQ corrected.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Start NINA, connect, and **run the Self-Calibration and Apply**.
6. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL.

## Test plan

1. **If your alignment was stopping short of tolerance on rc14, run the same alignment again** and tell me whether it closes. That is the sharpest test of this release.
2. **Re-calibrate and Apply**, then tell me the two backlash figures per axis and whether they came back equal or different. Equal means the calibration judged your axis symmetric — that is a result, not a failure to measure.
3. **Run the calibration a second time** and send both sets. Comparing each figure with itself is still the only way to separate a directional axis from a slipping one.
4. **Time the correction phase**, start to tolerance, against what you were getting before.
5. If the calibration tells you a measurement was unusable, **send me that log** — those two new refusals are the least field-tested part of this build.
6. Everything from the rc13 and rc14 test plans still applies.

## Known and being worked on

- The calibration still does not know your platform's travel limits, so an escalating measurement leg can run an axis into its end stop. One session escalated a leg to 111 arcminutes. Keep the STOP button in reach when calibrating a short-travel platform.
- Each transition is still measured once per calibration, so a single run cannot give the difference an uncertainty of its own. Measuring each one twice would fix both that and the slipping-versus-directional question, at the cost of two extra solves and one extra excursion per axis. Not decided yet.
- An axis with a real but modest asymmetry — below the threshold that declares it directional — keeps a residual equal to that real asymmetry. It is bounded by the mechanics rather than by measurement error, which is the point, but it is not zero.

Clear skies!
