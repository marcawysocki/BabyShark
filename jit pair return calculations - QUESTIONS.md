# Questions: jit pair return calculations — Round 2

Round 1 answers received (owner, 2026-09-08). This file records what is now settled as canon, the
contradictions between the answers and the live code, and the remaining questions.

No code has been changed. Every claim below has a file:line from a successful read/grep in this session.

---

## Part 1 — Now settled (canon, from the owner's answers)

### 1.1 Mining Base is a new canon concept
- Spawn location index is part of every identity: `0-TA-TB`, or equivalently referenced as `0,TA,TB`.
  Next game at a different spawn would be `1-TA-TB`.
- Workers are associated to a mining base: `T1` at Start Location 0 is really `0-T1`.
  Starting minerals [1..8] become `0-TA` .. `0-YB`.
- Debug drawing stays un-prefixed (`T1`) to avoid clutter.
- There is never both `0-T1` and `1-T1` in the same game — a competing player occupies a different start location.
- Spawn locations: plan for **0..3** (currently 0,1,2; a future map may add a 4th).
- Hard rule: a worker at Location 1 must never be instructed to mine Location 0 resources.
  `1-Y1`'s first instruction is **use `1-YA-YB.HarvestA`**, then **move to that stored point**.

### 1.2 Footprint and return distance
- The Hatchery and Minerals have a footprint a worker can stand on.
- A worker cannot get closer to the Hatchery than the footprint; at that distance it is standing next to
  the Hatchery and can issue a return-cargo command.
- **5.5u diameter, 2.75 radius.**
- This matches `BuildLinePoints(pool, townhall, 1.5f, 2.75f)` at
  BabySharkBot/Managers/BabySharkBuildManager.cs:1255 and `ret = townhall + u * 2.75f` at
  BabySharkBot/Managers/BabySharkBuildManager.cs:1431.
- It does **not** match the persisted mineral return radius of 5.5 at
  BabySharkBot/Setup/InitialMapData.cs:1358 — see contradiction C1 below.

### 1.3 Unit IDs are per-connection; identity is position + label
- A Unit ID is unique to the game connection. On a previously played spawn location, a resource and a worker
  start at the **same X,Y** with the same rules but possibly a different ID.
- `TA` is in the same place per spawn location; locations 0, 1 and 2 each have their own `TA`.
- 8 or 12 starting workers. The greedy chain always resolves W12..W1 correctly, and the rules that decide
  which worker becomes T1..Y3 always resolve correctly.
- **W12..W1 takes priority over X,Y because workers can move.** The starting formation before moving can be
  registered by the greedy chain in the proper order.
- There is a default team order and per-map team orders.
- First time a map is played, static map info is stored by `InitialMapData` into a serialized dat file that is
  read while the game connection is starting. The engine supplies information ~22.4 times/second = a frame;
  managers process it on frame. **On start each new unit ID needs to be rectified.**

### 1.4 One stored point per worker
- Mining Manager reads an instruction list per worker. One instruction supplies a new point for a set.
- **Workers must not hold multiple mining points — just the point for the next set.**
  This matches the single `StoredTargetPoint` at BabySharkBot/Setup/WorkerInstructionRuntime.cs:69.
- Example: a worker moving toward a mineral has stored target point `0-TA-TB.HarvestA` for worker `0-TA`.

### 1.5 `[1183]` and `[1184]` are worker ABILITIES, not frames
Verified in Sharky/S2ClientTypeEnums/Abilities.cs:
- `HARVEST_GATHER_DRONE = 1183` (Abilities.cs:227)
- `HARVEST_RETURN_DRONE = 1184` (Abilities.cs:231)
- `MOVE = 16` (Abilities.cs:288)

This matches the telemetry lines supplied (`CurrentOrderAbilityIds":[1183]` with a target tag = gather issued;
`[1184]` with target null = return cargo; `[16,1184]` = move queued behind return).

It also matches the existing condition rows, which are already ability-keyed, e.g.
`PreviousAbilityId = 1183, TargetAbilityId = 1184` at BabySharkBot/Managers/BabySharkBuildManager.cs:509-515,
and the evaluator at BabySharkBot/Managers/BabySharkMiningManager.cs:1113-1115.

**"rewrite relative frames" is already implemented**: when a satisfied `Wait` row executes, the Mining Manager
resets the baseline so the following set's relative frames are measured from that frame —
`runtimeWorker.InstructionStartFrame = Settings.GetRelativeFrame(_currentFrame)` at
BabySharkBot/Managers/BabySharkMiningManager.cs:1208 (and again at :1224 for `Jump`).
So round-1 question D15 needs no new field and no new mechanic.

### 1.6 Canonical pair order — store each reference once
- Resource order: **VA, VB, TA, TB, SA, SB, BA, BB, YA, YB**.
- The earlier-listed resource is first: `TA` + `YB` = `TA-YB`. `YB-TA` would be a duplicate and must not exist.
- `HarvestA` is the first listed resource, `HarvestB` the second in list order.
- Consequence: the table is **undirected**, one row per combination.

### 1.7 Pool is never harvested
- A worker travels to the pool point and morphs into a building (Zerg).
- On the way the worker must return cargo to the Hatchery. It collects a resource, its ability changes to
  `[1184]`, and it needs the shortest travel distance to drop the mineral off and then reach the pool.
- Straight to the Hatchery makes the second leg too long; straight to the shortest Hatchery-to-Pool point makes
  the first leg too long. **The jit point is calculated so both legs are shortest — the minimum total distance
  for: return cargo -> move to pool -> begin morphing.**
- If the worker arrives before it can build, it waits at the pool point until it can build.

### 1.8 Wait points
- **All B minerals must also have a wait point.**
- **VA and VB have only a Harvest point.**
- **Wait points are only used on minerals; otherwise not used.**
- **Never store (0,0)** — use the resource's own X,Y for Vespene Geysers and for buildings such as Spawning Pools.

### 1.9 No fallbacks
- No contingencies, no fallbacks, no contrived workarounds. When something looks like it needs one, the cause is
  that the assistant has failed to understand the project. This restates the standing No-Invention Rule and
  AGENTS.md "Explicit no-invention rule" (AGENTS.md:60-66).

---

## Part 2 — Contradictions between the answers and the live code

These need a ruling. I will not pick a side on my own.

### C1. Two different hatchery return radii are persisted for the same minerals
- `InitialMapData.BuildHarvestReturnCargoPoint` uses `hatcheryRadius = 5.5f` and `mineralRadius = 1.0f`
  (InitialMapData.cs:1358-1359). These are the values that end up in
  `MainMineralCargoPoints` and therefore in every `OrderedMineral.ReturnPoint` / `.HarvestPoint`
  (BabySharkBuildManager.cs:756-759).
- `InitialMapData.BuildMineralLinePoints` uses `hatcheryRadius = 2.75f` and `mineralRadius = 1.5f`
  (InitialMapData.cs:1189-1190).
- `BabySharkBuildManager.BuildMineralLinePoints` uses 1.5 / 2.75 (BabySharkBuildManager.cs:1428-1432).

Answer 2 states 5.5 diameter / **2.75 radius**. But the mineral return points actually stored in BaseDtos were
built with the **5.5 radius** constant, i.e. twice as far from the Hatchery as the footprint allows.

**Q-C1: Should the persisted mineral `ReturnPoint` be rebuilt at 2.75u from the Hatchery (matching the footprint
answer and `BuildMineralLinePoints`), and is `BuildHarvestReturnCargoPoint`'s 5.5 the stale value to correct?**
This changes stored geometry, so it needs an explicit instruction plus a `SpeedMiningVersion` bump.

### C2. `GetInstructionLabels` produces cross-team and self-repeat targets at 8 workers
The 12-worker branch returns exactly the team's own pair, A-first for roles 1 and 3, B-first for role 2
(BabySharkBuildManager.cs:1363-1366) — consistent with "2s mine the B mineral, 1 and 3 mine the A mineral".

The 8-worker branch returns cross-team label triples (BabySharkBuildManager.cs:1374-1385):
```
T1 -> TA, TA, SA      S1 -> SA, SA, TA      B1 -> BA, BA, YA      Y1 -> YA, YA, BA
T2 -> TB, TA, SA      S2 -> SB              B2 -> BB              Y2 -> YB, YA, SA
```
`T1` gets `TA` twice, then `SA` — a different team's mineral. Under the canonical pair rule these would produce
keys `TA-SA`, and under answer 1.1 a Teal worker would be instructed to travel to Salmon's mineral.

**Q-C2: Is the entire 8-worker `switch` block fabricated and to be deleted, so that 8-worker starts use the same
A/B rule as 12-worker (role 1 and 3 -> `{prefix}A`, role 2 -> `{prefix}B`) with "per map team orders" coming from
a separate per-map source? Or is the 8-worker table a real per-map order that must be preserved? I will not delete
it without an explicit instruction, because AGENTS.md forbids silently removing established behavior.**

### C3. The cross-table is currently 11x11 directed = 121 rows, which duplicates every reference
`PopulateJitPairReturnCalculations` loops `from` over all 11 labels and `to` over all 11 labels
(BabySharkBuildManager.cs:1275-1304), producing `TA-TB` **and** `TB-TA`. Answer 6 forbids that duplication.

`MiningTargetCrossTableDto.Routes` is likewise a full directed matrix over mineral labels
(BabySharkBuildManager.cs:1199-1211).

**This is the answer to round-1 Q8 / "what does 121 mean": 121 = 11 labels x 11 labels, both directions, self rows
included. Under the new canonical order it must become one row per unordered combination.**

### C4. Label order in the code is the reverse of the canonical order
The code appends minerals first, then VA/VB, then Pool (BabySharkBuildManager.cs:1241-1257), so keys are generated
as `TA-VA`, `TA-Pool`, `TB-YB`, etc. Canonical order requires VA/VB **before** the team minerals, so those same
pairs must be stored as `VA-TA`, `VA-Pool`, `TB-YB`. Every existing stored `PairKey` is therefore in the wrong
orientation for any pair involving VA/VB.

### C5. Pool harvest/wait fields are invented geometry
`PopulateJitPairReturnCalculations` creates a full 5-property Pool row and uses a point 1.5u from the pool toward
the Hatchery as the pool's "Harvest" (BabySharkBuildManager.cs:1253-1257). Answer 7/1.7: the pool is never
harvested. Answer 1.8: wait points are minerals-only, and non-minerals must carry their own X,Y rather than (0,0).

### C6. `WaitPointB` is (0,0) for essentially every pair today
- `CcaWaitPoint` is only ever calculated for the **A mineral** of the current spawn
  (BabySharkBuildManager.cs:192-247), at `harvestDistance + 0.8f` (:235).
- Persisted cargo points never set `CcaWaitPoint` (InitialMapData.cs:1368-1375 omits it), so B minerals and any
  mineral loaded from the dat file have (0,0).
- Vespene wait points are `cargo?.CcaWaitPoint ?? new Vector2Dto()` (BabySharkBuildManager.cs:1250) and
  `MainVespeneCargoPoints` entries never populate it — always (0,0).
- Pool wait point is `new Vector2Dto()` (BabySharkBuildManager.cs:1256) — always (0,0).

Answer 1.8 requires B minerals to have wait points and forbids storing (0,0).

### C7. Ordering bug — pairs are written before the wait points are calculated
`OnStart` calls `BuildGreedyMineralChainFromObservation(frame)` at BabySharkBuildManager.cs:161, which calls
`PopulateJitPairReturnCalculations` at :993, and only **afterwards** calls
`CalculateCcaWaitPointsForCurrentSpawn()` at :162. On a first run every pair row is written with `WaitPointA`
= (0,0) and the CCAw point computed a moment later never reaches it. (`OnFrame` has the same order:
:111 chain, then :162 is absent — CCAw is only called from `OnStart`.)

### C8. Stale unit tags are copied verbatim on reuse
The reuse branch adds the stored DTO unchanged (BabySharkBuildManager.cs:1280-1287), including
`FromResourceUnitTag` / `ToResourceUnitTag` (BaseDtos.cs:313-314) from the previous connection.
`ResolveInstructionPoint` finds its target by `candidate.ResourceUnitId == instruction.TargetId`
(BabySharkMiningManager.cs:1331), so a stale tag resolves to `null` and the worker receives no command.
Answer 1.3 says IDs must be rectified on start.

### C9. `MainMineralJitCargoPoints` is dead data
Written at InitialMapData.cs:936 and :1008 and at BabySharkBuildManager.cs:949; a repository-wide grep for the
identifier returns only the declaration (BaseDtos.cs:515) and those three writes. **No reader exists.**
`MiningPairCargoPointDto` therefore duplicates the new table while using a different return-point convention
(`JitReturnPoint = firstReturn` at BabySharkBuildManager.cs:810 vs midpoint at InitialMapData.cs:1109 vs midpoint
at BabySharkBuildManager.cs:1289).

### C10. No instruction can currently reference a Pool or Vespene pair
`TargetPointReference` is written (BabySharkBuildManager.cs:624-632) but never parsed.
`StoreReferencedTargetPoint` only null-checks the string and copies a point resolved by enum
(BabySharkMiningManager.cs:1294-1303), and the enum path requires a `MiningTargetDto` match on `ResourceUnitId`
(:1331). `MiningTargets` only ever contains minerals, because unknown labels are skipped with
`[ASSIGNMENT ERROR]` (BabySharkBuildManager.cs:1149-1155) and `GetInstructionLabels` only returns mineral labels.
So `0-TA-Pool`, `0-VA-VB` and `0-VA-Pool` exist in the table but are unreachable by any worker.

---

## Part 3 — Remaining questions

### Group A — Pair table shape

**A1. Where does `Pool` sit in the canonical order?**
Answer 6 lists `VA, VB, TA, TB, SA, SB, BA, BB, YA, YB` and does not mention Pool, but the spec requires
`TA-Pool`, `VA-Pool`, `VB-Pool`. Is Pool **last** (after YB), giving 11 labels? If Pool is last, the row count is
55 non-self + 11 self = 66. If self rows are excluded, 55.

**A2. Are self rows kept?**
`VA-VA` / `VB-VB` are described in the spec as "Speed Mining return and harvest points" and `TA-TA` as the `x`
cell. Do self rows stay in the table (so a worker's speed-mining set resolves through the same reference syntax),
or are they omitted and speed mining keeps using `Harvest`/`Return`/`SmHarvest`/`SmReturn`
(BabySharkMiningManager.cs:1349-1350)?

**A3. Confirm the row count and the dedup rule in code terms.**
Proposed: iterate the canonical label list once; for `i` from 0..n-1 and `j` from i..n-1 (or i+1 if self rows are
excluded) emit one row keyed `{startIndex}-{label[i]}-{label[j]}`, `HarvestA` = `label[i]`'s harvest,
`HarvestB` = `label[j]`'s harvest. Confirm, and confirm the separator: `-` in the stored key while `0,TA,TB` is
accepted as the spoken/reference form.

**A4. Should `ResourceLabels` / `Routes` in `MiningTargetCrossTableDto` survive at all?**
With the pair table deduplicated, `Routes` (a directed mineral-only matrix, BabySharkBuildManager.cs:1199-1211)
is a second, differently-shaped copy of the same information. Keep `JitPairs` only and drop `Routes`, or keep both?

### Group B — Pool geometry (the load-bearing calculation)

**B1. Confirm the formula.**
Reading answer 1.7 literally: the jit point is the point at the Hatchery footprint (2.75u from the Hatchery
center) that lies on the straight line **from the Hatchery toward the Pool**, i.e.
`poolPoint = hatchery + normalize(pool - hatchery) * 2.75`. That is exactly what
`BuildLinePoints(pool, townhall, 1.5f, 2.75f).Return` already computes (BabySharkBuildManager.cs:1255,
:1341). Confirm this is the intended "two shortest legs" point, and that it is the **same point for every
`X-Pool` pair** (independent of which mineral the worker was harvesting).

**B2. If instead the point must depend on the worker's harvest mineral** (a true per-pair minimum of
`mineral -> P -> pool` subject to `|P - hatchery| = 2.75`), say so and I will implement that optimization instead.
Which is it?

