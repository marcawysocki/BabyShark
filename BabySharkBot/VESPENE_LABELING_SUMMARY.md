# Vespene Labeling System - V1/V2 Implementation

## Overview
Implemented a vespene labeling system that mirrors the mineral labeling approach, labeling vespenes based on distance to W4 (4th starting worker).

**Labeling Scheme:**
- **V1** = Closest vespene geyser to W4 (4th starting worker)
- **V2** = Next closest vespene geyser to W4
- (V3, V4, etc. for additional vespenes if they exist on a map)

## Changes Made

### 1. **BaseDtos.cs** - Added `OrderedVespene` Class
**Location:** Lines 136-166

**Structure:**
```csharp
[MemoryPackable]
public partial class OrderedVespene
{
    public Vector2Dto Position { get; set; }      // Vespene position coordinates
    public int Index { get; set; }                 // Index: 1-2 (V1-V2)
    public float DistanceToW4 { get; set; }        // Distance from vespene to W4
    public string Label { get; set; } = "";        // Label: V1, V2, etc.
}
```

**Key Features:**
- Fully serializable with MemoryPack (binary format)
- Stores position, index, distance to W4, and label
- Index 1 = V1 (closest), Index 2 = V2 (next closest)

### 2. **BaseDtos.cs** - Added `OrderedMainVespene` to MawBaseLocationData
**Location:** Lines 197-204

```csharp
/// <summary>
/// Ordered vespenes (by distance to W4) for each start location.
/// OrderedMainVespene[0] = greedy-ordered vespenes at start location 0 (V1-V2)
/// OrderedMainVespene[1] = greedy-ordered vespenes at start location 1, etc.
/// Each entry contains Position, Index (1-2), distance to W4, and Label (V1 or V2).
/// Index 1 = closest to W4, Index 2 = next closest to W4.
/// </summary>
public List<List<OrderedVespene>> OrderedMainVespene { get; set; } = new List<List<OrderedVespene>>();
```

### 3. **InitialMapData.cs** - Added Vespene Ordering Logic
**Location:** Lines 766-835 (new section after mineral ordering)

**Algorithm:**
1. For each start location, identify W4 (4th worker at index 3 in multiStartingUnits[si])
2. Calculate distance from each vespene to W4
3. Sort vespenes by distance (ascending)
4. Assign labels: V1 (closest), V2 (next closest), V3 (if exists), etc.
5. Store as `OrderedVespene` objects with position, index, distance, and label

**Implementation Pattern:**
```csharp
// Sort vespenes by distance to W4
var vespeneDistances = vespenes
    .Select((vespene, idx) => new 
    { 
        Vespene = vespene, 
        Index = idx, 
        Distance = Vector2.Distance(
            new Vector2(vespene.X, vespene.Y),
            new Vector2(w4Position.X, w4Position.Y)
        )
    })
    .OrderBy(v => v.Distance)
    .ToList();

// Assign V1, V2, V3, etc.
for (int vi = 0; vi < vespeneDistances.Count; vi++)
{
    var label = $"V{vi + 1}";  // V1, V2, V3, etc.
    // Create OrderedVespene...
}
```

## Design Rationale

### Why W4 (4th Worker)?
- **Mineral Assignment**: W1-W3 gather minerals (Team 1)
- **Vespene Assignment**: W4+ gather gas (Teams 2-4)
- W4 is the first worker dedicated to vespene handling
- Ordering vespenes by distance to W4 optimizes gas worker pathing

### Why Order by Distance?
- **Efficiency**: V1 (closest vespene) requires shortest travel time from W4's spawn position
- **Consistency**: Mirrors mineral ordering (which starts from W1, furthest worker)
- **Predictability**: Always processes nearest vespenes first, just like nearest minerals

### Data Structure Integration
- `Vector2Dto` already supports X, Y, Z coordinates
- Each vespene position can include terrain elevation (Z component)
- MemoryPack serialization ensures binary compatibility with map .dat files

## Usage Example

```csharp
// From map load
var mapData = MapDataManager.LoadMapData(mapName);

// Access ordered vespenes for Start[0]
var vespenes = mapData.OrderedMainVespene[0];

// Get V1 (closest to W4)
var v1 = vespenes[0];  // Index = 1, Label = "V1"
Console.WriteLine($"V1 at ({v1.Position.X}, {v1.Position.Y}), distance to W4: {v1.DistanceToW4}");

// Get V2 (next closest to W4)
var v2 = vespenes[1];  // Index = 2, Label = "V2"
Console.WriteLine($"V2 at ({v2.Position.X}, {v2.Position.Y}), distance to W4: {v2.DistanceToW4}");
```

## Related Worker Labeling
This vespene system integrates with the existing worker labeling:

- **W1**: Furthest mineral worker (label: "D4" if 4 workers)
- **W2-W3**: Mineral workers 2-3
- **W4**: First vespene worker (labels vespenes as V1, V2)
- **W5-W8+**: Additional vespene workers and military

## Logging
Console output shows vespene assignment progress:
```
InitialMapData: Start[0] V1 = vespene at distance 5.23 from W4
InitialMapData: Start[0] V2 = vespene at distance 7.81 from W4
InitialMapData: Calculated vespene ordering for all start locations
```

## Future Enhancements
1. **Gas Worker Targeting**: Use V1/V2 labels to assign W4+ workers to specific geysers
2. **Dynamic Reassignment**: Re-order vespenes based on actual in-game positions if units move
3. **Visualization**: Draw V1/V2 labels on debug overlay (similar to N1-N4/F1-F4 minerals)
4. **Multi-base Support**: Extend ordering to expansion bases using expansion center as reference point

## Files Modified
- `BabySharkBot/Setup/BaseDtos.cs`: Added `OrderedVespene` class and `OrderedMainVespene` property
- `BabySharkBot/Setup/InitialMapData.cs`: Added vespene ordering algorithm (lines 766-835)

## Compilation Status
✅ No compile errors
✅ MemoryPack serialization compatible
✅ Binary format persistence ready
