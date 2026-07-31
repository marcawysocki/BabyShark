clean Markdown format

The user provided a massive repository ground truth context (a complete fil[3D[K
file registry). I need to analyze this and identify what's relevant for Bab[3D[K
BabyShark development tasks.

Looking at the key BabyShark files:
- `BabySharkBot.cs` - main bot file
- `BabySharkBot.cs.md` - documentation of BabySharkBot
- `BabySharkBot/Program.cs` - entry point
- `BabySharkBot/MicroTasks/CustomMiningTask.cs` - custom mining task implem[6D[K
implementation
- `BabySharkBot/MicroTasks/BabySharkOverlordScoutTask.cs` - overlord scout [K
task
- `BabySharkBot/MicroTasks/TeamPatchMiningTask.cs` - team patch mining task[4D[K
task


- `BabySharkBot/Managers/CcaManager.cs` - manager for chrisCrossAppleSause [K
service
- `BabySharkBot/Managers/BabySharkUnitManager.cs` - unit management
- `BabySharkBot/Managers/ManagerDebugService.cs` - debugging service
- `BabySharkBot/Managers/BabySharkMiningManager.cs` - mining management (co[3D[K
(core)
- `BabySharkBot/Managers/DrawOnlyManager.cs` - drawing operations

The key BabyShark services include various expansion and location calculati[9D[K
calculation services like ExpansionPointService, BaseLocationCalculationSer[26D[K
BaseLocationCalculationService, JitPrepositionService for JIT pre-positioni[13D[K
pre-positioning logic, and ProvisionalExpansionService. There's also the co[2D[K
core chrisCrossAppleSause service implementation along with its documentati[11D[K
documentation version. Mineral tracking is handled by MineraReturnRateTrack[21D[K
MineraReturnRateTrackerService and mineral-related files like MINERAL_LABEL[13D[K
MINERAL_LABEL_COMPLETION.md that document the mineral classification system[6D[K
system.

The setup layer includes several critical components: MapDataManager for ma[2D[K
managing game state, OngoingMapData and SecondaryMapData for dynamic inform[6D[K
information, ProcessVisableUnits.cs both as implementation and documentatio[12D[K
documentation, WorkerLabelChainHelper for the worker label chain system, an[2D[K
and various registration helpers like MapLabelRegistrationHelper and TeamLa[6D[K
TeamLabelRegistrationHelper. The foundational files are InitialMapData and [K
BaseDtos for data
