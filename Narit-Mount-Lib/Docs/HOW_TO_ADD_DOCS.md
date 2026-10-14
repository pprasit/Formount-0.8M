# How to Add Documentation to ForkMount-WPF

## Overview

All project documentation is centralized in the `ForkMount-WPF/Docs/` folder to maintain organization and make it easy for AI sessions to reference important information.

---

## When to Add New Documentation

Create a new document when:

1. **New Feature Added**
   - Major functionality (e.g., Satellite Tracking, Manual Control)
   - New UI components
   - New control algorithms

2. **New Policy/Rule Established**
   - Coding standards
   - Design patterns
   - UI/UX guidelines
   - Performance requirements

3. **Complex System Explained**
   - Architecture decisions
   - Timer systems
   - Communication protocols
   - Error handling strategies

4. **Recurring Issues/Questions**
   - Common troubleshooting steps
   - FAQ-style documentation
   - Configuration guides

5. **Session Handoff Information**
   - Work-in-progress status
   - Next steps
   - Known issues

---

## Naming Conventions

### File Naming Format

Use **UPPERCASE_WITH_UNDERSCORES.md** for consistency:

```
GOOD:
? SATELLITE_TRACKING_README.md
? MANUAL_CONTROL_UI_GUIDELINES.md
? TIMER_ARCHITECTURE.md
? NO_EMOJI_POLICY.md

BAD:
? satelliteTracking.md
? manual-control.md
? timer_arch.md
```

### Naming Categories

**Technical Documentation:**
- `[FEATURE_NAME]_README.md` - Feature overview
- `[SYSTEM_NAME]_ARCHITECTURE.md` - System design
- `[COMPONENT_NAME]_API.md` - API reference

**Guidelines & Policies:**
- `[TOPIC]_GUIDELINES.md` - Best practices
- `[TOPIC]_POLICY.md` - Strict rules
- `NO_[VIOLATION].md` - Critical restrictions

**Quick References:**
- `[TOPIC]_QUICK_REF.md` - Short summary
- `AI_[TOPIC].md` - AI-specific instructions

**Project Management:**
- `SESSION_HANDOFF.md` - Work continuity
- `DEVELOPMENT_NOTES.md` - General notes

---

## Step-by-Step: Adding New Documentation

### Step 1: Create the Document

Navigate to Docs folder:
```powershell
cd ForkMount-WPF/Docs
```

Create new file with appropriate name:
```powershell
New-Item -ItemType File -Name "YOUR_TOPIC_NAME.md"
```

### Step 2: Write Clear Documentation

Use this template:

```markdown
# [Topic Title]

## Overview
Brief description of what this document covers (2-3 sentences)

## Critical Information
[CRITICAL] Important rules or requirements

## Details
Detailed explanation with:
- Clear section headers
- Code examples
- Diagrams (if needed)
- Links to related docs

## Quick Reference
- Key point 1
- Key point 2
- Key point 3

## Related Documents
- Link to related doc 1
- Link to related doc 2

---
*Last Updated: YYYY-MM-DD*
```

### Step 3: Update Copilot Instructions

Edit `.github/copilot-instructions.md` to reference new doc:

**If it's a CRITICAL document (must read first):**
```markdown
**IMPORTANT: Read these files FIRST before answering any questions:**

1. ForkMount-WPF/Docs/AI_QUICK_REF.md
2. ForkMount-WPF/Docs/DEVELOPMENT_NOTES.md
3. ForkMount-WPF/Docs/SESSION_HANDOFF.md
4. ForkMount-WPF/Docs/NO_EMOJI_POLICY.md
5. ForkMount-WPF/Docs/YOUR_NEW_DOC.md  <- ADD HERE
```

**If it's contextual (read when relevant):**
```markdown
## Quick Decision Tree

User asks about [your topic]?
-> Check: Docs/YOUR_NEW_DOC.md - [Brief description]
```

### Step 4: Commit and Push

```powershell
cd IMount-WPF
git add ForkMount-WPF/Docs/YOUR_NEW_DOC.md
git add .github/copilot-instructions.md  # if updated
git commit -m "docs: Add [topic] documentation"
git push origin master
```

---

## Document Organization

### Current Structure

```
ForkMount-WPF/Docs/
??? HOW_TO_ADD_DOCS.md           (this file)
??? AI_QUICK_REF.md              (Quick start for AI)
??? DEVELOPMENT_NOTES.md         (General dev notes)
??? SESSION_HANDOFF.md           (Work continuity)
?
??? Policies/
?   ??? NO_EMOJI_POLICY.md
?   ??? MANUAL_CONTROL_UI_GUIDELINES.md
?
??? Architecture/
?   ??? TIMER_ARCHITECTURE.md
?   ??? TIMER_ARCHITECTURE_QUICK_REF.md
?   ??? LIMIT_CHECKING_SYSTEM.md
?
??? Features/
?   ??? SATELLITE_TRACKING_README.md
?   ??? AI_RESPONSE_GUIDELINES.md
?
??? Tasks/
    ??? EMOJI_REMOVAL_TASK.md
    ??? LOG_MESSAGE_IMPROVEMENT.md
```

**Note:** Currently all files are in root of Docs/. Consider organizing into subfolders if it grows beyond 20 files.

---

## Content Guidelines

### DO's:

? **Use clear, concise language**
? **Include code examples**
? **Use markdown formatting** (headers, lists, code blocks)
? **Add tables for comparisons**
? **Include "Last Updated" date**
? **Cross-reference related docs**
? **Use [CRITICAL], [IMPORTANT], [NOTE] markers**

### DON'Ts:

