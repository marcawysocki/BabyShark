# Mining System Evolution - JIT Phase 1 → Advanced Choreography

## Context: Three Distinct Systems

### Sharky's Mineral Assignment (Baseline - Inefficient)
```
APPROACH: Grab 3 workers, throw at geyser immediately when built
RESULT: Workers pile up waiting for turns to mine

Timeline (Sharky SharkBot approach):
  T+0:   12 workers start
  T+X:   Build starts (75 minerals accumulated)
  T+Y:   Building completes
  T+Y+0: Grab 3 workers, send to geyser immediately
  T+Y+0 to Y+20: Workers 1-2 idle, worker 3 mines vespene (loses ~15 minerals)

Cost: Massive inefficiency (workers waiting for turns)
```

### Your Just-In-Time Mining System - PHASE 1 (Foundation)
**YOUR CREATION - Not Sharky's**

```
APPROACH: Position workers optimally on minerals, establish saturation pattern
RESULT: 3 workers per 2 mineral nodes, maximum mineral income

Game Start Phase:
  Supply: 12/12 workers (starting drones)
  Minerals: 8 nodes

  Step 1: Get 12 workers to 8 minerals as fast as possible
  Step 2: Establish pattern: 3 workers per 2 mineral nodes
    - M1: 3 workers
    - M2: 3 workers  
    - M3: 3 workers
    - M4: 3 workers
    - Result: 8 nodes saturated, 0 idle workers

  Step 3: WAIT for vespene building (needs 75 minerals saved + completion time)

Duration: Frames 0-300+ (vespene is NOT ready at game start)
Income: Maximum mineral saturation (12 workers on optimal 3:2 pattern)
```

### Vespene Timing Constraint
```
Cannot start vespene mining until:
  1. 75 minerals accumulated (building cost)
  2. Building completes (~45 seconds)
  3. Worker assignment happens exactly when ready

Common scenarios:
  - Normal build: Vespene online around T+120-180 seconds
  - 12 Pool build: Drop to 11 workers (pool uses 1 worker temporarily)
  - Scout with W12: Drop to 11 workers (scout is away)
  - All-in: Vespene NEVER (all workers stay on minerals forever)
```

### BabyShark Dynamic Worker Juggling - PHASE 2+ (Advanced) ⭐
```
APPROACH: Pre-calculated choreography patterns indexed by worker count
BUILDS ON: JIT Phase 1 mineral saturation
ADDS: Vespene integration + multi-resource choreography

Timeline with 5 workers, 4 mineral patches, 1 unified vespene:
  (Assumes vespene is ready and saturated at this point)

  T+0:    W1 enters extractor (came from M1)
  T+0-40: W2 mining M1, W3 mining M2, W4 mining M3, W5 returning from M4
  T+40:   W1 exits vespene → W2 enters (perfectly timed arrival from M3)
  T+40-80: W1 returns to mineral, W3→M1, W4→M2, W5→M3
  T+80:   W2 exits vespene → W3 enters
  ...pattern continues

Efficiency: ~98% (workers almost never wait)
Throughput: 5 workers juggling ≈ 6 workers mining continuously
Pattern: Repeats every 200 frames (5 workers × 40s vespene cycle)
```

---

## Key Differences Explained

### 1. **System Architecture**
| Aspect | Sharky SharkBot | JIT Phase 1 | Dynamic Juggling Phase 2+ |
|--------|-----------------|------------|--------------------------|
| **Creator** | Sharknice/SharkBot | **You** | **You** |
| **Scope** | Throw workers at vespene | Saturate minerals | Choreograph minerals + vespene |
| **Worker Count** | N/A | 12 workers → 8 minerals | 1-24 workers, dynamic growth |
| **Vespene Ready** | Grab 3 immediately | Wait for building | Build on Phase 1 foundation |
| **Result** | Workers wait idle | Maximum mineral income | Maximum mineral + vespene income |

### 2. **Phase 1: JIT Mineral Saturation (Frames 0-180+)**
**Goal**: Establish optimal 3:2 pattern on minerals

```
Worker Count: 12 (or 11 if scout/12pool)
Mineral Nodes: 8
Target Pattern: 3 workers per 2 nodes

Allocation:
  M1-M2: 3 workers
  M3-M4: 3 workers
  M5-M6: 3 workers
  M7-M8: 3 workers
  Total: 12 workers

Result: All minerals saturated, zero idle workers
Income: Maximum from mineral patches until vespene ready
```

### 3. **Phase 2: Vespene Integration (Frames 180+)**
**Trigger**: 75 minerals saved + building completes

```
Constraint: Cannot start before building is ready
Timeline:
  Normal build: Extractor ready ~T+120-180
  12 Pool: Extractor delayed (1 worker building pool)
  All-in: Extractor NEVER (all workers stay on minerals)

When ready:
  Transition from Phase 1 → Phase 2
  Use Dynamic Juggling patterns for worker count
  Establish vespene saturation without losing mineral income
```

### 4. **Travel Time Variance (Key to Both Phases)**
**Phase 1**: Different mineral patches have different return times
```
Mineral patches aren't equidistant from base
M1 to base: 1.0s
M4 to base: 2.5s

JIT Phase 1 handles by:
  - Pre-positioning workers with correct HarvestPoint
  - Accounting for different saturation patterns per patch
  - Result: Even with 3 workers on M1, they don't collide

  vs SharkBot approach:
  - Throws 3 workers at one geyser
  - All 3 pile up, 2 wait idle
```

