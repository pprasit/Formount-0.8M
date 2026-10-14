# Pointing Model with Plate Solving - Technical Documentation

**Project:** ForkMount-WPF  
**Component:** NaritMountLib - BuildPointingModel  
**Last Updated:** 2025-01-XX  

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Key Components](#key-components)
4. [Workflow: AutoModelScriptParallel](#workflow-automodelscriptparallel)
5. [Workflow: PlateSolvePoints](#workflow-platesolvepoints)
6. [State Machine](#state-machine)
7. [Data Flow](#data-flow)
8. [Implementation Guide](#implementation-guide)
9. [Troubleshooting](#troubleshooting)

---

## Overview

### What is Pointing Model?

Pointing Model ??????????????????????????????????????? Mount ???:
1. ???? Mount ?????????????????????? (Alt/Az grid)
2. ?????????????????????
3. ??? Plate Solving ??????????????????????????????
4. ????? error ????????????????????? vs ???????????
5. ??????????????????????????

### Why Important?

- ?? pointing error ??? ~10 arcmin ????? ~10 arcsec
- ???????????? automated observation
- ????????????????????? Mount

---

## Architecture

### System Components

```
[BuildPointingModel] - Main controller class
    |
    +-- [AutoModelScriptParallel] - Async script: Slew + Image capture
    +-- [PlateSolvePoints] - Async script: Plate solving (parallel)
    |
    +-- [MaxIm.Camera] - External: Camera control via MaxIm DL
    +-- [PWLib.BackgroundPlateSolver] - External: Plate solving engine
    +-- [App.MountManager] - Mount control (GotoRaDec2000, etc.)
```

### Threading Model

```
[Timer Thread - 100ms interval]
    |
    +-- Step() AutoModelScriptParallel
    +-- Step() PlateSolvePoints
    
    [PARALLEL EXECUTION]
    
Thread 1: Mount Slew + Image Capture
    Slew -> Settle -> Expose -> Download -> [Queue]
    
Thread 2: Plate Solving
    [Queue] -> Load -> Extract Stars -> Match -> [Results]
    
    [SYNC via Queues]
    _samplesToSolve Queue: Thread 1 -> Thread 2
    _solvedSamples Queue: Thread 2 -> Thread 1
```

---

## Key Components

### 1. BuildPointingModel Class

**Location:** `NaritMountLib\NaritMountLib\PointingModel\BuildPointingModel.cs`

**Key Fields:**
```csharp
private BindingList<MapPoint> _autoMountPoints;     // Grid points to observe
private Queue<PointingModelSample> _samplesToSolve; // Images waiting for solve
private Queue<PointingModelSample> _solvedSamples;  // Solved samples to process
private bool _isSolvingSample;                      // Solve in progress flag
private YieldScript _takeImagesScript;              // Async script 1
private YieldScript _solveImagesScript;             // Async script 2

private double ExpLengthSec = 3.0;                  // Exposure time
private double ArcsecPerPixUnbinned = 0.60;         // Pixel scale
private short Binning = 2;                          // Camera binning
```

**Key Methods:**
- `AddPoints()` - Generate Alt/Az grid
- `StartAutoModel()` - Start the process
- `StopAutoModel()` - Cancel operation
- `Resume()` - Resume from failure
- `AutoModelScriptParallel()` - Main imaging loop
- `PlateSolvePoints()` - Plate solving loop

### 2. MapPoint Class

**Represents:** Single observation point in Alt/Az grid

**Properties:**
```csharp
int Index;                      // Point number
Angle Alt, Az;                  // Target coordinates
MapPointState State;            // Current state (see State Machine)
double RaErrorArcsec;           // Solved RA error
double DecErrorArcsec;          // Solved Dec error
double PositionAngleDegs;       // Image rotation
int NumExtractedStars;          // Stars detected
```

### 3. PointingModelSample Class

**Represents:** Single observation sample with data

**Properties:**
```csharp
DateTime LocalTime;             // Timestamp
double LstRads;                 // Local Sidereal Time
double JulianDay;               // Julian Date
double PhiEncRads;              // Azimuth encoder reading
double ThetaEncRads;            // Altitude encoder reading
double EstimatedRaAppRads;      // Expected RA (apparent)
double EstimatedDecAppRads;     // Expected Dec (apparent)
double SolvedRa2000Rads;        // Plate solved RA (J2000)
double SolvedDec2000Rads;       // Plate solved Dec (J2000)
string Filename;                // FITS file path
int PointNumber;                // Point index
MapPoint MapPoint;              // Reference to grid point
object Plate;                   // Plate solution object
```

### 4. External Dependencies

#### MaxIm DL Camera Interface
```csharp
App.Camera.Expose(duration, count);
App.Camera.ReadyForDownload;
App.Camera.StartDownload();
App.Camera.ImageReady;
App.Camera.SaveImage(filename);
App.Camera.SetFITSKey(key, value);
```

#### PWLib Plate Solver
```csharp
BackgroundPlateSolver plateSolver = new BackgroundPlateSolver(filename);
plateSolver.CenterRa2000GuessRads = ...;
plateSolver.CenterDec2000GuessRads = ...;
plateSolver.ArcsecPerPixelGuess = ...;
plateSolver.UseStartingCoordinates = true;
plateSolver.BeginSolve();

// Properties:
plateSolver.IsLoaded;
plateSolver.IsExtracted;
plateSolver.IsFinished;
plateSolver.FoundMatch;
plateSolver.CenterRaRad2000;
plateSolver.CenterDecRad2000;
plateSolver.RotationAngleDegs;
plateSolver.NumExtractedStars;
plateSolver.NumMatchedStars;
```

---

## Workflow: AutoModelScriptParallel

### Overview
Main loop that controls Mount slewing and image capture. Runs as async coroutine stepped every 100ms.

### Detailed Flow

```
[STEP 1] INITIALIZATION
    |
    +-- Print "Starting auto model"
    +-- Connect to Camera (MaxIm DL)
    +-- Set Camera.AutoDownload = false
    +-- Get site coordinates (Lat/Lon)
    +-- Register cancel handler (StopMount, AbortExposure)
    +-- Initialize variables:
        - currentSample = null
        - waitForImage = false
        - _isSolvingSample = false
    +-- Clear queues
    +-- Find first Pending point
    |
    V
[STEP 2] MAIN LOOP START
    |
    V
[STEP 3] PROCESS SOLVED SAMPLES
    |
    WHILE (_solvedSamples.Count > 0)
        +-- Dequeue sample
        +-- Call AddPointingSampleToModel(sample)
        +-- sample.MapPoint.State = AddedToModel
    |
    V
[STEP 4] PREPARE NEXT POINT (if available)
    |
    IF (pointNum < _autoMountPoints.Count)
        +-- point = _autoMountPoints[pointNum]
        +-- pointNum++
        +-- Convert AltAz -> RaDec (apparent)
        +-- Print "Goto Azm XX, Alt XX (RA XX Dec XX)"
        +-- point.State = Slewing
        +-- App.MountManager.GotoRaDec2000(raDecApp)
        |
        V
    [STEP 5] WAIT FOR SLEW
        |
        +-- Delay 1000 ms
        +-- Print "Waiting on slew"
        +-- WHILE (MountInfo.isSlewing)
        |       Delay 500 ms
        |
        +-- Calculate distance from target:
        |   - axis0DistArcsec (Azimuth)
        |   - axis1DistArcsec (Altitude)
        +-- Print "Slew finished"
        +-- Print distance from target
        |
        V
    [STEP 6] SETTLING
        |
        +-- settleTimeSec = 1
        +-- Print "Settling for 1.0 seconds..."
        +-- point.State = Settling
        +-- Delay 1000 ms
        +-- WHILE (axis0 >= 3.0 arcsec OR axis1 >= 3.0 arcsec)
        |       Print "Waiting for axes to settle"
        |       Delay 1000 ms
        |
        V
[STEP 7] RETRIEVE PREVIOUS IMAGE (if waiting)
    |
    IF (waitForImage == true)
        +-- Print "Retrieving previous image..."
        +-- WHILE (!Camera.ImageReady)
        |       Delay 500 ms
        +-- Print "Saving image..."
        +-- Camera.SaveImage(currentSample.Filename)
        +-- Enqueue currentSample -> _samplesToSolve
        +-- waitForImage = false
    |
    V
[STEP 8] START NEW EXPOSURE (if point exists)
    |
    IF (point != null)
        +-- point.State = Exposing
        +-- BeginTakingImage()
        |   - Set binning
        |   - Camera.Expose(ExpLengthSec, 1)
        +-- Start stopwatch
        +-- Delay (ExpLengthSec / 2) seconds
        |
        V
    [STEP 9] RECORD DATA AT MIDPOINT
        |
        +-- Print "Recording data at midpoint of exposure"
        +-- Create new PointingModelSample:
            - LocalTime = DateTime.Now
            - LstRads = MountInfo.Lmst (in radians)
            - JulianDay = MountInfo.JulianDate
            - PhiEncRads = MountInfo.AzimuthDegs (encoder reading)
            - ThetaEncRads = MountInfo.AltitudeDegs (encoder reading)
            - EstimatedRaAppRads = raDecApp.Ra
            - EstimatedDecAppRads = raDecApp.Dec
            - Filename = "platesolvetmp####.fits"
            - PointNumber = pointNum
            - MapPoint = point
        |
        V
    [STEP 10] WAIT FOR DOWNLOAD READY
        |
        +-- WHILE (!Camera.ReadyForDownload)
        |       Delay 500 ms
        +-- Print "Image ready for download XX sec after exposure started"
        +-- point.State = Downloading
        +-- Camera.StartDownload()
        +-- waitForImage = true
        |
        V
[STEP 11] CHECK END CONDITION
    |
    IF (point == null)  // No more points
        IF (waitForImage OR _samplesToSolve.Count > 0 OR 
            _solvedSamples.Count > 0 OR _isSolvingSample)
            +-- Still processing, delay 500 ms
            +-- Continue loop
        ELSE
            +-- All done, EXIT LOOP
    ELSE
        +-- Continue to next point
    |
    V
[GOTO STEP 3] - Loop until complete
```

### Key Concepts

#### 1. Parallel Processing
```
Time:  0s      3s      6s      9s
Point 1: [Slew][Expose][Download]
Point 2:              [Slew][Expose][Download]
Point 3:                        [Slew][Expose][Download]
Solve:         [---Solve Point 1---]
               [---Solve Point 2---]
                         [---Solve Point 3---]
```

Exposure ??? Point N+1 ???????????????????????????? Point N

#### 2. Midpoint Recording
```
[Exposure Start]----[MIDPOINT]----[Exposure End]
                        ^
                        |
                Record encoder position HERE
                (minimizes tracking error)
```

#### 3. Settling Criteria
```
Mount must be within 3 arcsec of target in BOTH axes
- Axis0 (Azimuth) < 3.0 arcsec
- Axis1 (Altitude) < 3.0 arcsec
```

---

## Workflow: PlateSolvePoints

### Overview
Parallel async loop that processes images from queue. Runs independently of main imaging loop.

### Detailed Flow

```
[STEP 1] INFINITE LOOP
    |
    V
[STEP 2] CHECK QUEUE
    |
    IF (_samplesToSolve.Count > 0)
        |
        V
    [STEP 3] DEQUEUE SAMPLE
        |
        +-- Print "X point(s) to platesolve"
        +-- _isSolvingSample = true
        +-- sample = _samplesToSolve.Dequeue()
        +-- Calculate arcsecPerPixel = ArcsecPerPixUnbinned * Binning
        |
        V
    [STEP 4] SETUP PLATE SOLVER
        |
        +-- Create BackgroundPlateSolver(sample.Filename)
        +-- Convert EstimatedRaAppRads/DecAppRads -> J2000
        +-- Set solver parameters:
            - CenterRa2000GuessRads
            - CenterDec2000GuessRads
            - UseStartingCoordinates = true
            - ArcsecPerPixelGuess
        +-- plateSolver.BeginSolve()
        |
        V
    [STEP 5] LOADING PHASE
        |
        +-- sample.MapPoint.State = Loading
        +-- WHILE (!plateSolver.IsFinished AND !plateSolver.IsLoaded)
                Delay 100 ms
        |
        V
    [STEP 6] EXTRACTING PHASE
        |
        +-- sample.MapPoint.State = Extracting
        +-- WHILE (!plateSolver.IsFinished AND !plateSolver.IsExtracted)
                Delay 100 ms
        +-- sample.MapPoint.NumExtractedStars = plateSolver.NumExtractedStars
        |
        V
    [STEP 7] MATCHING PHASE
        |
        +-- sample.MapPoint.State = Matching
        +-- WHILE (!plateSolver.IsFinished)
                Delay 100 ms
        +-- Print "Platesolve finished"
        |
        V
    [STEP 8] PROCESS RESULTS
        |
        IF (plateSolver.FoundMatch)
            |
            +-- Print "Found match!"
            +-- sample.MapPoint.State = Solved
            +-- Extract results:
                - centerRa2000 = plateSolver.CenterRaRad2000
                - centerDec2000 = plateSolver.CenterDecRad2000
            +-- Print RA/Dec J2000, Rotation, Plate scale, Matched stars
            +-- sample.SolvedRa2000Rads = plateSolver.CenterRaRad2000
            +-- sample.SolvedDec2000Rads = plateSolver.CenterDecRad2000
            +-- Convert J2000 -> Apparent (now)
            +-- sample.MapPoint.PositionAngleDegs = plateSolver.RotationAngleDegs
            |
            V
        [STEP 9] CALCULATE ERRORS
            |
            +-- RaError = (SolvedRaApp - EstimatedRaApp)
            |   - Wrap to [-180, 180] degrees
            |   - Convert to arcseconds
            |   - sample.MapPoint.RaErrorArcsec = totalArcsec
            |
            +-- DecError = (SolvedDecApp - EstimatedDecApp)
            |   - Wrap to [-180, 180] degrees
            |   - Convert to arcseconds
            |   - sample.MapPoint.DecErrorArcsec = totalArcsec
            |
            +-- sample.Plate = plateSolver.Plate
            +-- Enqueue sample -> _solvedSamples
            +-- UpdatePixelScaleIfGoodSolution(plateSolver)
            |
        ELSE
            +-- Print "Solve failed"
            +-- sample.MapPoint.State = SolveFailed
        |
        V
    [STEP 10] CLEANUP
        |
        +-- Delete FITS file
        +-- _isSolvingSample = false
        +-- sample = null
        |
    ELSE (queue empty)
        +-- yield return 0
    |
    V
[GOTO STEP 1] - Infinite loop
```

### Plate Solving Phases

```
[LOADING]
    - Read FITS file
    - Parse header
    - Load image data
    Duration: ~100-500ms

[EXTRACTING]
    - Detect stars in image
    - Calculate centroids
    - Measure star brightness
    Output: List of (x, y, magnitude)
    Duration: ~500-2000ms

[MATCHING]
    - Match detected stars to catalog
    - Calculate plate constants
    - Refine solution
    - Compute WCS (World Coordinate System)
    Output: RA/Dec center, rotation, scale
    Duration: ~1000-10000ms (depends on field)
```

### Pixel Scale Auto-Update

```csharp
UpdatePixelScaleIfGoodSolution(plateSolver):
    
    IF (NumMatchedStars > 20) OR 
       (NumMatchedStars > 8 AND NumMatchedStars > NumExtractedStars/2)
        
        newPixelScale = plateSolver.MatchedArcsecPerPixel / Binning
        oldPixelScale = ArcsecPerPixUnbinned
        
        IF (abs((oldPixelScale - newPixelScale) / oldPixelScale) > 0.1)
            // More than 10% difference
            Print "Updating pixel scale from X to Y"
            ArcsecPerPixUnbinned = newPixelScale
```

**Purpose:** Auto-correct pixel scale if user's initial guess is wrong

**Criteria:**
- High confidence: >20 matched stars
- OR moderate confidence: >8 stars AND >50% match rate
- AND difference > 10%

---

## State Machine

### MapPointState Enumeration

```
Pending      -> Initial state, waiting to be processed
    |
    V
Slewing      -> Mount is moving to target
    |
    V
Settling     -> Waiting for mount to stabilize
    |
    V
Exposing     -> Camera is taking exposure
    |
    V
Downloading  -> Image download in progress
    |
    V
Loading      -> Plate solver loading FITS file
    |
    V
Extracting   -> Detecting stars in image
    |
    V
Matching     -> Matching stars to catalog
    |
    V
Solved       -> Plate solve successful
    |
    V
AddedToModel -> Sample added to pointing model
    
    OR
    |
    V
SolveFailed  -> Plate solve failed
    
    OR
    |
    V
BadPoint     -> Manually marked as bad
```

### State Transitions

```
[User Action: StartAutoModel()]
    -> All points: State = Pending

[AutoModelScriptParallel - Point Selection]
    -> point.State = Slewing

[After GotoRaDec2000 completes]
    -> point.State = Settling

[After settling check passes]
    -> point.State = Exposing

[After Camera.StartDownload()]
    -> point.State = Downloading

[PlateSolvePoints - BeginSolve()]
    -> sample.MapPoint.State = Loading

[After IsLoaded = true]
    -> sample.MapPoint.State = Extracting

[After IsExtracted = true]
    -> sample.MapPoint.State = Matching

[After IsFinished = true]
    IF (FoundMatch)
        -> sample.MapPoint.State = Solved
    ELSE
        -> sample.MapPoint.State = SolveFailed

[AutoModelScriptParallel - ProcessSolvedSamples]
    -> sample.MapPoint.State = AddedToModel
```

---

## Data Flow

### Queue Architecture

```
[AutoModelScriptParallel]
         |
         | (Save image)
         V
[_samplesToSolve] Queue<PointingModelSample>
         |
         | (Dequeue)
         V
[PlateSolvePoints]
         |
         | (After solving)
         V
[_solvedSamples] Queue<PointingModelSample>
         |
         | (Dequeue)
         V
[AutoModelScriptParallel - AddPointingSampleToModel()]
```

### Sample Data Structure

```csharp
PointingModelSample {
    // Time Data
    DateTime LocalTime;
    double LstRads;             // Local Sidereal Time
    double JulianDay;
    
    // Mount Encoder Readings (at midpoint)
    double PhiEncRads;          // Azimuth encoder
    double ThetaEncRads;        // Altitude encoder
    
    // Expected Position (from Mount commands)
    double EstimatedRaAppRads;
    double EstimatedDecAppRads;
    
    // Solved Position (from Plate Solving)
    double SolvedRa2000Rads;
    double SolvedDec2000Rads;
    
    // Image Data
    string Filename;            // "platesolvetmp####.fits"
    object Plate;               // Plate solution object
    
    // Metadata
    int PointNumber;
    MapPoint MapPoint;          // Reference to grid point
}
```

### Error Calculation

```
[Plate Solve Result]
    SolvedRa2000Rads, SolvedDec2000Rads
    |
    V
[Convert J2000 -> Apparent]
    RaDec.J2000ToNow()
    |
    V
[Calculate Error]
    RaError = SolvedRaApp - EstimatedRaApp
    DecError = SolvedDecApp - EstimatedDecApp
    |
    V
[Wrap to [-180, 180] degrees]
    angle.RerangeDegs(-180.0, 180.0)
    |
    V
[Convert to arcseconds]
    RaErrorArcsec, DecErrorArcsec
    |
    V
[Store in MapPoint]
    mapPoint.RaErrorArcsec
    mapPoint.DecErrorArcsec
```

---

## Implementation Guide

### Step 1: Create Grid Points

```csharp
BuildPointingModel model = new BuildPointingModel();

// Define grid parameters
double azmSpacing = 90.0;   // degrees
double azmMin = 0.0;
double azmMax = 360.0;
double altSpacing = 45.0;   // degrees
double altMin = 10.0;       // minimum elevation
double altMax = 85.0;       // maximum elevation

// Generate grid
List<AltAz> points = model.AddPoints(
    azmSpacing, azmMin, azmMax,
    altSpacing, altMin, altMax
);

// Result: Grid with 16 points (4 azimuth x 4 altitude)
// Azimuth: 0, 90, 180, 270
// Altitude: 10, 55, 100 (limited to 85)
```

### Step 2: Configure Camera Settings

```csharp
// Set exposure time (seconds)
model.SetExposureTime(3.0);

// Set pixel scale (arcsec per pixel, unbinned)
model.SetPixelScale(0.60);

// Set binning
model.SetBinning(2);

// Effective pixel scale = 0.60 * 2 = 1.2 arcsec/pixel
```

### Step 3: Start Auto Model

```csharp
// Method 1: Start from scratch
model.StartAutoModel();

// Method 2: Resume after failure
model.Resume();

// Method 3: Single point at current position
model.TakeSingleImage();
```

### Step 4: Monitor Progress

```csharp
// Get all points
BindingList<MapPoint> points = model._autoMountPoints;

foreach (MapPoint point in points)
{
    Console.WriteLine($"Point {point.Index}: {point.State}");
    Console.WriteLine($"  Alt: {point.Alt.Degs:F2}, Az: {point.Az.Degs:F2}");
    
    if (point.State == MapPointState.Solved || 
        point.State == MapPointState.AddedToModel)
    {
        Console.WriteLine($"  RA Error: {point.RaErrorArcsec:F2} arcsec");
        Console.WriteLine($"  Dec Error: {point.DecErrorArcsec:F2} arcsec");
        Console.WriteLine($"  Stars: {point.NumExtractedStars}");
        Console.WriteLine($"  Rotation: {point.PositionAngleDegs:F2} deg");
    }
}
```

### Step 5: Handle Completion

```csharp
// Check if all points are processed
bool allDone = points.All(p => 
    p.State == MapPointState.AddedToModel ||
    p.State == MapPointState.SolveFailed ||
    p.State == MapPointState.BadPoint
);

if (allDone)
{
    // Extract samples for model fitting
    List<PointingModelSample> samples = new List<PointingModelSample>();
    
    foreach (MapPoint point in points)
    {
        if (point.State == MapPointState.AddedToModel)
        {
            // Get sample data
            // (Note: Actual implementation needs sample storage)
        }
    }
    
    // Fit pointing model (not shown in this code)
    // FitPointingModel(samples);
}
```

### Step 6: Stop/Cancel

```csharp
// Stop the process
model.StopAutoModel();

// Camera will be stopped
// Mount will be stopped
// Scripts will be cancelled
```

---

## Integration Requirements

### Required Components to Implement

#### 1. Mount Manager Interface
```csharp
interface IMountManager
{
    void GotoRaDec2000(RaDec target);
    void StopMount();
}
```

Must provide:
- `MountInfo.isSlewing` - bool
- `MountInfo.AzimuthDegs` - double
- `MountInfo.AltitudeDegs` - double
- `MountInfo.AzmTargetDistanceArcsec` - double
- `MountInfo.AltTargetDistanceArcsec` - double
- `MountInfo.Lmst` - string (HH:MM:SS)
- `MountInfo.JulianDate` - double
- `MountInfo.AirMass` - double
- `MountInfo.TargetRaAppHms` - string
- `MountInfo.TargetDecAppDms` - string

#### 2. Camera Interface (MaxIm DL)
```csharp
interface ICamera
{
    bool AutoDownload { get; set; }
    short BinX { get; set; }
    short BinY { get; set; }
    short MaxBinX { get; }
    bool ReadyForDownload { get; }
    bool ImageReady { get; }
    bool SequenceRunning { get; }
    
    void AbortExposure();
    void SetFullFrame();
    void Expose(double duration, int count);
    void StartDownload();
    void SaveImage(string filename);
    void SetFITSKey(string key, object value);
}
```

#### 3. Configuration
```csharp
interface IMountSetting
{
    double Latitude { get; }
    double Longitude { get; }
    string PointingModelPath { get; }
}
```

#### 4. Plate Solver (PWLib)
```
PWLib.dll must be available
- BackgroundPlateSolver class
- RaDec, AltAz, Angle classes
- Coordinate conversion functions
```

---

## Troubleshooting

### Common Issues

#### [ERROR] Camera Connection Failed
```
Symptom: "Camera error: [exception message]"
Solution:
1. Check MaxIm DL is running
2. Verify camera is connected in MaxIm
3. Check COM/USB connection
4. Restart MaxIm DL
5. Call model.ResetCamera() and retry
```

#### [ERROR] Plate Solve Failed
```
Symptom: MapPoint.State = SolveFailed
Possible Causes:
1. Too few stars detected
   - Check focus
   - Increase exposure time
   - Reduce binning
   
2. Wrong pixel scale
   - Verify ArcsecPerPixUnbinned setting
   - System will auto-correct after good solutions
   
3. Clouds/obstruction
   - Skip point and continue
   
4. Initial guess too far off
   - Check Mount alignment
   - Verify Lat/Lon settings
```

#### [ERROR] Mount Not Settling
```
Symptom: "Waiting for axes to settle" loop
Possible Causes:
1. Wind/vibration
2. Tracking error
3. Mount mechanical issues
4. Threshold too tight (3 arcsec)

Solution:
- Modify settling threshold in code
- Check mount tracking
- Wait for better conditions
```

#### [WARNING] Slow Performance
```
Symptom: Process taking too long
Check:
1. Plate solve time
   - PWLib solver configuration
   - Star catalog availability
   
2. Network/disk I/O
   - FITS file save location
   - Network drive issues
   
3. Camera download speed
   - USB bandwidth
   - Binning settings
```

### Performance Optimization

```
Typical Timing per Point:
- Slew: 10-30 seconds
- Settle: 1-5 seconds
- Exposure: 1-10 seconds (configurable)
- Download: 1-3 seconds
- Plate Solve: 5-30 seconds (parallel)

Total: ~20-80 seconds per point
16-point grid: ~5-20 minutes
```

**Optimization Tips:**
1. Use higher binning (2x or 4x) for faster downloads
2. Shorter exposure if field has many bright stars
3. Pre-warm MaxIm DL and plate solver
4. Use fast local SSD for FITS file storage
5. Reduce grid density for quick calibration

---

## Advanced Topics

### Custom Grid Generation

```csharp
// Custom points (not grid)
List<AltAz> customPoints = new List<AltAz>();
customPoints.Add(AltAz.FromDegs(45.0, 0.0));   // Alt 45, Az 0
customPoints.Add(AltAz.FromDegs(60.0, 90.0));  // Alt 60, Az 90
customPoints.Add(AltAz.FromDegs(30.0, 180.0)); // Alt 30, Az 180

// Add to model
model.ClearPoints();
foreach (var point in customPoints)
{
    model.AddPoint(point);  // Private method - needs to be public
}
```

### FITS Header Data

Automatically embedded in each FITS file:
```
AIRMASS  = MountInfo.AirMass
ALT      = MountInfo.AltitudeDegs
AZI      = MountInfo.AzimuthDegs
FOCUSPOS = Focuser position
JD       = Julian Date
LAT      = Site latitude
LONG     = Site longitude
LST      = Local Sidereal Time
OBJCTRA  = Target RA (apparent, HMS)
OBJCTDEC = Target Dec (apparent, DMS)
ROTATOR  = Rotator angle
FOCALLEN = Focal length
APTDIA   = Aperture diameter
TELESCOP = Telescope name
```

### Error Analysis

After completion, analyze errors:
```csharp
double rmsRa = Math.Sqrt(points
    .Where(p => p.State == MapPointState.AddedToModel)
    .Average(p => p.RaErrorArcsec * p.RaErrorArcsec));

double rmsDec = Math.Sqrt(points
    .Where(p => p.State == MapPointState.AddedToModel)
    .Average(p => p.DecErrorArcsec * p.DecErrorArcsec));

Console.WriteLine($"RMS RA Error: {rmsRa:F2} arcsec");
Console.WriteLine($"RMS Dec Error: {rmsDec:F2} arcsec");
```

Expected values:
- Before model: 60-600 arcsec (1-10 arcmin)
- After good model: 5-30 arcsec

---

## Code Examples for ForkMount-WPF

### Example 1: Basic Usage

```csharp
using NaritMountLib.PointingModel;

public class PointingModelController
{
    private BuildPointingModel _model;
    
    public void StartCalibration()
    {
        _model = new BuildPointingModel();
        
        // Configure
        _model.SetExposureTime(3.0);
        _model.SetPixelScale(0.60);
        _model.SetBinning(2);
        
        // Generate 16-point grid
        _model.AddPoints(
            azmSpacing: 90.0,
            azmMin: 0.0,
            azmMax: 360.0,
            altSpacing: 45.0,
            altMin: 10.0,
            altMax: 85.0
        );
        
        // Start
        _model.StartAutoModel();
        
        // Monitor via timer/event
        // (Points update automatically via BindingList)
    }
    
    public void StopCalibration()
    {
        _model?.StopAutoModel();
    }
}
```

### Example 2: UI Integration

```csharp
public partial class CalibrationWindow : Window
{
    private BuildPointingModel _model;
    
    public CalibrationWindow()
    {
        InitializeComponent();
        
        _model = new BuildPointingModel();
        
        // Bind to DataGrid
        pointsDataGrid.ItemsSource = _model._autoMountPoints;
    }
    
    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _model.SetExposureTime(
            double.Parse(exposureTextBox.Text)
        );
        
        _model.AddPoints(
            azmSpacing: double.Parse(azmSpacingTextBox.Text),
            azmMin: double.Parse(azmMinTextBox.Text),
            azmMax: double.Parse(azmMaxTextBox.Text),
            altSpacing: double.Parse(altSpacingTextBox.Text),
            altMin: double.Parse(altMinTextBox.Text),
            altMax: double.Parse(altMaxTextBox.Text)
        );
        
        _model.StartAutoModel();
    }
    
    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _model.StopAutoModel();
    }
    
    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        _model.Resume();
    }
}
```

### Example 3: Progress Monitoring

```csharp
using System.Timers;

public class CalibrationMonitor
{
    private BuildPointingModel _model;
    private Timer _progressTimer;
    
    public event EventHandler<ProgressEventArgs> ProgressChanged;
    
    public void StartMonitoring(BuildPointingModel model)
    {
        _model = model;
        
        _progressTimer = new Timer(1000); // 1 second
        _progressTimer.Elapsed += OnProgressTimer;
        _progressTimer.Start();
    }
    
    private void OnProgressTimer(object sender, ElapsedEventArgs e)
    {
        var points = _model._autoMountPoints;
        
        int total = points.Count;
        int completed = points.Count(p => 
            p.State == MapPointState.AddedToModel
        );
        int failed = points.Count(p => 
            p.State == MapPointState.SolveFailed
        );
        int pending = points.Count(p => 
            p.State == MapPointState.Pending
        );
        int processing = total - completed - failed - pending;
        
        var args = new ProgressEventArgs
        {
            Total = total,
            Completed = completed,
            Failed = failed,
            Pending = pending,
            Processing = processing,
            PercentComplete = (completed + failed) * 100.0 / total
        };
        
        ProgressChanged?.Invoke(this, args);
        
        if (completed + failed == total)
        {
            _progressTimer.Stop();
        }
    }
}

public class ProgressEventArgs : EventArgs
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public double PercentComplete { get; set; }
}
```

---

## Next Steps for ForkMount-WPF Implementation

### Phase 1: Core Infrastructure
1. [REQUIRED] Implement `IMountManager` interface
2. [REQUIRED] Integrate MaxIm DL camera control
3. [REQUIRED] Setup PWLib.dll reference
4. [REQUIRED] Create configuration settings

### Phase 2: UI Development
1. Create CalibrationWindow.xaml
2. Add grid configuration controls
3. Add progress monitoring display
4. Add error visualization (sky plot)

### Phase 3: Data Processing
1. Implement pointing model fitting algorithm
2. Add sample data export (CSV/JSON)
3. Create error analysis tools
4. Add model save/load functionality

### Phase 4: Testing
1. Test with simulated camera
2. Test with real hardware
3. Verify plate solving accuracy
4. Performance optimization

---

## References

### External Dependencies
- **PWLib.dll** - PlaneWave Instruments Library
  - `BackgroundPlateSolver` class
  - Coordinate conversion utilities
  
- **MaxIm DL** - Camera control software
  - COM/Automation interface
  - FITS file generation

### Related Documentation
- [ForkMount Architecture](./AI_QUICK_REF.md)
- [Development Notes](./DEVELOPMENT_NOTES.md)
- [Session Handoff](./SESSION_HANDOFF.md)

### Astronomy References
- [Pointing Models](https://en.wikipedia.org/wiki/Astronomical_pointing_model)
- [Plate Solving](https://en.wikipedia.org/wiki/Astrometry)
- [FITS Format](https://fits.gsfc.nasa.gov/)

---

## Revision History

| Date | Version | Changes |
|------|---------|---------|
| 2025-01-XX | 1.0 | Initial documentation |

---

**[END OF DOCUMENT]**
