# BabyShark Architecture

## Scope

BabyShark is a .NET 9 StarCraft II bot in `BabySharkBot/` built on the copied Sharky framework in `Sharky/`. BabyShark owns the bot composition, map-data model, worker labels, CCA startup choreography, JIT mining, custom managers, and custom microtasks. Sharky supplies the game connection, protocol abstractions, base managers, unit data, pathing, and shared microtask infrastructure.

`Sharky/` is an upstream protected dependency. BabyShark code should integrate with it without modifying it unless the owner explicitly authorizes a file-specific exception.

## Runtime composition

The composition root is `BabySharkBot/BabySharkBot.cs`, class `BabySharkAI`.

At construction time it:

1. Creates an underlying `DefaultSharkyBot` to obtain Sharky services and infrastructure.
2. Clears the default manager and microtask collections.
3. Registers the required Sharky managers for unit, map, enemy-race, base, targeting, and debug state.
4. Creates the shared BabyShark services:
   - `WorkerLabelService`
   - `CrosshairService`
   - `MineralLabelService`
   - `VespeneLabelService`
   - expansion and placement services
   - `chrisCrossAppleSause` for CCA choreography
5. Creates and registers:
   - `BabySharkUnitManager`
   - `BabySharkMiningManager`
   - `DrawOnlyManager`
   - `CcaManager`
6. Registers the custom `CustomMiningTask`, `TeamPatchMiningTask`, and scouting microtask through `RegisterRequiredMicroTasks()`.

The public entry point is `BabySharkBot/Program.cs`. It starts map-data loading, creates `BabySharkAI`, selects the build configuration, and runs either single-player or ladder mode.

## Source layout

```text
BabySharkBot/
├── BabySharkBot.csproj
├── Program.cs                         # Process entry point and game-mode selection
├── BabySharkBot.cs                    # BabySharkAI composition root and game-loop proxy
├── ZergBuildChoices.cs                # Build registration and matchup sequences
├── Builds/                            # BabyShark build strategies and build steps
├── Managers/                          # Stateful frame-loop coordinators
│   ├── BabySharkMiningManager.cs      # Main mining/JIT state and commands
│   ├── CcaManager.cs                  # CCA frame lifecycle and handoff
│   ├── BabySharkUnitManager.cs        # Custom unit observation/state integration
│   ├── BabySharkBuildManager.cs       # Build execution after CCA handoff
│   ├── DrawOnlyManager.cs             # Persistent debug drawing
│   └── ManagerDebugService.cs         # BabyShark drawing adapter
├── Services/                          # Reusable mining, placement, and expansion logic
│   ├── chrisCrossAppleSause.cs        # CCA worker choreography/state machine
│   ├── JitPrepositionService.cs       # Build-worker prepositioning
│   ├── TeamColorService.cs            # Team prefix/color mappings
│   └── ...
├── MicroTasks/                        # Sharky MiningTask/MicroTask specializations
│   ├── CustomMiningTask.cs            # Suppresses default Sharky mining labels
│   ├── TeamPatchMiningTask.cs         # Coordinates JIT prepositioning
│   └── BabySharkOverlordScoutTask.cs
├── Setup/                             # Map lifecycle, DTOs, labels, and settings
│   ├── BaseDtos.cs                    # MemoryPack DTOs and label/drawing services
│   ├── InitialMapData.cs              # New-map generation engine
│   ├── SecondaryMapData.cs            # New-spawn processing for known maps
│   ├── OngoingMapData.cs              # Current-spawn refresh and assignment resolver
│   ├── MapDataManager.cs              # Async cached `.dat` loading
│   ├── WorkerLabelChainHelper.cs      # W-chain generation
│   ├── TeamLabelRegistrationHelper.cs # Worker/team/mineral assignments
│   └── Settings.cs                    # Runtime flags and current-spawn state
└── Manager/
    └── WorkerLabelChangedEventArgs.cs # Legacy singular directory for event args
```

The project uses the SDK default compile glob, so `.cs` files under `BabySharkBot/` are compiled automatically. The project references `Sharky/` and `RLIntegration/` and uses MemoryPack `1.21.4`.

## Startup and map-data lifecycle

```text
Program.Main
    │
    ├─ Resolve local or ladder map name
    ├─ MapDataManager.TryLoadMapDataAsync(mapName)
    ├─ Create GameConnection and BabySharkAI
    └─ RunSinglePlayer(...) or RunLadder(...)
            │
            ▼
BabySharkAI.OnStart
    │
    ├─ Count self workers and reset mining startup state
    │
    ├─ Cached map data unavailable
    │      └─ InitialMapData.GetNewMiningData(...)
    │             ├─ Discover API/self town-hall start locations
    │             ├─ Scan neutral minerals and vespene
    │             ├─ Keep only self workers for worker labeling
    │             ├─ Group resources by nearby start location
    │             ├─ Calculate per-start mineral COM
    │             ├─ Build W-chain worker labels
    │             ├─ Greedily order minerals per start
    │             ├─ Register COM and labels
    │             ├─ Build TeamPatchAssignmentDto values
    │             └─ Return MawBaseLocationData
    │
    ├─ Cached map data available
    │      └─ Resolve current spawn from observed self town hall
    │             └─ Select only the matching per-start records
    │
    ├─ ProcessVisibleUnits(...)
    ├─ Enable CCA for the resolved spawn
    └─ Record the current spawn observation
            │
            ▼
StartupAwareSharkyBot.OnFrame
    └─ Runs the registered managers and aggregates their actions
```

