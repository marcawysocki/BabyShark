This is a comprehensive analysis request covering multiple layers: code tasks, team color mapping for both 8 and 12 worker starts, and future macro/hatch placement planning. Let me break this down systematically.

## Phase 1: Analysis & Team Mapping Table

### Current Architecture (from BabyShark context)
- **Team Label Service**: Tracks workers by tag with labels like H1, OV1, L1 for static units and W1-W12 for dynamic workers
- **Worker Assignment Logic**: 
  - Si=0: Average X,Y of all minerals = COM (Center Of Mining)
  - Find furthest worker from COM = W1 (gets label D1 or W12)
  - Remaining workers: greedy chain closest to previous → W2, W3...W12
- **Multi-Location Data**: BaseDtos stores OrderedMainMinerals with M[0]-M[7] in greedy order

### Team Mapping: 8 Worker Start vs 12 Worker Start

| 12-Worker Mineral Pair | 12-Worker Color/Team | 8-Worker Color/Team (When 3rd worker joins) |
|-----------------------|--------------------|-------------------------------------------|
| M[0] + M[1]           | **Teal** T1/T2     | **Green** G1/G2                             |
| M[2] + M[3]           | **Salmon** S1/S2   | **Orange** O1/O2                            |
| M[4] + M[5]           | **Blue** B1/B2     | **Purple** P1/P2                            |
| M[6] + M[7]           | **Yellow** Y1/Y2   | **Red** R1/R2                               |

### Team Mapping: 8 Worker → New Worker Reassignment Rules

| Current Worker Count | New Worker Joins As       | 12-Worker Color Equivalent | Mineral Pair Used |
|---------------------|--------------------------|---------------------------|-------------------|
| 0-2                 | Teams already exist (G/S/P/R) | N/A                       | N/A               |
| 3                   | **Teal** T1/T2 → G1/G2   | Teal                      | M[0]+M[1]         |
| 4                   | **Salmon** S1/S2 → O1/O2 | Salmon                    | M[2]+M[3]         |
| 5                   | **Blue** B1/B2 → P1/P2   | Blue                      | M[4]+M[5]         |
| 6                   | **Yellow** Y1/Y2 → R1/R2 | Yellow                    | M[6]+M[7]         |
| 7                   | (8th worker - team already full) | N/A                       | N/A               |
| 8                   | **Teal** T1/T2/T3        | Teal                      | M[0]+M[1]         |
| 9                   | **Salmon** S1/S2/S3      | Salmon                    | M[2]+M[3]         |
| 10                  | **Blue** B1/B2/B3        | Blue                      | M[4]+M[5]         |
| 11                  | **Yellow** Y1/Y2/Y3      | Yellow                    | M[6]+M[7]         |
| 12+                 | All teams now 4 workers (T/Y/B/R) - Speed Mining Rules apply |

### Key Insight: "Team Teal" in 8-Worker Context = Green Team
The mapping is essentially a color shift. In an 8-worker start, the first mineral pair (M[0]+M[1]) which would be Teal in 12-worker becomes **Green** (G1/G2). The mapping preserves team structure but shifts colors:

```
Teal   → Green    (first pair)
Salmon → Orange   (second pair)
Blue   → Purple   (third pair)
Yellow → Red      (fourth pair)
```

## Phase 2: Task Analysis - BuildIne.cs Larva Morphing

### Current Issue
`chrisCrossAppleSause` currently only morphs 1 larva per frame for 8-worker starts. Need to expand to handle both 8 and 12 worker starts, with proper worker labeling on new drones.

### Requirements Breakdown
**Task 1: BuildIne.cs - Morph Until DesiredUnitCount**
```csharp
// Current (simplified)
commands.Add(new SC2APIProtocol.Action { 
    ActionRaw = new ActionRaw { UnitCommand = morphCmd } });

// Need to change to loop until desired count reached
int droneCount = state.SpawnKey == "8Workers" ? 0 : 1; // First larva already morphed for 8-worker
while (droneCount < desiredUnitCount) {
    var nextLarva = ...; // Find next available larva
    if (!nextLarva) break;
    commands.Add(...);
    droneCount++;
}
```

