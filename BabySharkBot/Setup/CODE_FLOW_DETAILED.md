# Code Flow: Mineral Classification with Improvements

## Data Flow Diagram

```
GetNewMiningData()
    ↓
[Parse observation for minerals]
    ↓
multiMainMinerals[si] = [Vector2Dto, Vector2Dto, ...]
    ↓
GreedyOrderMinerals(minerals, w1Position, comPosition, townhallPosition, si)
    ├─ Phase 1: Find M[8] (furthest from W1)
    │   └─ Create OrderedMineral
    │       ├─ Position
    │       ├─ Index = 8
    │       ├─ DistanceFromCOM (for viz reference)
    │       ├─ DistanceToTownhall (NEW)
    │       └─ IsNear = (distance ≤ avgThreshold - 0.25) (UPDATED)
    │
    ├─ Phase 2: Greedy chain M[7-1] (closest to previous)
    │   └─ For each mineral:
    │       ├─ Calculate DistanceToTownhall
    │       ├─ Calculate threshold with 0.25 offset (UPDATED)
    │       └─ Create OrderedMineral with IsNear classification
    │
    └─ Phase 3: Classify by size (NEW)
        └─ ClassifyMineralSizes(orderedList, minerals)
            ├─ For each mineral:
            │   ├─ Count neighbors within 3.5 units
            │   ├─ IF neighbors ≥ 3 → Size = Normal
            │   ├─ ELSE IF isolated AND far → Size = Large
            │   └─ ELSE → Size = Normal
            │
            └─ Calculate and log classification counts
                ├─ Count Near minerals (IsNear = true)
                ├─ Count Large minerals (Size = Large AND IsNear = false)
                └─ Count Far minerals (remaining)
                    └─ Output: "Classification summary: 3N, 1L, 4F"
    ↓
Return List<OrderedMineral> with all properties populated
    ↓
tempBaseDto.OrderedMainMinerals[si] = result
    ↓
Save to .dat file (serialized)
    ↓
In-game: MineralLabelService uses IsNear + Size to create labels
    └─ N1-N4 (Cyan) for Near minerals
    └─ L1-L4 (Custom color) for Large Far minerals
    └─ F1-F4 (Magenta) for regular Far minerals
```

## Detailed Code Sections

### 1. Threshold Calculation (M[8])

**Location**: `InitialMapData.GreedyOrderMinerals()`, line ~930-950

```csharp
// Get average distance from townhall to all minerals
var avgTownhallDistance = townhallPosition != null
    ? minerals.Average(m => Vector2.Distance(
        new Vector2(m.X, m.Y), 
        new Vector2(townhallPosition.X, townhallPosition.Y)))
    : float.MaxValue;

// Calculate this specific mineral's distance
var distanceToTownhall = townhallPosition != null
    ? Vector2.Distance(
        new Vector2(mineral.X, mineral.Y), 
        new Vector2(townhallPosition.X, townhallPosition.Y))
    : float.MaxValue;

// Apply 0.25 offset for cleaner classification (NEW)
var nearThreshold = avgTownhallDistance > 0.25f 
    ? avgTownhallDistance - 0.25f 
    : avgTownhallDistance;

// Classify as Near if below threshold
result.Add(new OrderedMineral
{
    Position = mineral,
    Index = 8,
    OriginalIndex = bestIdx,
    DistanceFromCOM = distFromCom,
    DistanceToTownhall = distanceToTownhall,  // NEW: Store for reference
    IsNear = distanceToTownhall <= nearThreshold  // UPDATED: With offset
});
```

### 2. Threshold Calculation (M[7-1] Greedy Chain)

**Location**: `InitialMapData.GreedyOrderMinerals()`, line ~980-1005

