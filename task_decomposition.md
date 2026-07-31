
## Task Decomposition: JIT Mining Optimization & Worker Allocation Routines[8D[K
Routines

### Phase 1: Mining System Deep Audit (Files: Tier 1)
- **Task 1**: Map `BabySharkMiningManager.cs` decision flow — identify all [K
miner assignment branches and fallback paths
- **Task 2**: Analyze `CustomMiningTask.cs` for worker selection logic, min[3D[K
mineral target selection, and collision avoidance
- **Task 3**: Review `JitPrepositionService.cs` — current prepositioning th[2D[K
thresholds, idle worker detection, and optimal position calculation
- **Task 4**: Audit `MineralReturnRateTrackerService.cs` — what metrics are[3D[K
are tracked (minerals per minute, walking distance, idle time)

### Phase 2: Worker Allocation Routine Design (Files: BabySharkBot/Managers[21D[K
BabySharkBot/Managers/, MicroTasks/)
- **Task 5**: Design new worker rotation system that assigns workers to min[3D[K
mineral patches in priority order:
  - Priority 1: Nearest large minerals (L-label confirmed)
  - Priority 2: Undug large minerals
  - Priority 3: Vespene gas workers (auto-detected via `VESPENE_V1_V2_QUICK[20D[K
`VESPENE_V1_V2_QUICK_REF.md`)
- **Task 6**: Implement team patch coordination — when multiple bots share [K
a mineral line, prevent worker overlap and starvation
- **Task 7**: Build fallback mining logic for when primary target is deplet[6D[K
depleted or blocked

### Phase 3: JIT Optimization Targets (Files: BabySharkBot/Services/)
- **Task 8**: Optimize `JitPrepositionService.cs` — add pre-positioning bas[3D[K
based on mineral depletion rate and worker count
- **Task 9**: Implement worker health monitoring — auto-reassign workers fr[2D[K
from damaged/mineral-depleted zones
- **Task 10**: Add Vespene gas optimization — prioritize high-yield gas pat[3D[K
patches (V2), reduce low-yield gas mining
- **Task 11**: Implement mineral return rate optimization — workers should [K
walk directly to base, not meander

### Phase 4: Validation & Testing (Files: BabySharkBot/Setup/, MicroTasks/)[12D[K
MicroTasks/)
- **Task 12**: Add benchmark tests for mining efficiency metric[6D[K
metrics (before vs after JIT optimization)
- **Task 13**: Implement miner assignment visualization/debug logging via `[1D[K
`ManagerDebugService.cs`

### Phase 5: Clean-Up & Documentation
- **Task 14**: Update all relevant `.md` documentation files with new JIT M[1D[K
Mining architecture
- **Task 15**: Remove unused mining-related code from the listed file paths[5D[K
paths (the many `*.md` and old worker label files)

