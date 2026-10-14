# FITS File Organization: Session-Based Folders

## ?? New Folder Structure

### Before:
```
CalibrationData/
?? FITS/
   ?? point_001_20250115_120345.fits
   ?? point_002_20250115_120512.fits
   ?? point_001_20250115_143022.fits  ? Mixed! Confusing!
   ?? point_002_20250115_143155.fits
   ?? ...
```

### After:
```
CalibrationData/
?? FITS/
   ?? 15012025-01/  ? Session 1 on 15 Jan 2025
   ?  ?? point_001_20250115_120345.fits
   ?  ?? point_002_20250115_120512.fits
   ?  ?? ...
   ?? 15012025-02/  ? Session 2 on same day
   ?  ?? point_001_20250115_143022.fits
   ?  ?? point_002_20250115_143155.fits
   ?  ?? ...
   ?? 16012025-01/  ? Session 1 on 16 Jan 2025
      ?? ...
```

---

## ?? Benefits

### 1. **Clear Organization**
- Each calibration run ? separate folder
- Easy to identify which files belong together
- No mixing of different runs

### 2. **Auto-Incrementing Session Number**
- First run today ? `DDMMYYYY-01`
- Second run today ? `DDMMYYYY-02`
- Third run today ? `DDMMYYYY-03`
- Next day ? `DDMMYYYY-01` (resets)

### 3. **Easy Cleanup**
- Delete one session ? delete folder
- Keep best session ? delete others
- Archive old sessions ? move folders

### 4. **Better Tracking**
- See how many runs per day
- Compare results between sessions
- Track improvements over time

---

## ?? Implementation

### Code Changes:

**File:** `MountCalibrationManager.cs`

**1. Constructor - Generate Session Folder:**
```csharp
// OLD:
_fitsDirectory = Path.Combine(
    AppDomain.CurrentDomain.BaseDirectory,
    "CalibrationData",
    "FITS"
);

// NEW:
string baseFitsDir = Path.Combine(
    AppDomain.CurrentDomain.BaseDirectory,
    "CalibrationData",
    "FITS"
);

// Generate session folder: DDMMYYYY-NN
string sessionFolder = GenerateSessionFolderName(baseFitsDir);
_fitsDirectory = Path.Combine(baseFitsDir, sessionFolder);

Directory.CreateDirectory(_fitsDirectory);
Debug.WriteLine($"? Created session directory: {_fitsDirectory}");
```

**2. New Method - GenerateSessionFolderName():**
```csharp
/// <summary>
/// Generate session folder name: DDMMYYYY-NN
/// </summary>
private string GenerateSessionFolderName(string baseFitsDir)
{
    // Get current date
    DateTime now = DateTime.Now;
    string datePrefix = now.ToString("ddMMyyyy");  // "15012025"
    
    // Find existing folders for today
    var existingFolders = Directory.GetDirectories(baseFitsDir)
        .Select(Path.GetFileName)
        .Where(f => f != null && f.StartsWith(datePrefix + "-"))
        .ToList();
    
    int sessionNumber = 1;
    
    if (existingFolders.Count > 0)
    {
        // Get max session number and increment
        var sessionNumbers = existingFolders
            .Select(f => {
                var parts = f.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[1], out int num))
                    return num;
                return 0;
            })
            .Where(n => n > 0)
            .ToList();
        
        if (sessionNumbers.Count > 0)
        {
            sessionNumber = sessionNumbers.Max() + 1;
        }
    }
    
    // Format: DDMMYYYY-NN
    return $"{datePrefix}-{sessionNumber:D2}";
}
```

---

## ?? Examples

### Example 1: First Run of the Day

**Date:** 15 January 2025  
**Time:** 12:00

```
Existing folders: (none)
? Create: 15012025-01/

FITS files:
  15012025-01/point_001_20250115_120001.fits
  15012025-01/point_002_20250115_120145.fits
  ...
  15012025-01/point_040_20250115_123456.fits
```

---

### Example 2: Second Run Same Day

**Date:** 15 January 2025  
**Time:** 14:30

```
Existing folders: 15012025-01/
? Create: 15012025-02/

FITS files:
  15012025-02/point_001_20250115_143001.fits
  15012025-02/point_002_20250115_143145.fits
  ...
```

---

### Example 3: Multiple Runs

