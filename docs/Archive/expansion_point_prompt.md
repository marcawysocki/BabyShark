# GitHub Copilot Prompt — Expansion Townhall Placement

## Goal
Generate C# code that computes the **actual legal townhall placement point** for each expansion cluster.

This is the real TC placement location.
It is not:
- the mineral COM
- a point near one mineral
- a floating debug marker
- a hardcoded offset
- a guessed location

The result must follow StarCraft II townhall placement rules.

## Required Inputs
Use only these inputs:
- mineral node positions for the expansion cluster
- geyser positions for the same expansion cluster
- precomputed mineral cluster center
- SC2APIProtocol `placement_grid`
- SC2APIProtocol `pathing_grid`
- SC2APIProtocol `playable_area`
- townhall footprint = 5x5
- existing Sharky services and data structures

## Required Output
Generate:
- `ExpansionPointService.cs`
- `ExpansionPointModel.cs`

The code must be:
- deterministic
- map-independent
- compile-ready
- compatible with Sharky
- correct for real townhall placement

## Core Placement Rule
The townhall placement point must be the **best legal TC location** for the expansion.

Do not:
- place it at the mineral COM
- place it at a random point near the base
- place it near only one mineral
- place it above the COM for visibility
- use a floating Z value to make it show up
- invent placements without checking legality

## Expansion Scoring Rule
For each candidate point around the expansion:
- reject if the 5x5 footprint is not pathable
- reject if the footprint is not inside playable area
- reject if too close to any mineral
- reject if too close to any vespene
- score legal candidates using the full resource cluster geometry
- prefer the point that best fits the actual expansion layout

## Contested Base Rule
Some bases are contested. Not all bases are contested.

A base is contested when:
- there are two different legal TC placements near the same expansion
- both placements are within 6 tiles of the COM
- both placements have nearly identical scores
- the map geometry supports multiple valid placements, such as north/south or east/west

If the base is contested:
- return both valid placements
- do not invent extra placements
- do not force one fake answer
- do not mark every base as contested

## Placement Validation Rules
The final TC placement must satisfy all of these:
- 5x5 footprint
- all footprint tiles are pathable
- all footprint tiles are inside playable area
- no overlap with minerals
- no overlap with vespene
- workers can reach the base from the resources
- the point is legal for a townhall

## Z Coordinate Rule
The visualized TC placement must use the **actual Z for that expansion’s terrain**.

Do not:
- use Z = 12
- use any hardcoded floating Z
- place the dome above the COM
- fix visibility by moving the object upward

The green dome must be drawn at:
- the actual townhall placement X/Y
- the actual terrain Z for that expansion

## Debug Visualization Rule
Generate debug overlays for:
- mineral nodes
- geysers
- mineral COM
- candidate points
- valid TC point
- rejected candidate points
- footprint outline

Debug drawing must use the actual placement point, not a fake floating point.
All Drawing objects should follow DRAWING_PATTERN_GUIDE.md rules.

## Spiral Search Rule
If the best point is invalid:
- search outward deterministically
- radius 0 to 6
- step size 0.25
- stop at the first legal point

## Must Ask Before Assuming
If any required placement rule, map detail, or data source is unclear, ask before inventing a solution.

Do not:
- guess the TC location
- guess the contested-base rule
- guess the Z coordinate
- guess based on one resource
- replace missing data with a random fallback unless the prompt explicitly allows it

If the algorithm cannot prove a point is legal, mark it invalid and search again.

## Invalid Output Examples
These are wrong and must not be generated:
- a point chosen only from one mineral
- a point placed at the mineral COM
- a point floating above the base
- a point with hardcoded Z = 12
- a point that ignores pathability
- a point that ignores the 5x5 footprint
- a contested result invented for every base
- a point that is not the real TC location

## TC Placement Acceptance Checklist
Before returning a townhall point, confirm all of these:
- X/Y is the actual TC placement location
- X/Y is not the mineral COM
- the 5x5 footprint is legal
- the point is pathable
- the point is inside playable area
- the point does not overlap minerals
- the point does not overlap vespene
- the point uses the correct terrain Z
- the point follows the actual expansion geometry
- the point is not floating in the air

## Integration Rules
Integrate with Sharky using the existing service pattern.

If additional data is required and not available, ask before inventing anything.

## Final Output Format
The code must output:
- `ExpansionPointService.cs`
- `ExpansionPointModel.cs`

## Prompt to Copilot
Use this exact instruction:

> Generate the full implementation for ExpansionPointService and ExpansionPointModel. The code must compute the actual legal townhall placement point for each mineral cluster using SC2APIProtocol placement_grid, pathing_grid, playable_area, and the full expansion resource geometry. The algorithm must be deterministic, must correctly detect contested bases, must validate the 5x5 footprint, and must not place debug markers floating above the COM.

