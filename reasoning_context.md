## BabyShark Systems Context & JIT Mining Architecture

### 1. Entry Point & Lifecycle (BabySharkBot.cs)
- **Entry**: `BabySharkBot/BabySharkBot.cs` - main bot class implementing t[1D[K
the StarCraft II bot API lifecycle (`OnGameStart`, `OnStep`, etc.)
- **Key Pattern**: `BabySharkBot.cs.md` documents the full lifecycle flow i[1D[K
including initialization → game loop → unit processing
- **Critical Flow**: `ProcessVisibleUnits()` is called every game step to i[1D[K
identify worker/mineral/overlord units → triggers mining task assignments

### 2. Mining Manager (the JIT brain)
- **File**: `BabySharkBot/Managers/BabySharkMiningManager.cs` —
