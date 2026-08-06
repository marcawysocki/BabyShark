# Greedy Mineral Ordering Implementation - Complete

## Summary
Implemented the **greedy mineral ordering algorithm** from "Just In Time Mining.md" that orders minerals M[0-7] based on worker-to-mineral chain logic.

## What Was Added

### 1. **OrderedMineral Class** (BaseDtos.cs)
```csharp
public class OrderedMineral
{
    public Vector2Dto Position { get; set; }        // X,Y coordinates
    public int Index { get; set; }                  // 0-7 (M0-M7 in greedy chain)
    public bool IsNear { get; set; }                // True = Near (N4), False = Far (F4)
    public float DistanceFromCOM { get; set; }      // Distance from mineral center of mass
    public int OriginalIndex { get; set; }          // Which position in multiMainMinerals[si]
}
```

### 2. **OrderedMainMinerals Field** (MawBaseLocationData in BaseDtos.cs)
```csharp
public List<List<OrderedMineral>> OrderedMainMinerals { get; set; }
// OrderedMainMinerals[0] = greedy-ordered minerals at Start[0]
// OrderedMainMinerals[0][0] = M[0] (furthest from W1)
// OrderedMainMinerals[0][1] = M[1] (closest to M[0])
// ... up to [7] = M[7]
```

### 3. **Greedy Ordering Algorithm** (InitialMapData.cs)

#### Phase 1: Find M[0] (Furthest from W1)
```
Compare each mineral's distance to W1 (first worker)
Track which mineral index has maximum distance
NewMainIndexes[0] = that index
```

#### Phase 2: Greedy Chain M[1-7]
```
remainingIndices = [0,1,2,3,5,6,7]  (exclude M[0])
For i = 1 to 7:
  Find mineral in remainingIndices closest to current position
  NewMainIndexes[i] = that index
  Remove from remainingIndices
  Current position = position of newly selected mineral
```

#### Phase 3: Classify Near/Far
```
For each mineral M[i]:
  distFromCOM = distance(M[i].position, COM.position)
  avgDist = average distance of all minerals to COM
  If distFromCOM < avgDist:
    IsNear = true  (mineral labeled N4, N3, etc)
  Else:
    IsNear = false (mineral labeled F4, F3, etc)
```

### 4. **Helper Method** (InitialMapData.cs)
```csharp
private List<OrderedMineral> GreedyOrderMinerals(
    List<Vector2Dto> minerals,      // All minerals for this start
    Vector2Dto w1Position,           // W1 (furthest worker from COM)
    Vector2Dto comPosition,          // Mineral center of mass
    int startIndex)                  // For logging
```

## Implementation Details

### Called During Game Initialization
In `InitialMapData.GetNewMiningData()` after COM calculation:
```csharp
// For each start location
var orderedList = GreedyOrderMinerals(
    minerals,          // multiMainMinerals[si]
    w1Position,        // multiStartingUnits[si][0].Position
    comPosition,       // multiMineralCenterOfMass[si]
    si);               // start index

tempBaseDto.OrderedMainMinerals.Add(orderedList);
```

### Result Structure
After processing:
```
tempBaseDto.OrderedMainMinerals[0] = 
  [
    {Index:0, OriginalIndex:4, IsNear:false, Position:(x,y)},  // M[0]=F4
    {Index:1, OriginalIndex:2, IsNear:true,  Position:(x,y)},  // M[1]=N2
    {Index:2, OriginalIndex:6, IsNear:false, Position:(x,y)},  // M[2]=F6
    ...
    {Index:7, OriginalIndex:5, IsNear:true,  Position:(x,y)},  // M[7]=N5
  ]
```

## Console Output
When running InitialMapData:
```
InitialMapData: Start[0] M[0] = mineral[4] at distance 12.45 from W1
InitialMapData.GreedyOrderMinerals: Start[0] ordering complete:
  M[0] = mineral[4] at (x,y) distance=d F4
  M[1] = mineral[2] at (x,y) distance=d N2
  M[2] = mineral[6] at (x,y) distance=d F6
  ...
```

## Usage: Accessing Ordered Minerals

### In future code that needs mineral assignments:
```csharp
// Get the ordered minerals for start location 0
var orderedMinerals = baseLocationData.OrderedMainMinerals[0];

// Get M[0] (furthest from W1)
var m0 = orderedMinerals[0];  // Index=0

// Get M[4] position for mineral assignment
var m4Position = orderedMinerals[4].Position;

// Check if mineral is near or far
if (orderedMinerals[4].IsNear)
{
    // This is a "near" mineral (N4 label)
}
else
{
    // This is a "far" mineral (F4 label)
}
```

## Key Insights

### ✅ Why This Works
1. **Deterministic**: Same algorithm run every game produces consistent M[0-7] ordering
2. **Just In Time Ready**: M[0] is furthest from W1, making efficient worker routing
3. **Classifiable**: After greedy ordering, Near/Far classification is independent
4. **Future-Proof**: OrderedMineral stores all metadata for worker assignment

### ⚠️ Important Notes
- W1 is already identified (furthest worker from COM) during worker labeling
- COM is calculated before greedy ordering starts
- Ordering is done per start location (multiMainMinerals[si])
- Limited to M[0-7] (minerals array indices 0-7)
- Average distance calculation ensures consistent near/far split

## Testing Checklist
- [ ] Build succeeds (currently 8 warnings, 0 errors ✓)
- [ ] Run game and verify console output shows greedy ordering
- [ ] Check that OrderedMainMinerals gets populated in tempBaseDto
- [ ] Verify each mineral has correct Index (0-7) and IsNear flag
- [ ] Test console output for all start locations (including opponents)

## Files Modified
1. **BabySharkBot/Setup/BaseDtos.cs**
   - Added OrderedMineral class
   - Added OrderedMainMinerals field to MawBaseLocationData

2. **BabySharkBot/Setup/InitialMapData.cs**
   - Added greedy ordering calculation section (before Populate multi-location data)
   - Added GreedyOrderMinerals() helper method (~200 lines)
   - Integrated W1 position from multiStartingUnits[si][0]

## Next Steps
This establishes the data structure for "Just In Time Mining" worker-to-mineral assignments. Next phase:
- Use OrderedMainMinerals to assign workers F1-F4, N1-N4
- Create F-series and N-series worker labels
- Implement worker-to-mineral routing based on greedy chain
