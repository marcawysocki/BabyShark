# Greedy Mineral Ordering - Visual Algorithm Guide

## Example: 8 Minerals, 1 W1 Worker, COM at Base Center

```
MAP VIEW (Start Location)
==========================

     W1 (furthest from COM)
      ^
      |
      |
      |      M4        M7
      |   M2    M6    
      |        [COM]     M8
      +------ M3    M1 ---->
           M5

MINERAL POSITIONS (before ordering):
M1: (50, 50)  - East side
M2: (30, 60)  - North-west
M3: (40, 40)  - South side
M4: (20, 80)  - Far North
M5: (35, 30)  - South-west
M6: (45, 55)  - Near COM
M7: (55, 70)  - North-east
M8: (60, 45)  - East-south

W1 Position: (65, 85) - Furthest from COM at (40, 50)
COM: (40, 50)
```

## Phase 1: Find M[0] (Furthest from W1)

```
DISTANCES FROM W1 at (65, 85):
M1: dist((50,50) → (65,85)) = ~23.0
M2: dist((30,60) → (65,85)) = ~40.1  
M3: dist((40,40) → (65,85)) = ~50.5
M4: dist((20,80) → (65,85)) = ~45.3
M5: dist((35,30) → (65,85)) = ~61.4  ← MAXIMUM (M[0])
M6: dist((45,55) → (65,85)) = ~34.0
M7: dist((55,70) → (65,85)) = ~17.7
M8: dist((60,45) → (65,85)) = ~40.3

RESULT: M[0] = M5 (index 5)
NewMainIndexes[0] = 5
Remaining: [1,2,3,4,6,7,8]
Current position: (35, 30)
```

## Phase 2: Greedy Chain (Closest Mineral to Current)

```
ITERATION 1: Current = (35, 30) [M[0]]
========================================
Distances from (35, 30):
M1: dist = ~24.7
M2: dist = ~30.3
M3: dist = ~10.0  ← MINIMUM (M[1])
M4: dist = ~50.2
M6: dist = ~28.0
M7: dist = ~44.3
M8: dist = ~45.2

M[1] = M3 (index 3)
NewMainIndexes[1] = 3
Remaining: [1,2,4,6,7,8]
Current position: (40, 40)


ITERATION 2: Current = (40, 40) [M[1]]
========================================
Distances from (40, 40):
M1: dist = ~14.1  ← MINIMUM (M[2])
M2: dist = ~31.6
M4: dist = ~40.3
M6: dist = ~15.0
M7: dist = ~33.9
M8: dist = ~20.2

M[2] = M1 (index 1)
NewMainIndexes[2] = 1
Remaining: [2,4,6,7,8]
Current position: (50, 50)


ITERATION 3: Current = (50, 50) [M[2]]
========================================
Distances from (50, 50):
M2: dist = ~24.2
M4: dist = ~30.4
M6: dist = ~7.1   ← MINIMUM (M[3])
M7: dist = ~14.1
M8: dist = ~10.3

M[3] = M6 (index 6)
NewMainIndexes[3] = 6
Remaining: [2,4,7,8]
Current position: (45, 55)


... (repeat for M[4] through M[7]) ...

FINAL GREEDY CHAIN:
M[0] = 5  Current: (35, 30)  Distance: 61.4 from W1
M[1] = 3  Current: (40, 40)  Distance: 10.0
M[2] = 1  Current: (50, 50)  Distance: 14.1
M[3] = 6  Current: (45, 55)  Distance:  7.1
M[4] = 8  Current: (60, 45)  Distance: 20.2
M[5] = 7  Current: (55, 70)  Distance: 38.8
M[6] = 4  Current: (20, 80)  Distance: 61.7
M[7] = 2  Current: (30, 60)  Distance: 50.5
```

## Phase 3: Classify Near/Far

```
DISTANCES FROM COM at (40, 50):
M5: dist = ~25.1  ← Average = 22.5, so M5 is FAR
M3: dist = ~14.1  ← Less than avg, so NEAR
M1: dist = ~14.1  ← NEAR
M6: dist = ~7.0   ← NEAR
M8: dist = ~20.2  ← NEAR
M7: dist = ~21.4  ← NEAR
M4: dist = ~31.6  ← FAR
M2: dist = ~24.2  ← FAR

FINAL ORDERED MINERALS (with classification):
M[0] = mineral[5] (35,30)  IsNear=false  F5
M[1] = mineral[3] (40,40)  IsNear=true   N3
M[2] = mineral[1] (50,50)  IsNear=true   N1
M[3] = mineral[6] (45,55)  IsNear=true   N6
M[4] = mineral[8] (60,45)  IsNear=true   N8
M[5] = mineral[7] (55,70)  IsNear=true   N7
M[6] = mineral[4] (20,80)  IsNear=false  F4
M[7] = mineral[2] (30,60)  IsNear=false  F2
```

## Worker Assignment (Next Phase)

```
JUST IN TIME MINING STRATEGY:
============================

W1 (Furthest) assigns to F-series minerals (farther from COM)
Remaining workers assign to N-series minerals (near COM) in greedy chain order

ASSIGNMENT PATTERN:
W1      → F5 (furthest, far from COM)
W2-W4   → F2, F4 (remaining far minerals, in order visited)
W5-W12  → N3, N1, N6, N8, N7 (near minerals, greedy chain)

RESULT:
F4 label → W3 (assigned to mineral[4])
F5 label → W1 (assigned to mineral[5])
N1 label → W6 (assigned to mineral[1])
N3 label → W5 (assigned to mineral[3])
... etc ...
```

## Code Algorithm (Pseudocode)

```
function GreedyOrderMinerals(minerals, w1Position, comPosition):
    
    // Phase 1: Find furthest from W1
    result = []
    remaining = [0, 1, 2, 3, 4, 5, 6, 7]
    bestIdx = findMaxDistance(minerals, w1Position, remaining)
    result.add({Index: 0, OriginalIndex: bestIdx, ...})
    remaining.remove(bestIdx)
    currentPos = minerals[bestIdx].position
    
    // Phase 2: Greedy chain
    for i = 1 to 7:
        nearestIdx = findMinDistance(minerals, currentPos, remaining)
        result.add({Index: i, OriginalIndex: nearestIdx, ...})
        remaining.remove(nearestIdx)
        currentPos = minerals[nearestIdx].position
    
    // Phase 3: Classify
    avgDist = average(all minerals distance to comPosition)
    for each OrderedMineral in result:
        dist = distance(mineral.position, comPosition)
        mineral.IsNear = (dist < avgDist)
        mineral.DistanceFromCOM = dist
    
    return result
```

## Key Takeaways

✅ **M[0] is guaranteed to be furthest from W1** (best initial mineral for W1)  
✅ **Greedy chain ensures connected path** (workers walk mineral-to-mineral)  
✅ **Near/Far split is automatic** after ordering (no special classification needed)  
✅ **Deterministic** - same minerals always produce same ordering  
✅ **Worker-centric** - optimizes for the actual starting worker positions

## Edge Cases Handled

| Case | Handling |
|------|----------|
| Fewer than 8 minerals | OrderedMineral list is shorter, loop breaks at count |
| All minerals near COM | All have IsNear=true, but greedy chain still optimal |
| Clustered minerals | Greedy picks closest in chain, ensuring spread |
| W1 at mineral location | Distance = 0, still valid as M[0] anchor |
