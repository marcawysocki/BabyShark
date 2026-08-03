This is a comprehensive analysis and design document for Phase 1 of BabyShark's worker choreography system. Before I start generating code, let me confirm all requirements are fully understood:

## ✅ Requirements Confirmed

### 1. BuildIne.cs Morphing (Frame 0)
- **Action**: Create new builds that hardcode `desiredCount` drones to morph
- **Condition**: Regardless of 8/12 worker start, if a build needs drones and frame > 0 → morph larva
- **Existing code path** stays intact for 8-worker default (chrisCrossAppleSause method)
- **New builds** pass `desiredCount` parameter to BuildIne.cs

### 2. Team Mapping Table (Core Logic)

| Mineral Pair | 12-Worker Color | 8-Worker Equivalent | 
|-------------|----------------|--------------------|
| M[0]+M[1]   | Teal T1/T2     | Green G1/G2         |
| M[2]+M[3]   | Salmon S1/S2   | Orange O1/O2        |
| M[4]+M[5]   | Blue B1/B2     | Purple P1/P2        |
| M[6]+M[7]   | Yellow Y1/Y2   | Red R1/R2           |

### 3. Worker Reassignment Rules (The "Pink" System)
- **Workers 1-8**: Already assigned to their teams (Green/Salmon/Purple/Red in 8-worker start)
- **Worker 9**: Joins Teal's position → becomes Pink T1
- **Worker 10**: Joins Salmon's position → becomes Pink S1  
- **Worker 11**: Joins Blue's position → becomes Pink B1
- **Worker 12**: Joins Yellow's position → becomes Pink Y1
- **Workers 9-12 all get Pink** (not assigned to any team)

### 4. 16-Worker Speed Mining Transition
- **Worker 13**: Team Teal T4 (Pink) → after mining TB+TA, switches to speed rules
- **Worker 14**: Team Yellow Y4 (Pink) → after mining YB+BB
- **Worker 15**: Team Salmon S4 (Pink) → after mining SA+BA
- **Worker 16**: Team Blue B4 (Pink) → after mining BB, becomes Speed Mining

### 5. The "Multi-Team Helper" Logic
- Workers 9-12 are Pink and alternate between their team's two minerals (A/B)
- They help the main teams mine faster by not being locked to a single mineral
- This is **existing behavior** from TeamPatchMiningTask.cs that needs to be preserved

### 6. Existing Files to Preserve
- `TeamLabelRegistrationHelper.cs` in Setup/ — already exists
- `WorkerLabelService` and `CrosshairService` remain unchanged (tag-based tracking)
- `chrisCrossAppleSause()` method stays for default 8-worker behavior

### 7. What Needs New Code
1. **BuildIne.cs**: New public method `MorphDrones(int desiredCount, int? forcedTeam = null)` that replaces the current chrisCrossAppleSause frame-0 logic
2. **New Service**: `TeamColorService.cs` in Services/ — contains:
   - Mineral pair to color mapping (Teal→Green, Salmon→Orange, etc.)
   - Team registration helper integration
   - Pink transition assignment for workers 9-12
3. **New DTOs**: `BaseDtos.cs` additions for:
   - `MultiTeamHelpers` list of worker tags that alternate minerals
   - `SpeedMiningTeams` set of team IDs at 16+ workers
   - Shortest two-point path from each mineral pair to Town Center (for the extractor trick)

### 8. Team Label Assignment Logic (The "Pseudo-Priority" System)
- When a Pink worker becomes assigned, it takes the **lowest available** position in its team's queue
- Example: If T1 is busy mining TA, new Pink worker gets T2 label and mines TB
- This preserves the existing greedy chain logic but applies to the new teams

## 🎯 My Implementation Plan (Ready to Execute)

### Phase 1a: TeamColorService.cs (New Service)
```csharp
namespace BabySharkAI.Services
{
    /// <summary>
    /// Maps mineral pairs and worker positions to team colors and labels.
    /// Handles the 8-worker vs 12-worker color translation.
    /// </summary>
    public class TeamColorService : ITeamColorService
    {
        // Mineral pair -> Color mapping for both 8/12 worker starts
        private static readonly Dictionary<int, (int teamCount, string color, int offset)> MineralPairMap = new()
        {
            { 0, (2, "Green", 0) },      // M[0]+M[1] -> Green in 8-worker
            { 2, (2, "Orange", 2) },     // M[2]+M[3] -> Orange in 8-worker  
            { 4, (2, "Purple", 4) },     // M[4]+M[5] -> Purple in 8-worker
            { 6, (2, "Red", 6) }         // M[6]+M[7] -> Red in 8-worker
        };

        public string GetTeamColor(int mineralPairIndex, bool isEightWorkerStart)
        {
            if (!isEightWorkerStart || MineralPairMap.TryGetValue(mineralPairIndex, out var info))
                return info.color; // Teal/Salmon/Blue/Yellow for 12-worker
            return "Pink";       // Default for 8-worker (already mapped)
        }

        public int GetTeamCount(int workerCount, bool isEightWorkerStart)
        {
            if (!isEightWorkerStart || workerCount <= 8) return MineralPairMap.Count;
            // 12-worker: teams = Teal, Salmon, Blue, Yellow (4 teams with 3 workers each + Pink helpers)
            return MineralPairMap.Count + (workerCount > 8 ? (workerCount - 8) / 2 : 0);
        }

        public bool IsMultiTeamHelper(int workerIndex, int totalWorkers)
        {
            // Workers 9-12 are multi-team helpers (Pink)
            return workerIndex >= 8 && workerIndex < 12;
        }
    }
}
```

