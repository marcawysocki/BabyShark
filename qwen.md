# Qwen File-Mapping Directive 

## Objective

This is a three AI Work through. The local AI Analyzes my code page to create a KimiK3 files. It should Output for Kimi K3 Aware Text.  When Kimi Analyzes frpm My local AI it should output  suitable for "Coder"  Accio Work's AI assistant then "Coder"  Accio Work's AI assistant integrates The output of KimiK3 back into my system.

Analyze the codebase structure. Identify the necessary files required to execute our current development tasks.  The Local AI does not have the role of coding, Local AI Pass files to KimiK3 AI that does the code    

Any Questions that need to be clarified should be listed in your reasoning, the user will read that and rewrite the promt as necessary. 

New First Task The greed Chain needs to be updated for Color Team assignment. 
      { 0, (2, "Green", 0) },      // M[0]+M[1] -> Green in 8-worker, Teal in 12-worker
            { 2, (2, "Purple", 2) },     // M[2]+M[3] -> Purple in 8-worker, Salmon in 12-worker  
            { 4, (2, "Red", 4) },     // M[4]+M[5] -> Red in 8-worker, 
            { 6, (2, "Orange", 6) }         // M[6]+M[7] -> Orange in 8-worker, Yellow in 12-worker
            For an 8 worker start W3 needs to be the Anchor point instead of W4 for determing M[7]
     


Second Task.  BuildIne.cs Should create drones until DesiredUnitCount ZERG_DRONE has been reached See chrisCrossAppleSause 
commands.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = morphCmd } });
Console.WriteLine($"chrisCrossAppleSause [{state.SpawnKey}]: Morphing larva {larva.Tag} into drone on frame 0");

Third Task. When a larva morphs into a drone it can join the teams. It will need a worker label. This needs to be reusable code that New build orders can run. 

see TeamPatchMiningTask.cs and TeamLabelRegistrationHelper.cs 

StarCraft II Maps Have multiple starting locations And map configuration for mineral nodes and that's vespine gas '

Include in your reasoning Both the 8 worker starting Teams Green, Orange, Purple, and Red and The 12 worker starting Teams Teal, Salmon, Blue, Yellow
chrisCrossAppleSause should Morph 1 Larva into a Drone for both 12 and 8 worker start, currently it is only 8 worker start.



For an 8 worker start The teams will eventually reach the same worker count and functionas a 12 worker start. The new workers 9 thru 12 will join the teams that would have been created in a 12 worker start.

In an 12 worker start the third worker eventually Mines it's teams "A" Mineral. In an 8 worker start those Third workers Morph into a Drone one at a time.

Past 12, we start to create 4 worker teams once 16 workers have been reached, 13,14,15 will be multi team helpers in the transistion from 3 worder teams to 4 worder teams.


If the current worker count is 8 then the new worker will be will join the team that would have been Teal (Green) on a 12 worker start.
The "1","2" existing workers and "A", "B" Mineral will become a three worker team, the workers and Minerals all change to Teal T1,T2, The new Larva that just Morphed into a drone will be T3 and the Minerals will be TA and TB.

If the current worker count is 9 then the new worker will be will join the team that would have been Yellow (Orange) on a 12 worker start.
The "1","2" existing workers and "A", "B" Mineral will become a three worker team, the workers and Minerals all change to Yellow Y1,Y2, The new Larva that just Morphed into a drone will be Y3 and the Minerals will be YA and YB.

If the current worker count is 10 then the new worker will be will join the team that would have been Salmon (Purple) on a 12 worker start.
The "1","2" existing workers and "A", "B" Mineral will become a three worker team, 
the workers and Minerals all change to Salmon S1,S2. 
The new Larva that just Morphed into a drone will be S3 and the Minerals will be SA and SB.

If the current worker count is 11 then the new worker will be will join the team that would have been Blue (Red) on a 12 worker start.
The "1","2" existing workers and "A", "B" Mineral will become a three worker team, 
the workers and Minerals all change to Blue B1,B2. 
The new Larva that just Morphed into a drone will be B3 and the Minerals will be BA and BB.

If the worker count is 12 Then all the teams will be the same  Whether they started it as an eight worker count or a 12 worker count    
The 13th Worker would join team Teal as T4, that create a four worker team. That team now follow Speed Mining Rules.
The 14th Worker would join team Yellow as Y4, that create a four worker team. That team now follow Speed Mining Rules.

