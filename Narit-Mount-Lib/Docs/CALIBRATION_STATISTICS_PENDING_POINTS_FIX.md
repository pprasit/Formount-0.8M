# Calibration Statistics Fix: Pending/Skipped Points

## ?? Bug Report

### Issue Observed:
```
Total Points:  40
Completed:     40
Solved:        39
Failed:         0
?????????????????
Missing:        1  ? Where is this point?
```

**Problem:** `39 + 0 ? 40`

---

## ?? Root Cause

### Point States:
```csharp
public class CalibrationPoint
{
    bool IsCompleted;    // Finished processing
    bool IsSolved;       // Plate solve succeeded
    bool SolveFailed;    // Plate solve failed
    bool IsSolving;      // Currently solving
}
```

### Missing State:
**Pending/Skipped points** = Points ???:
- ? `IsCompleted = true` (process ????)
- ? `IsSolved = false` (?????? solve ??????)
- ? `SolveFailed = false` (?????? fail)
- ? `IsSolving = false` (??????????? solve)

**??????????????????:**
1. Plate solver ??????????? (skipped)
2. FITS file ????????????
3. Exception ???????????? solve
4. Process ???????????????????

---

## ? Solution

### 1. ????? PendingPoints property

**File:** `CalibrationCompleteDialog.xaml.cs`

```csharp
public int PendingPoints { get; set; }  // [NEW]
```

### 2. ????? PendingPoints ?? Manager

**File:** `MountCalibrationManager.cs`

```csharp
int solvedPoints = _calibrationPoints.Count(p => p.IsSolved);
int failedPoints = _calibrationPoints.Count(p => p.SolveFailed);
int pendingPoints = _calibrationPoints.Count(p => !p.IsSolved && !p.SolveFailed);

// Validation
Debug.WriteLine($"Check: {solvedPoints} + {failedPoints} + {pendingPoints} = {totalPoints}");
```

### 3. ???? Pending ?? UI (?????)

**File:** `CalibrationCompleteDialog.xaml`

```xaml
<!-- Row 3 (Pending - only shown if > 0) -->
<StackPanel x:Name="rowPending" Visibility="Collapsed">
    <TextBlock Text="Pending/Skipped"/>
    <TextBlock x:Name="txtPendingPoints" 
               Text="1" 
               Foreground="{StaticResource WarningColor}"/>
</StackPanel>
```

**Logic:**
```csharp
if (PendingPoints > 0)
{
    txtPendingPoints.Text = PendingPoints.ToString();
    rowPending.Visibility = Visibility.Visible;  // Show
}
else
{
    rowPending.Visibility = Visibility.Collapsed;  // Hide
}
```

---

## ?? Result

### Before Fix:
```
Total:     40
Completed: 40
Solved:    39
Failed:     0
???????????????
Missing:    1  ?
```

### After Fix:
```
Total:          40
Completed:      40
Solved:         39
Failed:          0
Pending/Skipped: 1  ? Now visible! ?
??????????????????????
Check: 39 + 0 + 1 = 40 ?
```

---

## ?? Debug Output

### Enhanced Logging:

```
[DIALOG] Statistics:
  Total=40
  Completed=40
  Solved=39
  Failed=0
  Pending/Skipped=1
  Check: 39 + 0 + 1 = 40 (should be 40) ?

[DIALOG] Pending points detail:
  Point #19: IsCompleted=True, IsSolving=False, IsTracking=False
```

**Explanation:** Point #19 completed tracking but never started plate solving

---

## ?? UI Display Logic

### Conditional Display:

**If `PendingPoints = 0`:**
```
???????????????????????
? Solved:    39       ?
? Failed:     0       ?
???????????????????????
(Pending row hidden)
```

**If `PendingPoints > 0`:**
```
???????????????????????
? Solved:         39  ?
? Failed:          0  ?
? Pending/Skipped: 1  ?  ? Shown in orange
???????????????????????
```

---

## ?? Statistics Breakdown

| Status | Count | Meaning |
|--------|-------|---------|
| **Total** | 40 | All calibration points |
| **Completed** | 40 | Finished processing |
| **Solved** | 39 | Plate solve success |
| **Failed** | 0 | Plate solve failed |
| **Pending** | 1 | Not solved, not failed |

**Formula:**
```
Total = Solved + Failed + Pending
40 = 39 + 0 + 1 ?
```

---

## ?? Common Causes of Pending Points

### 1. **Tracking Never Stabilized**
```
Point #19: [TRACKING] ? RMS = 140.29" (threshold: 3.0")
Status: Waiting for stability...
? Timeout ? IsCompleted=True but never captured image
```

### 2. **Camera Error**
```
Point #5: [EXPOSING] ? Camera error
Status: Skip to next point
? IsCompleted=True but no FITS file
```

### 3. **FITS Save Failed**
```
Point #12: Image captured ? FITS save error
Status: Continue to next point
? IsCompleted=True but no plate solve
```

### 4. **User Cancelled During Exposure**
```
Point #8: [EXPOSING] ? User clicks "Pause"
Status: Skip current point
? IsCompleted=True but exposure incomplete
```

---

## ? Validation

### Test Cases:

**1. All points solve successfully:**
```
Solved: 40, Failed: 0, Pending: 0
rowPending: Hidden ?
```

**2. Some points fail:**
```
Solved: 38, Failed: 2, Pending: 0
rowPending: Hidden ?
```

**3. One point skipped:**
```
Solved: 39, Failed: 0, Pending: 1
rowPending: Visible (orange) ?
```

**4. Multiple issues:**
```
Solved: 35, Failed: 3, Pending: 2
rowPending: Visible (orange) ?
```

---

## ?? Files Modified

### 1. **CalibrationCompleteDialog.xaml.cs**
- Added `PendingPoints` property
- Added conditional UI display logic
- Enhanced debug logging

### 2. **MountCalibrationManager.cs**
- Calculate `pendingPoints` count
- Pass to dialog
- Debug log pending point details

### 3. **CalibrationCompleteDialog.xaml**
- Added Row 3 for Pending/Skipped
- Conditional visibility
- Orange warning color

---

## ?? Technical Details

### State Machine:

```
[Created] ? Pending (not started)
    ?
[Slewing] ? Pending
    ?
[Tracking] ? Pending
    ?
[Capturing] ? Pending
    ?
[Plate Solving] ? IsSolving=True
    ?
    ??? [Solved] ? IsSolved=True ?
    ??? [Failed] ? SolveFailed=True ?
    ??? [Skipped] ? Pending (both false) ??
```

### Count Formula:

```csharp
// Total must equal sum of all states
int total = calibrationPoints.Count;
int solved = calibrationPoints.Count(p => p.IsSolved);
int failed = calibrationPoints.Count(p => p.SolveFailed);
int pending = calibrationPoints.Count(p => !p.IsSolved && !p.SolveFailed);

Debug.Assert(total == solved + failed + pending);
```

---

## ?? Summary

| Before | After |
|--------|-------|
| 39 + 0 ? 40 ? | 39 + 0 + 1 = 40 ? |
| Missing point | Pending point shown |
| User confused | Clear explanation |
| No visibility | Orange warning |

**Result:** 
- ? Complete transparency
- ? Clear accounting
- ? User knows what happened
- ? Debug info available

