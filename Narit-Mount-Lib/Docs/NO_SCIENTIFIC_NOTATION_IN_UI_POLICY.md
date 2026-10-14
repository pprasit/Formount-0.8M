# UI Number Format Policy

**Document Version:** 1.0  
**Last Updated:** 2025-01-20  
**Status:** ACTIVE

---

## POLICY: NO SCIENTIFIC NOTATION IN UI

### Summary
**All numeric values displayed in the ForkMount-WPF User Interface MUST use standard decimal notation.**  
**Scientific notation (E-notation, such as 1.23E+05) is PROHIBITED in all UI displays.**

---

## Rationale

1. **User Experience:** Scientific notation (e.g., `1.100000E+000`) is difficult to read and interpret for users
2. **Consistency:** All numbers should be displayed in the same format throughout the application
3. **Professional Appearance:** Standard decimal notation looks more professional in control system UIs
4. **Accessibility:** Decimal notation is universally understood without specialized knowledge

---

## Rules

### ? CORRECT - Use Standard Decimal Notation:
```
1.100000   (instead of 1.100000E+000)
0.000001   (instead of 1.000000E-006)
0.100000   (instead of 1.000000E-001)
50.00      (instead of 5.000000E+001)
```

### ? WRONG - Do NOT Use Scientific Notation:
```
1.100000E+000   [WRONG]
1.000000E-006   [WRONG]
1.000000E-001   [WRONG]
5.000000E+001   [WRONG]
```

---

## Implementation

### For All UI TextBoxes:

Use the `FormatToDecimal` helper method which implements this policy:

```csharp
private string FormatToDecimal(string text)
{
    if (string.IsNullOrWhiteSpace(text))
        return text;

    if (double.TryParse(text, out double value))
    {
        // Small numbers (< 0.0001): Use F10 for precision
        if (Math.Abs(value) < 0.0001 && value != 0)
        {
            return value.ToString("F10").TrimEnd('0').TrimEnd('.');
        }
        // Numbers < 1: Use F6
        else if (Math.Abs(value) < 1)
        {
            return value.ToString("F6").TrimEnd('0').TrimEnd('.');
        }
        // Numbers >= 1: Use F4
        else
        {
            return value.ToString("F4").TrimEnd('0').TrimEnd('.');
        }
    }

    return text;
}
```

### Format Specifications:

| Value Range | Format | Example Input | Example Output |
|-------------|--------|---------------|----------------|
| \|x\| < 0.0001 (x?0) | F10 | 0.00000123 | 0.0000012300 ? 0.00000123 |
| 0.0001 ? \|x\| < 1 | F6 | 0.123456 | 0.123456 |
| \|x\| ? 1 | F4 | 1.1 | 1.1000 ? 1.1 |
| x = 0 | Any | 0 | 0 |

**Note:** `.TrimEnd('0').TrimEnd('.')` removes trailing zeros and decimal point if not needed.

---

## Affected Components

### Windows Using This Policy:
1. ? `Azimuth_Control_Parameters_Window` - PD Controller, Elmo Internal Loop, Filter Parameters
2. ? `Altitude_Control_Parameters_Window` - PD Controller, Elmo Internal Loop, Filter Parameters
3. [ ] Any future parameter windows

### Controls Affected:
- All TextBox controls displaying numeric parameters
- PD Controller gains (Kp, Ki, Kd)
- Drive parameters (Velocity Gain, Velocity Integral, Position Gain, etc.)
- Filter parameters (Frequency, Q Factor, Magnitude, Damping)
- Tuning offsets
- Motion parameters (Acceleration, Deceleration, etc.)

---

## Code Locations

### Main Implementation Files:
```
ForkMount-WPF/UI/ParametersWindow/Azimuth_Control_Parameters_Window.cs
    - FormatToDecimal() method (line ~345)
    
ForkMount-WPF/UI/Altitude_Control_Parameters_Window.cs
    - FormatToDecimal() method (line ~XXX) [TO BE ADDED]
```

### Usage Pattern:
```csharp
private void TxtKp_LostFocus(object sender, RoutedEventArgs e)
{
    if (_isLoading) return;
    
    // FORMAT: Convert input to standard decimal notation
    txtKp.Text = FormatToDecimal(txtKp.Text);
    
    // Then apply to Drive
    ApplyPDParameter("Kp", ParseDouble(txtKp.Text, 1.0));
}
```

---

## Testing Checklist

When implementing or modifying numeric display, verify:

- [ ] Small numbers (0.000001) display without E-notation
- [ ] Large numbers (100000.0) display without E-notation  
- [ ] Decimal numbers (1.23456) display correctly
- [ ] Zero displays as "0"
- [ ] Trailing zeros are trimmed appropriately
- [ ] Unnecessary decimal points are removed (e.g., "1." becomes "1")
- [ ] User can still INPUT scientific notation (e.g., 1E-6) which gets converted to decimal

---

## Exceptions

**NO EXCEPTIONS.**  
This policy applies to **ALL** user-facing numeric displays in the UI.

Internal calculations, logging, or data storage MAY use scientific notation if needed,  
but **ALL UI displays MUST use standard decimal notation.**

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-01-20 | System Policy | Initial policy document - No scientific notation in UI |

---

## References

- Related Document: `NO_EMOJI_POLICY.md` - Text display standards
- Related Code: `FormatToDecimal()` helper method
- User Feedback: "Format E+XXX is confusing - use decimal notation instead"

---

**This is a MANDATORY policy for all ForkMount-WPF UI development.**
