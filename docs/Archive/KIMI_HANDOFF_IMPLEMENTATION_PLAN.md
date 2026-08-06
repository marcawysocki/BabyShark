# KimiK3 Handoff Implementation Plan

## Objective

Fix the live BabySharkBot mining startup so that:

1. Every self worker receives a stable label.
2. Worker/team assignments are resolved for the current spawn only.
3. CCA/JIT commands cannot select mineral coordinates from another spawn or the enemy base.
4. The existing CCA, JIT, debug-drawing, and MemoryPack architecture remains intact.

This is a surgical patch plan. It does **not** replace the production mining manager or introduce the stale KimiK3 file layout.

## Canonical live architecture

- Project: `BabySharkBot/BabySharkBot.csproj`
- Namespace: `BabySharkBot.*`
- Unit tags: `ulong`
- Serialized map model: `MawBaseLocationData`
- Worker labels: `WorkerLabelService` in `BabySharkBot/Setup/BaseDtos.cs`
- Spawn-aware map data: `BabySharkBot/Setup/InitialMapData.cs`, `SecondaryMapData.cs`, and `OngoingMapData.cs`
- CCA command owner: `BabySharkBot/Managers/CcaManager.cs` plus `Services/chrisCrossAppleSause.cs`
- Steady-state mining owner: `BabySharkBot/Managers/BabySharkMiningManager.cs`
- JIT build prepositioning: `BabySharkBot/Services/JitPrepositionService.cs`

## Findings that drive the fix

### 1. The KimiK3 payload is stale and must not be copied

Do not create or use:

- `src/BabyShark/...`
- `BabyShark.Workers.Services.WorkerLabelService`
- `Dictionary<int, string>` worker labels
- `GreedyChainColorTeamAssignment`
- A replacement `BabySharkMiningManager`
- A unit-level rewrite of `TeamPatchMiningTask`

The live code already has the required concepts with different APIs and stronger integration.

### 2. Initial worker collection is not explicitly self-only

In `BabySharkBot/Setup/InitialMapData.cs:329-440`, the raw-unit scan adds every unit whose type is a worker to `workerList` before checking `Alliance.Self`. The worker chain is later built from that list around `InitialMapData.cs:569-574`.

This can contaminate W-label generation and team assignments with enemy workers. The implementation must add the alliance check at collection time and ensure only self workers reach `WorkerLabelChainHelper` and `TeamLabelRegistrationHelper`.

### 3. Loaded-map spawn identity is not resolved from the current observation

`BabySharkBot/BabySharkBot.cs:311-330` loads cached data and calls `GetApiLocAndCOM.LoadCurrentSettings(gameInfo, mapData)` without passing the current observation. `GetApiLocAndCOM.ResolveCurrentSpawnIndex()` prefers an existing `Settings.CurrentSpawnIndex` and does not inspect the observed self town hall.

The cached data can therefore retain one game's spawn index while the current game starts at another location. This explains how a valid assignment can point at another base's mineral line.

### 4. CCA contains an unsafe cross-spawn fallback

`BabySharkBot/Services/chrisCrossAppleSause.cs:94-115` falls back to the first non-empty assignment list across all starts when the current state has no assignments. That is unsafe: the first valid list is not necessarily the current player's spawn.

CCA must use only the `currentAssignments` argument resolved for `startIndex`, or fail closed with no unit command until current-spawn assignments are available.

### 5. Worker label mappings can retain stale reverse entries

`WorkerLabelService.SetLabel()` in `BabySharkBot/Setup/BaseDtos.cs:388-401` adds the new label/tag pair but does not remove the prior label for the same tag or the prior tag for the same label. W-to-team transitions can leave stale mappings in both dictionaries.

The update must be atomic: remove conflicting old mappings, add the new pair, then emit `LabelChanged` once.

### 6. JIT worker selection reads a weaker assignment source

`BabySharkBot/Services/JitPrepositionService.cs:85-99` reads `mapData.TeamPatchAssignments` directly. The authoritative resolver in `OngoingMapData.ResolveTeamAssignments()` prioritizes worker-count and current-spawn data. JIT selection should use that resolver so it cannot bypass the current-spawn safeguards.

## Implementation sequence

### Phase 1: Add a single current-spawn resolver contract

**Files:**

- `BabySharkBot/Setup/GetApiLocAndCOM.cs`
- `BabySharkBot/BabySharkBot.cs`
- `BabySharkBot/Setup/SecondaryMapData.cs` only if needed to reuse its coordinate matching

**Changes:**

1. Add an overload or replace the current resolver so it accepts `ResponseObservation` and `MawBaseLocationData`.
2. Locate the self town hall from the current observation using the existing Zerg/Terran/Protoss town-hall unit types and `Alliance.Self`.
3. Match that observed position against `mapData.StartingTownHall` using the existing small coordinate tolerance.
4. Return the matching cached-data index; do not trust a stale static index when an observed town hall is available.
5. Keep a guarded fallback only when observation/town-hall data is unavailable. The fallback must validate that the index is within all relevant map-data collections.
6. In `BabySharkAI.OnStart`, call the observation-aware resolver on the cached-data path before assigning `Globals.CurrentStartIndex` and `Settings.CurrentSpawnIndex`.
7. Reset per-game spawn state at startup so a previous game cannot supply the fallback index or location.

**Acceptance criteria:**

- The current observed town hall determines the index in cached map data.
- A cached assignment for another start is never selected solely because it is the first non-empty list.

### Phase 2: Make initial worker and resource data ownership explicit

**Files:**

- `BabySharkBot/Setup/InitialMapData.cs`
- `BabySharkBot/Setup/WorkerLabelChainHelper.cs` (only if a defensive filter is useful)
- `BabySharkBot/Setup/ProcessVisableUnits.cs`

