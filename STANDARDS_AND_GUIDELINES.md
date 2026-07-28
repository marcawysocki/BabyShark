# BabyShark Development Standards & Guidelines

Based on standards from PupusPistrixVectatorPestium repository. This document establishes coding conventions, architectural patterns, and best practices for the BabyShark project.

---

## 1. Protected Paths & Upstream Framework Policy

### Sharky Framework Protection
- **`Sharky/` directory is protected read-only upstream code**
- Must not be modified without explicit approval
- Changes to `Sharky/` require:
  - Dedicated branch: `sharky/<reason>`
  - Clear justification in PR description
  - Owner approval
  - Test evidence
  - Label: `sharky-mod`

### Custom Code Organization
- **Custom code belongs in `BabySharkBot/` directory only**
- Subdivisions:
  - `BabySharkBot/Builds/` - Build orders and strategies
  - `BabySharkBot/Managers/` - Manager services
  - `BabySharkBot/Setup/` - Initialization, DTOs, configuration (NOT managers)
  - `BabySharkBot/Tasks/` - Custom MicroTasks (if created)

### Local Git Safeguards
```bash
# Skip-worktree protected files (prevents accidental commits)
git ls-files -z "Sharky" | xargs -0 -n1 git update-index --skip-worktree

# Undo if needed
git ls-files -z "Sharky" | xargs -0 -n1 git update-index --no-skip-worktree

# Exclude new local files
echo "/Sharky/" >> .git/info/exclude

# Enable pre-commit hook
git config core.hooksPath .githooks
chmod +x .githooks/pre-commit
```

---

## 2. Code Organization Standards

### Namespace Conventions
- **Always match directory structure to namespace**
  - Directory: `BabySharkBot/Managers/` → Namespace: `BabySharkBot.Managers`
  - Directory: `BabySharkBot/Setup/` → Namespace: `BabySharkBot.Setup`
  - Directory: `BabySharkBot/Builds/` → Namespace: `BabySharkBot.Builds`

### File Organization
```
BabySharkBot/
├── Program.cs                 # Entry point
├── ZergBuildChoices.cs        # Build choice logic
├── Builds/                    # Build orders and strategies
│   ├── BasicZerglingRush.cs
│   └── MutaliskRush.cs
├── Managers/                  # Manager services only
│   ├── SomeManager.cs
│   └── AnotherManager.cs
├── Setup/                     # Configuration & initialization (NOT managers)
│   ├── InitialMapData.cs      # Map setup utilities
│   ├── BaseDtos.cs            # Data transfer objects
│   └── Settings.cs            # Configuration
└── Tasks/                     # Custom MicroTasks (if any)
    └── CustomTask.cs
```

### What Belongs in Setup/
- `InitialMapData.cs` - Map parsing and resource detection
- `BaseDtos.cs` - Data transfer objects for serialization
- `Settings.cs` - Configuration and global settings
- **NOT**: Manager classes (those go in `Managers/`)

---

## 3. Debug Drawing Standards

### Centralized Label System (UnitLabelSystem Pattern)
All debug labels must use a centralized composition pattern to prevent:
- Labels disappearing after refactors
- Duplicate label creation each frame
- Inconsistent label rendering

### UnitLabel Class Pattern
```csharp
public class UnitLabel
{
    public Unit Unit { get; set; }                    // SC2API unit snapshot
    public string Label { get; set; }                 // Display text
    public string Role { get; set; }                  // Miner, Builder, Scout, etc.
    public Queue<Vector2> InstructionQueue { get; set; } = new();
    public Vector2? CurrentTarget { get; set; }       // For movement arrows
}
```

