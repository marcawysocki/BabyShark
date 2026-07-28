# Expansion Point Drawing Implementation

## Overview
Implemented drawing system for expansion townhall placement points following the BABYSHARK ARCHITECTURE pattern from copilot-instructions.md.

## Architecture Pattern Applied
Followed the THREE-PART DRAWING SERVICE pattern:

1. **Service Layer** - ExpansionPointDrawService.cs
2. **Manager Layer** - BabySharkMiningManager.DrawExpansionPoints()
3. **Registration Layer** - InitialMapData.cs populates service

## Files Created

### BabySharkBot/Services/ExpansionPointDrawService.cs
- Service class with internal Dictionary<string, ExpansionPointData>
- Methods:
  - `SetExpansionPoint()` - Register point for drawing
  - `GetAllPoints()` - Retrieve stored points
  - `Clear()` - Clear registry
- Enforces Z ≥ 12 for visibility above terrain
- Stores position, label, color, contested flag

## Files Modified

### BabySharkBot/Managers/BabySharkMiningManager.cs
1. Added field: `private ExpansionPointDrawService _expansionPointDrawService;`
2. Updated constructor to accept and store service parameter
3. Updated OnStart() to pass service to InitialMapData.GetNewMiningData()
4. Added OnFrame() call: `DrawExpansionPoints();`
5. Implemented `DrawExpansionPoints()` method:
   - Gets all registered points from service
   - Draws green sphere + label for each point
   - Uses Sharky's DrawSphere() and DrawText() primitives
   - Z = 12.0f for visibility
   - Logs all drawn points

### BabySharkBot/BabySharkBot.cs
1. Instantiated: `var expansionPointDrawService = new ExpansionPointDrawService();`
2. Updated BabySharkMiningManager constructor call to pass service

### BabySharkBot/Setup/InitialMapData.cs
1. Updated GetNewMiningData() method signature:
   - Added parameter: `ExpansionPointDrawService? expansionPointDrawService = null`
   - Also added existing: `ExpansionPointService? expansionPointService = null`
2. After expansion points are computed by ExpansionPointService:
   - Retrieves all computed expansion points via `GetAllExpansionPoints()`
   - For each placement option:
     - Generates label (e.g., "E1" for standard, "E1-Alt1" for contested alternate)
     - Color-codes:
       - **Green** - Standard expansion (not contested)
       - **Yellow** - Contested expansion, primary placement
       - **Orange** - Contested expansion, alternate placement
     - Creates Point with Z = 12.0f
     - Calls `expansionPointDrawService.SetExpansionPoint()`
   - Logs all registrations

## Drawing Output

### Visual Elements
- **Sphere**: 0.75f radius at each townhall placement point
- **Text Label**: Position name (e.g., "E1", "E2-Alt1")
- **Z-Coordinate**: 12.0f (above terrain but visible)

### Color Scheme
- **Green (0, 255, 0)**: Standard expansion bases
- **Yellow (255, 255, 0)**: Contested bases - primary placement
- **Orange (255, 165, 0)**: Contested bases - alternate placement options

### Logging
Each step logs to console:
- Service initialization
- Point registration (expansion index, coordinates, contested status)
- Frame drawing (point count, coordinates)
- Errors with full context

## Execution Flow

```
OnStart()
  ↓
GetNewMiningData()
  ↓
Compute expansion clusters
  ↓
ExpansionPointService.ComputeExpansionPoint()
  ↓
Retrieve all computed points
  ↓
Register with ExpansionPointDrawService (colors, labels, Z=12)
  ↓
OnFrame() → DrawExpansionPoints()
  ↓
DrawSphere() + DrawText() for each point
```

## Key Design Decisions

1. **Z = 12.0f**: Ensures visibility above terrain (following Sharky's standard)
2. **Color coding**: Immediate visual distinction between standard and contested bases
3. **Separate service**: Follows SEPARATION OF CONCERNS - InitialMapData only generates data, MiningManager only draws
4. **Lazy evaluation**: Points only drawn when debug enabled (ManagerDebugService.IsDebugEnabled check)
5. **Label generation**: Smart naming for contested alternates (E1-Alt1, E1-Alt2) vs standard (E1)

## Next Steps for Testing

1. Build solution (should compile with 0 errors)
2. Run game with SharkyOptions.Debug = true
3. Verify green/yellow/orange spheres appear at expansion points
4. Verify labels appear above spheres (E1, E1-Alt1, E1-Alt2, etc.)
5. Verify contested bases show 2 alternate placements in different colors
6. Verify standard bases show single green placement

## Integration Points

- **ExpansionPointService**: Computes valid placement points (already working)
- **ExpansionCOMService**: Visualizes cluster centers (blue crosshairs)
- **InitialMapData**: Orchestrates all map analysis
- **BabySharkMiningManager**: Central drawing hub
- **ManagerDebugService**: Sharky debug primitive access
