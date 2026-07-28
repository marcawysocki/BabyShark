# L1-L4 Large Mineral Labeling System

## Overview

Large mineral clusters that are far from townhall now receive **L# labels** (Yellow) instead of **F# labels** (Magenta). This provides strategic distinction between:

- **N1-N4** (Cyan): Near minerals - close to townhall, high efficiency
- **L1-L4** (Yellow): Large Far minerals - isolated, strategically important despite distance
- **F1-F4** (Magenta): Regular Far minerals - standard distance minerals, lower strategic value

## Label Meanings

### N# Labels (Cyan)
```
Distance to townhall: ≤ (average - 0.25)
Characteristics: High efficiency, short return trips
Priority: PRIMARY (always mine first)
Color: Cyan (R=0, G=255, B=255)
```

### L# Labels (Yellow) - NEW
```
Distance to townhall: > (average - 0.25)
Characteristics: 
  - Isolated from other minerals (few neighbors)
  - Far from standard clustering
  - Potentially large patch with high resource value
  - Worth the longer return trip due to volume
Priority: SECONDARY_HIGH (after N#, before F#)
Color: Yellow (R=255, G=255, B=0)
```

### F# Labels (Magenta)
```
Distance to townhall: > (average - 0.25)
Characteristics: Standard far minerals in cluster
Priority: SECONDARY_LOW (after N# and L#)
Color: Magenta (R=255, G=0, B=255)
```

## Size Detection Heuristic

A mineral is classified as **Large** if:

1. **Isolated**: Has ≤1 neighbors within 3.5 units
2. **Far**: IsNear = false (distance > threshold)
3. **Strategic**: Distance > average inter-mineral distance

Otherwise, it's classified as **Normal** (which becomes F# if Far, N# if Near).

## Example: 3N, 1L, 4F Map

**Mineral Distribution**:
```
Townhall at (50, 50)

Cluster A (Near):
  N1: (48, 48) distance=2.8  ← close group
  N2: (50, 52) distance=2.0
  N3: (52, 50) distance=2.0

Large Isolated (Far):
  L1: (60, 75) distance=26.5 ← isolated, far, but large/valuable

Regular Far Minerals:
  F1: (35, 40) distance=15.8 ← part of far cluster
  F2: (33, 42) distance=17.6
  F3: (37, 38) distance=15.2
  F4: (32, 40) distance=18.3

Average mineral distance: 9.2 units
Large detection threshold: 9.2 units
```

**Console Output**:
```
InitialMapData.GreedyOrderMinerals: Classification summary: 3N, 1L, 4F
  M[8] = mineral[4] at (60.00,75.00) distance=26.50 Large L1
  M[7] = mineral[5] at (35.00,40.00) distance=15.80 Normal F1
  M[6] = mineral[6] at (33.00,42.00) distance=17.60 Normal F2
  M[5] = mineral[7] at (37.00,38.00) distance=15.20 Normal F3
  M[4] = mineral[2] at (32.00,40.00) distance=18.30 Normal F4
  M[3] = mineral[1] at (52.00,50.00) distance=2.00 Normal N3
  M[2] = mineral[2] at (50.00,52.00) distance=2.00 Normal N2
  M[1] = mineral[0] at (48.00,48.00) distance=2.80 Normal N1
InitialMapData.RegisterMineralLabels: Start[0] Summary: 3N, 1L, 4F
```

**In-Game Display** (Debug mode):
```
Visual (colors):
  N1, N2, N3 → Cyan labels near townhall
  L1         → Yellow label at isolated far position
  F1-F4      → Magenta labels at regular far positions
```

## Worker Assignment Strategies

### Strategy A: 3N, 5F (No Large)
```
W1 → N1 + F1         (balanced: 1 near + 1 far)
W2 → N2 + F2
W3 → N3 + F3
W4 → F4 + F5         (specialist: both far)
```
**Rationale**: Equal distribution, F minerals cheaper than L

### Strategy B: 3N, 1L, 4F (One Large)
```
W1 → N1 + L1         (hybrid: pair near + large)
W2 → N2 + F1
W3 → N3 + F2
W4 → F3 + F4         (specialist far)
```
**Rationale**: L1 is valuable, pair with N1 worker. Lower priority far go to W4

### Strategy C: 4N, 1L, 3F (More Near, One Large)
```
W1 → N1
W2 → N2 + L1         (pair with large)
W3 → N3 + F1
W4 → N4 + F2 + F3    (top off with remaining)
```
**Rationale**: Many near minerals available, L1 efficiency matters

### Strategy D: 2N, 2L, 4F (Multiple Large)
```
W1 → N1 + L1         (first worker: near + first large)
W2 → N2 + L2         (second worker: near + second large)
W3 → F1 + F2         (specialists on regular far)
W4 → F3 + F4
```
**Rationale**: Both large minerals are valuable, pair with near workers

## Console Output Format

