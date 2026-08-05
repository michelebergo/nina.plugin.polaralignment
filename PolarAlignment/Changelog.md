# Changelog

## Version 2.2.7.0 (beta rc14)
- **A backlash that costs a different amount in each direction no longer blocks Apply.** The calibration measures the play on both transitions and used to treat any disagreement between them as proof that the mechanics were slipping. They are two different physical quantities, not two samples of one: an axis carrying its load against gravity crosses its own play unaided going down and has to be driven across it going up, so the two can legitimately differ several-fold. Field evidence: the same altitude axis measured 53.4'/16.2' and then 59.9'/15.9' on two consecutive calibrations 40 minutes apart — a 3.5x spread between the transitions, each of them repeating to within 2-12%. That rig was told its mechanics were unrepeatable, and its owner had to transcribe every value by hand.
- The disagreement is now reported as **direction-dependent backlash**, with both figures shown, and Apply stays enabled. The applied value is still their mean, so it is inexact for both directions and the fine phase needs extra cycles — but that is an imperfect compensation, not an invalid one, and it never justified withholding the calibration factor, which the backlash does not affect.
- The warning also says what would actually prove slipping mechanics: run the calibration twice and compare each figure with itself. Repeatability is a property of one transition measured twice, which is not something a single run can establish.
- FAQ updated accordingly, including the limit of Unidirectional mode on a strongly directional axis: its two legs pay one transition each, and those cancel only when the two cost the same.
- Firmware unchanged (1.2.2).

## Version 2.2.7.0 (beta rc13.2)
- **The self-calibration now waits between a move and the measurement that follows it**, using the same "Automated adjustment settle time" the correction loop has always used (default 2 s). The correction loop settled; the calibration went straight from the move to the capture. On a high-friction axis that keeps relaxing after the controller reports idle — one tester watched the position creep for about a second after every stop — the calibration was measuring a moving target: the response reads short, the two backlash transitions disagree, and the slippage detector then blocks an Apply for a calibration that was never measurable. Field symptom: Apply permanently greyed out on a rig whose mechanics were not, in fact, unrepeatable.
- Every calibration move now goes through one helper that owns the settle, so a move added later cannot silently skip it. S0, which measures solve noise with the axis at rest, deliberately does not wait.
- Firmware unchanged (1.2.2).

## Version 2.2.7.0 (beta rc13.1)
- The motor **speed dropdown now reaches 3000 steps/s**, the firmware's actual ceiling. It offered 100-1000, a range inherited unchanged from the pre-OAPA panel and harmless for as long as the firmware ignored the F feed value entirely — rc10 made the firmware honour it, and the list silently became a cap. Field symptom: a tester whose altitude axis runs at 1000 steps per arcminute was held at 0.6 arcmin/s, on the one axis where speed mattered, with no way to ask for more (the dropdown cannot be typed into). New values: 1250, 1500, 1750, 2000, 2500, 3000.
- New tests tie the offered range to the firmware's own `JOG_SPEED_MIN`/`JOG_SPEED_MAX` constants, so the two cannot drift apart again.
- Firmware unchanged (1.2.2).

## Version 2.2.7.0 (beta rc13)
- The OAPA panel shows the live polar alignment error (azimuth, altitude, total) above the manual controls, so a nudge and its effect are visible without switching to the alignment window. The values clear after 90 seconds without a measurement, so what is shown is always live.
- The motion parameters of both axes — gear ratio, backlash, backlash mode and speed, with the implied sky rate — are logged when the controller connects.
- Enabling or disabling automated adjustments during a run is now logged, so a log no longer appears to contradict its own header.
- FAQ: where the instruction belongs in an advanced sequence, that no manual pre-correction is required regardless of the starting error, and which backlash mode to use.

