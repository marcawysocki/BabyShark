Version 1.0 Just In Time Mining.MD

Working Directory: C:\Users\marca\source\repos\BabyShark
Do not processes zip files

jit BaseDtos.cs
CcaManager.cs
BabySharkMiningManager.cs

 Only one worker is harvested minerals then returned to the townhall but did not return cargo to the townhall. the other workers just stood around near their target minieral.
ccamanager accomplished the first part of the cycle moving to the target mineral the next part is to harvest the target. next, which T2 seemed to do is move to the townhall when there it needs to give the mineral to the townhall and move to a new target, the A/B switch

This Just In Time Mining (jitMining) version is focused on 12 workers and creating a spawning pool as fast as posible. 3 Workers per 2 Mineral Nodes.  After each worker returns cargo, it changes minerals.  For instance, the first worker T1 returns cargo from TA, T3 was waiting to start harvesting TA and now gets it turn. The returning worker now gets assigned to TB after returning cargo at a jit return cargo point.  The worker T2 that had been harvesting on TB should complete havesting while the T1 harvester is approaching. When workers are holding cargo than need to be sent to a jit-return cargo point that is the shortest path for alternating between minerals A and B.  The cycle repeats for Team 1.

Likewise, each team returns cargo from their assigned mineral nodes and then rotates between their mineral pair.  Every worker alternates between harvesting a "B" Far patch and a "A" Near patch.

Sharky Worker Patch Assignment needs to be replaced with the team method.  Sharky Mining manager needs to be replaced with a JIT Mining Manager.

Sharky Collision Manager might work but may need to be replaced if it can not handle workers going to a different mineral patch.

The Build order system will need a jit Preposition Service that only selects a worker that has just returned cargo from the team that is closest to the build location.  The Mining cycle, in addition to swapping Minerals, also needs to listen for build instructions. In this case, we are trying to set a new world record for a spawning pool.  The pool will be near team 4, next to V2. The last of team 4 (Y1,Y2,Y3) to return cargo and then move to the spawning pool location, Just In Time, for the Mineral total to reach 200.
A Build needs to be created that uses my location for the spawning pool, prepositions the most optimal worker from team 4, Just In Time to build a Spawning Pool. 

Versions beyound 1.0 need to account for a different count of workers and exhasted minerals.  Every variation of mineral node count and worker count has a different pattern.  Anything other than 3 for a team uses traditional Sharky Speed Mining. A worker mines the same mineral node and returns straight back to the townhall
Thinking...
Here's a thinking process that leads to the suggested implementation plan:

1.  **Analyze the Request:**
    *   **Context:** This is a version update for an RTS (Real-Time Strategy) bot, specifically focused on "Just In
Time" (JIT) Mining logic for a StarCraft II (implied by terms like Townhall, Spawning Pool, Minerals, Workers/T2/T1,
Build Order).
    *   **Goal:** Create Version 1.0 of the `JitMining.md` documentation/plan based on provided notes and code file
names (`BaseDtos.cs`, `CcaManager.cs`, `BabySharkMiningManager.cs`).
    *   **Core Mechanics:**
        *   Focus: Speed running (Spawning Pool ASAP).
        *   Worker Allocation: 12 workers total. 3 Workers per 2 Mineral Nodes (Team method).
        *   Rotation Logic: Workers alternate between two patches (A-Near, B-Far) after returning cargo. This prevents
congestion and maximizes throughput.
        *   JIT Return Cargo Point: A specific waypoint logic for dropping off minerals efficiently.
        *   Build Order Integration: Needs a `JitPrepositionService` to select the best worker from Team 4 (closest to
spawn point Y1/Y2/Y3) once the mining goal (200 minerals) is met, to build the Spawning Pool "Just In Time".
        *   Conflict Resolution: Current Sharky logic needs replacement for JIT. Collision manager might need updates.
    *   **Future Versions:** Must acknowledge that >1 worker/node or exhausted minerals require fallback to
