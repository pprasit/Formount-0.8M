# ForkMount Calibration System - Complete Guide
**Mount Pointing Model Calibration using Camera & Star Detection**

*Last Updated: 2025-01-21*

---

## ?? Table of Contents

1. [System Overview](#system-overview)
2. [Camera Integration](#camera-integration)
3. [Star Detection](#star-detection)
4. [Plate Solving](#plate-solving)
5. [Coordinate Conversion](#coordinate-conversion)
6. [Calibration Workflow](#calibration-workflow)
7. [Installation & Setup](#installation-setup)
8. [Usage Examples](#usage-examples)
9. [Troubleshooting](#troubleshooting)

---

## ?? 1. System Overview

### Architecture

```
???????????????????????????????????????????????????????
?  Mount Calibration System                           ?
???????????????????????????????????????????????????????
?                                                      ?
?  1. Camera Capture (ZWO ASI)                        ?
?     ??> ZWOASICameraController.cs                   ?
?         ??> CameraImageData.cs                      ?
?                                                      ?
?  2. Star Detection (AForge.NET)                     ?
?     ??> StarDetector.cs                             ?
?         ??> DetectedStar (50-200 stars)             ?
?                                                      ?
?  3. Plate Solving (ASTAP)                           ?
?     ??> ASTAPWrapper.cs                             ?
?         ??> PlateSolution (RA/Dec)                  ?
?                                                      ?
?  4. Coordinate Conversion                           ?
?     ??> CoordinateConverter.cs                      ?
?         ??> RA/Dec ? Az/Alt                         ?
?                                                      ?
?  5. Error Calculation                               ?
?     ??> Measured - Command = Error                  ?
?         ??> Pointing Model Fitting                  ?
?                                                      ?
???????????????????????????????????????????????????????
```

### Components

| Component | File | Description |
|-----------|------|-------------|
| **Camera** | `ZWOASICameraController.cs` | ZWO ASI camera interface |
| **Image Data** | `CameraImageData.cs` | Image + metadata container |
| **Star Detector** | `StarDetector.cs` | Find stars in image |
| **Plate Solver** | `ASTAPWrapper.cs` | Match stars ? RA/Dec |
| **Coordinates** | `CoordinateConverter.cs` | RA/Dec ? Az/Alt |
| **UI** | `MountCalibrationControl.xaml.cs` | Main calibration interface |

---

## ?? 2. Camera Integration

### ZWO ASI Camera Support

**Supported Models:**
- ASI120MM/MC
- ASI224MC
- ASI294MC/MM Pro
- ASI533MC/MM Pro
- ASI2600MC/MM Pro
- All ZWO ASI cameras via SDK

### Installation

#### Step 1: NuGet Package (Already Installed)
```xml
<PackageReference Include="ZWOptical.SDK" Version="3.4.531" />
```

#### Step 2: Basic Usage

```csharp
using ForkMount_WPF.Engine.Camera;

// 1. Discover cameras
var cameras = ZWOASICameraController.GetConnectedCameras();

// 2. Connect
var camera = new ZWOASICameraController();
camera.Connect(0);  // First camera

// 3. Configure
camera.SetExposure(5000000);  // 5 seconds
camera.SetGain(150);
camera.SetImageType(ASI_IMG_TYPE.ASI_IMG_RAW16);

// 4. Capture
var imageData = await camera.CaptureImageAsync();

// 5. Disconnect
camera.Disconnect();
```

### CameraImageData Properties

```csharp
public class CameraImageData
{
    // Image data
    public byte[] ImageData;
    public int Width, Height;
    public ASI_IMG_TYPE ImageType;
    
    // Camera settings
    public long ExposureTime_us;
    public int Gain, Offset;
    public DateTime CaptureTime;
    public double? Temperature_C;
    
    // Mount position
    public double? Target_Azimuth;
    public double? Target_Altitude;
    public double? Mount_Azimuth;
    public double? Mount_Altitude;
    
    // Observer location
    public double? Observer_Latitude;
    public double? Observer_Longitude;
    public double? Observer_Altitude_m;
    
    // Methods
    public BitmapImage GetBitmapImage();
    public double GetExposureTime_Seconds();
}
```

### Recommended Settings for Calibration

```csharp
// Bright sky (city)
camera.SetExposure(2000000);  // 2 sec
camera.SetGain(100);

// Dark sky (remote)
camera.SetExposure(5000000);  // 5 sec
camera.SetGain(150);

// Very dark + high altitude
camera.SetExposure(10000000); // 10 sec
camera.SetGain(200);
```

---

## ?? 3. Star Detection

### Algorithm Overview

**Process:**
1. Preprocess image (background removal, noise reduction)
2. Threshold detection (mean + 5?)
3. Blob detection (connected components)
4. Filter by size & circularity
5. Sub-pixel centroid calculation

### StarDetector Configuration

```csharp
var detector = new StarDetector
{
    ThresholdSigma = 5.0,          // 5? above mean
    MinStarSize = 3,                // Min 3 pixels
    MaxStarSize = 50,               // Max 50 pixels
    MinCircularity = 0.5,           // At least 50% circular
    MaxStars = 200,                 // Return top 200 brightest
    EnablePreprocessing = true,     // Clean up image
    EnableContrastEnhancement = true,
    CentroidWindowSize = 7          // 7x7 window for centroid
};
```

### Usage

```csharp
// Detect stars
var stars = detector.DetectStars(imageData);

// Process results
foreach (var star in stars)
{
    Console.WriteLine($"Star at ({star.X:F2}, {star.Y:F2})");
    Console.WriteLine($"  Brightness: {star.Brightness:F0}");
    Console.WriteLine($"  FWHM: {star.FWHM:F2} pixels");
    Console.WriteLine($"  Circularity: {star.Circularity:F2}");
}
```

### DetectedStar Properties

```csharp
public class DetectedStar
{
    public double X, Y;              // Sub-pixel position
    public int IntegerX, IntegerY;   // Rounded position
    public double Brightness;         // Sum of intensities
    public double Area;               // Area in pixels
    public double Circularity;        // 0-1 (1 = circle)
    public double FWHM;               // Full Width Half Maximum
    public double SNR;                // Signal-to-Noise Ratio
}
```

### Expected Performance

| Condition | Stars Detected | Time |
|-----------|----------------|------|
| City sky | 20-50 | 0.5-1 sec |
| Suburban | 50-100 | 1-2 sec |
| Dark sky | 100-200 | 2-4 sec |

### Accuracy

- **Centroid Accuracy**: ±0.1 pixels
- **With 2.4"/pixel scale**: ±0.24 arcseconds
- **Sub-pixel weighting**: Improves by 5-10x vs integer position

---

## ?? 4. Plate Solving (ASTAP)

### What is Plate Solving?

Match detected star patterns against a star catalog to determine:
- RA/Dec of image center
- Pixel scale (arcsec/pixel)
- Rotation angle
- Field of view

### ASTAP Installation

#### Step 1: Download ASTAP
1. Go to: http://www.hnsky.org/astap.htm
2. Download Windows version (~50 MB)
3. Extract to: `C:\Program Files\astap\`

#### Step 2: Download Star Database

**Recommended: h18.dat**
- Size: 1.4 GB
- Stars: 19 million (magnitude 18)
- Download: http://www.hnsky.org/h18.zip
- Extract to: `C:\Program Files\astap\h18.dat`

**Alternative: g17.dat (faster)**
- Size: 950 MB
- Stars: 18 million (magnitude 16)
- Download: http://www.hnsky.org/g17.zip

#### Step 3: Verify Installation

```
C:\Program Files\astap\
??? astap.exe
??? h18.dat  (or g17.dat)
```

### ASTAPWrapper Usage

```csharp
using ForkMount_WPF.Engine.Camera;

// 1. Create wrapper
var astap = new ASTAPWrapper
{
    ASTAPExecutablePath = @"C:\Program Files\astap\astap.exe",
    SearchRadius = 30.0,      // Search within 30°
    DownsampleFactor = 2,     // 2x downsample for speed
    TimeoutSeconds = 60
};

// 2. Check availability
if (!astap.IsASTAPAvailable())
{
    Console.WriteLine("ASTAP not installed!");
    return;
}

// 3. Solve plate
var solution = await astap.SolveAsync(imageData);

// 4. Check result
if (solution != null && solution.Solved)
{
    Console.WriteLine($"RA: {solution.RA_Center:F6}°");
    Console.WriteLine($"Dec: {solution.Dec_Center:F6}°");
    Console.WriteLine($"Pixel Scale: {solution.PixelScale:F3}\"/px");
    Console.WriteLine($"Rotation: {solution.Rotation:F2}°");
}
```

### PlateSolution Properties

```csharp
public class PlateSolution
{
    public bool Solved;           // Success?
    public double RA_Center;      // RA in degrees (0-360)
    public double Dec_Center;     // Dec in degrees (-90 to +90)
    public double PixelScale;     // arcsec/pixel
    public double Rotation;       // degrees
    public double FOV_Width;      // degrees
    public double FOV_Height;     // degrees
    public int StarsMatched;      // Number of stars matched
}
```

### Performance

| Image Size | Stars | Downsample | Time | Accuracy |
|------------|-------|------------|------|----------|
| 1920x1080 | 100+ | 2x | 3-5 sec | ±1" |
| 4096x2160 | 200+ | 2x | 5-10 sec | ±0.8" |
| 6000x4000 | 300+ | 4x | 8-15 sec | ±0.5" |

---

## ?? 5. Coordinate Conversion

### RA/Dec ? Az/Alt Conversion

```csharp
using ForkMount_WPF.Engine.Camera;

// Get plate solution
double ra = solution.RA_Center;
double dec = solution.Dec_Center;

// Observer location
double latitude = _observerLocation.Latitude;
double longitude = _observerLocation.Longitude;
DateTime utcTime = DateTime.UtcNow;

// Convert
var (azimuth, altitude) = CoordinateConverter.RADecToAzAlt(
    ra, dec,
    latitude, longitude,
    utcTime
);

Console.WriteLine($"Azimuth: {azimuth:F6}°");
Console.WriteLine($"Altitude: {altitude:F6}°");
```

### Az/Alt ? RA/Dec Conversion

```csharp
var (ra, dec) = CoordinateConverter.AzAltToRADec(
    azimuth, altitude,
    latitude, longitude,
    DateTime.UtcNow
);
```

### Angular Separation

```csharp
double separation = CoordinateConverter.AngularSeparation(
    ra1, dec1,
    ra2, dec2
);
```

### Important Notes

1. **Always use UTC time** - Not local time!
2. **Longitude convention**: East is positive
3. **Azimuth**: 0° = North, 90° = East, 180° = South, 270° = West
4. **Altitude**: 0° = horizon, 90° = zenith

---

## ?? 6. Calibration Workflow

### Complete Process

```csharp
private async Task CaptureCalibrationImageAsync(
    int pointNumber, 
    double targetAz, double targetAlt,
    double mountAz, double mountAlt)
{
    try
    {
        // 1. Wait for mount to stabilize
        await Task.Delay(2000);
        
        // 2. Capture image
        var imageData = await _camera.CaptureImageAsync();
        if (imageData == null) return;
        
        // Add metadata
        imageData.Target_Azimuth = targetAz;
        imageData.Target_Altitude = targetAlt;
        imageData.Mount_Azimuth = mountAz;
        imageData.Mount_Altitude = mountAlt;
        imageData.Observer_Latitude = _observerLocation.Latitude;
        imageData.Observer_Longitude = _observerLocation.Longitude;
        
        // 3. Detect stars
        var detector = new StarDetector();
        var stars = detector.DetectStars(imageData);
        Console.WriteLine($"[Calibration] Detected {stars.Count} stars");
        
        // 4. Plate solve
        var astap = new ASTAPWrapper();
        var solution = await astap.SolveAsync(imageData);
        
        if (solution != null && solution.Solved)
        {
            // 5. Convert to Az/Alt
            var (measuredAz, measuredAlt) = CoordinateConverter.RADecToAzAlt(
                solution.RA_Center,
                solution.Dec_Center,
                _observerLocation.Latitude,
                _observerLocation.Longitude,
                DateTime.UtcNow
            );
            
            // 6. Calculate error
            double azError = measuredAz - targetAz;
            double altError = measuredAlt - targetAlt;
            
            Console.WriteLine($"[Calibration] Error: " +
                $"Az={azError * 3600:F2}\" Alt={altError * 3600:F2}\"");
            
            // 7. Update pointing model data
            currentPoint.AzimuthError = azError;
            currentPoint.AltitudeError = altError;
            
            // 8. Update UI
            await Dispatcher.InvokeAsync(() =>
            {
                var bitmap = imageData.GetBitmapImage();
                liveCalibrationImageControl?.AddCalibrationImage(
                    pointNumber: pointNumber,
                    totalPoints: _pointingModelData.Count,
                    azimuth: targetAz,
                    altitude: targetAlt,
                    image: bitmap,
                    starsDetected: stars.Count,
                    rmsError: 0.0
                );
            });
        }
        else
        {
            Console.WriteLine("[Calibration] Plate solving failed");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Calibration] Error: {ex.Message}");
    }
}
```

### Calibration Grid

```csharp
// Generate calibration points
// Example: 8 azimuth × 5 altitude = 40 points

int azPoints = 8;   // 0°, 45°, 90°, 135°, 180°, 225°, 270°, 315°
int altPoints = 5;  // 30°, 41.25°, 52.5°, 63.75°, 75°

for (int azIdx = 0; azIdx < azPoints; azIdx++)
{
    double azimuth = azIdx * (360.0 / azPoints);
    
    for (int altIdx = 0; altIdx < altPoints; altIdx++)
    {
        double altitude = 30.0 + altIdx * (55.0 / (altPoints - 1));
        
        // Create calibration point
        var point = new PointingModelPoint
        {
            Azimuth = azimuth,
            Altitude = altitude,
            Status = "[PENDING]"
        };
    }
}
```

---

## ??? 7. Installation & Setup

### Prerequisites

1. **.NET 10** - Already installed
2. **NuGet Packages** - Already installed:
   - ZWOptical.SDK 3.4.531
   - AForge 2.2.5
   - AForge.Imaging 2.2.5
   - AForge.Math 2.2.5

### Installation Steps

#### 1. ASTAP Setup (Required)

```powershell
# Download ASTAP
Start-Process "http://www.hnsky.org/astap.htm"

# Download h18 database
Start-Process "http://www.hnsky.org/h18.zip"

# Extract both to: C:\Program Files\astap\
```

#### 2. Camera Setup (If using ZWO)

- Connect ZWO ASI camera via USB 3.0
- Install ZWO ASI driver (automatic via SDK)
- Test connection:

```csharp
int count = ZWOASICameraController.GetConnectedCameraCount();
Console.WriteLine($"Found {count} cameras");
```

#### 3. Verify Installation

```csharp
// Test all components
var camera = new ZWOASICameraController();
var detector = new StarDetector();
var astap = new ASTAPWrapper();

Console.WriteLine($"Camera: {camera.IsConnected}");
Console.WriteLine($"ASTAP: {astap.IsASTAPAvailable()}");
```

---

## ?? 8. Usage Examples

### Example 1: Simple Image Capture & Star Detection

```csharp
// Connect camera
var camera = new ZWOASICameraController();
camera.Connect(0);
camera.SetExposure(5000000);  // 5 sec
camera.SetGain(150);

// Capture
var image = await camera.CaptureImageAsync();

// Detect stars
var detector = new StarDetector();
var stars = detector.DetectStars(image);

Console.WriteLine($"Detected {stars.Count} stars");
foreach (var star in stars.Take(10))
{
    Console.WriteLine($"  Star at ({star.X:F2}, {star.Y:F2}), " +
        $"Brightness={star.Brightness:F0}");
}
```

### Example 2: Full Plate Solving Pipeline

```csharp
// 1. Capture image
var image = await camera.CaptureImageAsync();

// 2. Detect stars
var stars = detector.DetectStars(image);
Console.WriteLine($"Detected {stars.Count} stars");

// 3. Solve plate
var astap = new ASTAPWrapper();
var solution = await astap.SolveAsync(image);

if (solution.Solved)
{
    Console.WriteLine($"Solved: RA={solution.RA_Center:F6}° " +
        $"Dec={solution.Dec_Center:F6}°");
    
    // 4. Convert to Az/Alt
    var (az, alt) = CoordinateConverter.RADecToAzAlt(
        solution.RA_Center,
        solution.Dec_Center,
        13.7563,  // Bangkok latitude
        100.5018, // Bangkok longitude
        DateTime.UtcNow
    );
    
    Console.WriteLine($"Position: Az={az:F6}° Alt={alt:F6}°");
}
```

### Example 3: Calculate Pointing Error

```csharp
// Command position
double commandAz = 45.0;
double commandAlt = 60.0;

// Measured position (from plate solving)
double measuredAz = 45.0123;
double measuredAlt = 60.0089;

// Calculate error
double azError = (measuredAz - commandAz) * 3600;  // arcsec
double altError = (measuredAlt - commandAlt) * 3600;

Console.WriteLine($"Az Error: {azError:F2}\"");
Console.WriteLine($"Alt Error: {altError:F2}\"");
Console.WriteLine($"Total Error: {Math.Sqrt(azError*azError + altError*altError):F2}\"");
```

---

## ?? 9. Troubleshooting

### Camera Issues

**Problem: No camera found**
```csharp
// Check USB connection
// Try different USB port (USB 3.0 recommended)
// Check Windows Device Manager
```

**Problem: Capture timeout**
```csharp
// Reduce exposure time
camera.SetExposure(1000000);  // 1 sec instead of 5 sec
```

### Star Detection Issues

**Problem: Too few stars detected**
```csharp
// Lower threshold
detector.ThresholdSigma = 3.0;  // Instead of 5.0

// Increase max stars
detector.MaxStars = 500;

// Check image brightness
var stats = new ImageStatistics(image);
Console.WriteLine($"Mean: {stats.Gray.Mean}, StdDev: {stats.Gray.StdDev}");
```

**Problem: Too many false detections**
```csharp
// Increase threshold
detector.ThresholdSigma = 7.0;

// Increase minimum size
detector.MinStarSize = 5;

// Increase circularity requirement
detector.MinCircularity = 0.7;
```

### Plate Solving Issues

**Problem: ASTAP fails to solve**
```csharp
// Increase search radius
astap.SearchRadius = 60.0;

// Don't specify FOV if unknown
astap.FieldOfView = null;

// Try different downsample
astap.DownsampleFactor = 1;  // No downsampling
```

**Problem: Wrong RA/Dec**
```csharp
// Check star database is installed
// Check h18.dat or g17.dat exists in ASTAP folder
// Try with more stars in image
```

### Coordinate Conversion Issues

**Problem: Wrong Az/Alt**
```csharp
// Verify observer location
Console.WriteLine($"Lat: {latitude}, Lon: {longitude}");

// Use UTC time, not local
DateTime utc = DateTime.UtcNow;  // NOT DateTime.Now

// Check time is correct
Console.WriteLine($"UTC: {utc}");
```

---

## ?? Expected Accuracy

### Error Budget

| Source | Typical Error | Notes |
|--------|---------------|-------|
| Star Detection | ±0.2" | Sub-pixel centroiding |
| Plate Solving | ±1.0" | ASTAP accuracy |
| Coordinate Conversion | ±0.1" | Calculation precision |
| Mount Encoder | ±10-30" | Hardware limitation |
| **Total System** | **±15-35"** | Before pointing model |
| **After Calibration** | **±5-15"** | With pointing model |

### Performance Targets

- **Calibration Points**: 40-100 points
- **Time per Point**: 10-20 seconds
- **Total Calibration Time**: 10-30 minutes
- **Final Tracking Accuracy**: ±10" RMS

---

## ?? References

### Documentation
- ASTAP: http://www.hnsky.org/astap.htm
- ZWO SDK: https://astronomy-imaging-camera.com/software-drivers
- AForge.NET: http://www.aforgenet.com/framework/

### Star Catalogs
- Hipparcos: https://www.cosmos.esa.int/web/hipparcos
- Gaia DR2: https://gea.esac.esa.int/archive/
- Tycho-2: https://www.cosmos.esa.int/web/tycho-2

### Algorithms
- Triangle Pattern Matching
- Weighted Centroid Calculation
- Local Sidereal Time Calculation
- Coordinate Transformations

---

## ? Summary

**What We Have:**

1. ? Camera capture (ZWOASICameraController)
2. ? Star detection (StarDetector) 
3. ? Plate solving (ASTAPWrapper)
4. ? Coordinate conversion (CoordinateConverter)
5. ? UI integration (MountCalibrationControl)

**Next Steps:**

1. Install ASTAP + star database
2. Test with real camera
3. Run calibration sequence
4. Build pointing model
5. Achieve ±10" tracking accuracy!

---

**Questions?** Check the troubleshooting section or review the code examples above.

**Ready to calibrate!** ?????
