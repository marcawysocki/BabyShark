The BabySharkBot JIT mining system is built around a sophisticated **worker[8D[K
**worker labeling chain** (L_LABEL_IMPLEMENTATION_COMPLETE.md) that tracks [K
worker state across three levels: initial label → working label → final min[3D[K
mining target. The key files forming this pipeline are `BabySharkBot.cs` (e[2D[K
(entry point), `Managers/BabySharkMiningManager.cs` (central coordinator), [K
`MicroTasks/CustomMiningTask.cs` (mining task executor), and `Services/JitP[14D[K
`Services/JitPrepositionService.cs` (worker pre-positioning logic). The sys[3D[K
system also includes dynamic juggling (`DYNAMIC_WORKER_JUGGLING_SYSTEM.md`)[37D[K
(`DYNAMIC_WORKER_JUGGLING_SYSTEM.md`) for handling mineral depletion and a [K
`MineralReturnRateTrackerService.cs` that tracks mining efficiency. Common [K
JIT mining errors in StarCraft II bots include: (1) workers getting stuck o[1D[K
on depleted mines after depletion detection fails to update labels fast eno[3D[K
enough, (2) label conflicts where the same worker is simultaneously assigne[7D[K
assigned by multiple task types competing for priority, (3) prepositioning [K
race conditions where workers arrive at a mine before it's properly flagged[7D[K
flagged as mineable, and (4) resource contention with expansion placement l[1D[K
logic that may try to assign workers for PDS/Provisional expansion tasks si[2D[K
simultaneously. The JIT optimization targets should focus on: implementing [K
mineral depletion rate tracking per patch (not just binary depleted/not-dep[16D[K
depleted/not-depleted), adding worker state machine transitions (idle → pre[3D[K
prepositioning → mining → idle) to prevent stale assignments, and decouplin[9D[K
decoupling the mining label chain from other bot subsystems that might try [K
to override labels mid-task.
