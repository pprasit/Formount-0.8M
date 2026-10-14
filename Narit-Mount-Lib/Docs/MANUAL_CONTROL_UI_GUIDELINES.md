# Manual Control Window - UI Guidelines

## ?? CRITICAL: Arrow Symbol Encoding Issue

### Problem:
- Unicode arrow characters (? ? ? ?) display as `?` in some environments
- Font encoding issues cause rendering problems

### Solution:
**ALWAYS use SVG Path instead of Unicode characters for directional buttons**

```xaml
<!-- ? CORRECT: Use Path with SVG data -->
<Button>
    <Path Data="M7 14L12 9L17 14H7Z"    <!-- Up arrow -->
          Fill="White"
          Width="28"
          Height="28"
          Stretch="Uniform"/>
</Button>

<!-- ? WRONG: Don't use Unicode characters -->
<Button Content="?"/>  <!-- Will show as ? -->
```

---

## Standard Arrow Path Data

**Up Arrow:**
```xaml
Data="M7 14L12 9L17 14H7Z"
```

**Down Arrow:**
```xaml
Data="M7 10L12 15L17 10H7Z"
```

**Left Arrow:**
```xaml
Data="M14 7L9 12L14 17V7Z"
```

**Right Arrow:**
```xaml
Data="M10 17L15 12L10 7V17Z"
```

---

## UI Layout Requirements

### Button Sizes:
- **Direction Buttons**: Default size with 5px margin
- **Center Stop Button**: 70x70 px, circular (CornerRadius="35")
- **Icon Size**: 28x28 px for arrows, 24x24 px for stop icon

### Center Stop Button:
```xaml
<Button Width="70" Height="70" Margin="15">
    <Button.Style>
        <Style TargetType="Button">
            <Setter Property="Background" Value="#EF4444"/>
            <!-- Red background -->
            <Setter Property="Template">
                <ControlTemplate>
                    <Border CornerRadius="35">  <!-- Circular -->
                        <DropShadowEffect Color="#EF4444" BlurRadius="20"/>
                        <Path Data="M6 6H18V18H6V6Z"/>  <!-- Stop square -->
                        <TextBlock Text="STOP"/>
                    </Border>
                </ControlTemplate>
            </Setter>
        </Style>
    </Button.Style>
</Button>
```

---

## Color Scheme

### Direction Buttons:
- Default: `#2D3748`
- Hover: `#3B82F6` (Blue)
- Pressed: `#10B981` (Green)

### Stop Button:
- Default: `#EF4444` (Red)
- Hover: `#DC2626` (Darker Red)
- Pressed: `#B91C1C` (Even Darker Red)
- Glow Effect: `DropShadowEffect` with red color

---

## Event Handling

### Direction Buttons:
```csharp
// Press and hold
PreviewMouseLeftButtonDown ? Set Command_Velocity

// Release
PreviewMouseLeftButtonUp ? Set Command_Velocity = 0
```

### Stop Button:
```csharp
Click ? StopAllMotion()
```

---

## Size Guidelines

**Base Size Reduction: 30% smaller than original design**

- Grid Row Height: `280px` (was 400px)
- Button Margin: `5px` (consistent)
- Center Button: `70x70px` (was 100x100px)
- Icon Size: `28x28px` (was 40x40px)

---

## Summary for AI

When creating or modifying Manual Control Window:

1. ? **ALWAYS** use Path for arrows, NEVER Unicode characters
2. ? Center button MUST be red STOP button
3. ? Button sizes reduced by 30% from original
4. ? Use SVG path data for all icons
5. ? Maintain color scheme (Blue/Green for directions, Red for stop)
6. ? Implement Press+Hold for movement, Release to stop
7. ? Center stop button size: 70x70px circular

---

## File Locations

- XAML: `ForkMount-WPF/UI/ManualControlWindow.xaml`
- Code: `ForkMount-WPF/UI/ManualControlWindow.xaml.cs`

---

**Last Updated:** 2025-01-XX
**Created By:** Session fixing arrow encoding and adding stop button
