# Command Format Fix - 2025-01-XX

## Problem Discovered

From testing with ElmoDriveTest and EAS II Terminal, we found that:
- Drive **DOES work** with format: `AX1.PX`, `AX2.VX`, `AX2.MO`
- Drive **DOES NOT work** with format: `PX[1]`, `VX[2]`, `MO[2]`

Response when using wrong format: `?;` or `Drive error 244: Unknown Drive Error`

## Root Cause

The Elmo Platinum 4-Axis Drive uses **dot notation** for axis selection:
- Correct: `AXn.command` where n = axis number (1-4)
- Wrong: `command[n]` (this was what ForkMount-WPF was using)

## Test Results from ElmoDriveTest

```
Drive01> AX1.PX
AX1.PX=3.620711e+09          ? Works!

Drive01> AX2.PX  
AX2.PX=3.280522e+09          ? Works!

Drive01> AX2.VX
AX2.VX=6.056267e+03          ? Works!

Drive01> PX[2]
Error: Drive error 244       ? Failed!
```

## Files Changed

### 1. `ForkMount-WPF/Engine/PlatinumDrive.cs`
Changed all command generation from `command[n]` to `AXn.command`:

**Before:**
```csharp
string platinumCommand = $"{command}[{_axisNumber}]";  // PX[1]
string command = $"PX[{targetAxis}]";                  // PX[2]
string command = $"MO[{targetAxis}]={(enable ? "1" : "0")}";  // MO[1]=1
```

**After:**
```csharp
string platinumCommand = $"AX{_axisNumber}.{command}";  // AX1.PX
string command = $"AX{targetAxis}.PX";                   // AX2.PX
string command = $"AX{targetAxis}.MO={(enable ? "1" : "0")}";  // AX1.MO=1
```

**Methods Updated:**
- `SendCommand()` - Auto-formatting with axis
- `ReadPosition()` / `ReadPositionAsync()`
- `ReadVelocity()` / `ReadVelocityAsync()`
- `ReadCurrentIQ()` / `ReadCurrentIQAsync()`
- `ReadDriveEnabled()` / `ReadDriveEnabledAsync()`
- `SetDriveEnable()`
- `StopMotion()`

### 2. `ForkMount-WPF/Docs/DEVELOPMENT_NOTES.md`
Updated command format documentation with correct examples.

### 3. `ForkMount-WPF/Docs/AI_QUICK_REF.md`
Updated quick reference to show `AXn.command` format.

## Expected Results

After this fix:
1. **Axis 2 (Altitude) should now respond** - Previously returned `?;`
2. **Concurrent polling for both axes** - Will work correctly
3. **All read commands work** - PX, VX, IQ, MO

## Known Issues / To Be Tested

### Parameter Commands Format
Need to verify format for parameter commands in ElmoDriveTest:

**Option 1: Dot notation**
```
AX1.KV[5]      # Filter #1 Type for Axis 1
AX1.KP[2]      # Velocity Gain for Axis 1
```

**Option 2: Double bracket**
```
KV[5][1]       # Filter #1 Type for Axis 1
KP[2][1]       # Velocity Gain for Axis 1
```

**File needs update:** `Azimuth_Control_Parameters_Window.Saving.cs` (lines 68-100)
- Currently uses: `KP[2]=value`, `KI[2]=value`
- May need: `AX1.KP[2]=value` or `KP[2][1]=value`

## Testing Checklist

- [x] Build successful (0 errors, 0 warnings)
- [ ] Test Axis 1 reads (PX, VX, IQ, MO)
- [ ] Test Axis 2 reads (PX, VX, IQ, MO)
- [ ] Test Axis 1 Enable/Disable (MO=1/0)
- [ ] Test Axis 2 Enable/Disable (MO=1/0)
- [ ] Test concurrent polling (both axes)
- [ ] Test parameter writes (KV, KP, KI)
- [ ] Test motion commands (JV, BG, ST)

## References

- ElmoDriveTest Terminal tests: See Terminal screenshot
- EAS II Terminal behavior: Uses `AX2>` context switching, then `px` (no prefix needed)
- Actual Drive commands: Use `AXn.command` format directly

## Notes

- EAS II Terminal uses **context switching** (`AX2>` then `px`)
- ForkMount-WPF uses **direct addressing** (`AX2.PX` every time)
- Both methods work, but we use direct addressing for simplicity
- No context state to maintain between commands

---

**Date:** 2025-01-XX
**Issue:** Axis 2 not responding (returned `?;`)
**Fix:** Changed command format from `command[n]` to `AXn.command`
**Status:** ? Fixed - Build successful, awaiting hardware test
