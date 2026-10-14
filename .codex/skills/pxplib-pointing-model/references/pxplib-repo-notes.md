# PXPLib Repo Notes

## DLL summary

Reverse-engineering in this workspace found these important public APIs.

### `PXPLib.PointXPCore`

Useful methods:
- `AddCalPoint(PXPLib.CalPoint)`
- `Calibrate()`
- `ClearCalPoints()`
- `UseCalPoint(PXPLib.CalPoint, Boolean)`
- `UseCalPoint(Int32, Boolean)`
- `EncToAltAz(Double, Double, Double ByRef, Double ByRef)`
- `AppToEnc(Double, Double, Double, Int32, Double ByRef, Double ByRef)`
- `CtoEnc(Double, Double, Double, Double, Int32, Double ByRef, Double ByRef)`

Useful properties:
- `RMSErrorRad`
- `RMSErrorAllRad`
- `MaxErrorRad`
- `MaxErrorAllRad`
- `NumCalPoints`
- `NumPointsUsed`
- `NumErrorTermsInUse`
- `Latitude`
- `Longitude`
- `ThetaCountsPerRev`
- `PhiCountsPerRev`
- `IsAltAz`

### `PXPLib.CalPoint`

Important fields used by this repo:
- `RA`
- `Dec`
- `ThetaEnc`
- `PhiEnc`
- `LSTMark`
- `JDMark`
- `TimeStr`
- `DateStr`
- `Name`
- `Used`

## Existing repo behavior

### Runtime corrections

File:
- `C:/Users/Narit/Documents/GitHub/MountController-API-main/Narit-Mount-Lib/Engine/PointingModel/PointingModelManager.cs`

Important behavior:
- creates and keeps a single `PointXP`
- loads `.pxp` files into that instance
- applies runtime correction through `Core.EncToAltAz(...)` via reflection
- converts apparent RA/Dec to encoder target with `PointXP.ApparentToEncoder(...)`

### Legacy calibration path

File:
- `C:/Users/Narit/Documents/GitHub/MountController-API-main/Narit-Mount-Lib/Engine/Manager/MountCalibrationManager.cs`

Current legacy flow:
1. capture image
2. plate solve
3. append solved point into progressive `.pxp`
4. reload `PointXP` from the progressive file
5. update runtime correction incrementally

Strength:
- robust and file-compatible

Tradeoff:
- calibration quality is mediated through repeated file reloads
- model quality is mostly inferred from per-image solve quality instead of core fit quality

### Experimental calibration path

Current prototype intent:
1. keep the same UI
2. build an in-memory model from solved points using `PointXPCore`
3. surface core metrics such as RMS and max error
4. preserve the legacy path so the system can switch back safely

## Coordinate conventions used here

- Mount is treated as Alt-Az.
- `ThetaCountsPerRev` and `PhiCountsPerRev` are configured as `2pi`.
- Encoder-derived azimuth and altitude values are converted to radians before being fed to PXPLib.
- `.pxp` data written by this repo uses solved J2000 RA/Dec values directly.

## Practical guidance

- If a user asks for maximum compatibility, keep using the legacy `.pxp` path.
- If a user asks to compare accuracy or reduce reload overhead, prefer the experimental direct-core builder.
- When a task touches runtime correction, verify both:
  - `EncToAltAz(...)`
  - `ApparentToEncoder(...)`
- When a task touches calibration quality, prefer core metrics over only per-image plate solve RMS.
