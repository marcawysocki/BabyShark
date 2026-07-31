# Qwen File-Mapping Directive 

## Objective
Analyze the codebase structure. Identify the necessary files required to execute our current development tasks. Focus on Why workers are not working as Expected   Three of the teams are not mining at all. only team   Purple or salmon are working The other workers on the other  are just standing there. 

A possibility is that worker collision is causing this problem.  See MineralWalking.MD    

Teams Refer to the worker mining assignments. The starting base has eight mineral nodes and we have either 8 worker start or a 12 worker start. for a 12 worker start the teams have three workers apiece. On an 8 worker start we only start with two for the Teams. each of the teams has an "A" mineral and "B" the "A" refers to minerals that are large and close and the "B" mineral is a mineral node that's a little further away from the Town Center.  Teams are very important for JIT Just in time mining because they They juggle between the A and B minerals.

Additionally there is a couple of problems with the eight worker start. For JIT you need three workers on a in BaseDtos There are two different sets of points one is the JIT and the others were normal speed Mining. In normal speed mining the worker does not change It's mineral assignment It mines the same mineral over and over and again  So each point are straight to the mineral and straight back  With JIT the workers are alternating minerals so their is the shortest distance to travel  For the return cargo and then going to the other mineral to harvest This is why there are two different sets of points for the worker depending on whether or not they have three on their team.

 On an 8 worker start  There is a simpler  simpler The workers Assignment. The workers    started out with a label W12 through W1  or W8 Through W1. the minerals start in an array M[8] to M[1] If the  Mineral assigned to the worker matches the number for instance W8 would match up with M[8] W7-M[7] on down to W1-M[1] Currently The position of the A or B mineral causes the wrong worker to be assigned to the A or B Mineral.         

## Strict Rules
1. **Prioritize BabyShark**: List my custom scripts and implementation files first.
2. **Filter Out Base Framework**: Completely ignore files from the cloned `sharknice/Sharky` dependency tracking unless a specific class is critically broken or directly referenced by my task.