### Classification Line
```
InitialMapData.GreedyOrderMinerals: Classification summary: 3N, 1L, 4F
                                                            ↑    ↑   ↑
                                                    Near Count
                                                         │
                                                         Large Count
                                                              │
                                                              Far Count
```

### Mineral Detail Line
```
M[8] = mineral[4] at (60.00,75.00) distance=26.50 Large L1
│      │          │                │            │      │
│      │          │                │            │      └─ Label (N#/L#/F#)
│      │          │                │            └──────── Size classification
│      │          │                └─ Distance to townhall
│      │          └──────────────── Position coordinates
│      └──────────────────────────── Original mineral index
└────────────────────────────────── Greedy chain position
```

### Summary Line Per Start Location
```
InitialMapData.RegisterMineralLabels: Start[0] Summary: 3N, 1L, 4F
```

## Color Encoding

| Label Type | Color | RGB Values | Usage |
|-----------|-------|-----------|-------|
| N# Near | Cyan | (0, 255, 255) | Primary mining target |
| L# Large | Yellow | (255, 255, 0) | Strategic far minerals |
| F# Far | Magenta | (255, 0, 255) | Secondary far minerals |

## Implementation Details

### Labeling Order
L# labels are assigned in **registration order** (greedy chain order), not by actual mineral index:

```
If minerals are classified [N, F, F, L, F, N, F, N]:
Then labels are:
  N (1st Near)     → N1
  F (1st Far)      → F1
  F (2nd Far)      → F2
  L (1st Large)    → L1
  F (3rd Far)      → F3
  N (2nd Near)     → N2
  F (4th Far)      → F4
  N (3rd Near)     → N3
```

**Counter Tracking**:
- `nearCount++` for each N# label
- `largeCount++` for each L# label  
- `farCount++` for each F# label

### Label Registration
```csharp
// Current implementation (InitialMapData.RegisterMineralLabels)
foreach (var orderedMineral in orderedList)
{
    if (orderedMineral.IsNear)
    {
        label = $"N{++nearCount}";
        color = Cyan;
    }
    else if (orderedMineral.Size == MineralSize.Large)
    {
        label = $"L{++largeCount}";
        color = Yellow;
    }
    else
    {
        label = $"F{++farCount}";
        color = Magenta;
    }
    
    mineralLabelService.SetMineralLabel(label, position, color);
}
```

## Testing Verification

### Console Output Check
1. Look for: `Classification summary: XN, YL, ZF`
2. Verify: X + Y + Z = 8 (total minerals)
3. Check: L labels appear only when Large minerals detected

### In-Game Verification
1. Enable Debug mode
2. Look for Yellow (L#) labels at isolated far positions
3. Verify Near minerals (Cyan) are actually close
4. Verify Regular Far minerals (Magenta) are farther than Near
5. Verify Large minerals (Yellow) are isolated from clusters

### Label Correctness
1. No mineral should have multiple labels
2. All 8 minerals should be labeled (N + L + F = 8)
3. N# numbering should be 1-4 (or fewer)
4. L# numbering should be 1-4 (or 0 if no Large)
5. F# numbering should be 1-4 (or fewer)

## Tuning Adjustments

### If L# Labels Not Appearing When Expected
**Increase large detection sensitivity**:
```csharp
// In ClassifyMineralSizes method
const float proximityThreshold = 4.0f;  // ← increase from 3.5
const int neighborThreshold = 2;        // ← decrease from 3
```

### If Too Many L# Labels (False Positives)
**Decrease large detection sensitivity**:
```csharp
const float proximityThreshold = 3.0f;  // ← decrease from 3.5
const int neighborThreshold = 4;        // ← increase from 3
```

### If L# Labels At Wrong Positions
**Verify threshold offset**:
```csharp
var nearThreshold = avgTownhallDistance > 0.25f 
    ? avgTownhallDistance - 0.25f  // ← Check this is working
    : avgTownhallDistance;
```

## Future Enhancements

### Use Actual Mineral Size Data
If SC2 API provides BufferRadius or actual unit radius:
```csharp
float mineralRadius = unit.BufferRadius;  // ← Currently unavailable
if (mineralRadius > 90.0f)  // Large patches
    Size = MineralSize.Large;
```

### Weighted Priority System
Instead of just N/L/F, assign weights:
```csharp
float mineralValue = (EstimatedResources / DistanceToTownhall);
if (mineralValue > highThreshold) priority = "L";
```

### Density-Based Classification
Use clustering algorithms to identify patch types:
```csharp
// K-means or density analysis
var clusters = ClusterMinerals(minerals);
foreach (var cluster in clusters)
{
    if (cluster.Count <= 1) Size = MineralSize.Large;
    else if (cluster.Density > threshold) Size = MineralSize.Normal;
}
```

## Related Documentation
- `MINERAL_CLASSIFICATION_COUNTS_GUIDE.md` - Worker assignment patterns
- `CODE_FLOW_DETAILED.md` - Implementation details
- `IMPLEMENTATION_SUMMARY.md` - Overall changes overview
