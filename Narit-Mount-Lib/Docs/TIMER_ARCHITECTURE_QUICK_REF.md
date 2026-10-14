# Timer Architecture - Quick Reference Card

**For:** AI Session Handoff  
**Last Updated:** 2025-01-04

---

## ?? Quick Start

### **Read These Files First:**
1. `TIMER_ARCHITECTURE.md` - Full architecture guide
2. `SESSION_HANDOFF.md` - Current session status  
3. `.github/copilot-instructions.md` - Project rules

---

## ?? Architecture in 30 Seconds

```
???????????????????????????????????????????????????
? Polling (1ms) ? AzimuthInfo ? UI Timer (50ms)  ?
?                      ?                          ?
?               PID Loop (20ms)                   ?
???????????????????????????????????????????????????
```

**Rules:**
- Polling = Data collection only (no UI)
- UI Timer = Display only (no serial)
- PID Loop = Reads from AzimuthInfo (not Drive!)
- User Commands = async/await

---

## ? Do's

```csharp
// ? Polling: Only write to AzimuthInfo
private void PollingTimer_Tick(...)
{
    double pos = _drive.ReadPosition();
    _azimuthInfo.Raw_Position = pos;
}

// ? UI: Only read from AzimuthInfo
private void UIUpdateTimer_Tick(...)
{
    double pos = _azimuthInfo.Raw_Position;
    txtPosition.Text = pos.ToString();
}

// ? PID: Read from AzimuthInfo
private void PositionControlLoop(...)
{
    double pos = _azimuthInfo.Raw_Position; // FAST!
    double cmd = Calculate(...);
    _drive.MoveVelocity(cmd);
}

// ? Commands: Use async
private async void BtnMove_Click(...)
{
    await Task.Run(() => _drive.Move(...));
}
```

---

## ? Don'ts

```csharp
// ? Don't update UI from Polling
private void PollingTimer_Tick(...)
{
    txtPosition.Text = "..."; // WRONG!
}

// ? Don't call serial from UI Timer
private void UIUpdateTimer_Tick(...)
{
    double pos = _drive.ReadPosition(); // WRONG!
}

// ? Don't call ReadPosition in PID Loop
private void PositionControlLoop(...)
{
    double pos = _drive.ReadPosition(); // WRONG! Blocks!
}

// ? Don't use Dispatcher in loops
private void PositionControlLoop(...)
{
    Dispatcher.Invoke(() => ...); // WRONG!
}
```

---

## ?? Common Fixes

### **Problem:** UI freezes
```csharp
// Find blocking calls in PID Loop
double pos = _drive.ReadPosition(); // ? This!

// Replace with:
double pos = _azimuthInfo.Raw_Position; // ? Non-blocking!
```

### **Problem:** Need to display message from PID Loop
```csharp
// Don't use Dispatcher:
Dispatcher.BeginInvoke(() => AddLog(...)); // WRONG!

// Write to field instead:
_pidStatusMessage = "[ERROR] ..."; // UI Timer will display
```

### **Problem:** Button click blocks UI
```csharp
// Don't call serial directly:
_drive.MoveVelocity(vel); // WRONG! Blocks UI!

// Use async:
await Task.Run(() => _drive.MoveVelocity(vel)); // ? Non-blocking!
```

---

## ?? Performance Targets

| Timer | Rate | Purpose |
|-------|------|---------|
| Polling | 1000Hz (1ms) | Data collection |
| UI Update | 20Hz (50ms) | Display |
| PID Loop | 50Hz (20ms) | Control |

---

## ?? Checklist for New Code

- [ ] Polling only writes to AzimuthInfo
- [ ] UI Timer only reads from AzimuthInfo
- [ ] PID Loop reads from AzimuthInfo (not Drive!)
- [ ] User commands use async/await
- [ ] No Dispatcher calls in loops
- [ ] No blocking calls in PID Loop

---

## ?? Key Files

| File | Location | Purpose |
|------|----------|---------|
| AzimuthInfo.cs | Engine/ | Thread-safe data hub |
| Azimuth_Controller.xaml.cs | UI/ | Main controller |
| TIMER_ARCHITECTURE.md | Root | Full guide |
| SESSION_HANDOFF.md | Root | Session status |

---

**For Full Details:** Read `TIMER_ARCHITECTURE.md` (complete guide with examples)