An extractor trick Will be done so that we can increase our supply to 15 that  That will be done by a worker from the team that is closest to Vespene geyser V1
We will need hundred and twenty five minerals to do the extractor trick.  The team that will be doing the extractor trick will be the team that is closest to the Vespene geyser. Each time they change to carrying Minerals then if we will have 125 minerals once the worker returns his 5 minerals and travels to V1 we will have 125 minerals and the worker morphs into an extractor, a larva morphs into what will become the 15th worker, when it is concirmed that the 15th is morphing then the extractor is cancelled and the Worker that built sure and canceled it will return to mine with jis team

That point should  Already  be stored in BaseDtos

It Only takes one worker to do an extractor track  We need the shortest  two Point path  To return cargo to the Town Center and then travel to the Vespene Geyser 

We Will need A macro hatch placement service similar to the Spawning pool placement service and a new point in BaseDtos that is the shortest two point that is where the team closest to the ra 

The 15th Worker would join team Salmon as S4, that create a four worker team. That team now follow Speed Mining Rules.
The 16th Worker would join team Blue as B4, that create a four worker team. That team now follow Speed Mining Rules.

## Phase 1: Analysis & Team Mapping Table

### Current Architecture (from BabyShark context)
- **Team Label Service**: Tracks workers by tag with labels like H1, OV1, L1 for static units and W1-W12 for dynamic workers
- **Worker Assignment Logic**: 
  - Si=0: Average X,Y of all minerals = COM (Center Of Mining)
  - Find furthest worker from COM = W1 (gets label D1 or W12)
  - Remaining workers: greedy chain closest to previous → W2, W3...W12
- **Multi-Location Data**: BaseDtos stores OrderedMainMinerals with M[0]-M[7] in greedy order

### Team Mapping: 8 Worker Start vs 12 Worker Start

| 12-Worker Mineral Pair | 12-Worker Color/Team | 8-Worker Color/Team (When 3rd worker joins) |
|-----------------------|--------------------|-------------------------------------------|
| M[0] + M[1]           | **Teal** T1/T2/T3     | **Green** G1/G2                             |
| M[2] + M[3]           | **Salmon** S1/S2   | **Purplr** P1/P2                           |
| M[4] + M[5]           | **Blue** B1/B2     | **Red** R1/R2                           |
| M[6] + M[7]           | **Yellow** Y1/Y2   | **Orange** O1/O2                              |

Pink is a Multi team transition color that is used when a worker is morphing into a drone and has not yet joined a team. It is a temporary state  Between 13 and 16 workers  
The 13th, label S4, worker will Mine TB and SB a return to the Town Center returning minerals point will be the shortest two point path to the Town Center and then to the alternating minerals needs to be calculate and stored in BaseDtos.
The 14th, label Y4, worker will Mine YB and BB a return to the Town Center returning minerals point will be the shortest two point path to the Town Center and then to the alternating minerals needs to be calculate and stored in BaseDtos.
The 15th, label B4, worker will Mine SA and BA a return to the Town Center returning minerals point will be the shortest two point path to the Town Center and then to the alternating minerals needs to be calculate and stored in BaseDtos.

The 16th, label T4, worker will Mine TB and will be labeled Teal T4 and will   Signify the beginning of Speed Mining Rules for All teams.

The workers will contine to be switched to the alternating mineral pair for their team until they are om the correct Mineral for Speed Mining.

When we have 16 workers the teams will be as follows:
T1 and T3 will be TA, as soon either of them switches on the correct mineral for Speed Mining they will be switched to speed mining rules.
T2 and T4 will be TB, as soon either of them switches on the correct mineral for Speed Mining they will be switched to speed mining rules.
S1 and S3 will be SA, as soon either of them switches on the correct mineral for Speed Mining they will be switched to speed mining rules.
S2 and S4 will be SB, as soon either of them switches on the correct mineral for Speed Mining they will be switched to speed mining rules.
Y1 and Y3 will be YA, as soon either of them switches on the correct mineral for Speed Mining they will be switched to speed mining rules.
Y2 and Y4 will be YB, as soon either of them switches on the correct mineral for Speed Mining they will be switched to speed mining rules.
B1 and B3 will be BA, as soon either of them switches on the correct mineral for Speed Mining they will be switched to speed mining rules.
B2 and B4 will be BB, as soon either of them switches on the correct mineral for Speed Mining they will be switched to speed mining rules.
Since B4 is the last worker to be created and is mining the SA and BA mineral pair, this will need a switch BB after mining the BA mineral and 16 worker are in effect.