```csharp
while (remainingIndices.Count > 0 && chainIndex >= 1)
{
    // Find nearest mineral to current position (greedy)
    int nearestIdx = -1;
    float nearestDist = float.MaxValue;
    foreach (var idx in remainingIndices)
    {
        var mineral = minerals[idx];
        var dist = Vector2.Distance(
            new Vector2(mineral.X, mineral.Y), 
            new Vector2(currentX, currentY));
        if (dist < nearestDist)
        {
            nearestDist = dist;
            nearestIdx = idx;
        }
    }

    if (nearestIdx >= 0)
    {
        var mineral = minerals[nearestIdx];
        var distFromCom = comPosition != null
            ? Vector2.Distance(
                new Vector2(mineral.X, mineral.Y), 
                new Vector2(comPosition.X, comPosition.Y))
            : float.MaxValue;

        // Same threshold calculation as M[8] (UPDATED)
        var avgTownhallDistance = townhallPosition != null
            ? minerals.Average(m => Vector2.Distance(
                new Vector2(m.X, m.Y), 
                new Vector2(townhallPosition.X, townhallPosition.Y)))
            : float.MaxValue;

        var distanceToTownhall = townhallPosition != null
            ? Vector2.Distance(
                new Vector2(mineral.X, mineral.Y), 
                new Vector2(townhallPosition.X, townhallPosition.Y))
            : float.MaxValue;

        var nearThreshold = avgTownhallDistance > 0.25f 
            ? avgTownhallDistance - 0.25f 
            : avgTownhallDistance;

        result.Add(new OrderedMineral
        {
            Position = mineral,
            Index = chainIndex,
            OriginalIndex = nearestIdx,
            DistanceFromCOM = distFromCom,
            DistanceToTownhall = distanceToTownhall,
            IsNear = distanceToTownhall <= nearThreshold
        });

        currentX = mineral.X;
        currentY = mineral.Y;
        remainingIndices.Remove(nearestIdx);
        chainIndex--;
    }
    else
    {
        break;
    }
}
```

### 3. Size Classification Method (NEW)

**Location**: `InitialMapData.ClassifyMineralSizes()`

```csharp
private void ClassifyMineralSizes(List<OrderedMineral> orderedMinerals, List<Vector2Dto> allMinerals)
{
    if (orderedMinerals == null || orderedMinerals.Count == 0 || allMinerals == null)
        return;

    try
    {
        // Step 1: Calculate average distance between all mineral pairs
        float totalDist = 0;
        int pairCount = 0;
        for (int i = 0; i < allMinerals.Count; i++)
        {
            for (int j = i + 1; j < allMinerals.Count; j++)
            {
                var d = Vector2.Distance(
                    new Vector2(allMinerals[i].X, allMinerals[i].Y), 
                    new Vector2(allMinerals[j].X, allMinerals[j].Y));
                totalDist += d;
                pairCount++;
            }
        }
        float avgMineralDist = pairCount > 0 ? totalDist / pairCount : 1f;

        // Step 2: For each mineral, count close neighbors
        foreach (var ord in orderedMinerals)
        {
            int closeNeighbors = 0;
            const float proximityThreshold = 3.5f;

            foreach (var other in allMinerals)
            {
                // Skip self
                if (Math.Abs(other.X - ord.Position.X) < 0.01f && 
                    Math.Abs(other.Y - ord.Position.Y) < 0.01f)
                    continue;

                // Count neighbors within threshold
                var d = Vector2.Distance(
                    new Vector2(ord.Position.X, ord.Position.Y), 
                    new Vector2(other.X, other.Y));
                if (d < proximityThreshold)
                    closeNeighbors++;
            }

            // Step 3: Classify based on neighbor count
            if (closeNeighbors >= 3)
            {
                // Many neighbors → Part of tight cluster
                ord.Size = MineralSize.Normal;
            }
            else if (closeNeighbors <= 1 && !ord.IsNear && ord.DistanceToTownhall > avgMineralDist)
            {
                // Isolated, Far, and far from average → Strategic large mineral
                ord.Size = MineralSize.Large;
            }
            else
            {
                // Everything else
                ord.Size = MineralSize.Normal;
            }
        }

        Console.WriteLine($"InitialMapData.ClassifyMineralSizes: Classified {orderedMinerals.Count} minerals, avg inter-mineral distance = {avgMineralDist:F2}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"InitialMapData.ClassifyMineralSizes: Error during classification: {ex.Message}");
    }
}
```

### 4. Console Logging (UPDATED)

**Location**: `InitialMapData.GreedyOrderMinerals()`, line ~1020-1047

```csharp
// Count classification summary
int nearCount = 0, largeCount = 0, farCount = 0;
foreach (var ord in result)
{
    if (ord.Size == MineralSize.Large && !ord.IsNear)
        largeCount++;
    else if (ord.IsNear)
        nearCount++;
    else
        farCount++;
}

Console.WriteLine($"InitialMapData.GreedyOrderMinerals: Classification summary: {nearCount}N, {largeCount}L, {farCount}F");

foreach (var ord in result)
{
    string sizeStr = ord.Size == MineralSize.Large ? "Large" : 
                     (ord.Size == MineralSize.Normal ? "Normal" : "Small");
    var label = ord.IsNear ? $"N{ord.Index}" : 
                (ord.Size == MineralSize.Large ? $"L{ord.Index}" : $"F{ord.Index}");
    Console.WriteLine($"  M[{ord.Index}] = mineral[{ord.OriginalIndex}] at ({ord.Position.X:F2},{ord.Position.Y:F2}) distance={ord.DistanceToTownhall:F2} {sizeStr} {label}");
}
```

