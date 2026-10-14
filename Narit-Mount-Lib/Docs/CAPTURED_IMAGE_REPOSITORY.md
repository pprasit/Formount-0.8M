# ?? Captured Image Repository System

## Overview
???????????????????????????????????????????? ?????????? Plate Solving ??? Mount Calibration

## Components

### 1. `CapturedImageInfo` - ????????????????
????????????????? **16-bit 2D array** (`ushort[,]`) ????? metadata ???????

#### Key Properties:
- **Search Keys**:
  - `CaptureTime` (DateTime) - ??????????? (UTC)
  - `Azimuth_deg` (double) - Azimuth ??? telescope
  - `Altitude_deg` (double) - Altitude ??? telescope
  
- **Image Data**:
  - `ImageData` (ushort[,]) - 16-bit 2D array [row, col]
  - `Width`, `Height` - ??????? (???? binning)
  
- **Camera Settings**:
  - `Binning`, `ExposureTime_ms`, `Gain`, `Offset`
  - `Temperature_C` - ???????? camera
  
- **Optical Configuration**:
  - `FocalLength_mm` - ????????????
  - `PlateScale_ArcsecPerPixel` - plate scale
  - `FOV_Width_deg`, `FOV_Height_deg` - field of view
  
- **Plate Solve Results**:
  - `PlateSolveStatus` - ???????? solve
  - `Solved_RA_deg`, `Solved_Dec_deg` - ?????? solve ???
  - `PositionError_arcsec` - ???????????????
  - `StarsDetected` - ?????????????????

#### Usage Example:
```csharp
// Create from CameraImageData
var capturedImage = CapturedImageInfo.FromCameraImageData(
    cameraData,          // CameraImageData from camera
    azimuth: 180.0,      // Telescope azimuth
    altitude: 45.0,      // Telescope altitude
    focalLength_mm: 200.0,
    plateScale_arcsecPerPixel: 1.5);

// Calculate image statistics
capturedImage.CalculateStatistics();

// Access 2D image data
ushort pixelValue = capturedImage.ImageData[row, col];

// Get image size
double sizeMB = capturedImage.GetImageSize_MB();
```

### 2. `CapturedImageRepository` - Concurrent Repository
Thread-safe repository ???????????????????????????????????

#### Features:
- ? **Thread-safe**: ??? `ConcurrentDictionary`
- ? **Multiple search keys**: Time, Position (Az/Alt), Solve Status, Quality
- ? **Auto memory management**: ??????????????????? (default: 1000)
- ? **Fast indexing**: Secondary indexes ??????????????????

#### Search Methods:

##### ?? Search by Time
```csharp
var repository = new CapturedImageRepository();

// Get by exact time
var image = repository.GetImageByTime(captureTime);

// Get within time range
var images = repository.GetImagesByTimeRange(startTime, endTime);

// Get closest to time
var closest = repository.GetImageClosestToTime(targetTime);

// Get recent images
var recent = repository.GetRecentImages(count: 10);
```

##### ?? Search by Position (Az/Alt)
```csharp
// Get images near position (tolerance ±0.5°)
var nearby = repository.GetImagesByPosition(
    azimuth_deg: 180.0, 
    altitude_deg: 45.0, 
    tolerance_deg: 0.5);

// Get closest to position
var closest = repository.GetImageClosestToPosition(180.0, 45.0);
```

##### ?? Search by Solve Status
```csharp
// Get solved images
var solved = repository.GetSolvedImages();

// Get unsolved images
var unsolved = repository.GetUnsolvedImages();

// Get failed images
var failed = repository.GetFailedImages();
```

##### ?? Search by Quality
```csharp
// Get images with quality > 70
var highQuality = repository.GetImagesByQuality(minQualityScore: 70);

// Get top 10 best quality
var best = repository.GetBestQualityImages(count: 10);
```

#### Statistics
```csharp
// Get repository statistics
string stats = repository.GetStatistics();

// Output:
// ?? Image Repository Statistics:
//   Total Images: 42
//   Solved: 38
//   Unsolved: 3
//   Failed: 1
//   Memory Usage: 52.34 MB
//   Time Range: 20:15:30 - 21:45:12
//   Avg Quality: 78.5
```

## Integration with Camera

### In `CameraSettingsWindow.xaml.cs`:
```csharp
// Initialize repository
private CapturedImageRepository _imageRepository = new CapturedImageRepository();

// After capturing image (in BtnTestCapture_Click)
if (imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16)
{
    // Get mount position
    double azimuth = /* from mount manager */;
    double altitude = /* from mount manager */;
    
    // Get camera settings
    var settings = AppSettings.GetCameraSettings();
    
    // Create and store captured image
    var capturedImage = CapturedImageInfo.FromCameraImageData(
        imageData,
        azimuth,
        altitude,
        settings.FocalLength_mm,
        settings.PlateScale_ArcsecPerPixel);
    
    _imageRepository.AddImage(capturedImage);
    
    Debug.WriteLine($"? Image stored: {capturedImage.ImageId}");
    Debug.WriteLine($"?? Repository: {_imageRepository.Count} images");
}
```

