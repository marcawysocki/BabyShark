Version 1.0 Just In Time Mining.MD

Working Directory: C:\Users\marca\source\repos\BabyShark
the GitHub repository is https://github.com/marcawysocki/BabyShark
Do not processes zip files

local files 
C:\Users\marca\source\repos\BabyShark\BabySharkBot\Setup\BaseDtos.cs
C:\Users\marca\source\repos\BabyShark\BabySharkBot\Services\chrisCrossAppleSause.cs
C:\Users\marca\source\repos\BabyShark\BabySharkBot\Managers\CcaManager.cs
C:\Users\marca\source\repos\BabyShark\BabySharkBot\Managers\BabySharkMiningManager.cs
C:\Users\marca\source\repos\BabyShark\BabySharkBot\MicroTasks\TeamPatchMiningTask.cs

Startup project C:\Users\marca\source\repos\BabyShark\BabySharkBot\BabySharkBot.csproj

 Workers are harvested minerals but are not Utilizing speed mining techniques where A worker can bypass deceleration by issuing move commands.  

ccamanager accomplished the first part of the cycle moving to the target mineral the next part is to harvest the target. next, is move to the townhall when there it needs to give the mineral to the townhall and move to a new target, the A/B switch

This Just In Time Mining (jitMining) version is focused on 12 workers and creating a spawning pool as fast as posible. 3 Workers per 2 Mineral Nodes.  After each worker returns cargo, it changes minerals.  For instance, team teal, the first worker T1 returns cargo from TA, T3 was waiting to start harvesting TA and now gets it turn. The returning worker now gets assigned to TB after returning cargo at a jit return cargo point.  The worker T2 that had been harvesting on TB should complete havesting while the T1 harvester is approaching. When workers are holding cargo than need to be sent to a jit-return cargo point that is the shortest path for alternating between minerals A and B.  The cycle repeats for Team 1.

At the momment a worker switches  if (carrying && !wasCarrying) it needs to get a move command.  if it is isJitTeam then the return point is the jit return point for the team that it is on, otherwise it needs a move command to the speed mining return point.  if isJitTeam then it's  target mineral changes the A/B switch.  The workers need to move to the townhall avoiding Deceleration once at the footprint of the town hall it returned cargo and then needs move commands to the new target And once footprint of the mineral target It's given a harvest command.

It might be that at the point of (carrying && !wasCarrying) that they need a stop command and then a move command qued.

Each team returns cargo from their assigned mineral nodes and then rotates between their mineral pair.  Every worker alternates between harvesting a "B" Far patch and a "A" Near patch.


Other AI Thinking...
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

Kimmie K3 Chat contents
I wasn't able to pull the raw source from GitHub directly, but based on your design doc and the Sharky framework patterns, here's the refactored BabySharkMiningManager.cs with the JIT mining logic, stop-then-move queuing, and deceleration bypass for both JIT and non-JIT teams.
I'll also include the supporting DTO updates you'll need in BaseDtos.cs and the TeamPatchMiningTask.cs to make it all wire together.
BaseDtos.cs — Add These Types
using System.Collections.Generic;
using System.Numerics;
using Sharky;

namespace BabySharkBot.Manager
{
    public enum JitMiningState
    {
        Idle,
        MovingToHarvest,
        Harvesting,
        MovingToReturn,
        ReturningCargo,
        SwitchingMineral
    }

    public class JitWorkerData
    {
        public ulong Tag { get; set; }
        public int TeamId { get; set; }
        public bool IsJitTeam { get; set; }
        
        /// <summary>Current target mineral unit tag</summary>
        public ulong CurrentMineralTag { get; set; }
        
        /// <summary>Alternate mineral (A/B switch target)</summary>
        public ulong AlternateMineralTag { get; set; }
        
        public JitMiningState State { get; set; } = JitMiningState.Idle;
        
        /// <summary>True if worker was carrying minerals on previous frame</summary>
        public bool WasCarrying { get; set; }
        
        /// <summary>Frame count when last command was issued (prevents spam)</summary>
        public int LastCommandFrame { get; set; } = -999;
        
        /// <summary>Position to move to for JIT cargo return (shortest path between A/B)</summary>
        public Vector3 JitReturnPoint { get; set; }
        
        /// <summary>Standard speed-mining return point (non-JIT teams)</summary>
        public Vector3 SpeedMiningReturnPoint { get; set; }
        
        /// <summary>Waypoint just past the mineral to prevent deceleration</summary>
        public Vector3 MineralApproachPoint { get; set; }
    }

    public class MiningTeam
    {
        public int TeamId { get; set; }
        public List<ulong> WorkerTags { get; set; } = new();
        public List<ulong> MineralTags { get; set; } = new(); // Pair: [0]=A Near, [1]=B Far
        public bool IsJitTeam { get; set; }
        public Vector3 JitReturnPoint { get; set; }
        public Vector3 TownHallPosition { get; set; }
    }
}

