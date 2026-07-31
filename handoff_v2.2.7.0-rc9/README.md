# OAPA Beta — v2.2.7.0-rc9

The "robust calibration" release: the self-calibration now survives hardware it used to be fooled by (huge backlash, badly wrong starting factors), tells you honestly when the mechanics can't be compensated at all, and picks the right backlash strategy for your platform by itself. **Please don't redistribute firmware or plugin — still in beta.**

> Firmware unchanged (1.2.0) — only the DLL needs replacing. Your calibration stays valid, but we recommend re-running Self-Calibration once: the new sequence measures more and better, and its Apply now also selects the backlash mode for you.

## Why this release exists

Two field sessions drove rc9. In one, a platform with mechanical play of the same size as the calibration's measuring legs produced a factor ~4× too large — every later correction overshot wildly. In another, ~40' of play appeared only under payload and swallowed small correction moves entirely. rc9's calibration adapts its own measuring legs to whatever it finds, and the new backlash modes give large-play hardware a strategy (unidirectional approach) where the play never enters the final positioning at all.

## What's new vs rc8

### 1. Self-Calibration measures like an engineer now

The sequence is staged and self-scaling: it first measures your solve noise at rest, then probes with growing steps until motion is clearly visible, sizes its measuring legs to a fixed physical displacement (so a wrong starting factor doesn't matter), and — the key change — **when the reversal leg loses most of its travel to backlash, it grows the leg and re-measures** instead of accepting a contaminated number. Backlash larger than the measuring leg is now measured correctly. A hard solve budget keeps the sky excursion bounded.

### 2. It tells you when the mechanics can't be compensated

The backlash is measured on **both** direction transitions. If the two disagree beyond noise, that's not backlash — it's slippage (a slipping clutch, loose grub screw, belt), and **no constant compensation is valid**. The result stays visible for diagnosis, but Apply is blocked with a plain explanation of what to check. Likewise, if an axis responds differently in the two directions (>10%), both directional factors are reported.

### 3. Per-axis backlash modes — set automatically on Apply

Each axis now has a **Backlash mode** in its motor panel, from conservative to aggressive:
- **Off** — plain moves (negligible play);
- **Soft** — single move extended by 75% of the backlash (safe if the value might be overestimated);
- **Full** — single move extended by the whole backlash: the take-up is part of the move, no more out-and-back excursion;
- **Unidirectional** — overshoot past the target and come back, so the final approach always comes from the same side and the play never enters the positioning. This is the mode for large play (multi-arcminute, payload-dependent).

**Applying a calibration selects the recommended mode automatically** from the measured backlash and noise, and says so in the status. You can change it manually any time.

### 4. Your hand-entered values are protected

Every calibration factor and backlash value now shows where it came from (*manual* / *calibrated* — small label next to the field). Hand-entered values are validated at the door (backlash is clamped to a physically sane 0–90'), and **Apply never silently replaces a manual value**: the first press lists exactly what would change with both numbers; a second press confirms.

### 5. Precision finish (Options, default OFF)

For sub-0.5' targets: near completion, the displayed error and the automatic-finish decision use a **rolling average of the last 4 solves** instead of a single noisy one. The average restarts after every automated move, and the correction controller keeps working on raw measurements. Turn it on if you chase 0.3–0.4' finishes.

### 6. Smaller things you'll notice

- The manual movement buttons no longer disappear when "Do automated adjustments" is on — they stay visible but disabled, with a note explaining why (this confused more than one of you; now it can't).
- A **"First movement checklist"** expander at the top of the OAPA panel walks a new setup through connect → test nudges → direction check → calibration → apply.
- The refraction recommendation is now always visible next to the option (recommended ON — it aligns you to the true pole).
- The runaway halt classifies its cause from the **measured sky response** rather than the commanded move size, so a wrongly calibrated axis can no longer masquerade as "estimate drift".
- Calibration now cooperates with NINA's shared camera ownership: it refuses to start while a sequence owns the camera, and releases it even if a run fails.
- The stored Home position survives factor changes (it's kept in controller-native units now).

### Carried over from rc8

Fine-phase convergence (margin-hold confirmations, stationary-drift detector, best-effort finish, honest halt messages), **Auto verification run** (default ON), firmware version check with the reference link in the panel, remembered COM port for fast reconnects.

## Firmware

**Unchanged — still 1.2.0.** If you're on rc6 or later, nothing to reflash. First-timers: flash `firmware/oapa.ino`.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. Start NINA → the plugin shows **2.2.7.0** and the OAPA panel shows the firmware link under the connect button and "Backlash mode" selectors in the motor panels — that's how you know rc9 is loaded.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL. If that happens, just redo the swap.

## Test plan

1. **Re-run Self-Calibration** (plate-solvable field away from the zenith) and Apply. Note the backlash mode it selects for each axis and whether the reported values match your expectations. If you have payload-dependent play: calibrate **with the payload mounted**.
2. If your platform has large play: check that the selected **Unidirectional** mode visibly approaches targets from one side, and that fine-phase corrections stop see-sawing.
3. Try editing a backlash value by hand, then Apply a calibration — the confirmation flow should name your manual value before replacing it.
4. Optionally enable **Precision finish** and see whether your finishes land tighter and the displayed error is steadier near the end.
5. Logs, as always, are gold: `%LOCALAPPDATA%\NINA\Logs`, plus the polar-alignment log if you have "Log polar alignment error adjustments" on.

Clear skies, and thank you — every one of these changes traces back to one of your field sessions.
