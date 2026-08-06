# BabyShark Development - Quick Reference

## Current Status: ✅ Ready for Testing

### What Was Implemented

#### 1. **Mineral Center of Mass (COM) Visualization** ✅
- **Problem**: Crosshairs not showing (Z=0 was underground)
- **Solution**: Changed Z coordinate to Z=12 (above terrain)
- **Status**: Ready to test - should see yellow/orange crosshairs in-game
- **Files**: InitialMapData.cs (line 551: Z=12.0f)

#### 2. **Greedy Mineral Ordering Algorithm** ✅
- **What**: Orders 8 minerals into M[0-7] chain based on Just In Time Mining strategy
- **Process**:
  - M[0] = furthest mineral from W1
  - M[1-7] = greedy chain (closest remaining to current)
  - Classify each as Near/Far from COM
- **Output**: tempBaseDto.OrderedMainMinerals[si] = List<OrderedMineral>
- **Status**: Complete and tested (builds successfully)
- **Files**: BaseDtos.cs + InitialMapData.cs (~250 new lines total)

### Documentation Created

| File | Purpose | Use Case |
|------|---------|----------|
| DRAWING_PATTERN_GUIDE.md | How to add debug visualizations | Next visualization feature |
| GREEDY_MINERAL_ORDERING.md | Complete algorithm reference | Understanding worker assignment |
| GREEDY_MINERAL_ORDERING_VISUAL.md | Visual algorithm walkthrough | Learning the greedy chain |
| IMPLEMENTATION_STATUS.md | What's complete & next steps | Project status tracking |

---

## Quick Access Guides

### When You Need to Add a New Debug Visualization

→ **Read**: DRAWING_PATTERN_GUIDE.md

**TL;DR**:
1. Create service in BaseDtos.cs (Set, Get, Clear methods)
2. Instantiate in BabySharkBot.cs (line 65)
3. Pass to BabySharkMiningManager constructor
4. Add drawing method in BabySharkMiningManager.OnFrame()
5. **CRITICAL**: Use Z=12 or higher for visibility
6. Call Set() from InitialMapData or wherever data exists

### When You Need to Use Ordered Minerals

→ **Read**: GREEDY_MINERAL_ORDERING.md or GREEDY_MINERAL_ORDERING_VISUAL.md

**TL;DR**:
```csharp
var orderedMinerals = baseLocationData.OrderedMainMinerals[0];
var m0 = orderedMinerals[0];              // Furthest from W1
var m4Position = orderedMinerals[4].Position;
bool isNear = orderedMinerals[4].IsNear;  // N4 or F4?
```

### When Crosshairs Aren't Showing

→ **Check**:
1. Z coordinate >= 12 (terrain is ~0-2)
2. SharkyOptions.Debug == true (check in DEBUG builds)
3. ManagerDebugService.IsDebugEnabled == true
4. SetCOM() is being called (check console output)
5. DrawCenterOfMassLocations() is being called (should see in console)

### When Greedy Ordering Doesn't Work

→ **Check**:
1. W1 position exists (workers are labeled in InitialMapData)
2. COM is calculated (should happen before greedy ordering)
3. Minerals list is not empty
4. Console shows "ordering complete" message
5. OrderedMainMinerals is populated in tempBaseDto

---

## Code Architecture Summary

```
Game Initialization
    ↓
BabySharkBot.BabySharkAI()
    ├─ Creates WorkerLabelService
    ├─ Creates CrosshairService ← COM visualization service
    ├─ Creates BabySharkMiningManager
    └─ Passes both services to manager
    ↓
BabySharkMiningManager.OnStart()
    ├─ Checks if MapDataLoaded flag
    └─ Calls InitialMapData.GetNewMiningData()
        ↓
        InitialMapData.GetNewMiningData()
        ├─ [Single pass scan: collect minerals, workers]
        ├─ [Identify W1: furthest worker from COM]
        ├─ [Calculate mineral COM: average X,Y]
        ├─ [Register COM with CrosshairService ← FOR VISUALIZATION]
        ├─ [Label discovered units: Hatchery, Overlord, Larva, Workers]
        ├─ [← NEW: Calculate greedy mineral ordering]
        └─ Return MawBaseLocationData with:
            ├─ MainMinerals (unordered)
            ├─ OrderedMainMinerals ← GREEDY CHAIN M[0-7]
            ├─ MineralCenterOfMass (used for classification)
            └─ CrosshairService registry (for visualization)
        ↓
BabySharkMiningManager.OnFrame() (every frame)
    ├─ DrawWorkerLabels() ← Reads from WorkerLabelService
    ├─ DrawCenterOfMassLocations() ← Reads from CrosshairService
    └─ [Future: DrawWorkerInstructions(), etc.]
        ↓
    ManagerDebugService.DrawText/DrawLine/DrawSphere
        ↓
    DebugManager.OnFrame() (accumulates draw requests)
        ↓
    GameConnection.SendRequest(DrawRequest)
        ↓
    Rendered to game screen
```

