purpose maps, naming conventions, quirks

# BabyShark Codebase Domain Knowledge Transfer

## Repository Overview
BabyShark is a StarCraft II AI bot built on the Sharky framework, focusing [K
on advanced mining optimization, expansion management, and worker allocatio[9D[K
allocation systems. The codebase is heavily documented for AI-to-AI collabo[7D[K
collaboration.

## Critical File Purpose Map

| Category | Key Files | Purpose |
|----------|-----------|---------|
| **Core Bot** | `BabySharkBot/BabySharkBot.cs`, `Program.cs` | Main bot en[2D[K
entry point and lifecycle management |
| **Mining System** | `BabySharkBot/Managers/BabySharkMiningManager.cs`, `C[2D[K
`CustomMiningTask.cs` | Worker assignment, JIT mining logic |
| **Expansion** | `Services/ExpansionPointService.cs`, `ExpansionPointDrawS[20D[K
`ExpansionPointDrawService.cs` | Base location detection and expansion plan[4D[K
planning |
| **Worker Labeling** | `Setup/WorkerLabelChainHelper.cs`, `L_LABEL_IMPLEME[16D[K
`L_LABEL_IMPLEMENTATION_COMPLETE.md` | Near/Far mineral classification syst[4D[K
system |
| **RL Integration** | `RLIntegration/` directory | Reinforcement learning [K
training pipeline |

## Naming Convention Quirks (Critical for AI Understanding)

1. **`chrisCrossAppleSause.cs`**: This is NOT a typo - it's an intentional [K
playful naming convention in BabyShark. Used for worker initialization logi[4D[K
logic and mineral label registration. Do not "fix" this filename.
2. **`PupusPistrixVectatorPestium/`**: Subdirectory containing experimental[12D[K
experimental features, bot loading priority system, and dynamic manager con[3D[K
configuration.
3. **`BabySharkBot/Setup/` vs `BabySharkBot/Managers/`**: Setup files are i[1D[K
initialization/configuration, Managers are runtime systems.

## Worker Initialization Logic (chrisCrossAppleSause Pattern)
```
WorkerLabelRegistration → MineralClassification → MiningTaskAssignment → JI[2D[K
JITPreposition
```
This chain is documented in:
- `Setup/WorkerLabelChainHelper.cs` - Registration entry point
- `Setup/L_LABEL_IMPLEMENTATION_COMPLETE.md` - Label system status
- `Services/chrisCrossAppleSause.cs` - Core initialization service

## Microtask System Architecture
| Task Type | File | Responsibility |
|-----------|------|----------------|
| Mining | `CustomMiningTask.cs` | Worker mineral assignment |
| Scout | `BabySharkOverlordScoutTask.cs` | Overlord scouting coordination [K
|
| Team Patch | `TeamPatchMiningTask.cs` | Coordinated team mining operation[9D[K
operations |

## Documentation Index (Critical for AI Context)
- `EXPANSION_POINT_DRAWING_CHECKLIST.md` - Expansion visualization requirem[8D[K
requirements
- `MINERAL_LABEL_QUICK_SUMMARY.md` - Near/Far mineral classification rules
- `TOWNHALL_TECHNICAL_SUMMARY.md` - Townhall system integration guide
- `IMPLEMENTATION_STATUS.md` - Current development state

## Git Change Tracking Files (Critical vs Ignorable)

### CRITICAL: Must Review Before Any Code Changes
| File | Why Critical |
|------|-------------|
|
