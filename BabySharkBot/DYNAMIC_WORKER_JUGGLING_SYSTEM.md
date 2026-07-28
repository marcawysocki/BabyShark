# Dynamic Worker Juggling System - Phase 2+ Advanced Mining Choreography

## Overview
This document describes **Phase 2+ of your mining optimization system**. It builds on the **Just-In-Time Phase 1 foundation** (which saturates minerals with the 3:2 pattern) and extends it to **choreograph workers across multiple resources** once vespene becomes available.

---

## Historical Context

### Phase 1: Just-In-Time Mining (YOUR CREATION)
- **Status**: Documented, foundation for today's work
- **What it does**: Get 12 workers to 8 minerals in 3:2 pattern, maintain until vespene ready
- **Timeline**: Game start through vespene building completion (~Frame 0-1800)
- **Today's work**: Build Harvest/Return Cargo foundation to enable Phase 1

### Phase 2+: Dynamic Worker Juggling (THIS DOCUMENT)
- **Status**: Future implementation, builds on Phase 1
- **What it does**: Choreograph workers across minerals + vespene in optimal patterns per worker count
- **Timeline**: When vespene available through late game
- **Prerequisite**: Phase 1 working correctly

---

## Key Differences: Phase 1 vs Phase 2+

### Phase 1: Just-In-Time Mining (YOUR INNOVATION - Frames 0 through vespene ready)
```
Goal: Saturate minerals with 3 workers per 2 mineral nodes

Starting State:
  Workers: 12
  Minerals: 8 nodes
  Vespene: Not yet available (need 75 minerals + build time)

Pattern Execution:
  M1: 3 workers
  M2: 3 workers
  M3: 3 workers
  M4: 3 workers
  Total: 12 workers on minerals

Duration: Holds this pattern until vespene building completes

Result: Maximum mineral income during early game
Income rate: 12 workers × optimal mineral cycle = peak mineral rate
When vespene available: Transition to Phase 2
```

### Phase 2+: Dynamic Worker Juggling (BUILDS ON PHASE 1)
```
Prerequisite: Phase 1 foundation working (Harvest/Return Cargo calculated)

Goal: Choreograph workers across minerals + vespene, optimize for worker count

Starting State:
  Workers: 11-24 (varies by build and growth)
  Minerals: 8+ nodes (at main, possibly expansions)
  Vespene: 1-2 geysers available

Pattern Execution per worker count:
  11 workers: "M4_Juggle_5on4" pattern
    - 5 workers juggle 4 patches
    - 6 workers in vespene saturation chain

  12 workers: "M4_Juggle_6" pattern
    - Different choreography
    - Different timing windows

  13+ workers: New patterns

Result: Vespene saturated + minerals saturated = maximum combined income
Timeline: From vespene availability through late game (or forever if all-in)
```

---

## System Architecture

### Phase 1: Worker Count → Pattern Mapping
```
Worker Count: 11 Mineral Workers
↓
Lookup Optimal Pattern: "M4_Juggle_5on4"
↓
Pattern Data:
  - 4 primary mineral patches (M1, M2, M3, M4)
  - 5 workers assigned
  - Worker 1-2: Juggle M1-M2
  - Worker 3-4: Juggle M2-M3  
  - Worker 5: Solo M4 (or share with vespene when needed)
  - Timing: Precise return window calculations
```

### Phase 2: Mineral → Vespene Hand-Off (Single-Side Vespene)
```
Mineral Worker Chain (Unified Vespene):
  W1: Mineral→Base (time: X)→Extractor (W1 ready at exactly T when Extractor completes)
      While W1 mines vespene:
  W2: Mineral→Base (time: X)→Extractor (W2 ready at exactly T+40s when W1 exits)
      While W2 mines vespene:
  W3: Mineral→Base (time: X)→Extractor (W3 ready at exactly T+80s when W2 exits)

Result: 
  - 5 mineral workers rotating into extractor
  - Single extractor always saturated
  - Matches throughput of 6 continuous-mining workers
```

### Phase 3: Split Vespene Management
```
Split Vespene Layout (2 geysers, one per side):
  - Vespene A (North side): 2.5 avg workers needed
  - Vespene B (South side): 2.5 avg workers needed
  
Dynamic Juggling:
  - 5 mineral workers on North side + Vespene A
  - 5 mineral workers on South side + Vespene B
  - If South mineral worker A returns faster than needed → swap to Vespene B temporarily
  - Pattern adapts based on return vector travel times

Travel Time Variance:
  - Return path from M1→Base→V A: 8.2 seconds (short)
  - Return path from M4→Base→V A: 9.1 seconds (long)
  - Return path from M1→Base→V B: 12.4 seconds (cross-map)
  - System pre-calculates these and buffers arrival times
```