**B3. What does the worker's stored point become for the pool set?**
Answer 1.7 describes: ability becomes `[1184]` -> shortest drop-off -> move to pool -> wait at the pool point
until it can build -> morph. Should the pool set's stored point be the **B1 jit point** (the drop-off point), with
a separate later instruction pointing at the **pool placement** itself
(`SpawningPoolPlacements[startIndex]`, BabySharkBuildManager.cs:1102-1108)? Or is one point enough?

**B4. What are `HarvestA` / `HarvestB` / `WaitPoint` for a Pool row, given answer 1.8?**
Proposal: for `X-Pool`, `HarvestA` = X's harvest point (X is first in canonical order for minerals and vespene),
`HarvestB` = **the Pool's own X,Y** (the placement point, not a 1.5u offset), `WaitPointA` = X's wait point if X
is a mineral else X's own X,Y, `WaitPointB` = **the Pool's own X,Y**. Confirm.

**B5. Is the pool placement itself deterministic per spawn location, so it can be persisted and reused?**
`CalculateSpawningPoolPlacement(townhall, mineralCOM, VB)` (BabySharkBuildManager.cs:1096-1099) uses only static
geometry, and the result is stored in `SpawningPoolPlacements[startIndex]`. Confirm that on a replayed spawn the
stored placement is authoritative and must not be recomputed — or must it be recomputed each connection?

### Group C — Wait points

**C1. Confirm B minerals use the same 0.8u rule as A minerals.**
A minerals use `scale = (harvestDistance + 0.8f) / harvestDistance` measured from the mineral position toward its
own harvest point (BabySharkBuildManager.cs:226-239). Do B minerals get the identical formula from their own
position/harvest point? Any different radius for B?

**C2. Confirm vespene and pool wait points are their own X,Y.**
`VA`/`VB` wait point = the geyser's own position; `Pool` wait point = the pool placement position. And per
answer 1.8 these fields exist but are never *used* by any instruction (wait points are minerals-only). Confirm
they are stored purely so no field is ever (0,0).

**C3. Should the wait points be persisted into the dat file, or recomputed each connection?**
`CalculateCcaWaitPointsForCurrentSpawn` treats a stored non-zero point as authoritative and documents that a
radius change requires invalidating stored points first (BabySharkBuildManager.cs:201-219). With B minerals now
needing wait points, the stored set grows from 4 per spawn to 8. Confirm: compute once, persist, reuse — and the
reuse gate must include the wait points so a radius change can be rolled out deliberately (round-1 Q11).

**C4. Ordering fix.**
Confirm that pair population must move to **after** `CalculateCcaWaitPointsForCurrentSpawn()`, i.e. the frame-0
order becomes: greedy chain -> VA/VB ordering -> pool placement -> CCAw/A+B wait points -> jit pair table ->
assigned workers -> instruction seeding. (Contradiction C7.)

### Group D — Identity, rectification, storage

**D1. What exactly is rectified on start, and where?**
Answer 1.3: IDs differ per connection, positions and rules do not, and "on start each new unit ID needs to be
rectified". Confirm the rectification map is: **observed unit -> (mining base, label) -> persisted geometry**,
matched by X,Y for resources and by greedy-chain W-number for workers. Is `BabySharkBuildManager` the owner of
that rectification, or `ObservationManager`/`OngoingMapData`? (AGENTS.md:37-42 gives static discovery to
`InitialMapData`, current-spawn refresh to `OngoingMapData`, greedy chain to `BabySharkBuildManager`.)

**D2. Should unit tags be removed from `JitPairReturnCalculationDto` entirely?**
Given D1, `FromResourceUnitTag` / `ToResourceUnitTag` (BaseDtos.cs:313-314) are per-connection values inside a
persisted DTO — the direct cause of contradiction C8. Options: (a) drop both fields, (b) keep them and rewrite
them on every connection before use. Which? If (a), the SC2 gather command still needs a live tag — confirm it
comes from the reconciled `MiningTargetDto.ResourceUnitId` / observation, never from the persisted pair row.

**D3. Should instruction lists be persisted, or rebuilt each connection?**
Answer 1.3 says the dat file is read while the connection is starting, and answer 1.4 says a worker holds only the
next set's point. If identity is fully determined by mining base + label + static X,Y, then a worker's whole
instruction list is static per mining base and could be stored once and replayed with only tags rectified.
Do you want the lists persisted in BaseDtos (keyed `0-T1`), or regenerated at startup by Build from the pair table
as today (BabySharkBuildManager.cs:314-320)? Persisting them would also let `Mti`, jump indices and relative
frames survive reconnects exactly.

**D4. Do role strings become `0-T1` in the persisted DTOs?**
`WorkerEntryDto.Label` / `StartLabel` / `FinalLabel` (BaseDtos.cs:49-51), `AssignedWorkerDto.Role`
(BaseDtos.cs:393) and `OrderedMineral.FinalLabel` / `.Label` (BaseDtos.cs:191-208) are all un-prefixed today, and
`ResolveInstructionPoint` compares `assignedWorker.Role` against `"T3"`/`"Y3"` style literals
(BabySharkMiningManager.cs:1375-1377). Should the prefixed mining-base form be stored in these fields (with debug
drawing stripping the prefix), or should the prefix live only in the pair-key/reference strings? This decides how
much comparison code changes.

**D5. Where does the mining-base prefix get applied — one place or many?**
Proposal: a single canonical key builder used by Build when writing pairs and when writing instruction
references, and a single parser used by Mining Manager when reading a reference. Anything else would spread
string formatting across managers. Confirm that is acceptable, given the "no helpers" instruction in
`BaseDtos.cs needs to store for each spawn location` line 4 — does that instruction apply here too, or only to the
observation/label if-else logic it was written about?

### Group E — Reference resolution (round-1 Q4, still unanswered)

**E1. Should Mining Manager resolve points by parsing the reference string against the pair table?**
`TargetPointReference` is written but never parsed (contradiction C10). Under the new canon the reference is
`{miningBase}-{labelA}-{labelB}.{Property}` — e.g. `1-YA-YB.HarvestA` for `1-Y1`'s first instruction.
Proposal: Mining Manager parses that string, looks up
`MainJitPairReturnCalculations[miningBase]` by `PairKey`, reads the named property, and stores it as the worker's
single stored point. This removes the dependence on `MiningTargetDto.JitHarvestA/B`
(BabySharkBuildManager.cs:1319-1324) and makes Pool and Vespene pairs reachable.
Confirm this is the intended design, or state the alternative.

**E2. If E1 is yes, do `MiningTargetDto`'s six Jit fields stay?**
`JitPairKey`, `JitWaitPointA/B`, `JitHarvestA/B`, `JitReturnPoint` (BaseDtos.cs:363-368) exist only to feed the
enum resolution path. Keeping them alongside a parsed-reference path is exactly the parallel system AGENTS.md:35
forbids. Retire them, or keep them for the existing Magannatha/CCA paths that still read them
(BabySharkMiningManager.cs:1351-1355)?

**E3. Confirm the first two instructions for every worker.**
Answer 1.1: "`1-Y1` first instruction is a move to `1-YA-YB.HarvestA`. The first instruction for `1-Y1` is use
`1-YA-YB.HarvestA` then that is followed by move to that stored point."
Reading both sentences together: instruction 0 = **use/store** `1-YA-YB.HarvestA` (no condition, no SC2 action),
instruction 1 = **move** to the stored point. That matches the existing shape —
`AddJitPairSetupInstruction(... StoreTargetPoint, NoCondition = true ...)` then Move rows
(BabySharkBuildManager.cs:501-503, :581-584). Confirm, and confirm the same two-line opening applies to
role 2 workers with `HarvestB` (per `Worker Instruction List.MD:27`, Role 2 starts on B).

### Group F — Housekeeping

**F1. `jitMR` vs `jitRM`.** Spec lines 38/40 say `jitMR`; the code and `Worker Instruction List.MD:9,13,19-27`
say `jitRM` (BabySharkBuildManager.cs:589-593). Rename, or is the spec text the typo? Renaming invalidates the
`InstructionSet` strings in the frame-0 JSON dumps (BabySharkBuildManager.cs:378).

**F2. Is the existing 0/1/14/15 move pattern preserved?**
Spec line 40 says the third line is `+0, move to last stored target point`; the code emits Move at relative frames
0, 1, 14 then a queued Return/Gather at 15 (BabySharkBuildManager.cs:581-584, :590-593). Under the additive-change
rule I would keep 0/1/14/15 and add only the new first line. Confirm.

**F3. `SpeedMiningVersion`.** Already bumped `0.09 -> 0.10` in this worktree (Settings.cs:19) and used as the dat
file name (MapDataManager.cs:50). This change alters stored pair keys, wait points and possibly return radii
(C1). Does it need `0.11`, or is `0.10` still unreleased?

**F4. `MainMineralJitCargoPoints` / `MiningPairCargoPointDto`.** Dead per C9. Delete both (plus the writes at
InitialMapData.cs:936, :1008 and BabySharkBuildManager.cs:944-949), or leave them untouched for now? I will not
delete serialized DTO properties without an explicit instruction.

**F5. `TeamPatchAssignmentDto.JitReturnPoint` / `JitWaitPoint`.** Still consumed to override a target's return
point at BabySharkBuildManager.cs:1165-1168, and preserved across rebuilds at :964-992. `JitReturnPoint` is
currently written as `new Vector2Dto()` — zero — by `TeamLabelRegistrationHelper` (TeamLabelRegistrationHelper.cs:181-194),
so that override path can only ever fire from a previously stored value. Once the pair table is the single source,
should these two fields be retired, or does the CCA/Magannatha opening still need them?

**F6. Is the current uncommitted worktree the additive-only baseline?**
9 modified files under `BabySharkBot/`, +1149/-382; none of the jit-pair code exists in HEAD
(`git show HEAD:BabySharkBot/Managers/BabySharkBuildManager.cs` has no `PopulateJitPairReturnCalculations`).
Confirm I build on it as-is, and that nothing in it is the "Model GPT 5.6 Luna mess" to be rolled back. If something
is, name it — I will not revert worktree changes without an explicit instruction (AGENTS.md:13).

**F7. Expansion keys.** Answer 1 says the mining base index is "also needed for expansions once that is added".
Confirm the future form is `{expansionIndex}-{labelA}-{labelB}` with expansion resource labels assigned by the
same greedy rules, and that **no expansion work is in scope now** — I should only avoid designing the key format
in a way that blocks it.

---

## What unblocks implementation fastest

Answer these six and I can write the pair table and the instruction rows without guessing:
**C1** (return radius), **C2** (8-worker labels fabricated or real), **B1/B2** (pool jit formula),
**A1+A2** (Pool position in the order and whether self rows stay), **E1** (parse the reference string),
**D3** (persist instruction lists or rebuild them).

Everything else can follow in any order.

---
---

# Round 3 (owner answers 2026-09-09: C1, C2, and "what exactly is the C7/C8 question?")

## 3.1 C1 SETTLED — 5.5 diameter / 2.75 radius is the only correct hatchery footprint

Owner: "Correct anything that is not 5.5 diameter and 2.75 radius."

Audit of every hatchery/mineral offset constant in the repository:

| Location | Constant | Value | Verdict |
|---|---|---|---|
| InitialMapData.cs:1358 | `hatcheryRadius` | **5.5f** | WRONG — the diameter used as a radius. Must become 2.75f |
| InitialMapData.cs:1359 | `mineralRadius` | **1.0f** | Inconsistent with every other mineral offset (1.5f) — ruling needed, Q-C1a |
| InitialMapData.cs:1189 | `hatcheryRadius` | 2.75f | Correct |
| InitialMapData.cs:1190 | `mineralRadius` | 1.5f | Correct |
| BabySharkBuildManager.cs:1428 | `mineralHarvestOffset` | 1.5f | Correct |
| BabySharkBuildManager.cs:1431 | return offset | 2.75f | Correct |
| BabySharkBuildManager.cs:1255 | pool `returnOffset` | 2.75f | Correct |
| BabySharkBuildManager.cs:1432 | `smReturn` | **townhall + u * 1.0** | 1.0u from the hatchery center — INSIDE the 2.75 footprint, unreachable. Ruling needed, Q-C1b |
| InitialMapData.cs:1191/1360 | `smallInset` | 1.75f | With a corrected 2.75 hatchery radius, `smReturn = 2.75 - 1.75 = 1.0u` — same problem |

**Blast radius.** `BuildHarvestReturnCargoPoint` (InitialMapData.cs:1338-1376) feeds `MainMineralCargoPoints` and
`MainVespeneCargoPoints`, which are persisted to the dat file and copied verbatim into every runtime
`OrderedMineral` (BabySharkBuildManager.cs:756-759). So 5.5 is baked into stored data for every map already played.
Correcting it changes every `OrderedMineral.ReturnPoint` / `.SmReturnPoint`, every persisted
`HarvestReturnCargoPointDto.ReturnPoint`, and every pair `ReturnPoint` (derived at BabySharkBuildManager.cs:1289).
Requires a `SpeedMiningVersion` bump so the stale dat file is invalidated rather than reused (MapDataManager.cs:50
keys the file name on the version).

**Q-C1a: is the mineral harvest radius 1.0 or 1.5?** `mineralRadius = 1.0f` at InitialMapData.cs:1359 (persisted
path) versus `1.5f` at InitialMapData.cs:1190 and BabySharkBuildManager.cs:1428.

**Q-C1b: are the `Sm*` inset points still allowed inside the footprint?** `SmReturnPoint` lands at 1.0u from the
hatchery center under both paths once 2.75 is applied (InitialMapData.cs:1365; BabySharkBuildManager.cs:1432), but
answer 1.2 says a worker cannot get closer than the footprint. Options: (a) clamp to 2.75, making `SmReturnPoint`
identical to `ReturnPoint`; (b) reduce `smallInset` so the result stays >= 2.75; (c) the `Sm*` points are a
speed-mining artifact the instruction lists no longer use — retire them.

## 3.2 C2 SETTLED — the greedy chain is correct; the 8-worker `switch` is the fabricated part

Owner: at an 8-worker start every worker is assigned to its index — `W1 -> Mineral[1]` through `W8 -> Mineral[8]`.
The greedy chain puts the most clockwise worker next to the most clockwise mineral; on a left-to-right map both are
ordered left to right. **That is the result of the greedy chain working correctly, not a calculation.**

Verification that the existing chain already satisfies this:

1. `WorkerLabelChainHelper.BuildGreedyWorkerEntries` anchors on the worker farthest from the mineral COM, walks
   nearest-neighbour, and labels `W{count - traversalIndex}` so the last worker walked is `W1`
   (WorkerLabelChainHelper.cs:54-93). Comment at :59-60: observation order is never retained as a list index.
2. `BuildRuntimeGreedyMinerals` takes `w1Position = greedyWorkers.LastOrDefault()` (BabySharkBuildManager.cs:689),
   traverses from W1, takes the far end, re-traverses, then **reverses** (:703-722) so the mineral nearest W1 comes
   first, and assigns `Index = orderIndex + 1` (:761).
3. BaseDtos.cs:93-96 documents Index 1 = M[1] on the W1/Teal side, Index 8 = M[8] on the W12/Yellow side.
4. The 8-worker layout is a strict linear mapping over `canonicalMinerals` sorted by Index
   (TeamLabelRegistrationHelper.cs:148-151, :234-242): `M[1],M[2] -> W1,W2`; `M[3],M[4] -> W3,W4`;
   `M[5],M[6] -> W5,W6`; `M[7],M[8] -> W7,W8`.
5. `ApplyWorkerFinalLabels` gives `teamWorkers[0]` the near mineral and `teamWorkers[1]` the far mineral
   (TeamLabelRegistrationHelper.cs:718-727), so W1 = T1 on TA, W2 = T2 on TB, and so on.

**Conclusion: W1 -> M[1] -> TA and W8 -> M[8] -> YB already hold. No change needed to the chain or the layout.**

The only code contradicting C2 is the 8-worker `switch` in `GetInstructionLabels`
(BabySharkBuildManager.cs:1374-1385), which hands workers other teams' minerals and repeats one:
```
T1 -> TA, TA, SA      S1 -> SA, SA, TA      B1 -> BA, BA, YA      Y1 -> YA, YA, BA
T2 -> TB, TA, SA      S2 -> SB              B2 -> BB              Y2 -> YB, YA, SA
```
Under the mining-base rule `T1 -> TA, TA, SA` would instruct a Teal worker to cross to Salmon's patch.

**Q-C2a: confirm deletion and replacement.** Delete BabySharkBuildManager.cs:1369-1385 so 8-worker starts fall
through to the same rule as 12 workers — `startsOnA = roleNumber == '1' || roleNumber == '3'` returning
`{prefix}A`, else `{prefix}B` (:1363-1366). At 8 workers only roles 1 and 2 exist: role 1 -> `{prefix}A`,
role 2 -> `{prefix}B`.

**Q-C2b: with one mineral per worker, which pair does an 8-worker instruction reference?**
(i) both team workers reference the team pair `0-TA-TB`, the A worker using `HarvestA` and the B worker using
`HarvestB`, both sharing `0-TA-TB.ReturnPoint` — no alternation, no self rows; or (ii) each worker references a self
row `0-TA-TA`. This also settles round-2 A2.

**Q-C2c: is there any A/B alternation at an 8-worker start?** At 12 workers roles 1/3 alternate A->B->A. At 8
workers there is no role 3. Does T1 stay on TA for the whole game (speed mining its own mineral, sharing the pair
return point with T2), or still alternate TA/TB?

**Q-C2d: "default team order and per map team orders" — where does a per-map order come from?** Today the per-map
divergence is `Settings.IsMagannathaMap` / `IsMagannatha12WorkerOverride` selecting a different 12-worker W-mapping
(TeamLabelRegistrationHelper.cs:212-230), and `GetInstructionLabels` treating `IsMagannathaMap` as 12-worker
(BabySharkBuildManager.cs:1354). Is a map flag choosing between explicit W-label arrays the intended mechanism, and
should the 8-worker path use it instead of a hard-coded label switch?

## 3.3 C7 restated — the exact question

`OnStart` runs (BabySharkBuildManager.cs:161-162):
```csharp
BuildGreedyMineralChainFromObservation(frame);   // line 161
CalculateCcaWaitPointsForCurrentSpawn();          // line 162
```
Inside line 161 the chain builder calls `PopulateJitPairReturnCalculations` at :993. Inside line 162,
`CalculateCcaWaitPointsForCurrentSpawn` is what computes `CcaWaitPoint` and writes it to `aMineral.CcaWaitPoint` and
`assignment.JitWaitPoint` (:236-241).

`PopulateJitPairReturnCalculations` reads the wait point straight off the mineral:
```csharp
resourceLabels.Add((mineral.FinalLabel, mineral.UnitTag, mineral.Position,
    mineral.HarvestPoint, mineral.ReturnPoint, mineral.CcaWaitPoint));   // line 1243
```
and stores `WaitPointA = from.Wait` (:1297).

So on the first game at a spawn, line 993 runs **before** line 162 computes anything: every pair row is written with
`WaitPointA = (0,0)`. It is never corrected, because `BuildGreedyMineralChainFromObservation` latches — once
`_initialAssignmentLatched` is true and the worker count is unchanged it returns early (:882-905), so
`PopulateJitPairReturnCalculations` does not run again. And a repository-wide grep for
`CalculateCcaWaitPointsForCurrentSpawn` returns exactly two hits: the definition at :183 and the call at :162. It is
never called from `OnFrame`.

**The question:**

> Confirm the frame-0/start order must become: greedy chain + VA/VB ordering + pool placement -> **A and B mineral
> wait points** -> **jit pair table** -> assigned workers + cross table -> instruction seeding.
> Concretely: move `PopulateJitPairReturnCalculations` out of `BuildGreedyMineralChainFromObservation` (line 993) to
> a call **after** `CalculateCcaWaitPointsForCurrentSpawn()` (line 162), and extend
> `CalculateCcaWaitPointsForCurrentSpawn` to compute wait points for **B minerals** as well as A minerals (answer 1.8).

This is not cosmetic: the table is persisted and reused, and the reuse gate does not test wait points
(:1281-1283), so a table written with zero wait points would be reused with zero wait points forever.

**Q-C7a:** confirm the reorder.
**Q-C7b:** confirm B minerals use the identical 0.8u rule as A minerals — `scale = (harvestDistance + 0.8f) / harvestDistance`
from the mineral position toward its own harvest point (:226-239) — with no different radius for B. (Round-2 C1, still open.)
**Q-C7c:** confirm adding `HasNonZeroPoint(stored.WaitPointA)` and `...WaitPointB` to the reuse test at :1281-1283,
so a table written before B wait points existed is recalculated rather than reused forever.

## 3.4 C8 restated — the exact question, narrowed by a new finding

**Finding:** `FromResourceUnitTag` / `ToResourceUnitTag` are **never read anywhere**. A repository-wide grep returns
four hits only: declarations at BaseDtos.cs:313-314 and writes at BabySharkBuildManager.cs:1295-1296. So the
stale-tag hazard is latent, not active — no code path resolves a command from those fields.

**What carries the live tag.** `ResolveInstructionPoint` matches `instruction.TargetId` against
`MiningTargetDto.ResourceUnitId` (BabySharkMiningManager.cs:1331). Those DTOs are rebuilt on every assignment pass by
`PopulateAssignedWorkersAndCrossTable` using the current observation's `townHallUnitId` (BabySharkBuildManager.cs:995-998)
and `to.UnitTag` from the freshly observed mineral (:1396). The live path is already rectified per connection.

**The question:**

> Given identity is `{mining base, label, static X,Y}` and unit IDs are per-connection (answer 1.3), and given those
> two tag fields are written but never read:
> **(a)** delete both from `JitPairReturnCalculationDto`, so the persisted table holds only geometry keyed by
> `{miningBase}-{labelA}-{labelB}`; the live tag for a gather keeps coming from the rectified
> `MiningTargetDto.ResourceUnitId` / observation, never from the persisted row; or
> **(b)** keep both and rewrite them from the current observation on every reuse before anything reads them.

I recommend (a): it removes a per-connection value from a persisted DTO — the exact class of bug answer 1.3
describes — and costs nothing because no reader exists. Per AGENTS.md:73 I will not remove serialized DTO properties
without an explicit instruction.

**Q-C8a:** (a) or (b)?
**Q-C8b:** if (a), confirm the same treatment for persisted instruction lists — `WorkerInstruction.TargetId`
(WorkerInstructionRuntime.cs:44) is a unit tag and is what `ResolveInstructionPoint` matches on. If instruction lists
are ever persisted (round-2 D3), `TargetId` becomes a per-connection value inside stored data and must be rectified
on load, or the instruction must reference `{miningBase}-{label}` and resolve the tag at runtime. Which design?

---

## Round 3 — remaining open items

Still unanswered from round 2 and now blocking:

- **A1** where `Pool` sits in the canonical order VA, VB, TA..YB (last?), and the resulting row count.
- **A2 / Q-C2b** whether self rows (`VA-VA`, `TA-TA`) exist at all.
- **B1 / B2** the pool jit formula: `hatchery + normalize(pool - hatchery) * 2.75` (same for every `X-Pool` pair),
  or a true per-pair minimum of `mineral -> P -> pool` constrained to the footprint circle.
- **B4** what `HarvestA` / `HarvestB` / wait fields hold on a Pool row, given the pool is never harvested and must
  never be stored as (0,0).
- **Round-2 C2 / 1.8** confirm vespene and pool wait fields store their own X,Y purely so no field is (0,0), and are
  never used by an instruction.
- **D1** who owns ID rectification on start — `BabySharkBuildManager`, `OngoingMapData`, or `ObservationManager`.
- **D3** persist each worker's instruction list keyed `0-T1`, or keep rebuilding at startup.
- **D4** does the mining-base prefix go into `AssignedWorkerDto.Role` / `FinalLabel`, or only into key and reference
  strings.
- **E1** should Mining Manager parse `{miningBase}-{labelA}-{labelB}.{Property}` directly against the pair table
  (the only way Pool and Vespene pairs become reachable).
- **E2** if yes, retire the six `MiningTargetDto` Jit fields (BaseDtos.cs:363-368).
- **F1** `jitMR` vs `jitRM`. **F3** version bump to 0.11. **F4** delete dead `MainMineralJitCargoPoints`.
  **F5** retire `TeamPatchAssignmentDto.JitReturnPoint` / `JitWaitPoint`. **F6** is the uncommitted worktree the
  additive-only baseline.

New from round 3: **Q-C1a** (mineral radius 1.0 vs 1.5), **Q-C1b** (`Sm*` points inside the footprint),
**Q-C2a-d** (8-worker rule replacement and per-map team orders), **Q-C7a-c** (reorder, B wait rule, wait gate),
**Q-C8a-b** (delete or refresh persisted tags).

---
---

# Round 4 (owner 2026-09-09: "There was once a circle drawn around the minerals at the correct distance")

This answers Q-C1a and simultaneously exposes a regression in the uncommitted worktree.

## 4.1 The circle: recovered from HEAD, deleted in the worktree

`DrawMineralTargetPoints` and its helper `DrawCircle` exist in HEAD at
`git show HEAD:BabySharkBot/Managers/BabySharkMiningManager.cs` (method at line 252, helper at line 320) and were
called from the draw dispatcher (HEAD line 612: `DrawMineralTargetPoints();` ahead of `DrawAllUnitLabels`,
`DrawWorkerInstructions`, `DrawCcaWaitPoints`).

A repository-wide grep for `DrawMineralTargetPoints|DrawCircle` across `BabySharkBot/` returns **no matches** in the
current worktree. Both methods and the call site are gone.

The exact radii the deleted method drew, quoted from HEAD:

```csharp
DrawCircle(hatcheryPosition, 2.75f, new Color { R = 255, G = 255, B = 255 }, debugHeight);  // white, every spawn
...
DrawCircle(mineral.Position, 1.0f, color, debugHeight);      // every mineral, team color
ManagerDebugService.DrawText("h", ... mineral.HarvestPoint ...);
ManagerDebugService.DrawText("r", ... mineral.ReturnPoint ...);

if (!finalLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase)) { continue; }

DrawCircle(mineral.Position, 1.5f, color, debugHeight);      // A minerals only
ManagerDebugService.DrawLine(mineral.HarvestPoint -> teamAssignment.JitReturnPoint, color);
```

## 4.2 Q-C1a — SUPERSEDED, see section 4.5

**Correction.** The table below was my first reading, and it labelled the 1.5 circle as the "harvest standing circle".
The owner's round-4 second message corrects that: the larger circle is the **role-3 wait circle**, not the harvest
circle. Read section 4.5 for the corrected semantics and the 0.3u discrepancy it exposes. Retained here only to show
what was superseded.

The parts of this section that remain valid: `hatcheryRadius = 5.5f` at InitialMapData.cs:1358 is still the only
genuinely wrong constant, and the 1.0 mineral footprint circle still matches `mineralRadius = 1.0f`.

### Original (superseded) reading

| Circle | Radius | Meaning |
|---|---|---|
| Hatchery | **2.75** | footprint — a worker cannot get closer (answer 1.2: 5.5 diameter / 2.75 radius) |
| Every mineral | **1.0** | footprint — a worker cannot get closer |
| A mineral | **1.5** | harvest standing circle — 0.5u outside the mineral footprint |

So the two constants I flagged as inconsistent are both correct and measure different things:
- `mineralRadius = 1.0f` at InitialMapData.cs:1359 is the **footprint**, matching the drawn 1.0 circle.
- `mineralHarvestOffset = 1.5f` at BabySharkBuildManager.cs:1428 and `mineralRadius = 1.5f` at
  InitialMapData.cs:1190 are the **harvest point**, matching the drawn 1.5 circle.

**Q-C1a is closed.** The only genuinely wrong constant remains `hatcheryRadius = 5.5f` at
InitialMapData.cs:1358 — the diameter used as a radius. Everything else is consistent with the drawn circles.

One asymmetry worth confirming rather than assuming: the return point sits exactly **on** the hatchery footprint
(2.75), while the harvest point sits **0.5u outside** the mineral footprint (1.5 vs 1.0). That is plausible —
returning cargo only needs adjacency, harvesting needs the gather spot — but it is an inference from the radii, not
something stated in canon. **Q-C1a-2: confirm the harvest point stays at 1.5 (0.5u beyond the 1.0 footprint) and
the return point stays exactly on the 2.75 footprint.**

## 4.3 REGRESSION — five diagnostic methods deleted by the uncommitted worktree

Comparing `private void Draw*` methods between HEAD and the worktree in
`BabySharkBot/Managers/BabySharkMiningManager.cs`:

| HEAD (14 methods) | Worktree (11 methods) |
|---|---|
| DrawMineralTargetPoints | **DELETED** |
| DrawCircle | **DELETED** |
| DrawWorkerInstructions | **DELETED** |
| DrawCcaWaitPoints | **DELETED** |
| DrawArrow | **DELETED** |
| DrawAllUnitLabels | DrawAllUnitLabels |
| DrawCenterOfMassLocations | DrawCenterOfMassLocations |
| DrawExpansionCOMCrosshairs | DrawExpansionCOMCrosshairs |
| DrawCenterOfMass | DrawCenterOfMass |
| DrawMineralLabels | DrawMineralLabels |
| DrawExpansionMineralLabels | DrawExpansionMineralLabels |
| DrawVespeneLabels | DrawVespeneLabels |
| DrawExpansionPoints | DrawExpansionPoints |
| DrawSpawningPoolPlacement | DrawSpawningPoolPlacement |
| — | DrawTeamPointDiagnostics (added) |
| — | DrawDebugCircle (added, replaces DrawCircle; used only for COM 0.5f and expansions 2.5f) |

