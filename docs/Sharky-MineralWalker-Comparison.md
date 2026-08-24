# Sharky MineralWalker vs BabyShark Mining

## Conclusion

`Sharky/MicroControllers/MineralWalker.cs` is not the source of Sharky's high mineral rate. It is a small helper that issues a mineral-targeted `HARVEST_GATHER` command. The higher-value behavior is in `Sharky/MicroTasks/Mining/MineralMiner.cs`: it chooses between direct mineral walking and queued movement based on distance, route alignment, worker contact, and whether the worker is returning cargo.

BabyShark can adapt that command policy inside `BabySharkBot/Managers/BabySharkMiningManager.cs` while preserving BuildManager-owned worker/mineral assignments. `Sharky/` should remain unchanged.

## What `MineralWalker` actually does

`MineralWalker` provides four unrelated utilities:

- `MineralWalkHome` issues `HARVEST_GATHER` against the first self-base mineral when the commander is more than 3 game units away (`MineralWalker.cs:20-35`).
- `MineralWalkPatch` issues `HARVEST_GATHER` against a supplied mineral tag (`MineralWalker.cs:62-73`).
- `MineralWalkTarget` selects a target mineral near the enemy base and issues `HARVEST_GATHER` (`MineralWalker.cs:75-87`).
- `MineralWalkNoWhere` sends a worker to a mineral at a distraction base (`MineralWalker.cs:89-105`).

This class does not calculate harvest/return points, does not detect path alignment, does not queue a movement route, and does not track mining state. It is therefore not a suitable replacement for BabyShark's unique assignment system.

## Relevant Sharky behavior

`MineralMiner.GatherMinerals` does the following (`Sharky/MicroTasks/Mining/MineralMiner.cs:49-79`):

1. Computes whether the worker is on the mineral-to-dropoff route using `CollisionCalculator.Collides`.
2. Detects nearby blocking non-worker allies.
3. If the worker is too close to the mineral, too far from it, blocked, or off the route:
   - issue immediate `HARVEST_GATHER` on the assigned mineral;
   - queue a `MOVE` to the dropoff point.
4. Otherwise:
   - issue immediate `MOVE` to the harvest point;
   - queue `HARVEST_GATHER` on the assigned mineral.

`MineralMiner.ReturnMinerals` uses the same route-aware idea (`MineralMiner.cs:81-106`):

- If far from the town hall or off-route, issue direct `SMART` to the town hall.
- If close to the town hall, issue `HARVEST_RETURN`, then queue a `MOVE` to the harvest point.
- Otherwise, issue `MOVE` to the dropoff/return point, then queue `SMART` to the town hall.

`UnitCommander.Order` also provides important conflict/spam protection (`Sharky/Unit/UnitCommander.cs:78-146`): one unqueued command per worker per frame, suppression of unchanged orders, equivalence between generic and race-specific harvest abilities, and explicit queue handling.

## Current BabyShark behavior

The active steady-state path is:

- `BabySharkMiningManager.OnFrame` calls `ExecuteJustInTimeMining` after CCA handoff (`BabySharkMiningManager.cs:538-587`).
- `ExecuteJustInTimeMining` returns immediately after `ExecutePushAcceleration` and `ExecuteAssignedWorkerTargets` (`BabySharkMiningManager.cs:918-975`). The older per-team JIT loop below that return is unreachable and does not affect runtime behavior.
- `ExecuteAssignedWorkerTargets` correctly validates the current-spawn assigned worker, current target, live mineral tag, and role-3 gating (`BabySharkMiningManager.cs:1392-1479`).
- However, `AddHarvestSequence` receives the assigned `HarvestPoint` but ignores it and emits only an immediate mineral-target `SMART` (`BabySharkMiningManager.cs:1600-1610`). Thus the persisted geometry is not used for steady-state gathering.
- Cargo return currently emits a queued `MOVE` to the return point followed by queued `HARVEST_RETURN` (`BabySharkMiningManager.cs:1592-1598`), while `IssueCargoReturnSequence` emits `STOP` plus queued `MOVE` and defers the next movement/return continuation (`BabySharkMiningManager.cs:1877-1947`). This is more serialized than Sharky's normal return policy and may add unnecessary transition frames if applied when the worker is already on a valid route.
- The current path constructs raw protocol actions directly rather than using `UnitCommander.Order`, so it does not automatically receive Sharky's same-frame conflict and duplicate-order suppression.

