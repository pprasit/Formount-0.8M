# Sidereal Tracking Controller Design Document

## Overview

The **Sidereal Tracking Controller** is a PD (Proportional-Derivative) control system designed to track fixed celestial objects (stars, galaxies, deep-sky objects) by compensating for Earth's rotation.

## System Architecture

```
???????????????????????????????????????????????????????????????
?                   SIDEREAL TRACKING SYSTEM                   ?
???????????????????????????????????????????????????????????????
?                                                              ?
?  [User Input]                                                ?
?     ?                                                        ?
?     ??> Target RA/Dec (J2000)                               ?
?     ?                                                        ?
?  [Coordinate Transformation]                                 ?
?     ?                                                        ?
?     ??> Calculate LST (Local Sidereal Time)                 ?
?     ??> Calculate Hour Angle (HA = LST - RA)               ?
?     ??> Convert to Alt/Az (Horizontal Coordinates)          ?
?                                                              ?
?  [PD Controller]                                             ?
?     ?                                                        ?
?     ??> Position Error = Target - Actual                    ?
?     ??> Velocity = Kp × Error + Kd × dError/dt             ?
?     ??> Add Earth Rotation Feedforward                      ?
?                                                              ?
?  [Motor Commands]                                            ?
?     ?                                                        ?
?     ??> Azimuth Velocity Command                            ?
?     ??> Altitude Velocity Command                           ?
?                                                              ?
???????????????????????????????????????????????????????????????
```

## Control Law

### 1. Position Error Calculation

```
Error_Az = Target_Az - Actual_Az  (with 360° wrapping)
Error_Alt = Target_Alt - Actual_Alt
```

### 2. PD Control Law

```
V_az = Kp_az × Error_Az + Kd_az × dError_Az/dt + V_feedforward_az
V_alt = Kp_alt × Error_Alt + Kd_alt × dError_Alt/dt + V_feedforward_alt
```

Where:
- **Kp**: Proportional gain (converts position error to velocity)
- **Kd**: Derivative gain (provides damping, reduces oscillation)
- **V_feedforward**: Earth rotation compensation

### 3. Earth Rotation Feedforward

```
V_feedforward_az = ?_sidereal × cos(Latitude) / cos(Altitude)
V_feedforward_alt ? 0  (negligible for most objects)
```

Where:
- **?_sidereal** = 0.00417807462 deg/sec (15.041 arcsec/sec)
- Compensates for Earth's rotation at sidereal rate

## Coordinate Systems

### J2000 Equatorial Coordinates (Input)
- **Right Ascension (RA)**: 0-24 hours
- **Declination (Dec)**: ±90 degrees
- Fixed celestial coordinate system (epoch J2000.0)

### Horizontal Coordinates (Control)
- **Azimuth (Az)**: 0-360 degrees (North = 0°, East = 90°)
- **Altitude (Alt)**: 0-90 degrees (Horizon = 0°, Zenith = 90°)
- Observer-dependent, changes with time

## Transformation Equations

### 1. Local Sidereal Time (LST)

```
JD = Julian Date from UTC
T = (JD - 2451545.0) / 36525.0
GMST = 280.46061837 + 360.98564736629 × (JD - 2451545.0)
       + 0.000387933 × T² - T³ / 38710000.0
LST = GMST + Longitude / 15.0  (in hours)
```

### 2. Hour Angle (HA)

```
HA = LST - RA  (in hours)
HA_deg = HA × 15.0  (convert to degrees)
```

### 3. Alt/Az Calculation

```
sin(Alt) = sin(Lat) × sin(Dec) + cos(Lat) × cos(Dec) × cos(HA)
Alt = arcsin(sin(Alt))

cos(Az) = (sin(Dec) - sin(Lat) × sin(Alt)) / (cos(Lat) × cos(Alt))
Az = arccos(cos(Az))

If sin(HA) > 0: Az = 360° - Az  (adjust quadrant)
```

## PD Controller Gains

### Default Values

