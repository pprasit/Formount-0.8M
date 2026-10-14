# Calibration Point Status: SETTLING State

## ?? Overview

**New Status:** `[SETTLING]` - Mount arrived at target but RMS too high

**Purpose:** Show that mount is settling after slew, waiting for vibrations to dampen

---

## ?? Status Flow

### **Complete Workflow:**

```
[PENDING]
   ?
[SLEWING] ? Slew started
   ?
[SETTLING] ? Slew completed, RMS = 222.25" (too high!)
   ?
[SETTLING] ? RMS = 45.32" (still high)
   ?
[SETTLING] ? RMS = 12.56" (getting better)
   ?
[SETTLING] ? RMS = 5.21" (almost there)
   ?
[TRACKING] ? RMS = 2.15" (< 3.0" threshold!) ?
   ?
[EXPOSING] ? Capture image
   ?
[SOLVING] ? Plate solve
   ?
[COMPLETED] ? Done!
```

---

## ?? Status Logic

### **Code Implementation:**

```csharp
public string Status
{
    get
    {
        if (IsSolving)
            return "[SOLVING]";
        else if (SolveFailed)
            return "[SOLVE FAILED]";
        else if (IsSolved && IsCompleted)
            return "[COMPLETED]";
        else if (IsCapturing)
            return "[EXPOSING]";
        else if (IsTracking)
            return "[TRACKING]";
        else if (IsSlewing)
            return "[SLEWING]";
        else if (!IsSlewing && !IsTracking && !IsCapturing && 
                 !IsSolving && !IsCompleted)
            return "[SETTLING]";  // ? NEW!
        else if (IsCompleted)
            return "[COMPLETED]";
        else
            return "[PENDING]";
    }
}
```

---

### **Condition for SETTLING:**

```csharp
// All these must be false:
!IsSlewing      // Slew finished
!IsTracking     // RMS too high
!IsCapturing    // Not capturing yet
!IsSolving      // Not solving yet
!IsCompleted    // Not done

? Mount arrived but waiting for RMS to drop
```

---

## ?? Example Timeline

### **Point #5 (from screenshot):**

```
T = 0s:  [SLEWING]   ? Slew to 72.0° Az, 48.3° Alt
T = 5s:  [SETTLING]  ? Arrived! RMS = 222.25" (need < 3.0")
T = 10s: [SETTLING]  ? RMS = 145.30" (damping...)
T = 15s: [SETTLING]  ? RMS = 89.45" (still settling)
T = 20s: [SETTLING]  ? RMS = 45.23" (improving)
T = 25s: [SETTLING]  ? RMS = 18.56" (almost)
T = 30s: [SETTLING]  ? RMS = 8.12" (close!)
T = 35s: [SETTLING]  ? RMS = 4.56" (very close!)
T = 38s: [TRACKING]  ? RMS = 2.89" (< 3.0" ?)
T = 40s: [EXPOSING]  ? Capturing image
```

---

## ?? Benefits

### **1. Clear Feedback**

**Before:**
```
Point 5/15: [PENDING]
RMS = 222.25" (confusing - why pending if we're there?)
```

**After:**
```
Point 5/15: [SETTLING]
RMS = 222.25" (clear - settling after slew!)
```

---

### **2. Better Understanding**

User knows:
- ? Mount arrived at target
- ? Waiting for vibrations to stop
- ? RMS is actively being measured
- ? Will switch to TRACKING when stable

---

### **3. Diagnostic Value**

```
[SETTLING] for 60 seconds ? Normal (large slew)
[SETTLING] for 300 seconds ? Problem! (never stabilizes)
```

Can identify:
- Wind conditions
- Mechanical issues
- Drive tuning problems

---

## ?? State Transitions

### **Normal Flow:**