**Timeline:**
```
15/01/2025 12:00 ? 15012025-01/  (40 points)
15/01/2025 14:30 ? 15012025-02/  (40 points)
15/01/2025 18:00 ? 15012025-03/  (40 points)
16/01/2025 10:00 ? 16012025-01/  (40 points) ? New day, reset to -01
16/01/2025 15:00 ? 16012025-02/  (40 points)
```

---

## ?? Debug Output

### Startup Log:
```
[MountCalibrationManager] Session folder generated:
  Date: 15/01/2025
  Session: #2
  Folder: 15012025-02
[MountCalibrationManager] ? Created FITS session directory: 
  C:\...\CalibrationData\FITS\15012025-02
[MountCalibrationManager] Session: 15012025-02
```

### During Capture:
```
[MountCalibrationManager] Saving FITS: 
  C:\...\CalibrationData\FITS\15012025-02\point_001_20250115_143022.fits
[MountCalibrationManager] ? FITS saved successfully
```

---

## ?? Folder Naming Format

### Format: `DDMMYYYY-NN`

| Component | Description | Example |
|-----------|-------------|---------|
| `DD` | Day (01-31) | `15` |
| `MM` | Month (01-12) | `01` |
| `YYYY` | Year (4 digits) | `2025` |
| `-` | Separator | `-` |
| `NN` | Session number (01-99) | `02` |

**Full Example:** `15012025-02`
- Date: 15 January 2025
- Session: 2nd run of the day

---

## ?? Technical Details

### Auto-Increment Logic:

```
Step 1: Get current date ? "15012025"

Step 2: List existing folders:
  - 15012025-01/ ?
  - 15012025-02/ ?
  - 14012025-01/ ? (different date)
  - 16012025-01/ ? (different date)

Step 3: Extract session numbers: [1, 2]

Step 4: Get max: 2

Step 5: Increment: 2 + 1 = 3

Step 6: Create: 15012025-03/
```

---

## ??? File Management Tips

### View Sessions:
```powershell
# List all sessions
Get-ChildItem "CalibrationData\FITS" -Directory

# List sessions for specific date
Get-ChildItem "CalibrationData\FITS\15012025-*"

# Count files per session
Get-ChildItem "CalibrationData\FITS\15012025-01\*.fits" | Measure-Object
```

### Cleanup:
```powershell
# Delete specific session
Remove-Item "CalibrationData\FITS\15012025-01" -Recurse

# Delete all sessions from specific date
Remove-Item "CalibrationData\FITS\15012025-*" -Recurse

# Archive best session (copy to backup)
Copy-Item "CalibrationData\FITS\15012025-02" -Destination "Backup\" -Recurse
```

---

## ? Validation

### Test Case 1: First Run
```
Existing: (empty)
Expected: 15012025-01/
Result: ? Created 15012025-01/
```

### Test Case 2: Second Run Same Day
```
Existing: 15012025-01/
Expected: 15012025-02/
Result: ? Created 15012025-02/
```

### Test Case 3: Gap in Numbers
```
Existing: 15012025-01/, 15012025-03/  (02 deleted)
Expected: 15012025-04/  (max + 1)
Result: ? Created 15012025-04/
```

### Test Case 4: New Day
```
Existing: 15012025-01/, 15012025-02/, 16012025-01/
Current Date: 16/01/2025
Expected: 16012025-02/
Result: ? Created 16012025-02/
```

---

## ?? Error Handling

### If Folder Already Exists:
```csharp
// Directory.CreateDirectory() is idempotent
// If folder exists ? no error, just returns existing directory
Directory.CreateDirectory(_fitsDirectory);  // Safe!
```

### If Can't Create Folder:
```csharp
try
{
    Directory.CreateDirectory(_fitsDirectory);
}
catch (Exception ex)
{
    Debug.WriteLine($"? Failed to create session folder: {ex.Message}");
    // Fallback to base FITS directory
    _fitsDirectory = baseFitsDir;
}
```

---

## ?? Summary

| Aspect | Before | After |
|--------|--------|-------|
| Structure | Flat (all in FITS/) | Nested (FITS/session/) |
| Organization | Mixed files | Separated by session |
| Identification | Difficult | Easy (folder name) |
| Cleanup | Delete individual files | Delete folders |
| Tracking | Manual | Automatic numbering |

**Result:**
- ? Clear organization
- ? Easy to manage
- ? No file mixing
- ? Session tracking
- ? Better workflow