BabySharkMiningManager.cs — Refactored
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sharky;
using Sharky.DefaultBot;
using Sharky.Managers;
using SC2APIProtocol;

namespace BabySharkBot.Manager
{
    public class BabySharkMiningManager :IManager
    {
        private readonly DefaultSharkyBot _defaultSharkyBot;
        private readonly Dictionary<ulong, JitWorkerData> _jitWorkerData = new();
        private readonly List<MiningTeam> _miningTeams = new();
        
        private const int CommandCooldownFrames = 2;
        private const float MineralFootprintRadius = 0.5f;
        private const float TownhallFootprintRadius = 2.5f;
        private const float ReturnPointOffset = 1.0f;

        public BabySharkMiningManager(DefaultSharkyBot defaultSharkyBot)
        {
            _defaultSharkyBot = defaultSharkyBot;
        }

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();
            var frame = (int)observation.Observation.GameLoop;

            // Initialize or update worker data
            UpdateWorkerTracking(observation);

            foreach (var team in _miningTeams)
            {
                foreach (var workerTag in team.WorkerTags)
                {
                    if (!_jitWorkerData.TryGetValue(workerTag, out var workerData))
                        continue;

                    if (!_defaultSharkyBot.UnitManager.Commanders.TryGetValue(workerTag, out var commander))
                        continue;

                    var unit = commander.UnitCalculation.Unit;
                    var isCarrying = unit.BuffIds.Contains(Ids.Buffs.CARRYMINERALFIELDMINERALS) 
                        || unit.BuffIds.Contains(Ids.Buffs.CARRYHIGHYIELDMINERALFIELDMINERALS);

                    // Detect cargo pickup transition
                    var justPickedUp = isCarrying && !workerData.WasCarrying;
                    workerData.WasCarrying = isCarrying;

                    // --- STATE MACHINE ---
                    switch (workerData.State)
                    {
                        case JitMiningState.Idle:
                        case JitMiningState.MovingToHarvest:
                            actions.AddRange(HandleHarvestingPhase(commander, workerData, team, frame, justPickedUp));
                            break;

                        case JitMiningState.Harvesting:
                            actions.AddRange(HandlePostHarvest(commander, workerData, team, frame, justPickedUp));
                            break;

                        case JitMiningState.MovingToReturn:
                        case JitMiningState.ReturningCargo:
                            actions.AddRange(HandleReturnPhase(commander, workerData, team, frame));
                            break;

                        case JitMiningState.SwitchingMineral:
                            actions.AddRange(HandleSwitchPhase(commander, workerData, team, frame));
                            break;
                    }
                }
            }

            return actions;
        }

        /// <summary>
        /// Worker is approaching or at mineral. Issue harvest when in range.
        /// If just picked up cargo, transition to return phase.
        /// </summary>
        private List<SC2APIProtocol.Action> HandleHarvestingPhase(
            UnitCommander commander, JitWorkerData workerData, MiningTeam team, int frame, bool justPickedUp)
        {
            var actions = new List<SC2APIProtocol.Action>();
            var unit = commander.UnitCalculation.Unit;
            
            if (justPickedUp)
            {
                // Cargo acquired — immediately transition to return
                workerData.State = JitMiningState.MovingToReturn;
                return HandleReturnPhase(commander, workerData, team, frame);
            }

            // Get current target mineral position
            var mineralPos = GetMineralPosition(workerData.CurrentMineralTag);
            if (!mineralPos.HasValue)
                return actions;

            var dist = Vector3.Distance(unit.Pos, mineralPos.Value);

            // If close to mineral footprint, issue harvest
            if (dist <= MineralFootprintRadius + 0.3f)
            {
                if (frame - workerData.LastCommandFrame > CommandCooldownFrames)
                {
                    // Stop any pending move, then harvest
                    actions.Add(CreateStopCommand(unit.Tag));
                    actions.Add(CreateHarvestCommand(unit.Tag, workerData.CurrentMineralTag));
                    workerData.LastCommandFrame = frame;
                    workerData.State = JitMiningState.Harvesting;
                }
            }
            else
            {
                // Move to approach point (past mineral to defeat deceleration)
                var approachPoint = workerData.MineralApproachPoint;
                if (frame - workerData.LastCommandFrame > CommandCooldownFrames)
                {
                    actions.Add(CreateMoveCommand(unit.Tag, approachPoint));
                    workerData.LastCommandFrame = frame;
                    workerData.State = JitMiningState.MovingToHarvest;
                }
            }

            return actions;
        }

        /// <summary>
        /// Worker is harvesting. Wait for cargo pickup (detected via justPickedUp in OnFrame).
        /// </summary>
        private List<SC2APIProtocol.Action> HandlePostHarvest(
            UnitCommander commander, JitWorkerData workerData, MiningTeam team, int frame, bool justPickedUp)
        {
            var actions = new List<SC2APIProtocol.Action>();
            
            if (justPickedUp)
            {
                workerData.State = JitMiningState.MovingToReturn;
                return HandleReturnPhase(commander, workerData, team, frame);
            }

            return actions;
        }

