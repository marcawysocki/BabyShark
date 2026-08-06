# Expansion Point Drawing - Code Flow Example

## Complete Execution Path

```
GAME START
│
├─ BabySharkAI Constructor
│  └─ Creates ExpansionPointDrawService
│     └─ Passes to BabySharkMiningManager
│
├─ BabySharkMiningManager.OnStart()
│  └─ Calls _initialMapData.GetNewMiningData(
│        gameInfo, data, observation, null,
│        workerLabelService, crosshairService, 
│        mineralLabelService, vespeneLabelService,
│        expansionCOMService, 
│        expansionPointService,  ← Available but not populated here
│        expansionPointDrawService ← Ready to receive points
│     )
│
├─ InitialMapData.GetNewMiningData()
│  │
│  ├─ Phase 1: Parse minerals, vespene, workers
│  │
│  ├─ Phase 2: Cluster expansion minerals
│  │  └─ Creates expansionClusters list
│  │     └─ Each cluster = minerals near each other
│  │
│  ├─ Phase 3: Compute expansion townhall placements
│  │  └─ ExpansionPointService.ComputeExpansionPoint()
│  │     ├─ Step 1: Compute ideal point
│  │     ├─ Step 2: Detect contested base
│  │     ├─ Step 3: Create 1 or 2 placement options
│  │     └─ Store in _expansionPoints dictionary
│  │
│  └─ Phase 4: Register points for visualization
│     └─ if (expansionPointDrawService != null)
│        └─ var allPoints = expansionPointService.GetAllExpansionPoints()
│           └─ For each expansion point:
│              └─ For each placement option (1-2 options):
│                 ├─ if (option.IsValid)
│                 │  ├─ Generate label:
│                 │  │  ├─ if standard: "E1", "E2"
│                 │  │  └─ if contested: "E1", "E1-Alt1"
│                 │  ├─ Determine color:
│                 │  │  ├─ if standard: Green (0, 255, 0)
│                 │  │  ├─ if contested primary: Yellow (255, 255, 0)
│                 │  │  └─ if contested alternate: Orange (255, 165, 0)
│                 │  ├─ Create Point with Z = 12.0f
│                 │  └─ expansionPointDrawService.SetExpansionPoint()
│                 │     └─ Stores in internal dictionary
│                 └─ Console.WriteLine() logged
│
└─ Returns MawBaseLocationData

EVERY FRAME (if Debug Enabled)
│
├─ BabySharkMiningManager.OnFrame()
│  └─ Calls DrawExpansionPoints()
│     │
│     ├─ if (!ManagerDebugService.IsDebugEnabled) return
│     │
│     ├─ if (_expansionPointDrawService == null) return
│     │
│     └─ For each point in GetAllPoints():
│        ├─ Ensure position.Z ≥ 12
│        ├─ ManagerDebugService.DrawSphere(position, 0.75f, color)
│        ├─ ManagerDebugService.DrawText(label, position, color, 12)
│        └─ Console.WriteLine() logged
│
└─ Rendering happens in Sharky's debug system
```

## Data Structure Example

### Input: Map with 2 Expansions

**Expansion 1: Standard Base**
```
Minerals: [M1, M2, M3, M4, M5, M6, M7, M8] (8 minerals)
Geysers: [G1, G2] (2 geysers)
Cluster Center: (50.0, 50.0, 0.5)

ExpansionPointService Analysis:
├─ Ideal point calculation: (52.5, 50.0) [3.75 tiles from center]
├─ Contested detection: Distance to central nodes > 0.25? NO
└─ Result: StandardBase = true, PlacementOptions.Count = 1

PlacementOption[0]:
  Point: (52.5, 50.0)
  IsValid: true
  DistanceToCluster: 2.5f
  ValidationNotes: "Ideal point valid"
```

**Expansion 2: Contested Base**
```
Minerals: [M9, M10, M11, M12, M13, M14, M15, M16]
Geysers: [G3, G4]
Cluster Center: (80.0, 80.0, 0.5)

ExpansionPointService Analysis:
├─ Ideal point calculation: (82.5, 80.0)
├─ Contested detection: Distance to central nodes > 0.25? YES
└─ Result: IsContested = true, PlacementOptions.Count = 2

PlacementOption[0] (Primary):
  Point: (83.0, 77.0)
  IsValid: true
  DistanceToCluster: 3.5f
  FavoredStartLocation: 0

PlacementOption[1] (Alternate):
  Point: (81.0, 83.0)
  IsValid: true
  DistanceToCluster: 3.5f
  FavoredStartLocation: 1
```

