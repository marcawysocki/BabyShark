# JIT Mining Error Notes

## Root Cause
The `NullReferenceException` in `MiningDefenseService.Run()` is not just a local null check issue. It happens because Sharky’s mining defense code assumes its own base model is fully populated and stable during combat micro.

In this project, custom base data and custom mining behavior mean that assumption is false:
- `BaseLocation` records may not have Sharky-generated mineral-line metadata.
- `SelfBases` can shrink or change when an expansion is lost.
- Workers can remain assigned to a dead or invalid base during long-distance mining.
- Mining defense can still try to retreat those workers using `otherBase.MineralLineLocation`, which may be missing or stale.

## Immediate Failure Point
The exception is triggered when Sharky tries to retreat a worker using a retreat target derived from an invalid base location.

## Fix Direction for JIT Mining
For the JIT Mining rewrite, do not depend on Sharky’s mining-defense fallback paths for worker survival.

Required changes:
- Build and own the worker assignment state in the JIT mining system.
- Clear or reassign workers immediately when their base is destroyed or invalidated.
- Treat mineral-line points as optional unless your pipeline explicitly creates them.
- Add a safe fallback retreat point for workers that lose their expansion.
- Keep battle micro separate from mining-state assumptions.

## Migration Rule
If a worker loses its expansion while long-distance mining, the new system must:
1. Detect the invalid base.
2. Remove the worker from the old mining assignment.
3. Reassign it to a valid mineral target or safe fallback.
4. Avoid calling Sharky retreat logic with stale base geometry.

## Practical Guard
Any future retreat call should verify:
- the base exists,
- the base has a valid mineral-line point,
- the worker is still assigned to a live base,
- and a fallback point exists if the main target does not.

## Summary
This error is a data lifecycle problem, not only a null dereference. The fix belongs in the mining system design: JIT Mining must own worker state and base validity instead of relying on Sharky’s default expansion model.
