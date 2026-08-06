# NEAR & FAR MINERALS: The Core Concept (Quick Reference)

## In 30 Seconds

**Near Minerals (N1-N4)**: Closer to townhall = Shorter cargo return = Higher priority
**Far Minerals (F1-F4)**: Farther from townhall = Longer cargo return = Lower priority

**Pumpkin Model**: Townhall = Nose, Workers = Mustache, Minerals = Teeth. Distance from teeth to nose.

---

## The Pumpkin 🎃

```
                TOWNHALL (NOSE - 👃)
                 Reference Point
                       |
            W1 W2 W3 (MUSTACHE - 👨👨👨)
             Workers, between minerals
                    and townhall
                       |
        M8 M7 M6 M5  M4 M3 M2 M1 (SMILE/TEETH - 🦷)
        All on same side of townhall

DISTANCE MEASUREMENT:
From each mineral → back to the townhall

Near minerals (N1-N4): Close to townhall (inner circle)
                      ├─ Shorter distance = faster return
                      ├─ Higher mineral per minute (MPM)
                      └─ PRIORITY ★★★★

Far minerals (F1-F4):  Far from townhall (outer circle)
                      ├─ Longer distance = slower return
                      ├─ Lower mineral per minute (MPM)
                      └─ PRIORITY ★★
```

---

## Two Different Metrics (Don't Confuse!)

### 1. Greedy Ordering (M[8-1])
- **Purpose**: Route efficiency (order to visit minerals)
- **Metric**: Path distance from worker to minerals
- **Result**: M[8] → M[7] → ... → M[1] (optimal visiting order)
- **Uses**: W1 position as starting point
- **Does NOT affect**: Near/Far classification

### 2. Near/Far Classification (N/F)
- **Purpose**: Cargo return efficiency (priority for worker assignment)
- **Metric**: Distance from mineral to townhall
- **Result**: N1-N4 (≤ average) vs F1-F4 (> average)
- **Uses**: Starting Townhall position as reference
- **Does NOT care about**: Visiting order (that's greedy)

**KEY**: These measure different things!

---

## Classification Rule

```
For each mineral:
├─ Calculate: distance to StartingTownhall[si]
├─ Average all distances
└─ Classify:
    ├─ If distance ≤ average → N (Near) → Higher priority
    └─ If distance > average → F (Far)  → Lower priority
```

---

## Reference Points (Three Different Uses)

| Point | Used For | Reference | Example |
|-------|----------|-----------|---------|
| **Starting Townhall** | Near/Far classification | `tempBaseDto.StartingTownhall[si]` | (40, 50) |
| **Center of Mass (COM)** | Visualization only | `tempBaseDto.MineralCenterOfMass[si]` | (42, 48) |
| **Worker First (W1)** | Greedy ordering start | `multiStartingUnits[si][0].Position` | (35, 45) |

---

## What Was Wrong vs Right

### ❌ WRONG
```csharp
var comPosition = tempBaseDto.MineralCenterOfMass[si];  // Wrong reference!
var avgDist = minerals.Average(m => 
    Distance(mineral, comPosition)  // Measuring to COM!
);
IsNear = distance < avgDist;  // Wrong threshold!
```
**Result**: Shows mineral clustering, not cargo efficiency

### ✅ RIGHT
```csharp
var townhallPosition = tempBaseDto.StartingTownhall[si];  // Correct reference!
var avgTownhallDistance = minerals.Average(m => 
    Distance(mineral, townhallPosition)  // Measuring to townhall!
);
IsNear = distance <= avgTownhallDistance;  // Correct threshold!
```
**Result**: Shows actual cargo return efficiency

---

## Why It Matters

```
EXAMPLE: Two minerals

Mineral A: 10 units from townhall
           → Return trip: 20 units
           → Fast! → HIGH PRIORITY (N)

Mineral B: 50 units from townhall
           → Return trip: 100 units
           → Slow → SECONDARY PRIORITY (F)

WORKER EFFICIENCY:
W1 assigned to A (near) + B (far)
→ Gets one high-efficiency + one lower-efficiency = balanced load
```

---

## The Labels Shown In-Game

| Label | Color | Meaning | Z-Level |
|-------|-------|---------|---------|
| N1 | Cyan 🔵 | Nearest mineral | 12.0 |
| N2 | Cyan 🔵 | Second nearest | 12.0 |
| N3 | Cyan 🔵 | Third nearest | 12.0 |
| N4 | Cyan 🔵 | Fourth nearest | 12.0 |
| F1 | Magenta 🟣 | Farthest mineral | 12.0 |
| F2 | Magenta 🟣 | Second farthest | 12.0 |
| F3 | Magenta 🟣 | Third farthest | 12.0 |
| F4 | Magenta 🟣 | Fourth farthest | 12.0 |

**All rendered at Z=12.0 (above terrain for visibility)**

---

## Worker Assignment Pattern

```
W1 → M[1] (from greedy) → Split into N1 + F1
W2 → M[2] (from greedy) → Split into N2 + F2
W3 → M[3] (from greedy) → Split into N3 + F3
W4 → M[4] (from greedy) → Split into N4 + F4

Result: Each worker gets one near + one far
        All on same side of townhall (smile/mustache pattern)
        Balanced cargo efficiency across workers
```

---

## Center of Mass (COM): What's It For?

❌ **NOT** for Near/Far classification
❌ **NOT** for measuring cargo efficiency  
❌ **NOT** for worker assignment

✅ **IS** for visualization (crosshair on the cluster)
✅ **IS** for geographic reference (showing mineral cluster center)
✅ **IS** for early analysis (before full map known)

**Remember**: COM is just the average position - it's not where workers return cargo!

---

## Key Points Summary

| Concept | Details |
|---------|---------|
| **Reference Point** | `StartingTownhall[si]` - where workers return cargo |
| **Measurement** | Distance from mineral to townhall |
| **Threshold** | Average townhall distance |
| **Classification** | N = ≤ average, F = > average |
| **Priority** | N (Near) > F (Far) |
| **Why Matters** | Affects mineral per minute (MPM) and worker efficiency |
| **Pumpkin Model** | Townhall=Nose, Workers=Mustache, Minerals=Teeth |
| **Labels** | N1-N4 (Cyan), F1-F4 (Magenta) |
| **Display** | All at Z=12.0 above terrain |

---

## Documentation References

- **Comprehensive**: `MINERAL_CLASSIFICATION_CONCEPT.md` (full explanation)
- **Visual**: `MINERAL_LABEL_VISUAL_GUIDE.md` (diagrams)
- **Implementation**: `IMPLEMENTATION_FIX_REFERENCE.md` (code changes)
- **Index**: `MINERAL_DOCUMENTATION_INDEX.md` (navigate all docs)
- **Summary**: `DOCUMENTATION_CORRECTED_SUMMARY.md` (what was fixed)

---

## Next Steps

1. Understand this concept ✅ (you're reading it!)
2. Read: MINERAL_CLASSIFICATION_CONCEPT.md (full details)
3. Fix: Use IMPLEMENTATION_FIX_REFERENCE.md to update code
4. Build and verify
5. Test in-game with Debug enabled
6. Implement worker assignment using corrected N/F labels

---

**Remember**: Distance to Townhall, not to COM. That's the fundamental difference.
