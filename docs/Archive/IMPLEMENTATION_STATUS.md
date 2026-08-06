# Implementation Status: Greedy Mineral Ordering

## ✅ COMPLETE

### Greedy Mineral Ordering Algorithm
- [x] OrderedMineral class created in BaseDtos.cs
- [x] OrderedMainMinerals field added to MawBaseLocationData
- [x] GreedyOrderMinerals() method implemented in InitialMapData.cs
- [x] Integration in GetNewMiningData() before tempBaseDto population
- [x] Phase 1: Find M[0] (furthest from W1) ✓
- [x] Phase 2: Greedy chain M[1-7] (closest remaining) ✓
- [x] Phase 3: Classify Near/Far based on COM distance ✓
- [x] Console logging for debugging ✓
- [x] Build successful (0 errors, 8 warnings non-critical) ✓

### Documentation
- [x] DRAWING_PATTERN_GUIDE.md (for future visualizations)
- [x] GREEDY_MINERAL_ORDERING.md (complete reference)
- [x] GREEDY_MINERAL_ORDERING_VISUAL.md (algorithm walkthroughs)

## 📊 Data Flow

```
Game Start
    ↓
InitialMapData.GetNewMiningData()
    ↓
[Mineral observation scan - unchanged]
    ↓
[Worker labeling W1 identification]
    ↓
[COM calculation - Z=12 for visualization]
    ↓
← NEW: GreedyOrderMinerals() calculation →
    ├─ Input: minerals[], W1 position, COM position
    ├─ Phase 1: Find furthest → M[0]
    ├─ Phase 2: Greedy chain → M[1-7]
    ├─ Phase 3: Classify Near/Far
    └─ Output: List<OrderedMineral> [0-7]
    ↓
tempBaseDto.OrderedMainMinerals populated
    ↓
Return tempBaseDto with complete mineral ordering
```

## 🎯 How to Use OrderedMainMinerals

### Access Pattern
```csharp
// In your mining manager or assignment logic:
var baseData = observation.BaseLocationData;

// Get ordered minerals for your start location (usually 0)
var orderedMinerals = baseData.OrderedMainMinerals[0];

// Get specific mineral
var m0 = orderedMinerals[0];      // M[0] - furthest from W1
var m4Position = orderedMinerals[4].Position;  // M[4] position

// Check classification
if (orderedMinerals[0].IsNear)
    // This is a "near" mineral (N0)
else
    // This is a "far" mineral (F0)
```

### Worker-to-Mineral Assignment (Next Phase)
```csharp
// Pseudo-code for future implementation:
var workers = GetWorkersForStart(0);
var minerals = baseData.OrderedMainMinerals[0];

// W1 (index 0) → M[0]
AssignWorkerToMineral(workers[0], minerals[0]);

// W2-W4 → remaining far minerals
for (int i = 1; i < 4 && i < minerals.Count; i++)
{
    if (!minerals[i].IsNear)
        AssignWorkerToMineral(workers[i], minerals[i]);
}

// W5-W12 → near minerals (greedy chain)
for (int i = 4; i < workers.Count; i++)
{
    var mineral = minerals[(i-4) % minerals.Count];
    if (mineral.IsNear)
        AssignWorkerToMineral(workers[i], mineral);
}
```

## 🔍 Verification Checklist

When you run the game next time:

- [ ] Check console for "InitialMapData: Ordering complete:" message
- [ ] Verify greedy chain is printed: M[0]=mineral[X], M[1]=mineral[Y], etc.
- [ ] Confirm all 8 minerals are ordered (or fewer if < 8 exist)
- [ ] Check that M[0] is furthest from W1 (highest distance shown)
- [ ] Verify Near/Far classification (N or F prefix shown)
- [ ] All OrderedMineral objects have valid Position and Index
- [ ] crosshairs are still drawing (should see yellow and orange crosshairs)