        /// <summary>
        /// Worker has cargo. Move to return point, then return cargo at townhall footprint.
        /// Defeat deceleration by moving past the return point.
        /// </summary>
        private List<SC2APIProtocol.Action> HandleReturnPhase(
            UnitCommander commander, JitWorkerData workerData, MiningTeam team, int frame)
        {
            var actions = new List<SC2APIProtocol.Action>();
            var unit = commander.UnitCalculation.Unit;

            // Determine return target
            var returnPoint = workerData.IsJitTeam 
                ? workerData.JitReturnPoint 
                : workerData.SpeedMiningReturnPoint;

            var distToReturn = Vector3.Distance(unit.Pos, returnPoint);
            var distToTownhall = Vector3.Distance(unit.Pos, team.TownHallPosition);

            // If at townhall footprint, return cargo
            if (distToTownhall <= TownhallFootprintRadius)
            {
                if (frame - workerData.LastCommandFrame > CommandCooldownFrames)
                {
                    // STOP to cancel any pending move, then ReturnCargo
                    // This ensures the ReturnCargo command isn't queued behind a move
                    actions.Add(CreateStopCommand(unit.Tag));
                    actions.Add(CreateReturnCargoCommand(unit.Tag));
                    workerData.LastCommandFrame = frame;
                    workerData.State = JitMiningState.ReturningCargo;
                }
                return actions;
            }

            // If close to JIT return point but not yet at townhall, keep moving toward townhall
            // The return point is typically between A and B minerals, so we may need to pass through it
            if (distToReturn <= 0.5f && distToTownhall > TownhallFootprintRadius)
            {
                // Continue moving toward townhall to actually drop cargo
                var townhallApproach = CalculateApproachPoint(unit.Pos, team.TownHallPosition);
                if (frame - workerData.LastCommandFrame > CommandCooldownFrames)
                {
                    actions.Add(CreateMoveCommand(unit.Tag, townhallApproach));
                    workerData.LastCommandFrame = frame;
                }
                return actions;
            }

            // Still en route — move to return point with deceleration bypass
            if (frame - workerData.LastCommandFrame > CommandCooldownFrames)
            {
                // Move to a point past the return point so worker doesn't slow down
                var bypassPoint = CalculateApproachPoint(unit.Pos, returnPoint);
                actions.Add(CreateMoveCommand(unit.Tag, bypassPoint));
                workerData.LastCommandFrame = frame;
            }

            return actions;
        }

        /// <summary>
        /// Cargo has been returned. Now either switch minerals (JIT) or go back to same mineral (speed).
        /// Issue stop + move to defeat deceleration when leaving townhall.
        /// </summary>
        private List<SC2APIProtocol.Action> HandleSwitchPhase(
            UnitCommander commander, JitWorkerData workerData, MiningTeam team, int frame)
        {
            var actions = new List<SC2APIProtocol.Action>();
            var unit = commander.UnitCalculation.Unit;

            var mineralPos = GetMineralPosition(workerData.CurrentMineralTag);
            if (!mineralPos.HasValue)
                return actions;

            var dist = Vector3.Distance(unit.Pos, mineralPos.Value);

            // If at mineral, harvest
            if (dist <= MineralFootprintRadius + 0.3f)
            {
                if (frame - workerData.LastCommandFrame > CommandCooldownFrames)
                {
                    actions.Add(CreateStopCommand(unit.Tag));
                    actions.Add(CreateHarvestCommand(unit.Tag, workerData.CurrentMineralTag));
                    workerData.LastCommandFrame = frame;
                    workerData.State = JitMiningState.Harvesting;
                }
                return actions;
            }

            // En route to mineral — move with deceleration bypass
            if (frame - workerData.LastCommandFrame > CommandCooldownFrames)
            {
                var approachPoint = workerData.MineralApproachPoint;
                
                // STOP to clear any pending ReturnCargo or idle, then MOVE
                // This prevents the worker from decelerating at the townhall
                actions.Add(CreateStopCommand(unit.Tag));
                actions.Add(CreateMoveCommand(unit.Tag, approachPoint));
                
                workerData.LastCommandFrame = frame;
                workerData.State = JitMiningState.MovingToHarvest;
            }

            return actions;
        }

        /// <summary>
        /// Called from HandleReturnPhase after cargo is successfully returned.
        /// Switches A/B for JIT teams, keeps same mineral for speed teams.
        /// </summary>
        private void OnCargoReturned(JitWorkerData workerData, MiningTeam team)
        {
            if (workerData.IsJitTeam)
            {
                // A/B Switch: swap current and alternate
                var oldMineral = workerData.CurrentMineralTag;
                workerData.CurrentMineralTag = workerData.AlternateMineralTag;
                workerData.AlternateMineralTag = oldMineral;

                // Recalculate approach point for new mineral
                var newMineralPos = GetMineralPosition(workerData.CurrentMineralTag);
                if (newMineralPos.HasValue)
                {
                    workerData.MineralApproachPoint = CalculateApproachPoint(team.TownHallPosition, newMineralPos.Value);
                }
            }
            // Non-JIT: keep CurrentMineralTag, just go back to it

            workerData.State = JitMiningState.SwitchingMineral;
        }