## Version 2.2.7.0 (beta rc12)
- The **Apply button now says "Apply again to confirm"** while it is waiting for the second press. When a calibration would replace hand-entered values, Apply asks before overwriting - but the request lived only in the status line, so it read as "nothing happened" and a tester lost a good calibration to it in the field.
- The azimuth backlash value **no longer appears twice**: with OAPA selected it lives in the Azimuth Motor Settings panel only. The plugin options keep the field for the Avalon UPAS system, which has no motor panel of its own (it was always one value shown in two places, which is why editing either updated both).
- The motor **speed is labeled "Speed (steps/s)" and shows what it means**: a small hint next to it gives the physical rate derived from the calibration factor (e.g. "~ 74.5 '/s"). The same step rate is a very different sky speed on each axis - on one tester's rig 1000 steps/s is ~74 '/s in azimuth but ~8.6 '/s in altitude - and the hint makes that visible instead of surprising.
- Fixed the shipped **firmware source failing to compile** for some users ("stray '\255' in program"): the .ino carried a UTF-8 byte-order mark and non-ASCII characters in its comments, which survive the zip/unzip/IDE trip only on some Windows setups. The file is now pure ASCII; the compiled firmware is unchanged and still reports 1.2.2.
- Firmware unchanged (1.2.2).

