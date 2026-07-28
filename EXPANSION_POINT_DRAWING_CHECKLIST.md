# Expansion Point Drawing - Implementation Checklist

## ✅ Service Layer - ExpansionPointDrawService.cs

- [x] Created ExpansionPointDrawService.cs in BabySharkBot/Services/
- [x] Internal Dictionary<string, ExpansionPointData> storage
- [x] SetExpansionPoint(position, label, color, isContested) method
- [x] GetAllPoints() returns Dictionary for drawing
- [x] Clear() method for cleanup
- [x] Z-coordinate enforcement (minimum Z = 12 for visibility)
- [x] Console logging for debugging

## ✅ Manager Layer - BabySharkMiningManager.cs

- [x] Added field: `private ExpansionPointDrawService _expansionPointDrawService;`
- [x] Updated constructor signature to accept ExpansionPointDrawService parameter
- [x] Constructor stores the service: `_expansionPointDrawService = expansionPointDrawService;`
- [x] Updated OnStart() to pass service to GetNewMiningData()
- [x] OnFrame() calls DrawExpansionPoints()
- [x] Implemented DrawExpansionPoints() method:
  - [x] Checks if debug enabled
  - [x] Validates service not null
  - [x] Gets all points from service
  - [x] For each point:
    - [x] Draws sphere using ManagerDebugService.DrawSphere()
    - [x] Draws label using ManagerDebugService.DrawText()
    - [x] Ensures Z ≥ 12
    - [x] Logs coordinates
  - [x] Error handling with try-catch

## ✅ Registration Layer - InitialMapData.cs

- [x] Updated GetNewMiningData() signature:
  - [x] Added `ExpansionPointDrawService? expansionPointDrawService = null` parameter
- [x] After ExpansionPointService.ComputeExpansionPoint() completes:
  - [x] Retrieves all computed points via GetAllExpansionPoints()
  - [x] For each expansion point model:
    - [x] Iterates through PlacementOptions
    - [x] Filters for valid (option.IsValid == true) placements
    - [x] Generates appropriate labels:
      - [x] Standard: "E1", "E2", etc.
      - [x] Contested primary: "E1" (optionIdx == 0)
      - [x] Contested alternate: "E1-Alt1", "E1-Alt2", etc.
    - [x] Color-codes appropriately:
      - [x] Green (0, 255, 0) for standard
      - [x] Yellow (255, 255, 0) for contested primary
      - [x] Orange (255, 165, 0) for contested alternates
    - [x] Creates Point with Z = 12.0f
    - [x] Calls expansionPointDrawService.SetExpansionPoint()
    - [x] Logs all registrations

## ✅ Integration - BabySharkBot.cs

- [x] Instantiated: `var expansionPointDrawService = new ExpansionPointDrawService();`
- [x] Updated BabySharkMiningManager constructor call:
  - [x] Passed expansionPointDrawService as final parameter

## ✅ Data Model Verification

- [x] ExpansionPointModel.cs has PlacementOptions list
- [x] TownhallPlacementOption has Point property (Vector2Dto)
- [x] TownhallPlacementOption has IsValid property
- [x] ExpansionPointModel has IsContested property

## ✅ Documentation

- [x] Created EXPANSION_POINT_DRAWING.md with architecture overview
- [x] Documented three-part pattern (Service, Manager, Registration)
- [x] Documented execution flow
- [x] Documented color scheme
- [x] Documented next steps for testing

## Expected Behavior When Running

### Game Start
1. OnStart() called
2. GetNewMiningData() called with expansionPointDrawService
3. InitialMapData analyzes map
4. ExpansionPointService computes placement points
5. ExpansionPointDrawService populated with valid placements
6. Console logs all registrations

### Every Frame (When Debug Enabled)
1. OnFrame() called
2. DrawExpansionPoints() called
3. For each registered point:
   - Green sphere (0.75f radius) drawn at position
   - Label text drawn above point
   - Z = 12.0f for visibility

### Visual Output
- **Standard Expansions**: Green spheres with label "E1", "E2", etc.
- **Contested Expansions**: 
  - Yellow sphere with label "E1"
  - Orange sphere with label "E1-Alt1"
  - (May show 2 alternate placements)

## Key Design Principles Followed

1. **SEPARATION OF CONCERNS** (from copilot-instructions.md):
   - InitialMapData: ONLY generates data, NO drawing
   - BabySharkMiningManager: ONLY draws using Sharky primitives
   - ExpansionPointDrawService: ONLY stores data

2. **Z-COORDINATE RULE** (from DRAWING_PATTERN_GUIDE.md):
   - All visualization Z = 12.0f (ensures visibility above terrain)
   - Matches Sharky's standard for debug drawings

3. **DRAWING ARCHITECTURE PATTERN** (from copilot-instructions.md):
   - Service in BaseDtos area: ExpansionPointDrawService
   - Manager draws: BabySharkMiningManager.DrawExpansionPoints()
   - Registration: InitialMapData populates service

4. **COLOR CODING**:
   - Green: Standard expansions (simple case)
   - Yellow + Orange: Contested bases (complex placement logic)

## Ready for Testing

Build command:
```
dotnet build BabyShark.sln
```

Expected result: 0 errors, 0 relevant warnings

Game testing:
1. Run with SharkyOptions.Debug = true
2. Observe expansion points with green/yellow/orange spheres
3. Verify labels appear (E1, E2, E1-Alt1, etc.)
4. Verify contested bases show 2 placements in different colors