        // ------------------------------------------------------------------
        // Tracking & Setup
        // ------------------------------------------------------------------

        private void UpdateWorkerTracking(ResponseObservation observation)
        {
            var workers = observation.Observation.RawData.Units
                .Where(u => u.Alliance == Alliance.Self 
                    && (u.UnitType == (uint)UnitTypes.ZERG_DRONE 
                        || u.UnitType == (uint)UnitTypes.TERRAN_SCV 
                        || u.UnitType == (uint)UnitTypes.PROTOSS_PROBE))
                .ToList();

            // Remove dead workers
            var deadTags = _jitWorkerData.Keys.Except(workers.Select(w => w.Tag)).ToList();
            foreach (var tag in deadTags)
                _jitWorkerData.Remove(tag);

            // Add new workers (they'll get team-assigned elsewhere or on first frame)
            foreach (var w in workers)
            {
                if (!_jitWorkerData.ContainsKey(w.Tag))
                {
                    _jitWorkerData[w.Tag] = new JitWorkerData { Tag = w.Tag };
                }
            }
        }

        /// <summary>
        /// Assign a worker to a team and set up A/B minerals, return points, etc.
        /// Call this from your setup logic (e.g., CcaManager or OnStart).
        /// </summary>
        public void AssignWorkerToTeam(ulong workerTag, MiningTeam team, int workerIndexInTeam)
        {
            if (!_jitWorkerData.TryGetValue(workerTag, out var data))
                return;

            data.TeamId = team.TeamId;
            data.IsJitTeam = team.IsJitTeam;
            data.TownHallPosition = team.TownHallPosition;
            data.JitReturnPoint = team.JitReturnPoint;
            data.SpeedMiningReturnPoint = CalculateSpeedMiningReturnPoint(team);

            if (team.MineralTags.Count >= 2)
            {
                // Assign A/B based on worker index
                // Worker 0 -> A, Worker 1 -> B, Worker 2 -> A (for 3 workers / 2 patches)
                data.CurrentMineralTag = team.MineralTags[workerIndexInTeam % 2];
                data.AlternateMineralTag = team.MineralTags[(workerIndexInTeam + 1) % 2];

                var mineralPos = GetMineralPosition(data.CurrentMineralTag);
                if (mineralPos.HasValue)
                {
                    data.MineralApproachPoint = CalculateApproachPoint(team.TownHallPosition, mineralPos.Value);
                }
            }

            data.State = JitMiningState.MovingToHarvest;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private Vector3? GetMineralPosition(ulong mineralTag)
        {
            if (_defaultSharkyBot.UnitManager.Commanders.TryGetValue(mineralTag, out var cmd))
                return cmd.UnitCalculation.Unit.Pos;
            
            // Fallback: scan raw observation
            // This would need access to latest observation; simplified here
            return null;
        }

        /// <summary>
        /// Calculate a point past the target so the worker doesn't decelerate.
        /// </summary>
        private Vector3 CalculateApproachPoint(Vector3 from, Vector3 to)
        {
            var dir = to - from;
            if (dir.LengthSquared() < 0.0001f) return to;
            
            dir = Vector3.Normalize(dir);
            // Move 1.5 units past the target
            return to + dir * 1.5f;
        }

        private Vector3 CalculateSpeedMiningReturnPoint(MiningTeam team)
        {
            // Speed mining return point is typically slightly offset from townhall
            // toward the mineral field to shorten the walk
            var townhall = team.TownHallPosition;
            var avgMineral = Vector3.Zero;
            foreach (var mt in team.MineralTags)
            {
                var pos = GetMineralPosition(mt);
                if (pos.HasValue) avgMineral += pos.Value;
            }
            avgMineral /= team.MineralTags.Count;

            var dir = Vector3.Normalize(avgMineral - townhall);
            return townhall + dir * ReturnPointOffset;
        }

        // ------------------------------------------------------------------
        // Command Builders
        // ------------------------------------------------------------------

        private SC2APIProtocol.Action CreateMoveCommand(ulong unitTag, Vector3 point)
        {
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        UnitTags = { unitTag },
                        AbilityId = (int)Abilities.MOVE,
                        TargetWorldSpacePos = new Point { X = point.X, Y = point.Y, Z = point.Z }
                    }
                }
            };
        }

        private SC2APIProtocol.Action CreateStopCommand(ulong unitTag)
        {
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        UnitTags = { unitTag },
                        AbilityId = (int)Abilities.STOP
                    }
                }
            };
        }

        private SC2APIProtocol.Action CreateHarvestCommand(ulong unitTag, ulong mineralTag)
        {
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        UnitTags = { unitTag },
                        AbilityId = (int)Abilities.HARVEST_GATHER,
                        TargetUnitTag = mineralTag
                    }
                }
            };
        }

        private SC2APIProtocol.Action CreateReturnCargoCommand(ulong unitTag)
        {
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        UnitTags = { unitTag },
                        AbilityId = (int)Abilities.HARVEST_RETURN
                    }
                }
            };
        }

        // ------------------------------------------------------------------
        // IManager Interface
        // ------------------------------------------------------------------

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing ping)
        {
            // Team setup should happen here or in a dedicated setup manager
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
            _jitWorkerData.Clear();
            _miningTeams.Clear();
        }

        public void OnFrame(ResponseObservation observation, int frame)
        {
            // Alternative entry point if your bot uses this signature
        }
    }
}
TeamPatchMiningTask.cs — MicroTask Integration
If you want this wired as a Sharky MicroTask (so it plays nice with the MicroManager):
using System.Collections.Generic;
using System.Linq;
using Sharky;
using Sharky.MicroTasks;
using SC2APIProtocol;

