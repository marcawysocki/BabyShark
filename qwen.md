# Qwen File-Mapping Directive 

## Objective

This is a three AI Work through. The local AI Analyzes my code page to create a KimiK3 files. It should Output for Kimi K3 Aware Text.  When Kimi Analyzes frpm My local AI it should output  suitable for "Coder"  Accio Work's AI assistant then "Coder"  Accio Work's AI assistant integrates The output of KimiK3 back into my system.

Analyze the codebase structure. Identify the necessary files required to execute our current development tasks. 


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


## Strict Rules
1. **Prioritize BabyShark**: List my custom scripts and implementation files first.
2. **Filter Out Base Framework**: Completely ignore files from the cloned `sharknice/Sharky` dependency tracking unless a specific class is critically broken or directly referenced by my task.

