# Real-Time Pointing Model Updates

## ?? Overview

**New Feature:** Progressive model building with real-time updates

**Benefits:**
- ? Model terms updated after each point
- ? File shows actual values (not zeros)
- ? Final model ready before dialog shows
- ? Partial model usable if crash

---

## ?? File Evolution

### **After 3 Points (Minimum)**
```
# Status: IN_PROGRESS
ResidualRMS_Arcsec = 25.431

[MODEL_TERMS]
# Partial model from 3 points (updating...)
IH   = 0.12340000   ? Real value!
ID   = -0.05670000
NP   = 0.02340000
...

[CALIBRATION_DATA]
     1     0.00     30.00     -15.20    ...  SOLVED
     2     0.00     41.00     -12.10    ...  SOLVED
     3     0.00     52.00      -8.90    ...  SOLVED
```

### **After 10 Points**
```
# Status: IN_PROGRESS
ResidualRMS_Arcsec = 12.234  ? Improving!

[MODEL_TERMS]
# Partial model from 10 points (updating...)
IH   = 0.28450000   ? Updated!
ID   = -0.09230000
...
```

### **After 25 Points**
```
# Status: IN_PROGRESS
ResidualRMS_Arcsec = 4.521  ? Much better!

[MODEL_TERMS]
# Partial model from 25 points (updating...)
IH   = 0.33890000   ? Converging!
...
```

### **After 40 Points (Complete)**
```
# Status: COMPLETED  ? Finalized!
# Completed: 2025-02-05 13:45:23
ResidualRMS_Arcsec = 0.563  ? Final!

[MODEL_TERMS]
# Final model from 39 points  ? Done!
IH   = 0.34210000   ? Final values!
ID   = -0.12340000
...

? Renamed to: PointingModel.pmd
```

---

## ?? Workflow

### **Phase 1: Each Point Solved**

**Trigger:** `SolvePointAsync()` success

**Action:**
```csharp
// 1. Append point data
AppendPointToProgressiveFile(point);

// 2. Rebuild partial model (if >= 3 points)
UpdatePartialModel();
  ?? Build model from solved points
  ?? Calculate residual RMS
  ?? Update file with current terms
```

**Example:**
```
Point #5 solved ? Append line
               ? Rebuild model (5 points)
               ? RMS = 15.234"
               ? Update file

Point #10 solved ? Append line
                ? Rebuild model (10 points)
                ? RMS = 8.456"  ? Better!
                ? Update file
```

---

### **Phase 2: Calibration Complete**

**Trigger:** All points processed

**Action:**
```csharp
ShowCompletionDialogAsync()
  ?? Build final model (all solved points)
  ?? Update file with final terms
  ?? Mark status: COMPLETED
  ?? Rename: *_INPROGRESS.pmd ? PointingModel.pmd
  ?? Show dialog (model ready!)
```

**Timeline:**
```
Point #40 completes
  ?
Build final model (2 seconds)
  ?
Update file (status = COMPLETED)
  ?
Rename to PointingModel.pmd
  ?
Show completion dialog  ? Model already saved!
```

---

## ?? Model Convergence

### **RMS Evolution:**

```
Points | RMS (arcsec) | Status
-------|--------------|--------
   3   |    25.431    | Very rough
   5   |    18.234    | Rough
  10   |     8.456    | Improving
  15   |     4.892    | Good
  20   |     2.345    | Very good
  25   |     1.234    | Excellent
  30   |     0.789    | Excellent
  35   |     0.623    | Final approach
  39   |     0.563    | Final!
```

### **Term Convergence:**

```
Points | IH (deg)  | Status
-------|-----------|--------
   3   | 0.12340   | Initial estimate
  10   | 0.28450   | Improving
  20   | 0.32890   | Converging
  30   | 0.33980   | Near final
  39   | 0.34210   | Final!
```

---

## ?? Implementation Details

### **1. AppendPointToProgressiveFile() (Enhanced)**

```csharp
private void AppendPointToProgressiveFile(CalibrationPoint point)
{
    // Append point line (thread-safe)
    lock (_fileWriteLock)
    {
        File.AppendAllText(_progressiveModelPath, line + "\n");
    }
    
    // [NEW] Rebuild partial model
    UpdatePartialModel();  // ? Real-time update!
}
```

---

### **2. UpdatePartialModel() (New)**

```csharp
private void UpdatePartialModel()
{
    // Get all solved points
    var solved = _calibrationPoints.Where(p => p.IsSolved).ToList();
    
    if (solved.Count < 3)
        return;  // Need at least 3 points
    
    // Build partial model
    var builder = new PointingModelBuilder();
    var terms = builder.BuildModel(solved);
    var rms = builder.CalculateResidualRMS(terms, solved);
    
    // Update file (thread-safe)
    lock (_fileWriteLock)
    {
        UpdateModelTermsInFile(terms, rms, solved.Count);
    }
}
```

**Frequency:**
- Called after every successful solve
- Model gets better with each point
- File always has latest values

---

### **3. UpdateModelTermsInFile() (New)**

```csharp
private void UpdateModelTermsInFile(ModelTerms terms, double rms, int count)
{
    var lines = File.ReadAllLines(_progressiveModelPath).ToList();
    
    // Update statistics
    lines[i] = $"SolvedPoints = {count}";
    lines[j] = $"ResidualRMS_Arcsec = {rms:F3}";
    
    // Update model terms
    lines[k] = $"IH   = {terms.IH:F8}   # Azimuth index error";
    lines[l] = $"ID   = {terms.ID:F8}   # Altitude index error";
    // ... all 11 terms
    
    File.WriteAllLines(_progressiveModelPath, lines);
}
```