### Implementation Requirements
```csharp
public Dictionary<ulong, UnitLabel> UnitLabels = new();

// Update once per frame in OnFrame
foreach (var unit in observation.Observation.RawData.Units)
{
    if (!UnitLabels.TryGetValue(unit.Tag, out var label))
    {
        label = new UnitLabel
        {
            Unit = unit,
            Label = DefaultLabel(unit),
            Role = DetermineRole(unit)
        };
        UnitLabels[unit.Tag] = label;
    }
    else
    {
        label.Unit = unit; // Refresh snapshot
    }
}

// Centralized drawing
public void DrawUnitLabels(DebugService debug)
{
    foreach (var label in UnitLabels.Values)
    {
        var pos = new Point2D { X = label.Unit.Pos.X, Y = label.Unit.Pos.Y };
        debug.DebugTextOut($"{label.Label}\n{label.Role}", pos, Color.Yellow);
        
        if (label.CurrentTarget != null)
        {
            debug.DebugLineOut(label.Unit.Pos, label.CurrentTarget.Value, Color.White);
        }
    }
}
```

### Debug Options Control
- All debug drawing is gated by `SharkyOptions.Debug`
- Respect `SharkyOptions.DebugMicroTaskUnits` for task-specific labels
- Never log to console in production code
- Use `Settings.DebugMode` for conditional logging

---

## 4. Coding Conventions

### Style
- **Language:** C# 13, .NET 9
- **Indentation:** Tabs (4 spaces wide)
- **Naming:** Pascal case for public members, camelCase for private
- **Async:** Use `async/await` pattern consistently
- **Error Handling:** Log exceptions, don't crash

### Example
```csharp
public class MyManager
{
	private DebugService _debugService;
	
	public void ProcessUnits(List<Unit> units)
	{
		foreach (var unit in units)
		{
			try
			{
				HandleUnit(unit);
			}
			catch (Exception ex)
			{
				_debugService?.DrawText($"Error: {ex.Message}");
			}
		}
	}
	
	private void HandleUnit(Unit unit) { }
}
```

### Using Statements
- Always place in alphabetical order within groups:
  1. System namespaces
  2. Third-party
  3. Sharky
  4. BabySharkBot

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using BabySharkBot.Setup;
```

---

## 5. Type & API Standards

### Fully Qualified Names
When types from `Sharky` cannot be resolved by `using`, use fully qualified names:
```csharp
// If UnitTypes is not resolving from `using Sharky;`
var mineralTypes = new HashSet<Sharky.UnitTypes>
{
    Sharky.UnitTypes.NEUTRAL_MINERALFIELD,
    Sharky.UnitTypes.NEUTRAL_MINERALFIELD750,
};
```

### No External Packages Without Approval
- Default: No new NuGet packages
- If needed: List exact package name and version
- Request owner approval before adding

---

## 6. Prompt Template for AI Assistance

Use this template when requesting code changes to ensure precision and reduce hallucinations:

### Title
One short sentence describing the task.

### Repository / Context
- Repo path: `C:\Users\marca\source\repos\BabyShark`
- Files to inspect: List exact paths
- Active file: Which file to focus on
- Language: `C# 13`, `.NET 9`

### Goal (Explicit Success Criteria)
- What must be implemented, fixed, or changed
- Measurable acceptance criteria (compile, tests pass, behavior)

### Inputs
- Function signatures, sample data
- Allowed/disallowed libraries

### Outputs / Artifacts
- Files to create/modify (exact paths)
- Tests to add
- Expected behavior

### Constraints & Non-Goals
- What NOT to change
- Compatibility constraints (no breaking changes to Sharky)
- Security considerations

### Needed Domain Knowledge
- Provide exact type names, methods, snippets
- List project symbols if referencing them

### Style & Conventions
- Tabs for indentation
- Pascal case public, camelCase private
- No debug output in production
- Async/await patterns

### Validation & Tests
- Build command: `dotnet build`
- Test command: `dotnet test`
- Expected exit codes and output

### Deliverables
- Exact code changes
- Modified files with short changelog
- Clear, concise explanations

---

## 7. Documentation Standards

### Changelog Entries
Every commit touching `BabySharkBot/` should include:
- Short description of change
- Why it belongs in BabySharkBot (not Sharky)
- Files modified
- Test verification steps

### Code Comments
- Use `//` for single-line comments
- Use `/* */` only for multi-line block comments
- Document public method signatures:
  ```csharp
  /// <summary>
  /// Analyzes mineral patches and assigns workers.
  /// </summary>
  public void AssignWorkers(List<Unit> patches) { }
  ```