## Execution Sequence Example

### Input Data (Map with center cluster)
```
8 Minerals detected at:
[0] (10, 20)  ← Near townhall
[1] (12, 22)  ← Part of cluster
[2] (14, 24)  ← Part of cluster
[3] (30, 40)  ← Isolated, far
[4] (20, 30)  ← Medium distance
[5] (22, 32)  ← Medium distance
[6] (18, 28)  ← Medium distance
[7] (25, 35)  ← Far

Townhall at: (11, 21)
```

### Phase 1: Calculate Average Distance
```
Distance from each mineral to townhall:
[0] distance ≈ 1.4
[1] distance ≈ 1.4
[2] distance ≈ 4.2
[3] distance ≈ 21.0
[4] distance ≈ 11.7
[5] distance ≈ 13.8
[6] distance ≈ 7.6
[7] distance ≈ 17.8

Average = (1.4 + 1.4 + 4.2 + 21.0 + 11.7 + 13.8 + 7.6 + 17.8) / 8 = 9.86
Threshold = 9.86 - 0.25 = 9.61
```

### Phase 2: Greedy Ordering from W1
```
Assuming W1 at [0]:
M[8] = mineral[3] (furthest from W1) → distance=21.0 → IsNear=false
M[7] = greedy closest to M[8]
M[6] = greedy closest to M[7]
...and so on
```

### Phase 3: Size Classification
```
For mineral[3] at (30, 40):
- Close neighbors (within 3.5 units): None
- Neighbor count: 0
- IsNear: false
- DistanceToTownhall: 21.0 > avgMineralDist (9.86)
→ Classify as LARGE

For mineral[0] at (10, 20):
- Close neighbors (within 3.5 units): mineral[1]
- Neighbor count: 1
- IsNear: true
→ Classify as NORMAL (IsNear overrides isolated check)

For mineral[1] at (12, 22):
- Close neighbors: mineral[0], mineral[2] (partial)
- Neighbor count: 2
- IsNear: true
→ Classify as NORMAL
```

### Output
```
InitialMapData.GreedyOrderMinerals: Start[0] ordering complete:
InitialMapData.GreedyOrderMinerals: Classification summary: 3N, 1L, 4F
  M[8] = mineral[3] at (30.00,40.00) distance=21.00 Large L8
  M[7] = mineral[5] at (22.00,32.00) distance=13.80 Normal F7
  ...
  M[1] = mineral[0] at (10.00,20.00) distance=1.40 Normal N1
InitialMapData.ClassifyMineralSizes: Classified 8 minerals, avg inter-mineral distance = 10.87
```

## Key Differences: Before vs After

### Before (v1)
```csharp
// Only two properties
IsNear = distanceToTownhall <= avgTownhallDistance
DistanceFromCOM = calculated (unused for classification)
Size = (not tracked)

// Output
M[8] = mineral[3] at (30.00,40.00) distance=21.00 F8
```

### After (v2 - Current)
```csharp
// Five properties
IsNear = distanceToTownhall <= (avgTownhallDistance - 0.25)
DistanceFromCOM = calculated (still for visualization)
DistanceToTownhall = stored (used for classification)
Size = classified (Large/Normal/Small)
Label = derived (N#/L#/F#)

// Output
M[8] = mineral[3] at (30.00,40.00) distance=21.00 Large L8
```

## Performance Considerations

- **ClassifyMineralSizes**: O(n²) for all-pairs distance calculation
  - 8 minerals = 28 pairs = negligible
  - Only runs once at map load time
  
- **Neighbor counting**: O(n²) per mineral
  - 8 × 8 = 64 distance checks = negligible
  - Only runs once at map load time

- **Console output**: Minimal overhead, debug only

## Data Persistence

All changes are serialized to .dat file:
```csharp
[MemoryPackable] OrderedMineral
  - Position (already serialized)
  - Index (already serialized)
  - IsNear (already serialized, now with offset logic)
  - DistanceFromCOM (already serialized)
  - DistanceToTownhall (NEW, serialized)
  - Size (NEW, serialized as enum int)
  - OriginalIndex (already serialized)
```

File version may need update if upgrading from old .dat files (they won't have new properties).
