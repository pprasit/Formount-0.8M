# ??? Image Preview System - Memory Efficient UI Display

## Overview
????????? **Preview Image** ???????????? UI ????????????????????????????????????

## Problem
- **Full Image**: 11656×8750 (16-bit) = **~200 MB** per image
- ?????? UI ????? ??? ? **RAM ????????!**

## Solution: Two-Tier Image System

### 1?? **Full Image Data (16-bit)** ? `CapturedImageRepository`
- ??????????? 16-bit ?? `CapturedImageInfo.ImageData` (ushort[,])
- ?????????:
  - ? Plate Solving (?????????????????????)
  - ? Star Detection (??????? dynamic range ???)
  - ? Image Analysis (???????????????? bit)

### 2?? **Preview Image (8-bit, scaled)** ? `LiveCalibrationImageControl`
- ??????????????????? `ImagePreviewHelper`
- ?????????:
  1. **Downsample** (800×600) ? ?? pixels
  2. **Histogram Stretch** (auto) ? ??????????????
  3. **Convert to 8-bit** ? ???????????
- ?????????:
  - ? ?????? UI (LiveCalibrationImageControl)
  - ? Preview ???????? (????????????????????)

## Memory Savings

| Image Type | Resolution | Bit Depth | Size (MB) | Use Case |
|------------|-----------|-----------|-----------|----------|
| **Full** | 11656×8750 | 16-bit | **~200 MB** | Plate Solving, Analysis |
| **Preview** | 800×600 | 8-bit | **~0.46 MB** | UI Display Only |
| **Savings** | | | **~199.5 MB** | **433x smaller!** |

**With 40 calibration points:**
- Old way: 40 × 200 MB = **8 GB RAM** ?
- New way: 40 × 0.46 MB = **~18 MB RAM** ?

---

## Usage

### ?? Step 1: Capture Image & Store Full Data

```csharp
// Capture full 16-bit image
var imageData = await _camera.CaptureImageAsync(); // RAW16

// Create CapturedImageInfo (stores full 16-bit data)
var capturedImage = CapturedImageInfo.FromCameraImageData(
    imageData,
    azimuth: 180.0,
    altitude: 45.0,
    focalLength_mm: 200.0,
    plateScale_arcsecPerPixel: 1.5);

// Store full image in repository (for Plate Solving later)
_imageRepository.AddImage(capturedImage);

Debug.WriteLine($"Full image stored: {capturedImage.GetImageSize_MB():F2} MB");
// Output: Full image stored: 200.45 MB
```

### ??? Step 2: Create Preview for UI Display

```csharp
// Create memory-efficient preview (8-bit, 800x600, stretched)
var previewImage = capturedImage.CreatePreviewImage(
    maxWidth: 800,
    maxHeight: 600,
    autoStretch: true);

// Display in LiveCalibrationImageControl
liveCalibrationImageControl.AddCalibrationImage(
    pointNumber: 1,
    totalPoints: 40,
    azimuth: 180.0,
    altitude: 45.0,
    image: previewImage,  // ? Preview only! (0.46 MB)
    starsDetected: 42,
    rmsError: 2.5,
    statusText: "[COMPLETED]");

Debug.WriteLine($"Preview displayed: {0.46:F2} MB");
// Output: Preview displayed: 0.46 MB
```

### ?? Step 3: Use Full Data for Analysis

```csharp
// Later: Retrieve full image for Plate Solving
var fullImage = _imageRepository.GetImageById(capturedImage.ImageId);

// Access full 16-bit data
ushort pixelValue = fullImage.ImageData[row, col]; // Full precision!

// Send to Plate Solver
var solveResult = await _plateSolver.SolveAsync(fullImage);
```

---

## API Reference

### `ImagePreviewHelper.CreatePreviewImage()`

????? preview image ?????????? memory

```csharp
public static BitmapImage CreatePreviewImage(
    ushort[,] imageData16bit,        // Full 16-bit image data
    int maxPreviewWidth = 800,       // Max width (maintains aspect)
    int maxPreviewHeight = 600,      // Max height (maintains aspect)
    bool autoStretch = true)         // Apply histogram stretch
```

**Parameters:**
- `imageData16bit`: ??????? 16-bit (??? `CapturedImageInfo.ImageData`)
- `maxPreviewWidth`: ?????????????????? preview (default: 800px)
- `maxPreviewHeight`: ???????????????? preview (default: 600px)
- `autoStretch`: ??? histogram stretch ??????? (default: true)

**Returns:**
- `BitmapImage`: ?????? 8-bit ??????????? WPF (frozen)

**Processing Steps:**
1. **Calculate dimensions** - ?????????????? aspect ratio
2. **Downsample** - ????????????? area averaging
3. **Histogram stretch** - auto black/white point (2nd-98th percentile)
4. **Convert to 8-bit** - ???????????
5. **Create BitmapImage** - ??????????? WPF

### `CapturedImageInfo.CreatePreviewImage()`

Convenience method ??????????? preview ??? `CapturedImageInfo`

```csharp
public BitmapImage CreatePreviewImage(
    int maxWidth = 800,
    int maxHeight = 600,
    bool autoStretch = true)
```

**Example:**
```csharp
var preview = capturedImage.CreatePreviewImage(); // Uses defaults
```

---

## Example: Complete Calibration Workflow

