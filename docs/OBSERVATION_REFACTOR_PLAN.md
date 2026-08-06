# Observation and CCA Refactor Plan

## ✅ COMPLETE

The observation and CCA refactor is finished. The bot now uses a centralized observation layer, eliminating redundant unit scanning and ensuring stable, orientation-independent normalization on Frame Zero.

### Key Deliverables Completed:
- [x] **ObservationManager**: Reads raw observation once per frame; classifies all self units, minerals, and vespene.
- [x] **Shared State**: `Globals.CurrentObservation` (backed by `ObservationSnapshotDto`) is the source of truth for all managers.
- [x] **Frame-Zero Ownership**: `chrisCrossAppleSause` establishes W12-anchor, W-chain, and Team roles before mining begins.
- [x] **Retired Legacy Scanning**: Redundant loops in `ProcessVisableUnits`, `OngoingMapData`, and `SecondaryMapData` have been removed.
- [x] **Mineral Accounting**: Established `StartingTotalMinerals` on Frame Zero for precise build-order tracking.

---

## Purpose

Refactor BabyShark so the raw StarCraft II observation is read once per frame, stored in shared DTO state, and consumed by managers without each manager scanning every game unit independently.

The opening CCA phase will own frame-zero worker/mineral labeling and opening movement. Later managers will own labels for units in their domain when those units become relevant.

This document records the current agreement and the decisions that remain open. No implementation should proceed beyond the confirmed scope until unresolved items are discussed.

## Current agreement

### Observation ownership

A new `ObservationManager` will:

- Read the raw `ResponseObservation` once per frame.
- Store the current game-unit state in `BaseDtos`-backed state.
- Track minerals, vespene, self workers, and other visible self units.
- Detect new units, completed morphs, disappearing units, and state transitions.
- Maintain unknown-unit collections:
  - `unknownLarva`
  - `unknownWorker`
  - `unknownQueen`
  - `unknownOverlord`
- Reset `UnitReadyForLabeling`-style readiness state at the beginning of each frame.
- Set readiness only when a relevant unit becomes ready for a manager.
- Never assign display labels.

Other managers consume the stored observation snapshot instead of scanning the raw unit list independently.

### InitialMapData ownership

`InitialMapData` remains responsible for map geometry and initial map records:

- API start locations (`ApiLoc` and `ApiStart` data).
- Townhall positions.
- Mineral positions.
- Vespene positions.
- Possible expansion locations and resources.
- Mineral center of mass.
- Unordered mineral/resource records.

`InitialMapData` does not know the authoritative frame-zero worker arrangement. Worker positions are considered unknown for final role assignment until CCA receives the frame-zero observation snapshot.

### Frame-zero CCA ownership

`chrisCrossAppleSause` owns the frame-zero opening setup:

1. Read the prepared frame-zero observation state.
2. Read the known mineral records and COM from `BaseDtos`.
3. Select `W12` first using the live worker positions relative to mineral COM.
4. Build the consistent worker chain from `W12` through `W1`.
5. Build the greedy mineral relationship from the live worker chain and known minerals.
6. Determine team roles only after W12/W1 normalization exists.
7. Apply initial worker team labels.
8. Apply initial mineral labels.
9. **Mineral Accounting**: On Frame Zero, sum all `unit.MineralContents` from the starting minerals to establish `CurrentTotalBaseMinerals` and `StartingTotalMinerals`.
10. Select any CCA-specific frame-zero larva needed for an opening morph.
11. Issue the first CCA movement line.

The initial W labels exist only to normalize the starting formation. They are not persistent team roles.

### Worker-chain invariants

- Twelve-worker opening: `W12` is the worker farthest from mineral COM; `W1` is the opposite end.
- Eight-worker opening: `W8` is the worker farthest from mineral COM; the chain continues through `W1`.
- `M[0]` is on the same chain side as `W1`.
- `M[7]` is on the same chain side as `W12`.
- Map rotation, mirroring, and screen left/right/top/bottom have no semantic meaning.
- The greedy chain is the orientation-independent normalization.
- Teal and Yellow cannot be finalized before W12/W8 anchoring and the worker chain are established.

### Confirmed 12-worker team membership

```text
Teal:   W2, W3, W4
Salmon: W5, W6, W1
Blue:   W7, W8, W12
Yellow: W9, W10, W11
```

The final labels are role labels:

