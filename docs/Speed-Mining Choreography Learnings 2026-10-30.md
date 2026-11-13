# Speed-Mining Choreography — Learnings Through 2026-10-30

Everything we know about worker mining choreography on MagannathaAIE v2, how we
measured it, what we tried, and where tomorrow's plan starts. Companion to the
code in `BabySharkBot/Services/SmHandoffService.cs` (all line references are to
that file unless noted).

---

## 1. The map

- Two start locations, 180° rotational symmetry about map center: SE hatchery
  **(127.5, 24.5)**, NW hatchery **(24.5, 127.5)**; `(x,y) → (152−x, 152−y)`.
- 8 minerals per base. SE centers (from the persisted map data dump,
  `data/base/MagannathaAIE_v2.SC2Map.dump.json`):

  | label | SE center | NW center | team |
  |---|---|---|---|
  | TA | (127.0, 18.5) | (25.0, 133.5) | teal — football-shaped, points L/R |
  | TB | (128.0, 17.5) | (24.0, 134.5) | teal |
  | SA | (130.0, 18.5) | (22.0, 133.5) | teal |
  | SB | (133.0, 19.5) | (19.0, 132.5) | S worker |
  | BB | (134.0, 20.5) | (18.0, 131.5) | B workers |
  | BA | (134.0, 22.5) | (18.0, 129.5) | B workers |
  | YB | (135.0, 23.5) | (17.0, 128.5) | Y workers |
  | YA | (134.0, 24.5) | (18.0, 127.5) | Y workers |

  The yellow-side column is tight: SB→BB→BA gaps of 1, 2, 1, 1 units.
- Geysers: SE VA (134.5, 27.5), VB (130.5, 31.5); NW mirrored. VA tag resolves
  at runtime by position vs the persisted `OrderedMainVespene` (VA tag was
  4324065281 on SE seed 3).
- `SmHandoffService.cs` keeps `SeIdeals` / `NwIdeals` tables (centers + the
  2026-10-29 sweep results). **The `IdealX/Y` + `BestAngleDeg` sweep points are
  superseded** by the near point (§4) but the tables are still the source of
  mineral centers.

## 2. Measurement techniques (reuse these)

- **Telemetry**: 4 files per run in
  `BabySharkBot/bin/Debug/net9.0/data/mining_tests/`:
  `worker_commands_BabyShark_<SIDE>_<ts>.jsonl` (the big one),
  `worker_instruction_lists_<SIDE>_<ts>.jsonl`,
  `worker_instruction_lists_frame0_<SIDE>_<ts>.json`,
  `mining_test_<n>_<ts>.csv`.
- Key record types in the jsonl: `WorkerObservation` (has `WorkerX/Y`,
  `CurrentOrderAbilityIds`, `TargetMineralLabel`, `WaitPointX/Y`),
  `WorkerOrderAbilityChange`, `SmHandoffSample`, `HarvestTiming`.
- **⚠ `TargetMineralLabel` is unreliable** in the speed-book phase (stale
  last-assignment). Always cluster by **position**, then attribute to the
  nearest mineral center.
- **Stand measurement**: stationary samples = same `WorkerX/Y` (±0.001) on
  consecutive observations with ability 1183 present, within ~2u of a mineral
  center, frames > 1500 (skip the measurement phase; the speed book owns the
  steady state). Greedy-cluster with radius 0.12u.
- **Ability IDs**: SMART=1, MOVE=16, drone gather=1183, drone return=1184,
  generic gather=3666, generic return=3667.
- **Screenshot → game coordinates**: calibrate an affine on the **white
  elliptical base pads** under the mineral crystals (threshold R,G,B > 185,
  size filter 50–50000 px — the mspaint canvas background is one giant white
  component, exclude it). Do NOT use blue-sprite centroids: the crystal rises
  up-left of the unit position, so centroids differ per mineral depending on
  occlusion (overlapping sprites merged/split). Fit: game+x → screen
  ≈ (33.3, −0.4) px/u, game+y → screen ≈ (−3.0, −25.9) px/u (SE view).
  Residual ≤ 3.5 px. Hand-drawn circles are good to ±0.1–0.15u, ±10°.