`MapDataManager` loads `data/base/{map}.Version{SpeedMiningVersion}.dat` asynchronously. `Program` writes the current `MawBaseLocationData` back to that versioned file after the game ends. Cached data is indexed by start location, so the current observed self town hall is the authority for selecting the active index.

## Map and assignment data model

`MawBaseLocationData` in `Setup/BaseDtos.cs` is the serialized source model. Important per-start collections include:

- `StartingTownHall`
- `MainMinerals` and `MainVespene`
- `MineralCenterOfMass`
- `StartingUnits`
- `OrderedMainMinerals` and `OrderedMainVespene`
- `TeamPatchAssignments`
- `SecondaryTeamPatchAssignments`
- `AssignmentsByWorkerCount`
- `AssignmentFlagsByStart`

`OngoingMapData.ResolveTeamAssignments(mapData, startIndex)` is the runtime assignment resolver. Consumers must pass the current spawn index and must not search for the first non-empty assignment list across all starts.

Mineral assignments are validated by position and, when available, resource unit tag. Worker assignments must reference live self-worker tags. If current-spawn data is missing or invalid, CCA fails closed instead of issuing commands against another start's resource line.

## Worker labels

`WorkerLabelService` is defined in `Setup/BaseDtos.cs` and uses `ulong` unit tags:

```csharp
SetLabel(string label, ulong tag, Point? pos = null)
GetLabel(ulong tag)
GetTag(string label)
RemoveLabel(string label)
RemoveLabelByTag(ulong tag)
```

The service maintains synchronized label-to-tag and tag-to-label dictionaries and raises `LabelChanged` for effective mapping changes. W-chain labels are produced by `WorkerLabelChainHelper`; team labels are applied by `TeamLabelRegistrationHelper` and color/prefix metadata comes from `TeamColorService`.

The initial worker scan and all frame-level worker extraction are self-only. Static labels such as hatchery, overlord, and larva labels are separate intentional entries.

## Mining phases and command ownership

### CCA startup phase

`CcaManager` owns the startup choreography while `Settings.ccaMining` is true. It calls `chrisCrossAppleSause.BuildBumpOrders()` using the current spawn's assignments and live worker entries.

`BabySharkMiningManager` deliberately emits no normal unit commands during this phase. `TeamPatchMiningTask` may issue high-priority JIT prepositioning commands, but CCA owns the worker bump/order choreography.

### Handoff

At frame 35, `CcaManager` captures final CCA actions, signals `BabySharkMiningManager.SignalMiningStarted()`, unregisters itself, and adds `BabySharkBuildManager` to the manager list. `Settings.ccaMining` becomes false.

### Steady-state mining

After handoff, `BabySharkMiningManager.OnFrame()`:

- refreshes worker and assignment state;
- executes JIT mining rotations;
- handles idle-worker fallback;
- updates mineral return-rate tracking;
- draws debug state through the custom drawing services.

`JitPrepositionService` handles build-order worker movement, such as prepositioning a team-four worker near the V2 placement reference. It resolves the team from `OngoingMapData`, not from an arbitrary per-start collection.

`CustomMiningTask` suppresses Sharky's generic mining debug labels. `TeamPatchMiningTask` coordinates prepositioning and does not replace the production mining manager.

## Debug drawing

Drawing is split between data generation and runtime visualization, but the current implementation intentionally registers some COM data during initial map generation:

- `InitialMapData` calculates and registers per-start COM records through `CrosshairService`.
- `BabySharkMiningManager.DrawDebugVisuals()` draws worker labels, COMs, mineral labels, target points, expansion points, and placement guides.
- `DrawOnlyManager` invokes `DrawDebugVisuals()` every frame so labels remain visible even when the mining manager is skipped for performance.
- `ManagerDebugService` adapts the Sharky debug service.
- `CrosshairService` stores COM records using `SC2APIProtocol.Point` and `SC2APIProtocol.Color`.

Do not invent a parallel drawing protocol. Use the existing Sharky debug service adapter and the established SC2 protocol types.

## Type and ownership rules

- Unit tags are `ulong`, matching SC2 protocol and Sharky unit data.
- Serialized positions use `Vector2Dto` from `BabySharkBot.Setup`.
- Runtime control positions commonly use Sharky's `Point2D`.
- Debug drawing positions use `SC2APIProtocol.Point`.
- Map generation owns discovery and serialized data construction.
- `OngoingMapData` owns current-spawn refresh and assignment lookup.
- Managers own frame-loop state and command orchestration.
- Services own reusable calculations and state-machine logic.
- Microtasks integrate with Sharky's `MiningTask`/`MicroTask` contracts.
- Build and runtime code must not modify `Sharky/` without explicit authorization.
