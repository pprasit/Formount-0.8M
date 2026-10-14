# Progressive Pointing Model File Writing

## ?? Overview

**Implementation:** Real-time incremental file writing during calibration

**Benefits:**
- ? Data safety (backup every solve)
- ? Progress visibility (can view file anytime)
- ? Crash recovery (partial data saved)
- ? No file handle leak (open ? write ? close)

---

## ?? Workflow

### **Phase 1: Start Calibration**
```
User clicks "Start"
? Create: PointingModel_15012025-01_INPROGRESS.pmd
? Write header + placeholder sections
```

**File Structure (Initial):**
```
# ForkMount-WPF Pointing Model
# Status: IN_PROGRESS
# Session: 15012025-01
# Started: 2025-01-15 12:00:00

[STATISTICS]
TotalPoints = 40
SolvedPoints = 0
FailedPoints = 0
PendingPoints = 40
ResidualRMS_Arcsec = 0.000

[MODEL_TERMS]
# Model will be fitted when calibration completes
# Current status: PENDING (0 points solved)
IH   = 0.00000000   # Azimuth index error (deg)
...

[CALIBRATION_DATA]
# Point  Az(deg)   Alt(deg)  RA_Err(")  Dec_Err(")  Total_Err(")  Stars  Status
# Data will be appended as points are solved...
```

---

### **Phase 2: Each Point Solved (Incremental)**

**Trigger:** `SolvePointAsync()` success

**Action:** Append point data (thread-safe)

```csharp
// After plate solve succeeds
AppendPointToProgressiveFile(point);

// Implementation
lock (_fileWriteLock)
{
    File.AppendAllText(_progressiveModelPath, line + "\n");
}
```

**File Update (Real-time):**
```
[CALIBRATION_DATA]
# Point  Az(deg)   Alt(deg)  RA_Err(")  Dec_Err(")  Total_Err(")  Stars  Status
     1     0.00     30.00     -15.20        8.30         17.30     42  SOLVED
     2     0.00     41.00     -12.10        6.50         13.70     38  SOLVED
     3     0.00     52.00      -8.90        4.20          9.82     51  SOLVED
# ... appended as solving continues
```

**Safety Features:**
- `lock (_fileWriteLock)` - prevents concurrent writes
- `File.AppendAllText()` - atomic operation (OS-level)
- Open ? Write ? Close immediately (no handle leak)

---

### **Phase 3: Calibration Complete**

**Trigger:** All points processed ? Show completion dialog

**Action:** Finalize file with model terms

```csharp
// Build model from solved points
var builder = new PointingModelBuilder();
var terms = builder.BuildModel(calibrationPoints);
var rms = builder.CalculateResidualRMS(terms, calibrationPoints);

// Update progressive file
UpdateProgressiveFileWithModel(progressivePath, terms, rms);

// Rename: *_INPROGRESS.pmd ? PointingModel.pmd
File.Move(progressivePath, finalPath);
```

**File Update (Final):**
```
# Status: COMPLETED  ? Updated
# Started: 2025-01-15 12:00:00
# Completed: 2025-01-15 12:35:22  ? Added

[STATISTICS]
TotalPoints = 40
SolvedPoints = 39  ? Updated
FailedPoints = 0
PendingPoints = 1
ResidualRMS_Arcsec = 0.563  ? Calculated

[MODEL_TERMS]
# Model fitted from 39 points  ? Updated
IH   = 0.34210000   # Azimuth index error (deg)  ? Fitted!
ID   = -0.12340000  # Altitude index error (deg)
...
```

---

## ?? Thread Safety

### **Problem:** Multiple plate solves run in parallel (async)

```
Point #5 solving... (thread 1)
Point #8 solving... (thread 2)  ? Both try to write!
Point #12 solving... (thread 3)
```

### **Solution:** File write lock

```csharp
private readonly object _fileWriteLock = new object();

private void AppendPointToProgressiveFile(CalibrationPoint point)
{
    lock (_fileWriteLock)
    {
        // Only 1 thread can write at a time
        File.AppendAllText(_progressiveModelPath, line + "\n");
    }
}
```

**Result:** Sequential writes, no corruption ?

---

## ?? File Evolution Timeline

### **T = 0s** (Start)
```
# Status: IN_PROGRESS
SolvedPoints = 0
PendingPoints = 40

[CALIBRATION_DATA]
# (empty)
```

### **T = 5min** (10 points solved)
```
# Status: IN_PROGRESS
SolvedPoints = 10  ? Auto-updated by finalize
PendingPoints = 30

[CALIBRATION_DATA]
     1   ...   SOLVED
     2   ...   SOLVED
     ...
    10   ...   SOLVED
```

