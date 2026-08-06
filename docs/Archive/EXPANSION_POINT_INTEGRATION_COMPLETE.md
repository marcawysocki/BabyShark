# Expansion Point Drawing - Complete Integration

## Full Pipeline Connected

### 1. Service Creation (BabySharkBot.cs)
```csharp
var expansionPointService = new ExpansionPointService();
var expansionPointDrawService = new ExpansionPointDrawService();
```

### 2. Service Injection (BabySharkBot.cs → BabySharkMiningManager)
```csharp
var miningManager = new BabySharkMiningManager(
    workerLabelService, 
    crosshairService, 
    mineralLabelService, 
    vespeneLabelService, 
    expansionCOMService, 
    expansionPointService,      ← NEW
    expansionPointDrawService   ← Already had
);
```

### 3. OnStart Initialization (BabySharkMiningManager.cs)
```csharp
_mapData = _initialMapData.GetNewMiningData(
    gameInfo, data, observation, null,
    _workerLabelService, _crosshairService, 
    _mineralLabelService, _vespeneLabelService,
    _expansionCOMService, 
    _expansionPointService,      ← NEW
    _expansionPointDrawService
);
```

### 4. Computation (InitialMapData.cs)
```csharp
// ExpansionPointService computes valid placement points
expansionPointService.ComputeExpansionPoint(
    i, center, cluster, nearbyVespenes, startLocations
);
```

### 5. Registration (InitialMapData.cs)
```csharp
// Retrieve computed points from ExpansionPointService
var allExpansionPoints = expansionPointService.GetAllExpansionPoints();

// For each valid placement, register with draw service
for each (expansionIndex, model) in allExpansionPoints:
    for each option in model.PlacementOptions:
        if option.IsValid:
            expansionPointDrawService.SetExpansionPoint(
                drawPoint,      // Point with Z=12
                label,          // "E1", "E2-Alt1", etc.
                color,          // Green, Yellow, Orange
                isContested     // boolean
            )
```

### 6. Drawing (BabySharkMiningManager.OnFrame)
```csharp
DrawExpansionPoints()
  ├─ Get all points from expansionPointDrawService
  ├─ For each point:
  │  ├─ DrawSphere(position, 0.75f, color)
  │  └─ DrawText(label, position, color, 12)
  └─ Console log all drawn points
```

## Data Flow Summary

```
BabySharkBot.cs
    ↓ creates
[ExpansionPointService] & [ExpansionPointDrawService]
    ↓ passes to
BabySharkMiningManager (constructor)
    ↓ stores as fields (_expansionPointService, _expansionPointDrawService)
    ↓ passes to
InitialMapData.GetNewMiningData() (OnStart)
    ↓ uses both:
    │  ExpansionPointService.ComputeExpansionPoint()
    │  ExpansionPointDrawService.SetExpansionPoint()
    ↓ then every frame
BabySharkMiningManager.OnFrame()
    ↓ calls
DrawExpansionPoints()
    ↓ retrieves from
ExpansionPointDrawService.GetAllPoints()
    ↓ draws using
ManagerDebugService.DrawSphere() & DrawText()
```

## What Should Happen

### Game Start
1. ✅ ExpansionPointService initialized
2. ✅ ExpansionPointDrawService initialized  
3. ✅ GetNewMiningData called with both services
4. ✅ Expansion points computed by ExpansionPointService
5. ✅ Valid points registered with ExpansionPointDrawService
6. ✅ Console logs show all registrations
   - "Registered expansion draw point E1 at (52.50, 50.00) contested=False"
   - "Registered expansion draw point E2 at (83.00, 77.00) contested=True"
   - "Registered expansion draw point E2-Alt1 at (81.00, 83.00) contested=True"

### Every Frame (Debug Enabled)
7. ✅ DrawExpansionPoints called
8. ✅ Points retrieved from service
9. ✅ For each point:
   - ✅ Green sphere drawn at (X, Y, Z=12) if standard
   - ✅ Yellow sphere drawn if contested primary
   - ✅ Orange sphere drawn if contested alternate
   - ✅ Label drawn above sphere
   - ✅ Console logs show drawing: "Drew 'E1' at (52.50, 50.00, 12.00)"

### Visual Output
- **Green spheres**: Standard expansion bases (one per expansion)
- **Yellow + Orange spheres**: Contested bases (two per contested expansion)
- **Labels**: E1, E2, E1-Alt1, etc.
- **Position**: Z=12 (above terrain, below flying units)

## Key Changes Made

| File | Change | Purpose |
|------|--------|---------|
| BabySharkBot.cs | Added ExpansionPointService creation | Create the computation service |
| BabySharkBot.cs | Pass ExpansionPointService to manager | Inject into initialization flow |
| BabySharkMiningManager.cs | Added _expansionPointService field | Store for use in OnStart |
| BabySharkMiningManager.cs | Updated constructor parameter | Accept service injection |
| BabySharkMiningManager.cs | Pass to GetNewMiningData | Make available for computation |
| InitialMapData.cs | Retrieve all expansion points | Access computed placements |
| InitialMapData.cs | Register with draw service | Make available for drawing |

## Ready for Testing

All components are now connected and ready to draw:
1. Build solution
2. Run with Debug = true
3. Look for colored spheres on the map at expansion locations
4. Verify labels appear (E1, E2, E1-Alt1, etc.)
5. Check console output for all registration and drawing logs
