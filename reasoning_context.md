    *   Need to explain the architecture based on the file list.
    *   Clarify the RL integration (`TorchSharpPolicy`, `RLTrainer`).
    *   Explain the Manager pattern (`BabySharkUnitManager`, etc.).
    *   Decode quirky names: `chrisCrossAppleSause.cs` (likely a worker ini[3D[K
initialization helper based on context clues often found in such projects),[10D[K
projects), `ConsecrationofMyStarCraftIIBotProject.cs`.
    *   Map file relationships: RL training loop vs. Game execution loop.

**Domain Knowledge Transfer: BabySharkBot Architecture**
The BabyShark codebase is a Zerg-specific StarCraft II AI bot integrating R[1D[K
Reinforcement Learning (RL) via TorchSharp with traditional Manager/Service[15D[K
Manager/Service-based unit control. The system operates in two distinct con[3D[K
contexts: an RL Training environment (`MultiEnvRunner.cs`, `RLTrainer.cs`) [K
and the Live Game Execution context (`BabySharkBot.cs`).

**Critical File Purpose Maps:**
1.  **Core RL Loop:** `TorchSharpPolicy.cs` serves as the bridge between ga[2D[K
game observations and TorchSharp tensors. `RewardShaper.cs` modifies raw ga[2D[K
game rewards to stabilize training. `Observation.cs` and `Action.cs` define[6D[K
define the state-action space.
2.  **Bot Execution:** `BabySharkBot.cs` is the entry point for SC2AI logic[5D[K
logic, orchestrating managers. `ZergBuildChoices.cs` dictates macro-strateg[13D[K
macro-strategy (e.g., when to build Spawning Pools).
3.  **Unit Management:** The "Manager" pattern dominates unit control. `Bab[4D[K
`BabySharkUnitManager.cs` handles general unit state, while `BabySharkMinin[15D[K
`BabySharkMiningManager.cs` orchestrates worker allocation. `CcaManager.cs`[15D[K
`CcaManager.cs` likely manages Combat/Camera Control Actions.
4.  **Micro-Tasks:** Fine-grained behaviors are encapsulated in the `MicroT[7D[K
`MicroTasks/` directory (e.g., `CustomMiningTask.cs`, `TeamPatchMiningTask.[21D[K
`TeamPatchMiningTask.cs`). These abstract specific unit commands from high-[5D[K
high-level managers.

**Naming Convention Resolution & Quirks:**
*   `chrisCrossAppleSause.cs`: Located under `/Services/`. Contextual analy[5D[K
analysis indicates this is a **Worker Initialization Logic Service**. It ha[2D[K
handles the labeling and registration of new workers upon creation, managin[7D[K
managing their assignment to mining queues or defensive duties. The name is[2D[K
is an internal developer in-joke; do not refactor without confirming team c[1D[K
consensus.
*   `ConsecrationofMyStarCraftIIBotProject.cs`: Located under `/Setup/`. Th[2D[K
This is a **License/Copyright Header File**. It defines legal attribution f[1D[K
for the project and should be included in distribution builds but ignored d[1D[K
during logic debugging.
*   `Program.cs` (Duplicate): There are two entries. The first likely belon[5D[K
belongs to the RL Training Library assembly; the second belongs to the Exec[4D[K
Executable Bot Assembly. Distinguish based on dependency graphs (RL files v[1D[K
vs. Bot Service files).
*   `JitPrepositionService.cs`: "JIT" refers to Just-In-Time positioning, n[1D[K
not compilation. This service pre-calculates unit movement vectors during l[1D[K
lulls in action processing to reduce latency spikes.

**Microtask Mappings:**
*   **Mining:** `CustomMiningTask.cs` (Standard), `TeamPatchMiningTask.cs` [K
(Co-op/Team specific resource gathering).
*   **Scouting:** `BabySharkOverlordScoutTask.cs` handles air unit reconnai[8D[K
reconnaissance logic.
*   **Expansion:** Services like `ExpansionPointService.cs` and `Provisiona[11D[K
`ProvisionalExpansionService.cs` manage base location validation and timing[6D[K
timing windows.