```text
Teal:   T1, T2, T3 -> TA/TB
Salmon: S1, S2, S3 -> SA/SB
Blue:   B1, B2, B3 -> BA/BB
Yellow: Y1, Y2, Y3 -> YA/YB
```

Final suffixes are role/target assignments. They must not be copied directly from W-number order.

### Confirmed outside-mineral logic for Teal and Yellow

- **Teal outside-far (M[0] is Far)**:
  - `W2` becomes `T2` (furthest outside).
  - `W4` becomes `T1` (closest to TA).
  - `W3` becomes `T3` (push pair with T1).
- **Yellow outside-far (M[7] is Far)**:
  - `W11` becomes `Y2` (furthest outside).
  - `W9` becomes `Y1` (closest to YA).
  - `W10` becomes `Y3` (push pair with Y1).
- **False tests (Outside is Near)**:
  - Teal: `W4` becomes `T2` -> `TB`.
  - Yellow: `W9` becomes `Y2` -> `YB`.
  - Remaining workers in team fill 1/3 push roles based on confirmed geometry logic.

### Confirmed CCA cadence

The current non-RL cadence is:

```text
Frame 0:  establish labels and issue the first command line
Frame 5:  reevaluate movement
Frame 10: reevaluate movement
Frame 15: reevaluate movement
Frames 16-34: evaluate every frame and issue commands when required
Frame 35: stop CCA command generation and hand off
```

The exact frame-35 boundary must prevent two managers from issuing commands for the same unit on the same frame.

Future RL integration may change the cadence, but not in this refactor.

### Confirmed team movement rules

#### Teal and Yellow

- `T1`/`T3` must remain side by side during CCA.
- `Y1`/`Y3` must remain side by side during CCA.
- The adjacent pair is required for chrisCrossAppleSause push-repell acceleration.
- The pair must be selected before optimizing the team's `2` worker.

#### Salmon and Blue

- Salmon CCA pushing is always false.
- Blue CCA pushing is always false.
- `S1`/`S2` and `B1`/`B2` follow the direct 8-worker-style mineral behavior.
- `S3` and `B3` use ordinary MOVE commands, not the bump state machine.
- **Targeting and movement (Frames 0, 5, 10, 15)**:
  - B3 targets `BA`.
  - S3 targets `SA`.
  - On these frames, calculate the midpoint (half the distance) between the unit's current X,Y (from ObservationManager DTO) and the target mineral's X,Y.
  - Issue a `MOVE` command to that midpoint.
- The helper destinations provide indirect push-repell assistance to Teal and Yellow.

#### Fixed middle-chain targets

```text
W5 -> M[2]
W6 -> M[3]
W7 -> M[4]
W8 -> M[5]
```

These fixed targets exist to avoid cross-traffic congestion. The final `1` or `2` suffix is derived from whether the target is the team's A or B mineral.

### Label ownership

Observation never assigns labels.

The only expected unlabeled workers are the initial workers during frame zero before CCA establishes the W-chain. After frame zero, workers should not remain unlabeled.

Managers assign labels for their own domains:

- CCA: initial workers, initial minerals, opening roles, and CCA-specific frame-zero selections.
- Build manager: larva naming, morph-related labels, and build-order unit transitions.
- Queen manager: new queens.
- Army manager: new army units.
- Other domain managers: labels for their own units.

### Larva scope for this refactor

General larva naming is deferred to build-order work.

For now:

- Available larva do not receive labels merely because they are observed.
- Unknown larva remain in `unknownLarva`.
- CCA may select the larva nearest to `GA`/`TA` when a frame-zero opening morph requires it.
- CCA may assign the relevant `T3` or `T4` opening role at that moment.
- Queens and Overlords are not labeled by ObservationManager.

Possible future label forms include `LE0-*` for expansion-zero larva and `LM0-*` for macro-hatchery-zero larva, but the canonical spelling is not yet finalized.

## Implementation plan

### Phase 0: Freeze current behavior and inventory callers

Before changing code:

- Read `PROJECT_CANON.md` and this document.
- Inventory all raw `ResponseObservation` unit loops.
- Inventory all calls to:
  - `ProcessVisableUnits`
  - `OngoingMapData.RefreshMiningData`
  - `SecondaryMapData.GetNewMiningData`
  - `WorkerLabelService.SetLabel`
  - `TeamLabelRegistrationHelper`
  - CCA observation/command methods.
- Identify which loops are true observation reads and which are consumers of already-derived state.
- Record current manager ordering and frame-35 handoff behavior.

