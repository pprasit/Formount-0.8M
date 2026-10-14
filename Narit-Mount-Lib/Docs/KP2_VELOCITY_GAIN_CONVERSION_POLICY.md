# KP[2] Velocity Gain Conversion Policy

**CRITICAL POLICY - READ BEFORE MODIFYING ANY KP[2] RELATED CODE!**

---

## Overview

**KP[2]** = Velocity Loop Proportional Gain (Current Loop)

This document defines the **CORRECT** conversion between:
- **Drive units:** `A/(cnt/sec)` = Ampere per counts per second
- **UI units:** `A/(deg/sec)` = Ampere per degree per second

**Why this is critical:**
- Incorrect conversion causes wildly wrong values in UI
- Example: 0.1 A/(deg/sec) becomes 1.0E-9 if formula is wrong
- This bug has been fixed multiple times - DO NOT repeat the mistake!

---

## Encoder Specifications

**Encoder Type:** 32-bit Absolute Encoder  
**Resolution:** 4,294,967,296 counts/revolution  
**Conversion Factor:** 4,294,967,296 / 360 = **11,930,464.7111** counts/degree

**Key Insight:**
- 1 degree/second = 11,930,464.7111 counts/second
- Therefore, velocity in deg/sec requires **~11.9 million times MORE current** than velocity in cnt/sec
- This is why we **MULTIPLY** when reading from Drive!

---

## The Correct Formula

### Reading from Drive (Drive ? UI):

```csharp
// Drive sends: KP[2] in A/(cnt/sec)
// We need: KP[2] in A/(deg/sec)

double velocityGain_UI = kp2_drive * VELOCITY_GAIN_CONVERSION_FACTOR;

// Example:
// Drive: 8.38190E-9 A/(cnt/sec)
// UI: 8.38190E-9 × 11,930,464.7111 = 0.1 A/(deg/sec)
```

### Writing to Drive (UI ? Drive):

```csharp
// UI has: KP[2] in A/(deg/sec)
// Drive needs: KP[2] in A/(cnt/sec)

double kp2_drive = velocityGain_UI / VELOCITY_GAIN_CONVERSION_FACTOR;

// Example:
// UI: 0.1 A/(deg/sec)
// Drive: 0.1 / 11,930,464.7111 = 8.38190E-9 A/(cnt/sec)
```

---

## Why MULTIPLY when reading?

**Physics explanation:**

1. **Drive perspective:**
   - KP[2] = 8.38190E-9 means "give 8.38190E-9 Ampere for every 1 cnt/sec of velocity"
   - For 1 cnt/sec velocity ? Need 8.38190E-9 A

2. **Real-world perspective:**
   - 1 deg/sec = 11,930,464.7111 cnt/sec
   - For 1 deg/sec velocity ? Need 8.38190E-9 × 11,930,464.7111 = **0.1 A**

3. **Conclusion:**
   - Higher velocity (deg/sec vs cnt/sec) = Need more current
   - Therefore: **MULTIPLY** to get UI value

---

## Verification Example (from EASII)

**From EASII Software:**
- KP[2] = **8.38190E-9** (Drive units)

**Calculation:**
```
KP[2]_UI = 8.38190E-9 × 11,930,464.7111
        = 0.099999... 
        ? 0.1 A/(deg/sec)
```

**This matches the expected value of 0.1 A/(deg/sec)** ?

---

## Implementation Details

### Constant Definition:

```csharp
// For 32-bit encoder (4294967296 counts/revolution):
// VELOCITY_GAIN_CONVERSION_FACTOR = 4294967296 / 360 = 11930464.7111...
private double VELOCITY_GAIN_CONVERSION_FACTOR = 11930464.7111;
```

### Reading from Drive:

**Files:**
- `Azimuth_Control_Parameters_Window.Loading.cs`
- `Altitude_Control_Parameters_Window.Loading.cs`

