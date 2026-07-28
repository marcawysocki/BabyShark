# Morning Session Game Plan

1. **Lock the data model**
   - Store per map and per spawn:
	 - original worker labels `W12 -> W1`
	 - final team labels `T/S/B/Y`
	 - mineral positions and final mineral team labels
	 - spawn flags like `TealM1IsTA`

2. **Verify the team schema**
   - Confirm:
	 - Teal: `W2/W3/W4`
	 - Salmon: `W5/W6/W1`
	 - Blue: `W7/W8/W12`
	 - Yellow: `W9/W10/W11`

3. **Implement initial mining state**
   - Add a manager/state for the opening push only.
   - It should:
	 - move workers to their assigned opening points
	 - handle bump / acceleration
	 - stop once workers are on patch or waiting to mine

4. **Define handoff conditions**
   - Transition to steady-state mining when:
	 - all opening workers are in position
	 - first mining cycle is active or queued

5. **Wire per-team geometry**
   - Set up the four common bump lines.
   - Make line fractions configurable per team.

6. **Keep ML disabled**
   - Use deterministic rules only.
   - Save learning for a later release.

7. **Test one team first**
   - Start with Teal.
   - Validate:
	 - target mineral selection
	 - T1/T2/T3 assignment
	 - midpoint and quarter-point movement
	 - handoff timing

8. **Expand to the other teams**
   - Add Salmon, Blue, then Yellow.
   - Confirm the same structure works across maps.

9. **Clean up legacy logic**
   - Remove old assignment paths that conflict with the new schema.
   - Keep the new system as the only source of truth.
