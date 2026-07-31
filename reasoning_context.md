BabySharkBot is a StarCraft II bot that has invested significant effort int[3D[K
into an advanced mining automation system built around JIT (Just-In-Time) p[1D[K
pre-positioning. The key architecture involves three layers:

**Layer 1 - Mineral Tracking & Classification:** `BabySharkBot/Managers/Bab[26D[K
`BabySharkBot/Managers/BabySharkMiningManager.cs` and `MineralReturnRateTra[21D[K
`MineralReturnRateTrackerService.cs`. The bot tracks each mineral patch ind[3D[K
individually with a return rate tracker that monitors how quickly minerals [K
are delivered to the base. This enables it to identify which patches have s[1D[K
stopped producing (idle) vs active ones.

**Layer 2 - JIT Prepositioning:** `BabySharkBot/Services/JitPrepositionServ[41D[K
`BabySharkBot/Services/JitPrepositionService.cs`. This is the core optimiza[8D[K
optimization engine. It uses Colony Cluster Analysis (`CcaManager.cs`) to c[1D[K
cluster mineral patches into groups based on their proximity and calculates[10D[K
calculates optimal pre-position points where miners can queue for multiple [K
harvests. The service continuously adjusts miner assignments based on retur[5D[K
return rate changes.

**Layer 3 - Microtask System:** `BabySharkBot/MicroTasks/CustomMiningTask.c[43D[K
`BabySharkBot/MicroTasks/CustomMiningTask.cs` is the main mining task imple[5D[K
implementation, with `TeamPatchMiningTask.cs` for team-based farming and `B[2D[K
`BabySharkOverlordScoutTask.cs` for scouting-related mining (minerals near [K
overlord).

**Key JIT Mining Pattern:** Instead of having miners constantly travel betw[4D[K
between base and patches, JIT pre-positioning keeps them at optimized waypo[5D[K
waypoints where they can:
- Queue for 2-3 harvests from the same patch before returning to base
- Dynamically reassign based on which patches are running low (tracked by r[1D[K
return rate)
- Use Colony Cluster Analysis to minimize total travel distance

The system is designed around **return-rate-driven allocation**: workers ar[2D[K
are continuously reallocated from slower patches to faster ones, and idle t[1D[K
time is minimized through pre-positioning rather than reactive assignment.

