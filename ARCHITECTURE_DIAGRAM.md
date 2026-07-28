# Architecture & Data Flow Diagram

## Complete System Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         BABYSHARKAI INITIALIZATION                   │
└─────────────────────────────────────────────────────────────────────┘
                                   │
                    ┌──────────────┴──────────────┐
                    │                             │
        ┌───────────▼─────────────┐   ┌──────────▼──────────────┐
        │  WorkerLabelService     │   │  CrosshairService      │
        │ (Worker tracking)       │   │ (COM visualization)    │
        │                         │   │                        │
        │ SetLabel(name, tag)     │   │ SetCOM(pos, label)     │
        │ GetLabel(tag)           │   │ GetAllCOMs()           │
        └────────────┬────────────┘   └────────────┬───────────┘
                     │                             │
                     └──────────────┬──────────────┘
                                    │
                    ┌───────────────▼──────────────┐
                    │ BabySharkMiningManager       │
                    │ Constructor receives both   │
                    │ services                    │
                    └───────────────┬──────────────┘
                                    │


┌─────────────────────────────────────────────────────────────────────┐
│                   GAME START: ONINITIALMAP()                         │
└─────────────────────────────────────────────────────────────────────┘
                                   │
                    ┌──────────────▼──────────────┐
                    │                             │
        ┌───────────▼─────────────┐       ┌──────▼───────────────────┐
        │ InitialMapData          │       │ MapDataManager           │
        │ GetNewMiningData()      │       │ (loads from disk if      │
        │                         │       │  previously saved)       │
        └───────────┬─────────────┘       └──────────────────────────┘
                    │
                    │
        ┌───────────▼──────────────────┐
        │ SINGLE PASS: Unit Scan       │
        ├───────────────────────────────┤
        │ • Collect minerals            │
        │ • Collect vespene             │
        │ • Collect workers             │
        │ • Assign to nearest start     │
        │ • Label static units:         │
        │   - Hatchery (H1, H2...)      │
        │   - Overlord (OV1, OV2...)    │
        │   - Larva (L1, L2...)         │
        └───────────┬──────────────────┘
                    │
                    │ [Populate WorkerLabelService]
                    │
        ┌───────────▼──────────────────┐
        │ CALCULATE WORKER COM          │
        │ & IDENTIFY W1                 │
        ├───────────────────────────────┤
        │ For si=0:                     │
        │  - Average X,Y of all         │
        │    minerals = COM             │
        │  - Find furthest worker       │
        │    from COM = W1              │
        │  - W1 gets label D1 (or W12)  │
        │  - Remaining workers:         │
        │    greedy chain closest to    │
        │    previous                   │
        │    Get labels W2, W3...W12    │
        └───────────┬──────────────────┘
                    │
                    │ [Populate WorkerLabelService with W1-W12]
                    │
        ┌───────────▼──────────────────┐
        │ REGISTER COM FOR DRAWING      │
        ├───────────────────────────────┤
        │ crosshairService.SetCOM(      │
        │   Point(avgX, avgY, Z=12),    │
        │   label="Start[0]",           │
        │   color=YELLOW                │
        │ );                            │
        │                               │
        │ [Z=12 ensures visibility]     │
        └───────────┬──────────────────┘
                    │
                    │ [Populate CrosshairService registry]
                    │
        ┌───────────▼──────────────────────────┐
        │ ← NEW: GREEDY MINERAL ORDERING        │
        ├───────────────────────────────────────┤
        │ Input:                                │
        │ • minerals[] from multiMainMinerals   │
        │ • W1 position (just identified)       │
        │ • COM position (just calculated)      │
        │                                       │
        │ Phase 1: Find M[0]                    │
        │  ├─ For each mineral:                 │
        │  │  Calculate distance to W1          │
        │  └─ M[0] = maximum distance           │
        │                                       │
        │ Phase 2: Greedy chain M[1-7]          │
        │  ├─ remainingIndices = [1..7]         │
        │  └─ For i=1 to 7:                     │
        │      ├─ Find closest in remaining    │
        │      ├─ M[i] = that mineral          │
        │      └─ Remove from remaining        │
        │                                       │
        │ Phase 3: Classify Near/Far            │
        │  └─ For each mineral:                 │
        │      ├─ dist = distance to COM        │
        │      ├─ avgDist = average of all      │
        │      └─ IsNear = (dist < avgDist)     │
        │                                       │
        │ Result: List<OrderedMineral>[0..7]    │
        │  • Index: 0-7 in greedy order         │
        │  • IsNear: true=N*, false=F*          │
        │  • Position: X,Y coordinates          │
        │  • DistanceFromCOM: for threshold     │
        │  • OriginalIndex: original position   │
        └───────────┬──────────────────────────┘
                    │
                    │ [Populate OrderedMainMinerals]
                    │
        ┌───────────▼──────────────────┐
        │ POPULATE MULTI-LOCATION DATA  │
        ├───────────────────────────────┤
        │ tempBaseDto.MainMinerals      │
        │ tempBaseDto.MainVespene       │
        │ tempBaseDto.MineralCenterOfMS │
        │ tempBaseDto.StartingUnits     │
        │ tempBaseDto.OrderedMainMinera-│  ← NEW
        │ als                           │
        │ tempBaseDto.ExpansionTownhalls│
        │ tempBaseDto.BaseHasBeenPlayed │
        └───────────┬──────────────────┘
                    │
                    │ RETURN tempBaseDto
                    │
                    ▼
        Settings.MapDataLoaded = true


