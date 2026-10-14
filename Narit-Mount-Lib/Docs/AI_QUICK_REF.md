# AI Assistant Quick Reference - ForkMount-WPF

## TL;DR - Read This First!

**Command Format:** `AXn.command` (e.g., `AX1.PX`, `AX2.VX`)

**Wrong Formats:** `PX[1]`, `[1]PX`, `##AXIS_SendMain 1 PX`, `PX` (all will fail!)

**Serial Number:** 25262718 | **COM Port:** COM10 | **Axes:** 1=Azimuth, 2=Altitude

---

## 30-Second Briefing

```
========================================
  ForkMount = 4-Axis Platinum Drive
  Format: AXn.command
  Example: AX1.PX = Read Position Axis 1
  Poll Rate: 5ms (200Hz) for 2-axis
========================================
```

### System Layers
```
UI (Azimuth_Controller.xaml.cs)
    |
AzimuthDrive (Axis 1 wrapper)
    |
PlatinumDrive (Base communication)
    |
Elmo Platinum Drive (Hardware)
```

---

## ElmoCommandSearcher Tool

**When unsure about command syntax, use this tool!**

```powershell
cd ElmoCommandSearcher
dotnet run -- "KV"              # Search KV parameters
dotnet run -- "multi-axis"      # Search multi-axis format
dotnet run -- "parameter"       # Search parameter syntax
```

**Indexed:** 10,654 documentation files (from EAS II installation)
**Search time:** < 1 second

**Setup Required:**
- Copy NetHelp from: `C:\Program Files\Elmo Motion Control\Elmo Application Studio II\NetHelp`
- To: `ElmoCommandSearcher\NetHelp\`
- See: `ElmoCommandSearcher/NetHelp/README.md`

**Key Findings:**
- Multi-Axis format: `command[axis]`
- Parameter access: `command,param[axis]`
- Filter config: KV[1-60] parameters

---

## Command Quick Reference

| Task | Command | Axis | Example |
|------|---------|------|---------|
| Read Position | `AXn.PX` | 1-4 | `AX1.PX` |
| Read Velocity | `AXn.VX` | 1-4 | `AX1.VX` |
| Read Current | `AXn.IQ` | 1-4 | `AX1.IQ` |
| Read Motor On | `AXn.MO` | 1-4 | `AX1.MO` |
| Enable Motor | `AXn.MO=1` | 1-4 | `AX1.MO=1` |
| Disable Motor | `AXn.MO=0` | 1-4 | `AX1.MO=0` |
| Stop Motion | `AXn.ST` | 1-4 | `AX1.ST` |
| Set Velocity | `AXn.JV=value` | 1-4 | `AX1.JV=11930465` |
| Begin Motion | `AXn.BG` | 1-4 | `AX1.BG` |
| Read Digital Input | `AXn.IP` | 1-4 | `AX1.IP` |

**Drive-Level (no axis prefix):**
- `SN[4]` - Serial Number
- `VR[2]` - Software Version

**Encoder Resolution (32-bit for ALL axes):**
- Azimuth (Axis 1): 4,294,967,296 counts/revolution
- Altitude (Axis 2): 4,294,967,296 counts/revolution
- Fork1 (Axis 3): 4,294,967,296 counts/revolution
- Fork2 (Axis 4): 4,294,967,296 counts/revolution

**Conversion Formulas:**
- 1 degree = 11,930,465 counts (4,294,967,296 / 360)
- 1 deg/sec = 11,930,465 counts/sec
- Position (deg) = (counts / 4,294,967,296) × 360
- Velocity (deg/sec) = (counts/sec / 4,294,967,296) × 360

**[CRITICAL] KP[2] Velocity Gain Conversion:**
- **Reading from Drive:** `KP[2]_UI = KP[2]_Drive × 11,930,464.7111` (MULTIPLY!)
- **Writing to Drive:** `KP[2]_Drive = KP[2]_UI / 11,930,464.7111` (DIVIDE!)
- **Example:** Drive `8.38190E-9` ? UI `0.1 A/(deg/sec)`
- **See:** `Docs/KP2_VELOCITY_GAIN_CONVERSION_POLICY.md` for full details

---

## Common Tasks

### Connect to Drive
```csharp
var drive = new AzimuthDrive();
drive.ConnectBySerialNumber("25262718");
drive.StartPolling(); // 1000Hz
```

### Read Data
```csharp
double pos = drive.ReadPosition();    // Auto uses Axis 1
double vel = drive.ReadVelocity();    // Auto uses Axis 1
```

### Send Command
```csharp
platinumDrive.SendCommandToAxis("MO=1", 1, out response);
// Sends: MO[1]=1
```

---

## Common Errors & Fixes

| Error | Wrong Code | Correct Code |
|-------|------------|--------------|
| Drive error 2 | `[1]PX` | `PX[1]` |
| Drive error 2 | `##AXIS_SendMain 1 PX` | `PX[1]` |
| Drive error 2 | `PX` | `PX[1]` |
| SN = Unknown | Missing delay after connect | Add 500ms delay or retry |
| Unsure about KV | Guessing syntax | Use ElmoCommandSearcher! |

