# Velocity Command Architecture Policy

## ?? Overview

This document defines the **mandatory architecture** for all velocity command operations in the ForkMount WPF system.

---

## ?? Core Principles

### 1. **Single Source of Truth**
All velocity commands **MUST** be written to:
- `AzimuthInfo.Command_Velocity_Deg_Sec` (for Azimuth axis)
- `AltitudeInfo.Command_Velocity_Deg_Sec` (for Altitude axis)

### 2. **Centralized Command Sender**
Only `CommandSenderLoop` (running at 200Hz) is allowed to:
- Read `Command_Velocity_Deg_Sec`
- Send `JP` (Jog Position/Velocity) commands to Drive
- Send `BG` (Begin) commands to Drive

### 3. **Thread Safety**
All reads/writes to `Command_Velocity_Deg_Sec` **MUST** use locks:
```csharp
lock (_infoLock)
{
    _azimuthInfo.Command_Velocity_Deg_Sec = velocityDegSec;
}
```

---

## ?? Architecture Flow

```
???????????????????????????????????????????????????????????????
?                    VELOCITY SOURCES                          ?
?  (Satellite Tracking / PID Control / Manual Control)        ?
???????????????????????????????????????????????????????????????
                 ?
                 ? Write (with lock!)
                 ?
???????????????????????????????????????????????????????????????
?            AzimuthInfo / AltitudeInfo                        ?
?         Command_Velocity_Deg_Sec                             ?
?              (Thread-Safe Storage)                           ?
???????????????????????????????????????????????????????????????
                 ?
                 ? Read (with lock!)
                 ?
???????????????????????????????????????????????????????????????
?              CommandSenderLoop (200Hz)                       ?
?                                                              ?
?  1. Read Command_Velocity_Deg_Sec                           ?
?  2. Apply Position Limits & Soft Landing                    ?
?  3. Convert deg/s ? counts/s                                ?
?  4. Send: JP[axis] + BG[axis]                               ?
???????????????????????????????????????????????????????????????
                 ?
                 ?
???????????????????????????????????????????????????????????????
?                   Platinum Drive                             ?
?                  (Hardware Motor)                            ?
???????????????????????????????????????????????????????????????
```

---

## ? Correct Implementation Examples

### Example 1: Satellite Tracking
```csharp
// ? CORRECT - Use SetCommandVelocity() method
private void VelocityTrackingTimer_Tick(object sender, EventArgs e)
{
    double satelliteVelocity = GetSatelliteVelocity();
    _altitudeDrive.SetCommandVelocity(satelliteVelocity);
}
```

### Example 2: Manual Control
```csharp
// ? CORRECT - Use SetCommandVelocity() method
private void OnJoystickMoved(double velocityDegSec)
{
    _azimuthDrive.SetCommandVelocity(velocityDegSec);
}
```

### Example 3: PID Position Control
```csharp
// ? CORRECT - PID outputs velocity command
private void PositionControlLoop()
{
    double velocityCommand = _pidController.Calculate(currentPos, targetPos);
    
    lock (_infoLock)
    {
        _azimuthInfo.Command_Velocity_Deg_Sec = velocityCommand;
    }
}
```

---

## ? Incorrect Implementation Examples

### ? WRONG - Direct JP Command
```csharp
// ? WRONG - Do NOT send JP directly!
string jogCmd = $"AX{AZIMUTH_AXIS_NUMBER}.JP={velocityCounts}";
_platinumDrive.SendRawCommand(jogCmd, out response, 100);
```

### ? WRONG - No Lock
```csharp
// ? WRONG - Must use lock!
_azimuthInfo.Command_Velocity_Deg_Sec = velocityDegSec;
```

### ? WRONG - Direct Property Access from External Class
```csharp
// ? WRONG - External class should NOT access _altitudeInfo directly!
_altitudeInfo.Command_Velocity_Deg_Sec = satelliteVelocity;

// ? CORRECT - Use AltitudeDrive method instead
_altitudeDrive.SetCommandVelocity(satelliteVelocity);
```

---

## ?? API Reference

### AzimuthDrive / AltitudeDrive

#### `SetCommandVelocity(double velocityDegSec)`
**Purpose:** Thread-safe method to set velocity command  
**Used by:** Satellite Tracking, Manual Control, External Controllers  
**Thread-Safe:** ? Yes (uses internal lock)  