**Task 2: Worker Labeling - Reusable Code for New Build Orders**
Need to tap into the existing `WorkerLabelService` and `TeamPatchMiningTask` system. When a new drone appears from larva morph, it should be automatically assigned a worker label (W1-W12) following the same greedy chain logic used during InitialMapData.

This means we need a **reusable method** that:
1. Takes a list of minerals + COM position + existing workers
2. Assigns W1-W12 labels greedily
3. Can be called from both `InitialMapData` (at game start) and dynamically when new drones appear

## Phase 3: Extractor Trick & Macro Hatch Placement Service

### Extractor Trick Requirements
- **Condition**: Team closest to Vespene geyser V1 has ≥125 minerals
- **Process**: 
  1. Worker carries minerals from team (shortest path to Town Center → V1)
  2. At V1, worker morphs into extractor (takes 5 seconds)
  3. During morph, larva morphs into next worker
  4. When 15th worker confirmed, cancel the extraction
  5. Worker returns to mining with team
  
**Required Data in BaseDtos:**
- `TeamThatWillExtract` (which team has minerals closest to V1)
- `TeamShortestPathToV1` (for worker return path calculation)
- `HasExtractorTrickBeenUsed` flag

### New Point: Shortest Two-Point Path Service
Need a **HatchPlacementService** similar to the existing SpawningPool placement service. This service should:
1. Calculate shortest two-point path from mineral pair → Town Center → Vespene geyser
2. Identify which team is closest to V1 for extractor trick
3. Return optimal hatch position (likely near town center for early gas)
4. Store this in `BaseDtos.HatchPlacementPosition`

### 15th/16th Worker Team Assignment
- **15th worker** → joins team that would have been Teal (Green in 8-worker) = T3/T4 equivalent
- **16th worker** → joins team that would have been Salmon (Orange in 8-worker) = S3/S4 equivalent

This creates a pattern: after all 4 main teams are full (8 workers), the remaining 4 workers fill up "extra" positions on those same teams.

## Phase 4: File Mapping & Architecture

### Critical Files Needed for This Work
```
📁 /src/BuildIne.cs          - Task 1: Morph loop until desired count
📁 /src/chrisCrossAppleSause.cs - Worker labeling + initial morph logic  
📁 /src/TeamPatchMiningTask.cs - Team assignment (existing)
📁 /src/TeamLabelRegistrationHelper.cs - Label management
📁 /src/InitialMapData.cs   - Team detection + greedy chain (reference)
📁 /src/BaseDtos.cs         - Add: HasExtractorTrick, HatchPlacementPosition
📁 /src/JitPrepositionService.cs - Map data service (for reference)

NEW FILES NEEDED:
📄 /src/WorkerLabelService.cs    - Reusable worker labeling method
📄 /src/HatchPlacementService.cs - New macro hatch placement service
📄 /src/ExtractorTrickManager.cs  - Extractor trick logic + shortest path
```

### Key Design Decisions Needed

**1. Worker Labeling Reuse Strategy:**
- Create a static method: `WorkerLabelService.AssignLabels(minerals, comPosition, existingWorkers)` that returns label assignments
- Call this from both `InitialMapData` (at game start) and dynamically when new drones appear
- Service should maintain state across frames to prevent label loss

**2. Extractor Trick Integration:**
- Add method: `TeamInfo.GetClosestTeamToGeyser(geyserPosition, teams)` 
- When team reaches 125 minerals AND is closest to V1 → trigger extractor trick
- Need shortest path calculation (A* or simple Manhattan distance)

**3. Team Color Mapping Service:**
- Create mapping table: `TeamColorMap.GetTeamForMineralPair(mineralIndex, is8WorkerStart)`
- Returns color name and team label for consistent labeling

## Phase 5: Action Plan & Next Steps

### Immediate Tasks (Priority Order)