traditional "Sharky Speed Mining" (static assignment).

2.  **Deconstruct the Logic Flow:**
    *   *State:* Worker Idle -> Moving to Mine A/B -> Harvesting -> Moving to Townhall/JIT Dropoff -> Dropping Off ->
Receiving New Target (Switch A/B) -> Repeat.
    *   *The "Team" Concept:* Workers are grouped by proximity to specific mineral pairs. Team 1, Team 4, etc.
    *   *The Switch:* T1 returns from TA. T3 waits at TA. T1 goes to TB. T2 finishes TB.
    *   *Build Trigger:* When `TotalMinerals >= 200`. Select worker from Team 4 (closest to build site). Move to build
site. Build Pool.

3.  **Identify Code Structure & Refactoring Needs:**
    *   `BabySharkMiningManager.cs`: This is the core brain. Needs to switch from static assignment to JIT Rotation.
    *   `CcaManager.cs`: Likely handles Command/Control/Actions. Needs to integrate with the Build Order logic for the
Spawning Pool trigger.
    *   `BaseDtos.cs`: Needs data structures to represent Workers, Mineral Patches (A/B), and Teams.
    *   `Collision Manager`: Mentioned as potentially needing replacement if pathing fails between distant patches.

4.  **Drafting the Documentation (`JitMining.md`):**
    *   *Header:* Version info, Directory, Files.
    *   *Overview:* What is JIT Mining? (Efficiency, Rotation).
    *   *Architecture:* Classes involved.
    *   *Worker Lifecycle:* Step-by-step flow (Mine A -> Return -> Mine B).
    *   *Team Logic:* How 3 workers map to 2 patches.
    *   *Build Order Integration:* The specific requirement for the Spawning Pool using Team 4.
    *   *Constraints/Future Work:* When to fallback to Sharky logic.

5.  **Refining the Technical Details (Mental Simulation):**
    *   *The "JIT Return Cargo Point":* This implies a drop-off zone that isn't necessarily right next to the Townhall
if it saves travel time, but usually, in SC2 bots, it's the Townhall or a specific garrison. The prompt says "returned
cargo to the townhall". Let's assume standard return logic but with a state machine flag indicating readiness for
rotation.
    *   *The "A/B Switch":* This is the key optimization. Instead of one worker staying on Patch A forever, they swap.
Why? Because usually, one patch empties faster or congestion occurs near the Townhall. By alternating, you balance
load.
    *   *Team 4 Specifics:* The prompt says "Pool will be near team 4... Last of team 4 (Y1,Y2,Y3) to return cargo...
move to spawning pool". This is a specific build order trigger.

6.  **Addressing the "Do not process zip files" instruction:**
    *   I must ensure I don't try to access or reference any zip archives. The user explicitly warned against this. I
will stick to text/code generation.

7.  **Structuring the Output:**
    *   Since the user provided a snippet titled "Version 1.0 Just In Time Mining.MD", they likely want me to generate
the full content of that Markdown file based on their notes, potentially expanding on the technical implementation
details to make it actionable for a developer.
    *   I should organize it into clear sections: Architecture, Data Models, Logic Flow, Integration Points.

8.  **Self-Correction/Review during drafting:**
    *   *Correction:* The prompt mentions `Sharky Worker Patch Assignment needs to be replaced`. I need to make sure
the new design explicitly removes reliance on permanent patch assignment.
    *   *Clarification:* "JIT Return Cargo Point". Is this a physical point or a logical state? In SC2 pathfinding,
"Return Cargo" usually targets the Townhall. The JIT aspect implies that once returned, the *next* command is issued
immediately without waiting, and the target switches.
    *   *Build Order:* Needs to be an event listener inside `BabySharkMiningManager`.

9.  **Final Polish:** Ensure code file names match exactly (`BaseDtos.cs`, etc.). Make sure the logic regarding "3
Workers per 2 Mineral Nodes" is clear (1.5 workers per patch average, rotating).