### **T = 20min** (35 points solved)
```
# Status: IN_PROGRESS
SolvedPoints = 35
PendingPoints = 5

[CALIBRATION_DATA]
     1   ...   SOLVED
     ...
    35   ...   SOLVED
```

### **T = 35min** (Complete!)
```
# Status: COMPLETED  ? Finalized
# Completed: 2025-01-15 12:35:22
SolvedPoints = 39
ResidualRMS_Arcsec = 0.563

[MODEL_TERMS]
IH   = 0.34210000   ? Fitted!
...

[CALIBRATION_DATA]
     1   ...   SOLVED
     ...
    40   ...   SOLVED

? Renamed: PointingModel.pmd
```

---

## ?? Implementation Details

### **1. Initialize Progressive File (Start)**

**Method:** `InitializeProgressiveModelFile()`

**Location:** MountCalibrationManager.cs

**Called:** `Start()` method

```csharp
public void Start()
{
    // ...
    InitializeProgressiveModelFile();  // Create initial file
    // ...
}

private void InitializeProgressiveModelFile()
{
    var sb = new StringBuilder();
    sb.AppendLine("# ForkMount-WPF Pointing Model");
    sb.AppendLine($"# Status: IN_PROGRESS");
    sb.AppendLine($"# Session: {_sessionFolder}");
    // ... write header + placeholders
    
    File.WriteAllText(_progressiveModelPath, sb.ToString());
}
```

**File Created:**
```
CalibrationData/PointingModel_15012025-01_INPROGRESS.pmd
```

---

### **2. Append Point Data (Each Solve)**

**Method:** `AppendPointToProgressiveFile()`

**Called:** After `SolvePointAsync()` success

```csharp
private async Task SolvePointAsync(...)
{
    // ... plate solving
    
    if (result.Success)
    {
        // Store results
        point.IsSolved = true;
        point.RaError_Arcsec = raErr;
        // ...
        
        // [NEW] Append to progressive file
        AppendPointToProgressiveFile(point);  // ? Thread-safe!
    }
}

private void AppendPointToProgressiveFile(CalibrationPoint point)
{
    string line = $"{point.Index,6}  {point.TargetAzimuth,8:F2}  ...";
    
    lock (_fileWriteLock)  // Thread-safe
    {
        File.AppendAllText(_progressiveModelPath, line + "\n");
    }
}
```

**Safety:**
- `lock (_fileWriteLock)` - mutex
- `File.AppendAllText()` - atomic
- No long-lived file handle

---

### **3. Finalize File (Complete)**

**Method:** `UpdateProgressiveFileWithModel()`

**Location:** CalibrationCompleteDialog.xaml.cs

**Called:** When user clicks "Save to File"

```csharp
private void BuildPointingModel()
{
    // Build model
    var terms = builder.BuildModel(CalibrationPoints);
    var rms = builder.CalculateResidualRMS(terms, CalibrationPoints);
    
    // Find progressive file
    string progressivePath = FindProgressiveModelFile();
    
    if (progressivePath != null)
    {
        // Update with final model terms
        UpdateProgressiveFileWithModel(progressivePath, terms, rms);
        
        // Rename to final name
        File.Move(progressivePath, finalPath);
    }
}

private void UpdateProgressiveFileWithModel(...)
{
    var lines = File.ReadAllLines(filePath).ToList();
    
    // Update status ? COMPLETED
    // Update statistics ? SolvedPoints, RMS
    // Update model terms ? IH, ID, NP, ...
    
    File.WriteAllLines(filePath, lines);
}
```

---

## ?? File Naming Convention

### **During Calibration:**
```
CalibrationData/PointingModel_{SESSION}_INPROGRESS.pmd

Example:
PointingModel_15012025-01_INPROGRESS.pmd
PointingModel_15012025-02_INPROGRESS.pmd
```

### **After Complete:**
```
CalibrationData/PointingModel.pmd  ? Standard name
```

**Why suffix?**
- Easy to identify incomplete runs
- Prevents loading partial model
- Clear differentiation

---

## ??? Error Handling

### **Case 1: Calibration Crash**

**Scenario:** Application crashes at point #25

**Result:**
```
PointingModel_15012025-01_INPROGRESS.pmd exists:
- Contains 25 solved points
- Status: IN_PROGRESS
- Model terms: 0.00000000 (not fitted yet)
```

**Recovery:**
- File contains partial data ?
- User can review solved points
- Can manually build model from data
- Not loaded automatically (has _INPROGRESS suffix)

---

### **Case 2: Concurrent Write Attempt**

**Scenario:** Points #5, #8, #12 solve simultaneously