## Version 2.2.7.0 (beta rc11)
- The **azimuth backlash value** is now editable in the Azimuth Motor Settings panel, next to its backlash mode - mirroring the altitude axis (with the same provenance label and 0-90' validation). Previously it lived only in the plugin options as "Azimuth backlash compensation" (it still does - same value, two views), which made the two motor panels look asymmetric and the azimuth value hard to find. Field-reported by a beta tester.
- Fixed the unit label of "Azimuth backlash compensation" in the plugin options: the value is in **arcminutes**, not steps.
- Firmware unchanged (1.2.2).

## Version 2.2.7.0 (beta rc10)
- Fixed the motor driver **run current / hold percent** never reaching the controller: the panel sent axis-first commands ("XC600") while the firmware only parses type-first ("CX600") and silently ignored the rest. Field symptom: motors stuck at the 600 mA firmware default regardless of the configured value. The stored values are now also **pushed on every connection** (the controller does not persist them across power cycles), and every driver command is logged with the firmware's response.
- New **STOP button** in the OAPA panel: decelerates both axes to a halt (firmware `!` command). Deliberately enabled even while automated adjustments or a mistyped manual target are driving the motors. A stopped move ends gracefully instead of raising a stuck/timeout error.
- **Movement speed is now honored** (firmware 1.2.1): the F feed rate the plugin always sent is applied per jog, clamped to a safe 50-3000 steps/s (previously the firmware ran every move at a fixed 2000 steps/s profile).
- New firmware-grammar **contract tests**: a C# mirror of the firmware's command dispatcher pins every command shape the plugin emits as recognized - the class of "acknowledged but ignored" wire bugs is now caught at build time.
- Firmware default **hold current lowered to 25%** (was 50%): the hold current flows continuously from power-on - including before the plugin connects and pushes the configured values - and was keeping motors warm at rest.
- Firmware **1.2.2** (reflash required for the speed, STOP and hold-default changes; the plugin fixes work with 1.2.0 too).

## Version 2.2.7.0 (beta rc9)
- OAPA Self-Calibration rewritten as a staged, self-scaling sequence: it measures the solve noise first, escalates its probe until motion is visible, sizes its measuring legs to a fixed physical displacement, and grows the backlash leg until the shortfall is a minority share - so it now survives grossly wrong initial factors and backlash larger than the measuring leg. A hard solve budget bounds the sky excursion.
- Slippage detection: the calibration measures the backlash on both transitions; when they disagree beyond noise, the mechanics are declared non-repeatable and Apply is blocked with an explanation (check grub screws, belt tension, friction). Directional response asymmetry above 10% is reported with both directional factors.
- Per-axis **backlash handling modes** (Off / Soft 75% / Full single-move / Unidirectional overshoot-and-return), selectable in the motor panels. Applying a calibration sets the recommended mode automatically from the measured backlash and noise, and says so.
- Parameter provenance: every calibration factor and backlash value tracks whether it is a default, hand-entered or calibrated. Hand-entered values are validated (backlash clamps to 0-90'), and Apply never silently replaces a manual value - the first press names exactly what would change, the second confirms.
- Opt-in **Precision finish**: near completion the displayed error and the finish decision use a rolling average of the last 4 solves, making sub-0.5' finishes reliable. The correction controller keeps seeing raw measurements.
- The runaway halt now classifies its cause from the **observed sky response** instead of the commanded size, and the correction ceiling/probe profile is scoped per system (UPAS keeps the exact legacy behavior everywhere, including manual nudges, which always clear backlash on reversal).
- Self-calibration now coordinates with the shared **camera capture block** (it refuses to start while a sequence owns the camera and blocks it for the whole run), and the stored Home survives factor changes (controller-native units).
- Manual controls stay **visible but disabled** while automated adjustments are active, with a note explaining why; a "First movement checklist" expander guides new users; the refraction recommendation is always visible.
- Carried over from rc8: fine-phase convergence monitor (noise-aware confirmation margin, stationary-drift detector, best-effort finish), auto verification run, firmware version check with reference link, robust port scanning with remembered COM port.

## Version 2.2.6.7
- Automated adjustments: the per-cycle correction limit is now a **capability supplied by the selected alignment system**, re-evaluated each cycle. OAPA scales it with the measured error (80% of the current total error, floor 5) under a new "Max correction per cycle" safety ceiling (1-60 arcmin, default 30; above 30 is an opt-in for multi-degree initial errors), so multi-degree initial errors converge in a handful of cycles with zero configuration. UPAS and manual behavior unchanged (stock limit). Runaway halts now distinguish a calibration problem (large corrections kept worsening the error) from a drifted error estimate (only small corrections did): the latter recommends re-running the alignment instead of blaming the calibration.
- Automated adjustments: **identification probes scale with the measured error** (15% of the error, clamped between 1 and half the per-cycle limit) so probes are not drowned by solve noise on large errors while staying gentle near the pole. A 75% correction candidate is evaluated alongside the existing ones.
- Automated adjustments: **runaway detection** inside the correction controller — if consecutive corrective moves make the measured error worse (3 in a row, with a noise margin), the controller stops issuing moves, an error notification is shown, and the alignment pauses. Only observations following an executed corrective move are evaluated, so manual alignments and solve noise can never trip it.
- Automated adjustments: **backlash clearing is skipped when the commanded nudge is smaller than the compensation** — with a large compensation and sub-arcminute nudges, the out-and-back clearing excursion injected more error than the nudge removed. Manual absolute moves still always clear on reversal.

## Version 2.2.6.6
- OAPA: added **Self-Calibration** to the OAPA control dock. For each axis the routine runs a large-lever sequence (baseline solve, priming leg, forward leg, reversal leg, reverse leg of 45' each; net commanded motion is zero) and derives the calibration factor from the two backlash-free legs and the **mechanical backlash** from the reversal-leg shortfall. Apply persists both.
- OAPA calibration: azimuth displacements are corrected by **cos(field altitude)** — a base rotation of θ moves a field at altitude h by only θ·cos(h) — so the discovered values no longer depend on where the scope points. Calibration refuses to run the azimuth axis when the field is too close to the zenith (cos(alt) < 0.25).
- OAPA calibration: baseline solve before any motion (an unsolvable field aborts at zero cost), best-effort position restore on mid-sequence failure, forward/reverse leg asymmetry warning (>20%), and automatic Reverse Az/Alt flag correction with a single verified retry.
- OAPA: separate **altitude-axis backlash** value (measured by the calibration, editable in the Altitude Motor Settings panel).
- OAPA: **Home Position** panel (Set Home / Go Home). Home is session-scoped: the controller's position counter restarts at 0 on power-up, so the stored home is discarded on every reconnect instead of persisting stale coordinates.
- UI: renamed "GearRatio" to "Calibration Factor" in the OAPA panels (the value is a software calibration constant, not a mechanical reduction). Settings keys unchanged.

## Version 2.2.6.5
- Automatic completion now requires 2 consecutive solves below the alignment tolerance before finishing, so a single lucky solve cannot end a non-converged procedure. While a below-tolerance result awaits confirmation, automated corrections hold still so the confirmation solve measures the same state.

## Version 2.2.6.4
- Alignment Tolerance now accepts decimal values (e.g. `0.5` = 30 arcseconds). The instruction template bound the field with `UpdateSourceTrigger=PropertyChanged`, which re-parsed the text on every keystroke and silently swallowed the decimal separator — only integers could effectively be typed. The binding now commits on focus loss with `StringFormat 0.##`. Same fix applied to the Options "Default Alignment Tolerance" field; tooltips and the pre-flight validation message updated accordingly.

## Version 2.2.6.3
- Improved correction-loop performance by avoiding star detection until a reference star is manually selected, then projecting that locked star between frames and re-detecting only after 120 seconds, a field shift over 0.5 degrees, or an outside-image projection.

## Version 2.2.6.2
- Fixed TPPA cancellation during plate solving so skipping the sequence item does not surface ASTAP sidecar cleanup errors.

## Version 2.2.6.1
- Fixed a continuous-solver correction-loop failure when star detection returns no star list while reacquiring the reference star.

## Version 2.2.6.0
- Reworked the plugin options page into tabbed sections with built-in workflow, accuracy, warning-state, and troubleshooting guidance.
- Added descriptive tooltips for plugin settings and supported hardware adjustment panels.
- Added a run checklist and contextual guidance to the polar alignment workflow.
- Improved the FAQ and plugin description to better explain prerequisites, recommended sky positions, correction behavior, and troubleshooting.
- Added an experimental continuous error estimator option while keeping the legacy live error calculation as the default.
- Improved the live correction overlay so target and component lines stay anchored correctly on the selected reference star.
- Added a warning for correction fields near exact east or west when the experimental continuous estimator is enabled.
- Improved automated hardware adjustments, including direction handling, backlash behavior, movement timing, and recovery from failed moves.
- Hid manual hardware controls while automated adjustments are active.
- Corrected the polar-alignment log path documentation.

## Version 2.2.5.0
- Replaced AAPA/Avalon checkboxes with a single ComboBox selector (None / UPAS / AAPA) per code review feedback
- Common settings (reverse axes, backlash, automated adjustments) now displayed based on the selected system
- Eliminated code duplication by extracting shared base classes and interfaces for polar alignment systems
- Removed redundant UsePolarAlignmentSystem boolean in favor of enum-based selection

## Version 2.2.4.3
- Polar alignment tab in imaging now correctly pulls the binning settings from the plate solve settings on startup

## Version 2.2.4.2
- When polar alignment is started, guiding will be stopped automatically

## Version 2.2.4.1
- Polar alignment progress is now sent via message broker using message topic `PolarAlignmentPlugin_PolarAlignment_Progress` for other plugins to consume.

## Version 2.2.4.0
- Removed the position angle spread warning as it was not giving any useful information
- Instead the declination spread that the driver is reporting is now measured and a warning is shown if it exceeds 2 arcseconds. The declination axis should not move at all during measurements.

## Version 2.2.3.8
- Log mount position when connected on each measurement point

## Version 2.2.3.7
- Fix messagebroker message parsing for filter name

## Version 2.2.3.5
- Fixed the window popout not closing automatically after the polar alignment was within the set tolerance

## Version 2.2.3.4
- Fixed manual mode to work again without a mount being connected

## Version 2.2.3.2
- `PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment` will now process the message content to be able to adjust parameters as needed

## Version 2.2.3.1
- Added message broker subscription to message topic `PolarAlignmentPlugin_PolarAlignment_ResumeAlignment` to resume the procedure
- Added message broker subscription to message topic `PolarAlignmentPlugin_PolarAlignment_PauseAlignment` to pause the procedure

## Version 2.2.3.0
- Added an option to auto pause between continuous exposures

## Version 2.2.2.2
- Fixed an issue when multiple polar alignment instructions were placed in the sequence with custom binning

## Version 2.2.2.1
- Fixed an issue when the UPA Gear Ratio is changed that it will not be initialized with the changed ratio in the next session

## Version 2.2.2.0
- Fixed an issue when a weather device is connected but reporting 0 hPa pressure

## Version 2.2.1.0
- Added message broker broadcast for alignment error using message topic `PolarAlignmentPlugin_PolarAlignment_AlignmentError`
- Added message broker subscription to message topic `PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment` to start the procedure
- Added message broker subscription to message topic `PolarAlignmentPlugin_DockablePolarAlignmentVM_StopAlignment` to stop the procedure

## Version 2.2.0.1
- After slewing to the first point, added an explicit wait for the dome synchronization if a dome is connected

## Version 2.2.0.0
- Refraction correction will now be properly applied and the option `Adjust for refraction` should now correctly align to the true pole
- Observer elevation is now considered for all transformations

## Version 2.1.0.2
- Fixed an issue when using the UPA that the direction would constantly be reversed on each adjustment.
- When using the UPA it will no longer move a last time without re-evaluation when the alignment threshold has already been reached.
- Added options for UPA to reverse azimuth and altitude axes

## Version 2.1.0.1
- Polar Alignment Tolerance can now be set on instruction level. For example when you are running an automated polar alignment run and want to dial in the polar alignment in multiple phases and getting more precise in each step.
- Now showing UPA positions in automatic mode in addition to the already existing nudge direction

## Version 2.1.0.0
- The position angle spread between the three measurements is now measured. If it is too large, a warning will be shown.

### Integration for the [Avalon Universal Polar Alignment System](https://www.avalon-instruments.com/products-menu/accessories/universal-polar-alignment-system-detail)

#### New Setting: `Use Avalon Polar Alignment System?`
- When activated, the polar alignment routine will connect to the unit automatically after the third step, allowing you to remotely adjust the altitude and azimuth of your system.

#### New Setting: `Do automated adjustments?`
- When activated, this will connect to the UPA and slowly nudge the UPA to the target position automatically after the error has been determined. The control panel will not be shown as movements are done automatically.
- Ensure your gear ratio settings are roughly matched so that one step in the UPA results in an arcminute of movement. The default settings should work fine for the standard version of the UPA.
- Make sure your mount is roughly leveled.
- *Note: For this setting to work, you also need to set the `Polar Alignment Tolerance` to a non-zero value.*

## Version 2.0.2.0
- Automatically increase search radius on plate solve by 5 during solving of the first three points each time it fails

## Version 2.0.1.0
- After automated move to next point, wait for the telescope to indicate it is no longer slewing
- Use Snapshot mode for taking images during polar alignment

## Version 2.0.0.3
- Fixed issue where the TPPA instruction with a filter set would override the autofocus exposure time

## Version 2.0.0.1
- Fixed issue with serilog when PA error logging was enabled

## Version 2.0
- Updated plugin to work with latest major N.I.N.A. version

## Version 1.7.2.0
- It is now possible to pause in between the steps and continue after making the adjustments. Useful in case your image downloads and solves take a while.

## Version 1.7.1.0
- Add an option to continue tracking when TPPA is done. Use with caution to not run into pier collisions!
- Prepopulate the filter with the platesolving filter for defaults
- When refraction correction is enabled, the pole will now also be corrected for it to determine the initial error

## Version 1.7.0.0
- Show a loading spinner while a new image is waiting for a solve to update the error details. The spinner is shown in the total error details.
- Changed the error circle indicator to draw based on the image scale at 30 arcseconds, 1 arcminute and 5 arcminutes
- When latitude and longitude is set to 0 it was most likely never set (as these coordinates are inside the Atlantic ocean). A validation will now check for this and notify to set these values.
- Add a warning when initial error exceeds 2 degrees, that the adjustment phase will be error prone and that it is advised to run it again once the error was reduced
- A further warning when the error exceeds 10 degrees is shown, that the mount is too far off, the location is incorrect or that the RA axis was not moved exclusively

## Version 1.6.3.0
- Added a reset to defaults button
- Added an alignment tolerance to automatically finish polar alignment when below the given threshold

## Version 1.6.2.0
- Fixed an issue where the polar alignment would fail when output logging was enabled

## Version 1.6.0.0
- Enhanced the scaling of the error text for smaller resolutions
- Added an option to account for refraction (which needs further testing in live conditions)

## Version 1.5.3.0
- Gain should now be prepopulated by plate solve gain setting

## Version 1.5.1.0
- Added dome support by waiting for the dome to sync after moving the axis for both automated mode as well as manual mode when both the mount and dome is connected
- Improved manual mode when mount is connected to only get a plate solved image after movement is complete
- Adjusted status report slightly

## Version 1.5.0.0
- When moving near the pole in automated mode and having multiple degrees of PA error, the warning that the mount did not move far enough was shown, even when the mount did indeed travel far enough
- This was caused by comparing the actual solved image RA with the starting RA, but now it will compare the drivers reported RA where the mount thinks it is
- Comparing the actual solved RA does lead to this error, as the axis of the mount is shifted and the circle is not perfectly aligned with the pole
- Fixed an issue when solving succeeded, but star detection did not detect any stars, that the algorithm should no longer fail but use the center of the image instead

## Version 1.4.1.0
- With nightly 1.11 #165 the star detector became incompatible. This version will make it compatible again.

## Version 1.4.0.0
- The plugin now logs the amount of error into `User Documents >> N.I.N.A >> PolarAlignment` when activated in the options
- Added validation when telescope is connected but at park
- Fixed that filter is not saved when saving the instruction as part of an advanced sequence

## Version 1.3.7.0
- In addition to left/right the error display will also include east/west
- Fixed that the altitude error for southern hemisphere was flipped
- Added a toggle to be able to start from the current mount position instead of slewing to a specific alt/az
- Added an expander to the imaging tab tool panel to collapse the options

## Version 1.3.6.0
- Added the individual steps as progress and mark them visually as completed to give the user a better indication of the completion of individual steps
- Added a new color option for the completed steps color

## Version 1.3.5.0
- The manual mode now also works in full blind mode without any telescope connection. A blind solver needs to be setup.
- Added the validation messages to imaging dock to see why the routine cannot be started

## Version 1.3.4.0
- Adjusted plugin description with new markdown syntax

## Version 1.3.3.0
- Fix DefaultAzimuthOffset to be correctly applied in the southern hemisphere as azimuth 180° + offset (instead of 0° + offset)

## Version 1.3.2.0
- Remove the compensation when the automated slew did not reach the expected distance. The various mount drivers differ too much to determine a clever compensation model
- Instead the slew timeout factor can be adjusted. See the [FAQ for details](https://bitbucket.org/Isbeorn/nina.plugins/src/master/NINA.Plugin.Notification/NINA.Plugins.PolarAlignment/FAQ.md)
- In manual mode, wait for the telescope to not report *slewing* before trying to solve

## Version 1.3.1.0
- Improved the target distance check for more tolerance and better compensation

## Version 1.3.0.0
- Added a new "Manual Mode", for mounts that are either no goto mounts or do not implement the necessary interfaces for automated point retrieval
- Further refactoring to reduce code duplication

## Version 1.2.2.0
- Added a check, when the target distance was not reached within one degree to reslew again until the target distance is reached. This can happen when the move rate is less than advertised inside the mount driver.
- Fix an issue when running Three Point Polar Alignment on the imaging tab that it won't be started again after the first iteration.

## Version 1.2.1.0
- Reveal "Default Altitude Offset" and "Default Azimuth Offset" to alter the initial coordinates that are getting preset
- Optimize some of the default settings
- Internal refactorings to reduce code duplications as well as layout improvements
- Check if the camera is free to use when starting the routine out of the imaging tab. If the camera is in use, the play button will be disabled.
- When starting the polar alignment out of framing the camera will be blocked during the routine, to not allow other areas to take control of the camera.

## Version 1.2.0.1
- Fixed an issue when moving the axis would traverse over 24h right ascension - leading to an incorrect distance moved

## Version 1.2.0.0
- The plugin is now also available in the imaging tab to be started directly there instead of inside the sequence.
- A new button inside the tools pane in the imaging tab on the top right is available to open the polar alignment tool

## Version 1.1.0.0
- Complete rewrite of the error determination and correction logic to allow for locations further off from celestial pole and meridian
- Show the initial error amount in smaller numbers below the adjusted error
- Display a shadow rectangle showing the original error for reference behind the adjustet error rectangle

## Version 1.0.0.8
- Added a dedicated changelog file to the repository
- Fix: When using debayered images the plugin would close on the final step with an error

## Version 1.0.0.7
- Fix: Azimuth error could sometimes exceed 180° instead of showing a negative error instead

## Version 1.0.0.6
- Fix: Azimuth error for southern hemisphere was calculated incorrectly

## Version 1.0.0.5
- Initial release using the new plugin manager approach, making the plugin available for download inside N.I.N.A.
