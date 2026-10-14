# Plate Solving Integration Guide

**Project:** ForkMount-WPF  
**Feature:** Mount Calibration with Plate Solving  
**Date:** 2025-01-19  

---

## ? ??????????????????

### 1. **UI Enhancement** ?
- ? ?????????????? DataGrid:
  - `RA Err (")` - RA error (arcsec)
  - `Dec Err (")` - Dec error (arcsec)
  - `Total (")` - Total error (?????????? < 10 arcsec)
  - `Stars` - ??????????? match ???
  - `Rot (deg)` - ?????????????
  - `Status` - ????? `[SOLVING]`, `[SOLVED]`, `[SOLVE FAILED]`

### 2. **Data Model** ?
- ? `CalibrationPoint` class (Engine layer):
  - ????? properties: `RaError_Arcsec`, `DecError_Arcsec`, `TotalError_Arcsec`
  - ?????: `NumMatchedStars`, `RotationAngle_Deg`, `MeasuredPlateScale_ArcsecPerPixel`
  - ????? flags: `IsSolving`, `IsSolved`, `SolveFailed`, `IsGoodSolve`
  - ????? metadata: `ExpectedRa_J2000_Rads`, `SolvedRa_J2000_Rads`, `FitsFilePath`, `CaptureTime`

- ? `PointingModelPoint` class (UI layer):
  - ????? properties ?????? binding: `RaError_Arcsec`, `DecError_Arcsec`, `TotalError_Arcsec`
  - ?????: `NumMatchedStars`, `RotationAngle_Deg`, `IsGoodSolve`
  - PropertyChanged notifications

### 3. **Infrastructure** ?
????? classes ????:

#### A. **FITSWriter.cs** ?
```csharp
// Location: Engine/PlateSolving/FITSWriter.cs
public static bool WriteFITS(CameraImageData imageData, string filePath, FITSMetadata metadata);
```
**Features:**
- ?????? RAW8 ??? RAW16
- FITS header ??????? (2880-byte blocks)
- Metadata: Alt/Az, RA/Dec, Exposure, Gain, Observer location, etc.
- Big-endian conversion ?????? 16-bit data

#### B. **PlateSolveResult.cs** ?
```csharp
// Location: Engine/PlateSolving/PlateSolveResult.cs
public class PlateSolveResult
{
    public bool Success { get; set; }
    public double CenterRa_J2000_Rads { get; set; }
    public double CenterDec_J2000_Rads { get; set; }
    public double RotationAngle_Deg { get; set; }
    public double PlateScale_ArcsecPerPixel { get; set; }
    public int NumExtractedStars { get; set; }
    public int NumMatchedStars { get; set; }
    public string ErrorMessage { get; set; }
}
```

#### C. **PlateSolverManager.cs** ?
```csharp
// Location: Engine/PlateSolving/PlateSolverManager.cs
public class PlateSolverManager
{
    public bool Initialize(string catalogPath);
    public async Task<PlateSolveResult> SolveImageAsync(...);
    public static (double, double, double) CalculatePointingError(...);
}
```
**Features:**
- Async plate solving
- PWLib wrapper (????? simulation mode)
- Error calculation (RA/Dec/Total)

---

## ?? ???????????? (Implementation)

### Phase 1: Integrate FITS Export (????)
**File:** `MountCalibrationManager.cs`

```csharp
// ???? capture image ??????
private async Task CaptureAndSaveFITS(CalibrationPoint point, double raDeg, double decDeg)
{
    // 1. Capture image
    var imageData = await CaptureImageAsync(point, raDeg, decDeg);
    
    // 2. Prepare FITS metadata
    var metadata = new FITSMetadata
    {
        Azimuth_Deg = point.TargetAzimuth,
        Altitude_Deg = point.TargetAltitude,
        ExpectedRA_Deg = raDeg,
        ExpectedDec_Deg = decDeg,
        ObserverLatitude_Deg = _observerLocation.Latitude,
        ObserverLongitude_Deg = _observerLocation.Longitude,
        ObserverAltitude_m = _observerLocation.Altitude_m,
        FocalLength_mm = GetFocalLength(),
        PlateScale_ArcsecPerPixel = GetPlateScale(),
        PointIndex = point.Index
    };
    
    // 3. Save as FITS
    string fitsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "CalibrationData",
        $"point_{point.Index:D3}_{DateTime.Now:yyyyMMdd_HHmmss}.fits"
    );
    
    bool success = FITSWriter.WriteFITS(
        imageData.RawImageData,  // Full resolution RAW data
        fitsPath,
        metadata
    );
    
    if (success)
    {
        point.FitsFilePath = fitsPath;
        Debug.WriteLine($"[Calibration] ? FITS saved: {fitsPath}");
    }
}
```

