### FILE INDEX

Based on the architecture analysis, here are the necessary files for implementing the greedy chain color assignment:

#### Core Implementation Files (BabyShark-specific)
- `Services/chrisCrossAppleSause.cs` - Worker initialization service that needs updated greedy ordering logic
- `Managers/BabySharkMiningManager.cs` - Mining manager containing the greedy mineral ordering algorithm
- `Setup/InitialMapData.cs` - Map data generator that creates OrderedMainMinerals structure

#### Supporting Files
- `MicroTasks/` directory (contains mining task implementations)
- `Services/JitPrepositionService.cs` - Worker positioning service used by mining manager
- Any DTO files in `Setup/` directory defining the OrderedMainMinerals structure

#### Framework Files to Ignore
- All files from `sharknice/Sharky` dependency unless specifically referenced for debugging
- Base StarCraft II API files
- Standard library dependencies