What the deletions cost, from the HEAD source:

1. **DrawMineralTargetPoints** — the 2.75 hatchery footprint circle, the 1.0 mineral footprint circle on all eight
   minerals, the 1.5 harvest circle on A minerals, the `h` / `r` text markers on every harvest and return point, and
   the line from each A mineral's HarvestPoint to its team `JitReturnPoint`. This is the exact "circle drawn around
   the minerals at the correct distance" plus the JIT line, and it is the visual standard recorded in the project
   canon (1.5u circle on A minerals, line from JIT HarvestPoint to JIT ReturnPoint).
2. **DrawCcaWaitPoints** — drew the `W` text marker at each `assignment.JitWaitPoint` in the team-3 color. This is
   the only visualization of the CCAw wait point, and per round 3 it is the field that the pair table currently
   stores as (0,0). With it deleted, the C7 ordering bug became invisible.
3. **DrawWorkerInstructions** and **DrawArrow** — per-worker instruction visualization.

`DrawDebugCircle` (worktree BabySharkMiningManager.cs:2474) is a functional replacement for `DrawCircle` but is only
called for the mineral COM at 0.5f (:2396) and expansion points at 2.5f (:2490). No mineral, hatchery, harvest or
wait-point circle is drawn anywhere in the worktree.

This violates the project's own verification requirement, AGENTS.md:103 — "existing debug labels and drawings remain
present" — and AGENTS.md:13, "preserve unrelated existing worktree changes. Do not reset, revert, or overwrite
changes you did not make." The deletions are not mine.

**Q-R1: restore the five deleted diagnostic methods, or were they removed deliberately?**
My recommendation is restore, adapted rather than reverted: bring back `DrawMineralTargetPoints` and
`DrawCcaWaitPoints` drawing from the **new pair table** instead of the retired `TeamPatchAssignmentDto.JitReturnPoint`
/ `JitWaitPoint` fields, and draw the 1.0 footprint circle plus the 1.5 harvest circle on **B minerals too**, since
answer 1.8 now gives B minerals wait points. That keeps the established visual standard, satisfies AGENTS.md:103,
and makes the C7 ordering bug observable again instead of silent.

**Q-R2: if restored, what does the B-mineral diagnostic look like?**
A minerals currently get two circles (1.0 footprint + 1.5 harvest) and a JIT line. Options for B minerals:
(a) identical — 1.0 + 1.5 + a `W` marker at its new wait point; (b) 1.0 + 1.5 only, no line; (c) 1.0 only.
Which? This also determines whether the drawn circles can be used to verify the B wait point geometrically, the same
way the A circles verified the 0.8u CCAw rule.

**Q-R3: does the hatchery circle stay white and 2.75, and should a second circle mark the pair `ReturnPoint`?**
HEAD drew one white 2.75 circle per hatchery. With the pair table, the return point a worker actually moves to is
`{base}-{labelA}-{labelB}.ReturnPoint`, which is the midpoint of two return points (round-1 B5) and therefore not on
the 2.75 circle. Should the diagnostic draw the pair return point too, so the difference between the footprint
circle and the actual return target is visible?

## 4.4 Effect on the C1 correction

The C1 ruling ("correct anything that is not 5.5 diameter and 2.75 radius") now has a precise, verified scope:

- **Change:** `hatcheryRadius = 5.5f` -> `2.75f` at InitialMapData.cs:1358. This is the only wrong constant.
- **Leave:** `mineralRadius = 1.0f` at InitialMapData.cs:1359 (footprint, matches the drawn 1.0 circle).
- **Leave:** `mineralRadius = 1.5f` at InitialMapData.cs:1190 and `mineralHarvestOffset = 1.5f` at
  BabySharkBuildManager.cs:1428 (harvest circle, matches the drawn 1.5 circle).
- **Still needs Q-C1b:** `smReturn` at BabySharkBuildManager.cs:1432 lands 1.0u from the hatchery center, inside the
  2.75 footprint; `smallInset = 1.75f` at InitialMapData.cs:1191/1360 puts `smReturn` at 2.75 - 1.75 = 1.0u, same
  problem. The deleted diagnostic drew an `r` marker at `mineral.ReturnPoint` but never drew the `Sm*` points, so
  there is no visual precedent for them.
- **Version bump required:** the corrected 2.75 invalidates every persisted `ReturnPoint` already stored at 5.5.

---

## 4.5 The two circles on A minerals are footprint and wait circle — and they disagree with runtime by 0.3u

Owner (round 4, second message): "Two circles were drawn around 'A' minerals. The '3' workers went to a wait point on
the larger circle that did not interfere with the '1' worker."

This confirms the semantics of the two radii recovered from HEAD:

| Drawn in HEAD | Radius | Applied to | Meaning per owner |
|---|---|---|---|
| `DrawCircle(mineral.Position, 1.0f, color, debugHeight)` (HEAD line 280) | **1.0** | every mineral | inner circle — mineral footprint |
| `DrawCircle(mineral.Position, 1.5f, color, debugHeight)` (HEAD line 289) | **1.5** | **A minerals only** | larger circle — the role-3 wait circle |
| `DrawCircle(hatcheryPosition, 2.75f, white, debugHeight)` (HEAD line 266) | **2.75** | hatchery | footprint |

The A-only scope is explicit in HEAD: `if (!finalLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase)) { continue; }`
immediately precedes the 1.5 circle. So B minerals were drawn with the 1.0 circle only.

### The discrepancy

The runtime CCAw wait point is **not** at 1.5u. From BabySharkBuildManager.cs:226-239:

```csharp
var directionX = aMineral.HarvestPoint.X - aMineral.Position.X;
var directionY = aMineral.HarvestPoint.Y - aMineral.Position.Y;
var harvestDistance = MathF.Sqrt(directionX * directionX + directionY * directionY);
var scale = (harvestDistance + 0.8f) / harvestDistance;
var ccaWaitPoint = new Vector2Dto(
    aMineral.Position.X + directionX * scale,
    aMineral.Position.Y + directionY * scale,
    aMineral.HarvestPoint.Z);
```

which places the wait point at `harvestDistance + 0.8` from the mineral center, along the ray from the mineral toward
its harvest point.

`harvestDistance` resolves to exactly **1.0**, traced through:
- runtime minerals take `HarvestPoint = cargoPoint.HarvestPoint` from `MainMineralCargoPoints`
  (BabySharkBuildManager.cs:931, :756)
- `MainMineralCargoPoints` is built by `BuildHarvestReturnCargoPoint`, where
  `harvestPoint = GetPointOnLine(resourceVector, -unitDirection, mineralRadius, ...)` with
  `const float mineralRadius = 1.0f` (InitialMapData.cs:1359, :1363)

So the actual wait radius is **1.0 + 0.8 = 1.8u**, while the larger drawn circle is **1.5u** — a 0.3u mismatch.

### Why the drawing is stale

Commit chronology, verified with `git show -s --date=short`:
- `5b75d32` **2026-08-31** "Is Carrying Was Carrying" — contains both `DrawCircle(mineral.Position, 1.0f` and
  `DrawCircle(mineral.Position, 1.5f`
- `258f4ab` **2026-09-01** "Wait Circle 0.8u" — changes `var scale = (harvestDistance + 1f) / harvestDistance;` to
  `var scale = (harvestDistance + 0.8f) / harvestDistance;`

So even before the 0.8 tuning the wait radius was `1.0 + 1.0 = 2.0u`, and the drawn larger circle was 1.5u. The 1.5
drawing never matched the runtime wait radius in either version. It was drawn one day before the last tuning and
never updated — and it is now deleted entirely from the worktree (section 4.3).

### The interference question

