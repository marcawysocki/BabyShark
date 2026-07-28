Version 1.0 Initial Mining Worker Patch Assignment. (teamPatchAssignment)



My Mining Manager needs to be a Learning Manager that learns and records what it learns.

On Start should check for what it already knows. If it needs to run InitialMapData.cs it does that.  It should also prepare anything needed for RLMatrix.

Service chrisCrossAppleSause.cs RLMatrix Learning Service or class for any new base, expansion, or mineral Wall or vespine Geyser mining.  The first Test will be for start location zero initial worker formation   

From Start[0 to 3] Initial worker formation:

On Frame, Calculate Half the distance from each "A" Mineral and Each "1" worker.  Issue a Move command to that worker to move to the calulated point. Worker "1" contines to move until it reaches the harvest circle around the "A" mineral.  Additional cycles may change the angle and move to point as we learn the benifit and results of bumping, a straight line may not be the fastest route.

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



