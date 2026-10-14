# Calibration Debug Output: Reduced Logging

## ?? Overview

**Feature:** Configurable debug output level for calibration process

**Purpose:** Reduce console spam, keep only important messages

---

## ?? Implementation

### **Debug Levels:**

```csharp
private bool _enableVerboseLogging = false;  // Default: OFF

// Helper methods:
LogVerbose()  // Only if _enableVerboseLogging = true
LogInfo()     // Always shown (important events)
LogError()    // Always shown (errors)
```

---

### **Usage:**

**Enable verbose logging:**
```csharp
calibrationManager.SetVerboseLogging(true);  // Turn ON detailed logs
```

**Disable verbose logging (default):**
```csharp
calibrationManager.SetVerboseLogging(false);  // Turn OFF (quiet mode)
```

---

## ?? Log Levels

### **LogVerbose() - Only if enabled:**

```
- Detailed position updates
- RMS calculations each cycle
- Tracking status updates
- Image processing details
- Coordinate conversions
```

### **LogInfo() - Always shown:**

```
- Calibration started
- Point X of N completed
- Plate solve results (success/fail)
- Model updates
- Calibration complete
```

### **LogError() - Always shown:**

```
- File I/O errors
- Plate solve failures
- Camera errors
- Configuration issues
```

---

## ?? Example Output

### **Verbose OFF (Default):**

```
[MountCalibrationManager] Calibration started: 40 points
[MountCalibrationManager] Point 1/40: Slewing to Az=0.00°, Alt=30.00°
[MountCalibrationManager] Point 1/40: Plate solve SUCCESS (RMS=0.45")
[MountCalibrationManager] Point 2/40: Slewing to Az=0.00°, Alt=41.00°
...
[MountCalibrationManager] Calibration complete: 39/40 solved
```

**Clean and readable!** ?

---

### **Verbose ON:**

```
[MountCalibrationManager] Calibration started: 40 points
[MountCalibrationManager] Session folder: 15012025-01
[MountCalibrationManager] Point 1/40: Target Az=0.00°, Alt=30.00°
[MountCalibrationManager] Slewing: Current=72.00°, Target=0.00°, Delta=72.00°
[MountCalibrationManager] RMS: Az=145.23", Alt=89.45"
[MountCalibrationManager] RMS: Az=78.12", Alt=45.34"
[MountCalibrationManager] RMS: Az=23.45", Alt=12.67"
[MountCalibrationManager] RMS: Az=5.67", Alt=2.89" (tracking!)
[MountCalibrationManager] Capturing image: Exposure=5.0s
[MountCalibrationManager] Image captured: 1920x1080, 16-bit
[MountCalibrationManager] Converting Alt/Az ? RA/Dec
[MountCalibrationManager] RA=6.1234h, Dec=8.5678°
[MountCalibrationManager] Saving FITS: point_001_20250115_120345.fits
[MountCalibrationManager] Starting plate solve...
[MountCalibrationManager] Plate solve: 42 stars matched
[MountCalibrationManager] RA Error: -15.20", Dec Error: 8.30"
[MountCalibrationManager] Point 1/40: Plate solve SUCCESS (RMS=0.45")
...
```

**Full details for debugging!**

---

## ?? Configuration

### **In Code:**

```csharp
// During initialization
var calibrationManager = new MountCalibrationManager(...);

// Enable verbose mode for debugging
calibrationManager.SetVerboseLogging(true);

// Or keep quiet (default)
calibrationManager.SetVerboseLogging(false);
```

---

### **In UI (Future):**

```xaml
<CheckBox x:Name="chkVerboseLogging" 
          Content="Enable verbose logging"
          IsChecked="False"
          Checked="VerboseLogging_Changed"/>
```

```csharp
private void VerboseLogging_Changed(object sender, RoutedEventArgs e)
{
    bool enabled = chkVerboseLogging.IsChecked == true;
    _calibrationManager.SetVerboseLogging(enabled);
}
```

---

## ?? Summary

| Feature | Before | After |
|---------|--------|-------|
| **Log Count** | 266 lines | ~30 important lines |
| **Readability** | Cluttered | Clean |
| **Performance** | Minor overhead | Minimal |
| **Control** | None | Configurable |

**Result:**
- ? Much cleaner output by default
- ? Verbose mode available when needed
- ? Easy to enable/disable
- ? Better user experience

