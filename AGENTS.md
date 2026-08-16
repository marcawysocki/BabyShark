# BabyShark Agent Instructions

This repository contains an established StarCraft II bot with behavior that has been built and tuned over time. Treat existing working behavior as a compatibility contract.

## Core rule: new requirements are additive

A new requirement does not cancel, replace, or weaken an existing requirement unless the owner explicitly says so.

Before changing code:

1. Read `README.md`, `CONTRIBUTING.md`, `PROJECT_CANON.md`, `ARCHITECTURE.md`, and `CONVENTIONS.md`.
2. Inspect the current implementation, its call sites, startup lifecycle, and frame lifecycle.
3. Check `git status` and preserve unrelated existing worktree changes. Do not reset, revert, or overwrite changes you did not make.
4. Identify the smallest safe change that satisfies the new requirement without removing established behavior.

If a new request conflicts with an existing rule, stop and report the conflict. Do not resolve it by silently deleting, simplifying, replacing, or disabling the older behavior.

## Source-of-truth hierarchy

Use sources in this order:

1. Live C# implementation under `BabySharkBot/` for current behavior.
2. `PROJECT_CANON.md` for confirmed 12-worker mining invariants and intentional behavior.
3. `ARCHITECTURE.md` for ownership, lifecycle, and manager boundaries.
4. `CONVENTIONS.md` for implementation patterns and type conventions.
5. `CONTRIBUTING.md` for repository and protected-path policy.
6. Historical notes and archived documents only as background. They are not permission to change live behavior.

If documentation disagrees with live code, do not guess. Explain the discrepancy and update the canonical documentation only after the intended behavior is confirmed.

## Protected architecture

- `Sharky/` is copied upstream framework code and is read-only by default.
- Custom behavior belongs under `BabySharkBot/`.
- Do not create duplicate managers, worker-label services, map-data services, or parallel assignment systems.
- Preserve the existing composition root and manager lifecycle unless the requirement explicitly changes lifecycle ownership.
- `InitialMapData` owns static map discovery and generated map data.
- `OngoingMapData` owns current-spawn refresh and assignment lookup.
- `BabySharkBuildManager` owns greedy-chain construction and build execution after startup handoff.
- `CcaManager` and `chrisCrossAppleSause` own opening CCA choreography.
- `BabySharkMiningManager` owns steady-state mining and JIT behavior after handoff.
- `DrawOnlyManager` owns persistent debug visualization invocation.

## Mining and worker invariants

Read `PROJECT_CANON.md` before changing worker, mineral, CCA, JIT, label, or startup logic.

Do not:

- infer roles from screen direction, absolute X/Y, nearest-worker distance alone, or W-number order;
- reorder or mutate the greedy mineral chain during assignment;
- select the first non-empty assignment list instead of resolving the current spawn;
- issue commands using unverified worker tags, mineral tags, coordinates, or another spawn's data;
- enable Salmon or Blue CCA bumping as a shortcut for a missing assignment;
- replace route-aware team assignment with nearest-resource matching;
- remove established `MOVE` positioning or final `SMART` handoff behavior without an explicit requirement.

Unresolved rules in `PROJECT_CANON.md` are intentionally unresolved. Add diagnostics, tests, or discussion; do not invent an algorithm or threshold.

## Explicit no-invention rule

- NEVER add fallback, failsafe, recovery, nearest-match, substitute-target, default-mining, or made-up behavior unless the owner explicitly requests it.
- NEVER silently reinterpret a missing or incomplete assignment as permission to select another worker, mineral, coordinate, route, or spawn.
- If the instructed assignment cannot be executed exactly, preserve the assignment contract, report the concrete missing data, and stop that command path.
- Do not describe an invented fallback as a safety improvement. It is an unauthorized behavior change.

## Change discipline

Every implementation change must be narrow and explainable:

- Modify only files needed for the requirement.
- Preserve public APIs, serialized DTO property names, labels, manager registration, and command ownership unless explicitly required.
- Do not perform unrelated cleanup, formatting churn, renames, or architecture rewrites.
- Reuse existing services and helpers instead of introducing parallel abstractions.
- Keep comments focused on non-obvious ownership, lifecycle, or safety reasons.
- Never add secrets, credentials, generated binaries, local map data, or temporary files.

For each change, record or be able to state:

- the new behavior;
- the existing behavior intentionally preserved;
- the files and ownership boundaries affected;
- the regression risk and how it was checked.

## Required verification

After code changes, run the project checks documented in `CONVENTIONS.md`:

```powershell
dotnet build .\BabySharkBot\BabySharkBot.csproj
dotnet build .\BabyShark.sln
git diff --check
```

For startup, mining, map, label, CCA, or JIT changes, also verify when the local SC2 environment is available:

- the current spawn index and town-hall coordinates;
- self-only worker discovery and worker tags;
- current-spawn mineral and team targets;
- the CCA-to-steady-state handoff;
- no command targets another spawn's resources;
- existing debug labels and drawings remain present;
- `Sharky/` is unchanged.

If a check cannot be run, say exactly which check was not run and why. Do not claim a behavior is preserved without verification evidence.

## Agent completion checklist

Before reporting completion:

- [ ] Existing requirements were preserved or an explicit conflict was reported.
- [ ] No unrelated worktree changes were reverted or overwritten.
- [ ] The owning class and lifecycle were inspected before editing.
- [ ] The smallest viable change was made.
- [ ] Relevant build, test, and diff checks were run.
- [ ] Canonical documentation was updated if ownership or confirmed behavior changed.
- [ ] Any unresolved design choice is called out instead of being hidden in code.
