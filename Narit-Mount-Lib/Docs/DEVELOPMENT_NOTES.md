# ForkMount-WPF Development Notes

## Critical Information for AI Assistants

### System Overview
- **Project**: ForkMount-WPF (Telescope Mount Controller)
- **Drive**: Elmo Platinum 4-Axis Drive
- **Serial Number**: 25262718
- **COM Port**: COM10
- **Framework**: .NET 10, WPF, C# 14.0

---

## [CRITICAL SAFETY POLICY] NEVER Auto-Enable Axes!

### Rule:
**NEVER automatically enable motor axes (MO[x]=1) during initialization!**

### Reasons:
1. **Safety Risk** - Motor could move unexpectedly if system not ready
2. **Hardware Risk** - Could damage mount if not properly configured
3. **User Control** - User must explicitly enable when ready
4. **Debugging** - Can read axis status (MO[x], PX[x], VX[x]) WITHOUT enabling

### What You CAN Do:
```csharp
// [OK] Read axis status (even when disabled)
drive.SendRawCommand("MO[2]", out response, 1000);  // Read enable status
drive.SendRawCommand("PX[2]", out response, 1000);  // Read position (if configured)
drive.SendRawCommand("VX[2]", out response, 1000);  // Read velocity (if configured)
```

### What You MUST NOT Do:
```csharp
// [DANGER] Auto-enable axis during initialization
drive.SendRawCommand("MO[2]=1", out response, 2000);  // NEVER DO THIS!
```

### User Must Enable Manually:
- User clicks "Enable Drive" button in UI
- User has verified system is safe to enable
- User is ready to operate the mount

---

## MOST IMPORTANT - Command Format

### [CORRECT] Format for ForkMount 4-Axis Drive

```
AXn.command
```

**Examples:**
```csharp
"AX1.PX"       // Read Position from Axis 1 (Azimuth)
"AX2.PX"       // Read Position from Axis 2 (Altitude)
"AX2.VX"       // Read Velocity from Axis 2
"AX1.IQ"       // Read Current from Axis 1
"AX1.MO"       // Read Motor On status from Axis 1
"AX1.MO=1"     // Enable Motor on Axis 1
"AX1.ST"       // Stop Axis 1
"AX1.JV=1000"  // Set Jog Velocity on Axis 1
"AX1.BG"       // Begin motion on Axis 1
```

### [WRONG] Formats (will cause "Drive error" or "?;" response)

```csharp
"PX[1]"                    // [WRONG] Bracket notation
"[1]PX"                    // [WRONG] Axis before command
"##AXIS_SendMain 1 PX"     // [WRONG] Not supported
"PX1"                      // [WRONG] No separator
"PX"                       // [WRONG] Missing axis
```

### [EXCEPTION] Drive-Level Commands (NO axis prefix)

```csharp
"SN[4]"       // Serial Number (drive-level)
"VR[2]"       // Software Version
```

### [SPECIAL] Parameter Commands (needs testing)

```csharp
// May need axis prefix:
"AX1.KV[5]"   // Filter #1 Type for Axis 1
"AX1.KP[2]"   // Velocity Gain for Axis 1
"AX1.KI[2]"   // Velocity Integral for Axis 1

// Or may use bracket notation:
"KV[5][1]"    // Filter #1 Type for Axis 1 (needs verification)
"KP[2][1]"    // Velocity Gain for Axis 1 (needs verification)
```

**Note:** Parameter commands need to be tested in ElmoDriveTest to confirm correct format.

---

## Architecture

### Multi-Axis System Structure

```
PlatinumDrive (Base Communication Layer)
    |-- Axis 1: Azimuth (AzimuthDrive wrapper)
    |-- Axis 2: Altitude (AltitudeDrive wrapper)
    |-- Axis 3: Fork1 (Future)
    ?-- Axis 4: Fork2 (Future)
```

### Encoder Configuration (32-bit for ALL axes)
- **Azimuth (Axis 1)**: 4,294,967,296 counts/revolution (2^32)
- **Altitude (Axis 2)**: 4,294,967,296 counts/revolution (2^32)
- **Fork1 (Axis 3)**: 4,294,967,296 counts/revolution (2^32)
- **Fork2 (Axis 4)**: 4,294,967,296 counts/revolution (2^32)