- **Physics constants**: cruise speed **0.176 u/frame**; a harvest cycle is
  **exactly 45 stand-still frames** (rock stable; 46 when counted by command
  convention); pathing floor for a stand = **~0.9–1.0u from the mineral
  center** (worker radius + mineral footprint); two workers can transiently
  squeeze to ~0.3u during a handoff jostle.

## 3. Command-engine facts (all live-tested)

1. **Smart/gather (1183) from a moving worker** → the engine picks the stand
   cell from the approach angle and walks the worker there. From the 1.87u
   corridor it lands 1.37–1.5u out (the "sweep Ideal"). Fired close-in from a
   wrong angle it snaps to the engine's default cell.
2. **Smart on a mineral from a stationary worker already in range** → gathers
   in place. This is the basis of the CellWait protocol (§5).
3. **Queued commands after a MOVE fire on arrival** — `CreateSmartQueued`
   (MOVE + queued SMART in the same action batch) puts the waiter into
   1183-wait at the cell the moment it arrives.
4. **The 15-frame rule**: a worker standing at a busy mineral for 15 frames
   gets re-targeted by the engine to another mineral. Re-issuing MOVE +
   queued harvest every **14 frames** beats it.
5. **MOVE re-issued every frame (rolling)** prevents the arrival deceleration;
   aim the target 2u *past* the real destination so the worker rolls through
   at full speed. (⚠ only works when the extension lands in open ground —
   aiming through a mineral footprint stalls the worker, see §6.)
6. **The engine silently drops a MOVE** whose unit stops making progress
   (head-on block); re-issue on a cadence (~every 25f) or the worker idles
   forever with no order.
7. **Deceleration rolling for arrivals** (owner 2026-10-25): give ONE MOVE on
   frame 0, then ~1 frame before the worker would start decelerating give a
   MOVE extended 2u past the target; Smart N frames (1–5 tested) before
   arrival. Tested and adopted on T3/Y1 (no-bump workers).
8. **Bare geyser**: Smart AND explicit 3666 both get **redirected to mineral
   gathering** by the engine. MOVE walks to the geyser (stops at the footprint
   edge ~2.1u from center). After the extractor finishes, 3666 on the geyser
   tag works (Sharky-proven: 390 gathers on tag 4325638145).
9. **Busy-mineral handoff**: the waiter's promotion is driven by the miner's
   1183→1184 flip (detect via `WorkerOrderAbilityChange`); the seam
   (waiter's first harvest − miner's last harvest) can be held at ~1 frame.

## 4. The stand-point saga (the core geometry lesson)

What the owner wants, in his own annotated screenshots (decoded 2026-10-30,
method in §2):

- **YELLOW point (desired)**: 0.61–0.76u from the mineral center **ON the
  line to the hatchery** (measured bearings 143–151° vs hatchery bearings
  138–163° — on the line within hand-drawn error). The worker stands tucked
  at the mineral's near edge, closest to the hatchery.
