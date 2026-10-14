# Mount Tracking Status - Unified Threshold

## ?? Overview

**Problem:** ????? `IsTracking` ????????????????????? Calibration stability check

**Solution:** ??? `RmsStabilityThreshold_Arcsec` ?????????????????????????

---

## ?? Before Fix

### Old Behavior (2 different thresholds):

```csharp
// 1. MountInfo.IsTracking - Uses Tracking_Sensitivity_Deg
double azSensitivity = azConfig.Tracking_Sensitivity_Deg * 3600.0;  // 0.1° = 360"
double altSensitivity = altConfig.Tracking_Sensitivity_Deg * 3600.0; // 0.1° = 360"

bool isTracking = (azRms < 360") && (altRms < 360");  // Very loose!

// 2. Calibration Stability - Uses RmsStabilityThreshold_Arcsec
double rmsThreshold = calibrationSettings.RmsStabilityThreshold_Arcsec;  // 3.0"

bool isStable = (totalRms < 3.0");  // Very tight!
```

**Issue:** 360" vs 3.0" = ??????????? 120 ????!

---

## ? After Fix

### New Behavior (1 unified threshold):

```csharp
// BOTH use RmsStabilityThreshold_Arcsec from MountCalibrationSettings
double rmsThreshold = calibrationSettings.RmsStabilityThreshold_Arcsec;  // 3.0"

// 1. MountInfo.IsTracking
bool azTracking = azimuthInfo.TrackingRmsError_Arcsec <= rmsThreshold;
bool altTracking = altitudeInfo.TrackingRmsError_Arcsec <= rmsThreshold;
bool isTracking = azTracking && altTracking;

// 2. Calibration Stability
double totalRms = Math.Sqrt(azRms² + altRms²);
bool isStable = (totalRms < rmsThreshold);
```

**Result:** Consistent threshold ????????! ?

---

## ?? Files Modified

### 1. **MountManager.cs** (Lines 150-173)

**Before:**
```csharp
// Load tracking sensitivity from axis config (0.1° = 360")
var azConfig = AppSettings.GetAzimuthAxisConfig();
double azSensitivityArcsec = azConfig.Tracking_Sensitivity_Deg * 3600.0;
```

**After:**
```csharp
// Use SAME threshold as calibration (3.0" default)
var calibrationSettings = AppSettings.GetMountCalibrationSettings();
double rmsThresholdArcsec = calibrationSettings.RmsStabilityThreshold_Arcsec;
```

---

### 2. **MountCalibrationManager.cs** (Line 797)

Already using correct threshold:
```csharp
if (totalRms < _rmsStabilityThresholdArcsec)  // Same value!
{
    // Wait for stability duration
}
```

---

## ?? Benefits

### 1. **Consistency**
- `[TRACKING]` status ??? "ready to capture" ????????????????
- ????????: ??? IsTracking = true ? ????????????

### 2. **User Control**
- ???????????? (`RmsStabilityThreshold_Arcsec`) ??Settings?????
- Default: 3.0 arcsec (reasonable)
- ??????????: 1.0" (tight) ???? 5.0" (loose)

### 3. **Meaning**
- **3.0 arcsec threshold** = mount stable ???????:
  - ? ????????????
  - ? Track object ?????????
  - ? Plate solve ??????

---

## ?? Typical Values

| Condition | RMS Error | IsTracking | Ready to Capture |
|-----------|-----------|------------|------------------|
| Slewing | 50-200" | ? NO | ? NO |
| Settling | 5-15" | ? NO | ? NO |
| Tracking (good) | 1-3" | ? YES | ? YES |
| Tracking (excellent) | < 1" | ? YES | ? YES |
| Unstable | > 5" | ? NO | ? NO |

**Threshold: 3.0 arcsec**
- Below ? Tracking + Ready
- Above ? Not tracking

---

## ?? AppSettings Configuration

### Mount_Calibration_Settings:
```json
{
  "RmsStabilityThreshold_Arcsec": 3.0,   ? SHARED THRESHOLD
  "RmsStabilityDuration_Ms": 1000,
  "TrackingDuration_Seconds": 8
}
```

### ~~Azimuth_AxisConfig~~ (NOT USED ANYMORE):
```json
{
  "Tracking_Sensitivity_Deg": 0.1,   ? DEPRECATED (not used)
}
```

### ~~Altitude_AxisConfig~~ (NOT USED ANYMORE):
```json
{
  "Tracking_Sensitivity_Deg": 0.1,   ? DEPRECATED (not used)
}
```

---

## ?? Technical Details

### Why 3.0 arcseconds?

**Camera Pixel Scale Example:**
- 200mm lens, 3.75µm pixel ? 3.9 arcsec/pixel
- **3.0 arcsec RMS** = 0.77 pixel RMS error
- Result: Sharp images ?

**Plate Solving:**
- Typical solve accuracy: 1-2 arcsec
- **3.0 arcsec RMS** = within solve precision ?

**Tracking Performance:**
- Good mount tracking: 1-3 arcsec RMS
- **3.0 arcsec threshold** = reasonable requirement ?

---

## ?? Debug Output

### Before Fix:
```
[MOUNT MANAGER] Tracking: ACQUIRED
  Az RMS: 15.23" (threshold: 360.00") - OK  ? TOO LOOSE!
  Alt RMS: 12.45" (threshold: 360.00") - OK
```

### After Fix:
```
[MOUNT MANAGER] Tracking: ACQUIRED
  Az RMS: 2.15" (threshold: 3.00") - OK  ? CORRECT!
  Alt RMS: 1.87" (threshold: 3.00") - OK
```

---

## ? Validation Steps

1. **Check Settings:**
```csharp
var settings = AppSettings.GetMountCalibrationSettings();
Console.WriteLine($"RMS Threshold: {settings.RmsStabilityThreshold_Arcsec}\"");
// Expected: 3.0
```

2. **Test Tracking:**
```csharp
// Start tracking ? watch Debug Output
// Should see: "Tracking: ACQUIRED" when RMS < 3.0"
```

3. **Test Calibration:**
```csharp
// Run calibration ? watch "[TRACKING]" status
// Should match IsTracking state
```

---

## ?? Summary

| Aspect | Before | After |
|--------|--------|-------|
| Threshold Source | Axis Config (360") | Calibration Settings (3") |
| IsTracking Logic | Too loose | Correct |
| Calibration Status | Correct | Correct |
| Consistency | ? Inconsistent | ? Unified |
| User Control | ? Separate settings | ? Single setting |

**Result:** Clean, consistent, user-friendly! ?