Deliverable: a caller/ownership table before deleting or moving code.

### Phase 1: Define shared observation DTO state

Extend `BaseDtos` with explicit observation state without assigning labels:

- Current self workers.
- Current minerals and their live tags/positions.
- Current vespene and their live tags/positions.
- Current queens.
- Current overlords.
- Current larva.
- Other self army units when present.
- Unknown collections for each unit category.
- Per-unit first-seen/last-seen state.
- Morphing state.
- Completed state.
- `UnitReadyForLabeling` or equivalent transition flags.
- Current frame number.
- Current worker count.
- **MineralContents**: `MineralDto` should store `unit.MineralContents` from the observation.
- **BecameVisible flag**: A boolean flag for every unit indicating it transitioned to visible in the current observation loop.

The DTO state must distinguish:

```text
Observed this frame
Known from prior frame
Newly created
Morphing
Completed and ready
Missing/dead
```

No label assignment belongs in this phase.

### Phase 2: Add ObservationManager

Create a BabyShark-owned manager that runs once per frame before CCA and other consumers.

Responsibilities:

1. Read the raw observation once.
2. Classify self workers, minerals, vespene, larva, queens, overlords, and other self units.
3. Update the shared DTO snapshot.
4. Compare against the previous frame.
5. Mark new/completed/morphing units as ready for their domain manager.
6. Preserve unknown units without assigning labels.
7. Expose indexed lookup by unit tag and category.

The manager must not:

- Assign W labels.
- Assign team labels.
- Assign mineral labels.
- Choose build orders.
- Issue movement or harvest commands.

### Phase 3: Move frame-zero labeling into CCA

CCA receives the prepared snapshot and runs its own opening evaluation at the required cadence.

Frame zero responsibilities:

- Establish W12/W8 anchor.
- Build W-chain.
- Establish greedy mineral relationship.
- Assign team membership and final worker roles.
- Assign mineral labels.
- Select any CCA-specific larva needed for the opening.
- Issue the first command line.

CCA must not rescan the raw observation. It consumes ObservationManager state.

### Phase 4: Move CCA movement evaluation to the new cadence

Implement the cadence as an explicit state machine:

```text
Frame 0, 5, 10, 15:
  reevaluate planned movement

Frame > 15 and before handoff:
  evaluate every frame
  issue a command only when the worker arrives, becomes blocked,
  changes target, or otherwise requires a new command

Handoff frame:
  close CCA command generation before the next manager issues commands
```

The command coordinator must guarantee that one unit receives at most one non-queued command per frame.

### Phase 5: Introduce post-CCA manager consumption

After CCA handoff:

- ObservationManager remains active every frame.
- Mining manager consumes minerals, vespene, and self-worker state from the snapshot.
- Build manager consumes resources, larva readiness, and morph readiness.
- Queen and army managers remain inactive until their prerequisites exist.
- Managers are added or enabled only when their domain becomes necessary.

### Phase 6: Migrate and retire duplicate observation paths

After the new path is verified:

- Remove raw-unit scanning from `ProcessVisableUnits` or reduce it to a compatibility adapter.
- Remove raw observation responsibilities from `OngoingMapData`.
- Remove raw observation responsibilities from `SecondaryMapData`.
- Move only map-data lookup or compatibility behavior that is still needed.
- Remove duplicate labeling calls from all non-owner paths.
- Confirm no manager independently scans all raw units every frame.

Do not delete the three existing files in Phase 1. Retire them only after caller search confirms they are no longer required.

### Phase 7: Add build-order larva ownership later

This is explicitly outside the first opening refactor:

- Define canonical larva label spelling.
- Associate larva with a hatchery using a reliable observation rule.
- Handle expansion-zero and macro-hatchery-zero larva namespaces.
- Handle morph start/completion transitions.
- Handle new queens that emerge directly from hatcheries.
- Assign rally points and build-order roles.

## Acceptance criteria for the first refactor

- Raw observation is classified once per frame by ObservationManager.
- CCA does not scan raw units independently.
- CCA establishes W12/W8 before assigning Teal/Yellow or final team roles.
- CCA performs frame-zero worker and mineral labeling.
- No worker is unlabeled after frame zero unless it is a newly created worker waiting for its domain manager.
- CCA reevaluates at frames 0, 5, 10, 15, then every frame through the pre-handoff window.
- CCA stops before the handoff command frame.
- No unit receives duplicate non-queued commands on the handoff frame.
- Mining consumes snapshot state rather than performing a full raw-unit scan.
- InitialMapData provides map geometry but does not finalize worker-relative roles.
- 8-worker and 12-worker startup paths remain distinct.
- Existing worker-count-specific assignment storage remains intact.
- Sharky remains unmodified.
- The project builds successfully.

