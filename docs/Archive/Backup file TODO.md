# TODO List

## PRIORITY 1 - TOMORROW'S FIRST TASK

### Add Harvest & Return Cargo Calculations to Mineral/Vespene Data
**Priority:** HIGHEST - Phase 1 Foundation for JIT Mining  
**Status:** READY TO START - Vespene labeling complete  
**Reference:** `BabySharkBot/HARVEST_AND_RETURN_CARGO_REFERENCE.md`  
**Roadmap:** `BabySharkBot/PHASE_1_PHASE_2_ROADMAP.md`  
**Related:** `BabySharkBot/DYNAMIC_WORKER_JUGGLING_SYSTEM.md` (Phase 2+ future work)

**Objective**
Add HarvestPoint and DropOffPoint calculations to mineral and vespene data structures. These will be serialized in map data and used by worker assignment system for optimal positioning.

**Current State**
- ✅ Mineral positions stored in map data (multiMainMinerals)
- ✅ Vespene positions stored in map data (multiMainVespenes)
- ✅ Start location(s) locked down
- ❌ **HarvestPoint/DropOffPoint NOT calculated**
- ❌ **These values NOT serialized to map data**
- ❌ **Expansion minerals/vespenes NOT yet handled**

**Tasks (In Order)**

1. **Update Data Structures**
   - [ ] Add `HarvestPoint: Point` to `OrderedMineral` class (BaseDtos.cs)
   - [ ] Add `DropOffPoint: Point` to `OrderedMineral` class (BaseDtos.cs)
   - [ ] Add `HarvestPoint: Point` to `OrderedVespene` class (BaseDtos.cs)
   - [ ] Add `DropOffPoint: Point` to `OrderedVespene` class (BaseDtos.cs)

2. **Calculate Harvest/Dropoff Points - MINERALS**
   - [ ] In `InitialMapData.cs` RegisterMineralLabels() method
   - [ ] For each ordered mineral, calculate:
     - **DropOffPoint**: 2 units from base, along vector from base→mineral
       - Formula: `basePos + 2 * normalize(mineralPos - basePos)`
     - **HarvestPoint**: 0.5 units from mineral, along vector from mineral→base
       - Formula: `mineralPos + 0.5 * normalize(basePos - mineralPos)`
   - [ ] Set these in OrderedMineral before storing in service

3. **Calculate Harvest/Dropoff Points - VESPENE**
   - [ ] In `InitialMapData.cs` RegisterVespeneLabels() method
   - [ ] Apply same formulas to vespene geysers
   - [ ] Set these in OrderedVespene before storing in service

4. **Serialize to Map Data**
   - [ ] Update `MapDataSnapshot` (BaseDtos.cs) to store per-mineral harvest/dropoff data
     - Add: `Dictionary<int, (Point HarvestPoint, Point DropOffPoint)> MineralPoints`
     - OR: Extend existing MineralPatches to include these values
   - [ ] Update `MapDataSnapshot` to store per-vespene harvest/dropoff data
     - Add: `Dictionary<int, (Point HarvestPoint, Point DropOffPoint)> VespenePoints`

5. **Test & Validate**
   - [ ] Verify calculations by logging harvest/dropoff coordinates
   - [ ] Compare calculated points with Sharky's MiningInfo.cs formulas
   - [ ] Test on multiple maps (flat and varied terrain)
   - [ ] Ensure Z-coordinates are correct (terrain height)

**Expected Outcome**
- Minerals: Each has optimal HarvestPoint (where worker stands to mine) and DropOffPoint (where worker returns cargo)
- Vespenes: Same harvest/dropoff data
- Map Data: Serialized with all calculations
- Foundation: Ready for worker pre-positioning on Frame 1

**Implementation Reference**
See `Sharky/MiningInfo.cs` for the formulas already used by Sharky framework.

**Why This Matters**
These calculations enable:
- Optimal worker placement (multiple workers on same mineral without collision)
- Predictable return paths (workers know where to return cargo)
- Speed mining optimizations (approach vectors can then be layered on top)
- Worker micro coordination (roles can be choreography-aware with known positions)

**Estimated Effort**
- Update classes: 10 min
- Implement calculations: 20 min  
- Serialize to map data: 15 min
- Test & verify: 15 min
- **Total: ~1 hour**

---

## CRITICAL - Crash Prevention

### NullReferenceException in MiningDefenseService
**Priority:** HIGH - Causes crash when game plays out to loss  
**Issue:** `System.NullReferenceException` in `MiningDefenseService.cs` at `WorkerMicroController.Bait()` call.

**Root Cause**  
`Run()` method receives `null` for `selfBase` parameter when `BaseData.BaseLocations.FirstOrDefault()` returns null. Method then attempts to access `selfBase.Location` without validation.

**Tasks**
- [ ] Add null check at start of `Run()` method in `Sharky/MicroTasks/Mining/MiningDefenseService.cs`
  - Add: `if (selfBase == null) return actions;` as first line after `var actions = new List<SC2APIProtocol.Action>();`

- [ ] Add null check before calling `Run()` in liberation zone effect handler in `Sharky/MicroTasks/Mining/MiningDefenseService.cs` around line 76
  - Store `BaseData.BaseLocations.FirstOrDefault()` in variable
  - Only call `Run()` if the variable is not null

**Files to Modify**
- `Sharky/MicroTasks/Mining/MiningDefenseService.cs`

**Impact**
- Prevents crashes during defense scenarios when base locations may be empty
- Allows game to complete without crash on loss condition
- No behavioral changes, defensive null checks only

**Trigger Scenario**
- Occurs late game during loss condition
- Related to liberation zone effect handling or worker defense when bases are being destroyed

---

## Drawing Features

### Draw Center of Mass (COM)
**Priority:** Medium  
**Status:** TODO

**Requirements**
- [ ] Create COM visualization service in `Sharky/SharkyData/` (or appropriate location)
- [ ] Draw crosshair at mineral center of mass location using `DrawLine()` primitives
- [ ] Draw sphere at COM center point using `DrawSphere()` 
- [ ] Gate drawing with `SharkyOptions.Debug` flag
- [ ] Use actual Z coordinates: map height at COM location or ground level
- [ ] Integrate into appropriate manager/task for per-frame updates

**Implementation Notes**
- Follow BABYSHARK DEBUG VISUALIZATION PATTERN from copilot-instructions.md
- Use DebugService.DrawText/DrawLine/DrawSphere with proper Point coordinates


### Draw Expansion Locations
**Priority:** Medium  
**Status:** TODO

**Requirements**
- [ ] Create expansion location visualization service
- [ ] Draw sphere at each known expansion location (BaseData.BaseLocations)
- [ ] Use color coding: owned bases (green), enemy bases (red), expansions (yellow/neutral)
- [ ] Gate drawing with `SharkyOptions.Debug` flag
- [ ] Display expansion distances or priority if available
- [ ] Integrate into appropriate manager for per-frame updates

**Implementation Notes**
- Use BaseData.BaseLocations and BaseData.EnemyBaseLocations for positions
- Consider layering with different Z offsets if overlapping visualizations
- Could add labels with worker count or resource status

---

## Additional Investigation
- [ ] Monitor if this is the only location where `BaseLocations.FirstOrDefault()` result isn't validated before use
- [ ] Check if other micro-tasks have similar vulnerability
- [ ] Add defensive programming pattern review to code standards