**Task 1: BuildIne.cs Larva Morphing Loop**
```csharp
// In BuildIne.cs - Replace single morph with loop
int desiredCount = state.SpawnKey == "8Workers" ? 3 : 4; // First larva already morphed
int currentDroneCount = state.SpawnKey == "8Workers" ? 1 : 2;
while (currentDroneCount < desiredCount) {
    var nextLarva = FindNextAvailableLarva(state);
    if (!nextLarva || !CanMorph(nextLarva)) break;
    
    commands.Add(new SC2APIProtocol.Action { 
        ActionRaw = new ActionRaw { UnitCommand = morphCmd } });
    Console.WriteLine($"[BuildIne]: Morphing larva {nextLarva.Tag} into drone");
    currentDroneCount++;
}
```

**Task 2: Reusable Worker Labeling Service**
Create `WorkerLabelService.AssignLabels(minerals, comPosition, existingWorkers)` that can be called from both InitialMapData and dynamically. This service should maintain a dictionary of tag→label mappings and update on frame changes.

**Task 3: Team Color Mapping Table** (Answer to your question)
```csharp
public static class TeamColorMapping {
    // 12-worker start mineral pairs → color names
    public static readonly Dictionary<int, string> MineralPairToTeam12 = new() {
        {0, "Teal"},      {1, "Teal"},
        {2, "Salmon"},   {3, "Salmon"},
        {4, "Blue"},     {5, "Blue"},
        {6, "Yellow"},   {7, "Yellow"}
    };
    
    // 8-worker start mineral pairs → color names (shifted)
    public static readonly Dictionary<int, string> MineralPairToTeam8 = new() {
        {0, "Green"},     {1, "Green"},      // Teal→Green
        {2, "Orange"},    {3, "Orange"},     // Salmon→Orange  
        {4, "Purple"},    {5, "Purple"},     // Blue→Purple
        {6, "Red"},       {7, "Red"}         // Yellow→Red
    };
    
    public static string GetTeamColor(int mineralIndex, bool is8WorkerStart) => 
        (is8WorkerStart ? MineralPairToTeam8 : MineralPairToTeam12)[mineralIndex];
}
```

**Task 4: New Data Structures in BaseDtos**
- `public List<TeamInfo> OrderedMainMinerals { get; set; }` - Already exists
- Add: `public string TeamThatWillExtract { get; set; }` 
- Add: `public Point? HatchPlacementPosition { get; set; }`
- Add: `public bool HasExtractorTrickBeenUsed { get; set; }`

**Task 5: New Service Files**
- `HatchPlacementService.cs` - Calculate shortest two-point paths and optimal hatch positions
- `ExtractorTrickManager.cs` - Handle the extractor trick logic, mineral counting, and worker release
- `WorkerLabelService.cs` - Reusable labeling method for both game start and dynamic drone morphing

### Files That Need Modification (Not Base Framework)
1. **BuildIne.cs** - Add loop for larva morphing until desired count
2. **BaseDtos.cs** - Add new fields for extractor trick tracking and hatch placement
3. **chrisCrossAppleSause.cs** - Expand to handle both 8 and 12 worker starts, add dynamic labeling call
4. **TeamPatchMiningTask.cs** - May need updates for 15th/16th worker team assignments

### Files That Can Be Reused (No Modification Needed)
- `TeamLabelRegistrationHelper.cs` - Already handles tag→label mapping
- `InitialMapData.cs` - Just needs to call new reusable labeling service
- `BabySharkMiningManager` - Already has the drawing and choreography logic

## Phase 6: Critical Questions for You

Before proceeding with implementation, I need clarification on:

**1. Desired Unit Count Logic:** 
For 8-worker start, does "desired" mean 3 drones per frame (first 2 already exist) or just morph remaining needed? Need exact count.

**2. Worker Labeling Service Scope:**
Should the reusable labeling service be a standalone `WorkerLabelService` class (like the existing `CrosshairService`) that can be injected into multiple managers, or should it be static methods called directly from `chrisCrossAppleSause` and `InitialMapData`?