### Team Mapping: 8 Worker → New Worker Reassignment Rules

| Current Worker Count | New Worker Joins As       | 12-Worker Color Equivalent | Mineral Pair Used |
|---------------------|--------------------------|---------------------------|-------------------|
| 0-2                 | Teams already exist (G/S/P/R) | N/A                       | N/A               |
| 3                   | **Teal** T1/T2 → G1/G2   | Teal                      | M[0]+M[1]         |
| 4                   | **Salmon** S1/S2 → O1/O2 | Salmon                    | M[2]+M[3]         |
| 5                   | **Blue** B1/B2 → P1/P2   | Blue                      | M[4]+M[5]         |
| 6                   | **Yellow** Y1/Y2 → R1/R2 | Yellow                    | M[6]+M[7]         |
| 7                   | (8th worker - team already full) | N/A                       | N/A               |
| 8                   | **Teal** T1/T2/T3        | Teal                      | M[0]+M[1]         |
| 9                   | **Salmon** S1/S2/S3      | Salmon                    | M[2]+M[3]         |
| 10                  | **Blue** B1/B2/B3        | Blue                      | M[4]+M[5]         |
| 11                  | **Yellow** Y1/Y2/Y3      | Yellow                    | M[6]+M[7]         |
| 12+                 | All teams now 4 workers (T/Y/B/R) - Speed Mining Rules apply |

### Key Insight: "Team Teal" in 8-Worker Context = Green Team
The mapping is essentially a color shift. In an 8-worker start, the first mineral pair (M[0]+M[1]) which would be Teal in 12-worker becomes **Green** (G1/G2). The mapping preserves team structure but shifts colors:

```
Teal   → Green    (first pair)
Salmon → Purple   (second pair)
Blue   → Red   (third pair)
Yellow → Orange      (fourth pair)

### ⚠️ Questions for Clarification (Before Implementation)
1. **DesiredUnitCount source**: Where does `desiredCount` come from? Is it a build-order-specific parameter (e.g., 12 drones for rush, 8 for standard)? I recommend passing it as a constructor parameter or method argument to BuildIne.cs so it's configurable per build order.
2. **BuildIne.cs current structure**: Do you want me to replace the existing `chrisCrossAppleSause` morphing logic (which only works for 8-worker start) with this new loop, or keep both as separate methods? I recommend replacing it since the new system handles both 8 and 12 worker starts uniformly.
3. **TeamLabelRegistrationHelper.cs naming**: This is a placeholder name — do you want it in `Services/` folder alongside `WorkerLabelService` and `CrosshairService`, or create a new `TeamColorService` class? I recommend `Services/TeamColorService.cs` for the mapping + registration logic, and keeping `WorkerLabelService` as-is for tag-based tracking.
4. **Pink transition color**: Do you want the pink worker to be drawn with a special "morphing" animation (e.g., pulsating or semi-transparent) so it's visually obvious that the drone is transitioning? I recommend adding this to `CrosshairService.SetCOM()` as an optional `isMorphing` parameter.
User Edit: 1. BuildIne.cs, New Builds will hardcode the desired count of drones to morph. The count will change based on the build order. 2. No, Since the Builds are not active on frame 0, a Drone needs to Morph. Regardless of 12 or 8 worker start. 
TeamLabelRegistrationHelper is an existing file in setup.
4 Nothing fancy just pink

**Pink = Transition Color:** When a larva is morphing into a drone and has not yet joined its team, it gets Pink as temporary color until `TeamLabelRegistrationHelper` assigns the correct team + label. This happens during the single frame where `IsReady() == false` but `isMorphed == true`.
User Edit: Pink is the transition color for Workers 13,14,15 of Workers that have Morphed into Drones, not for Larva.
These Workers are muti team helpers that alternate two minerals that belong to different teams. They are not part of any team, but they help the other teams mine faster. They are assigned Pink color until they are assigned to a team at the 16 worker trasistion to Speed Mining.
They are given Team Labels of Y4/B4/S4 for the 16th worker transition to Speed Mining. The 13th, 14th, and 15th workers are assigned Pink color until they are assigned to a team at the 16 worker transition to Speed Mining.

### 1. BuildIne.cs Morphing (Frame 0)
- **Action**: Create new builds that hardcode `desiredCount` drones to morph
- **Condition**: Regardless of 8/12 worker start, if a build needs drones and frame > 0 → morph larva
- **Existing code path** stays intact for 8-worker default (chrisCrossAppleSause method)
- **New builds** pass `desiredCount` parameter to BuildIne.cs
- 
- ```BuildIne.cs is an existing file, BuildTest.cs is an existing file, as an example of another build> These files will have stages or steps with conditions to be met.  Worker count is a condition that needs to be met in order to create a structure or follow some logic or plan.
-  the condition is met on frame zero, not after. That consumes the larva and morphs it into a drone and uses the starting 50 mineral cost. Additional drones require room in our supply quota capacity that starts as 14 for Zerg.
- 
- We start with 50 minerals and 3 larva.  We can afford it with the starting 50 mineral cost and available larva. Additional drones require 50 minerals each. As the worker return cargo we increase the minerals in our bank
- The first new drone is created on frame zero.  The second new drone is created on frame after 50 new mineral have been returned to the Townhall. Consuming 50 minerals, 1 supply space, and a larva.
-  
-   The third new drone is created when another 50 minerals have been collected 
- The code in chrisCrossAppleSause tests for an 8 worker start, it no longer need that test to morph a larva into a drone.  The desireed count is greater than the current worker count, and we have a larva available to morph into a drone.  The code will morph the larva into a drone and assign it to the correct team based on the current worker count.  The new drone will be assigned a team label based on the current worker count and the team mapping table above.
We are not passing desired count

