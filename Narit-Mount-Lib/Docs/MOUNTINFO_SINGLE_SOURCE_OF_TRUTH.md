# Centralized Mount Position: MountInfo as Single Source of Truth

## ?? **Architecture Decision:**

**All mount position data flows through `MountInfo`**

---

## ?? **Data Flow:**

```
???????????????????????????????????????
? CentralizedPollingManager           ?
?  - Reads encoder positions (50Hz)   ?
???????????????????????????????????????
                ?
                ?
???????????????????????????????????????
? MountInfo.UpdateRawPosition()       ?
?  - Stores raw encoder data          ?
?  - _actAzimuthPosRaw (degrees)      ?
?  - _actAltitudePosRaw (degrees)     ?
???????????????????????????????????????
                ?
                ?
???????????????????????????????????????
? PointingModelManager                ?
?  - Calculates correction            ?
?  - Applies pointing model           ?
???????????????????????????????????????
                ?
                ?
???????????????????????????????????????
? MountInfo.ApplyPointingModelCorrection() ?
?  - Stores corrected position        ?
?  - _actAzimuthPosCorrect (degrees)  ?
?  - _actAltitudePosCorrect (degrees) ?
???????????????????????????????????????
                ?
                ? (consumers)
     ???????????????????????
     ?          ?          ?
     ?          ?          ?
CalibrationManager  UI   Export
```

---

## ?? **Implementation:**

### **MountInfo Properties:**

```csharp
public class MountInfo
{
    // Raw encoder positions (measured)
    public double Act_Azimuth_Pos_Raw { get; set; }
    public double Act_Altitude_Pos_Raw { get; set; }
    
    // Corrected positions (with pointing model)
    public double Act_Azimuth_Pos_Correct { get; set; }
    public double Act_Altitude_Pos_Correct { get; set; }
}
```

---

### **CalibrationManager Usage:**

**Before (Wrong):**
```csharp
// ? Using commanded position
point.CurrentAzimuth = mountAz;  // Target position
point.CurrentAltitude = mountAlt;
```

**After (Correct):**
```csharp
// ? Using actual encoder position from MountInfo
if (_pollingManager?.MountInfo != null)
{
    point.CurrentAzimuth = _pollingManager.MountInfo.Act_Azimuth_Pos_Raw;
    point.CurrentAltitude = _pollingManager.MountInfo.Act_Altitude_Pos_Raw;
}
```

**Timing:** At image capture time ?

---

### **PlaneWave Export:**

```csharp
// ThetaEnc, PhiEnc from actual encoder positions
double thetaEnc = point.CurrentAzimuth * Math.PI / 180.0;  // From MountInfo raw
double phiEnc = point.CurrentAltitude * Math.PI / 180.0;    // From MountInfo raw
```

**Result:** Accurate encoder positions in .pxp file ?

---

## ? **Benefits:**

### **1. Single Source of Truth:**
```
MountInfo = central repository for all position data
? No confusion about which position to use
? Consistent across entire system
```

### **2. Proper Separation:**
```
Raw Position:      Direct from encoder (uncompensated)
Corrected Position: After pointing model (compensated)
```

### **3. Easy to Use:**
```csharp
// For calibration (need raw):
double az = MountInfo.Act_Azimuth_Pos_Raw;

// For display (need corrected):
double az = MountInfo.Act_Azimuth_Pos_Correct;
```

### **4. Future-Proof:**
```csharp
// Add new features easily:
public double Act_Azimuth_Pos_Refraction { get; set; }  // With refraction
public double Act_Azimuth_Pos_Flexure { get; set; }     // With flexure
```

---

## ?? **Position Types:**

| Type | Source | Use Case |
|------|--------|----------|
| **Raw** | Encoder | Calibration, diagnostics |
| **Corrected** | Raw + Pointing Model | Display, goto commands |
| **Target** | Command | Slew destination |

---

## ?? **Example:**

### **Calibration Process:**

```
1. Mount slews to Target: Az=120.00°, Alt=30.00°
   
2. CentralizedPollingManager reads encoder:
   ? MountInfo.Act_Azimuth_Pos_Raw = 120.02°  (actual!)
   ? MountInfo.Act_Altitude_Pos_Raw = 30.01°
   
3. CalibrationManager captures image:
   point.CurrentAzimuth = 120.02°  (from MountInfo)
   point.CurrentAltitude = 30.01°
   
4. Plate solve finds:
   RA/Dec error = 15.2"
   
5. Export PlaneWave:
   ThetaEnc = 30.01° × ?/180 = 0.5237 rad  (actual position!)
   PhiEnc = 120.02° × ?/180 = 2.0945 rad
```

**Result:** Accurate model because we used real encoder positions! ?

---

## ?? **Why This Matters:**

### **Scenario 1: Perfect Mount**
```
Target:  Az=120.00°, Alt=30.00°
Actual:  Az=120.00°, Alt=30.00° (perfect!)
? No difference, model works either way
```

### **Scenario 2: Real World Mount**
```
Target:  Az=120.00°, Alt=30.00°
Actual:  Az=120.02°, Alt=30.01° (slight error)

If using Target:
  ? Model thinks mount is at 120.00° (wrong!)
  ? Calculates wrong correction
  ? Model inaccurate ?

If using Actual:
  ? Model knows mount is at 120.02° (correct!)
  ? Calculates right correction
  ? Model accurate ?
```

---

## ?? **Summary:**

| Aspect | Before | After |
|--------|--------|-------|
| **Source** | Commanded position | MountInfo raw position |
| **Accuracy** | Assumes perfect mount | Uses actual measurements |
| **Architecture** | Scattered | Centralized |
| **Future** | Hard to extend | Easy to add features |

**Result:**
- ? Single source of truth
- ? Accurate encoder positions
- ? Proper architecture
- ? Future-proof design
- ? Easy to use

**This is the right way! ?????????** ??