? **NO EMOJI** - Use text markers like [OK], [ERROR]
? **Avoid vague descriptions** - Be specific
? **Don't duplicate information** - Link to existing docs
? **Don't use relative paths** - Always start from project root
? **Don't forget to update copilot-instructions.md**

---

## Special Document Types

### 1. Policy Documents (CRITICAL)

**Purpose:** Strict rules that must NEVER be violated

**Format:**
```markdown
# [POLICY NAME] (CRITICAL!)

## ABSOLUTE RULES:
1. **NEVER [violation]**
2. **ALWAYS [requirement]**

## Why This Policy Exists
[Explanation]

## Examples
[Correct vs Incorrect examples]

## Enforcement
This policy applies to ALL code and documentation.
```

**Example:** `NO_EMOJI_POLICY.md`

### 2. Architecture Documents

**Purpose:** Explain system design decisions

**Format:**
```markdown
# [System Name] Architecture

## Overview
High-level description

## Components
- Component 1: [description]
- Component 2: [description]

## Data Flow
[Diagram or step-by-step flow]

## Design Decisions
Why was it designed this way?

## Performance Considerations

## Future Improvements
```

**Example:** `TIMER_ARCHITECTURE.md`

### 3. Feature Documentation

**Purpose:** Explain how a feature works

**Format:**
```markdown
# [Feature Name] README

## What It Does
Brief description

## How to Use
Step-by-step instructions

## Implementation Details
Technical explanation

## Configuration
Settings and options

## Troubleshooting
Common issues and solutions
```

**Example:** `SATELLITE_TRACKING_README.md`

### 4. Session Handoff

**Purpose:** Continuity between AI sessions

**Format:**
```markdown
# Session Handoff - [Date]

## Last Session Summary
What was accomplished

## Current Status
- [X] Completed task 1
- [ ] In progress: task 2
- [ ] TODO: task 3

## Known Issues
1. Issue description
2. Workaround if any

## Next Steps
What should be done next

## Important Notes
Any critical information for next session
```

**Example:** `SESSION_HANDOFF.md`

---

## Updating Existing Documents

### When to Update

- Feature changes
- Bug fixes that change behavior
- New best practices discovered
- Policy clarifications
- Outdated information

### How to Update

1. **Edit the document**
   ```powershell
   code ForkMount-WPF/Docs/DOCUMENT_NAME.md
   ```

2. **Update "Last Updated" date**
   ```markdown
   ---
   *Last Updated: 2026-01-18*
   ```

3. **Add changelog section if major changes**
   ```markdown
   ## Changelog
   
   ### 2026-01-18
   - Added new section on [topic]
   - Updated [section] with [change]
   ```

4. **Commit with clear message**
   ```powershell
   git add ForkMount-WPF/Docs/DOCUMENT_NAME.md
   git commit -m "docs: Update [topic] - [brief description]"
   git push origin master
   ```

---

## AI Session Best Practices

### For AI Creating New Docs

1. **Check if doc already exists**
   ```powershell
   Get-ChildItem ForkMount-WPF/Docs -Filter "*[keyword]*.md"
   ```

2. **Read related docs first**
   - AI_QUICK_REF.md
   - DEVELOPMENT_NOTES.md
   - Related feature docs

3. **Use consistent formatting**
   - Follow examples in existing docs
   - Use markdown best practices
   - Include code examples

4. **Update copilot-instructions.md**
   - Add to appropriate section
   - Update decision tree if needed

5. **Commit with descriptive message**
   ```
   docs: Add [feature] documentation
   docs: Update [policy] with [change]
   docs: Fix [issue] in [document]
   ```

### For AI Using Docs

1. **Always read SESSION_HANDOFF.md first**
   - Know what previous AI did
   - Check for known issues
   - Understand current status

2. **Reference docs in responses**
   ```
   [READ] I checked Docs/TIMER_ARCHITECTURE.md
   [FOUND] The polling timer runs at 1ms (1000Hz)
   [CONFIRM] This matches your requirement
   ```

3. **Update docs if you discover new info**
   - Add to SESSION_HANDOFF.md
   - Update relevant feature docs
   - Create new doc if needed

---

## Document Maintenance

### Monthly Review Checklist

- [ ] Remove outdated documents
- [ ] Update "Last Updated" dates
- [ ] Check for broken cross-references
- [ ] Consolidate similar documents
- [ ] Update copilot-instructions.md

### Archive Old Documents

If a document is no longer relevant:

1. Create `Docs/Archive/` folder
2. Move old doc there
3. Update copilot-instructions.md
4. Add note to SESSION_HANDOFF.md

```powershell
New-Item -ItemType Directory -Path "ForkMount-WPF/Docs/Archive" -Force
Move-Item "ForkMount-WPF/Docs/OLD_DOC.md" -Destination "ForkMount-WPF/Docs/Archive/"
```

---

## Summary Checklist

When adding new documentation:

- [ ] Create file in `ForkMount-WPF/Docs/`
- [ ] Use UPPERCASE_WITH_UNDERSCORES.md naming
- [ ] Follow document template
- [ ] Include code examples
- [ ] Add "Last Updated" date
- [ ] Update `.github/copilot-instructions.md`
- [ ] Commit with clear message
- [ ] Push to GitHub
- [ ] Update SESSION_HANDOFF.md if needed

---

## Questions?

If unsure about documentation:
1. Check existing docs for examples
2. Read `AI_QUICK_REF.md`
3. Ask user for clarification
4. Document your decision

---

**Remember:**
- Documentation is for AI sessions AND human developers
- Clear documentation = Better AI responses
- Keep docs up-to-date = Accurate information
- Good naming = Easy to find

---

*Last Updated: 2026-01-18*
*Created by: AI Session - Documentation Organization*