**Example:**
```csharp
_azimuthDrive.SetCommandVelocity(5.0); // Set 5 deg/s CW
_altitudeDrive.SetCommandVelocity(-2.5); // Set 2.5 deg/s DOWN
```

#### `StopMotion()`
**Purpose:** Immediately stop axis motion  
**Implementation:** Sets `Command_Velocity_Deg_Sec = 0` + sends `ST` command  
**Thread-Safe:** ? Yes (uses internal lock)  

**Example:**
```csharp
_azimuthDrive.StopMotion(); // Emergency stop
```

---

## ?? CommandSenderLoop Details

### Operation Frequency
- **200Hz** (5ms interval)
- Runs asynchronously on background task
- Non-blocking for UI thread

### Responsibilities
1. **Read** `Command_Velocity_Deg_Sec` (thread-safe)
2. **Check** Drive enabled status
3. **Apply** position limits & soft landing
4. **Convert** velocity (deg/s ? counts/s)
5. **Send** `JP` + `BG` commands to Drive

### Position Limit Protection
- **Soft Landing Zone:** Dynamic deceleration based on current velocity
- **Physics Formula:** `Stopping Distance = v² / (2a)`
- **Safety Factor:** 2.5x for system delays and inertia
- **Min Zone:** 1.0° 
- **Max Zone:** 30.0°

---

## ??? Thread Safety Guarantees

### Lock Object
```csharp
private readonly object _infoLock = new object();
```

### Protected Operations
- ? Read `Command_Velocity_Deg_Sec`
- ? Write `Command_Velocity_Deg_Sec`
- ? Read `Raw_Position`, `Raw_Velocity`
- ? Read `Is_Drive_Enabled`

### Lock Scope
- **Minimum:** Only protect data access, not I/O operations
- **No Deadlocks:** Locks are acquired in consistent order
- **Performance:** Locks are held for microseconds only

---

## ?? Benefits of This Architecture

### 1. **Thread Safety**
- No race conditions
- No torn reads/writes
- No data corruption

### 2. **Simplicity**
- Single responsibility per component
- Clear separation of concerns
- Easy to understand and maintain

### 3. **Reliability**
- Centralized command queue
- No duplicate commands
- Consistent command rate (200Hz)

### 4. **Flexibility**
- Easy to add new velocity sources
- Position limits enforced centrally
- Soft landing applied automatically

### 5. **Debugging**
- Single point to monitor commands
- Easy to log velocity changes
- Clear data flow

---

## ?? Implementation Checklist

When implementing a new velocity command source:

- [ ] Use `SetCommandVelocity()` method or lock when writing
- [ ] Do NOT send JV/BG commands directly
- [ ] Do NOT bypass the CommandSenderLoop
- [ ] Test with Drive enabled and disabled
- [ ] Test near position limits
- [ ] Verify thread safety with concurrent operations
- [ ] Add appropriate logging

---

## ?? Troubleshooting

### Problem: Axis not moving
**Check:**
1. Is Drive enabled? (`Is_Drive_Enabled = true`)
2. Is `Command_Velocity_Deg_Sec` being updated?
3. Is CommandSenderLoop running? (`IsPolling = true`)
4. Are position limits blocking motion?

### Problem: Jerky motion
**Check:**
1. Command rate: Should be stable 200Hz
2. Velocity changes: Should be smooth (use filtering if needed)
3. Position feedback: Check encoder quality

### Problem: Motion beyond limits
**Check:**
1. Limit values in `AppSettings.json`
2. Soft landing zone calculation
3. Drive deceleration parameters (AC, DC)

---

## ?? Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2024-01-XX | Initial architecture established |
| 1.1 | 2024-01-XX | Added thread-safe `SetCommandVelocity()` method |
| 1.2 | 2024-01-XX | Added position limit protection |
| 1.3 | 2024-01-XX | Added soft landing with dynamic zones |

---

## ?? Contact

For questions about this architecture, contact the development team or refer to:
- `AzimuthDrive.cs` (Line 1-50 for policy documentation)
- `AltitudeDrive.cs` (Line 1-40 for policy documentation)
- `CommandSenderLoop()` method implementation

---

**Last Updated:** 2024-01-XX  
**Document Owner:** ForkMount Development Team
