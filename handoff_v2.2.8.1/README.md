# OAPA Build — v2.2.8.1 (Hand-off)

This folder contains a **pre-release** build of the *Three Point Polar Alignment* plugin for N.I.N.A. The single change on top of v2.2.8.0 fixes the **intermittent completion popup** behaviour you reported (popup only appeared on the first of three back-to-back self-calibration runs).

> **Firmware is unchanged** since v2.2.5.1. The `firmware/oapa.ino` in this folder is included only for completeness; if you already flashed v2.2.5.1 / v2.2.6.0 / v2.2.7.0 / v2.2.8.0 firmware you do **not** need to re-flash.

---

## What's in this folder

```
handoff_v2.2.8.1/
├── README.md                    ← this guide
├── plugin/
│   ├── NINA.Plugins.PolarAlignment.dll   (v2.2.8.1)
│   └── Changelog.md
└── firmware/
    └── oapa.ino                 ← unchanged from v2.2.5.1, included for completeness
```

---

## 1. Update the plugin

> ⚠️ **Close N.I.N.A. before replacing the DLL.**

1. Open the NINA plugin folder in Explorer:
   ```
   %localappdata%\NINA\Plugins\3.0.0\Three Point Polar Alignment\
   ```
2. Make a backup of the existing `NINA.Plugins.PolarAlignment.dll` (rename to `.dll.bak`).
3. Copy `plugin\NINA.Plugins.PolarAlignment.dll` from this folder into that directory.
4. Start NINA. Under *Options → Plugins* the version should read **2.2.8.1**.

All existing settings are preserved (calibration factors, reverse-axis flags, motor currents, OAPA backlash, etc.).

---

## 2. What's new in 2.2.8.1

The post-calibration completion indicator was previously a single bottom-right toast (`Notification.ShowInformation`) lasting 10 seconds. You reported it appeared on only 1 of 3 consecutive runs.

We investigated the NINA notification system and confirmed:
- NINA does **not** deduplicate identical notifications.
- The toast really does get fired every time the calibration completes (verified in source).
- The 10-second default lifetime is short enough that a user looking at the panel (not the corner) easily misses it.

This release adds **two complementary, independent indicators**, so completion is hard to miss regardless of where you're looking:

### 2.1 Longer toast (30 seconds instead of 10)

The bottom-right toast `Calibration done. X factor: …, Y factor: …` now stays visible for **30 seconds** before fading. Same applies to the `Calibration factors updated` toast after clicking Apply.

### 2.2 Persistent completion banner in the result panel

A new line is now shown at the top of the Self-Calibration result panel:

> **✅ Calibration complete — review the discovered values below and click Apply or Discard.**

It appears the moment a calibration finishes and **stays visible until you click Apply or Discard**. Doesn't auto-fade, doesn't depend on watching the corner of the screen. If you walk away during the 1-minute calibration and come back later, the banner is still there.

### 2.3 What did NOT change

- All self-calibration math (the auto-flip retry from v2.2.7.0 is unchanged).
- The discovered X / Y factor values you get from the calibration will be the same as on v2.2.8.0 (only display, not math).
- Internal settings keys are unchanged — your saved values are preserved.

---

## 3. Test plan

### 3.1 Quick test (no sky needed, 2 minutes)

Goal: confirm the new banner appears after a calibration.

1. Install per §1 and start NINA. Verify version reads **2.2.8.1** in Options → Plugins.
2. Open the OAPA dock in the *Imaging* tab.
3. Click **Calibrate**. (If you're at the keyboard with no sky access, the routine will fail at plate-solve — that's fine for this test, but use the next section for a full test.)
4. **Expected outcome on success**: the result panel that was already in v2.2.8.0 now has a **bold checkmark banner** at the top: `✅ Calibration complete — review the discovered values below and click Apply or Discard.`
5. **Expected outcome on the toast**: it should remain visible at bottom-right for ~30 seconds instead of disappearing in 10.

### 3.2 The original repro test (clear sky, 15 min)

Repeat the procedure that originally exposed the bug:

1. Run Calibrate → wait for completion → click **Apply**.
2. Run Calibrate again → wait → click **Apply**.
3. Run Calibrate a third time → wait → click **Apply**.

**Expected on all three runs:**
- The bottom-right toast appears for ~30 seconds (no longer missed for being too brief).
- The persistent banner appears at the top of the result panel and stays there until you click Apply.

You can keep your attention on the OAPA panel itself or wander off — both should work this time.

---

## 4. Reverting

To roll back to v2.2.8.0:
- Delete the v2.2.8.1 DLL and rename the `.dll.bak` back to `NINA.Plugins.PolarAlignment.dll`.
- Restart NINA.

User settings are unaffected by the rollback. Firmware does not need to be reverted.

---

## 5. Support

If anything looks wrong during testing, please send back:
- The NINA log file from `%localappdata%\NINA\Logs\` (most recent).
- A screenshot of the OAPA panel right after a calibration completes (banner visible? toast visible? values populated?).
- A note of which of the three test runs in §3.2 showed/missed each indicator.
