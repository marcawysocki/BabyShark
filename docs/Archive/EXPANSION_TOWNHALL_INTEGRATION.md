# 🏗️ Expansion Townhall Point Integration with InitialMapData

## Data Flow Architecture

```
InitialMapData.GetNewMiningData()
    ↓
1. EXTRACT MINERALS & VESPENES from raw units
    ├─ Start location minerals → MainMinerals
    ├─ Start location vespenes → MainVespene
    └─ Unassigned minerals → expansionMineralsList  ⭐ KEY FOR EXPANSIONS
    ↓
2. CLUSTER EXPANSION MINERALS (lines 645-786)
    ├─ Create global labels M1, M2, M3...
    ├─ Iterative clustering (clusterDistance = 4.5f)
    ├─ Filter: >= 6 minerals = expansion base
    ├─ Filter: < 6 minerals = mineral wall (skip)
    ├─ Calculate cluster center (COM) = "Smile"
    ├─ Update labels M1 → M1-E1 when clustered
    ├─ Register with expansionCOMService.Set() for visualization ✅
    └─ Store in tempBaseDto.ExpansionTownhalls (list of COM positions)
    ↓
3. ⭐ COMPUTE EXPANSION TOWNHALL POINTS (lines 786 - NEW INTEGRATION POINT)
    ├─ For each expansion mineral cluster:
    │  ├─ expansionCluster = cluster minerals
    │  ├─ clusterCenter = expansionTownhalls[i] (from above)
    │  ├─ geyserPositions = geysers near this cluster
    │  └─ Call expansionPointService.ComputeExpansionPoint()
    │
    ├─ ExpansionPointService computes:
    │  ├─ Ideal point = 3.75 tiles from cluster center
    │  ├─ Direction = mineral center ← geyser center (reversed)
    │  ├─ Validate placement against placement_grid (5×5 footprint)
    │  ├─ Check clearance: geysers ≥3.5, minerals ≥2.0 tiles
    │  ├─ Spiral search if ideal invalid (radius 0→6)
    │  └─ Return ExpansionPointModel with result
    │
    └─ Store results in tempBaseDto ✅ (NEW DTO FIELD)
    ↓
4. CREATE MawBaseLocationData snapshot and return

tempBaseDto returns → _mapData (BabySharkMiningManager.OnStart)
                  ↓
                  OnFrame() draws all visualizations

```

## Integration Points - What Needs to Change

### 1. **InitialMapData.cs** - Add ExpansionPointService parameter
```csharp
// Line 18 - Update method signature:
public MawBaseLocationData GetNewMiningData(
    ResponseGameInfo gameInfo, 
    ResponseData data, 
    ResponseObservation observation, 
    Point2D startLoc = null, 
    WorkerLabelService? workerLabelService = null, 
    CrosshairService? crosshairService = null, 
    MineralLabelService? mineralLabelService = null, 
    VespeneLabelService? vespeneLabelService = null, 
    ExpansionCOMService? expansionCOMService = null,
    ExpansionPointService? expansionPointService = null  // ⭐ ADD THIS
)
```

### 2. **InitialMapData.cs** - Add computation after clustering (line 786)
```csharp
// After line 785 (clustering complete), add:

// COMPUTE EXPANSION TOWNHALL POINTS using ExpansionPointService
try
{
    if (expansionPointService != null)
    {
        expansionPointService.Initialize(gameInfo, data);
        
        // For each expansion cluster we found
        for (int ei = 0; ei < expansionTownhalls.Count; ei++)
        {
            // Gather minerals and geysers for this expansion
            var clusterMinerals = cluster from line 659;  // ⭐ Save clusters during loop
            var geyserPosition = find nearest geysers;     // ⭐ Match geysers to cluster
            
            // Compute townhall placement point
            expansionPointService.ComputeExpansionPoint(
                ei,                              // expansion index (0-based)
                expansionTownhalls[ei],         // cluster center (smile)
                clusterMinerals,                // minerals in cluster
                geyserPositions                 // geysers near cluster
            );
        }
        Console.WriteLine($"InitialMapData: Computed {expansionTownhalls.Count} expansion townhall points");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"InitialMapData: Failed to compute expansion points: {ex.Message}");
}
```

### 3. **BabySharkMiningManager.cs** - Add ExpansionPointService field and parameter
```csharp
// Line 33 - Add field:
private ExpansionPointService _expansionPointService;

// Line 36 - Update constructor:
public BabySharkMiningManager(
    WorkerLabelService workerLabelService = null, 
    CrosshairService crosshairService = null, 
    MineralLabelService mineralLabelService = null, 
    VespeneLabelService vespeneLabelService = null, 
    ExpansionCOMService expansionCOMService = null,
    ExpansionPointService expansionPointService = null)  // ⭐ ADD
{
    // ... existing code ...
    _expansionPointService = expansionPointService;
}

// Line 65 - Update GetNewMiningData call:
_mapData = _initialMapData.GetNewMiningData(
    gameInfo, data, observation, null, 
    _workerLabelService, _crosshairService, _mineralLabelService, 
    _vespeneLabelService, _expansionCOMService,
    _expansionPointService  // ⭐ ADD
);
```

