# Implementation Reference: Fix Near/Far Mineral Classification

## Quick Reference for Code Fix

### Location
- **File**: `BabySharkBot/Setup/InitialMapData.cs`
- **Method**: `RegisterMineralLabels()`
- **Approximate Lines**: 775-815

### What to Change

#### Find This Section (WRONG)
```csharp
// Using COM distance - WRONG
var comPosition = tempBaseDto.MineralCenterOfMass[si];
var avgDist = minerals.Average(m => 
    Vector2.Distance(
        new Vector2(m.X, m.Y), 
        new Vector2(comPosition.X, comPosition.Y)
    )
);

// Classification uses COM
IsNear = distFromCom < avgDist;
```

#### Replace With This (CORRECT)
```csharp
// Using Townhall distance - CORRECT
var townhallPosition = tempBaseDto.StartingTownhall[si];

// Calculate average distance to townhall
var avgTownhallDistance = minerals.Average(m => 
    Vector2.Distance(
        new Vector2(m.X, m.Y), 
        new Vector2(townhallPosition.X, townhallPosition.Y)
    )
);

// For each mineral, calculate townhall distance
foreach (var mineral in minerals)
{
    var distanceToTownhall = Vector2.Distance(
        new Vector2(mineral.X, mineral.Y),
        new Vector2(townhallPosition.X, townhallPosition.Y)
    );
    
    // Classification uses townhall distance
    mineral.IsNear = distanceToTownhall <= avgTownhallDistance;
}
```

---

## Key Points

| Aspect | Old (Wrong) | New (Correct) | Reference Point |
|--------|-----------|--------------|-----------------|
| **Reference Point** | `MineralCenterOfMass[si]` | `StartingTownhall[si]` | Where workers return cargo |
| **Distance Metric** | Distance to COM | Distance to Townhall | Travel efficiency |
| **Classification** | `IsNear = dist < avg` | `IsNear = dist <= avg` | Cargo return efficiency |
| **Represents** | Mineral clustering | Cargo return priority | Worker efficiency |

---

## Expected Behavior After Fix

### Before Fix
```
        COM (CENTER OF CLUSTER)
         ●
         
    Minerals based on how clustered they are
    around the COM point
    
    N1-N4: Closer to COM
    F1-F4: Farther from COM
    
Result: Doesn't reflect actual game efficiency
```

### After Fix
```
        TOWNHALL
           👃
           
        WORKERS
        👨 👨 👨
        
        MINERALS
        🦷 🦷 🦷 🦷 🦷 🦷 🦷 🦷
        
    Minerals based on distance to townhall
    
    N1-N4: Closer to townhall (shorter cargo return)
    F1-F4: Farther from townhall (longer cargo return)
    
Result: Reflects actual cargo return efficiency
```

---

## Verification Checklist

After making the code change:

- [ ] Build succeeds (0 errors)
- [ ] No new compile warnings introduced
- [ ] `RegisterMineralLabels()` references `StartingTownhall[si]` not `MineralCenterOfMass`
- [ ] Distance calculation uses townhall position
- [ ] `IsNear` threshold uses average townhall distance
- [ ] Labels can be drawn (no runtime errors)

---

## Testing Checklist

After running in-game with Debug enabled:

- [ ] N1-N4 labels appear closer to townhall
- [ ] F1-F4 labels appear farther from townhall
- [ ] Labels are on same side of townhall (smile/mustache pattern)
- [ ] Color coding: Cyan (N), Magenta (F)
- [ ] Labels are visible (Z=12 coordinate working)

---

## Why This Matters

```
EFFICIENCY IMPACT:

Near Mineral (N1): 10 units to townhall
  → Round trip: 20 units
  → Fast MPM = HIGH PRIORITY

Far Mineral (F4): 50 units to townhall
  → Round trip: 100 units
  → Slow MPM = SECONDARY PRIORITY

WORKER ASSIGNMENT:
  W1 gets N1 + F1 → Balanced load
  W2 gets N2 + F2 → Balanced load
  
Result: Efficient and fair mineral distribution
```

---

## The Pumpkin Analogy (For Reference)

```
TOWNHALL (Nose) ← Distance measured TO here
    👃
    │
    ├─────────────────────────
    │
W1 W2 W3 (Mustache)
👨 👨 👨 (Workers between minerals and townhall)
    │
    ├─────────────────────────
    │
M1 M2 M3 M4 M5 M6 M7 M8 (Teeth/Smile)
🦷 🦷 🦷 🦷 🦷 🦷 🦷 🦷

Inner circle (M1-M4): Close to nose = N1-N4 (Near)
Outer circle (M5-M8): Far from nose = F1-F4 (Far)
```

---

## Notes

- **COM (Center of Mass)**: Still useful for visualization (crosshair), just not for classification
- **Greedy Ordering (M[8-1])**: Still correct for routing efficiency, independent of Near/Far
- **Starting Townhall**: The anchor point for all cargo efficiency calculations
- **Worker Return**: This is the actual game mechanic that makes Near/Far important