```
???????????
? PENDING ?
???????????
     ?
     ?
???????????
? SLEWING ? ? Slew command sent
???????????
     ?
     ?
????????????
? SETTLING ? ? Slew done, RMS > 3.0"
????????????
     ? (wait for RMS < 3.0")
     ?
????????????
? TRACKING ? ? RMS < 3.0" ?
????????????
     ? (wait for stability duration)
     ?
????????????
? EXPOSING ? ? Capture image
????????????
     ?
     ?
???????????
? SOLVING ? ? Plate solve
???????????
     ?
     ?
?????????????
? COMPLETED ?
?????????????
```

---

### **Problem Flow (Never Settles):**

```
???????????
? SLEWING ?
???????????
     ?
     ?
????????????
? SETTLING ? ? RMS = 222.25"
????????????
     ? (60 seconds...)
     ?
????????????
? SETTLING ? ? RMS = 215.30" (barely changed!)
????????????
     ? (120 seconds...)
     ?
????????????
? SETTLING ? ? RMS = 210.45" (problem!)
????????????

User action: Pause ? Check mount/wind
```

---

## ?? RMS Thresholds

### **From AppSettings:**

```csharp
RmsStabilityThreshold_Arcsec = 3.0  // Default
```

### **State Logic:**

| RMS | Status | Action |
|-----|--------|--------|
| 222.25" | `[SETTLING]` | Wait for damping |
| 45.32" | `[SETTLING]` | Still high |
| 12.56" | `[SETTLING]` | Improving |
| 5.21" | `[SETTLING]` | Almost there |
| **2.89"** | `[TRACKING]` | **Ready!** ? |

---

## ?? Technical Details

### **RMS Evolution After Slew:**

**Typical Pattern:**
```
Slew ends:
  ?
T=0:   RMS = 300-500" (peak vibration)
T=5:   RMS = 150-250" (damping)
T=10:  RMS = 50-100"  (settling)
T=15:  RMS = 20-40"   (approaching)
T=20:  RMS = 10-20"   (close)
T=25:  RMS = 5-10"    (very close)
T=30:  RMS = 2-5"     (ready)
```

**Damping time depends on:**
- Slew distance (larger = longer)
- Mount stiffness (softer = longer)
- Wind conditions (windy = longer)
- Drive tuning (poor = oscillates)

---

### **State Variables:**

```csharp
public class CalibrationPoint
{
    public bool IsSlewing { get; set; }      // false after slew
    public bool IsTracking { get; set; }     // false when RMS > 3.0"
    public bool IsCapturing { get; set; }    // false (not capturing yet)
    public bool IsSolving { get; set; }      // false (not solving yet)
    public bool IsCompleted { get; set; }    // false (not done)
    
    // When all above are false ? [SETTLING]
}
```

---

## ?? UI Display

### **Before Fix:**

```
Point 5/15: Waiting for stability (RMS: 222.25", need < 3.0")
Status: [PENDING]  ? Confusing!
```

### **After Fix:**

```
Point 5/15: Waiting for stability (RMS: 222.25", need < 3.0")
Status: [SETTLING]  ? Clear! ?
```

---

## ?? User Experience

### **Clear Communication:**

```
Point 5/15: [SETTLING]
?
"Mount has arrived at target"
"Waiting for vibrations to dampen"
"RMS will drop as mount settles"
"Then will switch to TRACKING"
```

### **vs. Old:**

```
Point 5/15: [PENDING]
?
"Why is it pending?"
"Did slew complete?"
"Is it stuck?"
"What's happening?"
```

---

## ?? Summary

| Aspect | Before | After |
|--------|--------|-------|
| **Status** | PENDING | **SETTLING** |
| **Clarity** | Ambiguous | Clear |
| **User Understanding** | Confused | Knows what's happening |
| **Diagnostic** | Hard to debug | Easy to identify issues |
| **Implementation** | N/A | Simple condition check |

**Result:**
- ? Clear status indication
- ? Better user understanding
- ? Easier debugging
- ? No performance impact

