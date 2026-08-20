# BabyShark Project Canon

This file contains confirmed design rules for BabyShark's 12-worker mining system. It is intentionally narrower than the historical Markdown archive. When older documentation conflicts with this file, stop and discuss the conflict before changing worker-assignment behavior.

## Authority and editing boundaries

- Live C# source under `BabySharkBot/` is the implementation authority.
- This file is the authority for the confirmed 12-worker mining intent.
- `Sharky/` is upstream and protected. Do not modify it without explicit file-specific authorization.
- Do not replace the existing mining architecture with a simplified example or create duplicate worker/team services.
- Do not implement unresolved rules by guessing. Add a focused discussion or test fixture first.

## Worker and mineral chain invariants

- The initial W labels are temporary ordering labels, not final team roles.
- The canonical worker list is stored in greedy chain order from W1 through W12 (or W1 through W8 for an 8-worker opening). The numeric debug prefix is the list index: `1-W1` through `8-W8`.
- The 12-worker chain is generated from worker geometry relative to the mineral center of mass.
- `W12` is the worker farthest from the mineral center of mass; `W1` is at the opposite end.
- For an 8-worker opening, `W8` is the worker farthest from the mineral center of mass; `W1` is at the opposite end.
- The map may be rotated or mirrored. Screen directions such as left, right, top, and bottom have no semantic meaning.
- The greedy mineral chain is orientation-independent and is the only mineral ordering.
- The live mineral labels are displayed and referenced in descending chain notation: `M[8]` through `M[1]`.
  - `M[1]` is the Teal-side chain edge and Teal uses the `M[1]/M[2]` side.
  - `M[8]` is the Yellow-side chain edge and Yellow uses the `M[7]/M[8]` side.
  - Current labels such as `1-TA`, `1-TB`, `8-YA`, and `8-TB` are authoritative assignment references.
- Historical `M[0]` through `M[7]` examples and the former one-based persisted-index description must not be used to reinterpret the live labels. The implementation must resolve the current registered mineral labels and preserve their mapping.
- `W12` through `W1` always means the correctly sorted greedy worker chain; for 8-worker starts, `W8` through `W1` means that same correctly sorted chain.
- `M[8]` through `M[1]` always means the correctly sorted current mineral-label chain, never the order returned by Observation.
- Observation raw-unit, dictionary, or availability enumeration order is never semantic and must never define a worker number, mineral index, team, color, or target.
- Never add screen-direction or absolute-X/Y rules to infer worker or mineral roles.
- Do not reorder or mutate the greedy mineral chain during worker assignment.

## Mineral classification invariants

- A base has four large mineral nodes and four smaller mineral nodes.
- Near/Far classification is not simply the four closest versus four farthest nodes.
- Townhall distance and mineral size both participate in classification.
- At least two large minerals are commonly nearer to the Townhall.
- A large center mineral can be farther by distance than another node and still be classified as Near because its size gives it strategic priority.
- Worker assignment must not rename, recolor, reorder, or otherwise mutate mineral identity.

## 12-worker team membership

The confirmed initial W-chain membership is:

```text
Teal:   W2, W3, W4   # W1-side team; minerals M[1], M[2]
Salmon: W5, W6, W1
Blue:   W7, W8, W12
Yellow: W9, W10, W11 # W12-side team; minerals M[7], M[8]
```

Every team uses the same mineral suffix rule: the large mineral is `A` and the small mineral is `B`. Teal uses the `M[1]/M[2]` side and Yellow uses the `M[7]/M[8]` side. The live numeric display prefix follows the current mineral labels, including `1-...` through `8-...`; it must not be regenerated from Observation order or from a stale historical index convention.

Final labels are role labels, not copies of W labels:

```text
Teal:   T1, T2, T3 -> TA/TB
Salmon: S1, S2, S3 -> SA/SB
Blue:   B1, B2, B3 -> BA/BB
Yellow: Y1, Y2, Y3 -> YA/YB
```

The role suffix is determined by the worker's target and team geometry. It must not be assigned solely from the worker's W number.

## Confirmed 12-worker assignment rules

- The objective is the complete team route, not nearest-worker matching in isolation.
- Assignment must use the current live mineral labels (`M[8]` through `M[1]`) and their registered `A`/`B` identities.
- The 12-worker CCA opening uses the same assignment choreography as the correct 8-worker opening, but all CCA bumping/pushing is disabled.
- CCA emits calculated `MOVE` commands at frames `0`, `1`, `5`, `10`, and `15`; frame 1 repeats the initial movement command if the frame-0 command was not accepted by the game loop.
- A `SMART` command is expected before the frame-35 handoff when the worker and mineral unit tag are verified. The exact issuing frame and queue semantics remain an implementation detail to confirm from live code.

