# Mining System Roadmap - Phase 1 & Phase 2+ Architecture

## The Two-Phase System

### PHASE 1: Just-In-Time Mining (YOUR CREATION)
**Goal**: Establish 3 workers per 2 mineral nodes pattern at game start  
**Timeline**: Frame 0 through vespene available (~Frame 1800-2700)  
**Status**: Documented; implementation starts with TODAY'S WORK

### PHASE 2+: Dynamic Worker Juggling (ADVANCED CHOREOGRAPHY)
**Goal**: Choreograph workers across minerals + vespene in optimal patterns  
**Timeline**: When vespene available through late game  
**Status**: Design documented; implementation blocked on Phase 1 completion

---

## Phase 1: Just-In-Time Mining - Complete Roadmap

### STEP 1 (TODAY - TOMORROW MORNING): Harvest & Return Cargo Foundation
```
Task: Calculate and serialize HarvestPoint + DropOffPoint

Input:
  - Mineral/vespene positions (already extracted)
  - Base location (already known)
  - Sharky.MiningInfo formulas (available)

Output:
  - HarvestPoint for each mineral (position where 3 workers can stand to mine)
  - DropOffPoint for base (position where workers return to sell)
  - HarvestPoint for each vespene (position where 2-3 workers can mine)
  - Serialized to MapDataSnapshot

Time: ~1 hour
Validation: Console logs showing coordinates

Result: Foundation ready for Phase 1 worker positioning
```

### STEP 2 (DAY 2): Travel Time Calculations
```
Task: Calculate return times from each mineral/vespene to base and between resources

Input:
  - HarvestPoint for each resource (from Step 1)
  - DropOffPoint for base (from Step 1)
  - Worker movement speed (Sharky constant)

Output:
  - Mineral→Base travel time per patch
  - Base→Vespene travel time
  - Vespene→Base travel time
  - Stored in MapDataSnapshot

Purpose: Identifies travel time variance (why different patches have different cycles)

Time: ~30 minutes
Validation: Log all travel times, verify differences

Result: Travel variance data ready for Phase 2 pattern library
```

### STEP 3 (DAY 3): Worker Positioning System
```
Task: Implement worker positioning using HarvestPoint

Input:
  - HarvestPoint data (from Step 1)
  - Worker labels (already created)
  - Initial mineral assignments

Output:
  - Workers positioned at HarvestPoints
  - Maintains 3 per 2 mineral pattern
  - No worker collisions

Implementation:
  - In BabySharkMiningManager or worker assignment code
  - Calculate which 3 workers go to M1, next 3 to M2, etc.
  - Send workers to HarvestPoint positions

Time: ~1 hour
Validation: DrawWorkerLabels showing workers at correct mineral spots

Result: Phase 1 mineral saturation working in-game
```

### STEP 4 (DAY 4-5): Phase 1 Stability & Testing
```
Task: Verify Phase 1 works across multiple games

Testing:
  - Verify 12 workers reach minerals without delay
  - Verify 3:2 pattern holds (count workers at each patch)
  - Verify no worker collisions
  - Test on multiple maps (vary mineral layouts)
  - Test edge cases: 11 workers (scout/12pool), 10 workers (worker killed)

Logging:
  - Track worker positions per frame
  - Log when pattern breaks and why
  - Measure mineral income (should be consistent)

Time: ~2-3 hours
Validation: Consistent results across multiple games

Result: Phase 1 production-ready
```

---

## Phase 1 Output: What Gets Serialized

After Phase 1 completion, MapDataSnapshot contains:

```csharp
// Per-mineral data
List<Vector2Dto> MineralPositions          // M1, M2, M3, ..., M8
List<Point> MineralHarvestPoints           // Where workers stand to mine
Point MineralDropOffPoint                  // Base location

// Per-vespene data
List<Vector2Dto> VespenePositions          // V1, V2 (if present)
List<Point> VespeneHarvestPoints           // Where workers stand to mine gas
Point VespeneDropOffPoint                  // Base location

// Travel times (ready for Phase 2)
Dictionary<int, float> MineralToBaseTravelTimes          // M1→Base: 1.2s, etc.
Dictionary<(int, int), float> MineralToVespeneTravelTimes // M1→V1: 8.2s, etc.
Dictionary<int, float> VespeneToBaseTravelTimes          // V1→Base: 2.1s, etc.
```

