The user wants me to analyze a StarCraft II bot codebase called BabyShark b[1D[K
built on Sharky framework. The core problem: Three of the teams are not min[3D[K
mining at all, only one team (Purple or salmon) is working while other work[4D[K
workers just stand there idle. 

I need to find why worker allocation fails for 3/4 teams but works for 1 te[2D[K
team. This is a classic symptom of either:
- Race conditions in initial map data population where some teams' workers [K
aren't being registered correctly
- WorkerLabelService not properly tracking all teams (maybe only the active[6D[K
active player's team gets labels)
- The greedy mineral ordering or JIT prepositioning failing for certain tea[3D[K
team indices
- Team-specific initialization order issues

Let me structure this as a systematic investigation: find what differs betw[4D[K
between the working team and non-working teams.

I'll start by mapping the key files related to worker tracking, label assig[5D[K
assignment, and team initialization, then identify likely failure points wh[2D[K
where 3/4 teams could be skipped while 1 succeeds.
</think>

# BabyShark Worker Allocation Failure Analysis — Targeted Diagnosis Plan

## Executive Summary of the Symptom Pattern
**Three teams idle / one team mining correctly.** This is not a general cra[3D[K
crash but a selective worker registration or assignment failure that discri[6D[K
discriminates by team index, initial map scan order, or label service scope[5D[K
scope. The working team likely runs through `BabySharkMiningManager`'s full[4D[K
full initialization path (workers collected → labeled W1–W12 → JIT choreogr[8D[K
choreography activated), while the other three teams hit a silent break poi[3D[K
point in either:
- `InitialMapData` unit scanning (only one team's workers pass the filter)
- `WorkerLabelService` population (labels not persisted for non-active play[4D[K
player teams)
- Team-specific manager construction order

---

## Phase 1 — Map Data Population & Worker Registration Flow

**Files to audit:**
```
BabySharkBot/Setup/InitialMapData.cs          ← unit scanning loop for all [K
teams
BabySharkBot/Setup/ProcessVisableUnits.cs    ← visible unit filtering logic[5D[K
logic
BabySharkBot/Services/chrisCrossAppleSause.cs ← worker initialization [K
& label assignment
BabySharkBot/Services/BaseLocationCalculationService.cs ← team-specific map[3D[K
map data per team
BabySharkBot/BabySharkMiningManager.cs       ← manager that receives all te[2D[K
teams' workers
```

**Likely failure points:**
1. **`ProcessVisableUnits.cs` filtering by `IsEnemyTeam()` or `teamIndex ==[2D[K
== 0`** — if the unit scan only processes the active player's team (team in[2D[K
index 0), then teams 1-3 never get their workers added to mineral assignmen[9D[K
assignment pools, causing them to stand idle while Team 0 runs fine.
2. **`InitialMapData.cs` multi-main mineral collection** — `GetNewMiningDat[16D[K
`GetNewMiningData()` may only iterate one team's minerals/vespene. If anoth[5D[K
another team's minerals aren't scanned into tempBaseDto arrays, JIT preposi[7D[K
prepositioning has nothing to assign workers to for those teams.
3. **Team-specific map data isolation** — if `MapDataManager` or `OngoingMa[10D[K
`OngoingMapData` keeps separate per-team state but only initializes one tea[3D[K
team's state correctly at game start (race condition in `OnInit()` ordering[8D[K
ordering), the other three teams get empty mineral/worker lists.

---

## Phase 2 — WorkerLabelService & Label Persistence

**Files to audit:**
```
BabySharkBot/Services/chrisCrossAppleSause.cs ← SetLabel() implementation p[1D[K
per team
BabySharkBot/Managers/BabySharkUnitManager.cs ← label tracking dictionaries[12D[K
dictionaries (W1-W12)
BabySharkBot/Manager/WorkerLabelChangedEventArgs.cs ← event dispatch for la[2D[K
label changes
BabySharkBot/Services/JitPrepositionService.cs ← JIT assigns workers to nea[3D[K
nearest mineral by label
```

**Likely failure points:**
- `SetLabel()` only succeeds when called during the active player's frame c[1D[K
cycle; non-active teams' labels get GC'd or overwritten because the diction[7D[K
dictionary is keyed by `(teamIndex, unitTag)` but the dictionary itself may[3D[K
may be recreated per team in `BabySharkUnitManager` incorrectly.
- **Event-based label sync:** `WorkerLabelChangedEventArgs` fires only for [K
the team whose manager runs in the main loop; other teams' labels don't get[3D[K
get pushed to the drawing service or JIT service, so workers have no assign[6D[K
assigned W1-W12 labels and thus aren't targeted by JIT choreography — they [K
just stand idle waiting for a mineral assignment that never arrives.
- `JitPrepositionService` uses label-based worker lookup (`GetWorkerByLabel[18D[K
(`GetWorkerByLabel(tag)`) — if the non-working teams' workers don't get lab[3D[K
labeled W1-W12, JIT returns null and workers are left unassigned.

---

## Phase 3 — JIT Prepositioning & Mineral Assignment Loop

**Files to audit:**
```
BabySharkBot/Services/JitPrepositionService.cs ← core worker-to-mineral cho[3D[K
choreography
BabySharkBot/MicroTasks/BabySharkOverlordScoutTask.cs ← may steal workers f[1D[K
from mining
BabySharkBot/Managers/BabySharkMiningManager.cs ← main JIT orchestration pe[2D[K
per team
```

**Likely failure points:**
- `JitPrepositionService.AssignWorkersToMinerals(teamIndex)` loops over all[3D[K
all workers on that team, but if the worker list is empty (because Phase 1 [K
filtered them out), nothing happens — workers stand idle.
- **Overlord scout task stealing:** `BabySharkOverlordScoutTask.cs` may ass[3D[K
assign some or all workers to scouting duties and release them later, but i[1D[K
if released back as unassigned, JIT doesn't reassign because the `MicroTask[10D[K
`MicroTasks` list is stale. This would cause intermittent "standing idle" b[1D[K
behavior.
- **Team-specific manager instantiation:** `BabySharkMiningManager` constru[7D[K
constructor must receive both `WorkerLabelService` and `CrosshairService` f[1D[K
for each team. If only Team 0's manager gets properly constructed (e.g., vi[2D[K
via a singleton or main-loop call), the other three teams have no mining ma[2D[K
manager at all → workers sit idle with no choreography logic running.

---

## Phase 4 — Diagnostic Logging Strategy (Immediate Actions)

Before deep-dive into any single file, add **team-specific logging** to iso[3D[K
isolate which phase breaks:
1. In `InitialMapData.cs` scanning loop: log `teamIndex`, `workerCount`, `m[2D[K
`mineralCount`, `vespeneCount` per team.
2. In `BabySharkUnitManager` / `WorkerLabelService`: log `SetLabel(team, un[2D[K
unitTag, label)` success/failure per team.
3. In `JitPrepositionService.AssignWorkersToMinerals()`: log `teamIndex`, `[1D[K
`assignedCount`, `unassignedWorkers`.
4. In `BabySharkMiningManager` constructor: confirm all four teams get a ma[2D[K
manager instance.

This will immediately identify whether the failure is at scan phase (Phase [K
1), label phase (Phase 2), or JIT phase (Phase 3).

---

## Priority Investigation Order

| Priority | File | Question to Answer |
|----------|------|-------------------|
| **P0** | `BabySharkBot/Setup/InitialMapData.cs` | Does the mineral/vespen[14D[K
mineral/vespene scan loop iterate over all 4 teams or only team index 0? |
| **P1** | `BabySharkBot/Services/chrisCrossAppleSause.cs` | Is `SetLabel()[11D[K
`SetLabel()` called for every worker on every team, and does it persist acr[3D[K
across frames? |
| **P2** | `BabySharkBot/BabySharkMiningManager.cs` | Are all 4 teams' mana[4D[K
managers instantiated in `OnInitMapData()` or equivalent lifecycle callback[8D[K
callback? |
| **P3** | `BabySharkBot/Services/JitPrepositionService.cs` | Does JIT work[4D[K
work correctly when the worker list is empty (i.e., silently fail vs. crash[5D[K
crash)? |
| **P4** | `BabySharkBot/MicroTasks/BabySharkOverlordScoutTask.cs` | Is sco[3D[K
scout task stealing workers away from mining permanently or temporarily? |

---

## Expected Root Cause Hypotheses (Ranked by Likelihood)

1. **Most Likely:** `InitialMapData` scans only the active player's team mi[2D[K
minerals/vespene, so 3 teams never get their mineral pools populated → JIT [K
has nothing to assign workers to → those workers stand idle.
2. **Second Most Likely:** `WorkerLabelService` dictionary is per-team but [K
recreated each frame for the active player only; non-active teams' labels a[1D[K
are lost and JIT can't find them by tag, so workers sit unassigned.
3. **Third Most Likely:** `BabySharkMiningManager` singleton or main-loop i[1D[K
instance serves only one team; other 3 teams never enter the choreography l[1D[K
loop at all.

---

## Immediate Next Steps

1. **Open and scan** `InitialMapData.cs` for any hard-coded `teamIndex == 0[1D[K
0`, `IsEnemyTeam()`, or single-team mineral collection loops — this is the [K
#1 suspect.
2. **Confirm manager instantiation:** Check that `BabySharkMiningManager` g[1D[K
gets created for all 4 teams in `Game.OnInit()` or equivalent, not just for[3D[K
for the active player.
3. **Add team-specific logging** as described above to see which phase brea[4D[K
breaks per team — this is faster than guessing.
4. If P0 confirmed: fix mineral/vespene scan to loop over `AllTeams` or `Te[3D[K
`Teams.Except([activeTeam])`.

I can now generate a detailed line-by-line audit plan for the top 3 suspect[7D[K
suspect files (`InitialMapData.cs`, `chrisCrossAppleSause.cs`, `BabySharkMi[12D[K
`BabySharkMiningManager.cs`) if you want me to focus on them first, or I ca[2D[K
can produce a complete diagnostic patch with logging across all suspected b[1D[K
break points.