## Data Format

### Image Data Storage:
- **Format**: `ushort[,]` 2D array
- **Dimensions**: `[height, width]` (row-major order)
- **Pixel format**: 16-bit unsigned integer (0-65535)
- **Memory**: 2 bytes per pixel

### Coordinate System:
- **Azimuth**: 0° = North, 90° = East, 180° = South, 270° = West
- **Altitude**: 0° = Horizon, 90° = Zenith
- **RA/Dec**: Equatorial coordinates (degrees)

## Performance

### Memory Usage:
- 1000 images @ 11656x8750 (bin 1x1) = ~200 GB ? (too large!)
- 1000 images @ 5828x4375 (bin 2x2) = ~50 GB ? (still large)
- **1000 images @ 1000x1000 (cropped/binned) = ~2 GB** ? (recommended)

### Recommendations:
1. **Use binning 2x2 or higher** for faster processing
2. **Crop to region of interest** if possible
3. **Limit repository size** (default: 1000 images)
4. **Clear old images** periodically

## TODO: Next Steps

### 1. ? Integration with Mount Manager
```csharp
// Get actual mount position
var mountPos = _mountManager.GetCurrentPosition();
double azimuth = mountPos.Azimuth_deg;
double altitude = mountPos.Altitude_deg;
```

### 2. ?? Plate Solving Integration
```csharp
// After capture
var capturedImage = /* ... */;
_imageRepository.AddImage(capturedImage);

// Send to plate solver
var solveResult = await _plateSolver.SolveAsync(capturedImage);

// Update image with results
capturedImage.PlateSolveStatus = PlateSolveStatus.Solved;
capturedImage.Solved_RA_deg = solveResult.RA;
capturedImage.Solved_Dec_deg = solveResult.Dec;
capturedImage.PositionError_arcsec = solveResult.Error;
```

### 3. ?? Mount Calibration
```csharp
// Get all solved images
var solvedImages = _imageRepository.GetSolvedImages();

// Build pointing model
var calibration = new MountCalibrationModel();
foreach (var img in solvedImages)
{
    calibration.AddPoint(
        commanded_Az: img.Azimuth_deg,
        commanded_Alt: img.Altitude_deg,
        actual_RA: img.Solved_RA_deg,
        actual_Dec: img.Solved_Dec_deg);
}

calibration.Calculate();
```

### 4. ?? Image Quality Assessment
```csharp
// Calculate quality score based on:
// - Star FWHM (Full Width Half Maximum)
// - Signal-to-Noise Ratio (SNR)
// - Number of stars detected
// - Background noise level

capturedImage.QualityScore = CalculateQualityScore(capturedImage);
```

## Testing

### Run Demo:
```csharp
// In your test code
CapturedImageRepositoryDemo.RunDemo();
```

### Demo Output:
```
==========================================
?? Captured Image Repository Demo
==========================================

?? Simulating capture sequence...

?? Search by Time:
  Most recent: Image ... captured at ...
  Images in last 10 sec: 5

?? Search by Position:
  Images near Az=180°, Alt=60° (±5°): 1
    - ... at Az=180.0°, Alt=60.0° (?Az=0.0°, ?Alt=0.0°)

?? Search by Solve Status:
  Solved images: 2
  Unsolved images: 3

?? Search by Quality:
  Top 3 quality images:
    - ...: Quality=85.5, Az=180.0°, Alt=60.0°
    - ...: Quality=72.3, Az=90.0°, Alt=30.0°

?? Image Repository Statistics:
  Total Images: 5
  Solved: 2
  Unsolved: 3
  Failed: 0
  Memory Usage: 0.19 MB

==========================================
? Demo Complete
==========================================
```

## API Reference

### CapturedImageInfo Methods:
- `FromCameraImageData()` - Create from camera data
- `CalculateStatistics()` - Calculate min/max/mean/stddev
- `GetImageSize_MB()` - Get memory usage
- `ConvertTo1DByteArray()` - Convert back to byte array

### CapturedImageRepository Methods:
- `AddImage()` - Add image to repository
- `RemoveImage()` - Remove image by ID
- `Clear()` - Clear all images
- `GetImageById()` - Get by ID
- `GetImageByTime()` - Get by exact time
- `GetImagesByTimeRange()` - Get by time range
- `GetImagesByPosition()` - Get by position with tolerance
- `GetSolvedImages()` - Get all solved images
- `GetImagesByQuality()` - Get by quality score
- `GetStatistics()` - Get repository statistics

## Notes

### Performance Considerations:
1. **2D Array Access**: Fast random access to pixels
2. **Memory**: 16-bit = 2 bytes per pixel
3. **Thread Safety**: All methods are thread-safe
4. **Indexing**: Multiple indexes for fast search

### Best Practices:
1. ? Use **binning 2x2** for faster processing
2. ? **Calculate statistics** after capture
3. ? **Clear repository** periodically
4. ? **Check solve status** before using results
5. ? **Validate plate scale** for accurate FOV

---

Created: 2025-01-20  
Author: GitHub Copilot  
Version: 1.0
