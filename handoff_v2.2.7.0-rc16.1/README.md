# OAPA Beta — v2.2.7.0-rc16.1

**One targeted fix on top of rc16: a backlash pair with one side at zero and the other large no longer gets applied as a directional split.** Everything in rc16 stays. **Please don't redistribute firmware or plugin — still in beta.**

> **Firmware unchanged (1.2.2)** — nothing to reflash.

## What this fixes

The first rc16 field night showed it perfectly. An altitude axis measured its backlash as **4.10'/4.31'** — symmetric, applied as the mean. Five minutes later a second calibration measured **0.00'/8.69'** on the very same axis and applied it as a directional pair. The *sum* of the two figures was stable; the *split* had completely flipped. That is a transition slipping during the measurement, not directional mechanics — and a split that isn't real injects its whole gap into every reversal: a **23" residual was thrown to 6'32"** by a −0.21' correction, right at the finish line.

rc16.1 treats **zero-against-large as the slip signature it is**: the pair collapses to the mean (both directions equal), the log says why, and the real symmetric play stays compensated. A symmetric value's magnitude cancels out of the two-leg plan, so even an imperfect mean only costs travel time — it can never create a floor or an overshoot. Genuinely directional axes, whose pairs have two measurable sides, are unaffected.

The structural fix — measuring each transition twice, so the directionality verdict gets an uncertainty of its own — is planned for the next build.

## Install / update

1. Close NINA completely.
2. Open File Explorer and paste in the address bar:
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`
3. Replace `NINA.Plugins.PolarAlignment.dll` with the one from this package's `plugin\` folder.
4. Right-click the new DLL → Properties → tick **Unblock** if shown, then OK.
5. If a calibration ever left you with a pair like `+0.00'/-8.69'`, **re-run Self-Calibration and Apply** once — the new pass will collapse it correctly.
6. Firmware: nothing to do — still 1.2.2.

**Reminder: don't update TPPA from the NINA Plugins page while testing a beta** — it silently replaces the beta DLL.

## Test plan

1. **Calibrate twice in a row** and compare the per-axis pairs. Equal-both-ways twice is a symmetric axis; two measurable sides that repeat is a directional one; if you see the new log line about a zero-against-large split being collapsed, send me the log — that's the guard doing its job on a slipping transition.
2. Run an alignment to tolerance and watch the finish: small corrections near the target must not overshoot any more.
3. Everything from the rc16 test plan still applies (in particular: calibrate from two different pointings — the factors must come back the same).

Clear skies!