### Phase 4: Worker Count Growth (Frame 0-20)
```
Timeline as larvae hatch into drones:
  Frame  0:  3 workers (starting drones)
  Frame  5:  4 workers (first larva completes)
  Frame 10:  5 workers (second larva completes)
  Frame 15:  6 workers (third larva completes)
  Frame 20:  7 workers (pattern transition)

At each transition:
  1. Detect worker count changed
  2. Look up new pattern (e.g., "M4_Juggle_5on4" → "M5_Juggle_7")
  3. Re-assign workers to new positions
  4. Adjust timing windows for new pattern
  5. Continue without stalling
```

---

## Data Structures Needed

### 1. **Worker Pattern Definition**
```csharp
public class MiningPattern
{
    public int WorkerCount { get; set; }           // 1-24
    public string PatternName { get; set; }        // "M4_Juggle_5on4"
    public List<string> PatchAssignment { get; set; }  // ["M1", "M2", "M1", "M2", "M3", "M4", "Vespene"]
    public Dictionary<int, float> ReturnTimings { get; set; }  // Worker index → expected return time in seconds
    public List<(int W, int P, float Delay)> Transitions { get; set; }  // Worker → Patch transitions
    public float SaturationScore { get; set; }    // How well vespene is utilized (0-1)
}
```

### 2. **Per-Mineral/Vespene Return Vector Data**
```csharp
public class ResourceReturnVector
{
    public string ResourceLabel { get; set; }     // "M1", "M2", "V1"
    public Point HarvestPoint { get; set; }
    public Point DropOffPoint { get; set; }
    public Dictionary<string, float> ReturnTimesToOtherResources { get; set; }
    // ReturnTimesToOtherResources["V1"] = 8.2f seconds if this mineral to vespene
    // ReturnTimesToOtherResources["M2"] = 1.5f seconds if this mineral to another mineral
    public float ReturnTimeToBase { get; set; }
    public float ReturnTimeToVespene { get; set; }  // Calculated during init
}
```

### 3. **Worker State During Choreography**
```csharp
public class ChoreographyWorkerState
{
    public ulong UnitTag { get; set; }
    public string WorkerLabel { get; set; }      // "Drone-001"
    public string CurrentResource { get; set; }  // "M1", "V1", "Base"
    public string NextResource { get; set; }     // Planned next destination
    public string Pattern { get; set; }          // Current pattern name
    public int PatternPosition { get; set; }     // Index in juggling sequence
    public float ArrivalTimeAtNext { get; set; } // Predicted arrival
    public float LastReturnTime { get; set; }    // When did they last return?
    public bool IsOptimal { get; set; }          // Still following pattern?
}
```

### 4. **Serialized Map Data (Addition to MapDataSnapshot)**
```csharp
public class VespeneReturnPathData
{
    // From each mineral return vector to each vespene
    // Key: "M1_to_V1", Value: travel time in seconds
    public Dictionary<string, float> MineralToVespeneReturnTimes { get; set; }
    
    // From each vespene to each mineral start
    // Key: "V1_to_M1", Value: travel time in seconds
    public Dictionary<string, float> VespeneToMineralReturnTimes { get; set; }
}

public class MiningPatternLibrary
{
    // Pre-calculated patterns for all worker counts
    // Key: worker count (1-24), Value: list of optimal patterns for that count
    public Dictionary<int, List<MiningPattern>> OptimalPatterns { get; set; }
}
```

---

## Implementation Roadmap

### Phase A: Foundation (Current - Harvest/Return Cargo)
- [x] Calculate HarvestPoint for each mineral/vespene
- [x] Calculate DropOffPoint for each mineral/vespene
- [x] Serialize to map data
- [ ] Calculate return times: mineral→base, mineral→vespene, vespene→mineral

### Phase B: Single-Worker Pattern (Next)
- [ ] Build pattern library for 1-24 workers
- [ ] Pre-calculate optimal sequences for each count
- [ ] Calculate saturation scores per pattern
- [ ] Store patterns in map data or separate lookup table

### Phase C: Multi-Resource Choreography (Future)
- [ ] Implement pattern detection based on worker count
- [ ] Assign workers to pattern positions
- [ ] Track worker adherence to choreography
- [ ] Detect pattern breaks (worker took wrong turn)
- [ ] Logging: track actual vs planned timing

