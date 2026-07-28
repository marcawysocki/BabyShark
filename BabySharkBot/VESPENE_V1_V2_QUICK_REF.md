# Vespene Labeling Quick Reference - V1/V2

## Quick Answer
**Yes, Vector2Dto supports x, y, z coordinates.**

Vector2Dto has three float properties:
- `X` (float)
- `Y` (float)  
- `Z` (float)

It provides two constructors:
- `Vector2Dto(float x, float y)` → Z defaults to 0
- `Vector2Dto(float x, float y, float z)` → explicit Z value

---

## What Was Just Implemented

### Vespene Labeling System
**Purpose:** Order vespenes by proximity to W4 (4th worker), enabling efficient gas mining assignments.

**Labels:**
- **V1** = Closest vespene to W4
- **V2** = Next closest vespene to W4
- (V3, V4+ for additional vespenes if they exist)

### New Classes
- `OrderedVespene`: Stores vespene position, index, distance to W4, and label

### New Data Structure
- `OrderedMainVespene[si]` in `MawBaseLocationData`: Lists ordered vespenes for each start location

### Algorithm
1. Identify W4 position (worker at index 3 in multiStartingUnits[si])
2. Calculate distance from each vespene to W4
3. Sort vespenes by distance (ascending)
4. Assign labels V1, V2, V3, etc.

---

## Data Flow

```
Game Start
    ↓
GetNewMiningData() called
    ↓
Scan units for:
  • Minerals → multiMainMinerals
  • Vespenes → multiMainVespene
  • Workers → multiStartingUnits
    ↓
GreedyOrderMinerals() 
  → Creates OrderedMineral[] 
  → M8-M1 by W1 distance
    ↓
GreedyOrderVespenes() [NEW]
  → Creates OrderedVespene[] 
  → V1-V2 by W4 distance
    ↓
MapData saved to .dat file
  → OrderedMainMinerals included
  → OrderedMainVespene included [NEW]
```

---

## Usage in Your Bot

```csharp
// After map data is loaded
var mapData = tempBaseDto;  // From GetNewMiningData()

// Access vespenes for your start location
var myVespenes = mapData.OrderedMainVespene[0];

// V1 is always the closest to W4
if (myVespenes.Count > 0)
{
    var v1 = myVespenes[0];
    Console.WriteLine($"V1: Position=({v1.Position.X}, {v1.Position.Y}), Distance to W4={v1.DistanceToW4}");
    // Assign W4 to mine from V1
}

// V2 is the next closest
if (myVespenes.Count > 1)
{
    var v2 = myVespenes[1];
    Console.WriteLine($"V2: Position=({v2.Position.X}, {v2.Position.Y}), Distance to W4={v2.DistanceToW4}");
    // Assign W5 to mine from V2
}
```

---

## Key Design Points

| Aspect | Detail |
|--------|--------|
| **Worker** | W4 (4th worker) is reference point for vespene ordering |
| **Sorting** | Distance-based (closest first = V1) |
| **Storage** | Binary serialized in map .dat files via MemoryPack |
| **Coordinates** | Full x,y,z support via Vector2Dto |
| **Fallback** | If < 4 workers at start, vespenes not ordered (logged) |

---

## Console Output Example

```
InitialMapData: Start[0] V1 = vespene at distance 5.23 from W4
InitialMapData: Start[0] V2 = vespene at distance 7.81 from W4
InitialMapData: Calculated vespene ordering for all start locations
```

---

## Files Changed

| File | Lines | What |
|------|-------|------|
| `BaseDtos.cs` | 136-166 | `OrderedVespene` class definition |
| `BaseDtos.cs` | 216-223 | `OrderedMainVespene` property in `MawBaseLocationData` |
| `InitialMapData.cs` | 766-833 | Vespene ordering algorithm |

---

## Why This Approach?

1. **Mirrors Minerals**: Same pattern as mineral ordering (M8-M1 by W1)
2. **Optimal Pathing**: V1 is shortest travel for W4 from worker spawn
3. **Predictable**: Always processes nearest vespene first
4. **Persistent**: Stored in map .dat files for consistent behavior across games
5. **Extensible**: Can handle maps with 3+ vespenes per base

---

## Next Steps (Optional)

1. **Draw V1/V2 Labels** - Add to debug visualization similar to N1-N4/F1-F4 minerals
2. **Gas Worker Assignment** - Use V1/V2 labels in worker assignment logic
3. **Dynamic Updates** - Re-order if units move significantly during game
4. **Expansion Support** - Extend to expansion bases
