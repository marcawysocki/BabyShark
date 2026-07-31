# BabyShark Architecture

## Overview
BabyShark is a StarCraft II bot built on the Sharky framework, focusing on optimized mining and unique unit choreography.

## System Architecture

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
        │ ← GREEDY MINERAL ORDERING            │
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
        │ tempBaseDto.OrderedMainMinera-│
        │ als                           │
        │ tempBaseDto.ExpansionTownhalls│
        │ tempBaseDto.BaseHasBeenPlayed │
        └───────────┬──────────────────┘
                    │
                    │ RETURN tempBaseDto
                    │
                    ▼
        Settings.MapDataLoaded = true
```

## Key Principles
1. **Separation of Concerns:** `InitialMapData` generates data; `BabySharkMiningManager` draws and choreographs.
2. **Persistent State:** Use `WorkerLabelService` and dictionaries to prevent label loss across frames.
3. **Proven Drawing APIs:** Use only Sharky's `DrawText()`, `DrawLine()`, `DrawSphere()` - never invent custom drawing code.
4. **Build-Aware Choreography:** Each worker's role depends on current build, map, and opponent.
5. **Just-In-Time (JIT) Mining:** Use `OrderedMainMinerals` to ensure workers arrive at mineral patches exactly when needed, minimizing idle time and collisions.

## Key Abstractions
- **Managers** (`Managers/`): Stateful orchestrators (e.g., `BabySharkMiningManager`).
- **Services** (`Services/`): Stateless logic handlers (e.g., `JitPrepositionService`, `chrisCrossAppleSause`).
- **MicroTasks** (`MicroTasks/`): Unit-level behaviors (e.g., `TeamPatchMiningTask`, `CustomMiningTask`).
- **Setup** (`Setup/`): Map data, initialization, DTOs (e.g., `InitialMapData`, `BaseDtos.cs`).