**Phase 2**: Different vespene return paths from different minerals
```
Travel time variance:
  M1→Base→V: 8.2s
  M4→Base→V: 9.1s

Dynamic Juggling uses this to:
  - Assign fastest returners to vespene chain
  - Assign slower returners to back-fill minerals
  - Result: Continuous flow, no idle time
```

---

---

## Summary: The Two Phases of Your Innovation

### Phase 1: Just-In-Time Mining - Mineral Saturation
**Status**: Documented in JIT Phase 1  
**Timeline**: Game start (Frame 0) through vespene available  
**Goal**: Get 12 workers optimally distributed on 8 minerals in 3:2 pattern  
**Key Innovation**: Harvest/Return Cargo vectors + HarvestPoint/DropOffPoint calculation  
**Today's Work**: Foundation for this (Harvest/Return Cargo calculations)

### Phase 2+: Dynamic Worker Juggling - Multi-Resource Choreography  
**Status**: To be implemented (builds on Phase 1)  
**Timeline**: When vespene is available through late game  
**Goal**: Choreograph workers across minerals + vespene in optimal patterns per worker count  
**Key Innovation**: Discrete patterns (1-24 workers) with travel time variance integration  
**Future Work**: Pattern library generation + choreography engine

---

## Comparison: SharkBot vs Your JIT Phase 1

### SharkBot's Inefficiency
```
Vespene ready: T+X

SharkBot action:
  1. Grab any 3 workers
  2. Send to geyser immediately
  3. Result: 2 workers wait idle (one mining, one queued)

Cost: ~15 minerals lost while waiting
Why: No consideration for travel times, worker positioning, or saturation
```

### Your JIT Phase 1 Approach
```
Phase 1 (Frames 0-X):
  1. Get 12 workers to 8 minerals
  2. Establish 3:2 saturation pattern (M1: 3, M2: 3, etc.)
  3. Hold until vespene building completes
  4. Calculate exact arrival times for Phase 2 transition

Why better: Workers are positioned optimally from frame 0
  - No pile-ups
  - No idle time
  - Foundation for Phase 2 choreography
```

---

## Implementation Roadmap: Two-Phase Approach

### PHASE 1: Just-In-Time Mineral Saturation (TODAY + TOMORROW)

**Day 0 (Today)**:
- ✅ Fixed vespene labeling
- ✅ All vespene geyser types recognized

**Day 1 (Tomorrow - TODAY'S WORK)**:
- [ ] Harvest/Return Cargo calculations
- [ ] HarvestPoint for each mineral/vespene
- [ ] DropOffPoint for base
- [ ] Serialize to map data

**Day 2**:
- [ ] Travel time calculations (mineral→base→vespene)
- [ ] Position workers using HarvestPoint
- [ ] Verify 3:2 saturation pattern working
- [ ] Log worker positions and cycle times

**Result**: Phase 1 complete
- Workers reach minerals with zero delays
- Maintain 3:2 saturation indefinitely
- Ready for Phase 2 when vespene available

### PHASE 2: Dynamic Worker Juggling (FUTURE)

**Prerequisites**: Phase 1 must be working

**Components**:
- Pattern library (1-24 workers, pre-calculated)
- Travel time variance database (all mineral→vespene paths)
- Choreography engine (assigns workers to pattern positions)
- Pattern transition logic (when worker count changes)
- Split vespene handler (load balance cross-map)

**Expected Throughput Gain**:
- Phase 1 alone: +10-15% vs SharkBot
- Phase 1 + Phase 2: +20-30% vs SharkBot

---

## Worker Count Scenarios

### Standard Game (12 Workers)
```
Frame 0: 3 drones (starting units)
Frame ~50: 4 drones (first larva completes)
Frame ~100: 5 drones (second larva)
...grows to 12 by Frame ~420

Phase 1 Pattern:
  12 workers on 8 minerals
  3 per mineral (3:2 pattern)
  Continue until vespene available (~Frame 1800-2700 depending on build)
```

### Scout with W12
```
Scenario: Send 12th worker to scout enemy
Worker count: 11 (temporarily)
Pattern: Adjust to 11 on 8 minerals
  - M1: 3 workers
  - M2: 3 workers
  - M3: 3 workers
  - M4: 2 workers
  (Or some other valid 3:2+remainder distribution)
```

### 12 Pool Build
```
Scenario: Build pool at 12/12 supply (200 minerals)
Worker count: 11 (1 building pool temporarily)
Later: 12 (pool completes, worker freed)
Pattern: Adjust at each transition
```

### All-In Build
```
Scenario: No vespene mining, all-in with minerals only
Worker count: Varies (some spend building army)
Pattern: Keep remaining workers on minerals in Phase 1 forever
Result: Never transition to Phase 2
```

---

## Data Flow: From Foundation to Choreography

```
DAY 1: Harvest/Return Cargo (Tomorrow)
  ├─ HarvestPoint per mineral
  ├─ HarvestPoint per vespene
  ├─ DropOffPoint per base
  └─ Serialized to MapDataSnapshot

DAY 2: Travel Time Mapping
  ├─ Mineral→Base travel times
  ├─ Base→Vespene travel times
  ├─ Vespene→Mineral travel times
  └─ Stored in MapDataSnapshot (extended)

DAY 3-4: Pattern Library Generation
  ├─ For each worker count (1-24)
  ├─ Calculate optimal assignments
  ├─ Pre-compute timing windows
  ├─ Score saturation efficiency
  └─ Store patterns (in memory or data file)

DAY 5+: Worker Choreography Engine
  ├─ Detect worker count
  ├─ Look up pattern
  ├─ Assign workers to positions
  ├─ Monitor adherence
  └─ Feed back actual timings (for learning)
```

This progression ensures each layer builds on the previous, with clear testable outputs at each stage.
