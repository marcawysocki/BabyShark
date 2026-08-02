The user wants me to analyze their codebase structure and create a set of f[1D[K
files for KimiK3 to execute. This is a three-AI-workflow where I first anal[4D[K
analyze their code page, then create KimiK3 instructions that Kimi will use[3D[K
use to perform four specific tasks:

1. Create a bool `CreateWorkerFrameZero` in setting.cs and set it to true; [K
In chrisCrossAppleSause if that is true then on frame 0 select a Larva and [K
morph that into a drone.
2. Modify MineralReturnRateTrackerService.cs so reports include workers 8-1[3D[K
8-11 instead of 12-16 (start at 8)
3. Build a BabyShark replacement of Sharky Build order system - see referen[7D[K
references: SharkyBuildOrderSystem.md, "Buildtest.cs Hatchery Walloff.MD", [K
and the contents of C:\Users\marca\source\repos\BabyShark\BabySharkBot\Buil[55D[K
C:\Users\marca\source\repos\BabyShark\BabySharkBot\Builds (which is outdate[7D[K
outdated from another project file)
4. At or after Frame 35 handoff from chrisCrossAppleSause, add BabySharkBui[12D[K
BabySharkBuild Manager

I need to follow strict rules:
- Prioritize BabyShark custom scripts and implementation files first
- Filter out base framework (sharknice/Sharky) unless critically broken or [K
directly referenced
- Output exactly three distinct structural sections with specific delimiter[9D[K
delimiter tokens
- Write everything in clear English, no Chinese characters

Let me analyze the repository structure based on the provided file registry[8D[K
registry. Key BabyShark files I need to reference:

**BabySharkBot/Setup/Settings.cs** - Contains game settings and configurati[11D[K
configuration (Task 1 goes here)
**BabySharkBot/Services/chrisCrossAppleSause.cs** - Worker initialization s[1D[K
service (Task 1 frame 0 morph logic)
**BabySharkBot/Services/MineralReturnRateTrackerService.cs** - Mineral trac[4D[K
tracking for workers 8-16 (Task 2, change range to 8-11)
**BabySharkBot/BabySharkBot.cs** - Main bot entry point (Task 4 handoff at [K
frame 35)
**BabySharkBot/Managers/** - BabySharkUnitManager.cs, BabySharkMiningManage[21D[K
BabySharkMiningManager.cs (Task 4 new manager)
**BabySharkBot/Builds/** - BuildTest.cs exists but is outdated (Task 3 refe[4D[K
reference)
**SharkyBuildOrderSystem.md** - Reference document for Sharky build system [K
that needs replacement

Key existing patterns to follow from the BabyShark architecture:
- `chrisCrossAppleSause` = worker initialization service (already named cro[3D[K
cross-legged/attention metaphor)
- `BabySharkMiningManager.cs` - stateful orchestrator pattern to follow
- `WorkerLabelService` for tracking workers across frames
- `InitialMapData` and `MapDataManager` for map data persistence
- Manager pattern: stateful orchestrators in Man