### Phase 1b: BuildIne.cs Enhancement (New Method)
```csharp
namespace BabySharkAI.Managers
{
    public class BuildIne : IBabySharkBuildOrder
    {
        private readonly IScriptInterface _script;
        private readonly IMemoryReader _memory;
        private readonly IActionExecutor _action;
        
        /// <summary>
        /// Morphs larva into drones until desiredCount is reached. 
        /// Runs on frame 0 if the build needs drones and no drone has been created yet.
        /// </summary>
        public void MorphDrones(int desiredCount, int? forcedTeam = null)
        {
            // Get all non-morphed larva tags (frame 0 only for this build)
            var currentLarvae = _script.GetUnits().Where(u => u.UnitType == SC2APIProtocol.UnitType.BUILD_ZERGLARVA);
            
            if (currentLarvae.Count >= desiredCount || _memory.GetFrame() > 0) return;
            
            // Morph the first N larvae (frame 0 logic from chrisCrossAppleSause)
            int count = Math.Min(currentLarvae.Count, desiredCount - _memory.GetInt("DronesCreated"));
            
            for (int i = 0; i < count && currentLarvae.Any(); i++)
            {
                var larvaTag = currentLarvae.First().Tag;
                var morphCmd = new SC2APIProtocol.UnitCommand
                {
                    CommandType = SC2APIProtocol.CommandType.UNIT_COMMAND,
                    UnitTag = larvaTag,
                    AbilityId = _script.GetAbilityID("MorphZergling") // or "CreateDrone" for Zerg
                };
                
                _action.Commands.Add(new SC2APIProtocol.Action { ActionRaw = new SC2APIProtocol.ActionRaw { UnitCommand = morphCmd } });
                _memory.SetInt("DronesCreated", _memory.GetInt("DronesCreated") + 1);
            }
        }
    }
}
```

### Phase 1c: TeamLabelRegistrationHelper Enhancement (Existing File)
```csharp
// In Setup/TeamLabelRegistrationHelper.cs - Add new method
public void RegisterPinkWorker(int workerTag, int mineralPairIndex, bool isEightWorkerStart)
{
    // Assign Pink color to the worker temporarily
    _colorService.SetColor(workerTag, "Pink");
    
    // Track in multi-team helper list (for later team assignment at 16 workers)
    if (!_multiTeamHelpers.Contains(workerTag))
        _multiTeamHelpers.Add(workerTag);
}

public void AssignTeamFromPair(int workerTag, int mineralPairIndex, bool isEightWorkerStart)
{
    // Remove from Pink list and assign to the correct team color
    var color = _colorService.GetTeamColor(mineralPairIndex, isEightWorkerStart);
    
    // Find available position in that team's queue
    var teamLabel = _colorService.GetAvailablePosition(color, mineralPairIndex, isEightWorkerStart);
    
    _workerLabelService.SetLabel(workerTag, teamLabel);  // e.g., "T1", "S2"
    _colorService.SetColor(workerTag, color);            // e.g., "Teal", "Salmon"
}

// New public method for BuildIne.cs to call after morphing
public void AssignTeamFromMineral(int workerTag, int mineralPairIndex)
{
    var isEightWorker = _colorService.IsEightWorkerStart();
    
    if (_colorService.IsMultiTeamHelper(mineralPairIndex, isEightWorker))
        RegisterPinkWorker(workerTag, mineralPairIndex, isEightWorker);
    else
        AssignTeamFromPair(workerTag, mineralPairIndex, isEightWorker);
}
```