### README Requirements
- Update `README.md` if adding new features or changing build instructions
- Include any new map dependencies or external tools

---

## 8. Testing & Validation

### Pre-Commit Checks
1. Code compiles: `dotnet build`
2. No Sharky modifications
3. No new external dependencies (without approval)
4. All style conventions followed

### Test Coverage
- New public methods should have unit tests
- Integration tests for build orders recommended
- Manual validation: Run bot for 30+ seconds on test map

### Local Validation Script
```bash
# Fails if any staged change touches Sharky/
scripts/validate-protected-paths.sh
```

---

## 9. Branching & PR Policy

### Branch Naming
- Custom code: `maw/<short-description>` or `feature/<name>`
- Bug fixes: `fix/<short-description>`
- Framework mods: `sharky/<reason>` (requires approval)

### PR Requirements
- **Title:** Clear, descriptive
- **Description:** Include:
  - Motivation and summary
  - Files changed in `BabySharkBot/`
  - Explicit statement: "No changes to `Sharky/`"
  - Test steps and expected results
- **Labels:** 
  - `maw` for custom code
  - `sharky-mod` for framework changes (with approval)
- **Approval:** At least one code review required

### Merge Checklist
- [ ] Code compiles
- [ ] Tests pass
- [ ] No `Sharky/` modifications (or approved)
- [ ] Changelog updated
- [ ] Style conventions followed
- [ ] Approved by owner

---

## 10. Common Pitfalls & Solutions

### Problem: Debug Labels Disappear After Refactoring
**Solution:** Use `UnitLabelSystem` pattern with persistent dictionary keyed by `Unit.Tag`

### Problem: Duplicate Label Creation Each Frame
**Solution:** Check `Dictionary.TryGetValue()` before creating new labels

### Problem: Type Not Found (e.g., `UnitTypes`)
**Solution:** Use fully qualified name `Sharky.UnitTypes` if `using Sharky;` doesn't resolve

### Problem: Accidental Changes to `Sharky/`
**Solution:** Run git skip-worktree and pre-commit hook as documented in Section 1

### Problem: Inconsistent Namespace Paths
**Solution:** Always match directory structure exactly: `Dir/` → `namespace Dir`

---

## 11. Examples from This Project

### Correct Directory & Namespace
- **File:** `BabySharkBot/Setup/InitialMapData.cs`
- **Namespace:** `namespace BabySharkBot.Setup`
- **Rationale:** Setup utilities, not a manager

### Correct Using Statements
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using System.Numerics;
```

### Correct Fully Qualified Type Usage
```csharp
var mineralTypes = new HashSet<Sharky.UnitTypes>
{
    Sharky.UnitTypes.NEUTRAL_MINERALFIELD,
    Sharky.UnitTypes.NEUTRAL_MINERALFIELD750,
};
```

---

## 12. Questions & Escalation

**Q: Can I modify `Sharky/`?**  
A: Only with explicit owner approval via dedicated `sharky/<reason>` branch and PR with `sharky-mod` label.

**Q: Where should I put new code?**  
A: If it's a manager, use `BabySharkBot/Managers/`. If it's setup/config, use `BabySharkBot/Setup/`. If it's a build, use `BabySharkBot/Builds/`.

**Q: What if my code needs a new NuGet package?**  
A: Request approval from the owner with package name and version.

**Q: How do I prevent accidental commits to `Sharky/`?**  
A: Run the git skip-worktree commands in Section 1 and enable the pre-commit hook.

**Q: What if my AI assistant generates hallucinated code?**  
A: Use the prompt template in Section 6. Include exact file paths and snippets to anchor responses.

---

## References

- [Sharky Framework Repository](https://github.com/sharknice/Sharky)
- [PupusPistrixVectatorPestium Repository](https://github.com/YourOrg/PupusPistrixVectatorPestium)
- Contributing guidelines (see `CONTRIBUTING.md`)

---

**Last Updated:** [Current Date]  
**Document Owner:** Marc A Wysocki  
**Version:** 1.0