### Phase 2: Integrate Plate Solver (???????)

#### Option A: ??? PWLib (?????)
```csharp
private PlateSolverManager _plateSolver;

// ?? Initialize
public void InitializePlateSolver()
{
    _plateSolver = new PlateSolverManager();
    
    var settings = AppSettings.GetPlateSolverSettings();
    bool success = _plateSolver.Initialize(settings.Tycho2CatalogPath);
    
    if (!success)
    {
        Debug.WriteLine("[Calibration] ?? Plate solver initialization failed");
    }
}

// ???? save FITS
private async Task SolvePointAsync(CalibrationPoint point)
{
    if (point.FitsFilePath == null || _plateSolver == null)
        return;
    
    // Update status
    point.IsSolving = true;
    point.Status = "[SOLVING]";
    
    try
    {
        // Get plate scale
        var cameraSettings = AppSettings.GetCameraSettings();
        double plateScale = cameraSettings.PlateScale_ArcsecPerPixel;
        
        // Solve
        var result = await _plateSolver.SolveImageAsync(
            point.FitsFilePath,
            point.ExpectedRa_J2000_Rads.Value,
            point.ExpectedDec_J2000_Rads.Value,
            plateScale,
            searchRadius_Deg: 5.0
        );
        
        if (result.Success)
        {
            // Store results
            point.SolvedRa_J2000_Rads = result.CenterRa_J2000_Rads;
            point.SolvedDec_J2000_Rads = result.CenterDec_J2000_Rads;
            point.NumMatchedStars = result.NumMatchedStars;
            point.NumExtractedStars = result.NumExtractedStars;
            point.RotationAngle_Deg = result.RotationAngle_Deg;
            point.MeasuredPlateScale_ArcsecPerPixel = result.PlateScale_ArcsecPerPixel;
            
            // Calculate errors
            var (raErr, decErr, totalErr) = PlateSolverManager.CalculatePointingError(
                point.ExpectedRa_J2000_Rads.Value,
                point.ExpectedDec_J2000_Rads.Value,
                result.CenterRa_J2000_Rads,
                result.CenterDec_J2000_Rads
            );
            
            point.RaError_Arcsec = raErr;
            point.DecError_Arcsec = decErr;
            point.IsSolved = true;
            point.Status = "[SOLVED]";
            
            Debug.WriteLine($"[Calibration] ? Point {point.Index} solved:");
            Debug.WriteLine($"  RA Error:  {raErr:F2} arcsec");
            Debug.WriteLine($"  Dec Error: {decErr:F2} arcsec");
            Debug.WriteLine($"  Total:     {totalErr:F2} arcsec");
            Debug.WriteLine($"  Stars:     {result.NumMatchedStars}/{result.NumExtractedStars}");
        }
        else
        {
            point.SolveFailed = true;
            point.Status = "[SOLVE FAILED]";
            Debug.WriteLine($"[Calibration] ? Point {point.Index} solve failed: {result.ErrorMessage}");
        }
    }
    catch (Exception ex)
    {
        point.SolveFailed = true;
        point.Status = "[SOLVE FAILED]";
        Debug.WriteLine($"[Calibration] ? Solve exception: {ex.Message}");
    }
    finally
    {
        point.IsSolving = false;
    }
}
```

#### Option B: ??? Astrometry.net CLI (???)
```csharp
private async Task<PlateSolveResult> SolveWithAstrometryNet(string fitsPath, ...)
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "solve-field",
            Arguments = $"--ra {raDeg} --dec {decDeg} --radius {radiusDeg} " +
                       $"--scale-low {plateScale * 0.9} --scale-high {plateScale * 1.1} " +
                       $"--scale-units arcsecperpix --no-plots --overwrite {fitsPath}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        }
    };
    
    process.Start();
    await process.WaitForExitAsync();
    
    // Parse WCS file for results
    return ParseWCSFile(fitsPath + ".wcs");
}
```

