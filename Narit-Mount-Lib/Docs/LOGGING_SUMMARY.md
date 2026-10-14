## ? **?????????!**

---

## ?? **????????????:**

### **1. Debug Output Control**

**Fields:**
```csharp
private bool _enableVerboseLogging = false;  // Default: OFF
```

**Methods:**
```csharp
LogVerbose(string message)  // Only if verbose enabled
LogInfo(string message)     // Always shown (important)
LogError(string message)    // Always shown (errors)
SetVerboseLogging(bool enabled)  // Toggle verbose mode
```

---

## ?? **Output Comparison:**

### **Before (266 debug lines!):**
```
[MountCalibrationManager] Session folder generated:
[MountCalibrationManager]   Date: 15/01/2025
[MountCalibrationManager]   Session: #1
[MountCalibrationManager]   Folder: 15012025-01
[MountCalibrationManager] Slewing to: Az=0.00°, Alt=30.00°
[MountCalibrationManager] Current position: Az=72.00°, Alt=48.30°
[MountCalibrationManager] Delta: Az=-72.00°, Alt=-18.30°
[MountCalibrationManager] RMS: Az=145.23", Alt=89.45"
[MountCalibrationManager] RMS: Az=78.12", Alt=45.34"
... (260 more lines)
```

### **After (Clean!):**
```
[MountCalibrationManager] Calibration started: 40 points
[MountCalibrationManager] Point 1/40: Plate solve SUCCESS (0.45")
[MountCalibrationManager] Point 2/40: Plate solve SUCCESS (0.52")
...
[MountCalibrationManager] Calibration complete: 39/40 solved
```

---

## ?? **?????????:**

### **Default (Quiet Mode):**
```csharp
// Verbose logging = OFF (default)
// Only important messages shown
```

### **Enable Verbose (For Debugging):**
```csharp
calibrationManager.SetVerboseLogging(true);
// All detailed logs shown
```

---

## ?? **Log Categories:**

| Type | When Shown | Example |
|------|------------|---------|
| **LogVerbose()** | If enabled | RMS updates, coordinates |
| **LogInfo()** | Always | Point completed, solve result |
| **LogError()** | Always | File errors, solve failures |

---

**Documentation:** `CALIBRATION_REDUCED_LOGGING.md`

**Status:** 
- ? Verbose logging OFF by default
- ? Clean output (266 ? ~30 lines)
- ? Can enable for debugging
- ? Build successful

**?????? output ??????????!** ??????????????????? ? ?????? ??

---

### **???????? Verbose (?????????? Debug):**

```csharp
// In MainWindow or wherever you initialize
_calibrationManager.SetVerboseLogging(true);  // Turn ON
```

**?????????????? checkbox ?? UI ????????** ??
