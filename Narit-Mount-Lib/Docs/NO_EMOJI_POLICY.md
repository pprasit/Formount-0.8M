# NO EMOJI POLICY - ForkMount-WPF & IMount-WPF

## ?? CRITICAL RULE: NO EMOJI ANYWHERE IN THE CODEBASE

### Why?
- Emoji characters display as `?` symbols in logs and console outputs
- Creates confusion and makes logs unreadable
- Causes encoding issues in different environments
- Not professional for production software

---

## ?? FORBIDDEN - Do NOT Use These:

### Emoji Symbols (Examples):
```
?? ? ? ?? ?? ?? ?? ? ?? ?? ?? ?
?? ? ? ? ?? ?? ?? ? ?? ?? ??
```

### Where to Check:
- [ ] Source code files (`.cs`, `.xaml`)
- [ ] Log messages (`AddLog()`, `SimpleLogger.Log()`)
- [ ] UI text (`TextBlock.Text`, `Button.Content`)
- [ ] Documentation files (`.md`)
- [ ] Comments in code
- [ ] Git commit messages

---

## ? ALLOWED - Use These Instead:

### Text-Based Alternatives:

| Instead of Emoji | Use Text |
|------------------|----------|
| ? | `[OK]` or `SUCCESS:` |
| ? | `[ERROR]` or `FAILED:` |
| ?? | `[WARNING]` or `WARNING:` |
| ?? | `[CONFIG]` or `[SETUP]` |
| ?? | `[NOTE]` or `[INFO]` |
| ?? | `[TARGET]` or `[GOAL]` |
| ? | `[IMPORTANT]` or `NOTE:` |
| ?? | `[STOP]` or `EMERGENCY:` |
| ?? | `[UPDATE]` or `[SYNC]` |
| ?? | `[STATS]` or `[DATA]` |

### Examples:

**BAD (With Emoji):**
```csharp
AddLog("?? Opening Control Parameters Window...");
AddLog("? Drive connected successfully");
AddLog("? Connection failed");
```

**GOOD (Without Emoji):**
```csharp
AddLog("[WINDOW] Opening Control Parameters Window...");
AddLog("[CONNECT] SUCCESS: Drive connected successfully");
AddLog("[CONNECT] ERROR: Connection failed");
```

---

## ?? How to Find Emoji in Your Code

### Method 1: Visual Studio Search
```
Search Pattern: [^\x00-\x7F]
Options: Use Regular Expressions
```

### Method 2: PowerShell Script
```powershell
# Search for emoji in all .cs files
Get-ChildItem -Recurse -Filter *.cs | 
  Select-String -Pattern "[^\x00-\x7F]" | 
  Select-Object Path, LineNumber, Line
```

### Method 3: Git Pre-commit Hook
```bash
#!/bin/bash
# .git/hooks/pre-commit
if git diff --cached | grep -P '[^\x00-\x7F]' > /dev/null; then
    echo "ERROR: Emoji detected in staged files!"
    exit 1
fi
```

---

## ?? Checklist Before Commit

- [ ] No emoji in log messages
- [ ] No emoji in comments
- [ ] No emoji in UI text
- [ ] No emoji in documentation
- [ ] All text is ASCII or proper UTF-8
- [ ] Tested logs display correctly

---

## ? Approved Text Patterns

### For Logs:
```csharp
// Use brackets with descriptive text
AddLog("[LOAD] Loading parameters from Drive...");
AddLog("[SAVE] Parameters saved successfully");
AddLog("[ERROR] Connection timeout");
AddLog("[WARNING] Drive not responding");
AddLog("[INFO] Polling started at 1000Hz");
```

### For Documentation:
```markdown
## STATUS: Complete
- Task 1: DONE
- Task 2: DONE
- Task 3: IN PROGRESS
```

### For UI:
```xml
<TextBlock Text="Status: Connected" />
<Button Content="ENABLE" />
<TextBlock Text="[OK] Ready" />
```

---

## ?? If You Find Emoji

### Steps to Fix:
1. **Identify** - Find all emoji usage
2. **Replace** - Use text alternatives
3. **Test** - Verify logs display correctly
4. **Document** - Update this file if needed
5. **Commit** - Include "fix: remove emoji" in message

### Example Fix:
```diff
- AddLog("? Connected");
+ AddLog("[CONNECT] SUCCESS: Connected");

- SystemLogPanel.AddMessageLog("?? EMERGENCY STOP ACTIVATED!");
+ SystemLogPanel.AddMessageLog("[EMERGENCY] STOP ACTIVATED!");

- AddLog("? Drive status check: Enabled");
+ AddLog("[STATUS] IMPORTANT: Drive status check: Enabled");
```

---

## ?? For AI Assistants

**CRITICAL RULES:**
1. **NEVER** use emoji in any code or log messages
2. **ALWAYS** use text-based alternatives
3. **ALWAYS** check for emoji before suggesting code
4. **ALWAYS** replace existing emoji with text

**Before suggesting ANY code, ask yourself:**
- Does this contain emoji? ? Replace with text
- Will this display correctly in console? ? Use ASCII only
- Is this production-ready? ? No emoji allowed

---

## ?? Goal

**Zero emoji in the entire codebase**
- Professional appearance
- No encoding issues
- Clean, readable logs
- Works in all environments

---

**Status:** ? Policy Established
**Effective Date:** 2025-12-29
**Applies To:** All code, logs, documentation, and commits

---

*This policy must be followed for all future development*
*NO EXCEPTIONS - Emoji = Rejected PR/Commit*