```csharp
if (double.TryParse(response.Trim(), out double kp2_drive))
{
    double velocityGain_UI = kp2_drive * VELOCITY_GAIN_CONVERSION_FACTOR; // MULTIPLY!
    txtVelocityGain.Text = FormatToDecimal(velocityGain_UI.ToString());
    AddLog($"[READ] Velocity Gain (KP[2]) = {velocityGain_UI:F6} A/(deg/sec) (Drive: {kp2_drive:E6} A/(cnt/sec))");
}
```

### Writing to Drive:

**Files:**
- `Azimuth_Control_Parameters_Window.Saving.cs`
- `Altitude_Control_Parameters_Window.TuningParameters.cs`

```csharp
double velocityGain_UI = ParseDouble(txtVelocityGain.Text, 0);
double kp2_drive = velocityGain_UI / VELOCITY_GAIN_CONVERSION_FACTOR; // DIVIDE!
string kp2Command = $"AX{axis}.KP[2]={kp2_drive:E10}";
```

---

## Common Mistakes to AVOID

### [WRONG] Using DIVIDE when reading:
```csharp
// This gives WRONG values!
double velocityGain_UI = kp2_drive / VELOCITY_GAIN_CONVERSION_FACTOR; // WRONG!
// Result: 8.38190E-9 / 11,930,464.7111 = 7.03E-16 (WAY TOO SMALL!)
```

### [WRONG] Using MULTIPLY when writing:
```csharp
// This sends WRONG values to Drive!
double kp2_drive = velocityGain_UI * VELOCITY_GAIN_CONVERSION_FACTOR; // WRONG!
// Result: 0.1 × 11,930,464.7111 = 1,193,046 (WAY TOO BIG!)
```

---

## Summary Table

| Operation | Direction | Formula | Example |
|-----------|-----------|---------|---------|
| **Read** | Drive ? UI | `UI = Drive × Factor` | `0.1 = 8.38190E-9 × 11930464.7111` |
| **Write** | UI ? Drive | `Drive = UI / Factor` | `8.38190E-9 = 0.1 / 11930464.7111` |

**Mnemonic:**
- **Reading:** Values get **BIGGER** (multiply)
- **Writing:** Values get **SMALLER** (divide)

---

## Testing Checklist

Before deploying changes to KP[2] code:

- [ ] Read value from Drive (via EASII) and verify it matches UI display
- [ ] Example: Drive shows `8.38190E-9` ? UI should show `0.1`
- [ ] Write value from UI and verify it arrives correctly in Drive
- [ ] Example: UI input `0.1` ? Drive should receive `8.38190E-9`
- [ ] Test with multiple values (0.01, 0.1, 1.0) to confirm linearity
- [ ] Check both Azimuth (Axis 1) and Altitude (Axis 2)

---

## Related Files

**Code Files:**
- `Azimuth_Control_Parameters_Window.cs` - Constant definition
- `Azimuth_Control_Parameters_Window.Loading.cs` - Reading (Azimuth)
- `Azimuth_Control_Parameters_Window.Saving.cs` - Writing (Azimuth)
- `Altitude_Control_Parameters_Window.cs` - Constant definition
- `Altitude_Control_Parameters_Window.Loading.cs` - Reading (Altitude)
- `Altitude_Control_Parameters_Window.TuningParameters.cs` - Writing (Altitude)

**Documentation:**
- `Docs/ENCODER_CONFIGURATION.md` - Encoder specifications
- `Docs/DEVELOPMENT_NOTES.md` - General development info

---

## Version History

**2025-12-29:** Initial Policy Document
- Documented correct conversion formula
- Added verification example from EASII
- Added common mistakes section
- Fixed bug where DIVIDE was used instead of MULTIPLY for reading

---

**REMEMBER:** 
- **Read from Drive:** MULTIPLY by conversion factor
- **Write to Drive:** DIVIDE by conversion factor
- **Never guess** - always verify with EASII values!

**If you're not 100% sure, DO NOT CHANGE THE CODE!**