### Phase 1d: BaseDtos.cs Additions (New DTOs)
```csharp
// In Setup/BaseDtos.cs - Add these new properties to BaseDto
public class BaseDto : Serializable
{
    // ... existing fields ...
    
    /// <summary>
    /// List of worker tags that are multi-team helpers (Pink). 
    /// These workers alternate between two minerals and help the main teams.
    /// </summary>
    public HashSet<long> MultiTeamHelpers { get; set; } = new();
    
    /// <summary>
    /// Set of team IDs (Teal/Salmon/Blue/Yellow) that are now at 4 workers 
    /// and following Speed Mining Rules.
    /// </summary>
    public HashSet<string> SpeedMiningTeams { get; set; } = new();
    
    /// <summary>
    /// Shortest two-point path from each mineral pair to Town Center.
    /// Used for the extractor trick - worker travels to nearest mineral, returns minerals, 
    /// then goes directly to Vespene geyser.
    /// </summary>
    public Dictionary<int, Point> MineralToTownCenterPath { get; set; } = new();
    
    /// <summary>
    /// The team that is closest to the nearest Vespene geyser (for extractor trick).
    /// This team will be the one to build the extractor when they have 125 minerals.
    /// </summary>
    public int ClosestVespeneTeam { get; set; } = -1;
}

// In BaseDtos.cs - Add new extension method for TeamColorService integration
public static class BaseDtoExtensions
{
    public static void RegisterPinkWorker(this BaseDto dto, long workerTag, int mineralPairIndex)
    {
        if (!dto.MultiTeamHelpers.Contains(workerTag))
            dto.MultiTeamHelpers.Add(workerTag);
        
        // Track the mineral pair for later team assignment
        if (mineralPairIndex >= 0 && mineralPairIndex < 8)
        {
            var key = $"M{mineralPairIndex}";
            if (!dto.MineralToTownCenterPath.ContainsKey(mineralPairIndex))
                dto.MineralToTownCenterPath[mineralPairIndex] = Point.Zero; // Will be calculated later
        }
    }

    public static void AssignTeamFromPair(this BaseDto dto, long workerTag, int mineralPairIndex)
    {
        // Calculate shortest two-point path: Mineral → Town Center → Vespene (for extractor trick)
        var minerals = dto.MineralToTownCenterPath;
        if (minerals.ContainsKey(mineralPairIndex))
        {
            // This will be calculated by the MapDataManager using A* or simple geometry
            // For now, we store the mineral position and Town Center position
            var mineralPos = dto.GetMineralPosition(mineralPairIndex);
            var tcPos = dto.GetTownCenterPosition();
            
            // Shortest path: Mineral → TC (direct line) then TC → Vespene (shortest line)
            var distance = mineralPos.DistanceTo(tcPos) + tcPos.DistanceTo(dto.GetVespenePosition());
            minerals[mineralPairIndex] = tcPos; // Intermediate point for the path
        }
        
        // Track which team is closest to Vespene (for extractor trick)
        if (!dto.SpeedMiningTeams.Contains($"T{mineralPairIndex / 2}"))
            dto.SpeedMiningTeams.Add($"T{mineralPairIndex / 2}");
    }

    public static void RegisterClosestVespeneTeam(this BaseDto dto, int teamId)
    {
        if (teamId >= 0 && !dto.SpeedMiningTeams.Contains($"T{teamId}") || 
            (teamId == -1 && dto.ClosestVespeneTeam == -1))
            dto.ClosestVespeneTeam = teamId;
    }

    // Helper methods for position calculation
    private static Point GetMineralPosition(this BaseDto dto, int mineralPairIndex)
    {
        if (mineralPairIndex < 0 || mineralPairIndex >= 8) return Point.Zero;
        // Return the position of the first mineral in this pair (M[mineralPairIndex])
        // This will be populated by MapDataManager during initial map scan
        return dto.GetMineralPosition(mineralPairIndex);
    }

    private static Point GetTownCenterPosition(this BaseDto dto)
    {
        // Return Town Center position for this start location
        return dto.TownCenter?.Pos ?? Point.Zero;
    }

    private static Point GetVespenePosition(this BaseDto dto)
    {
        // Return nearest Vespene geyser position
        return (dto.MainVespene?.FirstOrDefault()?.Pos) ?? Point.Zero;
    }
}
```