### Conversion Formulas
```
1 degree = 11,930,465 counts (rounded)
1 degree = 4,294,967,296 / 360 counts (exact)

1 deg/sec = 11,930,465 counts/sec

Position (degrees) = (counts / 4,294,967,296) × 360
Velocity (deg/sec) = (counts/sec / 4,294,967,296) × 360

Counts = (degrees × 4,294,967,296) / 360
Counts/sec = (deg/sec × 4,294,967,296) / 360
```

### Key Classes

| Class | Purpose | File |
|-------|---------|------|
| `PlatinumDrive` | Base communication with drive | `Engine/PlatinumDrive.cs` |
| `AzimuthDrive` | Axis 1 wrapper + polling | `Engine/AzimuthDrive.cs` |
| `AltitudeDrive` | Axis 2 wrapper + polling | `Engine/AltitudeDrive.cs` |
| `AzimuthInfo` | Data container for Axis 1 | `Engine/AzimuthInfo.cs` |
| `AltitudeInfo` | Data container for Axis 2 | `Engine/AltitudeInfo.cs` |

---

## Polling System

### Configuration
- **Default Rate**: 1ms (1000 Hz)
- **Type**: Async, non-blocking
- **Method**: Commands sent async, responses received separately
- **No sync/wait**: Can send commands at 1ms intervals without blocking

### Polling Data
```csharp
// Read every 1ms (1000Hz) - async
- Position (PX[axis])
- Velocity (VX[axis])
- Current IQ (IQ[axis])
- Motor On Status (MO[axis])
```

---

## Common Commands Reference

### Motion Control
```csharp
"AX1.MO=1"           // Enable motor on Axis 1
"AX1.MO=0"           // Disable motor on Axis 1
"AX1.ST"             // Stop motion on Axis 1
"AX1.JV=11930465"    // Set jog velocity to 1 deg/sec (counts/sec)
"AX1.BG"             // Begin motion
```

### Reading Data
```csharp
"AX1.PX"       // Position (counts)
"AX1.VX"       // Velocity (counts/sec)
"AX1.IQ"       // Current (Amps)
"AX1.MO"       // Motor status (0/1)
"AX1.IP"       // Digital Input status
```

### Filter Parameters (for Control Parameters Window)

**[VERIFIED] Mapping from EAS II (2025-12-29)**

**Filter Type Values:**
- `0` = Disable
- `1` = Low-Pass
- `2` = Lead-Lag
- `3` = Double Lead-Lag
- `4` = Notch
- `5` = Anti-Notch
- `6` = General-Biquad
- `7` = Pole Zero

