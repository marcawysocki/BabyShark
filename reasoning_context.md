naming conventions

## BabyShark Repository Architecture & Domain Knowledge Map

### 1. Project Identity
- **Project**: BabyShark Bot — a StarCraft II (LadderBots-compatible) AI bo[2D[K
bot with heavy emphasis on **custom mining systems**, mineral classificatio[13D[K
classification, JIT mining optimization, and microtask management.
- **Language**: C# (.NET, likely targeting SC2 LadderBots runtime).
- **Key Files**: `BabySharkBot.cs`, `BabySharkMiningManager.cs`, `CustomMin[10D[K
`CustomMiningTask.cs`, `chrisCrossAppleSause.cs`.

### 2. Mining System Architecture (Core Domain)
The repository is dominated by a sophisticated mining pipeline:
- **`BabySharkBot/MicroTasks/CustomMiningTask.cs`** — Main custom mining ta[2D[K
task implementation.
- **`BabySharkBot/BabySharkMiningManager.cs`** — Central mining manager coo[3D[K
coordinating all worker assignments.
- **`BabySharkBot/MicroTasks/TeamPatchMiningTask.cs`** — Team-patch-aware m[1D[K
mining (Zerg-specific).
- **`BabySharkBot/Services/JitPrepositionService.cs`** — JIT pre-positionin[14D[K
pre-positioning service for optimized mineral access.
- **`BabySharkBot/BabySharkBot.cs`** — Main bot entry point with mining orc[3D[K
orchestration.

### 3. Mineral Classification System (Major Subsystem)
A dedicated, multi-layered classification system appears in `BabySharkBot/S[15D[K
`BabySharkBot/Setup/`:
- **`L_LABEL_IMPLEMENTATION_COMPLETE.md`** / **`L_LABEL_LARGE_MINERAL_SYSTE[30D[K
**`L_LABEL_LARGE_MINERAL_SYSTEM.md`** — Label implementation for large mine[4D[K
minerals (L-label).
- **`MINERAL_CLASSIFICATION_IMPROVEMENTS.md`** / **`MINERAL_CLASSIFICATION_[26D[K
**`MINERAL_CLASSIFICATION_COUNTS_GUIDE.md`** — Classification methodology d[1D[K
docs.
- **`WorkerLabelChainHelper.cs`** / **`MapLabelRegistrationHelper.cs`** — W[1D[K
Worker label chain and registration helpers.
- **`BabSharkBot/Services/chris