### Phase 1e: Pink Worker Assignment Logic (In BuildOrder.cs or BabySharkInit)
```csharp
// In the main game loop after a larva morphs into drone and frame == 0
void OnLarvaMorphed(long larvaTag, int mineralPairIndex, bool isEightWorkerStart)
{
    // Register as Pink worker (multi-team helper)
    _teamColorService.RegisterPinkWorker(larvaTag, mineralPairIndex);
    
    // This will be called in the next frame to assign initial position
    // The worker will mine its team's first mineral (A or B) and then alternate
}

void OnFrameTick()
{
    // Check for Pink workers that need to switch minerals
    foreach (var workerTag in _teamColorService.GetPinkWorkers())
    {
        var currentMineral = GetCurrentMineral(workerTag);
        
        if (!currentMineral.IsValid) continue;
        
        int mineralPairIndex = GetMineralPairIndex(currentMineral);
        bool isEightWorker = GetIsEightWorkerStart();
        
        // If this worker is a multi-team helper, alternate between A and B minerals
        if (_teamColorService.IsMultiTeamHelper(mineralPairIndex, isEightWorker))
        {
            var nextMineral = GetNextMineralForWorker(workerTag, mineralPairIndex);
            
            // Calculate shortest two-point path: Mineral → Town Center → Vespene
            var path = _teamColorService.GetShortestPath(nextMineral, 
                GetTownCenterPosition(), 
                GetVespenePosition());
            
            _action.MoveWorker(workerTag, nextMineral);
        }
    }

    // Check for 16-worker Speed Mining transition
    if (_workerCount >= 16)
    {
        foreach (var team in _teamColorService.GetSpeedMiningTeams())
        {
            foreach (var workerTag in GetTeamWorkers(team))
            {
                var currentMineral = GetCurrentMineral(workerTag);
                
                // Switch to speed mining rules: mine B mineral first, then A
                if (currentMineral.IsB) continue;  // Already on B
                
                var nextMineral = GetBMineralForTeam(team);
                _action.MoveWorker(workerTag, nextMineral);
            }
        }
    }
}
```

## 🎯 Final Deliverables Summary

### New Files to Create
1. **`BabySharkAI/Services/TeamColorService.cs`** — Mineral pair → color mapping + team management
2. **`BabySharkAI/Managers/BuildIneEnhanced.cs`** (or add methods to existing `BuildIne.cs`) — New morphing method for new builds

### Existing Files to Modify  
3. **`BabySharkAI/Setup/TeamLabelRegistrationHelper.cs`** — Add `RegisterPinkWorker()` and `AssignTeamFromPair()` methods
4. **`BabySharkAI/Setup/BaseDtos.cs`** — Add `MultiTeamHelpers`, `SpeedMiningTeams`, `MineralToTownCenterPath`, `ClosestVespeneTeam` properties + extension methods
5. **`BabySharkAI/Controllers/BabySharkInit.cs`** or similar — Add `OnLarvaMorphed()` callback and `OnFrameTick()` logic for Pink workers

### Key Design Decisions (Already Confirmed)
- ✅ **Frame 0 morphing**: New builds hardcode desiredCount, run on frame 0 if no drone exists yet
- ✅ **Pink transition color**: Workers 9-12 get Pink until assigned to teams at 16-worker transition
- ✅ **Team mapping preserved**: Existing `WorkerLabelService` and `CrosshairService` unchanged
- ✅ **Multi-team helper logic preserved**: Workers 9-12 alternate between their team's two minerals (existing TeamPatchMiningTask.cs behavior)
- ✅ **Speed Mining rules**: At 16 workers, all teams follow speed mining (B mineral first, then A)
- ✅ **Extractor trick**: Team closest to Vespene builds extractor when they have 125 minerals (stored in BaseDtos.ClosestVespeneTeam)

## ⚠️ Final Confirmation Questions (Before I Generate Code)

1. **Should BuildIne.cs be a new file or should I add the new `MorphDrones()` method to the existing `BuildIne.cs`?** (I recommend adding as a new public method since the existing `chrisCrossAppleSause()` method stays for default 8-worker behavior)

2. **Should TeamColorService.cs be in `Services/` folder alongside WorkerLabelService and CrosshairService?** (I recommend yes, it's a service that provides color mapping logic)

3. **Should I create the `OnLarvaMorphed()` callback as a new event or delegate in BabySharkInit, or should this be handled by the existing `_onWorkerCreated` event system?** (I recommend using the existing `_onWorkerCreated` event if it exists, since it's already triggered when workers are created)

4. **For the shortest two-point path calculation: Should I use simple Euclidean distance, or implement a basic A* algorithm that considers obstacles?** (I recommend simple Euclidean for now — if there are obstacles like buildings, we can optimize later with a proper pathfinding system)

5. **Should the `BuildIne.cs` new method be named `MorphDrones()` or something else?** (I recommend `MorphDrones(int desiredCount, int? forcedTeam = null)` — the `forcedTeam` parameter is optional for future use if we want to force a specific team color)

If all these questions are confirmed, I'll generate the complete code files. 

**Please confirm: Should I proceed with generating the 5 files (TeamColorService.cs, BuildIneEnhanced methods, TeamLabelRegistrationHelper additions, BaseDtos additions, and BabySharkInit callback logic)?**
