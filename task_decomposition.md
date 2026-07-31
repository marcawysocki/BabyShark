For JIT mining optimization, the critical files are:
- `BabySharkBot/Managers/BabySharkMiningManager.cs` - Main scheduling logic[5D[K
logic
- `BabySharkBot/Services/JitPrepositionService.cs` - Core pre-position[12D[K
pre-positioning algorithm
- `BabySharkBot/Managers/CcaManager.cs` - Colony Cluster Analysis
- `BabySharkBot/MicroTasks/CustomMiningTask.cs` - Mining task implementatio[13D[K
implementation
- `BabySharkBot/Services/BaseLocationCalculationService.cs` - Base routing

The following files can be safely ignored for this analysis: all external b[1D[K
bot examples (`SharkyRandomExampleBot`, `SharkyZergExampleBot`, etc.), RL i[1D[K
integration files, documentation/markdown files (README, TODOs), setup file[4D[K
files (`L_LABEL_IMPLEMENTATION_COMPLETE.md`, etc.), and any unrelated micro[5D[K
microtask implementations like `VikingDropTask.cs`.

