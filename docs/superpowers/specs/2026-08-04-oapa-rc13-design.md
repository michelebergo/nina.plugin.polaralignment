# OAPA beta rc13 — design

Date: 2026-08-04
Branch: `beta/rc9` (local beta stack, on top of rc12 `9f4c218`)

## Purpose

Three items, driven by what the beta group hit during the nights of 2026-08-03
and 2026-08-04:

1. A live polar-alignment error readout inside the OAPA control panel, so manual
   nudging no longer requires switching between the plugin options page and the
   alignment window.
2. Two log additions that close the diagnostic gaps which made the 2026-08-03
   field log expensive to analyse.
3. FAQ answers for three questions that arrived from three different testers in
   one week and that the current FAQ does not cover.

## Constraints

rc13 stays in the local beta. It does not go upstream until PR #16 and #17 merge.

Files are classified by whether they collide with the two PRs awaiting review:

| File | Status |
| --- | --- |
| `PolarAlignment/OAPA/OAPAControlPanel.xaml` | contested — #16 rewrites it |
| `PolarAlignment/OAPA/UniversalPolarAlignmentOAPAVM.cs` | contested — #16 and #17 both touch it |
| `PolarAlignment/PolarAlignmentPlugin.cs` | contested — #16 and #17 both touch it |
| `PolarAlignment/OAPA/UniversalPolarAlignmentOAPA.cs` | free |
| `PolarAlignment/FAQ.md` | free |
| `PolarAlignment/OAPA/AlignmentErrorMonitor.cs` | new file |
| `PolarAlignment/OAPA/OapaParameterSummary.cs` | new file |
| `NINA.Plugins.PolarAlignment.Test/AlignmentErrorMonitorTest.cs` | new file |
| `NINA.Plugins.PolarAlignment.Test/OapaParameterSummaryTest.cs` | new file |
| `NINA.Plugins.PolarAlignment.Test/OapaXamlContractTest.cs` | free |

`PolarAlignmentPlugin.cs` is unavoidable: it is the only construction site of the OAPA
view model, so it is the only place the message broker can be injected from. The
broker parameter on the view-model constructor is optional with a `null` default, which
keeps the seventeen existing five-argument call sites — five of them in files belonging
to PR #16 and #17 — untouched.

**Must not be modified by rc13:**

- `PolarAlignment/UniversalPolarAlignmentBaseVM.cs` — shared with the Avalon UPAS
  path, and touched by both PRs.
- `PolarAlignment/Avalon/UniversalPolarAlignmentVM.cs` — UPAS must be unaffected
  by this change. This is an explicit product decision, not an accident of
  scoping: the readout is an OAPA feature only.
- `PolarAlignment/Instructions/PolarAlignment.cs` and `PolarAlignment/TPAPAVM.cs` —
  both belong to PR #17.

## Item 1 — live error readout

### What already exists

The alignment instruction publishes `PolarAlignmentErrorMessage` on topic
`PolarAlignmentPlugin_PolarAlignment_AlignmentError` every time the continuous
estimate is stable (`Instructions/PolarAlignment.cs:611-618`). Its `Content` is an
anonymous object carrying `AzimuthError`, `AltitudeError` and `TotalError`, all in
**degrees**.

Nothing in the OAPA path subscribes to it. `UniversalPolarAlignmentOAPAVM` is not
an `ISubscriber`; the only subscribers today are `DockablePolarAlignmentVM` and
the instruction itself.

The OAPA control panel is embedded in the plugin options page
(`Options.xaml:2336`) and contains the manual nudge buttons but no error display.
That is the whole of the reported problem: buttons in one place, numbers in
another.

### Component: `AlignmentErrorMonitor`

A standalone class with no WPF dependency, implementing `ISubscriber`. It owns the
broker subscription and nothing else. It follows the extraction pattern already
used in this codebase for `OapaCalibrationService`, `BacklashModePlanner`,
`ConvergenceMonitor`, `AutoFinishGate` and `RunawayPauseGate`.

Subscribes to one topic: `PolarAlignmentPlugin_PolarAlignment_AlignmentError`.

Exposes:

- `double? AzimuthErrorArcmin`
- `double? AltitudeErrorArcmin`
- `double? TotalErrorArcmin`
- `bool HasLiveError`

and raises a change callback that the view model forwards as
`RaisePropertyChanged`.

Degrees-to-arcminutes conversion happens in the monitor, so the view model and the
XAML deal only in arcminutes, consistent with the rest of the panel.

The payload is an anonymous type. Since the monitor lives in the same assembly it
could be read with `dynamic`, but the monitor reads it by reflection on the three
property names instead: an unreadable or malformed payload must leave the previous
state untouched rather than throw inside a broker callback. Redefining the payload
as a named type would be cleaner and is explicitly **not** done here, because that
would modify `Instructions/PolarAlignment.cs`, a PR #17 file.

### Staleness: expiry, not an end-of-run signal

The readout must never show a value that is no longer live. A user nudging by hand
against a twenty-minute-old number is correcting toward a target that no longer
exists.

There is no end-of-run signal available on the broker. The instruction's `finally`
block reports the terminating empty status through `externalProgress`
(`Instructions/PolarAlignment.cs:770`), which is the caller's progress object, not
the wrapper that publishes to the broker. No subscriber ever sees it.

The monitor therefore expires by inactivity: if no error message has arrived for
**90 seconds**, all three values return to `null` and `HasLiveError` becomes false.