### 4. **BabySharkMiningManager.cs** - Add drawing method
```csharp
// Line 105 - Add call in OnFrame drawing sequence:
DrawExpansionTownhallPoints();  // After DrawExpansionMineralLabels()

// New method (add after DrawExpansionMineralLabels):
private void DrawExpansionTownhallPoints()
{
    if (!ManagerDebugService.IsDebugEnabled || _expansionPointService == null)
        return;

    try
    {
        var expansionPoints = _expansionPointService.GetAllExpansionPoints();
        if (expansionPoints.Count == 0) return;

        var greenColor = new Color { R = 0, G = 255, B = 0 };
        const float sphereRadius = 0.75f;

        foreach (var kvp in expansionPoints)
        {
            var model = kvp.Value;
            if (!model.IsValid) continue;

            var point = new Point { X = model.ExpansionPoint.X, Y = model.ExpansionPoint.Y, Z = model.ExpansionPoint.Z };
            ManagerDebugService.DrawSphere(point, sphereRadius, greenColor);
            ManagerDebugService.DrawText($"E{model.ExpansionIndex}", point, greenColor, 12);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"BabySharkMiningManager.DrawExpansionTownhallPoints: {ex.Message}");
    }
}
```

### 5. **Program.cs** - Instantiate and pass service
```csharp
// Where BabySharkMiningManager is created:
var expansionPointService = new ExpansionPointService();

var miningManager = new BabySharkMiningManager(
    workerLabelService, 
    crosshairService, 
    mineralLabelService, 
    vespeneLabelService, 
    expansionCOMService,
    expansionPointService  // ⭐ ADD
);
```

### 6. **BaseDtos.cs** - Add storage field (OPTIONAL - for persistence)
```csharp
// In MawBaseLocationData class, add:
public List<ExpansionPointModel> ComputedExpansionPoints { get; set; } = new List<ExpansionPointModel>();
```

## Current State → Flow with New Service

### ✅ Already Working (Existing Code):
1. Extract expansion minerals not assigned to start locations
2. Cluster minerals by proximity (4.5f distance)
3. Filter out mineral walls (< 6 minerals)
4. Calculate cluster center (COM) - the "smile"
5. Register with ExpansionCOMService for blue crosshair visualization
6. Store mineral labels M1-E1, M2-E2, etc.

### ⭐ NEW (ExpansionPointService):
1. **Compute ideal townhall point** from cluster center
   - Direction: mineral center → geyser center (reversed)
   - Offset: 3.75 tiles from mineral cluster
   - Result: point positioned between minerals and geysers
   
2. **Validate against placement_grid**
   - Check all 25 tiles (5×5 townhall footprint)
   - Verify clearance: geysers ≥ 3.5 tiles, minerals ≥ 2.0 tiles
   
3. **Spiral search if needed**
   - If ideal point invalid, search outward
   - Radius 0→6 tiles, 0.25 step size
   - Return first valid point
   
4. **Draw green townhall placement points**
   - Visualize computed expansion locations
   - Overlay on map showing where townhalls should go

## Data Available at Integration Point (Line 786)

At the point where we'd add ExpansionPointService.ComputeExpansionPoint():

```csharp
expansionTownhalls[]          // Vector2Dto: cluster centers (smiles)
cluster[]                     // List<Vector2Dto>: minerals in current cluster
vespeneList[]                 // List<Vector2Dto>: all vespenes on map
expansionMineralsList[]       // List<Vector2Dto>: all unassigned minerals
gameInfo                      // ResponseGameInfo: map size, start raw data
data                          // ResponseData: placement_grid, pathing_grid
```

## Summary: How It Meshes

| Stage | Component | Output | Used By |
|-------|-----------|--------|---------|
| 1 | Raw units extraction | expansionMineralsList | Clustering |
| 2 | Mineral clustering | expansionTownhalls[] (COM positions) | ExpansionPointService |
| 3 | ⭐ Townhall point computation | ExpansionPointModel[] | Drawing + building placement |
| 4 | Visualization | Blue crosshairs (COM) + Green spheres (townhalls) | Player debug view |

The ExpansionPointService **takes the output of clustering** (cluster centers + mineral/geyser positions) and **computes precise buildable townhall locations** using Blizzard's placement rules.