**Command Format for Multi-Axis:** Use `SendCommandToAxis("KV[index]", axisNumber, ...)`
This will send: `KV[index][axisNumber]` (e.g., `KV[5][1]` for Filter #1 Type on Axis 1)

```csharp
// Velocity Loop Filters
"KI[2]"       // Velocity Integral

// Filter #1 (Lead-Lag or Low-Pass)
"KV[5]"       // Filter #1 Type (0-7)
"KV[1]"       // Filter #1 Frequency [Hz]
"KV[2]"       // Filter #1 Damping

// Filter #2 (Notch)
"KV[10]"      // Filter #2 Type (0-7)
"KV[6]"       // Filter #2 Frequency [Hz]
"KV[7]"       // Filter #2 Q Factor
"KV[8]"       // Filter #2 Magnitude [dB]

// Filter #3 (Notch)
"KV[15]"      // Filter #3 Type (0-7)
"KV[11]"      // Filter #3 Frequency [Hz]
"KV[12]"      // Filter #3 Q Factor
"KV[13]"      // Filter #3 Magnitude [dB]

// Filter #4 (Notch)
"KV[20]"      // Filter #4 Type (0-7)
"KV[16]"      // Filter #4 Frequency [Hz]
"KV[17]"      // Filter #4 Q Factor
"KV[18]"      // Filter #4 Magnitude [dB]

// Feedback Filter #1 (Notch)
"KV[55]"      // Feedback Filter #1 Type (0-7)
"KV[51]"      // Feedback Filter #1 Frequency [Hz]
"KV[52]"      // Feedback Filter #1 Q Factor
"KV[53]"      // Feedback Filter #1 Magnitude [dB]

// Feedback Filter #2 (Notch)
"KV[60]"      // Feedback Filter #2 Type (0-7)
"KV[56]"      // Feedback Filter #2 Frequency [Hz]
"KV[57]"      // Feedback Filter #2 Q Factor
"KV[58]"      // Feedback Filter #2 Magnitude [dB]
```

**Example Usage:**
```csharp
// Read Filter #1 Type from Axis 1 (Azimuth)
string response;
platinumDrive.SendCommandToAxis("KV[5]", 1, out response);
// This sends: KV[5][1] to the drive

// Set Filter #1 Frequency to 40 Hz on Axis 1
platinumDrive.SendCommandToAxis("KV[1]=40", 1, out response);
// This sends: KV[1][1]=40 to the drive
```

**[IMPORTANT] Notes:**
- **KV[1]** is NOT Velocity Gain - it's Filter #1 Frequency!
- Velocity Gain is stored separately (use AppSettings)
- All filter parameters use `KV[index][axis]` format
- Filter Type must be set before configuring frequency/Q/magnitude

---

## Troubleshooting

### Error: "Drive error 2: Unknown Drive Error"

**Cause:** Wrong command format

**Solution:**
1. [CORRECT] Use: `command[axis]` format
2. [WRONG] Don't use: `[axis]command`
3. Check axis number is 1-4
4. Verify it's not a drive-level command

### Error: "Connected to SN: Unknown"

**Cause:** Can't read Serial Number after connect

**Solution:**
1. Check `ReadSerialNumber()` uses `SN[4]` (no axis)
2. Add 500ms delay after Connect if needed
3. Verify connection is successful first

### Polling Errors (10+ errors)

**Cause:** Commands failing repeatedly

**Solution:**
1. Check command format
2. Verify drive is connected
3. Check COM port is correct
4. Ensure drive is powered on

---

## Code Examples

### Connecting to Drive
```csharp
var azimuthDrive = new AzimuthDrive();
bool success = azimuthDrive.ConnectBySerialNumber("25262718");
if (success)
{
    azimuthDrive.StartPolling(); // Start 1000Hz polling
}
```

### Reading Data
```csharp
double position = azimuthDrive.ReadPosition();     // Axis 1
double velocity = azimuthDrive.ReadVelocity();     // Axis 1
double current = azimuthDrive.ReadCurrentIQ();     // Axis 1
bool enabled = azimuthDrive.ReadDriveEnabled();    // Axis 1
```

### Sending Commands
```csharp
// Enable motor
string response;
bool success = platinumDrive.SendCommandToAxis("MO=1", 1, out response);

// Set velocity and move
platinumDrive.SendCommandToAxis("JV=1000", 1, out response);
platinumDrive.SendCommandToAxis("BG", 1, out response);
```

---

## For AI Assistants: Quick Checklist

When working on this project:

- [ ] [CORRECT] Use `command[axis]` format (e.g., `PX[1]`)
- [ ] [WRONG] Don't use `[axis]command` format
- [ ] Check axis number is 1-4
- [ ] Drive-level commands (like `SN[4]`) don't need axis
- [ ] Polling at 1ms is OK (async, non-blocking)
- [ ] Serial Number: 25262718
- [ ] COM Port: COM10
- [ ] `PlatinumDrive` = base layer for all axes
- [ ] `AzimuthDrive` = wrapper for Axis 1 only
- [ ] `AltitudeDrive` = wrapper for Axis 2 only

---

## Important Files to Read First

1. **`Engine/PlatinumDrive.cs`** - Base communication layer (READ THIS FIRST!)
2. **`Engine/AzimuthDrive.cs`** - Axis 1 wrapper
3. **`Engine/AltitudeDrive.cs`** - Axis 2 wrapper
4. **`UI/Azimuth_Controller.xaml.cs`** - Main UI controller

---

## Related Projects

- **IMount-WPF**: Single-axis drive version (different command format!)
- **ForkMount-WPF**: This project (4-axis drive)

**[IMPORTANT]** DO NOT mix command formats between IMount and ForkMount!

---

## Last Updated

- **Date**: 2025-12-29
- **Status**: [WORKING] Commands use `command[axis]` format
- **Issues**: None - All polling and commands working at 1000Hz

---

## Tips for Future Development

1. **Always check Debug Output** for command/response logs
2. **Test commands** with timeout of 500-2000ms
3. **Format**: `command[axis]` is the ONLY correct way
4. **Async polling** at 1ms is safe and fast
5. **Read this file** before making changes!

---

*This file is maintained for AI assistants to quickly understand the system without re-explanation.*
