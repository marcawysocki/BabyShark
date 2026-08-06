# ⚡ QUICK REFERENCE - Townhall Placement System

## What Was Built

✅ **ExpansionPointModel.cs** - Data model for 1-2 townhall placements per expansion
✅ **ExpansionPointService.cs** - Engine that detects contested bases and computes placements
✅ **Contested base detection** - Threshold-based (0.25f closer to central nodes)
✅ **Multiple placements** - North/South or East/West variants for contested bases
✅ **Start location favoring** - Each placement marked as favoring Start[0] or Start[1]

## What's Needed Next

### 1️⃣ InitialMapData.cs - Save Clusters (Line ~654)

**BEFORE** clustering loop:
```csharp
var expansionClusters = new List<List<Vector2Dto>>();
```

**INSIDE** loop after `cluster.Count >= minMineralsForExpansion` check:
```csharp
expansionClusters.Add(new List<Vector2Dto>(cluster));  // ⭐ SAVE
```

### 2️⃣ InitialMapData.cs - Call Service (Line ~786)

```csharp
if (expansionPointService != null)
{
    expansionPointService.Initialize(gameInfo, data);
    for (int ei = 0; ei < expansionTownhalls.Count && ei < expansionClusters.Count; ei++)
    {
        // Find nearby geysers
        List<Vector2Dto> nearbyGeysers = vespeneList
            .Where(g => Vector2.Distance(
                new Vector2(expansionTownhalls[ei].X, expansionTownhalls[ei].Y),
                new Vector2(g.X, g.Y)) < 15f)
            .ToList();
        
        // Compute placements
        expansionPointService.ComputeExpansionPoint(
            ei, expansionTownhalls[ei], expansionClusters[ei], nearbyGeysers, startLocations
        );
    }
}
```

### 3️⃣ BabySharkMiningManager.cs - Add Field & Method

**Field** (line 35):
```csharp
private ExpansionPointService _expansionPointService;
```

**Constructor** (line 36):
```csharp
public BabySharkMiningManager(..., ExpansionPointService expansionPointService = null)
{
    // ... existing code ...
    _expansionPointService = expansionPointService;
}
```

**Drawing call** (line 105 in OnFrame):
```csharp
DrawExpansionTownhalls();  // After DrawExpansionMineralLabels()
```

**Drawing method** (after DrawExpansionMineralLabels):
```csharp
private void DrawExpansionTownhalls()
{
    if (!ManagerDebugService.IsDebugEnabled || _expansionPointService == null) return;
    
    var expansionPoints = _expansionPointService.GetAllExpansionPoints();
    foreach (var kvp in expansionPoints)
    {
        var model = kvp.Value;
        for (int i = 0; i < model.PlacementOptions.Count; i++)
        {
            var option = model.PlacementOptions[i];
            var point = new Point { X = option.Point.X, Y = option.Point.Y, Z = option.Point.Z };
            
            Color color = !option.IsValid ? new Color { R = 255, G = 0, B = 0 }  // Red if invalid
                        : model.IsContested && i > 0 ? new Color { R = 255, G = 255, B = 0 }  // Yellow if contested alt
                        : new Color { R = 0, G = 255, B = 0 };  // Green if valid primary
            
            ManagerDebugService.DrawSphere(point, 1.0f, color);
            
            string label = $"TC-E{model.ExpansionIndex}";
            if (model.IsContested)
            {
                label += i == 0 ? "-N/L" : "-S/R";
                if (option.FavoredStartLocation >= 0)
                    label += $"→S{option.FavoredStartLocation}";
            }
            
            ManagerDebugService.DrawText(label, point, color, 12);
        }
    }
}
```

### 4️⃣ Program.cs - Instantiate Service

```csharp
var expansionPointService = new ExpansionPointService();

var miningManager = new BabySharkMiningManager(
    workerLabelService, crosshairService, mineralLabelService, vespeneLabelService, 
    expansionCOMService, expansionPointService  // ⭐ ADD
);
```

## Console Output

```
STANDARD:
[STANDARD-E0] Standard base - computing single placement
TC-E0 at (42.10, 81.30)

CONTESTED:
[CONTESTED-E1] Detected contested base - computing multiple placements
[CONTESTED] Placement1 favors Start[0], Placement2 favors Start[1]
TC-E1-N/L→S0 at (118.00, 100.00)
TC-E1-S/R→S1 at (122.00, 90.00)
```

## Drawing Output

- 🟢 **Green sphere**: Valid standard townhall
- 🟡 **Yellow sphere**: Valid contested alternate townhall  
- 🔴 **Red sphere**: Invalid placement (failed validation)
- Label format: `TC-E#` (standard) or `TC-E#-N/L→S0` (contested)

## Files Created (Already Compiled ✅)

- `BabySharkBot/Setup/ExpansionPointModel.cs` (90 lines)
- `BabySharkBot/Services/ExpansionPointService.cs` (350+ lines)

## Files to Modify (Pending 🚧)

1. `InitialMapData.cs` - Save clusters + call service (2 edits)
2. `BabySharkMiningManager.cs` - Add field + drawing (2 edits + 1 method)
3. `Program.cs` - Instantiate + pass (2 edits)

## Key Algorithm

```
expansionPointService.ComputeExpansionPoint(
    expansionIndex,
    mineralClusterCenter,      // "Smile" (COM)
    clusterMinerals,           // Minerals in this cluster
    clusterGeysers,            // Geysers near this cluster
    startLocations             // For determining favored start
)
    ↓
    if (distance_to_central_node > 0.25f)
        → STANDARD: 1 placement toward geysers
    else
        → CONTESTED: 2 perpendicular placements (N/L + S/R)
    ↓
    Returns: ExpansionPointModel with PlacementOptions[1-2]
```

## Why This Matters

- **Standard bases**: Single optimal townhall placement (green dot)
- **Contested bases**: Two equally valid placements (green + yellow dots)
  - North/South maps: placements above/below minerals
  - East/West maps: placements left/right of minerals
  - Each "favors" a different start location
  - Bot can choose based on opponent spawn location

---

**Status**: ✅ Ready to integrate - all code written, compiled, 0 errors

See `TOWNHALL_SYSTEM_STATUS.md` for detailed line numbers and full code examples.