**(Self-Correction on File Names):** The prompt lists `jit BaseDtos.cs CcaManager.cs BabySharkMiningManager.cs`. I
should treat these as the primary files to modify/create.

Let's construct the Markdown content.

*   **Title:** Version 1.0 Just In Time Mining
*   **Scope:** Spawning Pool Speed Run (200 Minerals).
*   **Key Change:** Dynamic Worker Rotation vs Static Assignment.
*   **Classes:** Update descriptions for each file.
*   **Logic Diagram:** Describe the cycle.
*   **Edge Cases:** Fallback to Sharky logic if conditions aren't met (as per Version 2.0 note).

Let's write it.cw
...done thinking.

# Version 1.0 Just In Time Mining.MD

**Working Directory:** `C:\Users\marca\source\repos\BabyShark`
**Constraint:** Do not process zip files.

## 1. Overview
This document outlines the architecture and logic for **Just In Time (JIT) Mining v1.0**. The primary objective is to
optimize mineral gathering throughput to achieve a Spawning Pool build order as fast as possible (Target: 200
Minerals).

Unlike traditional static assignment (Sharky Speed Mining), JIT Mining utilizes dynamic rotation. Workers alternate
between two assigned mineral patches (A-Near and B-Far) within their team's sector after every cargo return. This
minimizes congestion at the Townhall and balances load across depleted nodes.

## 2. Core Components
The following files require modification or creation to support this logic:

1.  **`BaseDtos.cs`**: Defines data structures for Workers, Mineral Patches (A/B), Teams, and Build Orders.
2.  **`CcaManager.cs`**: Handles the high-level command execution, specifically integrating the JIT Preposition
Service for build triggers.
3.  **`BabySharkMiningManager.cs`**: The primary logic controller. Replaces `SharkyMiningManager`. Manages the worker
lifecycle state machine (Harvest -> Return -> Switch).

## 3. Architecture & Data Models

### 3.1 Worker Team Assignment
Workers are grouped by proximity to mineral pairs.
*   **Team Count:** Variable (Default v1.0: 4 Teams for 12 Workers).
*   **Allocation:** 3 Workers per 2 Mineral Nodes.
*   **Structure:**
    *   `MiningTeam`: Contains a list of assigned `MineralPatch` (Near/A and Far/B) and active `Worker`.
    *   `JitCycleState`: Enum (`Idle`, `HarvestingA`, `ReturningToTownhall`, `SwitchingToB`, `Building`).

### 3.2 BaseDtos Updates
```csharp
public class JitWorkerState
{
    public int WorkerId { get; set; }
    public MineralPatch CurrentTarget { get; set; } // Patch A or B
    public bool HasCargo { get; set; }
    public Vector3 JITDropOffPoint { get; set; } // Optimized return path point
}

public class MiningTeam
{
    public int TeamId { get; set; }
    public List<MineralPatch> AssignedPatches { get; set; } // Must be pair (A, B)
    public List<int> WorkerIds { get; set; }
}
```

## 4. Mining Logic Flow (The JIT Cycle)

### 4.1 The Rotation Strategy
Every team operates on a 2-Patch cycle to ensure continuous flow.
*   **Patch A:** Near the Townhall or primary pathing lane.
*   **Patch B:** Further away, or second node in the cluster.

**Cycle Sequence:**
1.  **Harvest (T1):** Worker arrives at Patch A, harvests full cargo.
2.  **Return:** Worker moves to JIT Return Cargo Point (Townhall).
3.  **Dropoff:** Cargo delivered to Townhall.
4.  **Switch:** `CurrentTarget` changes from A -> B immediately upon return confirmation.
5.  **Harvest (T1):** Worker arrives at Patch B, harvests full cargo.
6.  **Return:** Worker moves to JIT Return Cargo Point.
7.  **Loop:** Repeat until Build Order triggers or minerals exhausted.

