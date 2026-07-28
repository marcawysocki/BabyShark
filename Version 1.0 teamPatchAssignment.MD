Version 1.0 Initial Mining Worker Patch Assignment. (teamPatchAssignment)
Team 1: W1,W2,W3 on N1 and F1.  W1 is F1 secondary Miner. Closest between W2,W3 starting position to N1 harvest point is N1 primary Miner. Remaining is F1 Primary.
Team 2: W4,W5,W12 on N2 and F2.  W12 is N2 Secondary. Closest between W4,W5 to N2 is Primary, Remaining is F2 Primary.
Team 3: W6,W7,W11 on N3 and F3.  W11 is N3 Secondary. Closest between W6,W7 to N3 is Primary, Remaining is F3 Primary.
Team 4: W8,W9,W10 on N4 and F4.  W10 is N4 Secondary. Closest between W8,W9 to N4 is Primary, Remaining is F4 Primary.

Version 1.0 Just In Time Mining:
This Just In Time Mining (jitMining) version is focused on 12 workers and creating a spawning pool as fast as posible. 3 Workers per 2 Mineral Nodes.  After each worker returns cargo, it changes minerals.  For instance, the first worker returns cargo from N1, W1 was waiting to start harvesting N1 and now gets it turn. The returning worker now gets assigned to F2.  The worker that had been harvesting on F2 should complete havesting while the new F1 harvester is approaching. Now we get the first return cargo from F1. That worker now gets assigned to N1. As it approaches N1, a worker should be completing harvest of N1.  The cycle repeats for Team 1.

Likewise, each team returns cargo from their assigned mineral nodes and then rotates between their mineral pair.  Every worker alternates between harvesting a Far patch and a Near patch.

Sharky Worker Patch Assignment needs to be replaced with the team method.  Sharky Mining manager needs to be replaced with a JIT Mining Manager.

Sharky Collision Manager might work but may need to be replaced if it can not handle workers going to a different mineral patch.

The Build order system will need a jitPreposition Service that only selects a worker that has just returned cargo from the team that is closest to the build location.  The Mining cycle, in addition to swapping Minerals, also needs to listen for build instructions. In this case, we are trying to set a new world record for a spawning pool.  The pool will be near team 4, next to V2. The last of team 4 (W8,W9,W10) to return cargo and then move to the spawning pool location, Just In Time, for the Mineral total to reach 200.
A Build needs to be created that uses my location for the spawning pool, prepositions the most optimal worker from team 4, Just In Time to build a Spawning Pool. 

Versions beyound 1.0 need to account for a different count of workers and exhasted minerals.  Every variation of mineral node count and worker count has a different pattern.

I would like this done in stages, not all at once. Start with worker assignment only.  Once this tests out then I will give you the next instruction. The closest worker is already coded, also the harvest and return points.