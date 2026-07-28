# Mining Assignment Prompt

## Goal
Update the mining assignment logic so mineral labeling stays consistent, while worker assignment uses the new smHarvestPoint and smReturnPoint points.

## Core Rules
- Do **not** change the mineral order.
- Do **not** change the greedy worker order.
- Do **not** rename or relabel minerals based on worker assignment.
- Mineral labels are determined only from the mineral chain and the mineral's position/tag data.
- Worker assignment uses the mineral geometry, but it must not hijack mineral labels.

## Geometry
For each mineral, there is already:
- a `HarvestPoint`
- a `ReturnPoint`

Add two new points on the straight line between the mineral and the Hatchery:
- `smHarvestPoint` = 1 unit closer to the mineral than `HarvestPoint`
- `smReturnPoint` = 1 unit closer to the Hatchery than `ReturnPoint`

Both points must stay on the same line.

## Label Colors for Minerals
Use the existing mineral bands and map them to color letters:
- Near / teal color -> `T`
- Magenta color -> `M`
- Blue -> `B`
- Yellow -> `Y`

Do not change the mineral order when applying these colors.

## Worker Assignment Rules
Use the worker tag to change the worker label.
The worker label is the thing that changes; the mineral label does not change.

### Pass 1
Assign the 4 workers closest to the combined Near / Large mineral `smHarvestPoint` targets.
These workers get:
- `T1`
- `M1`
- `B1`
- `Y1`

### Pass 2
Assign the remaining workers closest to the Large minerals that are **not Near**.
These workers complete the first group using the same `1` suffix logic when needed.

### Pass 3
Assign 4 workers to the 4 Far minerals using the Far mineral `smHarvestPoint`.
These workers get:
- `T2`
- `M2`
- `B2`
- `Y2`

### Pass 4
The final 4 workers do not start mining yet.
They wait at the first group's harvest area and become the third group.
These workers get:
- `T3`
- `M3`
- `B3`
- `Y3`

## Important Clarification
This is **not** a wave of 12 workers.
This is **3 groups of 4 workers**:
- Group 1: 4 workers for Near / Large minerals
- Group 2: 4 workers for Far minerals
- Group 3: 4 waiting workers

## Logging Requirement
Write a console log when a worker label changes.
Example:
- `Worker Initial Mining Assignment: W3 has been changed to T1`

## Expected Outcome
After this change:
- mineral labels remain consistent
- worker labels reflect the correct mineral group
- the color letter on the worker matches the mineral color band
- worker assignment uses `smHarvestPoint` and `smReturnPoint` for routing decisions

## Do Not
- Do not reorder minerals
- Do not sort collections after the fact
- Do not rename minerals in the worker assignment step
- Do not let worker assignment overwrite the mineral label