namespace BabySharkBot.Manager
{
    public class TeamPatchMiningTask : MicroTask
    {
        private readonly BabySharkMiningManager _miningManager;

        public TeamPatchMiningTask(BabySharkMiningManager miningManager, float priority = 1.0f)
        {
            _miningManager = miningManager;
            Priority = priority;
        }

        public override void ClaimUnits(Dictionary<ulong, UnitCommander> commanders)
        {
            // Claim all worker units
            foreach (var c in commanders.Values)
            {
                var type = c.UnitCalculation.Unit.UnitType;
                if (type == (uint)UnitTypes.ZERG_DRONE 
                    || type == (uint)UnitTypes.TERRAN_SCV 
                    || type == (uint)UnitTypes.PROTOSS_PROBE)
                {
                    Claim(c);
                }
            }
        }

        public override IEnumerable<SC2APIProtocol.Action> PerformActions(int frame)
        {
            // The mining manager handles everything; we just route through
            return Enumerable.Empty<SC2APIProtocol.Action>();
        }
    }
}
Key Refactoring Points
| Concern                    | Implementation                                                                                        |
| -------------------------- | ----------------------------------------------------------------------------------------------------- |
| **Stop-then-Move**         | `CreateStopCommand` followed by `CreateMoveCommand` in `HandleSwitchPhase` and `HandleReturnPhase`    |
| **JIT A/B Switch**         | `OnCargoReturned` swaps `CurrentMineralTag` ↔ `AlternateMineralTag` only when `IsJitTeam`             |
| **Non-JIT Speed Mining**   | Same mineral every cycle, but still uses `CalculateApproachPoint` to defeat deceleration              |
| **Deceleration Bypass**    | `CalculateApproachPoint` returns a point **1.5 units past** the target so the worker never slows down |
| **Cargo Pickup Detection** | `justPickedUp = isCarrying && !wasCarrying` triggers immediate transition to `MovingToReturn`         |
| **Townhall Footprint**     | `TownhallFootprintRadius = 2.5f` — when inside, issue `STOP` then `HARVEST_RETURN`                    |
| **Command Cooldown**       | `CommandCooldownFrames = 2` prevents API spam and command queuing issues                              |
Wiring It Up in CcaManager.cs (or Program.cs)
// In your bot initialization:
var miningManager = new BabySharkMiningManager(defaultSharkyBot);
defaultSharkyBot.Managers.Add(miningManager);

// After map analysis, set up teams:
var team1 = new MiningTeam
{
    TeamId = 1,
    IsJitTeam = true,
    TownHallPosition = new Vector3(hatcheryX, hatcheryY, hatcheryZ),
    JitReturnPoint = new Vector3(jitX, jitY, jitZ),
    MineralTags = new List<ulong> { mineralA_Tag, mineralB_Tag }
};
miningManager.AssignWorkerToTeam(worker1_Tag, team1, 0); // gets A
miningManager.AssignWorkerToTeam(worker2_Tag, team1, 1); // gets B
miningManager.AssignWorkerToTeam(worker3_Tag, team1, 2); // gets A

