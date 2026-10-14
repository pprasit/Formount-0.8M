---
name: pxplib-pointing-model
description: Work with PXPLib PointXP and PointXPCore inside MountController-API. Use when Codex needs to debug pointing-model loading, calibration completion, .pxp export quality, or bright-star selection for calibration.
---

# PXPLib Pointing Model

Use this skill when the task touches `PXPLib.dll`, `PointXP`, `PointXPCore`, `.pxp` files, calibration completion, or bright-star based pointing-model workflows in `MountController-API`.

Primary repo locations:
- `C:/Users/Narit/Documents/GitHub/MountController-API-main/Narit-Mount-Lib/Engine/PointingModel`
- `C:/Users/Narit/Documents/GitHub/MountController-API-main/Narit-Mount-Lib/Engine/Manager/MountCalibrationManager.cs`
- `C:/Users/Narit/Documents/GitHub/MountController-API-main/Narit-Mount-Lib/Engine/Catalog`
- `C:/Users/Narit/Documents/GitHub/MountController-API-main/Narit-Mount-Lib/dll/PXPLib.dll`

## What matters in this repo

`PXPLib.dll` is used for:
1. Loading and applying `.pxp` pointing models at runtime.
2. Converting encoder positions to corrected Alt/Az.
3. Building models from solved calibration points.

The repo has 2 distinct paths:
- Legacy file path: `capture -> plate solve -> append/update .pxp -> reload PointXP`
- Experimental core path: `capture -> plate solve -> AddCalPoint -> Calibrate`

Do not blur these together unless the user explicitly asks for a migration.

## Current repo rules

- Keep a single runtime `PointXP` instance.
- Prefer `PointXP.Core.EncToAltAz(...)` over wrapper helpers for runtime correction.
- Use `PointXP.ApparentToEncoder(...)` for target conversion checks.
- Treat the mount as Alt-Az.
- Keep `ThetaCountsPerRev = PhiCountsPerRev = 2pi`.
- `.pxp` sky coordinates must be stored as solved `J2000` RA/Dec.
- Runtime-ready models need both:
  - valid `Cal Star` blocks
  - fitted `Model Terms`

## Critical lessons from this codebase

- If a built model loads but points badly, first verify the `.pxp` file was written with `SolvedRa_J2000_Rads` and `SolvedDec_J2000_Rads`, not expected target coordinates.
- If completion reaches 100% but no summary window appears, inspect `WaitForAllSolvesAndShowDialog()` and guard against duplicate or lost dialog requests.
- If `Load Pointing Model File` activates the model but the table stays empty, check the UI parser. PlaneWave `.pxp` files are not CSV; parse `Cal Star` blocks and `ThetaEnc/PhiEnc`.
- If the bright-star list looks fake or sparse, inspect `BrightStarCatalog.cs` and `Data/BrightStars.json`. Do not silently replace a real catalog with procedurally generated stars.

## Files to inspect first

1. `PointingModelManager.cs`
   Check:
   - startup autoload
   - model normalization/repair
   - runtime correction path
2. `PlaneWaveModelFile.cs`
   Check:
   - `.pxp` export contents
   - solved vs expected coordinates
3. `MountCalibrationManager.cs`
   Check:
   - completion-dialog path
   - autosave/runtime-ready snapshot path
   - final build/use path
4. `MountCalibrationControl.xaml.cs`
   Check:
   - load button behavior
   - save/build button behavior
   - UI table population
5. `BrightStarCatalog.cs`
   Check:
   - source of calibration stars
   - fallback behavior

## APIs and metrics to keep in mind

Important `PointXPCore` methods:
- `AddCalPoint(...)`
- `ClearCalPoints()`
- `UseCalPoint(...)`
- `Calibrate()`
- `EncToAltAz(...)`
- `AppToEnc(...)`

Important metrics:
- `RMSErrorRad`
- `RMSErrorAllRad`
- `MaxErrorRad`
- `MaxErrorAllRad`
- `NumCalPoints`
- `NumPointsUsed`
- `NumErrorTermsInUse`

Convert radians to arcseconds with:
- `arcsec = radians * 206264.80624709636`

## Decision guide

- If the bug is about a model file being unusable after build, inspect `.pxp` export first.
- If the bug is about “loaded but UI shows nothing”, inspect UI parsing first.
- If the bug is about “finished but no dialog”, inspect completion flow before touching solver code.
- If the bug is about “wrong stars chosen”, inspect `BrightStars.json` and catalog loading before changing calibration geometry.

## Safety rule

Do not replace real sky/catalog data with synthetic/generated data unless the user explicitly wants a fallback-only mode.

## Read more

For repo-specific notes, see:
- `C:/Users/Narit/Documents/GitHub/MountController-API-main/.codex/skills/pxplib-pointing-model/references/pxplib-repo-notes.md`
