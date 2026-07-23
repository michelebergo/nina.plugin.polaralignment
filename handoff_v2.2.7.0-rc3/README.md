# OAPA Beta — v2.2.7.0-rc3 (Package for Brian)

Hi Brian — everything you need is in here. This build already includes the **"Max correction per cycle"** setting you asked for on Discord.

**Please don't redistribute firmware or plugin — both are still in beta.**

```
handoff_v2.2.7.0-rc3/
├── README.md          ← this file
├── plugin/
│   ├── NINA.Plugins.PolarAlignment.dll
│   └── Changelog.md
└── firmware/
    └── oapa.ino       ← flash this to your FYSETC E4 (Arduino IDE / PlatformIO)
```

---

## 1. Install order (important)

1. **Flash `firmware/oapa.ino`** to the board first — without it, no connection is possible. Your mechanics, wiring and motors stay untouched.
2. Close NINA, replace the DLL in `%localappdata%\NINA\Plugins\3.0.0\Three Point Polar Alignment\` (back up the old one), restart NINA. Version must read **2.2.7.0**.
3. In *Options → Three Point Polar Alignment*: select **OAPA** as Polar Alignment System.

## 2. First-time setup (this fixes the "tiny steps" you saw)

The slow correction you reported happens when the **Calibration Factor is still at 1**: correction commands then translate to just a few motor steps. The fix:

1. Connect to the OAPA system in the Imaging-tab dock.
2. Set your motor values (Run 900 mA, Hold 20% — the values you're used to) directly in the panel. No firmware edit needed.
3. **Point the scope roughly toward the celestial pole** and click **Calibrate** (~2-3 min). It measures the real calibration factors *and* your mechanical backlash automatically.
4. Click **Apply**.

After this, 1 correction unit ≈ 1 arcminute and the steps are properly sized.

## 3. Settings that matter for your use case

- **Do automated adjustments: ON**
- **Alignment Tolerance: 0.5** (decimals work, e.g. 0.5' = 30")
- **Adjust for refraction: ON**
- **Max correction per cycle** (new, next to the settle time): default 5 arcmin. **If your initial error is large (degrees), raise it to 15-20** — this is the setting you asked for. The first 1-2 moves are still small on purpose (the controller probes your hardware to learn its response), then corrections get big and well-aimed.

## 4. Extras you might like

- **Home Position panel**: "Set Home" stores the current axis positions, "Go Home" returns to them with one click — handy before tearing down. (Counter restarts at 0 when the controller is power-cycled.)
- TPPA started via Touch'N'Stars / Advanced API uses the automated correction too — the whole thing works from your phone.

## 5. What to send back

- NINA log (`%localappdata%\NINA\Logs\`), final Az/Alt/Total error, number of correction cycles
- Your calibration results (factors + backlash) — a second hardware setup is exactly the data we need before submitting this upstream

Danke & clear skies!
Michele