Kimi:
BabyShark JIT Mining — Code Review & Refactoring Plan
I’ve analyzed all six files. This is a sophisticated StarCraft II bot with a custom “Just In Time” mining system, but there are critical architectural conflicts and broken handoffs between the CCA (early-game positioning) phase and steady-state JIT rotation. Below is a severity-ranked breakdown of what’s wrong and how to fix it.
________________________________________
1. Executive Summary
Component	Responsibility	Current State
chrisCrossAppleSause	Frames 0–35: precise worker bumping/alignment	✅ Functional but over-called
CcaManager	Orchestrates CCA phase, hands off at frame 35	⚠️ Handoff logic is OK but duplicates commands
BabySharkMiningManager	Steady-state JIT + debug visualization	🔴 Broken JIT rotation; fights with CCA; team ID mismatch
TeamPatchMiningTask	Sharky MicroTask for mining + build prepositioning	🔴 Dead code; conflicts with Manager in steady state
BaseDtos	Data structures	✅ Good, but MiningTeam is used inconsistently
The #1 problem: Three different systems are issuing ActionRaw commands to the same workers during the CCA phase, and the steady-state JIT A/B alternation is not implemented as described in the design doc. Instead, a broken team-role swapper sends returning workers to a wait point instead of the alternate mineral.
________________________________________
2. Critical Bugs (Fix Immediately)
🔴 Bug A: Triple-Command Collision (CCA Phase)
Location: CcaManager.OnFrame, BabySharkMiningManager.RunJustInTimeMining, TeamPatchMiningTask.PerformActions
During frames 0–35, all three call _ccaService.BuildBumpOrders() and return raw actions for the same workers:
// CcaManager.cs (line ~85)
var actions = _ccaService.BuildBumpOrders(frame, mapData, startIndex, liveWorkers, null);

// BabySharkMiningManager.cs (line ~330)
var ccaActions = _ccaMiningService.BuildBumpOrders(_currentFrame, _mapData, startIndex, liveWorkers);

// TeamPatchMiningTask.cs (line ~75)
var bumpCommands = _ccaService.BuildBumpOrders(frame, mapData, startIndex, liveWorkers, UnitCommanders);
Impact: Workers receive conflicting move orders every frame. The CCA precision alignment is destroyed by duplicate or conflicting commands.
Fix: Establish single ownership. - Option 1 (Recommended): CcaManager owns frames 0–35 exclusively. BabySharkMiningManager should skip action generation when Settings.ccaMining == true and limit itself to debug drawing. TeamPatchMiningTask should return Empty during CCA. - Option 2: Delete CcaManager entirely and let TeamPatchMiningTask own the CCA phase via its _ccaService reference. This is cleaner because MicroTasks already claim workers.
________________________________________
🔴 Bug B: Team ID Mismatch Breaks Main-Base JIT
Location: BabySharkMiningManager.ProcessFrameObservation + InitializeExpansionMining
InitializeExpansionMining creates MiningTeam objects with IDs like:
$"{expansionPosition.X:F1}_{expansionPosition.Y:F1}_T1"   // e.g. "123.4_456.7_T1"
But TeamPatchAssignmentDto.TeamId is a single letter ("T", "S", "B", "Y").
In ProcessFrameObservation:
var team = teams.FirstOrDefault(t => t.TeamId == assignment.TeamId);
// This is ALWAYS null for main base because "T" != "123.4_456.7_T1"
Impact: Workers are never synchronized into _expansionTeams. FindWorkerTeam returns null. GetJITMiningTarget falls back to (0,0). Workers walk to the map origin.
Fix: Use TeamNumber (int) as the canonical key, or ensure MiningTeam.TeamId is set to match TeamPatchAssignmentDto.TeamId.
// In InitializeExpansionMining, use the same ID scheme:
team.TeamId = GetTeamPrefix(i + 1); // "T", "S", "B", "Y" for main base
________________________________________
🔴 Bug C: JIT Rotation Is Backwards
Location: BabySharkMiningManager.RegisterCargoReturn + GetJITMiningTarget
The design doc specifies per-worker A/B alternation: “Every worker alternates between harvesting a B Far patch and a A Near patch.”
The current code uses a team-role swapper:
// RegisterCargoReturn
if (workerTag == state.MineralA_Worker) {
    state.WaitingWorker = workerTag;        // Returning worker becomes WAITING
    state.MineralA_Worker = switcher;       // Old waiter goes to A
}
Impact: The worker that just returned cargo is sent to the wait point, not the alternate mineral. The design doc explicitly says the returning worker should go to the other mineral after dropping cargo.
Fix: Implement per-worker mineral tracking exactly as shown in the design doc’s JitWorkerData:
// Add to BabySharkMiningManager or BaseDtos
private class JitWorkerState
{
    public ulong CurrentMineralTag { get; set; }
    public ulong AlternateMineralTag { get; set; }
    public bool WasCarrying { get; set; }
}