90 seconds is derived from the 2026-08-03 field log. Solve cycles there ran at 6 to
10 seconds, but a single unidirectional move with backlash compensation occupied
the axis for 50 seconds with no solve in between, so the threshold must sit well
clear of that. Expiry also covers cases an end-of-run signal would miss: the
alignment window closed mid-run, a crash, a disconnected cable.

The monitor takes its clock as a constructor dependency rather than reading
`DateTime.UtcNow` directly, so expiry is testable without real waits. This is the
only structural consequence of the staleness decision.

### UI

A three-value block at the top of the OAPA control panel, above the per-axis
controls, so it is visible regardless of whether the nudge buttons are enabled.

- Labels: Azimuth, Altitude, Total. Values in arcminutes.
- Azimuth and altitude keep the sign the publisher sends; the user needs to know
  which way to nudge, which is the entire point of putting the numbers next to the
  buttons. Total is a magnitude and is always positive.
- Before the first measurement, and after expiry, each value shows an em dash.
- No refresh timer in the UI. The display updates when a message arrives and when
  expiry fires.

## Item 2 — diagnostics

Both additions are chosen from what was actually missing when analysing the
2026-08-03 log, not from a general desire for more logging.

**OAPA parameters at connect.** The driver configuration commands are already
logged at connect (`CX600`, `HX50`, and so on) but the calibration factor,
backlash compensation, backlash mode and speed per axis are not logged anywhere.
Reconstructing that a tester was running 62 arcmin of altitude backlash required
deriving it from the arithmetic of the `$J=` commands. One line per axis at
connect makes the log self-sufficient. Goes in
`OAPA/UniversalPolarAlignmentOAPA.cs`, a free file.

**`DoAutomatedAdjustments` transitions.** The instruction header records the
configuration once at start and never revisits it, while the correction loop reads
the flag live on every cycle. A tester enabling automated adjustments 94 seconds
into a run produced a log that appeared to contradict itself. The setter in
`UniversalPolarAlignmentOAPAVM` logs the transition.

Nothing else is added. Backlash modes are already visible in the
`ExecuteRelativeMove` lines, and re-logging the full configuration block every
cycle would flood the file.

## Item 3 — FAQ

Three questions, three testers, one week, none answered by the current FAQ.

**Where does TPPA go in an advanced sequence, and what has to be set first.**
Order: unpark, cool camera, Three Point Polar Alignment, then slew and centre,
autofocus, start guiding. The trap worth stating plainly is that with Alignment
Tolerance at 0 the instruction never finishes on its own and the sequence waits
for the window to be closed by hand. Also: do not start guiding before the
alignment, and do not slew to the target first.

**There is no error threshold above which manual pre-correction is required.**
The ceiling is per cycle, not on the starting error. For OAPA the per-cycle limit
is `min(max(5', 0.8 x current total error), OAPAMaxCorrectionMagnitude)`, where the
setting defaults to 30 arcmin and accepts 1 to 60. A 9°52' starting error converged
in about 4.7 minutes at 60 and about 6.5 minutes at 30. Raising the ceiling shortens
the coarse phase at the cost of a larger worst-case excursion if the calibration is
wrong. Separately: "Auto verification run" re-runs the whole three-point
measurement once when the starting error exceeded 2°, and it is **not** enabled by
default.

**Which backlash mode, and why Apply is sometimes greyed out.** Off when the
measured backlash is below the noise floor, Full when it is small and repeatable,
Unidirectional when it is large or load-dependent. Apply is disabled when the
calibration detected slippage — the backlash measured differently in the two
directions, so no constant compensation is valid. The measured values stay on
screen deliberately, for diagnosis. That is a mechanical finding, not a fault in
the plugin.

## Testing

`AlignmentErrorMonitorTest`:

- a published message produces the three values converted to arcminutes
- no message for longer than the expiry threshold returns all three to `null` and
  clears `HasLiveError`
- a message arriving before the threshold restarts the clock
- a malformed or unreadable payload leaves the previous state untouched and does
  not throw

`OapaXamlContractTest` gains an assertion that the three readout bindings are
present in `OAPAControlPanel.xaml`. This test exists precisely because silent XAML
regressions are how the rc10 and rc11 bugs shipped.

## Out of scope, and why

**Manual controls in the dockable.** What the tester ultimately wants is nudge
buttons and the error side by side in the dockable panel, never opening the
settings page at all. That is a new UI surface and is deferred to rc14, to be
designed with the testers rather than guessed at.

**Two suspected defects, both investigated and dismissed on 2026-08-04.** Recorded
here so they are not investigated a third time:

- *An automated-adjustments gate desync.* There is none. `TPAPAVM.MoveCloser`
  gates on `activeSystem.DoAutomatedAdjustments`, and the OAPA view model's getter
  reads the same `Settings.Default.DoAutomatedAdjustments` that the instruction
  header prints. They cannot diverge. The header is simply stale, which item 2
  addresses.
- *A missing or duplicated cos(altitude) factor in the azimuth correction path.*
  The field log showed azimuth moves landing at roughly 67% of commanded, against
  cos(45.5°) = 0.70. The controller does not use the calibration factor open-loop:
  it learns `delta_error ~= A * command` from observation and inverts that model,
  so a constant gain error of any origin is absorbed after a few samples. It costs
  cycles, not accuracy. In that particular log the learned model was being reset
  continuously by the altitude axis worsening every cycle, which is why the
  azimuth response never converged.
