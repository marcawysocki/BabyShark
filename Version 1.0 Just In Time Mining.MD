Version 1.0 Just In Time Mining.MD

Working Directory: C:\Users\marca\source\repos\BabyShark
Do not processes zip files

jit BaseDtos.cs
CcaManager.cs
BabySharkMiningManager.cs

This Just In Time Mining (jitMining) version is focused on 12 workers and creating a spawning pool as fast as posible. 3 Workers per 2 Mineral Nodes.  After each worker returns cargo, it changes minerals.  For instance, the first worker T1 returns cargo from TA, T3 was waiting to start harvesting TA and now gets it turn. The returning worker now gets assigned to TB after returning cargo at a jit return cargo point.  The worker T2 that had been harvesting on TB should complete havesting while the T1 harvester is approaching. When workers are holding cargo than need to be sent to a jit-return cargo point that is the shortest path for alternating between minerals A and B.  The cycle repeats for Team 1.

Likewise, each team returns cargo from their assigned mineral nodes and then rotates between their mineral pair.  Every worker alternates between harvesting a "B" Far patch and a "A" Near patch.

Sharky Worker Patch Assignment needs to be replaced with the team method.  Sharky Mining manager needs to be replaced with a JIT Mining Manager.

Sharky Collision Manager might work but may need to be replaced if it can not handle workers going to a different mineral patch.

The Build order system will need a jit Preposition Service that only selects a worker that has just returned cargo from the team that is closest to the build location.  The Mining cycle, in addition to swapping Minerals, also needs to listen for build instructions. In this case, we are trying to set a new world record for a spawning pool.  The pool will be near team 4, next to V2. The last of team 4 (Y1,Y2,Y3) to return cargo and then move to the spawning pool location, Just In Time, for the Mineral total to reach 200.
A Build needs to be created that uses my location for the spawning pool, prepositions the most optimal worker from team 4, Just In Time to build a Spawning Pool. 

Versions beyound 1.0 need to account for a different count of workers and exhasted minerals.  Every variation of mineral node count and worker count has a different pattern.  Anything other than 3 for a team uses traditional Sharky Speed Mining. A worker mines the same mineral node and returns straight back to the townhall
