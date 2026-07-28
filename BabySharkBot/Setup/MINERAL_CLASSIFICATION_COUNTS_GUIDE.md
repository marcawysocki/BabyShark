# Mineral Classification Counts Reference

## Summary Format
Console output pattern: `Classification summary: {N}N, {L}L, {F}F`

Where:
- **N** = Count of Near minerals (close to townhall, distance ≤ avg - 0.25)
- **L** = Count of Large minerals that are Far (isolated, far from townhall)
- **F** = Count of standard Far minerals (distance > avg - 0.25, not Large)

Total minerals = N + L + F (always 8 for standard maps)

## Common Map Patterns

### Pattern A: Symmetric Near/Far (No Large)
**Examples**: Altitude, Most ladder maps
```
Classification summary: 4N, 0L, 4F
Classification summary: 3N, 0L, 5F
Classification summary: 5N, 0L, 3F
```
**Characteristics**:
- Clean split between Near and Far
- No isolated large mineral patches
- Standard 1:1 or 2:1 distribution ratio

### Pattern B: Central Cluster with Large Mineral
**Examples**: TritonLE, Maps with center staging
```
Classification summary: 3N, 1L, 4F
Classification summary: 4N, 1L, 3F
Classification summary: 3N, 2L, 3F
```
**Characteristics**:
- 1-2 large isolated minerals far from townhall
- Smaller cluster of normal minerals near townhall
- Large minerals are strategic (high value, moderate distance)

### Pattern C: Poor Split with Edge Cases
**Before 0.25 offset** (not using):
- Problem: Minerals exactly at average boundary
- Result: Mixed N/F classification on same patch

**After 0.25 offset** (current):
- Cleaner splits
- Fewer edge cases
- More predictable patterns

## Worker Assignment Strategy by Pattern

### Strategy for 4N, 0L, 4F
Best case - good distribution
```
Worker 1 → N1 + F1       (balanced workload)
Worker 2 → N2 + F2       (balanced workload)
Worker 3 → N3 + F3       (balanced workload)
Worker 4 → N4 + F4       (balanced workload)
```

### Strategy for 3N, 0L, 5F
More Far minerals available
```
Worker 1 → N1            (light load, flexibility)
Worker 2 → N2 + F1       (balanced)
Worker 3 → N3 + F2       (balanced)
Worker 4 → F3 + F4 + F5  (specialist on Far minerals)
```

### Strategy for 3N, 1L, 4F
One valuable large mineral
```
Worker 1 → N1            (near specialist)
Worker 2 → N2 + L1       (hybrid: secure Near, add valuable Large)
Worker 3 → N3 + F1       (balanced)
Worker 4 → F2 + F3 + F4  (Far specialists)
```

### Strategy for 5N, 0L, 3F
Limited Far minerals (expansion-heavy maps)
```
Worker 1 → N1 + N2       (dual Near)
Worker 2 → N3 + N3       (dual Near)
Worker 3 → N5 + F1       (top off with Far)
Worker 4 → F2 + F3       (Far backup)
```

## Reading Console Output

### Example Output Line
```
M[8] = mineral[5] at (24.50,32.25) distance=18.75 Large L8
  ^    ^           ^                ^           ^     ^^
  |    |           |                |           |     ||
  |    |           |                |           |     |+-- Label (L=Large, N=Near, F=Far)
  |    |           |                |           |     +--- Index in labeling (1-8)
  |    |           |                |           +--------- Size classification
  |    |           |                +-------------------- Distance to townhall
  |    |           +----------------------------------- Position coordinates
  |    +----------------------------------------------- Original mineral index
  +----------------------------------------------- Greedy chain position (8=first)
```

### Reading Sizes
- **Large**: Isolated mineral patch, far from others, strategic value
- **Normal**: Standard mineral in cluster
- **Small**: Part of tight cluster (rarely used currently)

## Tuning Parameters

### Threshold Offset
Location: `InitialMapData.GreedyOrderMinerals()` method
```csharp
var nearThreshold = avgTownhallDistance > 0.25f 
    ? avgTownhallDistance - 0.25f 
    : avgTownhallDistance;
```

**Adjust if**:
- Too many minerals classified as Near → Increase offset (e.g., 0.35)
- Too many minerals classified as Far → Decrease offset (e.g., 0.15)
- Exact average minerals causing issues → Increase offset (current best: 0.25)

### Size Detection Proximity Threshold
Location: `ClassifyMineralSizes()` method
```csharp
const float proximityThreshold = 3.5f;  // Units
```

**Adjust if**:
- Too many minerals classified as Large → Decrease threshold (e.g., 3.0)
- Too few minerals classified as Large → Increase threshold (e.g., 4.0)

### Size Detection Neighbor Count
Location: `ClassifyMineralSizes()` method
```csharp
if (closeNeighbors >= 3)  // Threshold for Normal vs Large
```

**Adjust if**:
- Too many minerals classified as Large → Increase neighbor threshold (e.g., 2)
- Too few minerals classified as Large → Decrease neighbor threshold (e.g., 4)

## Debug Checklist

When verifying correct classification:

- [ ] Console logs show expected pattern (e.g., 3N, 1L, 4F)
- [ ] All 8 minerals are classified (N + L + F = 8)
- [ ] Near minerals (N) have smaller distances to townhall
- [ ] Large minerals (L) are marked as isolated and far
- [ ] Regular Far minerals (F) fill remaining count
- [ ] No mineral is double-counted
- [ ] Classification summary printed before mineral detail lines
- [ ] Size printed as "Large", "Normal", or "Small"

## Future Enhancements

Potential improvements to classification:
1. **Radius-based**: If BufferRadius available from unit data, use actual mineral size
2. **Density analysis**: More sophisticated clustering to detect patch types
3. **Heatmap analysis**: Use mineral positions to identify "dense" vs "sparse" areas
4. **Economic modeling**: Factor in gathering efficiency by size + distance
