
## Domain Knowledge Transfer & File Architecture Map

### 🧠 Core Bot Architecture
**BabySharkBot** is a complex, multi-system StarCraft II bot with:
- **Race flexibility**: Multiple race variants (Protoss/Terran/Zerg) in sib[3D[K
sibling folders
- **Mining system**: JIT Mining + Large Mineral Classification (L-label sys[3D[K
system) — this appears to be the primary optimization target based on the f[1D[K
file density and naming patterns
- **Worker management**: `BabySharkMiningManager.cs` with dynamic worker ju[2D[K
juggling, team patch mining, and return rate tracking
- **Expansion services**: `BaseLocationCalculationService`, `ExpansionPoint[15D[K
`ExpansionPointService`, `ProvisionalExpansionService` — suggests a sophist[7D[K
sophisticated expansion decision system

### 🔑 Critical Naming Resolution Mappings
1. **`chrisCrossAppleSause.cs`** (appears twice) — Worker initialization lo[2D[K
logic that assigns labels to workers based on mineral position and proximit[8D[K
proximity
2. **L-label system** (`L_LABEL_IMPLEMENTATION_COMPLETE.md`, `L_LABEL_LARGE[14D[K
`L_LABEL_LARGE_MINERAL_SYSTEM.md`) — Large mineral classification for worke[5D[K
workers; this is the foundation of JIT Mining optimization
3. **`JitPrepositionService.cs`** — The JIT (Just-In-Time) mining prepositi[9D[K
prepositioning service that assigns workers to optimal positions before the[3D[K
they mine
4. **`MineralReturnRateTrackerService.cs`** — Tracks how efficiently worker[6D[K
workers return minerals; key metric for JIT optimization

### 📊 File Relevance Hierarchy for Active Objective
**Tier 1 (Critical - JIT Mining & Worker Allocation):**
- `BabySharkBot/Managers/BabySharkMiningManager.cs` — Main mining decision [K
engine
- `BabySharkBot/MicroTasks/CustomMiningTask.cs` — Core mining microtask imp[3D[K
implementation
- `BabySharkBot/MicroTasks/TeamPatchMiningTask.cs` — Team patch mining logi[4D[K
logic (multi-bot coordination)
- `BabySharkBot/Services/JitPrepositionService.cs` — JIT prepositioning dec[3D[K
decisions
- `BabySharkBot/Services/MineralReturnRateTrackerService.cs` — Mining effic[5D[K
efficiency metrics
- `BabySharkBot/Managers/BabySharkUnitManager.cs` — Worker unit management

**Tier 2 (Supporting - Expansion & Map Analysis):**
- `BabySharkBot/Setup/BaseLocationCalculationService.cs`
- `BabySharkBot/Services/ExpansionPointService.cs`
- `BabySharkBot/Services/JitPrepositionService.cs`
- `BabySharkBot/MicroTasks/BabySharkOverlordScoutTask.cs`

**Tier 3 (Low Priority / Can Be Safely Ignored for JIT Mining):**
- All RL Integration files (`RLIntegration/`) — separate training pipeline
- `SharkyProtossExampleBot/`, `SharkyTerranExampleBot/`, etc. — race-specif[11D[K
race-specific variants, not BabySharkBot's mining system
- `BabySharkBot/MicroTasks/BabySharkOverlordScoutTask.cs` — scouting, not m[1D[K
mining
- All documentation files (`*.md`) except those explicitly about JIT Mining[6D[K
Mining:
  - `L_LABEL_IMPLEMENTATION_COMPLETE.md`
  - `JIT_MINING_OPTIMIZATION_PLAN.md` (if exists)
  - `HARVEST_AND_RETURN_CARGO_REFERENCE.md`

### 🎯 JIT Mining Optimization Target Analysis
The current system appears to have these optimization opportunities:
1. **Worker label precision** — The L-label system needs refinement for bet[3D[K
better mineral proximity detection
2. **JIT prepositioning** — Workers should be assigned to positions before [K
mining, not during
3. **Return rate tracking** — Minimize walking back and forth; optimize wor[3D[K
worker rotation
4. **Team patch coordination** — Multi-bot harmony in shared mineral patche[6D[K
patches