**3. Extractor Trick Priority:**
When multiple teams have ≥125 minerals, which team gets priority for the extractor trick? The closest one to V1 (as stated) - but what if multiple teams are equidistant?

**4. New Team Color Naming:**
The 8-worker color mapping I proposed (Green/Orange/Purple/Red instead of Teal/Salmon/Blue/Yellow) - should these be hardcoded as new color names, or should we keep the original team labels but just change the drawing color? The BabyShark context mentions "W1 gets label D1" which suggests labels are separate from colors.

**5. 15th/16th Worker Team Assignment:**
When 15th worker joins and creates a 4-worker team, does that team immediately follow Speed Mining Rules (which currently applies to 4+ workers)? And should we add new logic for this?

## Final Summary Table: All Teams Mapping

### 8-Worker Start (Teams = 4 mineral pairs)
| Mineral Pair | Color Name | Team Label | Workers | 
|--------------|-----------|------------|---------|
| M[0]+M[1]   | **Green**    | G1/G2      | 2 workers |
| M[2]+M[3]   | **Orange**   | O1/O2      | 2 workers |
| M[4]+M[5]   | **Purple**   | P1/P2      | 2 workers |
| M[6]+M[7]   | **Red**      | R1/R2      | 2 workers |

### 12-Worker Start (Teams = 8 mineral pairs)  
| Mineral Pair | Color Name | Team Label | Workers |
|--------------|-----------|------------|---------|
| M[0]+M[1]   | **Teal**       | T1/T2      | 2 workers |
| M[2]+M[3]   | **Salmon**     | S1/S2      | 2 workers |
| M[4]+M[5]   | **Blue**       | B1/B2      | 2 workers |
| M[6]+M[7]   | **Yellow**     | Y1/Y2      | 2 workers |
| M[8]+M[9]   | **Teal+**      | T3/T4      | 2 workers |
| M[10]+M[11] | **Salmon+**    | S3/S4      | 2 workers |
| M[12]+M[13] | **Blue+**      | B3/B4      | 2 workers |
| M[14]+M[15] | **Yellow+**    | Y3/Y4      | 2 workers |

### Worker Reassignment Rules (8-Worker → New Workers)
```
Worker 0-2   → Existing teams (Green/Orange/Purple/Red)
Worker 3     → Joins Green team (was Teal in 12-worker)
Worker 4     → Joins Orange team (was Salmon in 12-worker)  
Worker 5     → Joins Purple team (was Blue in 12-worker)
Worker 6     → Joins Red team (was Yellow in 12-worker)
Worker 7     → (8th worker - already full teams)
Worker 8     → Joins Green team again as T3 equivalent
Worker 9     → Joins Orange team again as S3 equivalent
Worker 10    → Joins Purple team again as B3 equivalent  
Worker 11    → Joins Red team again as Y3 equivalent
Worker 12+   → All teams now have 4 workers (Speed Mining Rules)
```

### Team Color Mapping for BuildIne.cs Task

The key insight is that **the mineral pair indices remain the same** (M[0]+M[1] etc.), but the color names change based on whether it's an 8 or 12 worker start. The `WorkerLabelService` should use these mapped colors consistently:

```csharp
// When assigning W1-W12 labels, use the correct team color for that mineral pair
var teamColor = TeamColorMapping.GetTeamColor(mineralPairIndex, is8WorkerStart);
label = $"{teamColor}{workerNumber}"; // e.g., "Green1", "Teal3"
```

This ensures consistency between InitialMapData (which sets up the teams) and dynamic labeling when new drones appear from larva morphing.

---

**Ready to proceed with implementation.** Please confirm:
1. The team color mapping table above is correct for your system
2. Whether WorkerLabelService should be a standalone injectable service or static methods
3. Your preferred approach for the 8/12 worker start detection (is it based on map configuration or state.SpawnKey?)
4. Any additional constraints I missed in the macro hatch placement logic

I can then generate the exact code changes needed for BuildIne.cs, create the new service files, and update BaseDtos with all the required fields.
