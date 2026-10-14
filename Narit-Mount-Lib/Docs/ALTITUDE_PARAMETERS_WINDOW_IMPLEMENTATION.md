# Altitude Control Parameters Window - Implementation Summary

## Overview
Completed the Altitude Control Parameters Window for tuning Position Loop and Elmo Internal Loop parameters for Altitude Axis (Axis 2).

## Files Modified/Created

### New Files:
1. **`ForkMount-WPF/UI/ParametersWindow/Altitude_Control_Parameters_Window.SaveFlash.cs`**
   - Implements Save to Flash functionality for Altitude axis
   - Sends `AX2.SAVE` command to save parameters to Drive Flash memory
   - Proper queue management and button enable/disable logic

### Modified Files:
1. **`ForkMount-WPF/UI/ParametersWindow/Altitude_Control_Parameters_Window.xaml`**
   - Added "Save Flash" button in title bar (green button with save icon)
   - Button placement matches Azimuth window design
   - Professional styling with hover/press states

2. **`ForkMount-WPF/UI/ParametersWindow/Altitude_Control_Parameters_Window.cs`**
   - Fixed `LoadPDControllerFromAppSettings()` to load motion parameters from `AltitudeAxisConfig`
   - Corrected property access patterns

3. **`ForkMount-WPF/UI/ParametersWindow/Altitude_Control_Parameters_Window.PDController.cs`**
   - Fixed `SavePDControllerToAppSettings()` to save motion parameters to `AltitudeAxisConfig`
   - Added motion parameter logging
   - Corrected offset saving to use `TuningOffsets` class

4. **`ForkMount-WPF/UI/ParametersWindow/Altitude_Control_Parameters_Window.TuningParameters.cs`**
   - Already implemented (from previous session)
   - Handles Elmo internal loop tuning parameters for Axis 2

## Features Implemented

### PD Controller Section (External Loop)
Position control parameters that run in the WPF application:
- **Kp** - Proportional Gain
- **Ki** - Integral Gain
- **Kd** - Derivative Gain
- **Deadband** - Position tolerance (degrees)
- **Acceleration** - Normal acceleration (deg/s²)
- **Deceleration** - Normal deceleration (deg/s²)
- **Stop Deceleration** - Emergency stop deceleration (deg/s²)

### Elmo Internal Loop Section (Drive Parameters)
Internal drive parameters sent to Elmo controller:
- **Velocity Gain** (KP[2]) - Velocity loop proportional gain
- **Velocity Integral** (KI[2]) - Velocity loop integral gain
- **Position Gain** (KP[3]) - Position loop gain
- **Acc Feed Forward** (FF[1]) - Acceleration feedforward
- **Vel Feed Forward** (FF[2]) - Velocity feedforward

### Tuning Interface
Each parameter includes:
- **Main Value TextBox** - Current parameter value
- **Up/Down Buttons** - Increment/decrement by offset amount
- **Offset TextBox** - Step size for up/down buttons
- **Auto-Save** - Parameters save to AppSettings on focus loss
- **Real-time Application** - Parameters sent to drive immediately

### Save Flash Button
- **Green button** with download icon in title bar
- **Saves all parameters** to Elmo drive Flash memory
- **Command:** `AX2.SAVE` (for Altitude = Axis 2)
- **Queue-based** - Uses CentralizedPollingManager
- **Visual feedback** - Button disables during save, re-enables when complete
- **Logging** - All operations logged to System Log panel

## Technical Details

### Command Format
All Elmo commands use Axis 2 prefix:
```
AX2.KP[2]=value    # Velocity Gain
AX2.KI[2]=value    # Velocity Integral
AX2.KP[3]=value    # Position Gain
AX2.FF[1]=value    # Acc Feed Forward
AX2.FF[2]=value    # Vel Feed Forward
AX2.SAVE           # Save to Flash
```

### Unit Conversions
**Velocity Gain conversion:**
- UI units: A/deg/sec (Amperes per degree per second)
- Drive units: A/(cnt/sec) (Amperes per count per second)
- Conversion factor: `360.0 / 4294967296.0` (for 32-bit encoder)
- Formula: `Drive_Value = UI_Value / CONVERSION_FACTOR`

### Data Storage
**PD Controller Parameters:**
- Kp, Ki, Kd, Deadband: Stored in `AppSettings.PD_Controller`
- Motion Parameters: Stored in `AppSettings.Altitude_AxisConfig`

**Elmo Internal Loop Parameters:**
- All tuning parameters: Stored in `AppSettings.Altitude_Tuning_Parameters`

**Offsets (Step Sizes):**
- All offset values: Stored in `AppSettings.Tuning_Offsets`

### Command Queue
- All drive commands use `CentralizedPollingManager.EnqueueExtraCommand()`
- Non-blocking async execution
- Proper logging of success/failure
- Queue size monitoring

## UI Design
Matches the Azimuth Control Parameters Window:
- **Modern dark theme** (#1E1E1E background)
- **Rounded corners** (CornerRadius=6 for inputs)
- **Professional styling** with hover states
- **Consistent spacing** and alignment
- **Clear section headers** with separators
- **System Log panel** at bottom for debugging

## Testing Status
- **Build:** SUCCESS (0 errors, 0 warnings)
- **Code Review:** COMPLETE
- **UI Layout:** COMPLETE
- **Save Flash Button:** Implemented and tested (code-level)
- **Ready for:** Hardware testing with actual Elmo drive

## Usage Instructions

1. **Open Window:**
   - Click "Altitude Parameters" button in main UI
   
2. **Adjust PD Controller:**
   - Modify Kp, Ki, Kd for position loop tuning
   - Adjust Deadband for setpoint tolerance
   - Set Acceleration/Deceleration for motion profile
   
3. **Tune Elmo Internal Loop:**
   - Adjust Velocity Gain (KP[2]) for velocity tracking
   - Tune Velocity Integral (KI[2]) for steady-state error
   - Set Position Gain (KP[3]) for position loop
   - Configure feedforward terms (FF[1], FF[2])
   
4. **Save to Flash:**
   - Click green "Save Flash" button in title bar
   - Watch System Log for confirmation
   - Parameters persist across power cycles

## Notes
- All parameters automatically save to AppSettings JSON file
- Flash save is optional but recommended for persistence
- Use up/down buttons for fine-tuning during operation
- System Log shows all parameter changes and command results
- Window follows NO EMOJI POLICY (uses text markers)

## Next Steps
- Hardware testing with Altitude axis
- Verify parameter ranges for safety
- Test Save Flash functionality with actual drive
- Tune default values based on mechanical system

---
**Date:** 2026-01-20  
**Status:** COMPLETE  
**Build:** SUCCESS (0 errors, 0 warnings)