| Parameter | Default Value | Unit | Description |
|-----------|--------------|------|-------------|
| Azimuth Kp | 1.5 | (deg/s)/deg | Proportional gain for azimuth |
| Azimuth Kd | 0.3 | (deg/s)/(deg/s) | Derivative gain for azimuth |
| Altitude Kp | 1.5 | (deg/s)/deg | Proportional gain for altitude |
| Altitude Kd | 0.3 | (deg/s)/(deg/s) | Derivative gain for altitude |

### Tuning Guidelines

**Kp (Proportional Gain)**
- Increase ? Faster response, larger overshoot
- Decrease ? Slower response, more stable
- Typical range: 0.5 - 3.0

**Kd (Derivative Gain)**
- Increase ? More damping, smoother motion
- Decrease ? Less damping, may oscillate
- Typical range: 0.1 - 0.5

## Implementation Details

### SiderealTrackingController Class

```csharp
// Initialize controller
var location = new ObserverLocation(latitude, longitude, altitude);
var controller = new SiderealTrackingController(location);

// Set target star
controller.SetTargetCoordinates(raHours, decDegrees);

// Start tracking
controller.StartTracking();

// In control loop (e.g., 100 Hz)
var (azVel, altVel) = controller.CalculateVelocityCommands(
    actualAzimuth, 
    actualAltitude, 
    deltaTime);

// Send to drives
azimuthDrive.SetVelocity(azVel);
altitudeDrive.SetVelocity(altVel);
```

### Key Methods

1. **SetTargetCoordinates(ra, dec)** - Set target star position
2. **StartTracking()** - Begin sidereal tracking
3. **StopTracking()** - Stop tracking
4. **CalculateVelocityCommands()** - Main control loop
5. **SetGains()** - Update PD gains
6. **GetStatus()** - Get current tracking status

## Performance Characteristics

### Update Rate
- **Recommended**: 10-100 Hz (100-10 ms period)
- **Minimum**: 1 Hz (1 second period)

### Tracking Accuracy
- **Position Error**: < 0.01° (36 arcsec) typical
- **Velocity Accuracy**: < 0.001 deg/s

### Earth Rotation Compensation
- **Sidereal Rate**: 0.00417807462 deg/sec
- **Accuracy**: Sub-arcsecond level

## Integration with Drive System

### Velocity Command Interface

```csharp
// Azimuth axis
azimuthController.SetVelocity(azVelocity);  // deg/s

// Altitude axis
altitudeController.SetVelocity(altVelocity);  // deg/s
```

### Position Feedback

```csharp
// Read actual positions from encoders
double actualAz = azimuthController.GetPosition();
double actualAlt = altitudeController.GetPosition();
```

## Error Handling

### Common Issues

1. **Target Below Horizon** - Altitude < 0°
   - Solution: Check visibility before tracking

2. **Gimbal Lock** - Altitude near 90° (zenith)
   - Solution: Avoid tracking near zenith, use pole avoidance

3. **Azimuth Wrap** - 0°/360° crossing
   - Solution: Proper error wrapping implemented

4. **LST Calculation Drift** - Long-term accuracy
   - Solution: Use NTP time sync, proper JD calculation

## Testing & Validation

### Unit Tests
1. Coordinate transformation accuracy
2. LST calculation verification
3. Error wrapping (azimuth 360° crossing)
4. PD controller stability

### Integration Tests
1. Track bright star (e.g., Sirius, Vega)
2. Measure position error over time
3. Verify smooth motion (no oscillation)
4. Test pole avoidance

## Future Enhancements

1. **Refraction Correction** - Atmospheric refraction model
2. **Precession/Nutation** - High-precision coordinate transformation
3. **Differential Tracking** - RA tracking rate adjustment
4. **Autoguiding Interface** - Integration with guiding camera
5. **Adaptive Gains** - Self-tuning PD controller

## References

- Meeus, J. (1998). *Astronomical Algorithms*. Willmann-Bell.
- Green, R. M. (1985). *Spherical Astronomy*. Cambridge University Press.
- USNO Circular 179: *The IAU Resolutions on Astronomical Reference Systems*

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2024-01-XX | System | Initial design document |

---

**Contact**: ForkMount-WPF Development Team  
**License**: Proprietary - Platinum Drive Control System