### Example Expected Output
```
InitialMapData: Start[0] ordered 8 minerals
InitialMapData.GreedyOrderMinerals: Start[0] M[0] = mineral[4] at distance 61.4 from W1
InitialMapData.GreedyOrderMinerals: Start[0] ordering complete:
  M[0] = mineral[4] at (35.0,30.0) distance=25.1 F4
  M[1] = mineral[3] at (40.0,40.0) distance=14.1 N3
  M[2] = mineral[1] at (50.0,50.0) distance=14.1 N1
  M[3] = mineral[6] at (45.0,55.0) distance=7.0 N6
  M[4] = mineral[8] at (60.0,45.0) distance=20.2 N8
  M[5] = mineral[7] at (55.0,70.0) distance=21.4 N7
  M[6] = mineral[2] at (30.0,60.0) distance=24.2 F2
  M[7] = mineral[5] at (20.0,80.0) distance=31.6 F5
```

## 📚 Documentation Files Created

1. **DRAWING_PATTERN_GUIDE.md**
   - How to add new debug visualizations
   - Service pattern (Set/Get/Clear)
   - Z-coordinate rule (Z=12+)
   - Used for: future crosshairs, domes, arrows

2. **GREEDY_MINERAL_ORDERING.md**
   - Algorithm explanation
   - OrderedMineral class definition
   - Usage examples
   - Testing checklist

3. **GREEDY_MINERAL_ORDERING_VISUAL.md**
   - Visual walkthrough with example
   - 8-mineral scenario from Phase 1 to Phase 3
   - Edge cases
   - Pseudocode

## 🚀 Next Steps

1. **Test on actual game run** - Verify OrderedMainMinerals is populated
2. **Worker assignment** - Use OrderedMainMinerals to create F1-F4 and N1-N4 labels
3. **Opponent start detection** - Add opponent start visualization (red domes)
4. **Vespene ordering** - Apply similar greedy ordering to geysers (V1-V2, etc)

## Files Modified

```
BabySharkBot/Setup/BaseDtos.cs
  - Added OrderedMineral class (~20 lines)
  - Added OrderedMainMinerals field to MawBaseLocationData (~3 lines)

BabySharkBot/Setup/InitialMapData.cs
  - Added greedy ordering calculation section (~45 lines)
  - Added GreedyOrderMinerals() helper method (~200 lines)
  - Total: ~245 new lines

Build Status: ✅ Success (0 errors, 8 warnings)
```

## 💡 Key Insights

### Why M[0] = Furthest from W1?
The furthest mineral from W1 gives W1 a significant distance to travel, allowing other workers to start mining nearby minerals immediately while W1 handles the far mineral. This overlaps execution and maximizes efficiency.

### Why Greedy Chain?
After M[0], each subsequent mineral is closest to the previous. This creates:
- Physical proximity path (workers walk mineral-to-mineral)
- Natural load balancing
- Deterministic order (same every game)

### Why Near/Far Split?
Divides minerals into two categories:
- **Far minerals (F)**: Assigned to fewer workers (W1-W4)
- **Near minerals (N)**: Assigned to many workers (W5-W12)
This matches "Just In Time Mining" strategy from the reference document.

## ⚠️ Known Limitations

- Currently handles 8 minerals max per start
- Assumes W1 is already calculated (done during worker labeling)
- Requires COM calculation first (done before this phase)
- COM visibility at Z=12 (can be adjusted if needed)
- Does not handle vespene ordering yet (future phase)

## Testing Strategy

| Test | Command | Expected |
|------|---------|----------|
| Build | `dotnet build` | 0 errors ✓ |
| Game Start | Run game | Console shows "ordering complete" |
| Visualization | In-game | See yellow/orange crosshairs for start locations |
| Data Access | Debug breakpoint | OrderedMainMinerals populated |
| Greedy Chain | Console output | M[0-7] listed in order |

---

**Status**: ✅ **READY FOR TESTING ON GAME RUN**

All infrastructure is in place. Next phase requires worker assignment logic to use OrderedMainMinerals for creating F1-F4 and N1-N4 labels.