---

### **4. ShowCompletionDialogAsync() (Enhanced)**

```csharp
private async Task ShowCompletionDialogAsync()
{
    // [NEW] Build final model BEFORE showing dialog
    if (solvedPoints.Count >= 10)
    {
        var builder = new PointingModelBuilder();
        var finalTerms = builder.BuildModel(solvedPoints);
        var finalRMS = builder.CalculateResidualRMS(finalTerms, solvedPoints);
        
        // Update file with final model
        UpdateModelTermsInFile(finalTerms, finalRMS, solvedPoints.Count);
        
        // Mark as COMPLETED
        UpdateStatusToCompleted();
        
        // Rename to final
        File.Move(_progressiveModelPath, finalPath);
    }
    
    // Show dialog (model already saved!)
    completionDialog.ShowDialog();
}
```

**Key Change:** Model built **BEFORE** dialog shows!

---

### **5. BuildPointingModel() (Simplified)**

```csharp
private void BuildPointingModel()
{
    // Check if model already exists (built before dialog)
    if (File.Exists(finalPath))
    {
        // Just show success message!
        MessageBox.Show("? Model Already Saved!");
        return;
    }
    
    // Otherwise, build from scratch (fallback)
    // ...
}
```

**User Clicks "Save to File":**
- Model already exists ? Show success
- Doesn't need to rebuild!

---

## ?? Performance Impact

### **Per-Point Overhead:**

| Operation | Duration | Impact |
|-----------|----------|--------|
| Append line | 1 ms | Minimal |
| Build partial model | 5-50 ms | Low |
| Update file | 2-10 ms | Low |
| **Total** | **8-61 ms** | **Negligible** |

**Context:** Each point takes 30-60 seconds (slew + track + solve)
? 50ms overhead = **0.1%**

---

### **Rebuild Frequency:**

```
3 points solved ? Rebuild (fast)
10 points solved ? Rebuild (medium)
25 points solved ? Rebuild (slower)
40 points solved ? Final rebuild (slowest)

Total rebuilds: 37 times (one per solved point after #3)
Total time: ~1-2 seconds over 30 minutes
```

---

## ?? User Experience

### **During Calibration:**

User can open file and see:
```
# Status: IN_PROGRESS
# 15/40 points solved

ResidualRMS_Arcsec = 5.234  ? Real value!

[MODEL_TERMS]
IH   = 0.31450000  ? Real value!
ID   = -0.11230000
...
```

**Benefits:**
- See progress (not all zeros)
- Check model quality (RMS improving)
- Verify terms converging
- Useful partial model if crash

---

### **Before Dialog Shows:**

```
Point #40 completed
  ?
[Processing...]
  Building final model...
  RMS = 0.563 arcsec
  Updating file...
  Renaming to PointingModel.pmd
  ? Done!
  ?
[Show Dialog]  ? Model ready!
```

**User sees:**
- Final statistics (not zeros)
- Real RMS values
- "Save to File" ? Already saved!

---

## ?? Debug Output

### **During Calibration:**

```
[MountCalibrationManager] ? Appended point 5 to progressive file
[MountCalibrationManager] Updating partial model from 5 points...
[MountCalibrationManager] Partial model RMS: 18.234 arcsec
[MountCalibrationManager] ? Updated partial model in file

...

[MountCalibrationManager] ? Appended point 10 to progressive file
[MountCalibrationManager] Updating partial model from 10 points...
[MountCalibrationManager] Partial model RMS: 8.456 arcsec  ? Improving!
[MountCalibrationManager] ? Updated partial model in file
```

---

### **Before Dialog:**

```
[DIALOG] Building final model from 39 points...
[DIALOG] ? Final model built: RMS = 0.563 arcsec
[DIALOG] ? Model finalized and saved: PointingModel.pmd
[DIALOG] Showing completion dialog...
```

---

### **User Clicks "Save":**

```
[CalibrationCompleteDialog] Checking pointing model...
[CalibrationCompleteDialog] ? Model file already exists: PointingModel.pmd
[CalibrationCompleteDialog] Model info: 39 points, RMS = 0.563"
? MessageBox: "? Model Already Saved!"
```

---

## ?? Error Handling

### **Case 1: Few Points (< 3)**

```csharp
if (solvedPoints.Count < 3)
{
    Debug.WriteLine("Only 2 points - need at least 3 for model");
    return;  // Skip model update
}
```

**Result:** File still has placeholders until 3+ points

---

### **Case 2: Model Build Fails**

```csharp
try
{
    var terms = builder.BuildModel(solvedPoints);
}
catch (Exception ex)
{
    Debug.WriteLine($"? Failed to build partial model: {ex.Message}");
    // Calibration continues (data still appended)
}
```

**Result:** Points appended, model update skipped

---

### **Case 3: File Update Fails**

```csharp
lock (_fileWriteLock)
{
    try
    {
        File.WriteAllLines(_progressiveModelPath, lines);
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"? Failed to update file: {ex.Message}");
        // Continue (data in memory)
    }
}
```

**Result:** Model in memory, file may be stale

---

## ?? Summary

| Feature | Before | After |
|---------|--------|-------|
| **Model Terms** | All zeros | Real values |
| **RMS** | 0.000 | Current value |
| **Updates** | Once (at end) | Every point |
| **Dialog Timing** | Before model build | After model build |
| **User View** | Zeros during run | Real values |
| **Crash Recovery** | No model | Partial model |

**Result:**
- ? Real-time model evolution
- ? Non-zero values always
- ? Model ready before dialog
- ? Usable partial model
- ? Negligible overhead