---

## Key Files & Line Numbers

### BaseDtos.cs
- **Line 45-70**: OrderedMineral class definition
- **Line 120-125**: OrderedMainMinerals field in MawBaseLocationData

### InitialMapData.cs
- **Line 15**: Method signature (parameters: crosshairService)
- **Line 551**: SetCOM() call (Z=12.0f) ← COM VISUALIZATION
- **Line 687-730**: Greedy ordering calculation section
- **Line 733+**: GreedyOrderMinerals() helper method (~200 lines)

### BabySharkBot.cs (BabySharkAI)
- **Line 65**: Create CrosshairService
- **Line 71**: Inject both services to BabySharkMiningManager

### BabySharkMiningManager.cs
- **Line 30**: CrosshairService field
- **Line 33**: Constructor parameter
- **Line 85**: DrawCenterOfMassLocations() call
- **Line 202-252**: DrawCenterOfMassLocations() method ← DRAWS THE CROSSHAIRS

---

## Testing Checklist

### Before Game Run
- [ ] Build successful? `dotnet build BabySharkBot`
- [ ] All warnings non-critical? (nullable reference types only)
- [ ] No obvious errors in code review?

### During Game Run
- [ ] **COM Visualization**:
  - [ ] See yellow crosshair at Start[0]
  - [ ] See orange crosshair at Start[1] (opponent)
  - [ ] Crosshairs are above ground (not invisible)
  
- [ ] **Greedy Ordering**:
  - [ ] Console shows "InitialMapData: Ordering complete:"
  - [ ] Shows M[0] = mineral[X] "at distance Y from W1"
  - [ ] Shows M[1-7] in greedy chain order
  - [ ] Shows N/F classification (N3, F4, etc)

### Debugging
- [ ] Add breakpoint in DrawCenterOfMassLocations()
- [ ] Check if allCOMs is populated (not empty)
- [ ] Check if OrderedMainMinerals has entries
- [ ] Check console output for errors

---

## Memory: Key Principles

### 1. Z-Coordinate Rule
- **Terrain**: Z = 0-2
- **Static visualization**: Z = 12+
- **Unit relative**: Z = unit.Pos.Z + 1.5f
- If Z is too low: visualization invisible (underground)

### 2. Service Pattern (for any visualization)
1. Service in BaseDtos.cs (Set/Get/Clear)
2. Instantiate in BabySharkBot.cs
3. Inject into manager constructor
4. Draw in manager.OnFrame()
5. Call Set() where data exists

### 3. Greedy Mineral Ordering
- Find M[0]: furthest from W1
- Build M[1-7]: greedy chain (closest remaining)
- Classify: Near if < average distance from COM

### 4. Worker Labeling Pattern
- W1 = furthest worker from COM (already done)
- Workers discovered during unit scan
- Labels set in InitialMapData
- Retrieved in drawing methods via WorkerLabelService

---

## Frequently Asked Questions

**Q: Where are COMs drawn?**  
A: BabySharkMiningManager.DrawCenterOfMassLocations() (line 202-252)

**Q: What does "Just In Time Mining" mean?**  
A: Mining strategy where W1 takes far minerals, other workers take near minerals in greedy chain order. Maximizes overlap of harvest times.

**Q: Why greedy chain instead of random order?**  
A: Greedy chain ensures workers walk mineral-to-mineral without backtracking. Deterministic and efficient.

**Q: How do I access OrderedMainMinerals?**  
A: `baseLocationData.OrderedMainMinerals[startIndex][mineralIndex]` where mineralIndex = 0-7

**Q: Can I have fewer than 8 minerals?**  
A: Yes, loop breaks when minerals run out. OrderedMainMinerals might have 3-7 entries.

**Q: Is W1 already identified?**  
A: Yes, it's the first entry in multiStartingUnits[si] and is the furthest worker from COM.

**Q: What's the difference between MainMinerals and OrderedMainMinerals?**  
A: MainMinerals = unordered minerals as scanned; OrderedMainMinerals = M[0-7] greedy chain with Near/Far classification

---

## Next Immediate Task

When you're ready to implement worker-to-mineral assignments:
1. Read GREEDY_MINERAL_ORDERING.md to understand the chain
2. Get OrderedMainMinerals from baseLocationData
3. Assign workers W1→M[0], W2-W4→far, W5-W12→near
4. Create worker labels: F1-F4 for far, N1-N4 for near
5. Use the existing WorkerLabelService.SetLabel() pattern

---

## Support References

All documentation files are in your workspace:
- `/DRAWING_PATTERN_GUIDE.md` - Add visualizations
- `/GREEDY_MINERAL_ORDERING.md` - Algorithm details
- `/GREEDY_MINERAL_ORDERING_VISUAL.md` - Visual examples
- `/IMPLEMENTATION_STATUS.md` - Full status report

**Remember**: When stuck, check the appropriate .md file first. It's all documented!
