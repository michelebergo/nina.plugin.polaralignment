# Changelog

## Version 2.2.9.0
Focus: **sub-arcminute convergence**. Addresses beta-tester reports that the automated correction loop could not settle below ~1 arcminute.
- **Alignment Tolerance now accepts decimals** (e.g. `0.5` = 30 arcseconds). The instruction template bound the field with `UpdateSourceTrigger=PropertyChanged`, which re-parsed the text on every keystroke and silently swallowed the decimal separator — only integers could effectively be typed. The binding now commits on focus loss with `StringFormat 0.##`. Same fix applied to the Options → "Default Alignment Tolerance" field. Tooltips and the pre-flight validation message updated accordingly.
- OAPA self-calibration reworked for a **10× longer lever arm with automatic backlash measurement**:
  - Calibration step raised from 5' to **45'** per leg. Plate-solve noise (2-5") is now <1% of the measured displacement instead of ~5-10%.
  - New sequence per axis: priming move (absorbs any pending backlash) → solve → forward leg → solve → reversal leg → solve → reverse leg → solve. Net commanded motion is zero, so the axis returns to its starting position.
  - The two single-direction legs are backlash-free and yield the **calibration factor**; the direction-reversal leg comes up short by exactly the mechanical backlash, which is now **measured automatically** and shown in the result panel.
  - Clicking **Apply** now persists the measured backlash into the OAPA X/Y backlash compensation settings (previously defaulted to 0 — the main cause of oscillation around ±0.5-1.5').
- Correction loop (`MoveCloser`) reworked, inspired by the AAPA companion-app algorithm:
  - **Dual-axis correction**: both azimuth and altitude are adjusted in every iteration (previously one axis per iteration), roughly halving the number of exposure/solve/settle cycles.
  - **Adaptive gain**: 0.9 while the axis error is above 2', 0.6 below (previously fixed 0.75) — aggressive far from the pole, gentle on final approach.
  - **Dead-band of 0.15' per axis**: below that, plate-solve noise dominates and the motor no longer chases it.
  - The direction-reversal heuristic now requires the error to worsen by more than the dead-band in addition to the previous 15% margin, eliminating spurious sign flips near the target.
- **Auto-finish now requires 2 consecutive solves** below the alignment tolerance before completing (a single lucky solve can no longer end a non-converged procedure). Motors hold still between the two confirmation solves.
- New pre-flight warning when the alignment tolerance is set below 1.5× the dead-band (< ~0.23'), where the loop may not be able to converge.
- Test suite: fixed pre-existing compilation errors (missing `declinationSpreadArcsec` argument) and added unit tests for the new gain/dead-band logic.

## Version 2.2.8.1
- OAPA self-calibration: completion popup is now **much harder to miss**. Two complementary fixes addressing beta-tester reports that the post-calibration toast appeared on only 1 of 3 consecutive runs:
  - The bottom-right toast (`Notification.ShowInformation`) now stays visible for **30 seconds** instead of the default 10 seconds. Same for the "Calibration factors updated" toast after Apply.
  - A new **persistent completion banner** is shown at the top of the Self-Calibration result panel: `✅ Calibration complete — review the discovered values below and click Apply or Discard.` It remains visible until the user clicks Apply or Discard, regardless of whether the toast was seen.
- No functional changes to the calibration math, motion code, or auto-flip logic.

## Version 2.2.8.0
- UI: renamed **"Gear Ratio"** → **"Calibration Factor"** in the Self-Calibration panel, the Azimuth/Altitude motor settings panels, and the Options panel. The previous label was misleading: the value is a software calibration constant (mm of G-code command per arcminute of polar-axis movement), not a mechanical gear ratio like a harmonic reducer's `1:N`. The Self-Calibration button is now simply labelled "Calibrate". Internal settings keys (`OAPAXGearRatio`, `OAPAYGearRatio`, `AvalonXGearRatio`, `AvalonYGearRatio`) are unchanged, so existing saved user values are preserved across the upgrade.
- Toast notifications and status messages updated to say `X factor` / `Y factor` instead of `X ratio` / `Y ratio`. The post-Apply toast now reads `Calibration factors updated`.
- FAQ.md and the handoff README/test plan rephrased to use the new terminology.

## Version 2.2.7.0
- OAPA self-calibration: **auto-fixes wrong Reverse Az / Reverse Alt flags**. When an axis fails the direction-consistency check, the routine flips the relevant Reverse flag, retries that axis once, and — if the retry passes — persists the flipped flag automatically. The consistency message reads `Direction consistency: OK (Reverse Az auto-corrected)` so the change is visible to the user; if the auto-flip does not resolve the inconsistency it falls back to the previous WARNING and restores the original flag.
- OAPA: added separate **Y (Altitude) backlash compensation** setting (`OAPAYBacklashCompensation`). The shared base VM `ClearBacklash` routine now uses it on Y-axis direction changes for OAPA only.
- Avalon UPAS: **Y-axis backlash compensation is intentionally a no-op** (`YBacklashCompensation` returns `0`, setter ignored). The Options row for "Altitude backlash compensation" is hidden when Avalon is the selected system, and the corresponding setting key has been removed from `app.config` / `Settings.settings`. Avalon defaults / behavior are otherwise unchanged.

## Version 2.2.6.0
- OAPA: added **self-calibration** for gear ratios. New "Self-Calibration" panel in the OAPA control dock runs an automated capture/move/plate-solve sequence and discovers the real azimuth and altitude gear ratios from sky displacement. Useful when the mechanical reduction is unknown or only approximately known (e.g. a custom harmonic reducer).
  - Per axis: 2 samples, each one a +5'/-10'/+5' triangle around the starting position. Median is used so an occasional plate-solve outlier cannot poison the result.
  - Discovered ratios are shown next to the current ratios; user clicks **Apply** to commit them (settings auto-persist) or **Discard** to keep the current values.
  - Direction-consistency check: a warning is shown if the +/- displacements are not antiparallel on the tangent plane, which usually means the reverse-axis flag is wrong.
  - All motion is gated through `IsNotMoving` so manual jogs are blocked while calibration is running.
  - AAPA / Avalon UI is unchanged.

## Version 2.2.5.1
- OAPA: fixed motion completion timeouts ("Motor appears stuck at position...") when using high gear ratios (e.g. harmonic reducers at 100:1). Tolerance and timeout now scale with the configured gear ratio instead of being fixed.
- OAPA: targets are now rounded to the nearest motor step before being sent, preventing sub-step oscillation around the goal position at high ratios.
- OAPA firmware (`oapa.ino` / `src/main.cpp`): replaced `(long)` truncation of jog values with `lround()` so that fractional steps are no longer silently discarded — small commanded moves at high ratios now actually execute.
- AAPA / Avalon behavior is unchanged (defaults preserved via virtual hooks in the shared base class).

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

- The manual mode now also works in full blind mode without any telescope connection. A blind solver needs to be setup - but it must not be astrometry.net due to being too slow.
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