### Salmon

```text
W1 -> S3 -> either 3-SA or 4-SA
```

`W1` becomes `S3`. The selected target is whichever of the current `3-SA` or `4-SA` labels is the team's `A` mineral.

### Teal

Teal membership is `W2`, `W3`, and `W4`.

```text
If M[1] is 1-TB:
    W2 -> T2 -> 1-TB
    Choose T1 as the nearer of W3/W4 to 2-TA
    The remaining Teal worker -> T3 (pushing/support)

If M[1] is 1-TA:
    W4 -> T2 -> 2-TB
    Choose T1 as the nearer of W2/W3 to 1-TA
    The remaining Teal worker -> T3 (pushing/support)
```

`T3` is the Teal pushing/support worker. For this 12-worker opening, its bumping behavior is disabled; it later mines `TA` after completing its support movement.

### Fixed middle-chain assignments

```text
W5 -> M[3]
W6 -> M[4]
W7 -> M[5]
W8 -> M[6]
```

Salmon and Blue pushing/bumping pairs are disabled. These workers retain their fixed current-chain mineral targets.

### Yellow

Yellow membership is `W9`, `W10`, and `W11`.

```text
If M[8] is 8-TB:
    W11 -> Y2 -> 8-TB
Else:
    W9 -> Y2 -> the Yellow B mineral
```

The nearest of the remaining Yellow workers to `YA` becomes `Y1`. The other remaining Yellow worker becomes `Y3` and is the pushing/support role. As with Teal, Yellow bumping is disabled for this 12-worker opening; `Y3` later mines `YA` after support movement.

### Blue edge worker

```text
W12 -> B3
```

`W12` becomes `B3`. Blue bumping is disabled.

These rules are explicit role mappings for the current 12-worker opening and supersede the older sequential Team 1–4 assignments.

## Fixed middle-chain targets

The following greedy-chain targets are confirmed:

```text
W5 -> M[3]
W6 -> M[4]
W7 -> M[5]
W8 -> M[6]
```

These targets are retained to avoid cross-traffic congestion. Each worker's final `1` or `2` suffix is derived from whether its target is that team's A or B mineral.

## CCA rules for the first 35 frames

- CCA/chrisCrossAppleSause owns the opening choreography through the frame-35 handoff.
- The 12-worker opening follows the same CCA assignment and movement choreography as the correct 8-worker opening.
- For the 12-worker opening, bumping/pushing is false. Support workers still follow their calculated movement paths and later receive their mining target.
- Opening mineral `MOVE` targets use the persisted radius-safe `HarvestPoint`, outside the mineral footprint.
- Calculated `MOVE` commands are issued at frames `0`, `1`, `5`, `10`, and `15`; frame 1 repeats the initial movement command.
- A verified mineral-target `SMART` command is expected before the frame-35 handoff. A world-position-only `SMART` is not a valid mining handoff. The exact command queue/timing must be confirmed against the live implementation before it is treated as a code invariant.
- Teal role selection must preserve the `T1`/`T3` support formation while selecting `T2` and `T1` using the current `M[1]` condition.
- Yellow role selection must select `Y2`, the nearest `Y1`, and the remaining `Y3` using the current `M[8]` condition.
- Salmon and Blue bumping/pushing pairs are false. `W1` is `S3`, and `W12` is `B3`.
- Do not enable any bump flag as a shortcut for missing assignments.

## Confirmed opening scope and unresolved rules

The 8-worker opening behavior is currently considered correct and must be preserved. The new 12-worker rules above define the current CCA assignment contract, but the following details still require live-code confirmation or separate discussion:

- The exact implementation of the `M[8]`/`M[1]` label lookup and the mapping from displayed labels to persisted DTO entries.
- The exact algorithm and weights for route-crossing, congestion, and arrival-time scoring where the explicit conditional rules do not decide the worker.
- The exact precedence and threshold combining Townhall distance and mineral size.
- The exact A/B pairing of ordered mineral indices outside the explicitly documented Teal, Salmon, Blue, and Yellow cases.
- The exact SMART issuing frame, queue behavior, and whether SMART is issued once or repeated before frame 35.
- Post-frame-35 role rotation details.
- Whether any historical triple-bump variant should be enabled; the current 12-worker CCA contract says bumping is false.

Until these are confirmed, implement only the explicit assignments, shared infrastructure, diagnostics, validation, and tests. Do not select among unresolved alternatives by fallback or guesswork.