**Changes:**

1. Filter `workerList` to `unit.Alliance == Alliance.Self` in the initial scan.
2. Preserve the existing `ulong` tags and `WorkerLabelService` API.
3. Ensure the heavy visible-unit path only builds worker entries for self workers and never labels enemy workers.
4. Preserve the existing self-relative start-location generation behavior for newly created map data, but validate that the generated current start has non-empty mineral, worker, and assignment data before enabling CCA.
5. Do not add a new color-team class; continue using `TeamLabelRegistrationHelper` and `TeamColorService`.

**Acceptance criteria:**

- The worker label service contains only self workers plus intentional static labels such as `H1`, `OV1`, and larva labels.
- Initial team assignments contain only self worker tags and minerals from the selected start.

### Phase 3: Harden worker-label remapping

**File:**

- `BabySharkBot/Setup/BaseDtos.cs`

**Changes:**

Update `WorkerLabelService.SetLabel(string label, ulong tag, Point? pos = null)` so the two dictionaries remain a bijection:

1. If `tag` already has a different label, remove the old label-to-tag entry.
2. If `label` already belongs to a different tag, remove the old tag-to-label entry.
3. Store the new pair.
4. Raise `LabelChanged` only when the effective mapping changed.

Do not change the public signatures or move the service into a new directory.

**Acceptance criteria:**

- W labels transition to team labels without stale reverse lookups.
- `GetLabel(tag)` and `GetTag(label)` agree after every remapping.

### Phase 4: Make assignment consumers fail closed

**Files:**

- `BabySharkBot/Setup/OngoingMapData.cs`
- `BabySharkBot/Services/chrisCrossAppleSause.cs`
- `BabySharkBot/Services/JitPrepositionService.cs`
- `BabySharkBot/Managers/CcaManager.cs` if call-site guards are needed
- `BabySharkBot/Managers/BabySharkMiningManager.cs` only for bounds/empty-data guards

**Changes:**

1. Keep `OngoingMapData.ResolveTeamAssignments(mapData, startIndex)` as the single assignment resolver.
2. Validate `startIndex >= 0` and that the selected assignment list belongs to the requested start.
3. Remove `chrisCrossAppleSause` fallbacks that search `FirstOrDefault` across all starts.
4. Use the passed `currentAssignments` for the current CCA state. If it is empty, emit no bump/harvest command rather than selecting another spawn.
5. Validate each assignment before command generation:
   - worker tag is present in the live self-worker set;
   - mineral entries have valid positions and non-zero/known tags where a unit command requires a tag;
   - mineral position belongs to the current spawn's ordered mineral set;
   - command target is not merely a cached coordinate from another assignment list.
6. Change `JitPrepositionService.SelectOptimalTeam4Worker()` to resolve current assignments through `OngoingMapData.ResolveTeamAssignments()` instead of reading `TeamPatchAssignments` directly.
7. Add bounds checks before indexing `StartingTownHall`, `OrderedMainMinerals`, and related per-start arrays in the mining manager.
8. Preserve the existing CCA frame handoff at frame 35 and the current `MOVE`/`SMART` command conventions.

**Acceptance criteria:**

- CCA emits zero commands when current-spawn assignments are missing or invalid.
- CCA and JIT never use an assignment from another start.
- Existing steady-state mining still uses current-spawn assignments after the CCA handoff.

### Phase 5: Add focused regression coverage

There is no existing test project under `BabySharkBot`, so first inspect solution/package conventions before adding one. Prefer a small test project rather than ad hoc production-only checks if the repository can support it without changing the protected `Sharky/` tree.

**Required test cases:**

1. **Worker filtering:** an observation containing self and enemy worker units yields labels/entries only for self workers.
2. **Label remapping:** W1 -> G1 removes the stale W1 reverse mapping and preserves G1 -> tag.
3. **Spawn resolution:** the observed self town hall maps to the matching cached `StartingTownHall` index, including a non-zero index.
4. **Assignment isolation:** resolving start 1 never returns start 0 assignments.
5. **CCA fail-closed behavior:** empty/invalid current assignments produce no unit commands and do not fall back to another start.
6. **JIT source consistency:** team-4 worker selection uses the same current-spawn resolver as CCA.

## Verification commands

After implementation:

```powershell
dotnet build .\BabySharkBot\BabySharkBot.csproj
dotnet build .\BabyShark.sln
```

If a test project is added:

```powershell
dotnet test .\BabyShark.sln
```

Then run one local SC2 game on a map with multiple start locations and inspect logs for:

- resolved current spawn index and town-hall coordinates;
- self worker count and label/tag pairs;
- selected assignment start index;
- CCA command target labels/positions;
- absence of cross-spawn fallback messages.

Manual success criteria:

- All starting self workers show stable labels.
- No worker receives an enemy worker tag.
- Workers stay at the current base's mineral line during CCA and JIT.
- CCA hands off at the existing phase boundary.
- No files under `Sharky/` are modified.

## Explicit non-goals

- Do not rewrite `BabySharkMiningManager`.
- Do not replace `InitialMapData` with the short KimiK3 DTO.
- Do not extract or create the int-based `WorkerLabelService`.
- Do not create `Workers/` or `ColorTeams/` solely to match stale documentation.
- Do not modify the upstream `Sharky/` directory.
- Do not add a new color-team algorithm until the existing `TeamLabelRegistrationHelper` behavior is proven insufficient.

## Recommended implementation order

1. Observation-aware cached spawn resolution.
2. Self-only worker filtering.
3. Label dictionary bijection.
4. CCA fail-closed assignment isolation.
5. JIT resolver alignment and bounds checks.
6. Regression tests.
7. Build and local-game verification.
