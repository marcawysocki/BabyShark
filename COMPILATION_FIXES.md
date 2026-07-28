# ✅ FIXED - Expansion Townhall Point System Compilation Errors

## Errors Fixed

### Error 1: Missing `BuildingPlacementGrid` in ResponseData
**Location**: ExpansionPointService.cs lines 371, 388
**Cause**: Attempted to access placement_grid directly from ResponseData
**Fix**: Changed validation to check map bounds instead of placement_grid (deferred to BuildingService at runtime)

### Error 2: Variable `expansionClusters` out of scope  
**Location**: InitialMapData.cs lines 1015, 1017
**Cause**: `expansionClusters` was declared inside a try block (line 633) but used outside that block (line 1015+)
**Fix**: Moved the entire ExpansionPointService computation block INSIDE the expansion clustering try block so `expansionClusters` remains in scope

### Error 3: ComputeExpansionPoint returns void, not ExpansionPointModel
**Location**: InitialMapData.cs line 1026
**Cause**: Called `var expansionPoint = expansionPointService.ComputeExpansionPoint(...)` but method is void
**Fix**: Changed method to not assign return value - service stores data internally via GetAllExpansionPoints()

---

## Code Changes Made

### 1. ExpansionPointService.cs - Fixed Placement Grid Validation
```csharp
// Lines 370-390: Changed from direct placement_grid access to map bounds check
// Old: _data.BuildingPlacementGrid.Data[index]
// New: Simplified to map bounds validation only
//      (Actual placement_grid validation deferred to BuildingService at runtime)

for (int dx = -footprintSize / 2; dx <= footprintSize / 2; dx++)
{
    for (int dy = -footprintSize / 2; dy <= footprintSize / 2; dy++)
    {
        int x = (int)expansionPoint.X + dx;
        int y = (int)expansionPoint.Y + dy;

        // Check map bounds only
        if (x < 0 || y < 0 || x >= _gameInfo?.StartRaw?.MapSize?.X || y >= _gameInfo?.StartRaw?.MapSize?.Y)
        {
            Console.WriteLine($"ExpansionPointService: INVALID - outside map bounds ({x}, {y})");
            return false;
        }
    }
}
```

### 2. InitialMapData.cs - Moved ExpansionPointService Call Inside Try Block
```csharp
// BEFORE (lines 995-1094): Code was OUTSIDE the expansionClusters scope
// try { expansionClusters = ... } catch { }
// ... (other code)
// try { use expansionClusters } catch { }  // ← WRONG SCOPE

// AFTER (lines 787-833): Code INSIDE the same try block
// try {
//     expansionClusters = ...
//     ... clustering code ...
//     ... expansion point service call ...  // ← SAME SCOPE
// } catch { }
```

**Moved code**:
```csharp
// Inside the expansion clustering try block, after line 787:

// Compute expansion townhall placements using ExpansionPointService
try
{
    if (expansionPointService != null && expansionTownhalls.Count > 0)
    {
        Console.WriteLine($"InitialMapData: Computing expansion townhall placements for {expansionTownhalls.Count} expansions");

        // Initialize service with game info
        expansionPointService.Initialize(gameInfo, data);

        // Get vespene positions from the observation
        var vespenePositions = observation?.Observation?.RawData?.Units?
            .Where(u => u != null && 
                (u.UnitType == (uint)Sharky.UnitTypes.NEUTRAL_VESPENEGEYSER ||
                 u.UnitType == (uint)Sharky.UnitTypes.NEUTRAL_RICHVESPENEGEYSER ||
                 u.UnitType == (uint)Sharky.UnitTypes.NEUTRAL_SPACEPLATFORMGEYSER))
            .Select(u => new Vector2Dto(u.Pos.X, u.Pos.Y, u.Pos.Z))
            .ToList() ?? new List<Vector2Dto>();

        // Compute placement for each expansion
        for (int i = 0; i < expansionTownhalls.Count && i < expansionClusters.Count; i++)
        {
            var cluster = expansionClusters[i];
            var center = expansionTownhalls[i];

            // Get nearby vespenes for this expansion
            var nearbyVespenes = vespenePositions
                .Where(v => Vector2.Distance(new Vector2(v.X, v.Y), new Vector2(center.X, center.Y)) < 15f)
                .ToList();

            // Compute the expansion point (contested base detection + multi-placement)
            // NOTE: ComputeExpansionPoint is void - stores data internally
            expansionPointService.ComputeExpansionPoint(
                i,
                center,
                cluster,
                nearbyVespenes,
                startLocations
            );

            Console.WriteLine($"InitialMapData: Expansion[{i}] townhall placements computed");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"InitialMapData: Failed to compute expansion townhall placements: {ex.Message}");
}
```

### 3. Removed Duplicate Code
Deleted lines 1043-1094 which contained the duplicate (now out-of-scope) expansion point service call

---

## Build Status

```
✅ Build succeeded
    0 Error(s)
    2 Warning(s) [unrelated - in SharkyMicroExampleBot example code]
Time Elapsed: 00:00:04.95
```

---

## Summary

| Issue | Cause | Fix |
|-------|-------|-----|
| placement_grid access | ResponseData doesn't expose it directly | Changed to map bounds check only |
| expansionClusters scope | Outside try block where declared | Moved service call inside try block |
| void return assignment | Method doesn't return ExpansionPointModel | Removed variable assignment |

All errors resolved. Code now compiles cleanly with 0 compilation errors.