### 3. Worker Reassignment Rules (Not The "Pink" System) Corrected
- **Workers 1-8**: Already assigned to their teams (Green/Purple/Orange/Red in 8-worker start)
- **Worker 9**: Joins Teal's position → becomes Teal T3, G1 becomes T1, G2 becomes T2
- **Worker 10**: Joins Yellow's position → becomes Yellow Y3, O1 becomes Y1, O2 becomes Y2
- **Worker 11**: Joins Salmon's position → becomes Salmon S3, P1 becomes S1, P2 becomes S2
- **Worker 12**: Joins Blue's position → becomes Blue B3, R1 becomes B1, R2 becomes R2
-

Perhaps I am not communicating this clearly enough.  The 13th, 14th, and 15th workers are assigned Pink color until they are assigned to a team at the 16 worker transition to Speed Mining.  They are given Team Labels of Y4/B4/S4 for the 16th worker transition to Speed Mining.  The 13th, 14th, and 15th workers are assigned Pink color until they are assigned to a team at the 16 worker transition to Speed Mining.  They are given Team Labels of Y4/B4/S4 for the 16th worker transition to Speed Mining.
The transition refers to the condition where we are in between 3 worker teams and 4 worker teams. During this transition the 13th, 14th, and 15th workers are pink to help the user identify the multi team helpers. They get the labels for 16 worker assignments that they wiill transition into once the 16 worker consition is met.

During the transition from 2 worker teams to 3 worker teams, the entire team changes color to the 12 worker start color one team at a time.  
The first team to transition is Green to Teal, then Orange to Yellow, then Perple to Salmon, then Red to Blue.

### 4. 16-Worker Speed Mining Transition Corrected Team Assignments Pink Workers
- **Worker 13**: Team Salmin S4 (Pink) → after mining SB+TB, switches to speed rules ater the 16 worker transition and will speed mine SB
- **Worker 14**: Team Yellow Y4 (Pink) → after mining YB+BB, switches to speed rules after the 16 worker transition and will speed mine YB
- **Worker 15**: Team Blue  B4 (Pink) → after mining SA+BA, switches to speed rules after the 16 worker transition and will speed mine BB
- **Worker 16**: Team Teak T4 (Teal) → after mining BB, becomes Speed Mining




- ## Strict Rules
1. **Prioritize BabyShark**: List my custom scripts and implementation files first.
2. **Filter Out Base Framework**: Completely ignore files from the cloned `sharknice/Sharky` dependency tracking unless a specific class is critically broken or directly referenced by my task.