## Decisions still requiring discussion

### A. InitialMapData failure behavior

If map geometry is missing or incomplete at frame zero, should CCA:

1. Wait without issuing commands until InitialMapData completes; or
2. Use visible live minerals as a temporary fallback?

Recommended default: wait and fail closed, because the greedy chain and team roles depend on stable map geometry.

### B. Exact mineral-label scope at frame zero

Should CCA label:

1. All eight ordered minerals immediately; or
2. Only the active team pairs first, with the remaining labels added later?

Recommended default: label all eight once the greedy mineral relationship is established, because labels are useful for debugging and must remain stable.

### C. New worker after frame zero

If a worker appears after frame zero and is in `unknownWorker`, should:

1. CCA temporarily assign a worker label while CCA is active; or
2. The worker remain unknown until a worker/build manager assigns it?

The current stated direction is that managers own labels, but workers should not remain unlabeled after frame zero. This needs an explicit owner during the opening transition.

### D. Initial worker color transition

Confirm the exact 8-to-12 transition behavior. The current example is:

```text
8-worker state:
  G1, G2

9th worker/team formation:
  G1 -> T1
  G2 -> T2
  new worker -> T3
```

Clarify whether the transition happens immediately when the third worker enters a team or only after the complete 12-worker opening set is established.

### E. Larva label spelling

Choose one canonical format before build-order implementation:

```text
LE0-17 / LM0-18
```

or another explicit format. The current code and historical documents contain multiple spellings such as `Leo13`, `LE0-17`, and `Lm0-18`.

### F. Larva-to-hatchery association

If macro-hatchery larva labels are used later, decide whether proximity is sufficient:

```text
nearest Hatchery within 4.0 game units
```

with a generic fallback when no hatchery is close, or whether a stronger association mechanism is required.

### G. Handoff manager ordering

Confirm the exact manager order on frame 35:

```text
ObservationManager snapshot
    -> CCA closes and emits no command
    -> handoff manager/build manager begins
    -> mining/build consumers issue commands
```

The goal is to guarantee one command owner per unit per frame.

### H. Compatibility wrappers

Confirm whether `ProcessVisableUnits`, `OngoingMapData`, and `SecondaryMapData` should:

1. Remain as compatibility wrappers until the migration is complete; or
2. Be removed immediately after the new ObservationManager is introduced.

Recommended default: retain wrappers during migration, remove them only after caller inventory proves they are unused.

### I. Manager activation policy

Confirm the prerequisite rules for enabling future managers:

```text
ObservationManager: always active
CCA: active during opening only
Mining manager: active when mining snapshot is available
Build manager: active when the build begins. Tracks `justPickedUp = isCarrying && !workerData.WasCarrying` to calculate minerals en route to the Town Center for precise structure timing.
Scouting manager: responsible for updating DTOs when scanning unknown minerals/vespene
Queen manager: active after spawning pool/queen prerequisites
Army manager: starts at frame 500 (requested by Build manager); responds to army units
```

The manager should not be created or enabled merely because its code exists.

## Risks

- Existing cached `.dat` files may not contain the new observation DTO fields and may need regeneration or a version bump.
- Moving labels out of `ProcessVisableUnits` can expose hidden callers that currently depend on side effects.
- The current `WorkerLabelService` is shared state; CCA and later managers must not race to relabel the same tag.
- Frame-zero ordering is sensitive: W12 must be selected before team assignment.
- Mineral positions are stable map data, but target routing depends on live worker positions.
- Handoff ordering can create duplicate commands if CCA and the next manager both process the same unit on frame 35.
- New units can appear between manager activation boundaries; the unknown collections must not silently lose them.

## Suggested first implementation slice

Do not attempt the entire refactor in one change. Start with a narrow vertical slice:

1. Add observation snapshot DTOs for minerals, vespene, workers, and frame number.
2. Add `ObservationManager` that classifies those categories once per frame without labels.
3. Make CCA consume the snapshot for frame-zero worker ordering.
4. Leave larva, queens, overlords, and army labeling deferred.
5. Verify frame 0/5/10/15/16-34 behavior and frame-35 command ownership.
6. Only then migrate build/mining consumers and retire duplicate observation loops.