- **GREEN point (wrong)**: 0.77–0.87u, rotated **20–40° off** the hatchery
  line (bearings 92–107°). Mining from green narrows every worker corridor
  and is a longer walk (dhatch 6.57–7.15u vs yellow's 6.23–6.91u).
- The **bearing matters more than the radius**: an on-line stand at 0.9u keeps
  the through-lanes wide; an off-line stand at the same radius pinches them.
- The hand-drawn 0.6–0.76u is **inside the pathing floor** (§2): the closest a
  worker can actually stand is ~0.9–1.0u. We place the target at **0.85u** and
  let pathing clamp; measured stands land 0.85–1.45u, all on the line.
- History: 10-29 sweep found the "best" stand cells at 1.37–1.5u (minimizing
  distance to the hatchery return circle over *engine-achievable* cells from a
  1.87u Smart). 10-30 17:21 "mining the inside edge" → corridor protocol
  (1.87u approach, roll-through, Smart at the corridor). 10-30 19:39 screenshot
  proved even the sweep Ideal was too far out and off-angle. The near point
  replaced everything.

## 5. Current protocol (as built, 2026-10-30 21:27 SE run)

**Near point**: `NP = mineral center + 0.85u toward the hatchery` — pure
hatchery bearing, no best-angle offset (`NearPointRadius`, SmHandoffService).

**Approach-3 CellWait drop for ALL workers** (owner's "I like 3 best",
extended from SB to every mineral): after the cargo drop the worker
MOVEs to point 0 (= NP) with a **queued harvest** in the same batch; on
arrival the queued SMART fires into a 1183-wait at the cell; every **14
frames** the MOVE + queued harvest is re-issued while the partner is still
Mining; the miner's 1183→1184 flip promotes the waiter the same frame.

- SB additionally has **W3 parked at point 1** (staging ring, 2.17u out on the
  hatchery line) — the approach-3 conveyor slot, currently just parked.
- BA's staging ring is shifted **0.5u perpendicular toward BB** (keeps it out
  of YB's inbound lane; measured YB-B pinned ~150f there before the shift).
- The vespine worker (2nd idle) MOVEs to VA geyser frame 1; no mining command
  until the extractor exists.
- Routing: `OnFrame` → incomplete tests run the `TickA/TickB` measurement
  phase (first MOVE already retargeted to NP); completed tests run
  `TickBook → TickBookWorker` (CellWait drop). SB (TagC ≠ 0) runs
  `TickApproach3` (W3 parking + the same CellWait drop).

**Measured steady state, SE seed 3, 21:27 run** (stands = greedy clusters of
stationary 1183 samples, frames > 1500):

| mineral | exact-NP cluster | bearing (hatchery line) | main mass | avgCycle now | avgCycle before |
|---|---|---|---|---|---|
| SB | (132.37,20.07) n=954 | 137.8° (137.7) ✓ | 1.0–1.3u on-line | 120.2 / 119.9 | 115.8–118 |
| BB | (133.28,20.95) n=336 | 148.3° (148.4) ✓ | 1.05–1.45u | 120.6 / 120.7 | ~117 |
| BA | — | 164–169° (162.9) ✓ | 1.28–1.41u | **107.2 / 107.3** | 110–112 |
| YA | — | 165–180° (180) ✓ | 1.3–1.4u | 104.9 / 105.1 | 103.6–104.3 |
| YB | (134.16,23.61) n=1220 | 172.4° (172.4) ✓ | exact | **121.0 / 121.0** | **~262** |

YB's congestion (two workers fighting in front of the mineral, ~450–656f
cycles in bad runs) **collapsed to 121f** — the widest win. BA improved.
Honest cost: **SB/BB +2–4 frames** vs the old corridor book — the handoff
jostle (§8).

**NW mirror verified 2026-10-31** (`--seed=1` rolls NW in smhandoff mode;
hatchery (24.5,127.5)). Stands land on the hatchery line within a few degrees
(SB 0.5° off, BA 1.5–6° off, BB 2–16° off, YA exact) at 1.0–1.3u, with the
same jostle strays as SE. Cycle times mirror SE within ±0.4f: BA 107.4/107.6,
YA 105.1/105.1, SB 120.1/119.8, BB 120.2/120.4, YB 120.8/121.1.

## 6. What failed (don't retry without new evidence)

- **Roll-through corridor at the near point**: the outbound MOVE extended 2u
  *past* AP now aims into the mineral footprint (AP = 0.85u). Measured
  BA 250–293, BB 224–247, YA 310–337, **YB 656–682**. The roll-through
  technique requires open ground beyond the target.
