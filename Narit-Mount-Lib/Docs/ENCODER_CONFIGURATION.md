# Encoder Configuration - ForkMount Platinum Drive

## [CRITICAL] Encoder Resolution Policy

**ALL AXES use 32-bit Encoder Resolution**

---

## Encoder Specifications

### Resolution (All Axes)
```
Encoder Resolution = 4,294,967,296 counts/revolution
Binary: 2^32 counts
Hex: 0x100000000 counts
```

**Applies to:**
- Axis 1: Azimuth
- Axis 2: Altitude
- Axis 3: Fork1
- Axis 4: Fork2

---

## Conversion Tables

### Position Conversion

| Degrees | Counts (exact) | Counts (rounded) |
|---------|----------------|------------------|
| 1° | 11,930,464.71 | 11,930,465 |
| 10° | 119,304,647.11 | 119,304,647 |
| 45° | 536,870,912 | 536,870,912 |
| 90° | 1,073,741,824 | 1,073,741,824 |
| 180° | 2,147,483,648 | 2,147,483,648 |
| 270° | 3,221,225,472 | 3,221,225,472 |
| 360° | 4,294,967,296 | 4,294,967,296 |

### Velocity Conversion

| Degrees/sec | Counts/sec (exact) | Counts/sec (rounded) |
|-------------|-------------------|----------------------|
| 0.001 deg/sec | 11,930.46 | 11,930 |
| 0.01 deg/sec | 119,304.65 | 119,305 |
| 0.1 deg/sec | 1,193,046.47 | 1,193,046 |
| 1 deg/sec | 11,930,464.71 | 11,930,465 |
| 10 deg/sec | 119,304,647.11 | 119,304,647 |
| 60 deg/sec | 715,827,882.67 | 715,827,883 |
| 100 deg/sec | 1,193,046,471.11 | 1,193,046,471 |

**Note:** 60 deg/sec = 10 RPM (10 revolutions per minute)

---

## Formulas

### Position (Degrees to Counts)
```
Counts = (Degrees × 4,294,967,296) / 360
Counts = Degrees × 11,930,464.71
```

### Position (Counts to Degrees)
```
Degrees = (Counts / 4,294,967,296) × 360
Degrees = Counts / 11,930,464.71
```

### Velocity (Deg/sec to Counts/sec)
```
Counts/sec = (Deg/sec × 4,294,967,296) / 360
Counts/sec = Deg/sec × 11,930,464.71
```

### Velocity (Counts/sec to Deg/sec)
```
Deg/sec = (Counts/sec / 4,294,967,296) × 360
Deg/sec = Counts/sec / 11,930,464.71
```

---

## Code Examples

### C# Implementation
```csharp
// Constants
public const long ENCODER_RESOLUTION = 4294967296; // 2^32
public const double COUNTS_PER_DEGREE = 11930464.71;

// Position Conversion
public static double CountsToDegrees(double counts)
{
    return (counts / ENCODER_RESOLUTION) * 360.0;
}

public static double DegreesToCounts(double degrees)
{
    return (degrees * ENCODER_RESOLUTION) / 360.0;
}

// Velocity Conversion
public static double CountsPerSecToDegreesPerSec(double countsPerSec)
{
    return (countsPerSec / ENCODER_RESOLUTION) * 360.0;
}

public static double DegreesPerSecToCountsPerSec(double degreesPerSec)
{
    return (degreesPerSec * ENCODER_RESOLUTION) / 360.0;
}
```

---

## Elmo Drive Commands

### Reading Position
```csharp
// Read position in counts
drive.SendRawCommand("AX1.PX", out response, 500);
// Response: e.g., "1073741824" = 90 degrees

// Convert to degrees
double counts = double.Parse(response);
double degrees = (counts / 4294967296.0) * 360.0; // = 90.0
```

### Setting Velocity
```csharp
// Set velocity to 1 deg/sec
double degreesPerSec = 1.0;
long countsPerSec = (long)((degreesPerSec * 4294967296.0) / 360.0);
// countsPerSec = 11,930,465

drive.SendRawCommand($"AX1.JV={countsPerSec}", out response, 1000);
drive.SendRawCommand("AX1.BG", out response, 1000); // Begin motion
```

---

## Testing with ElmoDriveTest

### Test Position Reading
```csharp
// In ElmoDriveTest/Program.cs
string response;
comm.SendCommandAnalyzeError("AX1.PX", out response, out error, 500);
Console.WriteLine($"Position (counts): {response}");

double counts = double.Parse(response);
double degrees = (counts / 4294967296.0) * 360.0;
Console.WriteLine($"Position (degrees): {degrees:F2}");
```

### Test Velocity Command
```csharp
// Set velocity to 1 deg/sec
long countsPerSec = 11930465;
comm.SendCommandAnalyzeError($"JV[1]={countsPerSec}", out response, out error, 1000);
comm.SendCommandAnalyzeError("BG[1]", out response, out error, 1000);

// Read back velocity
comm.SendCommandAnalyzeError("AX1.VX", out response, out error, 500);
Console.WriteLine($"Velocity (counts/sec): {response}");

double vel = double.Parse(response);
double degPerSec = (vel / 4294967296.0) * 360.0;
Console.WriteLine($"Velocity (deg/sec): {degPerSec:F4}");
```

---

## Quick Reference Card

**For Testing:**
```
1 degree     = 11,930,465 counts
1 deg/sec    = 11,930,465 counts/sec
10 deg/sec   = 119,304,647 counts/sec
100 deg/sec  = 1,193,046,471 counts/sec
```

**Commands:**
```
AX1.PX              Read position (counts)
AX1.VX              Read velocity (counts/sec)
JV[1]=11930465      Set velocity to 1 deg/sec
BG[1]               Begin motion
AX1.ST              Stop motion
```

---

## Common Mistakes to Avoid

### [WRONG] Using old 20-bit resolution
```csharp
long encoderResolution = 1048576; // WRONG! This is 2^20, not 2^32
```

### [CORRECT] Using 32-bit resolution
```csharp
long encoderResolution = 4294967296; // CORRECT! 2^32
```

### [WRONG] Integer overflow
```csharp
int encoderResolution = 4294967296; // COMPILE ERROR! Exceeds int.MaxValue
```

### [CORRECT] Using long type
```csharp
long encoderResolution = 4294967296; // CORRECT! Uses long (64-bit)
```

---

## File Locations

### Configuration Files
- `ForkMount-WPF/Engine/AppSettingsModel.cs` - Contains `AzimuthAxisConfig` and `AltitudeAxisConfig`
- `ForkMount-WPF/Settings.json` - Stored settings (auto-generated)

### Code Files
- `ForkMount-WPF/Engine/PlatinumDrive.cs` - Conversion methods
- `ForkMount-WPF/Engine/AzimuthDrive.cs` - Axis 1 wrapper
- `ForkMount-WPF/Engine/AltitudeDrive.cs` - Axis 2 wrapper

---

**Last Updated:** 2025-12-29
**Applies to:** ForkMount-WPF v1.0-Platinum
**Drive:** Elmo Platinum 4-Axis, SN: 25262718