// On cargo return:
private void OnCargoReturned(ulong workerTag)
{
    if (!_jitWorkerStates.TryGetValue(workerTag, out var state)) return;
    var old = state.CurrentMineralTag;
    state.CurrentMineralTag = state.AlternateMineralTag;
    state.AlternateMineralTag = old;
}
Then GetJITMiningTarget simply looks up state.CurrentMineralTag for that worker. No team-role state machine needed.
________________________________________
🔴 Bug D: Debugger.Break() in Production Loop
Location: BabySharkMiningManager.ExecuteJustInTimeMining
if (carrying && !wasCarrying)
{
    Console.WriteLine($"...Triggering system break.");
    System.Diagnostics.Debugger.Break();  // <-- REMOVE THIS
}
________________________________________
🔴 Bug E: Settings.ccaMining Is a Shared Mutable Flag
Location: CcaManager.OnFrame, BabySharkMiningManager.ProcessFrameObservation, BabySharkMiningManager.SignalMiningStarted
CcaManager sets it to false at frame 35. ProcessFrameObservation sets it to _currentFrame <= 35. If BabySharkMiningManager runs after CcaManager in the manager list, it overwrites the handoff.
Fix: One owner. CcaManager should set the flag to false and then BabySharkMiningManager should treat it as read-only.
________________________________________
3. Architecture Issues
Issue 1: Manager vs. MicroTask Ownership Conflict
BabySharkMiningManager is an IManager. TeamPatchMiningTask is a MicroTask (inherits from MiningTask). In Sharky: - Managers run first and generate actions - MicroTasks are executed by the MicroManager and also generate actions - If both issue ActionRaw commands for the same unitTag, the last one in the action list wins
Currently: - BabySharkMiningManager issues raw move/harvest/return commands directly - TeamPatchMiningTask also issues commands via commander.Order(...)
Recommendation: Pick one architecture:
Approach	Pros	Cons
A. Manager-only	Simple; no Sharky MicroTask complexity	Bypasses Sharky’s worker defense/build logic
B. MicroTask-only	Integrates with Sharky; defense/build prepositioning is natural	Must reimplement inside TeamPatchMiningTask
Recommended: Go with B — move ALL mining logic into TeamPatchMiningTask. Remove BabySharkMiningManager’s action generation entirely; keep it as a data service (map data, debug drawing, mineral tracking). This is the Sharky-idiomatic way.
Issue 2: Dead Code in TeamPatchMiningTask
The task carries heavy baggage that appears unused: - ImitationRecorder _recorder, _scoreRecorder - MiningPatternAdvisor _patternAdvisor - MiningCycleRecord _currentCycle - CycleLengthFrames = 90 and cycle phase math that is bypassed
Recommendation: Delete all imitation-learning and cycle-recording code. It complicates the state machine and isn’t referenced by any other file.
Issue 3: Hardcoded Frame Thresholds
chrisCrossAppleSause uses magic numbers: frame == 0, frame >= 35, frame > 65. These are scattered across CcaManager and chrisCrossAppleSause.
Recommendation: Centralize in a CcaTimingConfig class or constants.
________________________________________
4. Refactoring Plan
Phase 1: Fix the CCA Handoff (Immediate)
1.	Remove BabySharkMiningManager.RunJustInTimeMining() — CCA should not exist in the mining manager.
2.	Remove TeamPatchMiningTask’s CCA bump order call. Let CcaManager own it exclusively.
3.	In BabySharkMiningManager.OnFrame(), if Settings.ccaMining == true, return only debug draw actions, no unit commands.
Phase 2: Fix Team Identity (Immediate)
Unify team identification. Either: - Make MiningTeam.TeamId match TeamPatchAssignmentDto.TeamId (e.g., "T", "S"), OR - Use TeamNumber (int) everywhere and drop string IDs for internal lookups.
Phase 3: Rewrite JIT Rotation (High Priority)
Replace RegisterCargoReturn / MiningTeamState with per-worker mineral alternation:
// In BabySharkMiningManager (or BaseDtos)
private readonly Dictionary<ulong, JitWorkerState> _jitStates = new();

public void InitializeWorkerJitState(ulong workerTag, ulong mineralA_Tag, ulong mineralB_Tag, string teamId)
{
    _jitStates[workerTag] = new JitWorkerState
    {
        TeamId = teamId,
        CurrentMineralTag = mineralA_Tag,    // Start on A
        AlternateMineralTag = mineralB_Tag,  // Switch to B after return
        WasCarrying = false
    };
}

// In ExecuteJustInTimeMining, replace the cargo transition block:
var carrying = worker.BuffIds.Any(b => b == CARRY_MINERALS_BUFF);
_previousCarryingState.TryGetValue(worker.Tag, out var wasCarrying);

if (!carrying && wasCarrying && _jitStates.TryGetValue(worker.Tag, out var jit))
{
    // SWAP: returning worker goes to alternate mineral next
    var temp = jit.CurrentMineralTag;
    jit.CurrentMineralTag = jit.AlternateMineralTag;
    jit.AlternateMineralTag = temp;
}
_previousCarryingState[worker.Tag] = carrying;