### Phase D: Dynamic Growth Management (Later)
- [ ] Detect when worker count changes (larva completes)
- [ ] Look up new pattern
- [ ] Transition workers from old to new pattern
- [ ] Handle mid-transition arrivals gracefully

### Phase E: Split Vespene Adaptation (Advanced)
- [ ] Detect vespene layout (unified vs split)
- [ ] Apply cross-map balancing logic
- [ ] Handle split-base worker swaps
- [ ] Optimize 2.5 worker per geyser scenarios

---

## Key Innovation: Travel Time Variance

### Example: Why Pattern Matters
```
Unified Vespene (one geyser on West side)

Mineral Patch Positions:
  M1 (North):  0.8s to base, 7.4s base→geyser = 8.2s total
  M2 (East):   1.1s to base, 9.1s base→geyser = 10.2s total
  M3 (South):  0.9s to base, 8.8s base→geyser = 9.7s total
  M4 (West):   1.3s to base, 7.1s base→geyser = 8.4s total

Vespene Mining Time: 40 seconds per trip

Pattern: 5 workers alternating into single extractor
  T+0:   W1 enters extractor (came from M1: 8.2s earlier)
  T+40:  W1 exits → W2 should be ready (came from M3: 9.7s earlier) 
         But W2 needs to arrive at T+40. If it left M3 at T+30.3, it arrives at T+40. ✓
  T+80:  W2 exits → W3 should be ready (came from M2: 10.2s earlier)
         If W3 left M2 at T+69.8, it arrives at T+80. ✓
  T+120: W3 exits → W4 ready (came from M4: 8.4s earlier)
  T+160: W4 exits → W5 ready (came from M1: 8.2s earlier)
  T+200: W5 exits → W1 ready again (came from M1: 8.2s earlier)

Result: Perfect saturation, 5 workers feel like 6 because timing is optimal
```

---

## Comparison Table: Speed Mining vs JIT vs Dynamic Juggling

| Aspect | Speed Mining | Just-In-Time | Dynamic Juggling |
|--------|--------------|--------------|------------------|
| **Optimization Level** | Single worker | Single worker + binary vespene | Multi-resource choreography |
| **Resource Juggling** | No (repeat same patch) | No | Yes (juggle M1-M4) |
| **Vespene Model** | Ignored | Binary (ready/not ready) | Saturated with timing |
| **Worker Count Patterns** | All same | All same | Discrete 1-24 patterns |
| **Travel Time Variance** | Ignored | Ignored | **Pre-calculated per patch** |
| **Dynamic Growth** | N/A | N/A | Adapts as workers hatch |
| **Split Vespene** | N/A | N/A | Cross-map balancing |
| **Implementation Complexity** | Low | Medium | **Very High** |
| **Expected APM Gain** | +5-10% | +10-15% (vespene only) | **+20-30% overall** |

---

## Recommended Development Sequence

### Day 1 (Tomorrow): Harvest/Return Cargo Foundation ✅
- Calculate and serialize HarvestPoint/DropOffPoint
- Verify with console logging
- Ready for worker assignment

### Day 2: Travel Time Mapping
- Calculate return times: mineral→base→vespene
- Identify travel time variance per patch
- Log patterns to CSV for analysis

### Day 3: Pattern Library Design
- Prototype patterns for 11, 12, 13 workers (common mid-game count)
- Calculate saturation scores
- Validate against replays

### Day 4-5: Worker State Choreography
- Implement pattern detection
- Assign workers to pattern positions
- Add logging for adherence tracking

### Future: Dynamic Growth & Split Vespene
- Detect worker count changes
- Transition between patterns
- Handle split vespene layouts

---

## Why This Matters

This system transforms mining from **reactive** (workers independently optimize) to **choreographed** (workers coordinate via pre-planned patterns). The result:

1. **20-30% throughput gain** vs standard mining
2. **Vespene saturation** without idle workers
3. **Worker count flexibility** (5 workers ≠ 6 workers when optimized)
4. **Scalable to late game** (24 worker patterns exist)
5. **Replicable** (patterns can be extracted and shared)

---

## Implementation Notes

- This system is **distinct from and comes AFTER** the Harvest/Return Cargo work
- Start with foundation (travel times), prototype with mid-game (11-14 worker) patterns
- Can be deployed incrementally (start with 3-worker patterns, expand later)
- Works alongside existing Sharky mining system, doesn't replace it