**Visual Representation of Team Cycle:**
```text
[Worker T3] Waiting at TA (Buffer)
    |
    v
[Worker T1] Returns from TA -> Drops Cargo -> Moves to TB
    |                               ^
    |                               |
[Worker T2] Harvests TB            [Next Return]
    |
    v
[Worker T1] Arrives at TB (T2 finishes, T1 takes over)
```

### 4.2 Collision & Pathing
*   **Sharky Collision Manager:** Currently assumed functional for standard movement.
*   **Modification:** Must validate that workers moving between Patch A and B do not deadlock with other teams. If
pathfinding fails to the secondary patch, fallback to nearest available mineral (Traditional Sharky logic).

## 5. Build Order Integration: JIT Preposition Service
This is specific to Version 1.0 Speed Run (Spawning Pool @ Team 4 Location).

### 5.1 Trigger Condition
*   `TotalMinerals >= 200` AND `CurrentBuildOrder == SpawningPool`.
*   Target Location: Near Team 4 Mineral Nodes (V2 Area).

### 5.2 Preposition Service (`CcaManager.cs`)
When the trigger fires, `BabySharkMiningManager` must pause JIT rotation for **one specific worker** from **Team 4**.

1.  **Identify Candidates:** Select all Workers in Team 4 currently holding cargo or returning to Townhall.
2.  **Select Optimal Worker:** Calculate distance from current position (or predicted arrival) to the Spawning Pool
Build Location (`SpawnPoint`).
3.  **Interrupt Cycle:**
    *   If worker is harvesting: Stop immediately, move to build site.
    *   If worker is returning: Drop cargo at nearest Townhall waypoint (if possible without delay), then move to
build site.
4.  **Execute Build:** Command worker to construct Spawning Pool.

### 5.3 Team 4 Specifics (Y1, Y2, Y3)
*   The last worker of the specific set (Y1/Y2/Y3) from Team 4 to return cargo is prioritized for the build task if
they are closest. This ensures the Spawning Pool is placed "Just In Time" without delaying the mineral income more
than necessary.

## 6. Implementation Plan

### Phase 1: Data Structure Refactor (`BaseDtos.cs`)
*   Create `MiningTeam` class.
*   Add `PatchPair` logic (A/B).
*   Remove static `WorkerPatchAssignment`.

### Phase 2: State Machine Logic (`BabySharkMiningManager.cs`)
*   Implement the `Harvest -> Return -> Switch` loop.
*   Create method `OnCargoDropped(Worker w)`: This is where the switch happens.
    *   If `w.Patch == A`, set `w.Target = B`.
    *   If `w.Patch == B`, set `w.Target = A`.

### Phase 3: Build Trigger (`CcaManager.cs`)
*   Implement `CheckBuildOrderTrigger()`.
*   Integrate with `MiningManager.GetBestWorkerForConstruction(TeamId)`.

## 7. Constraints & Fallback (Future Versions)
**Version 2.0+ Requirements:**
This v1.0 logic is hard-coded for **12 Workers / Standard Patches**.
*   **Condition:** If `TotalWorkers != 12` OR `MineralNodes < 4`.
*   **Fallback:** Revert to **Traditional Sharky Speed Mining** (Static Assignment: Worker stays on Patch X until
exhausted).
*   **Exhaustion:** v1.0 does not handle moving teams when minerals are fully depleted. This will be addressed in
future versions.

## 8. Testing Checklist
- [ ] Verify T1, T2, T3 rotation logic prevents stacking at Townhall.
- [ ] Verify "Switch" occurs immediately after cargo drop (no idle time).
- [ ] Verify Spawning Pool build command interrupts the mining cycle correctly for Team 4.
- [ ] Ensure workers do not pathfind into invalid mineral patches if A/B switch fails.

---
**Status:** Ready for Implementation
**Author:** BabyShark Dev Team
**Date:** 2023-10-XX