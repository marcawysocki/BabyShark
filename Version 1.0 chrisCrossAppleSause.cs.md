Source files https://github.com/marcawysocki/BabyShark 

Version 1.0 chrisCrossAppleSause.cs, teamPatchAssignment.cs 
The working directory is C:\Users\marca\source\repos\BabyShark 

 C:\Users\marca\source\repos\BabyShark\Version 1.0 chrisCrossAppleSause.cs.md, C:\Users\marca\source\repos\BabyShark\BabySharkBot\Services\chrisCrossAppleSause.cs C:\Users\marca\source\repos\BabyShark\BabySharkBot\MicroTasks\TeamPatchMiningTask.cs

This Operates on the principal of a worker repelling another worker from behind, Causing the worker to accelerate faster.

Workers start with a designation W12 to W1 or W8 to W1 depending on a 8 or 12 worker start.  
W12 thru W1 are NOT TEAMS. no team logic or assignment is correct for these labels. This is a temporary label, Do not confuse this with the final team labels. Starcraft two maps are all slightly different from each other and the observation loop, it's kind of random    

This is done as a consistent map Independent ordering of the workers. W12 is always the farthest away from the Mineral center of Mass.  The order of the minerals are based on the order of the workers  And then the teams are created based on the mineral order. 

 It's not necessary to understand how that works. It is necessary for you to understand that the original labels are there for a reason.  

Right aways, the labels change to four teams.  Team teal, T1,T2,T3 for example, this team mines TA and TB which are mineral node labels. Three other teams start the game. For a 12 worker start, the teams are teal, salmon, blue, and yellow.  The labels take the first letter for Example S for salmon would give us S1 S2 S3 and Minerals SA and SB.

The goal is to get the "1" worker to the "A" mineral as fast as possible. The "2" worker is trying to get to the "B" mineral as fast as possible. The "3" worker is trying to get it's teammate to the "A" mineral as fast as possible and Later also goes to the Same mineral and waiting for "1" worker to finish mining to the "A" mineral. The "3" worker will be assigned to the "A" mineral if we have a 12 worker starting worker count. They will be trying to accelerate their teammate to the "A" mineral.


the 12 worker starting Teams are Teal, Salmon, Blue, and Yellow. 
Teal Team is T1, T2, T3. Target Minerals are TA and TB.
Salmon Team is S1, S2, S3. Target Minerals are SA and SB
blue Team is B1, B2, B3. Target Minerals are BA and BB
Yellow Team is Y1, Y2, Y3. Target Minerals are YA and YB

bools Settings.


jitMining = False ccaMining = True M1Bump = TealM1IsFar M8Bump = YellowM8IsFar T1Bump = True S1Bump = True B1Bump = True Y1Bump = True

Service chrisCrossAppleSause.cs Starting base mining Assignment. From Start[0 to 3] Initial worker formation: This will be for either a 8 or 12 worker test on 8 mineral nodes. The first 4 workers will be the "1" workers. They are assigned as the most optimal worker to go to the "A" mineral. The next 4 workers will be the "2" workers. Their job is to move to the "B" minerals as fast as posibile. The last 4 workers will be the "3" workers. The "1" workers are the primary workers that we are trying to accelerate to the "A" mineral. The "3" workers are the secondary workers that are trying to accelerate their teammate to the "A" mineral.

If our starting worker count is 8 then all Bumping pairs are false. The "1" workers will be T1, S1, B1, Y1. The "2" workers will be T2, S2, B2, Y2. There are no starting "3" workers In an 8 worker start. 

The 8 worker Starting worker Count is a much much simpler process where each worker just goes to a mineral and starts mining.

The "3" workers will be assigned to the "A" minerals if we have a 12 worker starting worker count. They will be trying to accelerate their teammate to the "A" mineral.

We will have 2 to 4 bumping pairs with the possibility of A 5th and 6th if the outside minerals are far minerals TealM1IsFar and YellowM8IsFar

TealM1IsFar and YellowM8IsFar Maybe causing a lot of the confusion for the program, and Might need to be scrapped as an idea.
If TealM1IsFar then S3 T1 and T3 are bumping Triple. If YellowM8IsFar then B3 and Y1 Y3 are bumping Triple. The Outside "3" workers are only Temporary bumping the "T1" or "Y1"  worker before moving to the "A" mineral of their own team to mine. Normal Bumping pairs are T1/T3, Y1/Y3. These are determined by the team label, not by distance or helper or fabricated non existing preloaded data.  The idea was if there was an outside mineral that was far that would give me a chance to have another worker in the mix moving The whole group forward.  Usually B3 and S3 are to far away from S1 and B1 to bump them forward.