The owner says the role-3 wait point is on the larger circle **and does not interfere with the role-1 worker**. The
current formula puts role 3 *collinear* with role 1 — same ray from the mineral toward the hatchery, 0.8u beyond the
harvest point, i.e. role 3 stands directly behind role 1 as seen from the mineral. That is a form of non-interference
(role 3 does not occupy role 1's harvest spot) but it is not angular separation on a shared circle.

**Q-W1: which is authoritative for the wait circle — the drawn 1.5u or the runtime 1.8u (1.0 footprint + 0.8)?**
This is the value that gets stored as `WaitPointA` in the new pair table, and it is the visual verification standard.
If 1.5 is correct, the 0.8 offset must become 0.5 and the tuning commit `258f4ab` is superseded. If 1.8 is correct,
the restored diagnostic must draw the larger circle at `harvestDistance + 0.8` rather than the hard-coded 1.5f.

**Q-W2: is collinear-behind-role-1 the intended non-interference, or must the wait point be angularly offset on the
circle?** If angular, what determines the angle — away from the B mineral, away from the hatchery ray, or a fixed
offset? Note this interacts with the existing canon that Role 3 wait points are calculated exclusively from A-mineral
geometry, ignoring B minerals.

**Q-W3: what does a B mineral's wait point mean, given B minerals were drawn with one circle only?**
HEAD drew the larger circle for A minerals exclusively. Answer 1.8 now requires **all B minerals to have a wait
point**. At 12 workers the B mineral is mined by role 2 alone — roles 1 and 3 both mine A. So:
- which worker ever stands on a B mineral's wait point?
- is it stored now purely so the field is never (0,0) and so a later A/B alternation has it ready?
- should the restored diagnostic draw the larger circle on B minerals too, once they have wait points?

**Q-W4: does the larger circle radius become per-mineral rather than a constant?**
Since the wait radius is `harvestDistance + 0.8` and `harvestDistance` is derived from each mineral's own harvest
point, the honest diagnostic draws a circle of that computed radius per mineral instead of a literal `1.5f`. Confirm
the restored drawing should compute the radius from the stored wait point (distance from mineral position to
`CcaWaitPoint`) rather than hard-code a constant, so the circle can never drift from the geometry again.

## 4.6 Wait point required for ALL minerals; the 0.8 offset is treated as a bandaid

Owner (round 4, third message): "I need that wait point for all minerals. The runtime was probably changed to the
actual closest distance — a bandaid, not a fix. The actual point the worker moved to would be the correct wait
distance."

Two rulings taken from this:
1. **Every mineral gets a wait point, A and B.** HEAD's diagnostic drew the larger circle for A minerals only
   (`if (!finalLabel.EndsWith("A", ...)) { continue; }` before the 1.5 circle), and
   `CalculateCcaWaitPointsForCurrentSpawn` also selects A minerals only
   (BabySharkBuildManager.cs:194-195, `FirstOrDefault(mineral => mineral?.FinalLabel?.EndsWith("A", ...) == true)`).
   Both must change.
2. **The authoritative wait distance is where the worker actually ends up standing**, not the tuned constant.

### Attempted empirical measurement — telemetry cannot support it

Searched the recorded runs in `BabySharkBot/bin/Debug/net9.0/data/mining_tests/`. The full game is
`worker_commands_BabyShark_20260908092614.jsonl` (100,126,511 bytes, 164,089 `WorkerObservation` records).

**Blocker 1 — the field named `WaitPoint` logs the harvest point instead.** WorkerCommandTelemetry.cs:138-139:
```csharp
WaitPointX = target?.HarvestPoint?.X,
WaitPointY = target?.HarvestPoint?.Y,
```
So `WaitPointX/Y` is the mineral's `HarvestPoint`, never the CCAw wait point. No record anywhere contains the
actual wait-point coordinates.

**Blocker 2 — the wait-state flag can never fire.** WorkerCommandTelemetry.cs:116-120 computes `waitState` from
`workerLabel.EndsWith("3", ...)` combined with a distance test against `target.HarvestPoint` using `1.5f * 1.5f`.
Measured across the full game:
```
role3true  = 0
role3false = 164089
```
and the `RoleNumber` distribution is a single bucket — `'[]' = 164089`. `WorkerLabel` is empty in every record, so
`EndsWith("3")` is always false and the flag is always false. Per-role isolation is therefore impossible from this
file.

**What the 9 distinct `WaitPointX/Y` values actually are** — i.e. the 8 minerals' harvest points, plus one blank:
```
Mineral Side      X         Y
(blank)         0         0
BA      A     133.044   22.794
BB      B     133.148   21.024
SA      A     129.615   19.423
SB      B     132.260   20.173
TA      A     127.083   19.497
TB      B     127.929   18.497
YA      A     133.000   24.500
YB      B     134.009   23.632
```
All eight are non-zero, so every mineral already has a usable harvest point. This is not wait-point data.

**Worker distance to the harvest point** was aggregated per mineral in 0.1u buckets. Every mineral shows a dominant
cluster at 0.2-0.5u (workers at the harvest spot) plus scattered transit samples. Notable secondary mass: SA at 2.6u
(6025 records) and 1.7u (5013), TA at 2.6u (4844) and 0.2u (5778), YB at 1.6u (4844), YA at 1.0u (5760). These
cannot be attributed to role 3 because of Blocker 2, and they mix travel with settled positions, so **no wait radius
can be derived from them.** Reporting them as evidence of the wait distance would be fabrication.

### What geometry does establish

- Runtime wait radius = `harvestDistance + 0.8` from the mineral center (BabySharkBuildManager.cs:226-239).
- `harvestDistance` = 1.0, traced: runtime minerals take `HarvestPoint = cargoPoint.HarvestPoint`
  (BabySharkBuildManager.cs:931, :756) <- `MainMineralCargoPoints` (InitialMapData.cs:1006) <-
  `BuildHarvestReturnCargoPoint` with `const float mineralRadius = 1.0f` (InitialMapData.cs:1359, :1363).
- So the current wait radius is **1.8u** from the mineral center, i.e. **0.8u beyond the harvest point**.
- The drawn larger circle was **1.5u**. Mismatch 0.3u.
- Commit `258f4ab` (2026-09-01, "Wait Circle 0.8u") changed `harvestDistance + 1f` to `harvestDistance + 0.8f`, one
  day after `5b75d32` (2026-08-31) introduced the 1.5f circle. The circle was stale from the moment it was written
  and never matched either the 2.0u or the 1.8u runtime value.

**So there are three candidate distances and the recorded data cannot arbitrate:** 1.5u (drawn), 1.8u (current
runtime, the bandaid), 2.0u (runtime before `258f4ab`).

### Questions

**Q-W5: how should the actual wait distance be obtained, given the telemetry is mislabeled?**
My proposal, in order:
1. Fix WorkerCommandTelemetry.cs:138-139 to log the real wait point (the mineral's `CcaWaitPoint`, or
   `assignment.JitWaitPoint`) instead of `HarvestPoint`, and log the worker's own settled position alongside it.
2. Fix the `waitState` condition at :116-120 so it can fire — it currently depends on `workerLabel`, which is empty
   in every record. Role must come from the assigned worker's role, not the label string.
3. Run one game, then measure the distance from each mineral center to the settled role-3 position and use that as
   the canonical wait radius for all minerals.

Confirm this sequence, or state the distance directly if you already know it.

**Q-W6: while the true distance is unknown, what value goes into the pair table?**
Options: (a) keep the current `harvestDistance + 0.8` = 1.8u and correct it once measured; (b) use the drawn 1.5u
now, which means changing the 0.8 offset to 0.5 and superseding `258f4ab`; (c) block the pair table until measured.
Per the no-invention rule I will not pick a number that has no source.

**Q-W7: does the B-mineral wait point use the same ray and same distance as A?**
The A formula runs along the ray from the mineral center toward that mineral's own harvest point, which is the
mineral-to-hatchery direction. For a B mineral, is the wait point on the same ray (mineral -> its own harvest point ->
hatchery) at the same radius, so role 2's wait point sits directly behind role 2's harvest spot exactly as role 3's
does behind role 1's? Or must the B wait point be offset to avoid a different worker?

**Q-W8: with wait points on all 8 minerals, does the diagnostic draw two circles on all 8?**
HEAD drew 1.0 on every mineral and the larger circle on A only. Should the restored diagnostic draw both circles on
every mineral, each at its own computed radius (from the stored wait point) rather than a literal constant?

## 4.7 Round 5 — role-3 flags are outdated; the goal is list-only commands

Owner (round 5): "Role 3 flags are outdated. Build manager creates a list for the Mining manager to follow on start
or on frame 0. The full replay was workers not working because they all had bad instructions that were a mix of the
list instructions and hard coded a/b switch instructions along with role three which should have only been active
with the bad commands the first few frames. Getting the mining manager to ONLY use list commands should solve a lot
of problems."

This retires **Q-W5/Q-W6's measurement plan** as the immediate priority: the telemetry role-3 flags are outdated by
ruling, so they were never going to yield a correct wait distance. It also confirms round 4's Q-R1 direction — the
diagnostic must follow the list, not the flags.

### Command production is ALREADY list-only — verified

`BabySharkMiningManager.OnFrame` produces worker commands from exactly one call:
```csharp
actions.AddRange(ExecuteRuntimeWorkerInstructions(observation));   // BabySharkMiningManager.cs:606
```
That is the only command-producing call in `OnFrame` (BabySharkMiningManager.cs:571-614). Tracing every action
factory:

- `CreateInstructionMoveAction` (:1946), `CreateInstructionGatherAction` (:1966), `CreateInstructionReturnAction`
  (:1990), `CreateInstructionBuildExtractorAction` (:2009) — all four are called **only** from
  `ExecuteWorkerInstruction` (:1250, :1256, :1265, :1266, :1274, :1278, :1282, :1286).
- `ExecuteWorkerInstruction` (:1230) is called only from `ExecuteRuntimeWorkerInstructions` (:1136) and
  `LoadNextInstructionSet` (:1321), which is itself reached only through the `LoadInstructionSet` instruction
  (:1289) inside that same loop.
- `ExecuteJustInTimeMining` (:1506-1561) is **dead and already gutted** — a repository-wide search for the
  identifier returns exactly one hit, its own definition. No caller. Its body issues no commands: it resolves
  assignments, finds the town hall, writes three `[MINING REACH]` console lines (:1535, :1540, :1557) and returns
  the empty `actions` list it created at :1508. The hard-coded A/B switch logic that used to live here is already
  gone; only the husk remains.
- `chrisCrossAppleSause` issues no commands at all — a search for `Abilities.`, `UnitCommand`, `new SC2Action`,
  `Order(` in that file returns no matches. It only sets `Settings.ccaMining = true` (chrisCrossAppleSause.cs:11).
- `ExtractorTrickService` uses `LoadInstructions` for all worker movement (`prepMR` :151, `prepW` :191, `jitMH`
  :468). Its only direct `CreateUnitCommand` calls are `Abilities.TRAIN_DRONE` (:411) and
  `Abilities.CANCEL_BUILDINPROGRESS` (:438) — neither moves a worker.

**So the mixing the owner describes is not in command production. It is in point resolution.**

### The actual mixing: `ResolveInstructionPoint` reaches outside the list

`WorkerInstructionPoint` has 14 members (WorkerInstructionRuntime.cs:22-37). `ResolveInstructionPoint`
(BabySharkMiningManager.cs:1347-1365) resolves 13 of them, and only **one** reads from the list's own stored point:

| Point | Source it reads | List-driven? |
|---|---|---|
| `StoredTarget` (:1356) | `runtimeWorker.StoredTargetPoint` | **YES — the only one** |
| `Harvest` (:1349) | `target.SmHarvestPoint` / `target.HarvestPoint` | No — `MiningTargetDto`, chosen by `_speedMiningActive` |
| `Return` (:1350) | `target.SmReturnPoint` / `target.ReturnPoint` | No — same flag |
| `JitHarvestA/B` (:1351-1352) | `target.JitHarvestA` / `.JitHarvestB` | No — copied in by `ApplyJitPairTarget` (BabySharkBuildManager.cs:1319-1324) |
| `JitWaitPointA/B` (:1353-1354) | `target.JitWaitPointA` / `.JitWaitPointB` | No — same copy |
| `JitReturnPoint` (:1355) | `target.JitReturnPoint` | No — same copy |
| `Staging` (:1357-1358) | `ResolveCcaWaitPoint(teamAssignment, role)` | No — live team lookup, marked PROTECTED at :1337-1339 |
| `BumpCcaWaitCircle` (:1362-1363) | `ResolveCcaWaitPoint(teamAssignment, role)` | No — same |
| `BumpPartner` (:1359) | live observed partner position (:1368-1388) | No |
| `BumpMidpoint` (:1360) | live observed partner + mineral (:1391-1427) | No |
| `BumpHarvestCircle` (:1361) | live observed worker + mineral (:1429-1465) | No |

So the instruction list picks an enum, and 12 of 13 enums then resolve from somewhere else — `MiningTargetDto`
(the hard-coded A/B switch data, populated by `GetInstructionLabels` + `CreateMiningTarget`), the live team
assignment, or the worker's current observed position. That is the "mix of list instructions and hard coded a/b
switch instructions."

`ResolveInstructionPoint` also cannot find a target unless `instruction.TargetId` matches a
`MiningTargetDto.ResourceUnitId` (:1331), and `MiningTargets` only ever contains minerals
(BabySharkBuildManager.cs:1149-1155) — which is why Pool and Vespene pairs remain unreachable (round 2, C10).

### The role-3 coupling that is now ruled outdated

Three places still branch on role 3 or on `IsMagannatha12WorkerOverride`:

1. `SeedRuntimeWorkerInstructions` — `roleThree = role.EndsWith("3", ...)`, then `useCcaw`, and a distinct
   `gatherFrame = 55` for CCAw versus `15` otherwise, plus a different movement point
   (BabySharkBuildManager.cs:292-313). This is where "role three should have only been active with the bad commands
   the first few frames" bites: role 3 gets a structurally different opening list, not just a different point.
2. `ResolveFirstLinePoint` — `"CCAw" => JitWaitPointA` (BabySharkBuildManager.cs:600).
3. `ResolveObservedBumpPartner` / `ResolveObservedBumpMidpoint` — hard-coded `"T3"↔"T1"`, `"Y3"↔"Y1"` pairs
   (BabySharkMiningManager.cs:1375-1377, :1401-1403).
4. Telemetry — `waitState` from `workerLabel.EndsWith("3", ...)` (WorkerCommandTelemetry.cs:116), which measured
   zero true in 164,089 records because `WorkerLabel` is always empty.

### Questions

**Q-L1: confirm the target design — every point comes from the list.**
Proposal: the Build Manager resolves each instruction's point at list-construction time on frame 0 (or start) and
writes it into the instruction itself, so the Mining Manager's resolution collapses to `StoredTarget`-style reads
only — `runtimeWorker.StoredTargetPoint` for a set's point, with no `MiningTargetDto`, no `_speedMiningActive`
branch, no live team lookup, and no observed-position math. That satisfies answer 1.4 ("workers should not have
multiple points for mining, just a point for the next set"). Confirm, or state which of the 12 non-list resolutions
must survive.

**Q-L2: what happens to the four `Bump*` points, which are inherently live?**
`BumpPartner`, `BumpMidpoint`, `BumpHarvestCircle` and `BumpCcaWaitCircle` all compute from the partner's or
worker's **current observed position** (BabySharkMiningManager.cs:1423-1426, :1450-1464), so they cannot be
pre-resolved at frame 0. Options: (a) the bump sets stay as the one sanctioned exception; (b) the bump choreography
is replaced by pre-computed geometry from the pair table; (c) the Magannatha bump role is retired. Which?

**Q-L3: is `_speedMiningActive` still a thing?**
`UpdatePhaseState(workerCount)` runs every frame before the list executes (BabySharkMiningManager.cs:604-605) and
`Harvest`/`Return` switch between `Sm*` and normal points based on `_speedMiningActive` (:1349-1350). If every point
is pre-resolved into the list, this flag has no consumer left in point resolution. Should it be removed from the
mining path, kept for diagnostics only, or does it still gate something?

**Q-L4: does role 3 stop having a different opening list?**
Confirm whether `SeedRuntimeWorkerInstructions` should emit the **same list structure** for every role — differing
only in which pair reference and which property (`HarvestA` vs `HarvestB` vs the wait point) the first line stores —
so the role-3 CCAw/bump special-casing at BabySharkBuildManager.cs:292-313 is replaced by data in the list rather
than by a code branch. The `gatherFrame = 55` versus `15` difference and the `Staging` movement point would then be
list values, not role tests.

**Q-L5: do the hard-coded partner pairs go?**
`"T3"↔"T1"` and `"Y3"↔"Y1"` (BabySharkMiningManager.cs:1375-1377, :1401-1403) and the
`IsMagannatha12WorkerOverride` guards are exactly the "hard coded" coupling the owner describes. If Q-L2 picks (b)
or (c), these die. Confirm they should be removed rather than preserved as Magannatha-specific behavior — because
`Worker Instruction List.MD:21` still specifies `bump3` sets for T1/Y1/T3/Y3, and removing them changes documented
canon.

**Q-L6: which is the first deliverable?**
Given this ruling, my proposed order is:
1. Delete the dead `ExecuteJustInTimeMining` husk (:1506-1561) — zero callers, no commands, only stale console
   logging. Removing it is cleanup, not a behavior change.
2. Make the Build Manager resolve every instruction point into the list at frame 0, and reduce
   `ResolveInstructionPoint` to the list-driven reads (Q-L1).
3. Then build the pair table with the mining-base key and canonical order (round 2 A1/A2/B1/B4, round 3 C1/C2).
4. Then restore the diagnostics reading from the list (round 4 Q-R1), including the wait circle on all 8 minerals.

Confirm this order, or tell me to start elsewhere.

## 4.8 Round 6 — answers 1-5, and question 1 restated plainly

Owner (round 6):
1. "What is your question."
2. "do not do anything with bump"
3. "The points collected for a Mineral or Vespine going to itself is Speed Mining. Where the worker continues to
   mine the same resource and does not switch to a different one after harvest."
4. "Lists are independent for each worker. Some workers go straight to their pattern, others have a cycle of
   instructions before the pattern. This is a simple list to follow. Do not over engineer this, do not fabricate
   rules, do not create bogus logic."
5. "NO. You can delete old A/B logic."

### 1. My question, stated plainly

**Should the `StoreTargetPoint` / `UseTargetPoint` instruction rows resolve their point by parsing their own
`TargetPointReference` string (e.g. `TA-TB.HarvestA`) against the pair table — instead of reading
`MiningTargetDto.JitHarvestA`, which `ApplyJitPairTarget` copied in earlier?**

Evidence from the frame-0 dump `worker_instruction_lists_frame0_20260909090107.json` (StartIndex=0, 12 workers):

The lists are **already** store-once-then-use-stored. Point distribution across all 468 instructions:
```
StoredTarget        = 240     <- every Move/Gather/Return row reads the worker's single stored point
None                =  84     <- Wait rows and Jump rows
JitReturnPoint      =  36
BumpPartner         =  30
JitHarvestB         =  28
JitHarvestA         =  28
Harvest             =  12
Staging             =   6
BumpCcaWaitCircle   =   2
JitWaitPointA       =   2
```
So 240 of 468 rows already read only `StoredTargetPoint` — answer 1.4's "just a point for the next set" is already
the runtime shape. The remaining Jit* rows (36+28+28+2 = 94) are the `StoreTargetPoint` / `UseTargetPoint` rows
whose only job is to put a value into `StoredTargetPoint`. Command distribution confirms it:
```
Move = 242, Wait = 72, UseTargetPoint = 36, Return = 36, Gather = 36, StoreTargetPoint = 34, Jump = 12
```

The reference strings those 94 rows carry are already exactly the pair-key form, 26 distinct values:
```
TA-TB.HarvestA  TA-TB.HarvestB  TA-TB.ReturnPoint     TB-TA.HarvestA  TB-TA.HarvestB  TB-TA.ReturnPoint
SA-SB.*         SB-SA.*         BA-BB.*  BB-BA.*      YA-YB.*  YB-YA.*   TA-TB.WaitPointA  SA-SB.WaitPointA  BA-BB.WaitPointA
```
But nothing parses them. `StoreReferencedTargetPoint` only null-checks the string and copies a point that was
resolved by enum from `MiningTargetDto` (BabySharkMiningManager.cs:1294-1303, :1351-1355), and
`ApplyJitPairTarget` had already copied that value out of the pair table into the DTO
(BabySharkBuildManager.cs:1309-1325).

So the pair table -> DTO copy is a redundant hop, and it is the hop that breaks Pool/Vespene: the DTO lookup
requires `TargetId` to match a `MiningTargetDto.ResourceUnitId` (:1331) and those DTOs only contain minerals
(BabySharkBuildManager.cs:1149-1155).

**Two further observations the dump proves, which I will not act on without a ruling:**
- The keys have **no mining-base prefix** (`TA-TB`, not `0-TA-TB`) and are **directed duplicates** (`TA-TB` and
  `TB-TA` both present), contradicting answers 1.1 and 1.6.
- `WaitPointA` appears in only 3 of 26 references and **no `WaitPointB` reference exists at all**, which matches
  round 3's C6/C7 finding that B minerals have no wait point yet.

**Q-L1 (restated): yes or no — resolve by parsing `TargetPointReference` against the pair table, and delete the
`MiningTargetDto` Jit copy step?**

### 2. Bump is frozen

"Do not do anything with bump." I will not modify, re-derive, or delete any `Bump*` code:
`WorkerInstructionPoint.BumpPartner` / `BumpMidpoint` / `BumpHarvestCircle` / `BumpCcaWaitCircle`
(WorkerInstructionRuntime.cs:27-30), `BuildBumpInstructions` (BabySharkBuildManager.cs:424-475),
`ResolveObservedBumpPartner` (BabySharkMiningManager.cs:1368-1388), `ResolveObservedBumpMidpoint` (:1391-1427),
`ResolveBumpHarvestCirclePoint` (:1429-1465), the `"T3"↔"T1"` / `"Y3"↔"Y1"` pair literals (:1375-1377, :1401-1403),
and the `bumpT3` / `bumpY3` sets (17 instructions each in the dump, 49-instruction lists for T3 and Y3 versus 37 for
every other worker).

This closes round-5 **Q-L2** and **Q-L5**. The bump rows keep their live-position resolution as the one sanctioned
exception; they are the 30 `BumpPartner` + 2 `BumpCcaWaitCircle` rows above. Everything else is fair game.

### 3. Self pairs = Speed Mining — this settles round-2 A2 and round-3 Q-C2b

"The points collected for a Mineral or Vespine going to itself is Speed Mining, where the worker continues to mine
the same resource and does not switch to a different one after harvest."

Rulings taken:
- **Self rows exist** and are the Speed Mining definition: `TA-TA`, `TB-TB`, …, `VA-VA`, `VB-VB`. Round-2 A2 answered yes.
- A worker on a self pair does **not** alternate. It harvests the same resource and returns to the same hatchery,
  repeatedly. That is the "continue to mine the same resource" case.
- Non-self pairs are the A/B switch: harvest one resource, return via the pair jit point, harvest the other.
- **Round-3 Q-C2b answered**: at an 8-worker start, where `W1 -> Mineral[1]` and each worker owns one mineral, the
  correct reference is the worker's **self pair** (`0-TA-TA` for `0-T1`), because that worker never switches
  resources. It is not the team pair with `HarvestA`/`HarvestB`.
- This also means `IsSpeedMining` on `MiningTargetDto` (BaseDtos.cs:360) and the `CreateMiningTarget`
  `speedMining`/`abSwitch` flags (BabySharkBuildManager.cs:1388, :1405-1407) map directly onto
  self-pair / non-self-pair — same distinction, expressed twice.

**Q-S1: confirm the self-pair return point.** For `TA-TA`, `ReturnPoint` is currently
`Midpoint(from.Return, to.Return)` with `from == to`, i.e. exactly `TA`'s own return point
(BabySharkBuildManager.cs:1289). That is already correct under answer 3 — no special case needed. Confirm, and
confirm `HarvestA == HarvestB == TA`'s own harvest point on a self row, so a Speed Mining worker can use either
property.

**Q-S2: does a Speed Mining worker still use the `Sm*` inset points, or the plain harvest/return?**
`ResolveInstructionPoint` picks `SmHarvestPoint`/`SmReturnPoint` when `_speedMiningActive`
(BabySharkMiningManager.cs:1349-1350), and `_speedMiningActive` is updated every frame by
`UpdatePhaseState(workerCount)` (:604-605). The pair table has no `Sm*` fields (BaseDtos.cs:308-323). If self pairs
*are* Speed Mining, the `Sm*` values need a home: (a) add them to the self rows; (b) drop `Sm*` and use the plain
harvest/return for self pairs; (c) keep the `_speedMiningActive` flag as the only remaining non-list resolution.
Round-5 Q-L3 is the same question. Which?

### 4. Lists are independent per worker; do not over-engineer

"Lists are independent for each worker. Some workers go straight to their pattern, others have a cycle of
instructions before the pattern. This is a simple list to follow."

The dump confirms this is already true and needs no new mechanism:
- 12 independent lists, one per worker, `idx=0` at frame 0.
- Workers that go straight to the pattern: T2, S1, S2, B1, B2, S3, B3 — 37 instructions, first set `jitMH` or `CCAw`.
- Workers with a cycle before the pattern: T1 and Y1 open with `jitMHb` (37 instructions); T3 and Y3 open with
  `bumpT3` / `bumpY3` (49 instructions) and then fall into the same `jitRM>jitMH` pattern.
- Every list ends with `last-line` (a `Jump`, 12 total) back into the repeating pattern.

So "independent list per worker, some with a preamble, all ending in a repeating pattern" is the existing shape. I
will not add a list-builder abstraction, a rules engine, or a shared-template mechanism on top of it. Per-worker
lists stay built per worker, as at BabySharkBuildManager.cs:314-320.

This also retires round-2 **Q-D20** (per-worker point values): the lists are already independent; only the point
*source* changes under question 1.

### 5. Old A/B logic may be deleted

"NO. You can delete old A/B logic." Answering round-5 Q-L4 (no, roles do not need to keep differing by code branch)
and authorizing deletion of the superseded A/B paths:

Authorized for deletion, with evidence each is superseded:
- `ExecuteJustInTimeMining` (BabySharkMiningManager.cs:1506-1561) — dead husk, one grep hit, issues no commands.
- `GetInstructionLabels`'s 8-worker `switch` (BabySharkBuildManager.cs:1369-1385) — the cross-team table
  `T1 -> TA, TA, SA` etc. that contradicts answer C2; replaced by the self-pair rule from answer 3.
- The `MiningTargetDto` Jit copy step: `ApplyJitPairTarget` (BabySharkBuildManager.cs:1309-1325) and the six fields
  `JitPairKey`, `JitWaitPointA/B`, `JitHarvestA/B`, `JitReturnPoint` (BaseDtos.cs:363-368) — redundant once the
  reference string is parsed. Contingent on Q-L1 = yes.
- `IsSpeedMining` / `IsABSwitch` / `IsInitialMineralAssignment` on `MiningTargetDto` (BaseDtos.cs:360-362) and the
  `speedMining`/`abSwitch` parameters of `CreateMiningTarget` (:1388-1407) — superseded by self-pair vs non-self-pair.
  Contingent on Q-S2.
- `MainMineralJitCargoPoints` / `MiningPairCargoPointDto` — written at InitialMapData.cs:936, :1008 and
  BabySharkBuildManager.cs:944-949, read nowhere (round-2 C9).

NOT authorized by this answer, still needing an explicit ruling:
- `TeamPatchAssignmentDto.JitReturnPoint` / `JitWaitPoint` (BaseDtos.cs:334-335). `JitWaitPoint` is the CCAw
  storage the runtime still reads at BabySharkMiningManager.cs:1491-1494, and `JitReturnPoint` still overrides a
  target's return point at BabySharkBuildManager.cs:1165-1168. Round-2 F5.
- `_speedMiningActive` / `UpdatePhaseState` — see Q-S2.
- Anything bump-related — frozen by answer 2.

**Q-DEL1: confirm the five authorized deletions above, and rule on `TeamPatchAssignmentDto.JitReturnPoint` /
`JitWaitPoint`.**

## 4.9 Round 7 — parse the reference; bump and 8-worker on hold; `Midpoint` is a Sharky artifact; delete freely

Owner (round 7):
1. "`0-TA-TB.HarvestA` will use the table for Start location 0 and the harvest point calculated for TA that is the
   optimal harvest point for mining pair TA and TB. The worker has two legs in its journey: to and from mineral TA to
   the Hatchery to return cargo, then if the instructions in the list specify, go to TB to harvest minerals. The
   Mining Manager does not look ahead or calculate points. `MiningTargetDto.JitHarvestA` and `ApplyJitPairTarget`
   are not likely to be valid for processing a list of instructions."
2. Bump "will probably involve Machine Learning to find the optimum pattern. Once the list of instructions is
   working I would like to go back to that. On hold until after it works."
3. "8 worker is not a priority, also on hold."
4. "Midpoint is inherited from Sharky and very likely is confusing the halfway point that Sharky did something with
   as a point halfway between the Hatchery and the Mineral. I believe the agent confused that with the optimal return
   point being halfway between the speed mining return points of the 'A' mineral and the speed mining return points of
   the 'B' mineral. Two different concepts but similar words to a Large Language Model. I suspect that since I use the
   word midpoint in describing what I was trying to do that the agent latched on to Sharky's use of the word midpoint
   and confused the two concepts. JIT is not used by any BOT it is my invention."
5. "The Workers move all over except where they are supposed to go. I suspect the same variables were used and given
   to all 12 workers instead of independent points."
6. "Please comment in code that 8 worker requires a complete rework after 12 worker is satisfactory. Feel free to
   delete non-working code related to worker instructions."

### Answer 1 — Q-L1 is YES: parse the reference string

- `0-TA-TB.HarvestA` = mining base `0` -> pair table for start location 0, labels `TA`/`TB`, property `HarvestA`.
- `HarvestA` is the **optimal harvest point for TA computed for the pair TA/TB** — pair-specific, per the owner's
  wording. This is the semantic I had been treating as TA's generic harvest point.
- Two legs only: mineral -> hatchery (return cargo), then hatchery -> the other mineral if the list says so.
- **The Mining Manager does not look ahead and does not calculate points.** It reads a coordinate.
- `MiningTargetDto.JitHarvestA` and `ApplyJitPairTarget` are ruled invalid for list processing.

Closes round-5 **Q-L1** (yes) and round-2 **C10/E1** (parse against `MainJitPairReturnCalculations[startIndex]`).

**Q-P1: where does "optimal harvest point for TA within pair TA/TB" come from?**
Today `HarvestA` is copied from TA's single generic harvest point — `HarvestA = from.Harvest`
(BabySharkBuildManager.cs:1299), `from.Harvest` = `mineral.HarvestPoint` (:1243) — the same value every pair shares
for TA. So `0-TA-TB.HarvestA`, `0-TA-SA.HarvestA` and `0-TA-YB.HarvestA` are all identical today, which contradicts
pair-specific optimality. Either (a) a new per-(from, to, property) calculation that does not exist anywhere in the
repo — in which case what makes it optimal for the pair — or (b) TA's own harvest point shared by every pair
containing TA, with "optimal for the pair" describing the existing value. I will not invent a formula.

### Answer 2 — bump stays frozen and parked

No bump code modified or deleted: `bumpT3`/`bumpY3` sets (17 instructions each in the frame-0 dump),
`BuildBumpInstructions`, the three `ResolveObservedBump*` methods, the `T3<->T1` / `Y3<->Y1` literals, and
`BumpCcaWaitCircle` all stay exactly as they are. Closes round-5 **Q-L2**.

### Answer 3 — 8-worker on hold, but a comment is required

- `GetInstructionLabels` returns `Array.Empty<string>()` for any count that is not 12 and not 8
  (BabySharkBuildManager.cs:1369-1372); an 8-worker start reaches the fabricated cross-team table at :1374-1385.
- Answer 6 requires a code comment: 8-worker needs a complete rework after 12-worker is satisfactory.

**Q-8W1: what happens to the 8-worker `switch` now?** (a) leave it, add only the comment; (b) delete the fabricated
table and return `Array.Empty<string>()` for 8, with the comment, so an 8-worker start produces no targets rather
than wrong ones; (c) delete it and route 8-worker through the self-pair rule from round-6 answer 3. "On hold" reads
as (a) or (b). I will not pick.

### Answer 4 — the Sharky `midpoint` diagnosis is confirmed from source

Sharky's `midPoint`/`MidPoint` means **a map-edge waypoint for air harass pathing**, halfway between the target and
the forward defense point, placed on a map border:
```csharp
midPoint = new Point2D { X = 0, Y = TargetingData.ForwardDefensePoint.Y };                     // left edge
MidPoint = new Point2D { X = MapData.MapWidth, Y = (target.Y + ForwardDefensePoint.Y) / 2f };  // right edge
```
PhoenixGroupMicroController.cs:197-218, BansheeGroupMicroController.cs:186-207,
MutaliskGroupMicroController.cs:183-204, OracleWorkerHarassTask.cs:21 and :383-416. Nothing in Sharky computes a
halfway point between two mineral return points. The word was reused for an unrelated concept.

**Current JIT code does not match your stated intent.** `Midpoint` (BabySharkBuildManager.cs:1344-1350) is applied to
`from.Return`/`to.Return` (:1289), sourced from `mineral.ReturnPoint` (:1243) — the **normal** return point.
`SmReturnPoint` is never referenced by `PopulateJitPairReturnCalculations` at all, yet your intent is "halfway
between the speed mining return points of A and B".

**Q-M1: confirm formula and rename.**
- `ReturnPoint = (SmReturnPoint_A + SmReturnPoint_B) / 2`, using `SmReturnPoint` not `ReturnPoint`. These differ:
  `ReturnPoint` sits at `hatcheryRadius` from the hatchery, `SmReturnPoint` at `hatcheryRadius - smallInset`
  (InitialMapData.cs:1362, :1365).
- Rename `Midpoint` to something JIT-specific so the Sharky word cannot mislead again. Name of your choice.

**Q-M2: do `HarvestA`/`HarvestB` get the same treatment?** They copy `mineral.HarvestPoint` (:1243, :1299-1300), not
`SmHarvestPoint`. Worse, the two builders disagree on radii: InitialMapData uses `mineralRadius = 1.0f` and
`smallInset = 1.75f` giving harvest at 1.0u and smHarvest at 2.75u (InitialMapData.cs:1359-1360, :1363, :1366),
while `BuildMineralLinePoints` uses a 1.5u harvest offset and a 2.75u smHarvest offset
(BabySharkBuildManager.cs:1428-1430). Which pair of values is canonical for the JIT table? This also supersedes the
round-4 1.0-vs-1.5 question: they are two different builders, not two circles.

**Q-M3: the conflicting third and fourth sites.** InitialMapData.cs:1109 averages two `ReturnPoint` values into
`MiningPairCargoPointDto.JitReturnPoint`; BabySharkBuildManager.cs:810 sets the same field to `firstReturn` with no
averaging. They contradict each other and :1289. Since `MainMineralJitCargoPoints` is read nowhere (round 2 C9) and
answer 6 permits deletion, confirm all of it goes so one JIT return formula remains.

### Answer 5 — I searched for the shared-variable cause; three real defects found

**Points are not corrupted by mutation.** `Vector2Dto` is a `class` (BaseDtos.cs:19), so assignments copy
references — but a repo-wide search for in-place coordinate writes,
`(HarvestPoint|ReturnPoint|CcaWaitPoint|JitHarvestA|JitReturnPoint|StoredTargetPoint).(X|Y|Z) =`, returns **zero
matches**. Each worker also gets its own `MiningTargetDto` (BabySharkBuildManager.cs:1158-1170, :1391) and
`BuildMineralInstructionList` reads only that worker's targets (:483-493).

Three defects that would still produce "workers move all over":

1. **One shared `OrderedMineral` per label.** `allMineralsByLabel` maps each label to a single `group.First()`
   (:1127-1131). Every worker targeting `TA` therefore gets points referencing the same `OrderedMineral.TA` point
   objects, and `ApplyJitPairTarget` copies those references into each worker's DTO (:1319-1324). Values are
   identical across workers by construction — no per-worker or per-pair differentiation.
2. **A team-scoped override overwrites each target's return point.**
   ```csharp
   if ((workerCount == 12 || Settings.IsMagannathaMap) && HasNonZeroPoint(assignment.JitReturnPoint))
       miningTarget.ReturnPoint = assignment.JitReturnPoint;   // BabySharkBuildManager.cs:1165-1168
   ```
   `assignment` is the team, the loop is per worker per label, so every worker in a team gets one team-level return
   point stamped over the mineral-specific value. Its source is `new Vector2Dto()` — i.e. **(0,0)** — at
   TeamLabelRegistrationHelper.cs:182, made non-zero only by the `previousJitPoints` restore at
   BabySharkBuildManager.cs:988.
3. **The pair key carries no worker identity.** `ApplyJitPairTarget(firstTarget, secondTarget, ...)` and the reverse
   call run for every worker (:494-495), and the key is labels only
   (`$"{target.ResourceLabel}-{pairedTarget.ResourceLabel}"`, :1311). T1 and T2 resolve the same pair row and hence
   identical `JitHarvestA`/`JitHarvestB`/`JitReturnPoint`.

**Q-W1: is independence expected from different pair rows, or from per-worker point values?** Under round-6 answer 3,
T1 (self pair `0-TA-TA`) and T2 (A/B switch) do land on different rows. But T1 and T3 both start on A and would share
`0-TA-TB.HarvestA`. Confirm that sharing is intended and defect 1 is by design, or that per-worker differentiation is
required. This is round-2 **Q-D20** again and it decides whether defect 1 is a bug.

**Q-W2: confirm both overrides are deleted** — the team-scoped `ReturnPoint` override (:1165-1168), and
`ApplyJitPairTarget` (:1309-1325) with the six `MiningTargetDto` Jit fields (BaseDtos.cs:363-368) — so a store/use
row's only point source is the parsed reference.

**Q-W3: `TeamPatchAssignmentDto.JitWaitPoint` is load-bearing and cannot simply go.**
`CalculateCcaWaitPointsForCurrentSpawn` reads it as a reuse source (BabySharkBuildManager.cs:206-214) and writes it
(:241); the runtime reads it via `ResolveCcaWaitPoint` for `Staging` and `BumpCcaWaitCircle`
(BabySharkMiningManager.cs:1357-1358, :1362-1363). Bump is frozen, so `BumpCcaWaitCircle` must keep working.
Propose: keep `JitWaitPoint` as CCAw storage, delete `JitReturnPoint` (only consumer is the Q-W2 override). Confirm.

### Answer 6 — deletion set I will propose before touching anything

1. `ExecuteJustInTimeMining` (BabySharkMiningManager.cs:1506-1561) — repo-wide search returns one hit, its own
   definition; dead, issues no commands.
2. `ApplyJitPairTarget` (BabySharkBuildManager.cs:1309-1325) + six `MiningTargetDto` Jit fields — ruled invalid.
3. Team-scoped `ReturnPoint` override (:1165-1168) — defect 2.
4. `MainMineralJitCargoPoints` writes (:944-949) + `BuildRuntimeJitCargoPoints` (:778-819) +
   `MiningPairCargoPointDto` — read nowhere; also Q-M3's conflicting site.
5. InitialMapData's jit-pair builder (:1100-1126) — same DTO, same conflict. **InitialMapData is protected by
   standing instruction; this needs your explicit go.**
6. The 8-worker `switch` (:1374-1385) — pending Q-8W1.
7. `Midpoint` (:1344-1350) — renamed and formula-corrected per Q-M1/Q-M2.

**Q-DEL2: approve this set and rule on item 5.**

**Q-DEL3: `IsSpeedMining`/`IsABSwitch`/`IsInitialMineralAssignment` (BaseDtos.cs:360-362) and the `speedMining`/
`abSwitch` parameters of `CreateMiningTarget` (BabySharkBuildManager.cs:1388, :1405-1407)** become self-pair vs
non-self-pair under round-6 answer 3. Delete now, or keep until the pair table drives everything? Note `IsSpeedMining`
also feeds the `Sm*` choice at BabySharkMiningManager.cs:1349-1350 — round-6 **Q-S2** is still unanswered.

## 4.10 Round 8 — the geometry definition: points are circle/line intersections

Owner (round 8) supplied an annotated screenshot of a Zerg hatchery with two of its eight minerals circled, with the
legend: "White lines indicate the speed mining path, directly to and from the hatchery. The green lines represent a
path to alternating minerals. The blue circles represent the mineral footprint. The red circle represents the hatchery
footprint. The harvest and return points are the intersections of the circles and lines."

This is the construction rule every previous round was missing. Read against the code:

### What the image settles

1. **Every point lies ON a footprint circle.** A harvest point is where a path line crosses the mineral's footprint
   circle; a return point is where a path line crosses the hatchery's footprint circle. No point is an arbitrary
   offset along a line — it is an intersection, so its distance from the center IS the footprint radius.
2. **The return point is on the hatchery footprint circle = 2.75u from center** (round 3 C1: 5.5 diameter). This
   confirms the `hatcheryRadius = 5.5f` constant at InitialMapData.cs:1358 is wrong and must become 2.75: with the
   intersection rule, a return point computed at 5.5u would sit OUTSIDE the drawn red circle, i.e. off the footprint.
3. **Two distinct path lines exist per mineral pair, and they produce different points:**
   - **White line** = mineral center <-> hatchery center, the speed-mining (self-pair) journey, "directly to and from
     the hatchery". Its intersections are the speed-mining harvest point (on the blue circle) and the speed-mining
     return point (on the red circle).
   - **Green path** = the alternating (A/B switch) journey. In the image it runs hatchery -> mineral A, mineral A ->
     mineral B, mineral B -> hatchery. Its intersections with the blue circles are the pair's harvest points; its
     intersection with the red circle is the pair's return point.
4. **This answers round-7 Q-P1.** `HarvestA` for the pair TA/TB is NOT TA's generic harvest point. It is the
   intersection of the **TA<->TB line** with **TA's footprint circle** — the standing spot on TA's footprint facing
   TB, which is exactly "the harvest point calculated for TA that is the optimal harvest point for mining pair TA and
   TB". Pair-specific, computed from the pair's own line. `HarvestB` is the same line's intersection with TB's circle.
   So `0-TA-TB.HarvestA`, `0-TA-SA.HarvestA` and `0-TA-YB.HarvestA` are three DIFFERENT points, as the owner's wording
   required and the current code (`HarvestA = from.Harvest`, BabySharkBuildManager.cs:1299) does not produce.
5. **It refines round 7's "halfway between the speed mining return points".** The pair return point is on the red
   circle (an intersection), not at the interior chord midpoint that `Midpoint(from.Return, to.Return)`
   (BabySharkBuildManager.cs:1289) computes. The green return leg in the image passes between the two white return
   points and meets the red circle; "halfway" describes the direction of that leg, while the stored point is the
   circle intersection along it.

### My reading of the construction, to be confirmed

- Blue circle radius = the mineral footprint radius. In InitialMapData that is `mineralRadius = 1.0f`
  (InitialMapData.cs:1359), so a harvest point is 1.0u from the mineral center along whichever path line applies.
- Red circle radius = 2.75u (hatchery footprint), so every return point is 2.75u from the hatchery center along
  whichever path line applies.
- Self pair (`0-TA-TA`, speed mining): `HarvestA = HarvestB` = white line intersect blue circle (1.0u from TA);
  `ReturnPoint` = white line intersect red circle (2.75u from hatchery). Both on the TA<->hatchery line.
- Non-self pair (`0-TA-TB`): `HarvestA` = TA<->TB line intersect TA circle; `HarvestB` = same line intersect TB
  circle; `ReturnPoint` = green return leg intersect red circle.
- The wait point is NOT in this image. It remains the separate larger circle from round 4 (drawn 1.5u, runtime
  1.8u, radius still unresolved), required on all 8 minerals per round 4/5.

### Questions

**Q-I1: confirm the radii and the fate of the `Sm*` fields.**
If the white-line intersections ARE the speed-mining points, then the speed-mining harvest point is 1.0u from the
mineral (blue circle) and the speed-mining return point is 2.75u from the hatchery (red circle). The code currently
holds `HarvestPoint` at 1.0u and `SmHarvestPoint` at 2.75u (InitialMapData.cs:1363, :1366), and `SmReturnPoint` at
`hatcheryRadius - 1.75` = 3.75u (InitialMapData.cs:1365) or 1.0u (BabySharkBuildManager.cs:1432) — none of which is
"on the red circle along the white line" except by coincidence after the 2.75 fix. Confirm: (a) blue = 1.0, red =
2.75; (b) the white-line intersections replace both `HarvestPoint`/`ReturnPoint` and `SmHarvestPoint`/`SmReturnPoint`
as concepts, i.e. the `Sm*` fields are retired (this also closes round-6 Q-S2 and round-7 Q-DEL3); (c)
`BuildMineralLinePoints`' 1.5u harvest offset (BabySharkBuildManager.cs:1428) is wrong and becomes 1.0u.

**Q-I2 / Q-I3 / Q-I4: SUPERSEDED by round 9 (section 4.11).** The owner supplied the explicit construction; the
harvest direction is toward the JIT Return Point (not toward the other mineral, so Q-I2 as posed was wrong), and the
return direction is toward the average of the two mineral centers (Q-I3 option (a)). Q-I4 (wait circle) remains open
and is carried into section 4.11.

## 4.11 Round 9 — the JIT pair formula, stated by the owner

Owner (round 9), with a second annotated screenshot (green line hatchery -> red-circle crossing -> between the two
circled minerals; blue line from the first mineral to that same crossing; yellow line from the second mineral to it):

> "There may be better math, I would take the average of x,y of two minerals. Calculate where a line from that point
> and the hatchery x,y. And use the 2.75 distance from the hatchery x,y towards the average point of the minerals.
> That is the JIT Return Point. Second 1u from the First Mineral towards the new JIT Return Point is HarvestA.
> Third, 1u from the second Minerals x,y is HarvestB."

### Canon formula

```
avg       = (mineralA.Position + mineralB.Position) / 2
dirReturn = normalize(avg - hatchery.Position)
JitReturn = hatchery.Position + dirReturn * 2.75          // on the hatchery footprint circle

dirA      = normalize(JitReturn - mineralA.Position)
HarvestA  = mineralA.Position + dirA * 1.0                // on A's footprint circle

dirB      = normalize(JitReturn - mineralB.Position)
HarvestB  = mineralB.Position + dirB * 1.0                // on B's footprint circle
```

All three points are circle/line intersections exactly as round 8 described: `JitReturn` on the red (2.75) circle
along the hatchery->avg line; `HarvestA`/`HarvestB` on the blue (1.0) circles along each mineral->JitReturn line.
The screenshot shows all three lines meeting at the single red-circle crossing, which is the stored point.

### Corrections to my earlier readings

- **Q-I2 was wrong.** The harvest direction is toward the JIT Return Point, NOT along the A<->B center line. The
  A<->B line is never used. Only the two mineral centers, the hatchery center, and the two radii enter the math.
- **Q-I3 answered: option (a)** — the return direction is toward the average of the two mineral centers.
- **"Halfway between the speed mining return points" (round 7 answer 4) is now fully explained**: the avg of the two
  mineral centers projects, through the hatchery, to a return point that lies between the two white-line (speed
  mining) return points on the red circle. Same direction, expressed from mineral centers instead of return points.
- This supersedes `Midpoint(from.Return, to.Return)` (BabySharkBuildManager.cs:1289) and
  `HarvestA = from.Harvest` / `HarvestB = to.Harvest` (:1299-1300). Both are deleted with the round-7 set.

### Self pair degenerates to the speed-mining line — no special case needed

For `0-TA-TA`: `avg == mineralA`, so `dirReturn` points hatchery -> TA and `JitReturn` is 2.75u from the hatchery
toward TA — the speed-mining return point on the white line. `HarvestA` is then 1.0u from TA toward `JitReturn`,
i.e. toward the hatchery — the speed-mining harvest point on the blue circle. So the self row IS the speed-mining
geometry, confirming round 6 answer 3 ("a mineral going to itself is Speed Mining") with zero extra code, and
retiring the `Sm*` fields (round 6 Q-S2, round 7 Q-DEL3): the white-line points are simply the self-pair row.

### Consequences for the pair table

- One row per unordered pair plus one self row per resource, keyed `{base}-{first}-{second}` with `first` before
  `second` in the canonical order VA, VB, TA, TB, SA, SB, BA, BB, YA, YB (round 2 answer 6, round 3 answer 1).
  `HarvestA` always belongs to the first-listed label, `HarvestB` to the second.
- The Mining Manager reads `JitReturn`/`HarvestA`/`HarvestB` as stored coordinates via the parsed reference
  (round 7 answer 1). No lookahead, no calculation at runtime.
- The formula needs only: the two resource centers, the hatchery center, and the constants 2.75 and 1.0.

### Still open

**Q-I4 (carried): the wait circle radius.** Drawn 1.5u in HEAD versus runtime `harvestDistance + 0.8` = 1.8u
(BabySharkBuildManager.cs:235). The wait point is stored per resource as `WaitPointA`/`WaitPointB` in the pair table
and is required on all 8 minerals (rounds 4-5). The round 8/9 images show only footprint circles, so they are silent
on it. Rule the radius, or rule that the wait point is measured from a run as round 5 proposed.

**Q-V1: vespene and pool radii.** The formula says "1u from the mineral", which is the mineral footprint radius. For
a `VA-VB` pair the resources are geysers, not minerals, and for `X-Pool` pairs the pool is a building that is never
harvested (round 1 answer 9). State the harvest offset for geysers (their own footprint radius? which value?) and
what `HarvestB` means on a pool row (unused/zero, or the pool position), or rule that pool/vespene rows store only
`JitReturn` plus the mineral-side `HarvestA`.

**Q-GO: authorization to implement.** With this formula the mineral-pair calculation is fully specified. Confirm I
may now, in one change: (1) rewrite `PopulateJitPairReturnCalculations` to this formula with the canonical
unordered+self keys and the mining-base prefix; (2) move its call after `CalculateCcaWaitPointsForCurrentSpawn`
(round 3 C7) and extend that method to B minerals; (3) apply the round-7 deletion set including `ApplyJitPairTarget`,
the team-scoped `ReturnPoint` override, the dead `ExecuteJustInTimeMining` husk and `MainMineralJitCargoPoints`;
(4) add the 8-worker rework comment (round 7 answer 6) without reworking 8-worker; (5) fix
`hatcheryRadius` 5.5 -> 2.75 (round 3 C1, now confirmed by the intersection rule) with a persisted-data version bump.
Item (5) and the InitialMapData jit-pair builder deletion still need your explicit go because InitialMapData is
protected by standing instruction.

## 4.12 Round 10 — the journey cycle A -> B -> C -> D -> C, repeating B-C-D-C

Owner (round 10), with a third annotated screenshot (magenta rectangle at the hatchery edge labelled A, yellow-circled
mineral labelled B, red-circle crossing labelled C implicitly via the green line, blue-circled mineral labelled D),
and the explicit caveat: "This is not an actual example. The letters do not represent real data."

> "In this example the Worker Starts the Game at Position 'A', Moves to Position 'B': HarvestB, collects minerals,
> then moves to JIT return point Marked as position 'C', then returns cargo, then moves to Position 'D': HarvestA,
> Collects minerals there, then returns to the JIT Return Point. And will repeat B-C-D-C, B-C-D-C, B-C-D-C...
> Returning Cargo each time at 'C' and collecting mineral each time at 'B', and (correction) 'D'."

### Canon cycle

```
A = the worker's game-start (spawn) position. Not a stored pair property; no reference needed for it.
B = HarvestB of the worker's pair.   collect cargo here
C = the pair's JitReturnPoint (2.75u on the hatchery footprint circle). return cargo here, EVERY time
D = HarvestA of the worker's pair.   collect cargo here
cycle: B -> C -> D -> C -> B -> C -> D -> C ...   (cargo returned at C after every harvest, at B and at D)
```

Consequences:

1. **Cargo handoff happens at C, not at the hatchery center.** The HARVEST_RETURN ability is issued with the worker
   standing at the JIT return point on the footprint circle. This is what the existing `jitRM` set already does
   (`UseTargetPoint ...ReturnPoint`, Move, `Return` against the stored point), so the command shape is right; only
   the stored coordinates were wrong before the round-9 formula.
2. **The existing list structure already matches the cycle.** Verified from the frame-0 dump
   (`worker_instruction_lists_frame0_20260909090107.json`), T1's opening lines:
   `jitMHb StoreTargetPoint TA-TB.HarvestA` -> Move x3 -> `Gather` -> `wait` ->
   `jitRM UseTargetPoint TA-TB.ReturnPoint` -> Move x3 -> `Return` -> `wait` ->
   `jitMH StoreTargetPoint TB-TA.HarvestB` -> ... i.e. harvest, return at C, harvest the other, return at C, repeat.
   The set names and the Store/Use/Move/Gather/Return shapes need no redesign; they need correct coordinates and the
   parsed reference (round 7 answer 1).
3. **The letters are positional labels, not data.** Do not bind "B" to a specific mineral or team. What is canon is
   the alternation: the two harvest points of the worker's pair, with a cargo return at C between every harvest.
4. **Phase of the opening is the one open nuance (Q-J1).** The owner's example opens with HarvestB first
   (A -> B -> C -> D). The current lists open with `HarvestA` first for T1 (`jitMHb` stores `JitHarvestA`). In an
   endless cycle the two are the same loop with a different first step, but the first step after spawn is observable.
   Question: should every worker's first harvest after spawn be `HarvestB` (partner-side point) as in the example,
   or is the current `HarvestA`-first opening acceptable? Since the letters are explicitly not data, I will not
   change the phase without a ruling.

### Distinct references present in the frame-0 dump (26)

`{X}A-{X}B.{HarvestA,HarvestB,ReturnPoint}` for all four teams plus `WaitPointA` for BA-BB and SA-SB only, and the
reversed duplicates `{X}B-{X}A.{HarvestA,HarvestB}`. Confirms: no mining-base prefix yet, directed duplicates still
emitted, no self rows yet, no Pool/Vespene rows yet, `WaitPointB` never referenced. All consistent with the round-2/3
canon still being unimplemented.

## 4.13 Round 11 — the juggling rhythm: three workers, two resources, four teams

Owner (round 11): "The heart and soul of JIT mining is juggling three workers on two resources. Role 1 goes to A,
Role 2 goes to B, Role three waits to mine A after Role [1] completes collection of a mineral. While Role 1 is moving
to the Hatchery, Role 3 is mining the A mineral. Role 2 will complete Mining Mineral B shortly after Role 1
completed... Since Role 3 is now mining A, the next available mineral for Role 1 is the B Mineral... No decision needs
to be made about which mineral will be available, it is like the nature of two hands juggling three balls... We have
four 3 Worker Teams juggling an A/B Targets."

### Canon rhythm

- One team = 3 workers on one pair (its A and B minerals). Four teams = 12 workers = the four A/B pairs
  (TA/TB, SA/SB, BA/BB, YA/YB). This is what "four 3 Worker Teams juggling an A/B Targets" fixes: the pair IS the
  team's two minerals, and roles 1/2/3 are positions inside that team.
- Phase per role, then strict alternation forever:
  - Role 1: harvest A, return at C, harvest B, return at C, harvest A, ...
  - Role 2: harvest B, return at C, harvest A, return at C, harvest B, ...
  - Role 3: wait on A's wait circle until Role 1's collection ends, harvest A, return at C, harvest B, ... (then
    alternates like the others, one half-step behind Role 1)
- The alternation is structural, not computed. "No decision needs to be made" means the list's loop encodes it: the
  repeating section alternates the B set and the A set and the last line jumps back into it. Verified in code: the
  loop body is `secondHarvestIndex` (jitMH, JitHarvestB, BabySharkBuildManager.cs:526-527) then the A set
  (jitMH, JitHarvestA, :545-546) then `last-line` Jump to `secondHarvestIndex` (:563-568). Nothing at runtime selects
  a mineral.
- The hand-off timing between Role 1 and Role 3 is baked into relative frames, not observed cross-worker: Role 1/2
  gather at relative frame 15 while Role 3's opening gather is at frame 55 (round 4.7 finding,
  BabySharkBuildManager.cs:292-313), so Role 3's gather lands after Role 1 has left A. The wait circle is where
  Role 3 stands during that interval.

### Q-J1 CLOSED

The round-10 example opened with "B" first, which I flagged as a possible phase change. The juggling description
resolves it: the first harvest is per-role, not global. Role 1 opens on A, Role 2 opens on B, Role 3 opens on the
wait circle then A. The current openings already do exactly this (Role 1's first set stores JitHarvestA, Role 2's
stores JitHarvestB per the 12-worker label rule, Role 3's opens with the CCAw wait). The round-10 letters were
positional, as the owner said; no re-phasing is needed.

### Consequences

- The pair table's per-team rows (HarvestA, HarvestB, ReturnPoint, WaitPointA, WaitPointB) are exactly the five
  points a juggling team needs. No per-worker point values are required: all three workers of a team share the same
  five stored points and differ only in list phase. This answers round-6 Q-W1 in the negative: worker independence
  comes from list phase, not from different coordinates. The round-7 "shared variables" defects (one shared
  OrderedMineral per label, team-scoped ReturnPoint override, worker-less pair keys) are therefore NOT bugs in the
  point values — sharing is by design. They are bugs only in that the shared values are computed wrong
  (Midpoint/harvest-copy instead of the round-9 formula) and that the team override can stamp (0,0).
- Role 3's wait is on A's wait circle (CCAw). The round-4/5 ruling that all 8 minerals carry a wait point stands;
  only A's is used by Role 3 in the 12-worker opening. B's wait point exists for completeness and for any later
  pattern (e.g. the parked ML work).

## 4.14 Round 12 — wait radius 1.5; vespene and building wait semantics; Q-GO restated plainly

Owner (round 12):
1. "use the smaller number, this was hard coded then adjustes after gameplay, but the hard coding remained."
2. "Vespine Workers, the waitpoint is on the footprint, the first worker enters the Geyser, a second worker waits
   outside as near as it can stand. Buildings, one worker goes to the coordinate, only waits if it arrives before it
   can build. When it can build it morph into the building. For a worker building a vespine geyser it moves to the
   footprint and is given the morph command."
3. "what is the question here?" (about Q-GO)

### Q-I4 CLOSED: the wait circle radius is 1.5u

The two candidates were the drawn circle (1.5f in HEAD) and the runtime value `harvestDistance + 0.8` = 1.8u. The
owner rules: use the smaller = **1.5**. The 0.8 offset was a post-gameplay adjustment ("adjustes after gameplay")
while the hard-coded 1.5 remained correct; the adjustment is superseded. So:

- Wait point = mineral center + normalize(toward hatchery/return direction) * 1.5, on all 8 minerals.
- Role 1 harvests at 1.0u, Role 3 waits at 1.5u on the same ray: 0.5u separation, matching "the 3 workers went to a
  wait point on the larger circle that did not interfere with the 1 worker" (round 4).
- The `harvestDistance + 0.8f` expression (BabySharkBuildManager.cs:235, introduced by commit 258f4ab 2026-09-01)
  becomes `1.5f` from the mineral center (equivalently harvestDistance + 0.5).

### Vespene and building wait semantics (Q-V1 substantially answered)

- **Vespene mining:** the wait point is ON the geyser footprint circle. The code already carries the geyser footprint
  radius as `const float geyserRadius = 3f` (InitialMapData.cs:1211, SpawningPoolPlacementService.cs:72), so the
  vespene wait point is 3u from the geyser center. First worker enters the geyser (harvest point = inside, i.e. the
  geyser center); second worker waits outside as near as it can stand = on the 3u footprint circle.
- **Buildings (Spawning Pool):** one worker travels to the coordinate; it waits only if it arrives before it can
  build; when it can build it morphs. The wait is a runtime condition on build availability, not a standing circle.
  The stored point is the pool coordinate (round 2 answer 10: use the resource X,Y, never (0,0)).
- **Extractor builder:** moves to the geyser footprint and is given the morph command. So the build instruction's
  move target is a point on the 3u geyser footprint circle, and the morph (BUILD extractor) is issued there.

One conflict to confirm (Q-V2): round 2 answer 10 said "VA and VB should only have a Harvest point. Wait points are
only used on minerals, otherwise not used." Round 12 gives vespene a real wait point on its footprint for the second
worker. I read round 12 as refining round 2 for the gas case (the "otherwise not used" rule still applies to
buildings, whose wait is conditional). Confirm: the VA-VB pair row carries WaitPointA/WaitPointB on the 3u circle,
and pool rows carry no wait point (conditional wait only). Also confirm the vespene harvest point is the geyser
center (worker enters), and the direction of the 3u wait point follows the same convention as minerals (toward the
hatchery / JIT return).

### Q-GO restated plainly (owner asked "what is the question here?")

The question was simply: **may I start editing code now?** Concretely, the change set I am asking to approve:

1. `BabySharkBuildManager`: rewrite the pair calculation to the round-9 formula (avg of the two centers -> 2.75u
   return point on the hatchery footprint; 1.0u harvest points from each center toward that return point), keys
   `{base}-{first}-{second}` unordered plus self rows in the canonical order VA,VB,TA..YB, wait points on all 8
   minerals at 1.5u.
2. Move the pair-calculation call to after the CCAw wait-point calculation (so wait points are not stored as (0,0))
   and extend that calculation to B minerals at 1.5u.
3. `BabySharkMiningManager`: parse `{base}-{A}-{B}.{Property}` from `TargetPointReference` against the pair table and
   read stored coordinates; delete the `ApplyJitPairTarget` copy and the six `MiningTargetDto` Jit fields.
4. Delete dead code: the `ExecuteJustInTimeMining` husk, the `MainMineralJitCargoPoints` writes, the team-scoped
   `ReturnPoint` override, and the 8-worker cross-team switch (replaced by the required "8 worker requires a complete
   rework after 12 worker is satisfactory" comment, no rework now).
5. `InitialMapData`: fix `hatcheryRadius` 5.5 -> 2.75 (changes persisted geometry, so it needs a data version bump)
   and remove the old jit-pair builder there. **These two touch the protected InitialMapData file and need an
   explicit yes; everything else does not.**

Answer per item, or "all yes", or "all yes except N".

## 4.15 Round 13 — vespene/pool runtime semantics; items 4 and 5 DELETED (implemented)

Owner (round 13): "Vespine footprint, A worker will go there to build a vespine Geyser. A worker can move to the
footprint while the geyser is morphing. When a Geyser has completed workers can mine Vespine Gas. The[y] do this with
a Smat command on the Geyser, if more that 1 worker is given a Smart command on a completed Geyser, they will all wait
on the footprint for a turn to enter the Geyser and begin collecting. When a worker is collecting the Geyser is busy
and additional workers can only stand next to the geyser. Like Minerals a worker that is waiting can recieve the Smart
Command less than 15 frames before a busy worker completes mining. Pools and Geysers will need adjusting, these are
not blocks as they will likely need changes. 4 & 5, go for delete."

### Vespene/pool canon (deferred, not implemented)

- Builder: move to geyser footprint (3u), issue morph there; a second worker may move to the footprint while morphing.
- Miners: SMART on the completed geyser; extra workers wait on the footprint for a turn; the geyser is busy while one
  worker collects; a waiting worker may receive SMART less than 15 frames before the busy worker completes.
- Owner states pools and geysers "will need adjusting" and are "not blocks" — i.e. these semantics are expected to
  change again. NOT implemented in this change set.

### Implemented deletions (owner-approved "4 & 5, go for delete")

Item 4, BabySharkMiningManager.cs:
- `ExecuteJustInTimeMining` (was :1506-1561) deleted; zero callers, no commands, stale console logging only.
- `_lastCargoEvaluationConsoleFrame` field (was :78) deleted with it (only user was the husk).

Item 4, BabySharkBuildManager.cs:
- `BuildRuntimeJitCargoPoints` (was :778-819) deleted; its only caller was the `MainMineralJitCargoPoints` write.
- The runtime `MainMineralJitCargoPoints` write block (was :944-949) deleted.
- Team-scoped `miningTarget.ReturnPoint = assignment.JitReturnPoint` override (was :1165-1168) deleted.
- 8-worker cross-team label switch (was :1374-1385) replaced by the required comment: "8 worker requires a complete
  rework after 12 worker is satisfactory." It now returns no labels for 8-worker starts. Safe: `BuildMineralInstructionList`
  guards `targets.Count < 2` (BabySharkBuildManager.cs:487) and returns the startup instructions, so 8-worker workers
  get no pair instructions instead of wrong ones.

Item 5, InitialMapData.cs (explicitly approved):
- `hatcheryRadius` 5.5f -> 2.75f with a footprint comment (was :1358).
- `BuildMultiLocationJitCargoPoints` (was :1051-1084), `BuildJitPairCargoPoints` (was :1086-1126) and the
  InitialMapData-local `IsSamePosition` (was :1128-1136, used only by those two) deleted, plus all three call sites
  (was :936, :1008, :1012). The DTO fields `MainMineralJitCargoPoints` / `ExpansionMineralJitCargoPoints` remain for
  serialization compatibility and are simply never populated.

Item 5, Settings.cs:
- `SpeedMiningVersion` "0.10" -> "0.11" with a comment stating why. MapDataManager gates saved dat files on this
  constant (MapDataManager.cs:63-71), so previously stored maps with 5.5u return points are invalidated and rebuilt.

Verification: `dotnet build BabySharkBot\BabySharkBot.csproj` -> 0 errors, 114 warnings (all pre-existing nullability /
unused-field warnings, none introduced). Repo-wide grep confirms zero remaining references to
`ExecuteJustInTimeMining`, `BuildRuntimeJitCargoPoints`, `BuildMultiLocationJitCargoPoints`,
`BuildJitPairCargoPoints`, `_lastCargoEvaluationConsoleFrame`, `hatcheryRadius = 5.5f`, or the 8-worker switch.

Note for the record: the `Sm*` fields ARE read at runtime during the 16-worker speed-mining phase
(BabySharkMiningManager.cs:774-775 `_speedMiningActive = TeamColorService.IsSpeedMiningPhase(totalWorkers)` and
:1349-1350). The 2.75 fix therefore changes `SmReturnPoint` (hatcheryRadius - 1.75 = 1.0u) and `SmHarvestPoint`
(mineralRadius + 1.75 = 2.75u) in stored data. That is a mechanical consequence of the approved radius fix, not a new
design decision; the round-9/10 canon (self pair = speed mining) will supersede the `Sm*` fields when items 1-3 land.

### Still held (not approved yet)

Items 1-3: pair formula rewrite with canonical keys, reorder + B wait points at 1.5u, reference-string parsing.
Plus Q-V2 (vespene wait points on the 3u circle vs round-2 "only minerals get wait points").

## Round 4 — status of the open list

Closed by round 4: **Q-C1a** (two circles, 1.0 and 1.5 — semantics corrected in section 4.5, then re-read in round 7
Q-M2 as two builders, and finally resolved in round 8 section 4.10 as footprint-circle intersections).
Opened by round 4: **Q-C1a-2** (confirm harvest 1.5 and return 2.75 offsets), **Q-R1** (restore deleted
diagnostics), **Q-R2** (B-mineral diagnostic form), **Q-R3** (draw the pair return point).

Still open and blocking implementation: **Q-C1b**, **Q-C2a-d**, **Q-C7a-c**, **Q-C8a-b**, and from round 2
**A1**, **A2**, **B1/B2**, **B4**, **D1**, **D3**, **D4**, **E1**, **E2**, **F1**, **F3**, **F4**, **F5**, **F6**.