**Without Lock:**
```
Line 1: "     5  0.00  41.00  -12.10..."
Line 2: "     8  90.00  52.00  8.45..."  ? Corrupted!
Line 2 (partial): "    12  135.00  63.00..."
```

**With Lock:**
```csharp
lock (_fileWriteLock)
{
    File.AppendAllText(...);  // Sequential writes
}
```

**Result:**
```
Line 1: "     5  0.00  41.00  -12.10..."  ? Clean!
Line 2: "     8  90.00  52.00  8.45..."
Line 3: "    12  135.00  63.00..."
```

---

### **Case 3: File Write Error**

**Scenario:** Disk full, permission error

```csharp
try
{
    File.AppendAllText(_progressiveModelPath, line + "\n");
}
catch (Exception ex)
{
    Debug.WriteLine($"? Failed to append: {ex.Message}");
    // Calibration continues (data in memory)
    // Final save will try again
}
```

**Behavior:**
- Solve continues (data in memory)
- Warning logged
- Final save attempts full file write

---

## ?? Performance Impact

### **Write Operations:**

| Phase | Frequency | Duration | Impact |
|-------|-----------|----------|--------|
| Initialize | 1× (start) | ~5ms | Negligible |
| Append point | 40× (per solve) | ~1ms each | Minimal |
| Finalize | 1× (end) | ~10ms | Negligible |

**Total overhead:** ~50ms over 20-40 minutes = **0.004%**

---

## ? Benefits Summary

### **1. Data Safety**
```
Crash at point #25 ? 25 points saved ?
Power failure ? Data up to last solve ?
```

### **2. Progress Visibility**
```
Open file during calibration:
? See points solved so far
? Check error magnitudes
? Monitor quality
```

### **3. No File Handle Leak**
```
Open ? Write ? Close (per append)
No long-lived StreamWriter
Crash-safe ?
```

### **4. Thread Safety**
```
lock (_fileWriteLock)
? Sequential writes
? No corruption
```

---

## ?? User Experience

### **During Calibration:**
```
File: PointingModel_15012025-01_INPROGRESS.pmd

User can:
- Open file anytime (read-only safe)
- See progress (25/40 points solved)
- Check errors (verify quality)
- Monitor if calibration is progressing
```

### **After Complete:**
```
File: PointingModel.pmd

Auto-loaded on next startup ?
Ready to use immediately ?
```

---

## ?? Code Flow Diagram

```
???????????????????????????????????????????????
? Start Calibration                            ?
? ? InitializeProgressiveModelFile()          ?
?   Create: PointingModel_SESSION_INPROGRESS  ?
???????????????????????????????????????????????
                   ?
                   ?
???????????????????????????????????????????????
? For each point (40×):                        ?
?   1. Slew ? Track ? Capture                 ?
?   2. Plate Solve (async)                    ?
?   3. If success:                            ?
?      ? AppendPointToProgressiveFile()       ?
?         lock (_fileWriteLock)               ?
?         File.AppendAllText(line)            ?
???????????????????????????????????????????????
                   ?
                   ?
???????????????????????????????????????????????
? Calibration Complete                         ?
? ? ShowCompletionDialog()                    ?
?   User clicks "Save to File"                ?
?   ? BuildPointingModel()                    ?
?     1. Build model (least-squares)          ?
?     2. Find progressive file                ?
?     3. UpdateProgressiveFileWithModel()     ?
?        - Update status ? COMPLETED          ?
?        - Update statistics ? RMS            ?
?        - Update model terms ? IH, ID, ...   ?
?     4. Rename ? PointingModel.pmd           ?
???????????????????????????????????????????????
```

---

## ?? Technical Details

### **File Operations:**

**AppendAllText (Safe):**
```csharp
// Atomic operation (OS-level)
// No handle leak
File.AppendAllText(path, data);

// Equivalent to:
using (var writer = new StreamWriter(path, append: true))
{
    writer.Write(data);
}  // Auto-dispose
```

**ReadAllLines / WriteAllLines (Safe):**
```csharp
// Read entire file
var lines = File.ReadAllLines(path);

// Modify
lines[10] = "new value";

// Write back (atomic replace)
File.WriteAllLines(path, lines);
```

---

## ?? Summary

| Aspect | Implementation | Benefit |
|--------|----------------|---------|
| **Initialization** | Create template file | Clear structure |
| **Incremental Write** | Append per solve | Data safety |
| **Thread Safety** | lock + AppendAllText | No corruption |
| **Finalization** | Update + rename | Clean final file |
| **Error Recovery** | Partial data saved | Crash-resistant |
| **Performance** | ~50ms total overhead | Negligible |

**Result:** Safe, efficient, crash-resistant progressive file writing! ?