- **Full conveyor shuffle (3-worker rotation)**: degenerated to ~138f cycles —
  the arithmetic of the 45f harvest means a third worker can't pay for itself
  without the return-push (owner's steps 4–5, still unbuilt).
- **In-flight steering** (perpendicular push-off, near-side waypoint): both
  made BA worse (114→128) — waypoint commands break the body-slide into
  half-speed hops.
- **Shove-match slide window** (withholding re-issues while stalled 4–30f):
  left YB-B standing in an empty corridor ~190f of a 300f grind. Constant
  re-issue stays.
- **Yield code** and the slide-window harvest counter: measured negative,
  reverted.
- **Smart/3666 on a bare geyser**: redirected to minerals (§3.8).
- **Bumping T1 backward** (10-28 garbage tests): any push geometry must move
  T1 *toward* the mineral; telemetry must show T1 monotonically closer to the
  mineral and farther from the hatchery every frame.

## 7. The choreography spec (owner's 3-worker shuffle, 2026-10-30 13:39 + answers)

Reference: `docs/Map Images/3 Worker Shuffle.png`.

- Point 0 = SM harvest point; point 1 = wait slot; point 2 = far slot.
- Release timing: the next worker must **arrive at point 0 within 1–14 frames
  before** the current miner's 1183→1184 flip; 0 = idle mineral. Actual
  measured stand-still harvest = 45 frames (the old "46" was command-count
  convention). Counters reset when there is no current harvester or when the
  waiting worker moves.
- Harvest-frame counter starts at **standstill at the harvest point**, not at
  the harvest command.
- Staging: ~0.4u outside the mineral footprint, straight-on, never blocking a
  corridor; staging areas exist as *ranges*, not exact spots (±~0.35u).
- Ideal return points: the hatchery's return footprint (circle 1 = measured
  worker stand when returning; circle 2 = +0.4u).
- Steps 4–5 (the **return push**: the outgoing miner and the staging worker
  push each other so the miner reaches the hatchery sooner) — discussed,
  approved in principle, NOT yet built or tested.
- Full-book context: perfect 700-frame book for the first set; then 16-worker
  per-base saturation speed mining as the endgame; extractor finishes ~48s
  (Sharky timing) → 3666 the vespine workers onto VA.

## 8. Open items (tomorrow's menu)

