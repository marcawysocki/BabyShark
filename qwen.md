# Qwen File-Mapping Directive 

## Objective

This is a three AI Work through. The local AI Analyzes my code page to create a KimiK3 files. It should Output for Kimi K3 Aware Text.  When Kimi Analyzes frpm My local AI it should output  suitable for "Coder"  Accio Work's AI assistant then "Coder"  Accio Work's AI assistant integrates The output of KimiK3 back into my system.

Analyze the codebase structure. Identify the necessary files required to execute our current development tasks.  

Any Questions that need to be clarified should be listed in your reasoning, the user will read that and rewrite the promt as necessary. 


First Task.  BuildIne.cs Should create drones until DesiredUnitCount ZERG_DRONE has been reached See chrisCrossAppleSause 
commands.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = morphCmd } });
Console.WriteLine($"chrisCrossAppleSause [{state.SpawnKey}]: Morphing larva {larva.Tag} into drone on frame 0");

Second Task. When a larva morphs into a drone it can join the teams. It will need a worker label. This needs to be reusable code that New build orders can run. 

see TeamPatchMiningTask.cs and TeamLabelRegistrationHelper.cs 

StarCraft II Maps Have multiple starting locations And map configuration for mineral nodes and that's vespine gas '

Include in your reasoning Both the 8 worker starting Teams Green, Orange, Purple, and Red and The 12 worker starting Teams Teal, Salmon, Blue, Yellow
chrisCrossAppleSause should Morph 1 Larva into a Drone for both 12 and 8 worker start, currently it is only 8 worker start.

I Need a table of the teams for instance M[1] and M[2] are Teal Team [T1,T2,T3,T4, TA, TB] With A&B in any order depending on the map. What color does that correspond to in and 8 worker start
Break down all teams and their corresponding colors for both 8 worker start and 12 worker start.

For an 8 worker start The teams will eventually reach the same worker count and functionas a 12 worker start. The new workers 9 thru 12 will join the teams that would have been created in a 12 worker start.

In an 12 worker start the third worker eventually Mines it's teams "A" Mineral. In an 8 worker start those Third workers Morph into a Drone one at a time.

Past 12, we start to create 4 worker teams.

If the current worker count is 8 then the new worker will be will join the team that would have been Teal on a 12 worker start.
The "1","2" existing workers and "A", "B" Mineral will become a three worker team, the workers and Minerals all change to Teal T1,T2, The new Larva that just Morphed into a drone will be T3 and the Minerals will be TA and TB.

If the current worker count is 9 then the new worker will be will join the team that would have been Yellow on a 12 worker start.
The "1","2" existing workers and "A", "B" Mineral will become a three worker team, the workers and Minerals all change to Yellow Y1,Y2, The new Larva that just Morphed into a drone will be Y3 and the Minerals will be YA and YB.

If the current worker count is 10 then the new worker will be will join the team that would have been Salmon on a 12 worker start.
The "1","2" existing workers and "A", "B" Mineral will become a three worker team, 
the workers and Minerals all change to Salmon S1,S2. 
The new Larva that just Morphed into a drone will be S3 and the Minerals will be SA and SB.

If the current worker count is 11 then the new worker will be will join the team that would have been Blue on a 12 worker start.
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
| M[0] + M[1]           | **Teal** T1/T2     | **Green** G1/G2                             |
| M[2] + M[3]           | **Salmon** S1/S2   | **Orange** O1/O2                            |
| M[4] + M[5]           | **Blue** B1/B2     | **Purple** P1/P2                            |
| M[6] + M[7]           | **Yellow** Y1/Y2   | **Red** R1/R2                               |

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
Salmon → Orange   (second pair)
Blue   → Purple   (third pair)
Yellow → Red      (fourth pair)
```

## Strict Rules
1. **Prioritize BabyShark**: List my custom scripts and implementation files first.
2. **Filter Out Base Framework**: Completely ignore files from the cloned `sharknice/Sharky` dependency tracking unless a specific class is critically broken or directly referenced by my task.