---

## File Structure

```
ForkMount-WPF/
??? Engine/
    ??? PlatinumDrive.cs          [BASE LAYER] (read first!)
    ??? AzimuthDrive.cs            [Axis 1 wrapper]
    ??? AltitudeDrive.cs           [Axis 2 wrapper]
    ??? AzimuthInfo.cs             [Data container]
    ??? AltitudeInfo.cs            [Data container]
    ??? AppSettings.cs             [Configuration]
??? UI/
?   ??? Azimuth_Controller.xaml.cs [Main UI]
?   ??? Azimuth_Control_Parameters_Window.xaml.cs
??? DEVELOPMENT_NOTES.md           [Full documentation]
??? ElmoCommandSearcher/           [Documentation search tool]
    ??? Program.cs
    ??? NetHelp/                   [10,654 HTML docs]
```

---

## Learning Path for New AI Session

1. **Read this file** (you're here!)
2. **Read:** `Engine/PlatinumDrive.cs` header comment
3. **Read:** `DEVELOPMENT_NOTES.md` for details
4. **Scan:** `Engine/AzimuthDrive.cs` to see wrapper pattern
5. **Tool:** Use `ElmoCommandSearcher` if unsure about commands
6. **Ready!** Now you understand the system

**Time:** ~2 minutes to read all documentation

---

## Decision Tree

```
Need to send command to drive?
|
+-- Is it axis-specific? (PX, VX, MO, etc.)
|   +-- YES -> Use: command[axis]
|              Example: PX[1]
|
+-- Is it drive-level? (SN, VR, etc.)
|   +-- YES -> Use: command[param]
|              Example: SN[4]
|
+-- Unsure about syntax?
    +-- Use ElmoCommandSearcher!
        cd ElmoCommandSearcher; dotnet run -- "command_name"
```

---

## Red Flags (Don't Do These!)

- [WRONG] `[axis]command` format
- [WRONG] Sending commands without axis number
- [WRONG] Using IMount-WPF command format (different system!)
- [WRONG] Blocking waits in polling loop
- [WRONG] Polling slower than 1ms (can go faster!)
- [WRONG] Guessing command syntax without checking docs

---

## Green Flags (Do These!)

- [CORRECT] `command[axis]` format
- [CORRECT] Use wrapper classes (AzimuthDrive, AltitudeDrive)
- [CORRECT] Async polling at 1ms
- [CORRECT] Check Debug Output for command logs
- [CORRECT] Read DEVELOPMENT_NOTES.md for full context
- [CORRECT] **Use ElmoCommandSearcher when unsure!**

---

## Quick Links

- Full Docs: `DEVELOPMENT_NOTES.md`
- Base Layer: `Engine/PlatinumDrive.cs`
- Axis 1: `Engine/AzimuthDrive.cs`
- Axis 2: `Engine/AltitudeDrive.cs`
- **Search Tool:** `ElmoCommandSearcher/Program.cs`
- **Documentation:** `ElmoCommandSearcher/NetHelp/` (10,654 files)

---

**Last Updated:** 2025-12-29 - Removed emoji per NO EMOJI POLICY
**Status:** Working with `command[axis]` format
**Next Steps:** Read DEVELOPMENT_NOTES.md for complete understanding

---

*[GOAL] Any AI should understand this system in < 5 minutes*
