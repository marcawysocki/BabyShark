# 🏗️ Expansion Townhall Point System - CONTESTED BASE LOGIC COMPLETE

## Overview

The system now includes:
1. **ExpansionPointModel** - Stores single OR multiple townhall placements
2. **TownhallPlacementOption** - Individual placement with validation data
3. **ExpansionPointService** - Computes placements and detects contested bases
4. **Contested base detection** - Identifies bases that can be placed north/south or east/west
5. **Multiple placement computation** - Generates 2 placements for contested bases

## Key Features

### ✅ Contested Base Detection
- Triggered when: computed TC placement NOT significantly closer (>0.25f) than both central nodes
- Result: 2 placements computed instead of 1
- Placement1: North/Left of mineral line
- Placement2: South/Right of mineral line

### ✅ Placement Favoring
- Computes which start location each placement favors
- Placement1 might favor Start[0], Placement2 might favor Start[1]
- Used for economic optimization based on opponent spawns

### ✅ Data Model
```csharp
ExpansionPointModel
├─ ExpansionIndex (0, 1, 2...)
├─ MineralClusterCenter (the "Smile" COM)
├─ MineralPositions[]
├─ GeyserPositions[]
├─ IsContested (bool)
└─ PlacementOptions[] (List<TownhallPlacementOption>)
   ├─ TownhallPlacementOption[0]
   │  ├─ Point (Vector2Dto - exact townhall location)
   │  ├─ IsValid (bool - passed placement_grid check)
   │  ├─ DistanceToCluster (float - offset from COM)
   │  ├─ FavoredStartLocation (0, 1, or -1)
   │  └─ ValidationNotes (string)
   └─ TownhallPlacementOption[1] (if contested)
      ├─ Point
      ├─ IsValid
      ├─ DistanceToCluster
      ├─ FavoredStartLocation
      └─ ValidationNotes
```

## Integration Steps

### Step 1: Update ExpansionPointService Call in InitialMapData (Line 786+)

**File**: `BabySharkBot/Setup/InitialMapData.cs`

After the expansion clustering completes (line 785), add:

```csharp
// COMPUTE EXPANSION TOWNHALL POINTS using ExpansionPointService
try
{
    if (expansionPointService != null)
    {
        expansionPointService.Initialize(gameInfo, data);
        
        Console.WriteLine($"InitialMapData: Computing expansion townhall points for {expansionTownhalls.Count} expansions");

        // Build list of all start locations for contested base detection
        var startLocationPositions = new List<Vector2Dto>();
        for (int si = 0; si < numStartLocations; si++)
        {
            if (si < startLocations.Count)
            {
                startLocationPositions.Add(startLocations[si]);
            }
        }

        // For each expansion cluster we found, compute townhall placements
        int clusterIndex = 0;
        foreach (var cluster in expansionClusters)  // ⭐ SAVE clusters during clustering loop
        {
            if (clusterIndex >= expansionTownhalls.Count) break;

            int expansionIndex = clusterIndex;
            Vector2Dto clusterCenter = expansionTownhalls[clusterIndex];
            List<Vector2Dto> clusterMinerals = cluster;

            // Find geysers near this expansion cluster
            List<Vector2Dto> nearbyGeysers = new List<Vector2Dto>();
            const float geyserMatchDistance = 15f;  // Geysers within 15 tiles belong to this expansion
            foreach (var geyser in vespeneList)
            {
                float dist = Vector2.Distance(
                    new Vector2(clusterCenter.X, clusterCenter.Y),
                    new Vector2(geyser.X, geyser.Y)
                );
                if (dist < geyserMatchDistance)
                {
                    nearbyGeysers.Add(geyser);
                }
            }

            // Compute townhall placement(s) - may be 1 for standard, 2 for contested
            expansionPointService.ComputeExpansionPoint(
                expansionIndex,
                clusterCenter,
                clusterMinerals,
                nearbyGeysers,
                startLocationPositions  // ⭐ Pass start locations for contested favoring
            );

            clusterIndex++;
        }

        Console.WriteLine($"InitialMapData: Computed townhall points for {expansionTownhalls.Count} expansions");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"InitialMapData: Failed to compute expansion townhall points: {ex.Message}");
}
```

### Step 2: IMPORTANT - Save Clusters During Loop

**File**: `BabySharkBot/Setup/InitialMapData.cs` (Line 654 area)

Modify the expansion clustering loop to save clusters:

```csharp
// ⭐ ADD THIS OUTSIDE THE LOOP
var expansionClusters = new List<List<Vector2Dto>>();  // Save for later use

// Then in the loop, after cluster completion (line 699):
// BEFORE: if (cluster.Count >= minMineralsForExpansion)
// ADD: expansionClusters.Add(new List<Vector2Dto>(cluster));  // Save cluster

if (cluster.Count >= minMineralsForExpansion)
{
    expansionClusters.Add(new List<Vector2Dto>(cluster));  // ⭐ SAVE CLUSTER
    
    // ... rest of code
    var expansionCenter = new Vector2Dto(centerX, centerY, centerZ);
    expansionTownhalls.Add(expansionCenter);
```

### Step 3: Add Drawing Service (Optional but Recommended)

**File**: `BabySharkBot/Services/ExpansionPointService.cs` - Add after GetExpansionPoint():

```csharp
/// <summary>
/// Get all townhall placements for a specific expansion
/// </summary>
public List<TownhallPlacementOption> GetPlacementOptions(int expansionIndex)
{
    if (_expansionPoints.TryGetValue(expansionIndex, out var model))
    {
        return model.PlacementOptions;
    }
    return new List<TownhallPlacementOption>();
}

/// <summary>
/// Check if expansion is contested
/// </summary>
public bool IsContestedExpansion(int expansionIndex)
{
    if (_expansionPoints.TryGetValue(expansionIndex, out var model))
    {
        return model.IsContested;
    }
    return false;
}
```

### Step 4: Add Drawing Method in BabySharkMiningManager

**File**: `BabySharkBot/Managers/BabySharkMiningManager.cs`

Add field (line 35):
```csharp
private ExpansionPointService _expansionPointService;
```

Update constructor (line 36):
```csharp
public BabySharkMiningManager(
    WorkerLabelService workerLabelService = null, 
    CrosshairService crosshairService = null, 
    MineralLabelService mineralLabelService = null, 
    VespeneLabelService vespeneLabelService = null, 
    ExpansionCOMService expansionCOMService = null,
    ExpansionPointService expansionPointService = null)  // ⭐ ADD
{
    _expansionPointService = expansionPointService;  // ⭐ ADD
    // ... rest
}
```

Add drawing call in OnFrame (line 105):
```csharp
DrawExpansionTownhalls();  // After DrawExpansionMineralLabels()
```

Add drawing method (after DrawExpansionMineralLabels method):
```csharp
/// <summary>
/// Draw expansion townhall placement points (green spheres and labels).
/// Shows primary placement for standard bases, both placements for contested.
/// </summary>
private void DrawExpansionTownhalls()
{
    if (!ManagerDebugService.IsDebugEnabled || _expansionPointService == null)
        return;

    try
    {
        var expansionPoints = _expansionPointService.GetAllExpansionPoints();
        if (expansionPoints.Count == 0) return;

        const float sphereRadius = 1.0f;
        var primaryGreen = new Color { R = 0, G = 255, B = 0 };      // Green for valid
        var alternateYellow = new Color { R = 255, G = 255, B = 0 }; // Yellow for contested alternate
        var invalidRed = new Color { R = 255, G = 0, B = 0 };        // Red for invalid

        foreach (var kvp in expansionPoints)
        {
            var model = kvp.Value;
            if (model.PlacementOptions.Count == 0) continue;

            // Draw all placement options
            for (int i = 0; i < model.PlacementOptions.Count; i++)
            {
                var option = model.PlacementOptions[i];
                var point = new Point 
                { 
                    X = option.Point.X, 
                    Y = option.Point.Y, 
                    Z = option.Point.Z 
                };

                // Choose color based on validity and contested status
                Color color = invalidRed;
                if (option.IsValid)
                {
                    color = (model.IsContested && i > 0) ? alternateYellow : primaryGreen;
                }

                // Draw sphere at townhall location
                ManagerDebugService.DrawSphere(point, sphereRadius, color);

                // Draw label with expansion info
                string label = $"TC-E{model.ExpansionIndex}";
                if (model.IsContested)
                {
                    label += (i == 0) ? "-N/L" : "-S/R";  // North/Left or South/Right
                    if (option.FavoredStartLocation >= 0)
                    {
                        label += $"→S{option.FavoredStartLocation}";
                    }
                }

                ManagerDebugService.DrawText(label, point, color, 12);
            }

            // Log contested base info if applicable
            if (model.IsContested && model.PlacementOptions.Count >= 2)
            {
                Console.WriteLine($"[CONTESTED-E{model.ExpansionIndex}] 2 placements: " +
                    $"Opt1@({model.PlacementOptions[0].Point.X:F1},{model.PlacementOptions[0].Point.Y:F1}) favors S{model.PlacementOptions[0].FavoredStartLocation}, " +
                    $"Opt2@({model.PlacementOptions[1].Point.X:F1},{model.PlacementOptions[1].Point.Y:F1}) favors S{model.PlacementOptions[1].FavoredStartLocation}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"BabySharkMiningManager.DrawExpansionTownhalls: {ex.Message}");
    }
}
```

