# OAPA Beta — v2.2.7.0-rc17.4

**One fix: the legacy backlash clearing (Avalon UPAS compensation path and absolute moves) now physically moves the mechanism.** Everything in rc17.3 and earlier stays. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash. **No re-calibration required.** OAPA relative nudges (the ones the alignment loop uses) already went through the backlash modes and are unchanged — for most OAPA testers this build behaves identically to rc17.3.

## What changed

After a direction change, the clearing sequence used to send a zero-sum pair: −B then +B in the new direction. Both of those legs reverse — and are consumed by the very play they were supposed to clear. Net physical motion: zero. The reversal's shortfall stayed.

The clearing is now a **single further move of the full compensation in the new direction**: it continues the motion (no new reversal, no new play paid) and recovers exactly what the reversal lost. Tests for this path now assert the final physical position against a dead-travel mechanism, in both reversal directions.

Credit: identified by **Stefan Berg** in upstream review — the same sequence ships in the upstream plugin today.

## Who should care

- If you use the **Move (absolute) buttons** in the panel: those now land where they say.
- Avalon UPAS users with backlash compensation set: the compensation now does something.
- Everyone else: no behavioral change; this build is mainly a stability snapshot of the release-candidate track.

## Install / update

1. Close NINA completely.
2. `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → **Unblock** if shown.
5. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta.**

Clear skies!
