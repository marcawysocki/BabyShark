Source files https://github.com/marcawysocki/BabyShark
Local AI returned a Truncated  File for the following Prompt, the responce is  pasted at the end. 

Version 1.0 chrisCrossAppleSause.cs, teamPatchAssignment.cs currently not working

Return code only. Do not output any <think> tags or reasoning logs. Provide the complete C# code block directly.

You are not working in other repo directories. 
The working directory is C:\Users\marca\source\repos\BabyShark 
The files you need to work on is this prompt C:\Users\marca\source\repos\BabyShark\Version 1.0 chrisCrossAppleSause.cs.md, 
C:\Users\marca\source\repos\BabyShark\BabySharkBot\Services\chrisCrossAppleSause.cs
C:\Users\marca\source\repos\BabyShark\BabySharkBot\MicroTasks\TeamPatchMiningTask.cs


Please work on this until the code is complete.  This is a test to see if we can accelerate a worker to a mineral by having another worker bump it from behind.  The goal is to get the "1" worker to the "A" mineral as fast as possible.  The "2" worker is trying to get to the "B" mineral as fast as possible and also trying to accelerate it's teammate to the "A" mineral.  The "3" worker is trying to get to the "A" mineral as fast as possible and also trying to accelerate it's teammate to the "A" mineral.  The "3" worker will be assigned to the "A" mineral if we have a 12 worker starting worker count. They will be trying to accelerate their teammate to the "A" mineral.
Please add progress and coding changes to this MD file so that restarting the prompt can continue where it left off. 


bools 
Settins.

jitMining = False
ccaMining = True
M1Bump = TealM1IsFar
M8Bump = YellowM8IsFar
T1Bump = True
S1Bump = True
B1Bump = True
Y1Bump = True




Service chrisCrossAppleSause.cs Starting base mining Assignment.
From Start[0 to 3] Initial worker formation:
This will be for either a 8 or 12 worker test on 8 mineral nodes.  The first 4 workers will be the "1" workers.  The next 4 workers will be the "2" workers. Their job is to move to the "B" minerals as fast as posibile.   The last 4 workers will be the "3" workers.  The "1" workers are the primary workers that we are trying to accelerate to the "A" mineral.  The "3" workers are the secondary workers that are trying to accelerate their teammate to the "A" mineral.  

If our starting worker count is 8 then all Bumping pairs are false. The "1" workers will be T1, S1, B1, Y1.  The "2" workers will be T2, S2, B2, Y2.  There are no starting "3" workers.  The "1" workers will be assigned to the "A" minerals and the "2" workers will be assigned to the "B" minerals.  

The 8 worker  Starting worker  Count is a much much simpler process where each worker just goes to a mineral and starts mining .

The "3" workers will be assigned to the "A" minerals if we have a 12 worker starting worker count. They will be trying to accelerate their teammate to the "A" mineral.

We will have 4 bumping pairs with the possibility of A 5th and 6th if the outside minerals are far minerals   TealM1IsFar and YellowM8IsFar

If TealM1IsFar then S3 and T2 are bumping pairs.  If YellowM8IsFar then B3 and Y2 are bumping pairs.  Normal Bumping pairs are T1/T3, S1/S2, B1/B2, Y1/Y3.  These are determined by the team label, not by distance or helper or fabricated non existing preloaded data.

A point that is the average the bumping pair and the A mineral forms a target line.

On Frame 0 and each 5th frame, Calculate Half the distance from each "A" Mineral and on each target line.  Issue a Move command to the "1" worker to move to the calulated point. Worker "1" contines to move until it reaches the harvest circle around the "A" mineral.  When we add learning, Additional cycles may change the angle and move to point as we learn the benifit and results of bumping, a straight line may not be the fastest route.  The other bumping partner moves 1/4 along the bumping line. 

If what we know is nothing because we are on the first attempt then only the "1" are issued commands.  The workers do not mine in this test.  They move back to the start positions.  The first attemp without assistance is the baseline speed for all the "1" workers.

Worker "2" units need to find a mineral that has a path that intersects the path of workers "1" That worker is issued a Mineral Walk command.  When we reach the frame that puts that worker on the intersection of a "1" worker, the "2" worker switches to a Bump Push from behind, by being issued the same Move as it's "1" team mate.

On the second attempt, the "2" worker mineral walks all the way to the "B" mineral For a baseline time.
On the 3rd attempt the "2" worker  Is about to cross the path of the "1" worker teamate, then  "2" worker switches to a move command to learn the effect of bumping  "1" worker teamate.

After  the third attempt Teams T,M,B,Y can each try a different variations. The "1" worker teamate only does moves and it's goal is getting to "A" as fast as posible. The "2" worker has a lesser goal of getting to the "B" mineral as fast as posible and the The immediate goal of trying to accelerate it's teammate. 

We will Continue the weight system that we've been using: 
Near = 1x base weight, Large = 1.25×, Far = 1.75×.

Teams M & B Could be a large mineral that is not a Near mineral. Both teams could reach a threshold of 1.25 with less testing needed. In other words it what the "2" mineral is doing increase its time getting to "B" by more than 1.25 then it is doing too much to accerate "1" to "A"

For a team that is comprised of near mineral and a  far mineral we have a threshold of 1.75   In other words it what the "2" mineral is doing increase its time getting to "B" by more than 1.75 then it is doing too much to accerate "1" to "A" 