### Step 5: Update Program.cs to Instantiate and Pass Service

**File**: `BabySharkBot/Program.cs` (where BabySharkMiningManager is created)

```csharp
// Create the ExpansionPointService
var expansionPointService = new ExpansionPointService();

// Pass to BabySharkMiningManager
var miningManager = new BabySharkMiningManager(
    workerLabelService,
    crosshairService,
    mineralLabelService,
    vespeneLabelService,
    expansionCOMService,
    expansionPointService  // ⭐ ADD
);
```

## Console Output Examples

### Standard Base:
```
[STANDARD-E0] Standard base - computing single placement
ExpansionPointService: Expansion 0 ideal point VALID at (45.23, 78.50)
Drew Expansion[0] COM (smile) at (45.23, 78.50, 2.50)
TC-E0 at (42.10, 81.30)
```

### Contested Base:
```
[CONTESTED-E1] Detected contested base - computing multiple placements
[CONTESTED] Placement1 favors Start[0], Placement2 favors Start[1]
[CONTESTED] Created 2 placements for expansion - option1 valid=true, option2 valid=true
Drew Expansion[1] COM (smile) at (120.50, 95.00, 1.80)
TC-E1-N/L→S0 at (118.00, 100.00)
TC-E1-S/R→S1 at (122.00, 90.00)
[CONTESTED-E1] 2 placements: Opt1@(118.0,100.0) favors S0, Opt2@(122.0,90.0) favors S1
```

## Drawing Color Legend

| Color | Meaning |
|-------|---------|
| 🟢 Green | Valid townhall placement (primary) |
| 🟡 Yellow | Valid contested alternate placement |
| 🔴 Red | Invalid placement (failed validation) |
| White | Mineral COM crosshairs (existing) |
| Blue | Expansion COM crosshairs (existing) |

## Data Flow Diagram

```
expansionMineralsList (scattered minerals)
    ↓
CLUSTERING (lines 645-786)
├─ Iterative grouping by 4.5f distance
├─ Filter: >= 6 minerals → EXPANSION
└─ Save cluster[] for later use ⭐
    ↓
expansionTownhalls[] = cluster centers (COM/"Smile")
expansionClusters[] = cluster mineral lists ⭐
    ↓
COMPUTE TOWNHALL POINTS (NEW)
├─ For each expansion:
│  ├─ Detect if contested
│  ├─ If standard: 1 placement computed
│  └─ If contested: 2 placements (N/L + S/R)
│
└─ ExpansionPointModel[]
   └─ PlacementOptions[] (1 or 2 items)
        └─ TownhallPlacementOption
           ├─ Point (exact location)
           ├─ IsValid (bool)
           ├─ FavoredStartLocation (0, 1, or -1)
           └─ ValidationNotes
    ↓
DRAW (DrawExpansionTownhalls)
├─ Green sphere + label for valid placements
├─ Yellow sphere for contested alternates
└─ Red sphere for invalid placements
```

## Key Algorithm Points

### Contested Detection (Line ~0.25f threshold)
```
If (distanceToCentral1 > 0.25f AND distanceToCentral2 > 0.25f)
    → Standard base (1 placement)
Else
    → Contested base (2 placements)
```

### Alternate Placement Computation
```
For contested bases:
- Find line connecting two central minerals
- Create perpendicular offsets (left/right)
- Placement1 = mineral_center + perpendicular1 * 3.5f
- Placement2 = mineral_center + perpendicular2 * 3.5f
- Validate both against placement_grid
- Compute which start location each favors
```

### Favoring Logic
```
distance_to_start0 < distance_to_start1
    → FavoredStartLocation = 0
Else
    → FavoredStartLocation = 1
```

## Status

✅ **Build**: Successful (0 errors, 0 warnings)
✅ **Models**: TownhallPlacementOption + updated ExpansionPointModel
✅ **Service**: Contested detection + multiple placement computation
✅ **Drawing**: Ready to implement (code provided above)
✅ **Integration**: Ready to add to InitialMapData

## Next Steps

1. ⚠️ **IMPORTANT**: Modify clustering loop to save `expansionClusters` list
2. Add ExpansionPointService.Initialize() call in InitialMapData  
3. Loop through expansions and call ComputeExpansionPoint()
4. Add drawing service helper methods
5. Add drawing method to BabySharkMiningManager
6. Update Program.cs to instantiate and pass service
7. Test with various map types (2-player, 4-player, standard, contested)