┌─────────────────────────────────────────────────────────────────────┐
│                     EVERY FRAME: ONFRAME()                           │
└─────────────────────────────────────────────────────────────────────┘
                                   │
        ┌──────────────────────────▼───────────────────────┐
        │ BabySharkMiningManager.OnFrame()                 │
        │ (called every game frame)                        │
        ├──────────────────────────────────────────────────┤
        │                                                  │
        │  ┌─────────────────────────────────────────┐    │
        │  │ DrawWorkerLabels()                      │    │
        │  ├─────────────────────────────────────────┤    │
        │  │ • Get workers from observation          │    │
        │  │ • For each worker:                      │    │
        │  │   - label = WorkerLabelService.GetLabel │    │
        │  │   - ManagerDebugService.DrawText(       │    │
        │  │       label, unit.pos+1.5f, color)     │    │
        │  └─────────────────────────────────────────┘    │
        │            ▼                                     │
        │  ┌─────────────────────────────────────────┐    │
        │  │ DrawCenterOfMassLocations()             │    │
        │  ├─────────────────────────────────────────┤    │
        │  │ • allCOMs = CrosshairService.GetAllCOMs │    │
        │  │ • For each COM:                         │    │
        │  │   ├─ DrawLine (H crossbar)              │    │
        │  │   ├─ DrawLine (V crossbar)              │    │
        │  │   └─ DrawSphere (center point)          │    │
        │  │   All at position (x, y, Z=12)          │    │
        │  └─────────────────────────────────────────┘    │
        │            ▼                                     │
        │  ┌─────────────────────────────────────────┐    │
        │  │ [Future: DrawWorkerInstructions()]      │    │
        │  └─────────────────────────────────────────┘    │
        │                                                  │
        └──────────────┬───────────────────────────────────┘
                       │
                       │ All Draw commands accumulated
                       │ in DebugService.DrawRequest
                       │
        ┌──────────────▼───────────────────────────────────┐
        │ DebugManager.OnFrame()                           │
        │ (runs after all managers)                        │
        ├──────────────────────────────────────────────────┤
        │                                                  │
        │ GameConnection.SendRequest(DrawRequest)          │
        │           ▼                                      │
        │     SC2 API                                      │
        │           ▼                                      │
        │     Game Renderer                                │
        │           ▼                                      │
        │  SCREEN OUTPUT:                                  │
        │  • Worker labels at unit positions               │
        │  • Yellow crosshair at Start[0]                  │
        │  • Orange crosshair at Start[1] (opponent)       │
        │                                                  │
        └──────────────────────────────────────────────────┘