## Most likely performance problem

The biggest mismatch is not mineral assignment. The assignment validation is deliberately strict and should remain that way. The likely throughput loss is the command policy after assignment:

1. Workers are repeatedly given direct `SMART` mineral handoffs instead of a route-aware `MOVE`/queued-gather sequence.
2. The fixed `HarvestPoint` is effectively unused in `AddHarvestSequence`.
3. Return transitions are always treated as a queued multi-stage sequence instead of selecting direct town-hall `SMART` when the worker is far/off-route and a short `MOVE` plus queued handoff when it is near the return point.
4. Raw actions bypass `UnitCommander.Order`'s duplicate/conflict protections, increasing the risk of replacing a useful movement order or emitting conflicting actions in the same frame.
5. The active method contains a large unreachable legacy implementation, making it difficult to know which mining policy is actually being tuned.

## Safe adaptation for unique assignments

Adapt Sharky's **command policy**, not its assignment policy:

### Preserve

- `BabySharkBuildManager` remains the sole owner of worker labels, team membership, target order, A/B switching, and current-spawn target DTOs.
- `BabySharkMiningManager` continues to resolve only the current spawn and verify live worker/mineral tags.
- Role-3 wait behavior and the 12-worker A/B cycle remain unchanged.
- Existing CCA ownership and the protected `Sharky/` directory remain unchanged.

### Add to the active BabyShark path

For each verified assigned target, use the target's existing `HarvestPoint`, `ReturnPoint`, and optionally `SmHarvestPoint`/`SmReturnPoint`:

```text
if worker is carrying:
    if worker is far from townhall or its route is not aligned:
        SMART townhall
    else if worker is close to return point:
        HARVEST_RETURN
        queue MOVE to the assigned harvest point
    else:
        MOVE to assigned return point
        queue SMART townhall
else:
    if worker is too close to mineral, too far from mineral, blocked, or off route:
        SMART/HARVEST_GATHER assigned mineral
        queue MOVE to the assigned return point or route exit
    else:
        MOVE to assigned harvest point
        queue HARVEST_GATHER assigned mineral
```

The exact thresholds should initially match Sharky's existing policy where the geometries have equivalent meaning. Do not introduce nearest-mineral selection, alternate worker selection, or fallback assignment when a verified BabyShark target is missing.

### Collision/path input

The default bot already creates and exposes `CollisionCalculator` (`Sharky/DefaultBot/DefaultSharkyBot.cs:41`, initialized at `:206`). The BabyShark composition root currently creates the `DefaultSharkyBot` proxy, so the manager can receive the shared calculator through its constructor without modifying `Sharky/`. The existing `WorkerAwareCollisionManager` only detects worker pass-through pairs; it is not a substitute for Sharky's route-alignment test.

## Recommended implementation order

1. Replace only the active `AddHarvestSequence` behavior with a verified-target, route-aware gather sequence. Keep assignment lookup and role gating unchanged.
2. Add the same route-aware decision for cargo return, using the assigned worker's exact return/harvest geometry and verified town-hall tag.
3. Ensure at most one immediate command and explicitly queued follow-up commands per worker per frame.
4. Remove or isolate the unreachable legacy JIT block only after behavior is covered by logs/tests; it is not necessary for the first performance change.
5. Run the required builds and compare mineral return-rate logs against the current baseline. The relevant tracker is invoked from `BabySharkMiningManager.OnFrame` (`BabySharkMiningManager.cs:581-584`).

## Important caution

Do not copy `MineralWalker.GetTargetMineralPatch`, `GetEnemyNaturalMineralPatch`, or any first/nearest mineral behavior into steady-state mining. Those methods intentionally select generic or enemy-base targets and conflict with BabyShark's current-spawn, label-driven assignment contract.

## Files inspected

- `Sharky/MicroControllers/MineralWalker.cs`
- `Sharky/MicroTasks/Mining/MineralMiner.cs`
- `Sharky/Unit/UnitCommander.cs`
- `BabySharkBot/Managers/BabySharkMiningManager.cs`
- `BabySharkBot/Managers/BabySharkBuildManager.cs`
- `BabySharkBot/Managers/WorkerAwareCollisionManager.cs`
- `BabySharkBot/BabySharkBot.cs`
- `PROJECT_CANON.md`
- `ARCHITECTURE.md`
- `CONVENTIONS.md`

No source code was changed during this comparison.