```csharp
public async Task CaptureCalibrationPoint(int pointNumber, double azimuth, double altitude)
{
    // Step 1: Capture full 16-bit image from camera
    Debug.WriteLine($"[Point {pointNumber}] Capturing image...");
    var cameraData = await _camera.CaptureImageAsync(); // RAW16
    
    if (cameraData == null)
    {
        Debug.WriteLine("[ERROR] Capture failed!");
        return;
    }
    
    Debug.WriteLine($"[Point {pointNumber}] Captured: {cameraData.Width}x{cameraData.Height} " +
                   $"({cameraData.ImageData.Length / 1024 / 1024} MB raw)");
    
    // Step 2: Store full image in repository (for Plate Solving)
    var capturedImage = CapturedImageInfo.FromCameraImageData(
        cameraData,
        azimuth,
        altitude,
        focalLength_mm: 200.0,
        plateScale_arcsecPerPixel: 1.5);
    
    _imageRepository.AddImage(capturedImage);
    
    Debug.WriteLine($"[Point {pointNumber}] Full image stored in repository: " +
                   $"{capturedImage.GetImageSize_MB():F2} MB");
    Debug.WriteLine($"[Repository] Total images: {_imageRepository.Count}, " +
                   $"Total memory: {_imageRepository.TotalMemoryUsage_MB:F2} MB");
    
    // Step 3: Create memory-efficient preview for UI
    Debug.WriteLine($"[Point {pointNumber}] Creating preview image...");
    var previewImage = capturedImage.CreatePreviewImage(
        maxWidth: 800,
        maxHeight: 600,
        autoStretch: true);
    
    // Step 4: Display preview in UI (NOT full image!)
    liveCalibrationImageControl.AddCalibrationImage(
        pointNumber: pointNumber,
        totalPoints: 40,
        azimuth: azimuth,
        altitude: altitude,
        image: previewImage,        // ? Preview only! (~0.5 MB)
        starsDetected: 0,           // Will update after star detection
        rmsError: 0.0,              // Will update after plate solving
        statusText: "[CAPTURING]");
    
    Debug.WriteLine($"[Point {pointNumber}] Preview displayed in UI");
    
    // Step 5: (Later) Plate solve using FULL image from repository
    Debug.WriteLine($"[Point {pointNumber}] Starting Plate Solve...");
    var solveResult = await PlateSolveAsync(capturedImage);
    
    if (solveResult.Success)
    {
        // Update CapturedImageInfo with solve results
        capturedImage.PlateSolveStatus = PlateSolveStatus.Solved;
        capturedImage.Solved_RA_deg = solveResult.RA;
        capturedImage.Solved_Dec_deg = solveResult.Dec;
        capturedImage.PositionError_arcsec = solveResult.Error;
        capturedImage.StarsDetected = solveResult.Stars;
        
        // Update UI status (still uses preview image)
        liveCalibrationImageControl.UpdateCalibrationImage(
            pointNumber: pointNumber,
            starsDetected: solveResult.Stars,
            rmsError: solveResult.Error,
            statusText: "[COMPLETED]");
        
        Debug.WriteLine($"[Point {pointNumber}] ? Plate solved! " +
                       $"RA={solveResult.RA:F4}° Dec={solveResult.Dec:F4}° " +
                       $"Error={solveResult.Error:F2}\" Stars={solveResult.Stars}");
    }
    else
    {
        Debug.WriteLine($"[Point {pointNumber}] ? Plate solve failed!");
    }
}
```

**Output:**
```
[Point 1] Capturing image...
[Point 1] Captured: 11656x8750 (200 MB raw)
[Point 1] Full image stored in repository: 200.45 MB
[Repository] Total images: 1, Total memory: 200.45 MB
[Point 1] Creating preview image...
[ImagePreview] Creating preview from 11656x8750 (16-bit)
[ImagePreview] ? Preview created: 800x600 (8-bit)
[ImagePreview] Memory saved: 199.5 MB
[Point 1] Preview displayed in UI
[Point 1] Starting Plate Solve...
[Point 1] ? Plate solved! RA=180.0000° Dec=45.0000° Error=2.50" Stars=42
```

---

## Best Practices

### ? DO:
- Store **full 16-bit image** in `CapturedImageRepository`
- Display **preview 8-bit image** in UI (`LiveCalibrationImageControl`)
- Use **auto stretch** for preview visibility
- Limit **preview resolution** to 800×600 or less

### ? DON'T:
- Display full 16-bit image directly in UI (wastes RAM)
- Store preview image in repository (useless for analysis)
- Skip histogram stretch on preview (stars won't be visible)
- Use preview image for Plate Solving (not enough precision!)

---

## Troubleshooting

### Problem: Preview image is too dark
**Solution:** Enable auto stretch:
```csharp
var preview = capturedImage.CreatePreviewImage(autoStretch: true);
```

### Problem: Preview image is too large (still eating RAM)
**Solution:** Reduce max resolution:
```csharp
var preview = capturedImage.CreatePreviewImage(maxWidth: 640, maxHeight: 480);
```

### Problem: Stars not visible in preview
**Solution:** Auto stretch uses 2nd-98th percentile - if still not visible, check if image has stars at all:
```csharp
Debug.WriteLine($"Min: {capturedImage.MinPixelValue}, Max: {capturedImage.MaxPixelValue}");
```

### Problem: Out of memory with many calibration points
**Solution:** Repository auto-limits to 1000 images. If still an issue:
```csharp
// Clear old images
_imageRepository.Clear();

// Or limit max images:
// (edit CapturedImageRepository._maxImages)
```

---

## Summary

| Feature | Full Image | Preview Image |
|---------|-----------|--------------|
| **Storage** | `CapturedImageRepository` | Memory only (not stored) |
| **Resolution** | 11656×8750 | 800×600 |
| **Bit Depth** | 16-bit | 8-bit |
| **Size** | ~200 MB | ~0.5 MB |
| **Use Case** | Plate Solving, Analysis | UI Display |
| **Quality** | Full precision | Good enough to see |
| **Processing** | None | Downscale + Stretch + Convert |

**Memory Savings: 400x smaller!** ??

---

Created: 2025-01-20  
Version: 1.0
