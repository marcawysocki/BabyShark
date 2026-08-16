MiningTarget assignments.md
Add boolean abSwitch to MiningTarget class list

if 12 worker all assignments abSwitch = true
	No changes in assignments
	For each team
	"1","3" targets "A" first then "B"
	"2" targets "B" first then "A"
	each worker has two mineral targets

if 8 worker
	If M[0] is Near, W1 = T1, W2 = T2 
	If M[0] is Far,	 W1 = T2, W2 = T1


	T1 assignment:	
		target 0 = TA, abSwitch = false,
		target 1 = TA, abSwitch = true, //Mine the same mineral twice then engage abSwitch
		target 2 = SA, abSwitch = true,
	T2 assignment:
		target 0 = TB, abSwitch = false, //Mind the "B" once and then switch to the "A" 
		target 1 = TA, abSwitch = true, //Mine the same mineral twice then engage abSwitch
		target 2 = SA, abSwitch = true,

	If M[2] is Near, W3 = S1, W4 = S2 
	If M[2] is Far,	 W3 = S2, W4 = S1


	S1 assignment:	
		target 0 = SA, abSwitch = false,
		target 1 = SA, abSwitch = true, //Mine the same mineral twice then engage abSwitch
		target 2 = TA, abSwitch = true,
	S2 assignment:
		target 0 = SB, abSwitch = false, //Speed Mine the "B" until Build creates more workers that can form a team

	If M[4] is Near, W5 = B1, W6 = B2 
	If M[4] is Far,	 W5 = B2, W6 = B1

	B1 assignment:	
		target 0 = BA, abSwitch = false,
		target 1 = BA, abSwitch = true, //Mine the same mineral twice then engage abSwitch
		target 2 = YA, abSwitch = true,
	B2 assignment:
		target 0 = BB, abSwitch = false, //Speed Mine the "B" until Build creates more workers that can form a team
	
	If M[6] is Near, W7 = B1, W8 = B2 
	If M[6] is Far,	 W7 = B2, W8 = B1

	Y1 assignment:	
		target 0 = YA, abSwitch = false,
		target 1 = YA, abSwitch = true, //Mine the same mineral twice then engage abSwitch
		target 2 = BA, abSwitch = true,
	Y2 assignment:
		target 0 = YB, abSwitch = false, //Mind the "B" once and then switch to the "A" 
		target 1 = YA, abSwitch = true, //Mine the same mineral twice then engage abSwitch
		target 2 = SA, abSwitch = true,
			
If ABswitch was false and becomes true then pop the first false entry off the list so that only 2 remain, the purpose is to find a free mineral for frame 0 assignment and then create a team for more Efficient resource gathering  