---

## Phase 1 vs Phase 2+: Timing & Triggers

### How Phase 1 Works (Simple)
```
Frame 0-X:
  Detect 12 workers available
  ↓
  Get workers to minerals
  ↓
  Position at HarvestPoints (from calculated data)
  ↓
  Maintain 3:2 pattern
  ↓
  Hold until event: "Vespene building completes"

When event triggered:
  Transition to Phase 2
```

### How Phase 2 Works (Complex)
```
Frame X (vespene ready):
  Detect vespene available
  ↓
  Look up Pattern for current worker count
    (e.g., 11 workers → "M4_Juggle_5on4")
  ↓
  Use travel times to calculate choreography
  ↓
  Assign workers to pattern positions
  ↓
  Execute choreography (hand-offs, timing)
  ↓
  When worker count changes: re-lookup pattern, re-assign
```

---

## Why This Two-Phase Approach Works

### Phase 1 is Independent
- Works WITHOUT vespene
- Maximizes mineral income early game
- All-in builds stay in Phase 1 forever
- Works for 12, 11, or 10 workers (scout/12pool/death scenarios)

### Phase 2 Builds on Phase 1 Foundation
- Uses HarvestPoint/DropOffPoint/TravelTimes calculated in Phase 1
- Doesn't change Phase 1 behavior (just adds vespene coordination)
- Can be implemented incrementally (start with 11-14 worker patterns, expand)
- Relies on Phase 1 having established stable worker positions

### Clear Separation of Concerns
- Phase 1: "Get minerals saturated and keep them saturated"
- Phase 2: "Keep minerals saturated AND saturate vespene simultaneously"

---

## Implementation Sequence Summary

```
TODAY (Day 1):
  ✅ Fix vespene labeling
  ✅ Recognize all vespene types
  → Harvest/Return Cargo calculations START

DAY 2:
  Harvest/Return Cargo COMPLETE
  Travel time calculations START

DAY 3:
  Travel time COMPLETE
  Worker positioning system IMPLEMENTED

DAY 4-5:
  Phase 1 STABLE & TESTED
  Ready to document Phase 2 patterns

WEEK 2+:
  Pattern library generation
  Choreography engine implementation
  Phase 2 production ready
```

---

## Key Insight: Why Worker Juggling Matters

### Standard Approach (SharkBot)
```
12 workers on 8 minerals: All workers mine simultaneously
Problem: If 1 mineral has low resources → 3 workers waste time
Result: Variable income, sub-optimal throughput
```

### Phase 1 Approach (Your JIT)
```
12 workers on 8 minerals: 3:2 pattern
Benefit: Balanced load per patch
Result: Consistent, maximum mineral income
```

### Phase 2 Approach (Dynamic Juggling)
```
12 workers on 8 minerals + 1 vespene:
  Phase 1 keeps 12 workers balanced on minerals
  ↓
  As vespene becomes available:
  ↓
  Use travel time data to choreograph hand-offs
  ↓
  5 workers in vespene rotation chain
  ↓
  Remaining workers back-fill minerals perfectly
  ↓
  Result: Maximum mineral + maximum vespene simultaneously
```

---

## Files Updated by This Roadmap

- `TODO.md` - Phase 1 tasks laid out
- `HARVEST_AND_RETURN_CARGO_REFERENCE.md` - Foundation calculations
- `DYNAMIC_WORKER_JUGGLING_SYSTEM.md` - Phase 2+ design
- `MINING_SYSTEM_EVOLUTION.md` - Comparison and context

All three documents now clearly show Phase 1 ↔ Phase 2+ relationship.

---

## Ready to Start?

**TODAY'S FIRST TASK**: Implement Harvest/Return Cargo calculations
- Reference: `HARVEST_AND_RETURN_CARGO_REFERENCE.md`
- Uses: Sharky.MiningInfo formulas
- Output: HarvestPoint, DropOffPoint, serialized to map data
- Time: ~1 hour
- Result: Phase 1 foundation complete, ready for worker positioning

Let's build! 🚀