A point that is the average the bumping pair and the A mineral forms a target line. This average line is closer to the "1" worker than the "3" worker This is to give the primary worker a shorter line and trying to get pushing worker in a possession where it is behind the primary worker and giving him a push so that he gets repelled faster to the mineral.

On Frame 0 and each 5th frame, Calculate Half the distance from each "A" Mineral and on each target line. Issue a Move command to the "1" worker to move to the Calculated point. Worker "1" continue to move until it reaches the harvest circle around the "A" mineral. When we add learning, Additional cycles may change the angle and move to point as we learn the benefit and results of bumping, a straight line may not be the fastest route. The other bumping partner moves 1/4 along the bumping line.

If what we know is nothing because we are on the first attempt then only the "1" are issued commands. The workers do not mine in this test. They move back to the start positions. The first attempt without assistance is the baseline speed for all the "1" workers.



Thinking so far

Progress and Coding Changes (Updated 2026-07-25)
Completed Tasks:
Implemented State Machine in chrisCrossAppleSause.cs:

Added Idle, AssigningWorkers, AcceleratingWorkerOne, AlignAtMineralA, and CancelAndReturnHome phases.
Implemented BuildBumpOrders to run every 5 frames and handle phase transitions.
Added automatic transition from Idle to AssigningWorkers when ccaMining is enabled and assignments are available, ensuring the test cycle starts automatically.
Implemented Worker Pairing and Bumping Logic:

Added logic to identify workers by label suffix ("1", "2", "3").
Implemented ResolveLiveWorkerBySuffix to ensure movement commands are issued to live units with valid UnitTag values by matching labels between logical records and live observations.
Updated RecordSpawnObservation to synchronize UnitTag values from live workers into the team assignments state.
Implemented target line calculation based on the average of the bumping pair and the target mineral.
Added movement commands for primary workers (W1) and bumpers (W3/W2).
Integrated with TeamPatchMiningTask.cs:

Added enemy detection in base, triggering a transition to CancelAndReturnHome.
Implemented handling for CancelAndReturnHome to return workers to their starting positions and reset the cycle.
Updated Settings:

Set Settings.ccaMining = true as per Version 1.0 requirements.
Refactored BabySharkBot.cs Manager and Task Initialization:

Replaced the inefficient "load-and-remove" pattern with a "clear-and-selectively-add" approach for both managers and microtasks.
Only DebugManager, BabySharkUnitManager, and CcaManager are now loaded initially.
MicroTaskData is now cleared on startup and only registers three essential tasks: CustomMiningTask, TeamPatchMiningTask, and BabySharkOverlordScoutTask.
This prevents 50+ unwanted Sharky tasks from running when the MicroManager is eventually activated, ensuring a cleaner execution environment for the acceleration test.
Implemented Just-In-Time (JIT) Mining Integration:

Added MineralNode and MiningTeam DTOs to BaseDtos.cs for tracking paired mineral nodes and worker rotations.
Updated BabySharkMiningManager.cs to support expansion and main base JIT initialization, including clockwise mineral ordering and team formation.
Integrated JIT logic into TeamPatchMiningTask.cs: starting on or after frame 15, teams with exactly 3 workers automatically switch to JIT Mining (alternating targets), while others continue with optimized Speed Mining.
Fixed Missing Team Assignments on Startup:

Updated InitialMapData.cs to call TeamLabelRegistrationHelper.RegisterTeamLabels before returning the initial map data. This ensures team assignments are populated on the very first frame.
Added a multi-level fallback mechanism in chrisCrossAppleSause.cs (BuildBumpOrders) to search across all possible assignment sources (TeamPatchAssignments, SecondaryTeamPatchAssignments, and AssignmentsByWorkerCount) if the current spawn state is empty.
Added detailed logging to track assignment resolution and fallback hits.
Implemented Just-In-Time (JIT) Mining for 12 Workers:

Updated TeamLabelRegistrationHelper.cs to calculate optimized JitReturnPoint and JitWaitPoint for each 3-worker team. The return point is calculated on the townhall boundary to minimize alternating travel distance between minerals A and B.
Refactored BabySharkMiningManager.cs to handle the rotation state machine: when a worker returns cargo, they switch roles with the "waiting" worker, alternating targets between Near and Far patches.
Added JitPrepositionService.cs to handle specialized build orders. It selects an optimal worker from Team 4 (Yellow) who has just returned cargo to preposition at the Spawning Pool location (near V2) when minerals reach ~160.
Integrated prepositioning into TeamPatchMiningTask.cs, ensuring the selected builder is excluded from standard mining orders and issued the BUILD_SPAWNINGPOOL command precisely when minerals hit 200.
Next Steps:
Verify the exact "S3 and T2" crossover logic if TealM1IsFar is true.
Add learning logic to adjust the bumping angles and move-to points over multiple attempts.
Implement the "second attempt" and "third attempt" logic for baseline comparisons.