### Registration Process

```csharp
// InitialMapData.cs - After ExpansionPointService.ComputeExpansionPoint()

var allExpansionPoints = expansionPointService.GetAllExpansionPoints();
// Returns: Dictionary<int, ExpansionPointModel>
//   [0] → ExpansionPointModel (E1, Standard)
//   [1] → ExpansionPointModel (E2, Contested)

foreach (var kvp in allExpansionPoints)
{
    int expansionIndex = kvp.Key;        // 0 or 1
    var model = kvp.Value;               // ExpansionPointModel

    foreach (var option in model.PlacementOptions)
    {
        if (option.IsValid)
        {
            // Expansion 1 (E1, Standard):
            // expansionIndex=0, optionIdx=0, IsContested=false
            // label = "E1"
            // color = Green (0, 255, 0)

            // Expansion 2 (E2, Contested):
            // [First iteration]
            // expansionIndex=1, optionIdx=0, IsContested=true
            // label = "E2"
            // color = Yellow (255, 255, 0)
            // [Second iteration]
            // expansionIndex=1, optionIdx=1, IsContested=true
            // label = "E2-Alt1"
            // color = Orange (255, 165, 0)

            expansionPointDrawService.SetExpansionPoint(
                drawPoint,      // Point with X, Y, Z=12.0f
                label,          // "E1", "E2", "E2-Alt1"
                color,          // Green, Yellow, Orange
                isContested     // false or true
            );
        }
    }
}
```

### Drawing Output

```
Frame 0 (OnFrame):

  DrawExpansionPoints()
  ├─ Point "E1":
  │  ├─ DrawSphere at (52.5, 50.0, 12.0) radius=0.75 color=Green
  │  └─ DrawText "E1" at (52.5, 50.0, 12.0) color=Green
  │
  ├─ Point "E2":
  │  ├─ DrawSphere at (83.0, 77.0, 12.0) radius=0.75 color=Yellow
  │  └─ DrawText "E2" at (83.0, 77.0, 12.0) color=Yellow
  │
  └─ Point "E2-Alt1":
     ├─ DrawSphere at (81.0, 83.0, 12.0) radius=0.75 color=Orange
     └─ DrawText "E2-Alt1" at (81.0, 83.0, 12.0) color=Orange

Console Output:
  "ExpansionPointService initialized"
  "BabySharkAI: Creating ExpansionPointDrawService..."
  "BabySharkMiningManager.OnStart: Calling GetNewMiningData()"
  "InitialMapData: GetNewMiningData called"
  "InitialMapData: Expansion[0] townhall placements computed"
  "InitialMapData: Expansion[1] townhall placements computed"
  "InitialMapData: Registered expansion draw point E1 at (52.50, 50.00) contested=False"
  "InitialMapData: Registered expansion draw point E2 at (83.00, 77.00) contested=True"
  "InitialMapData: Registered expansion draw point E2-Alt1 at (81.00, 83.00) contested=True"
  "BabySharkMiningManager.DrawExpansionPoints: Drawing 3 expansion points"
  "Drew 'E1' at (52.50, 50.00, 12.00)"
  "Drew 'E2' at (83.00, 77.00, 12.00)"
  "Drew 'E2-Alt1' at (81.00, 83.00, 12.00)"
```

## Visual Result on Screen

```
Screen View (Top-Down):

     [Expansion 1 - Standard]

     ●── Green sphere "E1"
         ├─ Valid standard townhall placement
         └─ 3.75 tiles from mineral cluster

     [Expansion 2 - Contested]

     ●── Yellow sphere "E2"
         └─ Primary placement option (favors Start[0])

     ●── Orange sphere "E2-Alt1"
         └─ Alternate placement option (favors Start[1])

     (User sees: 3 colored spheres with labels, can immediately
      identify which bases are contested and where alternate
      placements are available)
```

## Key Points

1. **One-time Registration**: Points registered during OnStart(), not recreated every frame
2. **Frame-based Drawing**: GetAllPoints() called every frame only if debug enabled
3. **Zero Overhead When Debug Off**: No drawing primitives called, no sphere/text overhead
4. **Color-Coded Intelligence**: Visual distinction between standard and contested bases
5. **Contested Logic Built-In**: ExpansionPointService handles all contested base detection, draw service just visualizes it