┌─────────────────────────────────────────────────────────────────────┐
│                        DATA STRUCTURES                               │
└─────────────────────────────────────────────────────────────────────┘

MawBaseLocationData
├─ MainMinerals: List<List<Vector2Dto>>
│  └─ [0] = unordered minerals at Start[0]
│
├─ OrderedMainMinerals: List<List<OrderedMineral>>  ← NEW
│  └─ [0] = [
│        {Index:0, Position:(x,y), IsNear:false, DistanceFromCOM:25.1, OriginalIndex:5},  M[0]
│        {Index:1, Position:(x,y), IsNear:true,  DistanceFromCOM:14.1, OriginalIndex:3},  M[1]
│        ...
│        {Index:7, Position:(x,y), IsNear:false, DistanceFromCOM:31.6, OriginalIndex:2}   M[7]
│      ]
│
├─ MineralCenterOfMass: List<Vector2Dto>
│  └─ [0] = (avgX, avgY) for Start[0]
│
└─ StartingUnits: List<List<WorkerEntryDto>>
   └─ [0] = [
        {UnitTag:..., Position:..., Label:"D1"},  W1 (furthest)
        {UnitTag:..., Position:..., Label:"W2"},  W2 (closest to W1)
        ...
        {UnitTag:..., Position:..., Label:"W12"}  W12
      ]


┌─────────────────────────────────────────────────────────────────────┐
│                    FUTURE PHASE: WORKER ASSIGNMENT                   │
└─────────────────────────────────────────────────────────────────────┘

Using OrderedMainMinerals for "Just In Time Mining":

Workers            Minerals (Greedy Chain)
W1   ──────────→  M[0] (furthest from W1, far from COM)
W2   ──────────→  M[6] (next far mineral)
W3   ──────────→  M[7] (next far mineral)
W4   ──────────→  M[5] (last far mineral)
                  
W5   ──────────→  M[1] (near mineral, closest to M[0])
W6   ──────────→  M[2] (near mineral, closest to M[1])
W7   ──────────→  M[3] (near mineral, closest to M[2])
W8   ──────────→  M[4] (near mineral, closest to M[3])
W9   ──────────→  [cycle back or support]
W10  ──────────→  [cycle back or support]
W11  ──────────→  [cycle back or support]
W12  ──────────→  [cycle back or support]

Labels created:
F5, F6, F7, F4  (far minerals)
N1, N2, N3, N4  (near minerals)

This ensures:
✓ W1 travels far, other workers start nearby
✓ Harvest times are overlapping (just-in-time)
✓ Workers don't collide at mineral
✓ Optimal density at each mineral node
```

---

## Key Coordination Points

```
SERVICE INSTANTIATION:
  BabySharkBot.cs line 65-71
    WorkerLabelService → BabySharkMiningManager
    CrosshairService   → BabySharkMiningManager

DATA FLOW:
  InitialMapData.GetNewMiningData()
    ├─ Input: game observation, worker positions
    ├─ Phase 1: Identify workers, label static units
    ├─ Phase 2: Calculate COM
    ├─ Phase 3: Register COM (SetCOM for visualization)
    ├─ Phase 4: ← NEW: Greedy ordering (GreedyOrderMinerals)
    └─ Output: tempBaseDto with all populated fields

DRAWING EACH FRAME:
  BabySharkMiningManager.OnFrame()
    ├─ DrawWorkerLabels() → WorkerLabelService.GetLabel()
    ├─ DrawCenterOfMassLocations() → CrosshairService.GetAllCOMs()
    └─ [Accumulate in DebugService.DrawRequest]

    DebugManager.OnFrame()
    └─ GameConnection.SendRequest(DrawRequest)
       └─ Render to screen
```

---

## Quick Navigation

- **Need to trace data?** Start at InitialMapData.GetNewMiningData()
- **Need to trace drawing?** Start at BabySharkMiningManager.OnFrame()
- **Need algorithm details?** Check GreedyOrderMinerals() method
- **Need to add feature?** Follow pattern in BabySharkBot.cs lines 65-71