### Phase 3: UI Updates (????)

```csharp
// ?? UIUpdateTimer_Tick
private void SyncPointsWithManager()
{
    var managerPoints = _calibrationManager.GetCalibrationPoints();
    
    foreach (var managerPoint in managerPoints)
    {
        var uiPoint = _pointingModelData.FirstOrDefault(p => p.Index == managerPoint.Index);
        if (uiPoint != null)
        {
            // Sync plate solve results
            uiPoint.RaError_Arcsec = managerPoint.RaError_Arcsec;
            uiPoint.DecError_Arcsec = managerPoint.DecError_Arcsec;
            uiPoint.NumMatchedStars = managerPoint.NumMatchedStars;
            uiPoint.RotationAngle_Deg = managerPoint.RotationAngle_Deg;
            
            // Update status
            if (managerPoint.IsSolving)
                uiPoint.Status = "[SOLVING]";
            else if (managerPoint.IsSolved)
                uiPoint.Status = "[SOLVED]";
            else if (managerPoint.SolveFailed)
                uiPoint.Status = "[SOLVE FAILED]";
        }
    }
}
```

---

## ?? Dependencies

### ???????????:
1. **PWLib.dll** (PlaneWave Instruments)
   - ???????: https://planewave.com
   - ??????? simulation mode ????

2. **Star Catalog**
   - Tycho-2 catalog (~2.5 GB)
   - Download: https://vizier.u-strasbg.fr/viz-bin/VizieR?-source=I/259

3. **NuGet Packages** (??????)
   - `nom.tam.fits` - FITS I/O library

---

## ?? Testing Plan

### Test 1: FITS Export
1. ????? calibration grid
2. Start calibration
3. ???????????? FITS files ?? `CalibrationData/` folder
4. ??? DS9 ???? MaxIm DL ???? FITS file ?? header

### Test 2: Simulated Plate Solve
1. Run calibration ???? simulation mode
2. ??????? DataGrid ???? RA/Dec errors
3. ??????????????????????? `[SOLVED]`
4. ??????? Total Error < 10 arcsec (???????)

### Test 3: Real Plate Solve (??????????? PWLib)
1. ???? comment ?? `PlateSolverManager.cs`
2. ??????? catalog path
3. Run calibration
4. ??????????????

---

## ?? Performance

### ????????????? (??????):
- Slew: 10-30 sec
- Settle: 1-5 sec
- Exposure: 1-10 sec (?????????????)
- FITS Export: 0.1-0.5 sec
- Plate Solve: 5-30 sec (parallel!)
- **Total: ~20-80 sec/point**

### Grid ???? 40 ??? (8x5):
- Sequential: ~50-80 ????
- **Parallel: ~20-40 ????** ? (solve ???????????????!)

---

## ?? Troubleshooting

### ? FITS Export Failed
- ??????? `CameraImageData.ImageData` ??????? null
- ??????? permission ??? `CalibrationData/` folder

### ? Plate Solve Failed
- **Too few stars:** ????? exposure time
- **Wrong plate scale:** ??????? PlateScale setting
- **Initial guess too far:** ??????? mount alignment
- **Catalog not found:** ??????? Tycho2CatalogPath

### ?? Performance Issues
- ?? grid density (4x3 ??? 8x5)
- ????? binning (2x ? 4x)
- ??? local SSD ?????? FITS files

---

## ?? Next Features

### Future Enhancements:
1. ? Auto plate scale refinement
2. ? RMS error visualization (graph)
3. ? Export pointing model to file
4. ? Load/apply pointing corrections
5. ? Multi-star pointing model fitting

---

## ?? ??????????????

- [POINTING_MODEL_WITH_PLATESOLVE.md](./POINTING_MODEL_WITH_PLATESOLVE.md) - ??????????
- FITS Format: https://fits.gsfc.nasa.gov/
- PWLib Documentation: (from PlaneWave)
- Astrometry.net: https://astrometry.net/

---

**Status:** ? Infrastructure Ready - ????? implement!  
**Next Step:** Phase 1 - FITS Export  