1. **Handoff jostle** (+2–4f on SB/BB): during the swap both workers squeeze
   to ~0.3u of the mineral for a few frames. Candidate fixes: ordering (miner
   holds its return until the waiter's queued harvest has fired), or the
   waiter holds 0.3u short of NP until the flip (owner's step-2 spec).
2. **NW mirror run**: the near point derives from the live hatchery, so it
   should mirror automatically (BA's perpendicular shift sign is derived from
   the BB direction) — but UNVERIFIED. Run seed 1.
3. **NearPointRadius 0.70 test**: expect the pathing floor to clamp stands to
   the same 0.9–1.0u; only worth it to confirm the floor.
4. **Return-trip rolling** (owner 2026-10-25 21:15, approved pattern): same
   anti-deceleration technique on the hatchery return leg; T3/Y1 were the
   test subjects.
5. **YB at 121f** vs the ~116f pair baseline — mine whether the residual is
   the staging spot or the YA-side mass stand at (133.69,23.62).
6. **Return push (steps 4–5)** — the next real cycle-time gain after the
   jostle fix.
7. Live processes: the 22:07 live session (near-point build, seed 3) may still
   be running — `taskkill //F //IM SC2_x64.exe //T` before new runs.

## 9. How to run / test

```bash
# build (from C:\Users\marca\source\repos\BabyShark)
"C:\Program Files\dotnet\dotnet.exe" build BabySharkBot/BabySharkBot.csproj -c Debug --no-restore

# SE test run (~3.5 min; kills SC2 afterwards)
cd BabySharkBot/bin/Debug/net9.0
./BabySharkBot.exe --smhandoff --seed=3 --smradius=0.25 > <log> 2>&1 &
# wait, kill bot, taskkill //F //IM SC2_x64.exe //T
# seed 1 rolls NW (unverified in smhandoff mode)

# newest telemetry
ls -t data/mining_tests/worker_commands_BabyShark_*.jsonl | head -1
```

Analysis scripts (stand clustering, screenshot calibration, cycle parsing)
from 2026-10-30 live in the Kimi workspace
`C:\Users\marca\Documents\kimi\tasks\2026-10-22\08-57-52-c54bf904\`
(`global_clusters.py`, `analyze_white.py`, `measure_stands.py`,
`cluster_stands.py`, owner screenshots `owner_shuffle_31.png`).


---

## 2026-10-31 — --f0test: first-20-frames no-assist baseline (Shortest Paths & Bumping phase)

New mode `--f0test` (`Services/F0TelemetryService.cs`, same owns-all-commands
pattern as `--smhandoff`). Owner spec 2026-10-31 16:15.

**Choreography**: T1 rolls 2u-past the TA SMH (homing rolling waypoint, MOVE
re-issued every frame, SMART at ≤0.45u — the champion N8 gate), Y1 same to the
YA SmHarvestPoint. T4+T5 mineral-walk (SMART) to SA, B2+Y4 mineral-walk to BA
at frame 0. Suicide (attack own hatchery, 3674): T2/T3/S2/Y2/Y3/Y5 at frame 0;
the six telemetry workers when BOTH rollers arrived or fail-open frame 26
(owner guessed arrival <19; measured no-assist arrival is ~21-22, so 26).

**Stay-on-task lock (owner correction 2026-10-31 16:40)**: the first version
failed the six open at frame 19 — before anyone arrived — so all six turned
around mid-path and the telemetry was garbage. Two fixes: (1) fail-open moved
to frame 26 so T1/Y1 actually ARRIVE (1183 on the mineral) before the suicide;
(2) `TickWalker` re-asserts the walk SMART whenever a walker's order leaves
SA/BA, so the engine's ~15-frame busy-mineral re-target can never peel them
off task. Verified in both starts: re-assert fires ONLY at frame 0 (the
game's initial auto-mine order); after that all six hold their assignments
until the suicide lands the frame both rollers arrive.

**Measured no-assist arrival baseline (the numbers bumping must beat)**:

| worker | SE arrive | NW arrive | SMART fired |
|--------|-----------|-----------|-------------|
| T1 → TA SMH | f22 | f21 | SE f21 / NW f20 (0.41-0.43u out) |
| Y1 → YA HP  | f21 | f21 | SE f20 / NW f20 |

**Start-relative radii** (circles drawn at the workers' start positions,
frames 9/11/13 — identical across runs, NW within 0.012u of SE):

| role | f9 | f11 | f13 |
|------|-----|-----|-----|
| T1 | 0.434 | 0.635 | 0.875 |
| Y1 | 0.439 | 0.645 | 0.889 |
| B2 | 0.431 | 0.632 | 0.872 |
| T4 | 0.431 | 0.632 | 0.872 |
| T5 | 0.436 | 0.639 | 0.881 |
| Y4 | 0.432 | 0.632 | 0.872 |

Acceleration profile visible in the per-frame logs: ~0.05 u/f through f9,
~0.10 u/f by f13, ~0.15 u/f mid-run — full 0.176 cruise is not reached on a
2.2u journey.

**Overlay** (drawn every frame from frame 0, current start only): teal line
T1 start → TA SMH, yellow line Y1 start → YA HP, red mineral-walk lines
T5 start → SA/SB/BB centers and B2 start → BA/BB/SB centers (mineral CENTERS
per owner: mineral walk targets the X,Y of the mineral, not the harvest
spot), plus the 18 radius circles (T1 teal, Y1 yellow, B2/T4 white, T5/Y4
purple). Radii hardcoded in `F0CircleRadii` (SE measurement, mirrored).

**Run configs**: `--f0test --seed=3` (SE), `--f0test --seed=1` (NW).
Role note: the owner's "V4" = role **Y4** (no V4 role exists in the 12-worker
roster: T1-T5, Y1-Y5, S2, B2).

Logs: workspace `f0test_se.log`, `f0test_se2.log`, `f0test_nw.log`.

### --f0test choreography v2 (owner spec 2026-10-31 19:13) — SEVEN roles

Y5 joined the roles. Phase books (F0TelemetryService.ChoreoBooks):
- f0: Y4+Y5 MOVE -> BB SM harvest point, T5 MOVE -> SA SMHP; T4 mineral-walk
  -> SA, B2 mineral-walk -> BA (locked, stay-on-task).
- f14: T4 MOVE -> TB SMHP; B2 MOVE -> YB SMHP.
- f19: T4 mineral-walk -> TB, B2 mineral-walk -> BB, Y4 mineral-walk -> YB.
Suicide-0 group is now FIVE (T2,T3,S2,Y2,Y3). Verified SE + NW: phases fire
exactly on frame, only re-asserts are the f0 auto-mine corrections and the
f19 phase-change SMARTs. T1/Y1 arrive f21 both starts, suicide f21.

Y5 radii (SE, frames 10/13/16/19): 0.535 / 0.882 / 1.316 / 1.828.
Drawing: phase-aware mover lines from each mover's start — white T4/B2,
purple T5/Y4, yellow Y5; RED for mineral-walk phases (target = mineral
center), colored point for MOVE phases (target = SM harvest point). Launch
profiles "F0Test SE/NW (step)" now include --live for SC2-client stepping.

### Drawing convention fix + collision gate (owner feedback 2026-11-01 07:30)

Owner: "white line when moving, red line when mineral walking" (his
2026-10-27 convention). He misread T5's purple role-colored MOVE->TB line
at frame 15 as mineral-walking. Fix in F0TelemetryService.DrawAnnotations:
mover line color is now purely phase-kind — WHITE for MOVE phases (target
= SM harvest point), RED for MineralWalk phases (target = mineral center),
for all five movers (T4, T5, B2, Y4, Y5). Role-colored circles around
start positions unchanged. Unused MoverLineColor helper deleted.

Collision manager question ("is collision manager interfering"): verified
WorkerCollisionAvoidanceService has exactly one call site inside
ExecuteRuntimeWorkerInstructions, unreachable in --f0test mode (dispatch
returns earlier). Gated it anyway with `&& !Settings.F0TelemetryMode` in
BabySharkMiningManager.cs so the intervention is hard-disabled in f0test.
WorkerAwareCollisionManager is a dead stub (returns zero actions). Any
"avoidance" seen on screen is the SC2 engine's own pathing, not the bot.

Headless verification (2026-11-01), seeds 3 (SE) and 1 (NW), both clean:
- T5 holds MOVE->TB phases 0-18, first walk log at f19 (proof it was never
  mineral-walking at f15 — the red he saw was the old purple role line).
- T4: walk->SA f0, MOVE->TB f14, walk->TB f19. B2: walk->BA f0, MOVE->YB
  f14, walk->BB f19. Y4: MOVE->BA f0, walk f19. Y5: MOVE->BB f0 only.
- T1/Y1 rolling MOVE, SMART f20, 1183 on TA/YA f21 both starts; seven-role
  suicide f21.

### Follow phases f13-f17 (owner spec 2026-11-01 08:21)

T4+T5 follow T1, B2+Y4+Y5 follow Y1: every frame a fresh MOVE to the
roller's CURRENT position (deliberate collision course for the push).
New ChoreoKind.FollowRole with EndFrame=17; after the window the phase
goes quiet (no commands) until the f19 mineral walks. Drawing: white
start->roller-live-position line, redrawn per frame. Verified SE + NW:
phase logs fire f13 and f19 exactly; telemetry shows ability-16 MOVEs in
flight f13-f17 with workers converging on the roller. SE: Y1 f20, T1 f21.
NW: both f21. Suicide after both arrivals.

### f13/f16 SMH convergence + f19 SMARTs (owner spec 2026-11-01 09:03)

Replaced the per-frame follow with: f13 and f16 MOVE for all seven to the
TA/YA SMH (rollers get a direct MOVE onto the SMH point those two frames
instead of the rolling 2u waypoint), then frame-19 SMARTs for all seven:
T1:TA, Y1:YA, T4:TB, T5:SB, B2:BB, Y4:YB, Y5:BB. New ChoreoKind.SmartTo
reuses the walk-lock (SMART re-assert if peeled). Drawing: SMART phases
draw a RED line to the mineral center. Verified SE + NW: f13/f16 MOVEs
land exactly, all seven SMARTs issued f19, arrivals T1 f20 / Y1 f20 BOTH
starts — ties the best result and matches it consistently (previous f20s
were one-off runs).