Teams T & Y We'll have a pair "3" workers nearby that can help accelerate "1" and "2" They have time Push and bump,  they need to arive at their Designated mineral before their teammate finishes mining.

Worker "3" will end with moving in line with the "A" mineral, the "1" worker about 1u closer to the hatchery. The hatchery the mineral and each of the two workers will all be on the same straight line.  Finally the worker Mineral  walks into position so that it is in the same position as its teammate and ready to begin mining once it's teammate has finished mining, it starts mining.      


At this point we learn the Fastest time of "1" going to "A" All 12 workers cancel mining And return to original starting position and then another cycle begins.  So we should be collecting 0 minerals during the test ends when The user decides that we can no longer get a faster time. We are learning how to mine faster with out needing to mine.  

When an opponet enter the base the test ends and the current cycle is void.    


## Progress and Coding Changes (Updated 2026-07-25)

### Completed Tasks:
1.  **Implemented State Machine in `chrisCrossAppleSause.cs`**:
    *   Added `Idle`, `AssigningWorkers`, `AcceleratingWorkerOne`, `AlignAtMineralA`, and `CancelAndReturnHome` phases.
    *   Implemented `BuildBumpOrders` to run every 5 frames and handle phase transitions.
    *   Added automatic transition from `Idle` to `AssigningWorkers` when `ccaMining` is enabled and assignments are available, ensuring the test cycle starts automatically.
2.  **Implemented Worker Pairing and Bumping Logic**:
    *   Added logic to identify workers by label suffix ("1", "2", "3").
    *   Implemented `ResolveLiveWorkerBySuffix` to ensure movement commands are issued to live units with valid `UnitTag` values by matching labels between logical records and live observations.
    *   Updated `RecordSpawnObservation` to synchronize `UnitTag` values from live workers into the team assignments state.
    *   Implemented target line calculation based on the average of the bumping pair and the target mineral.
    *   Added movement commands for primary workers (W1) and bumpers (W3/W2).
3.  **Integrated with `TeamPatchMiningTask.cs`**:
    *   Added enemy detection in base, triggering a transition to `CancelAndReturnHome`.
    *   Implemented handling for `CancelAndReturnHome` to return workers to their starting positions and reset the cycle.
4.  **Updated Settings**:
    *   Set `Settings.ccaMining = true` as per Version 1.0 requirements.
5. **Refactored `BabySharkBot.cs` Manager and Task Initialization**:
    *   Replaced the inefficient "load-and-remove" pattern with a "clear-and-selectively-add" approach for both managers and microtasks.
    *   Only `DebugManager`, `BabySharkUnitManager`, and `CcaManager` are now loaded initially.
    *   `MicroTaskData` is now cleared on startup and only registers three essential tasks: `CustomMiningTask`, `TeamPatchMiningTask`, and `BabySharkOverlordScoutTask`.
    *   This prevents 50+ unwanted Sharky tasks from running when the `MicroManager` is eventually activated, ensuring a cleaner execution environment for the acceleration test.

6. **Implemented Just-In-Time (JIT) Mining Integration**:
    *   Added `MineralNode` and `MiningTeam` DTOs to `BaseDtos.cs` for tracking paired mineral nodes and worker rotations.
    *   Updated `BabySharkMiningManager.cs` to support expansion and main base JIT initialization, including clockwise mineral ordering and team formation.
    *   Integrated JIT logic into `TeamPatchMiningTask.cs`: starting on or after frame 15, teams with exactly 3 workers automatically switch to JIT Mining (alternating targets), while others continue with optimized Speed Mining.

7. **Fixed Missing Team Assignments on Startup**:
    *   Updated `InitialMapData.cs` to call `TeamLabelRegistrationHelper.RegisterTeamLabels` before returning the initial map data. This ensures team assignments are populated on the very first frame.
    *   Added a multi-level fallback mechanism in `chrisCrossAppleSause.cs` (`BuildBumpOrders`) to search across all possible assignment sources (`TeamPatchAssignments`, `SecondaryTeamPatchAssignments`, and `AssignmentsByWorkerCount`) if the current spawn state is empty.
    *   Added detailed logging to track assignment resolution and fallback hits.

8. **Implemented Just-In-Time (JIT) Mining for 12 Workers**:
    *   Updated `TeamLabelRegistrationHelper.cs` to calculate optimized `JitReturnPoint` and `JitWaitPoint` for each 3-worker team. The return point is calculated on the townhall boundary to minimize alternating travel distance between minerals A and B.
    *   Refactored `BabySharkMiningManager.cs` to handle the rotation state machine: when a worker returns cargo, they switch roles with the "waiting" worker, alternating targets between Near and Far patches.
    *   Added `JitPrepositionService.cs` to handle specialized build orders. It selects an optimal worker from Team 4 (Yellow) who has just returned cargo to preposition at the Spawning Pool location (near V2) when minerals reach ~160.
    *   Integrated prepositioning into `TeamPatchMiningTask.cs`, ensuring the selected builder is excluded from standard mining orders and issued the `BUILD_SPAWNINGPOOL` command precisely when minerals hit 200.

### Next Steps:
- Verify the exact "S3 and T2" crossover logic if TealM1IsFar is true.
- Add learning logic to adjust the bumping angles and move-to points over multiple attempts.
- Implement the "second attempt" and "third attempt" logic for baseline comparisons.