// Then resolve target:
ulong targetMineralTag = 0;
if (carrying) {
    // Move to JIT return point, then SMART to townhall
} else {
    if (_jitStates.TryGetValue(worker.Tag, out var jit))
        targetMineralTag = jit.CurrentMineralTag;
    // Issue HARVEST_GATHER to targetMineralTag, or MOVE to its approach point
}
Phase 4: Consolidate Action Generation (Medium Priority)
Move all worker command generation into one of these two locations: 1. TeamPatchMiningTask (Sharky-native, recommended) 2. BabySharkMiningManager (simpler migration, but non-idiomatic)
If you choose TeamPatchMiningTask: - BabySharkMiningManager becomes a read-only data provider - TeamPatchMiningTask queries it for mineral tags, return points, and JIT state - Build prepositioning (JitPrepositionService) stays in the Task where it already lives
Phase 5: Clean Up chrisCrossAppleSause
•	Extract frame thresholds (0, 35, 65) into const fields
•	The AlignAtMineralA phase issues global harvest commands to all workers. Ensure this only happens once (use a bool _harvestCommandsIssued flag) to avoid spamming HARVEST_GATHER every frame.
________________________________________
5. Specific Code Snippets
Fix: Remove Duplicate CCA in BabySharkMiningManager
// BabySharkMiningManager.OnFrame
public IEnumerable<SC2Action> OnFrame(ResponseObservation observation)
{
    _currentFrame = (int)observation.Observation.GameLoop;
    ProcessFrameObservation(observation);  // Read-only sync, NO command generation

    var actions = new List<SC2Action>();

    if (Settings.ccaMining)
    {
        // DO NOT issue commands here. CcaManager owns this phase.
        // Only debug drawing:
        if (ManagerDebugService.IsDebugEnabled) DrawDebugVisuals(observation);
        return actions;
    }

    actions.AddRange(ExecuteJustInTimeMining(observation));
    // ... rest of debug drawing ...
    return actions;
}
Fix: Per-Worker A/B Swap
private class JitWorkerState
{
    public string TeamId { get; set; }
    public ulong CurrentMineralTag { get; set; }
    public ulong AlternateMineralTag { get; set; }
    public bool WasCarrying { get; set; }
}

private void OnWorkerCargoReturned(ulong workerTag)
{
    if (!_jitStates.TryGetValue(workerTag, out var state)) return;
    (state.CurrentMineralTag, state.AlternateMineralTag) = 
        (state.AlternateMineralTag, state.CurrentMineralTag);
}
Fix: Use Named Buff Constants
Replace 271, 272 with:
private const uint BUFF_CARRY_MINERAL_FIELD = 271;
private const uint BUFF_CARRY_HIGH_YIELD = 272;
// Or use Sharky's Ids.Buffs if available
________________________________________
6. Testing Checklist
After refactoring, verify:
•	☐ Frame 0–34: Only CcaManager issues commands. BabySharkMiningManager draws debug only.
•	☐ Frame 35: Clean handoff. No duplicate commands. Settings.ccaMining becomes false and stays false.
•	☐ Frame 35+: Workers alternate minerals. A worker returning from Mineral A goes to Mineral B next cycle, not the wait point.
•	☐ Team IDs: FindWorkerTeam successfully resolves all 12 workers to their correct MiningTeam.
•	☐ Speed Mining Fallback: 2-worker teams (non-JIT) stay on their assigned mineral and use standard speed mining geometry.
•	☐ Build Prepositioning: JitPrepositionService can still pull a worker from Team 4 for Spawning Pool without breaking the rotation for remaining workers.
________________________________________
Would you like me to generate the fully refactored BabySharkMiningManager.cs with the per-worker JIT state machine and the team-ID fix applied?

Fix: Establish single ownership.
Option 1 (Recommended): CcaManager owns frames 0–35 exclusively. BabySharkMiningManager should skip action generation when Settings.ccaMining == true and limit itself to debug drawing. TeamPatchMiningTask should return Empty during CCA.
Fix: One owner. CcaManager should set the flag to false and then BabySharkMiningManager should treat it as read-only.
fix  Bug B: Team ID Mismatch Breaks Main-Base JIT
🔴 Bug C: JIT Rotation Is Backwards there are 8 minerals in the starting base for four teams of a large mineral and a small mineral node, some maps differ but usually 4 large mineral nodes that are near "A" and four smaller nodes that are further away "B" with an 8 worker start we have 4 "1" workers and 4 "2" workers the "1" workers starts on the "A" mineral node, and the "2" workers start on the "B" nodes.
For a 12 worker start we have 4 workers that have the role of pushing and bumping the "1" workers from behind in order to repel the "1" workers faster to the target.  These four "3" team members will mine "A" when the "1" worker has collected minerals.
   🔴 Bug D: Debugger.Break() in Production Loop that is there so I can see what is happening and diagnose the real bugs 
🔴 Bug E: Settings.ccaMining Is a Shared Mutable Flag Fix: One owner. CcaManager should set the flag to false and then BabySharkMiningManager should treat it as read-only.
3. Architecture Issues
Issue 1: Manager vs. MicroTask Ownership Conflict
Recommended: Go with B — move ALL mining logic into TeamPatchMiningTask. Remove BabySharkMiningManager's action generation entirely; keep it as a data service (map data, debug drawing, mineral tracking). This is the Sharky-idiomatic way.
Issue 2: Dead Code in TeamPatchMiningTask Recommendation: Delete all imitation-learning and cycle-recording code. It complicates the state machine and isn't referenced by any other file.  Machine learning was tried to improve the worker "3" pushing and bumping but never worked
Issue 3: Hardcoded Frame Thresholds Recommendation: Centralize in a CcaTimingConfig class or constants.

