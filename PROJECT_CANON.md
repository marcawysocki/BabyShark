# BabyShark Project Canon

This file contains confirmed design rules for BabyShark's 12-worker mining system. It is intentionally narrower than the historical Markdown archive. When older documentation conflicts with this file, stop and discuss the conflict before changing worker-assignment behavior.

## Authority and editing boundaries

- Live C# source under `BabySharkBot/` is the implementation authority.
- This file is the authority for the confirmed 12-worker mining intent.
- `Sharky/` is upstream and protected. Do not modify it without explicit file-specific authorization.
- Do not replace the existing mining architecture with a simplified example or create duplicate worker/team services.
- Do not implement unresolved rules by guessing. Add a focused discussion or test fixture first.

## Worker-chain invariants

- The initial W labels are temporary ordering labels, not final team roles.
- The 12-worker chain is generated from worker geometry relative to the mineral center of mass.
- `W12` is the worker farthest from the mineral center of mass; `W1` is at the opposite end.
- The map may be rotated or mirrored. Screen directions such as left, right, top, and bottom have no semantic meaning.
- The greedy mineral chain is orientation-independent:
  - `M[0]` is on the same chain side as `W1`.
  - `M[7]` is on the same chain side as `W12`.
- Never add screen-direction or absolute-X/Y rules to infer worker or mineral roles.
- Do not reorder the greedy mineral chain during worker assignment.

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
Teal:   W2, W3, W4
Salmon: W5, W6, W1
Blue:   W7, W8, W12
Yellow: W9, W10, W11
```

Final labels are role labels, not copies of W labels:

```text
Teal:   T1, T2, T3 -> TA/TB
Salmon: S1, S2, S3 -> SA/SB
Blue:   B1, B2, B3 -> BA/BB
Yellow: Y1, Y2, Y3 -> YA/YB
```

The role suffix is determined by the worker's target and team geometry. It must not be assigned solely from the worker's W number.

## Confirmed route-aware assignment rules

- The objective is the best complete team route, not the nearest worker to one mineral.
- Evaluate route length, target arrival timing, route crossing, congestion, collision risk, and access to the team's B mineral.
- A slightly longer individual route is correct when it prevents cross traffic or keeps the B-mineral route open.
- Large-mineral assignment is therefore a constrained team optimization problem.

For the confirmed outside-mineral Teal case:

```text
W2 -> T2 -> TB
W4 -> T1 -> TA
W3 -> T3 -> push/support T1
```

The reason is that W2 has the clean route to the outside/far TB mineral, W4 is the best primary worker for TA, and W3 is the remaining push/support worker. This is not a fixed W-number suffix rule; it is the result of the route geometry.

## Fixed middle-chain targets

The following greedy-chain targets are confirmed:

```text
W5 -> M[2]
W6 -> M[3]
W7 -> M[4]
W8 -> M[5]
```

These targets are retained to avoid cross-traffic congestion. Each worker's final `1` or `2` suffix is derived from whether its target is that team's A or B mineral.

## CCA rules for the first 35 frames

- CCA/chrisCrossAppleSause owns the opening choreography through the frame-35 handoff.
- Teal `T1` and `T3` must remain side by side for push-repell acceleration.
- Yellow `Y1` and `Y3` must remain side by side for push-repell acceleration.
- Teal and Yellow role selection must preserve those adjacent 1/3 formations before optimizing their 2 worker.
- Salmon and Blue CCA pushing is always disabled.
- `S1`/`S2` and `B1`/`B2` behave like the 8-worker direct-mining opening.
- `S3` moves toward `BA` and `B3` moves toward `SA` using ordinary target movement. They provide indirect push-repell assistance to Teal and Yellow; they are not Salmon/Blue bump-pair members.
- Do not enable Salmon or Blue bump flags as a shortcut for missing assignments.

## Unresolved rules: do not guess

The following rules are not yet canonical and require discussion before implementation:

- The complete conditional role mapping for all outside-mineral configurations, especially Yellow/M8 cases.
- The exact algorithm and weights for route-crossing, congestion, and arrival-time scoring.
- The exact precedence and threshold combining Townhall distance and mineral size.
- The exact A/B pairing of every ordered mineral index for every team.
- Post-frame-35 role rotation details.
- Whether the historical triple-bump variants remain enabled or should be removed.

Until these are confirmed, implement only shared infrastructure, diagnostics, validation, and tests that do not choose among the unresolved alternatives.
