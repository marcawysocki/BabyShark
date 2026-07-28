# TODO List

## PRIORITY 1 - MAP DATA START[1] VALIDATION

### Validate Map Data Across Different Start Locations
**Priority:** HIGH - Phase 1 Validation  
**Status:** PENDING (after InitialMapData start[0] complete)  
**Reference:** `BabySharkBot/BASELINE_MINERAL_ACCUMULATION.md`  
**Baseline Data:** Frame 335 (14.96s) = 200 minerals, Frame 248 (11.1s) = 160 minerals

**Objective**
After `InitialMapData` successfully processes and writes map data for start location [0], validate that the same map data works correctly when spawning at start location [1].

**Current State**
- ✅ Map data pipeline defined (Parse map → Calculate COM → Save to .dat file)
- ✅ Baseline mineral accumulation captured (Frame-level data for start[0])
- ✅ Vespene positions now correct (Z coordinate fixed, all geyser types included)
- ✅ Console mineral tracking added (frame-by-frame logging)
- ⏳ **Only tested on start location [0]**
- ❌ **Not yet validated on start location [1]**

**Process**

1. **Play map on start location [0]** 
   - Run full game with InitialMapData processing
   - Verify map data written to `Setup/MapData/{MapName}.dat`
   - Capture baseline mineral frame numbers (Frame 335 for 200 minerals)
   - Verify all mineral/vespene positions correctly labeled
   - Document frame numbers in console output

2. **Play SAME map on start location [1]**
   - Load existing map data from .dat file (should skip re-calculation via MapDataManager cache)
   - Verify mineral/vespene positions still correct from different spawn point
   - Verify center of mass relevance from new starting position
   - Capture mineral accumulation frame numbers (should match start[0])
   - Compare console mineral output to start[0] baseline

3. **Document Findings**
   - [ ] Log mineral accumulation for start[1] (should match start[0])
   - [ ] Verify start location detection in InitialMapData (multiStartingUnits[si])
   - [ ] Verify MapDataManager properly loads cached .dat file
   - [ ] Document coordinate system differences (if any)
   - [ ] Confirm map data is location-agnostic (not tied to single spawn)

**Expected Outcome**
- Same map data file works correctly from both start locations
- Mineral accumulation timings match (Frame 335 for 200 minerals on both)
- Map data is properly location-independent
- Baseline established for multi-location testing

**Why This Matters**
- Ensures map data calculations are robust (not accidentally dependent on start[0])
- Validates that Phase 1 worker system works regardless of spawn location
- Prepares for tournament scenarios where you do not know start location in advance
- Confirms MapDataManager cache correctly prevents re-processing on same map

**Trigger Condition**
- Start this task AFTER: InitialMapData fully processes start[0] and writes to disk successfully

---

## PRIORITY 1.5 - SECONDARY AND CONTINUING MAP DATA

### Add SecondaryMapData and ContinuingMapData
**Priority:** HIGH - Phase 1/2 data flow support
**Status:** PENDING

**Objective**
Add separate map-data paths for first-time processing of start[1] or start[2], and for continuing runs on maps/locations that have already been played.

`InitialMapData` should only run when a new map is played for the first time.
`SecondaryMapData` should only run when a new start location that has not been played loads for the first time.

For start[1] or start[2], once the worker positions are learned for the first time, calculate worker, mineral, vespene, and building placements from the greedy worker chain.

**Tasks**
- [ ] Create `SecondaryMapData` path for first-time `start[1]` or `start[2]` processing
- [ ] Create `ContinuingMapData` path for maps and locations that have already been played
- [ ] Define when cached data should be reused vs recalculated
- [ ] Ensure spawning pool placements and cargo points are loaded from existing `BaseDtos` data when available
- [ ] Use the greedy worker chain as the basis for worker/mineral/vespene/building placements on first-time start[1]/start[2] processing

**Notes**
- `InitialMapData` handles the first time a new map is played.
- `SecondaryMapData` handles a first-time spawn location on that map that has not been played before.
- `ContinuingMapData` should be used on replayed maps or previously processed spawn locations.

---

## PRIORITY 1.75 - RAMP BUILD PLACEMENTS

### Add Macro Hatchery and Roach Warren Ramp Placements
**Priority:** HIGH - Phase 1 base setup support
**Status:** PENDING

**Objective**
For every start location processed during `InitialMapData`, calculate a top-of-ramp macro hatchery placement and a roach warren placement inside the main. The hatchery should touch the unbuildable edge of the ramp when possible and prefer the vespene/mineral side unless that would make placement illegal.

**Tasks**
- [ ] Add macro hatchery placement storage to `BaseDtos.cs`
- [ ] Add roach warren placement storage to `BaseDtos.cs`
- [ ] Calculate ramp placements during `InitialMapData`
- [ ] Respect legal placement if the preferred side is too close to resources
- [ ] Use the same learned-worker / greedy-chain basis for start[1]/start[2] placement logic when applicable

**Notes**
- Sharky already draws ramp domes, so these placements should align with the top-of-ramp build region.
- The hatchery should favor the resource side when legal.
- If the legal build area is too tight, place the hatchery on the opposite legal side.

---

## PRIORITY 2 - HARVEST & RETURN CARGO CALCULATIONS

### Add Harvest & Return Cargo Calculations to Mineral/Vespene Data
**Priority:** HIGH - Phase 1 Foundation for JIT Mining  
**Status:** READY TO START (after Start[1] validation)  
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
- [ ] Monitor if this is the only location where `BaseLocations.FirstOrDefault()` result is not validated before use
- [ ] Check if other micro-tasks have similar vulnerability
- [ ] Add defensive programming pattern review to code standards
