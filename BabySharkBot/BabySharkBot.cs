using Sharky;
using Sharky.Builds;
using Sharky.DefaultBot;
using Sharky.Managers;
using Sharky.MicroTasks;
using Sharky.MicroTasks.Mining;
using SC2APIProtocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BabySharkBot.Managers;
using BabySharkBot.Setup;
using BabySharkBot.Services;
using BabySharkBot.MicroTasks;
using RLIntegration;

namespace BabySharkBot
{
    /// <summary>
    /// Custom BabyShark bot that loads only essential Sharky managers.
    /// Replaces DefaultSharkyBot for lightweight, focused operations.
    /// </summary>
    public class BabySharkAI
    {
        // Singleton instance for cross-component coordination (used to register managers on demand)
        public static BabySharkAI Instance { get; private set; }

        public SharkyOptions SharkyOptions { get; set; }
        public FrameToTimeConverter FrameToTimeConverter { get; set; }
        public List<IManager> Managers { get; set; }
        public BabySharkMiningManager MiningManager => _miningManager;

        // Essential managers
        public DebugManager DebugManager { get; set; }

        // Essential services
        public DebugService DebugService { get; set; }
        public ProvisionalExpansionService ProvisionalExpansionService { get; set; }
        public ProvisionalExpansionService ProvisionalExpansionComSource { get; set; }
        public MineralReturnRateTrackerService MineralReturnRateTrackerService { get; set; }
        public ExpansionCOMService ExpansionCOMService { get; set; }
        public SpawningPoolPlacementService SpawningPoolPlacementService { get; set; }

        // Construction parameters
        private GameConnection _gameConnection;
        private DefaultSharkyBot _defaultBot;
        private BabySharkMiningManager _miningManager;
        private readonly ExtractorTrickService _extractorTrickService;
        private readonly WorkerCommandTelemetry _workerCommandTelemetry;

        // Exposed for test services (SmSweepService) that log their own telemetry
        // records from outside the BabySharkAI frame loop.
        public WorkerCommandTelemetry CommandTelemetry => _workerCommandTelemetry;

        /// <summary>
        /// Initialize BabySharkAI with a GameConnection instance.
        /// The actual Sharky managers are loaded through a proxy DefaultSharkyBot.
        /// </summary>
        public BabySharkAI(GameConnection gameConnection)
        {
            Instance = this;
            _gameConnection = gameConnection;
            _workerCommandTelemetry = new WorkerCommandTelemetry();
            // Create the underlying DefaultSharkyBot to manage all Sharky infrastructure
            _defaultBot = new DefaultSharkyBot(gameConnection);
            
            // Copy essential managers and services from DefaultSharkyBot
            SharkyOptions = _defaultBot.SharkyOptions;
            // Ensure debug drawing is enabled for BabyShark managers so ManagerDebugService will draw labels
            SharkyOptions.Debug = true;
            Console.WriteLine("BabySharkAI: Force-enabled debug drawing (SharkyOptions.Debug = true)");
            FrameToTimeConverter = _defaultBot.FrameToTimeConverter;
            
            // Refactor: Clear all managers and microtasks loaded by DefaultSharkyBot and only add what's necessary.
            Managers = _defaultBot.Managers;
            Managers.Clear();
            _defaultBot.MicroTaskData.Clear();
            Console.WriteLine("BabySharkAI: DefaultSharkyBot managers and microtasks cleared; starting with minimal set.");

            // Essential services
            DebugManager = _defaultBot.DebugManager;
            DebugService = _defaultBot.DebugService;

            // ObservationManager runs first so every later manager consumes the same frame snapshot.
            // CCA remains available as dormant code but is not part of runtime command ownership.
            var observationManager = new ObservationManager(_defaultBot.ActiveUnitData, _defaultBot.SharkyUnitData, _defaultBot.BaseData, _defaultBot.MapDataService, _defaultBot.UnitDataService, null);
            Managers.Add(observationManager);

            // BuildManager runs second and executes the selected opening build.
            _extractorTrickService = new ExtractorTrickService(_defaultBot);
            var buildManager = new BabySharkBuildManager(_defaultBot);
            // --naturaltest (owner spec 2026-10-28 14:13, "Test 1"): passive
            // observation build — production schedule only, zero worker
            // commands (the command sources are gated on
            // Settings.NaturalObservationMode inside the managers).
            buildManager.SetBuild(Settings.NaturalObservationMode
                ? new BabySharkBot.Builds.NaturalObservationBuild(_defaultBot)
                : Settings.SmHandoffMode
                    ? new BabySharkBot.Builds.SmHandoffBuild(_defaultBot)
                    : Settings.SmStageMode
                        ? new BabySharkBot.Builds.SmStageBuild(_defaultBot)
                        : Settings.SmSweepMode
                            ? new BabySharkBot.Builds.SmSweepBuild(_defaultBot)
                            : Settings.UseTwelvePoolBuild
                                ? new BabySharkBot.Builds.TwelvePoolBuild(_defaultBot)
                                : new BabySharkBot.Builds.BuildIne(_defaultBot, _extractorTrickService));
            Console.WriteLine($"BabySharkAI: active opening build = {(Settings.NaturalObservationMode ? "NaturalObservation (passive, production schedule only)" : Settings.SmHandoffMode ? "SmHandoff (handoff service owns all worker commands)" : Settings.SmStageMode ? "SmStage (stage service owns all worker commands)" : Settings.SmSweepMode ? "SmSweep (sweep service owns all worker commands)" : Settings.UseTwelvePoolBuild ? "TwelvePoolBuild (12 pool, race to 200)" : "BuildIne (mining test)")}");
            Managers.Add(buildManager);

            // Scouting consumes the observation prepared by the first manager.
            var scoutingManager = new ScoutingManager(_defaultBot.ActiveUnitData, _defaultBot.SharkyUnitData);
            Managers.Add(scoutingManager);

            // Initialize ManagerDebugService with Sharky's debug service and options
            // This allows custom managers to use ManagerDebugService.DrawText() etc.
            ManagerDebugService.Initialize(DebugService, SharkyOptions);

            // Create WorkerLabelService for worker label tracking
            var workerLabelService = new WorkerLabelService();

            // Create CrosshairService for COM visualization
            var crosshairService = new CrosshairService();

            // Create MineralLabelService for F1-F4, N1-N4 mineral label visualization
            var mineralLabelService = new MineralLabelService();

            // Create VespeneLabelService for V1, V2, V3, etc. vespene label visualization
            var vespeneLabelService = new VespeneLabelService();

            // Create ExpansionCOMService for expansion center-of-mass visualization (blue crosshairs)
            var expansionCOMService = new ExpansionCOMService();
            ExpansionCOMService = expansionCOMService;

            // Create ExpansionPointService for expansion townhall placement computation
            var expansionPointService = new ExpansionPointService();

            // Create ExpansionPointDrawService for expansion townhall placement visualization (green/yellow/orange points)
            var expansionPointDrawService = new ExpansionPointDrawService();

            // Create SpawningPoolPlacementService for mini-wall placement visualization
            SpawningPoolPlacementService = new SpawningPoolPlacementService(DebugService);

            // Create ProvisionalExpansionService for expansion/wall verification
            var provisionalExpansionService = new ProvisionalExpansionService();
            ProvisionalExpansionService = provisionalExpansionService;
            ProvisionalExpansionComSource = provisionalExpansionService;
            MineralReturnRateTrackerService = new MineralReturnRateTrackerService();
            Console.WriteLine("BabySharkAI: Mineral return rate tracker initialized for 12-16 drone counts");

            // Get MapDataService from DefaultSharkyBot for terrain height queries
            var mapDataService = _defaultBot.MapDataService;

            // Register only the necessary microtasks
            RegisterRequiredMicroTasks();
            InstallRlMicroControllerWrappers();

            // Create BabySharkMiningManager. CCA remains dormant and is not passed as a runtime owner.
            _miningManager = new BabySharkMiningManager(_defaultBot.ActiveUnitData, _defaultBot.SharkyUnitData, _defaultBot.CollisionCalculator, workerLabelService, crosshairService, mineralLabelService, vespeneLabelService, expansionCOMService, expansionPointService, expansionPointDrawService, provisionalExpansionService, MineralReturnRateTrackerService, FrameToTimeConverter, mapDataService, SpawningPoolPlacementService, null, _extractorTrickService);
            buildManager.ConfigureLabelServices(workerLabelService, mineralLabelService, vespeneLabelService, SpawningPoolPlacementService);
            Console.WriteLine("BabySharkAI: Created BabySharkMiningManager and configured BuildManager label ownership");

            // Register the mining manager so current-run labels and map assignments are refreshed every frame.
            Managers.Add(_miningManager);
            Console.WriteLine("BabySharkAI: Registered BabySharkMiningManager for per-frame label registration and mining updates.");

            // Drawing is independent from CCA and must remain active after CCA is retired.
            var drawOnlyManager = new DrawOnlyManager(_miningManager);
            Managers.Add(drawOnlyManager);
            Console.WriteLine("BabySharkAI: Registered DrawOnlyManager for worker and mineral labels.");

            // CCA is dormant. It is intentionally not registered in the runtime manager list.
            Console.WriteLine("BabySharkAI: CCA runtime registration disabled; worker instructions own mining commands.");

            Console.WriteLine("BabySharkAI initialized with essential BabyShark managers");
        }

        public CcaManager CcaManager => null;
        public chrisCrossAppleSause CcaMiningService => null;

        /// <summary>
        /// Register only the necessary microtasks for BabyShark operations.
        /// This method populates MicroTaskData from a blank slate.
        /// </summary>
        private void RegisterRequiredMicroTasks()
        {
            try
            {
                var microTaskData = _defaultBot.MicroTaskData;
                
                // No microtasks registered to ensure MicroManager has nothing to do if it were present.
                Console.WriteLine($"BabySharkAI: Registered {microTaskData.Count} essential microtasks.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkAI: Error registering microtasks: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public void DisableBuildManager()
        {
            try
            {
                Managers.RemoveAll(manager => manager is BuildManager);
                Console.WriteLine("BabySharkAI: BuildManager removed so the mining test can keep running.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkAI: Error removing BuildManager: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void InstallRlMicroControllerWrappers()
        {
            try
            {
                if (!Settings.EnableRLInference)
                {
                    return;
                }

                var rlConfig = new RLMatrixConfig
                {
                    ModelPath = Settings.RLModelPath,
                    MetadataPath = Settings.RLMetadataPath,
                    DatasetPath = Settings.RLTrainingDataPath
                };

                var policy = new TorchSharpPolicy(rlConfig);
                Console.WriteLine($"BabySharkAI: RL policy initialized (ready={policy.IsReady})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkAI: Failed to initialize RL policy: {ex.Message}");
            }
        }

        /// <summary>
        /// Create the bot instance for the game loop.
        /// Delegates to the underlying DefaultSharkyBot.
        /// </summary>
        public ISharkyBot CreateBot()
        {
            return new StartupAwareSharkyBot(this);
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            ConsecrationofMyStarCraftIIBotProject.Invoke();
            Console.WriteLine("BabySharkAI: OnStart called");
            Settings.CurrentMapName = gameInfo?.MapName ?? Settings.CurrentMapName ?? string.Empty;
            Settings.ResetRuntimeWorkers();
            Settings.ccaMining = false;
            
            var workersCount = observation?.Observation?.RawData?.Units?.Count(u => u != null && u.Alliance == Alliance.Self && (u.UnitType == (uint)UnitTypes.ZERG_DRONE || u.UnitType == (uint)UnitTypes.TERRAN_SCV || u.UnitType == (uint)UnitTypes.PROTOSS_PROBE)) ?? 12;
            Settings.WorkerCount = workersCount;
            Console.WriteLine($"BabySharkAI: Detected {workersCount} workers at start.");

            if (_miningManager == null)
            {
                Console.WriteLine("BabySharkAI: Mining manager is not initialized.");
                return;
            }

            _miningManager.ResetStartupState();

            if (!Settings.SerializeDataLoaded || !Settings.MapDataLoaded)
            {
                Console.WriteLine($"BabySharkAI: SerializeDataLoaded={Settings.SerializeDataLoaded}, MapDataLoaded={Settings.MapDataLoaded}, calling InitialMapData.GetNewMiningData()");

                try
                {
                   // System.Diagnostics.Debugger.Break();
                    var initialMapData = new InitialMapData();

                    var mapData = initialMapData.GetNewMiningData(gameInfo, data, observation, null, _miningManager.CrosshairService, ExpansionCOMService, _miningManager.ExpansionPointService, _miningManager.ExpansionPointDrawService, ProvisionalExpansionService, _defaultBot.MapDataService, _miningManager.WorkerLabelService);
                     Console.WriteLine($"[MAP HANDOFF 01] InitialMapData returned map={(mapData != null)} startLists={(mapData?.StartingMinerals?.Count ?? 0)} orderedLists={(mapData?.OrderedMainMinerals?.Count ?? 0)} start1Starting={(mapData?.StartingMinerals?.Count > 1 ? mapData.StartingMinerals[1]?.Count ?? 0 : -1)} start1Ordered={(mapData?.OrderedMainMinerals?.Count > 1 ? mapData.OrderedMainMinerals[1]?.Count ?? 0 : -1)}");
                     _miningManager.SetCurrentMapData(mapData);
                     Globals.CurrentMapData = mapData;
                     Console.WriteLine($"[MAP HANDOFF 02] Globals.CurrentMapData assigned start1Starting={(Globals.CurrentMapData?.StartingMinerals?.Count > 1 ? Globals.CurrentMapData.StartingMinerals[1]?.Count ?? 0 : -1)} start1Ordered={(Globals.CurrentMapData?.OrderedMainMinerals?.Count > 1 ? Globals.CurrentMapData.OrderedMainMinerals[1]?.Count ?? 0 : -1)}");

                    var currentStartIndex = mapData == null
                        ? -1
                        : GetApiLocAndCOM.ResolveCurrentSpawnIndex(gameInfo, mapData, observation);
                    Settings.CurrentSpawnIndex = currentStartIndex;
                    Globals.CurrentStartIndex = currentStartIndex;
                    Settings.CurrentSpawnLocation = mapData?.StartingTownHall != null
                        && currentStartIndex >= 0
                        && currentStartIndex < mapData.StartingTownHall.Length
                        && mapData.StartingTownHall[currentStartIndex] != null
                        ? mapData.StartingTownHall[currentStartIndex]
                        : new Vector2Dto();
                    Settings.CurrentSpawnCOM = mapData?.MineralCenterOfMass != null
                        && currentStartIndex >= 0
                        && currentStartIndex < mapData.MineralCenterOfMass.Count
                        && mapData.MineralCenterOfMass[currentStartIndex] != null
                        ? mapData.MineralCenterOfMass[currentStartIndex]
                        : new Vector2Dto();
                    Settings.CurrentBaseHasBeenPlayed = false;
                    Settings.MapDataLoaded = true;
                    Settings.SpawnDataLoaded = true;

                    if (ProvisionalExpansionService != null && mapData != null)
                    {
                        ProvisionalExpansionService.Initialize(mapData);
                    }

                    Console.WriteLine("BabySharkAI: InitialMapData.GetNewMiningData() completed successfully");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"BabySharkAI: Error calling InitialMapData.GetNewMiningData(): {ex.Message}");
                }
            }
            else
            {
                var mapData = Globals.CurrentMapData;
                _miningManager.SetCurrentMapData(mapData);
                if (mapData != null)
                {
                    GetApiLocAndCOM.LoadCurrentSettings(gameInfo, mapData, observation);
                    Globals.CurrentStartIndex = Settings.CurrentSpawnIndex;
                    if (mapData.M1IsFar != null && Settings.CurrentSpawnIndex >= 0 && Settings.CurrentSpawnIndex < mapData.M1IsFar.Length)
                    {
                        Settings.M1IsFar = mapData.M1IsFar;
                    }

                    if (mapData.M8IsFar != null && Settings.CurrentSpawnIndex >= 0 && Settings.CurrentSpawnIndex < mapData.M8IsFar.Length)
                    {
                        Settings.M8IsFar = mapData.M8IsFar;
                    }

                    Settings.CurrentBaseHasBeenPlayed = GetApiLocAndCOM.ResolveCurrentBaseHasBeenPlayed(mapData, Settings.CurrentSpawnIndex);
                    if (Settings.WorkerCount == 8) Settings.CurrentBaseHasBeenPlayed8 = Settings.CurrentBaseHasBeenPlayed;
                    else if (Settings.WorkerCount == 12) Settings.CurrentBaseHasBeenPlayed12 = Settings.CurrentBaseHasBeenPlayed;
                }
            }

            // PatternJIT bite 1: applied to the LIVE map data after BOTH the
            // dat-load path and the compute path converge
            // (Globals.CurrentMapData). The end-of-run dat save is skipped in
            // PatternJitMode (Program.cs) so the cached dat is never polluted
            // by the harness override.
            ApplyPatternJitSbStand(Globals.CurrentMapData);
            // Bite 4 (owner spec 2026-11-10 18:48): same live-data treatment
            // for SA's stand (S1's speed-mining target; --sasmh= walks it west
            // hunting the diagonal tipping point) + S3's derived wait spot.
            ApplyPatternJitSaStand(Globals.CurrentMapData);
            // Bite 5b (owner spec 2026-11-10 21:35): S1's angled bump line and
            // the shared meet-frame stand-thru point for the T2 push.
            ComputePatternJitT2BumpGeometry(Globals.CurrentMapData);

            // Telemetry log naming (owner spec 2026-10-23): resolve the NW/SE
            // start tag BEFORE any manager runs — the mining-test loggers
            // (worker_commands / worker_instruction_lists / mining_test csv /
            // frame0 dump) pick it up when they create their files, lazily on
            // first write, so the two spawns never share file names.
            Globals.currentMapStartLocation = GetApiLocAndCOM.ResolveStartLocationTag(gameInfo);
            Console.WriteLine($"BabySharkAI: Start location tag = '{Globals.currentMapStartLocation}'");

            foreach (var playerInfo in gameInfo.PlayerInfo)
            {
                if (playerInfo.PlayerId == playerId)
                {
                    _defaultBot.MacroData.Race = playerInfo.RaceActual;
                }
            }
            _defaultBot.MacroSetup.SetupMacro(_defaultBot.MacroData);

            foreach (var manager in Managers)
            {
                manager.OnStart(gameInfo, data, pingResponse, observation, playerId, opponentId);
            }

            // PatternJIT bite-4 correction (owner 2026-11-10 19:45: "do that
            // test correct... not mine from the wrong place"): the loop
            // chapters ride the self-pair row's HarvestA/ReturnPoint, which
            // the SmHarvestPoint patch above never touches — cycles 2+ mined
            // the dat's stale seats (S2 looped at (132.07,20.35) instead of
            // the locked (132.00,19.80) approach). PopulateJitPairReturnCalculations
            // runs inside BuildManager.OnStart (its reuse path keeps the dat
            // rows), so the self-pair rows are overridden HERE, after it.
            ApplyPatternJitPairRowStands(Globals.CurrentMapData);
        }

        // PatternJIT bite 1 (owner spec 2026-11-10 08:51, superseding
        // 2026-11-09 21:36): "0.9 West and 0.3 North of SB" = (132.10, 19.80).
        private static void ApplyPatternJitSbStand(MawBaseLocationData mapData)
        {
            if (!Settings.PatternJitMode || mapData == null)
            {
                return;
            }
            // Owner spec 2026-11-10 09:10: "Move it to 1u West and 0.3 North"
            // of SB center (133.0,19.5) = (132.00, 19.80).
            var sbSmhX = Settings.PatternJitSbSmhX >= 0f ? Settings.PatternJitSbSmhX : 132.00f;
            var sbSmhY = Settings.PatternJitSbSmhY >= 0f ? Settings.PatternJitSbSmhY : 19.80f;

            bool IsSb(float x, float y) => Math.Abs(x - 133.0f) < 0.05f && Math.Abs(y - 19.5f) < 0.05f;

            // AUTHORITIVE source: BuildRuntimeGreedyMinerals (BuildManager
            // OnStart) rebuilds every per-game OrderedMineral from
            // MainMineralCargoPoints[startIndex] — the fresh instances carry
            // cargoPoint.SmHarvestPoint. Patch THIS first so every downstream
            // rebuild (TeamPatchAssignments, AssignedWorkers MiningTargets)
            // inherits the new stand.
            foreach (var cargoList in mapData.MainMineralCargoPoints ?? new List<List<HarvestReturnCargoPointDto>>())
            {
                foreach (var cargoPoint in cargoList ?? new List<HarvestReturnCargoPointDto>())
                {
                    if (cargoPoint?.ResourcePosition != null && IsSb(cargoPoint.ResourcePosition.X, cargoPoint.ResourcePosition.Y))
                    {
                        cargoPoint.SmHarvestPoint = new Vector2Dto(sbSmhX, sbSmhY, cargoPoint.ResourcePosition.Z);
                        Console.WriteLine($"[PATTERN-JIT SB STAND CARGO] label={cargoPoint.Label} smH=({sbSmhX:F3},{sbSmhY:F3})");
                    }
                }
            }

            // Choreo mineral records (TeamPatchAssignments).
            foreach (var assignmentList in mapData.TeamPatchAssignments ?? new List<List<TeamPatchAssignmentDto>>())
            {
                foreach (var assignment in assignmentList ?? new List<TeamPatchAssignmentDto>())
                {
                    foreach (var mineral in assignment?.Minerals ?? new List<OrderedMineral>())
                    {
                        if (mineral?.Position != null && IsSb(mineral.Position.X, mineral.Position.Y))
                        {
                            mineral.SmHarvestPoint = new Vector2Dto(sbSmhX, sbSmhY, mineral.Position.Z);
                            Console.WriteLine($"[PATTERN-JIT SB STAND] mineral=({mineral.Position.X:F2},{mineral.Position.Y:F2}) label={mineral.FinalLabel} smH=({sbSmhX:F3},{sbSmhY:F3})");
                        }
                    }
                }
            }

            // OrderedMainMinerals (per-spawn lists — separate OrderedMineral
            // instances from TeamPatchAssignments; viz circles and base mining
            // init read these).
            foreach (var mineralList in mapData.OrderedMainMinerals ?? new List<List<OrderedMineral>>())
            {
                foreach (var mineral in mineralList ?? new List<OrderedMineral>())
                {
                    if (mineral?.Position != null && IsSb(mineral.Position.X, mineral.Position.Y))
                    {
                        mineral.SmHarvestPoint = new Vector2Dto(sbSmhX, sbSmhY, mineral.Position.Z);
                        Console.WriteLine($"[PATTERN-JIT SB STAND OMM] mineral=({mineral.Position.X:F2},{mineral.Position.Y:F2}) label={mineral.FinalLabel} smH=({sbSmhX:F3},{sbSmhY:F3})");
                    }
                }
            }

            // Persisted AssignedWorkers MiningTargets — SNAPSHOT CLONES baked
            // into the dat at build time (CloneMiningTarget copies
            // SmHarvestPoint), so the per-frame point resolver's MiningTargets
            // fallback still sees the OLD SMH unless these are patched too.
            foreach (var workerList in mapData.AssignedWorkers ?? new List<List<AssignedWorkerDto>>())
            {
                foreach (var worker in workerList ?? new List<AssignedWorkerDto>())
                {
                    foreach (var target in worker?.MiningTargets ?? new List<MiningTargetDto>())
                    {
                        if (target?.ResourcePosition != null && IsSb(target.ResourcePosition.X, target.ResourcePosition.Y))
                        {
                            target.SmHarvestPoint = new Vector2Dto(sbSmhX, sbSmhY, target.ResourcePosition.Z);
                            Console.WriteLine($"[PATTERN-JIT SB STAND TARGET] worker={worker.UnitID} role={worker.Role} label={target.ResourceLabel} smH=({sbSmhX:F3},{sbSmhY:F3})");
                        }
                    }
                }
            }
        }

        // PatternJIT bite 4 (owner spec 2026-11-10 18:48): "have S1 and S3
        // trying new West angles on SA at the same time... find the tipping
        // point where it falls over to the west Diagonal." Mirror of the SB
        // patch: SA's SmHarvestPoint is overridden in the same four live
        // locations (the end-of-run dat save stays skipped in PatternJitMode,
        // so the cached dat is never polluted). Default = S1's measured
        // natural stand (129.85,19.37); --sasmh=X,Y walks it west. Also
        // derives S3's default wait spot: 2.0u from the stand toward the
        // hatchery (SE harness hatchery (127.5,24.5)) — released by the
        // 32-accumulated-harvest-frames gate, the ~13-frame walk-in lands S3
        // on SA right as S1 flips. --sasawait=X,Y overrides.
        private static void ApplyPatternJitSaStand(MawBaseLocationData mapData)
        {
            if (!Settings.PatternJitMode || mapData == null)
            {
                return;
            }
            var saSmhX = Settings.PatternJitSaSmhX >= 0f ? Settings.PatternJitSaSmhX : 129.85f;
            var saSmhY = Settings.PatternJitSaSmhY >= 0f ? Settings.PatternJitSaSmhY : 19.37f;

            // S3 wait-spot default: 2.0u from the stand toward the hatchery.
            if (Settings.PatternJitSa3WaitX < 0f || Settings.PatternJitSa3WaitY < 0f)
            {
                const float hatcheryX = 127.5f;
                const float hatcheryY = 24.5f;
                var dx = hatcheryX - saSmhX;
                var dy = hatcheryY - saSmhY;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 0.0001)
                {
                    Settings.PatternJitSa3WaitX = saSmhX + (float)(dx / length * 2.0);
                    Settings.PatternJitSa3WaitY = saSmhY + (float)(dy / length * 2.0);
                    Console.WriteLine($"[PATTERN-JIT SA WAIT] derived S3 wait=({Settings.PatternJitSa3WaitX:F3},{Settings.PatternJitSa3WaitY:F3}) (2.0u from stand toward hatchery)");
                }
            }

            bool IsSa(float x, float y) => Math.Abs(x - 130.0f) < 0.05f && Math.Abs(y - 18.5f) < 0.05f;

            foreach (var cargoList in mapData.MainMineralCargoPoints ?? new List<List<HarvestReturnCargoPointDto>>())
            {
                foreach (var cargoPoint in cargoList ?? new List<HarvestReturnCargoPointDto>())
                {
                    if (cargoPoint?.ResourcePosition != null && IsSa(cargoPoint.ResourcePosition.X, cargoPoint.ResourcePosition.Y))
                    {
                        cargoPoint.SmHarvestPoint = new Vector2Dto(saSmhX, saSmhY, cargoPoint.ResourcePosition.Z);
                        Console.WriteLine($"[PATTERN-JIT SA STAND CARGO] label={cargoPoint.Label} smH=({saSmhX:F3},{saSmhY:F3})");
                    }
                }
            }

            foreach (var assignmentList in mapData.TeamPatchAssignments ?? new List<List<TeamPatchAssignmentDto>>())
            {
                foreach (var assignment in assignmentList ?? new List<TeamPatchAssignmentDto>())
                {
                    foreach (var mineral in assignment?.Minerals ?? new List<OrderedMineral>())
                    {
                        if (mineral?.Position != null && IsSa(mineral.Position.X, mineral.Position.Y))
                        {
                            mineral.SmHarvestPoint = new Vector2Dto(saSmhX, saSmhY, mineral.Position.Z);
                            Console.WriteLine($"[PATTERN-JIT SA STAND] mineral=({mineral.Position.X:F2},{mineral.Position.Y:F2}) label={mineral.FinalLabel} smH=({saSmhX:F3},{saSmhY:F3})");
                        }
                    }
                }
            }

            foreach (var mineralList in mapData.OrderedMainMinerals ?? new List<List<OrderedMineral>>())
            {
                foreach (var mineral in mineralList ?? new List<OrderedMineral>())
                {
                    if (mineral?.Position != null && IsSa(mineral.Position.X, mineral.Position.Y))
                    {
                        mineral.SmHarvestPoint = new Vector2Dto(saSmhX, saSmhY, mineral.Position.Z);
                        Console.WriteLine($"[PATTERN-JIT SA STAND OMM] mineral=({mineral.Position.X:F2},{mineral.Position.Y:F2}) label={mineral.FinalLabel} smH=({saSmhX:F3},{saSmhY:F3})");
                    }
                }
            }

            foreach (var workerList in mapData.AssignedWorkers ?? new List<List<AssignedWorkerDto>>())
            {
                foreach (var worker in workerList ?? new List<AssignedWorkerDto>())
                {
                    foreach (var target in worker?.MiningTargets ?? new List<MiningTargetDto>())
                    {
                        if (target?.ResourcePosition != null && IsSa(target.ResourcePosition.X, target.ResourcePosition.Y))
                        {
                            target.SmHarvestPoint = new Vector2Dto(saSmhX, saSmhY, target.ResourcePosition.Z);
                            Console.WriteLine($"[PATTERN-JIT SA STAND TARGET] worker={worker.UnitID} role={worker.Role} label={target.ResourceLabel} smH=({saSmhX:F3},{saSmhY:F3})");
                        }
                    }
                }
            }
        }

        // PatternJIT bite-4 correction (owner 2026-11-10 19:45: "do that test
        // correct... we are not doing all this testing so that you can ignore
        // it and mine from the wrong place"). The loop chapters (cycle 2+)
        // resolve their Move/Return targets from the SELF-PAIR row
        // ({SA,SA} / {SB,SB}) in MainJitPairReturnCalculations — a separate
        // store the SmHarvestPoint patches never touch, so loop harvests kept
        // seating at the dat's stale points. Called AFTER the manager OnStart
        // loop because PopulateJitPairReturnCalculations (BuildManager.OnStart)
        // rewrites the pair list and its reuse path keeps stored rows.
        //   HarvestA/B = the locked stand (the exact f0 Move target, so the
        //     loop seats exactly where the proven first trip seated);
        //   ReturnPoint = 2.85u from the hatchery toward the stand — the
        //     SMR-off-SMH rule (owner 2026-11-08 11:30: "SMR points should
        //     have been calculated off the SMH"), same formula/radius as
        //     PopulateJitPairReturnCalculations.
        private static void ApplyPatternJitPairRowStands(MawBaseLocationData mapData)
        {
            if (!Settings.PatternJitMode || mapData == null)
            {
                return;
            }
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var rows = mapData.MainJitPairReturnCalculations?.ElementAtOrDefault(startIndex);
            var townhall = mapData.StartingTownHall?.ElementAtOrDefault(startIndex);
            if (rows == null || townhall == null)
            {
                return;
            }
            const float hatcheryReturnRadius = 2.85f; // 2.75 footprint + 0.1 (owner request; PopulateJitPairReturnCalculations)

            var sbStandX = Settings.PatternJitSbSmhX >= 0f ? Settings.PatternJitSbSmhX : 132.00f;
            var sbStandY = Settings.PatternJitSbSmhY >= 0f ? Settings.PatternJitSbSmhY : 19.80f;
            var saStandX = Settings.PatternJitSaSmhX >= 0f ? Settings.PatternJitSaSmhX : 129.85f;
            var saStandY = Settings.PatternJitSaSmhY >= 0f ? Settings.PatternJitSaSmhY : 19.37f;
            PatchSelfPairRow(rows, townhall, "SB", sbStandX, sbStandY);
            PatchSelfPairRow(rows, townhall, "SA", saStandX, saStandY);

            // Bite 5c juggler (owner spec 2026-11-10 21:00 + the SMR-off-SMH
            // rule): the {SA,SB} cross row carries the locked stands and a
            // return point computed from the AVERAGE of the two stands —
            // "Average ... with SB SMH ... calculate a line from the hatchery
            // to the average ... intersecting the foot print return circle"
            // (owner spec 2026-11-10 17:35).
            if (Settings.PatternJitJuggler)
            {
                foreach (var row in rows)
                {
                    if (row == null
                        || !string.Equals(row.FromResourceLabel, "SA", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(row.ToResourceLabel, "SB", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var avgX = (saStandX + sbStandX) / 2f;
                    var avgY = (saStandY + sbStandY) / 2f;
                    var dx = avgX - townhall.X;
                    var dy = avgY - townhall.Y;
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    if (length < 0.0001)
                    {
                        continue;
                    }
                    var returnZ = row.ReturnPoint?.Z ?? townhall.Z;
                    var harvestZ = row.HarvestA?.Z ?? 0f;
                    row.HarvestA = new Vector2Dto(saStandX, saStandY, harvestZ);
                    row.HarvestB = new Vector2Dto(sbStandX, sbStandY, harvestZ);
                    row.ReturnPoint = new Vector2Dto(
                        townhall.X + (float)(dx / length) * hatcheryReturnRadius,
                        townhall.Y + (float)(dy / length) * hatcheryReturnRadius,
                        returnZ);
                    Console.WriteLine($"[PATTERN-JIT SA-SB PAIR ROW] key={row.PairKey} harvestA=({saStandX:F3},{saStandY:F3}) harvestB=({sbStandX:F3},{sbStandY:F3}) return=({row.ReturnPoint.X:F3},{row.ReturnPoint.Y:F3})");
                }
            }

            static void PatchSelfPairRow(List<JitPairReturnCalculationDto> rows, Vector2Dto townhall, string label, float standX, float standY)
            {
                foreach (var row in rows)
                {
                    if (row == null
                        || !string.Equals(row.FromResourceLabel, label, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(row.ToResourceLabel, label, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var dx = standX - townhall.X;
                    var dy = standY - townhall.Y;
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    if (length < 0.0001)
                    {
                        continue;
                    }
                    var returnZ = row.ReturnPoint?.Z ?? townhall.Z;
                    var harvestZ = row.HarvestA?.Z ?? 0f;
                    row.HarvestA = new Vector2Dto(standX, standY, harvestZ);
                    row.HarvestB = new Vector2Dto(standX, standY, harvestZ);
                    row.ReturnPoint = new Vector2Dto(
                        townhall.X + (float)(dx / length) * hatcheryReturnRadius,
                        townhall.Y + (float)(dy / length) * hatcheryReturnRadius,
                        returnZ);
                    Console.WriteLine($"[PATTERN-JIT {label} PAIR ROW] key={row.PairKey} harvestA/B=({standX:F3},{standY:F3}) return=({row.ReturnPoint.X:F3},{row.ReturnPoint.Y:F3})");
                }
            }
        }

        // PatternJIT bite 5b (owner spec 2026-11-10 21:00/21:35): geometry for
        // the T2->S1 bump. T2 mineral-walks toward SB on a measured line
        // (telemetry run worker_commands_BabyShark_SE_20261110214641.jsonl);
        // S1 starts on an angle so both arrive at the meeting area on the meet
        // frame with S1 ~0.6u ahead on the line to the SA stand; at the meet
        // frame both Move to the SAME stand-thru point (T2's push), T2 breaks
        // to TB --t2push=N frames later.
        //   S1's f0 Move target  = 2.0u past the meeting point on the line
        //     from S1's start (hatchery + (2.0,-3.0) at this spawn) — S1
        //     passes the meeting point at speed, never decelerating into it.
        //   Stand-thru target    = 2.0u past the SA stand on the line from the
        //     meeting point (the shared meet-frame Move target).
        private static void ComputePatternJitT2BumpGeometry(MawBaseLocationData mapData)
        {
            if (!Settings.PatternJitMode || !Settings.PatternJitT2Bump || mapData == null)
            {
                return;
            }
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var townhall = mapData.StartingTownHall?.ElementAtOrDefault(startIndex);
            if (townhall == null)
            {
                return;
            }
            var saSmhX = Settings.PatternJitSaSmhX >= 0f ? Settings.PatternJitSaSmhX : 129.85f;
            var saSmhY = Settings.PatternJitSaSmhY >= 0f ? Settings.PatternJitSaSmhY : 19.37f;

            // T2's measured mineral-walk line toward SB (seed 3 SE), frames
            // 10-20 — the useful bump window (full speed ~f19).
            var t2Line = new (int Frame, float X, float Y)[]
            {
                (10, 128.983f, 21.285f), (11, 129.080f, 21.242f), (12, 129.186f, 21.195f),
                (13, 129.300f, 21.145f), (14, 129.423f, 21.090f), (15, 129.555f, 21.031f),
                (16, 129.695f, 20.969f), (17, 129.845f, 20.902f), (18, 130.003f, 20.832f),
                (19, 130.163f, 20.761f), (20, 130.324f, 20.689f),
            };
            var meet = Array.Find(t2Line, p => p.Frame == Settings.PatternJitT2MeetFrame);
            if (meet.Frame == 0)
            {
                Console.WriteLine($"[PATTERN-JIT T2 BUMP] REJECTED — meet frame {Settings.PatternJitT2MeetFrame} outside the measured f10-f20 line");
                return;
            }

            // Meeting point: T2 at meet.X/meet.Y; S1 0.6u ahead on the line
            // to the stand.
            var toStandX = saSmhX - meet.X;
            var toStandY = saSmhY - meet.Y;
            var toStandLen = Math.Sqrt(toStandX * toStandX + toStandY * toStandY);
            if (toStandLen < 0.0001)
            {
                return;
            }
            var meetS1X = (float)(meet.X + toStandX / toStandLen * 0.6);
            var meetS1Y = (float)(meet.Y + toStandY / toStandLen * 0.6);

            // S1's f0 angled thru target: 2.0u past the meeting point on the
            // line from S1's start (hatchery + (2.0,-3.0) at this spawn).
            var s1StartX = townhall.X + 2.0f;
            var s1StartY = townhall.Y - 3.0f;
            var s1LegX = meetS1X - s1StartX;
            var s1LegY = meetS1Y - s1StartY;
            var s1LegLen = Math.Sqrt(s1LegX * s1LegX + s1LegY * s1LegY);
            if (s1LegLen < 0.0001)
            {
                return;
            }
            Settings.PatternJitS1BumpThruX = (float)(meetS1X + s1LegX / s1LegLen * 2.0);
            Settings.PatternJitS1BumpThruY = (float)(meetS1Y + s1LegY / s1LegLen * 2.0);

            // Shared meet-frame target: 2.0u past the stand on the line from
            // the meeting point.
            var mtStandX = saSmhX - meetS1X;
            var mtStandY = saSmhY - meetS1Y;
            var mtStandLen = Math.Sqrt(mtStandX * mtStandX + mtStandY * mtStandY);
            Settings.PatternJitSaStandThruX = (float)(saSmhX + mtStandX / mtStandLen * 2.0);
            Settings.PatternJitSaStandThruY = (float)(saSmhY + mtStandY / mtStandLen * 2.0);

            Console.WriteLine($"[PATTERN-JIT T2 BUMP] meet f{Settings.PatternJitT2MeetFrame} T2=({meet.X:F3},{meet.Y:F3}) S1-meet=({meetS1X:F3},{meetS1Y:F3}) s1-thru=({Settings.PatternJitS1BumpThruX:F3},{Settings.PatternJitS1BumpThruY:F3}) stand-thru=({Settings.PatternJitSaStandThruX:F3},{Settings.PatternJitSaStandThruY:F3}) push={Settings.PatternJitT2PushFrames}f");
        }

        /// <summary>
        /// Get the underlying DefaultSharkyBot for access to non-essential managers if needed.
        /// </summary>
        public DefaultSharkyBot GetUnderlyingBot()
        {
            return _defaultBot;
        }

        private sealed class StartupAwareSharkyBot : ISharkyBot
        {
            private readonly BabySharkAI _owner;

            public StartupAwareSharkyBot(BabySharkAI owner)
            {
                _owner = owner;
            }

            public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
            {
                // Hydrate MacroData so builds can read Minerals/Supply/Frame
                MacroDataUpdater.UpdateFromObservation(observation, _owner._defaultBot.MacroData);

                if (_owner.Managers == null)
                {
                    return Array.Empty<SC2APIProtocol.Action>();
                }

                var actions = new List<SC2APIProtocol.Action>();
                var begin = System.Diagnostics.Stopwatch.GetTimestamp();

                try
                {
                    foreach (var manager in _owner.Managers.ToList())
                    {
                        if (observation?.Observation != null && observation.Observation.GameLoop % 25 == 0)
                        {
                            Console.WriteLine($"[MANAGER LOOP] frame={observation.Observation.GameLoop} manager={manager.GetType().Name} skip={manager.SkipFrame} neverSkip={manager.NeverSkip}");
                        }

                        if (!manager.NeverSkip && manager.SkipFrame)
                        {
                            manager.SkipFrame = false;
                            continue;
                        }

                        var beginManager = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (manager is BabySharkBuildManager)
                        {
                            Console.WriteLine($"[BUILD DISPATCH] frame={observation?.Observation?.GameLoop} manager={manager.GetType().Name}");
                            if (System.Diagnostics.Debugger.IsAttached)
                            {
                                //System.Diagnostics.Debugger.Break();
                            }
                        }
                        try
                        {
                            var mgrActions = manager.OnFrame(observation);
                            if (manager is ObservationManager)
                            {
                                var observedWorkers = Globals.CurrentObservation?.SelfUnits?.Values
                                    .Where(worker => worker != null && worker.UnitType == (uint)UnitTypes.ZERG_DRONE)
                                    .ToList();
                                _owner._workerCommandTelemetry.LogObservedOrderAbilityChanges(
                                    observation,
                                    observedWorkers,
                                    manager.GetType().Name);
                            }
                            if (manager is BabySharkMiningManager)
                            {
                                var observedWorkers = Globals.CurrentObservation?.SelfUnits?.Values
                                    .Where(worker => worker != null && worker.UnitType == (uint)UnitTypes.ZERG_DRONE)
                                    .ToList();
                                var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                                var assignedWorkers = Globals.CurrentMapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                                    ?? new List<AssignedWorkerDto>();
                                _owner._workerCommandTelemetry.LogWorkerAbilityChanges(observation, observedWorkers, manager.GetType().Name);
                                _owner._workerCommandTelemetry.LogWorkerObservations(observation, observedWorkers, assignedWorkers, manager.GetType().Name);
                            }
                            if (mgrActions != null)
                            {
                                actions.AddRange(mgrActions);
                                foreach (var action in mgrActions)
                                {
                                    _owner._workerCommandTelemetry.LogAction(action, observation, manager.GetType().Name);
                                    if (action?.ActionRaw?.UnitCommand?.UnitTags != null)
                                    {
                                        foreach (var tag in action.ActionRaw.UnitCommand.UnitTags)
                                        {
                                            if (!observation.Observation.RawData.Units.Any(u => u.Tag == tag))
                                            {
                                                Console.WriteLine($"{observation.Observation.GameLoop} {manager.GetType().Name}, order {action.ActionRaw.UnitCommand.AbilityId}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            _owner.GetUnderlyingBot().TagService.TagException();
                            Console.WriteLine(exception.ToString());
                        }

                        var endManager = System.Diagnostics.Stopwatch.GetTimestamp();
                        var managerTime = (endManager - beginManager) / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
                        manager.TotalFrameTime += managerTime;

                        if (managerTime > 1 && observation.Observation.GameLoop > 100)
                        {
                            manager.SkipFrame = true;

                            if (_owner.SharkyOptions.LogPerformance && managerTime > manager.LongestFrame)
                            {
                                manager.LongestFrame = managerTime;
                                Console.WriteLine($"{observation.Observation.GameLoop} {manager.GetType().Name} {managerTime:F2}ms, average: {(manager.TotalFrameTime / observation.Observation.GameLoop):F2}ms");
                            }
                        }
                    }

                    var end = System.Diagnostics.Stopwatch.GetTimestamp();
                    var endTime = (end - begin) / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
                    _owner._defaultBot.PerformanceData.TotalFrameCalculationTime += endTime;

                    // After managers populate debug/spawn requests, send them to the game connection
                    var debugService = _owner._defaultBot.DebugService;
                    if (debugService != null && _owner._gameConnection != null)
                    {
                        try
                        {
                            var drawReq = debugService.DrawRequest;
                            var spawnReq = debugService.SpawnRequest;

                            var hasDraws = drawReq != null && drawReq.Debug != null && drawReq.Debug.Debug != null && drawReq.Debug.Debug.Count > 0;
                            var hasSpawns = spawnReq != null && spawnReq.Debug != null && spawnReq.Debug.Debug != null && spawnReq.Debug.Debug.Count > 0;

                            if (hasDraws)
                            {
                                try
                                {
                                    var draw = drawReq.Debug.Debug[0].Draw;
                                    var textCount = draw?.Text?.Count ?? 0;
                                    var lineCount = draw?.Lines?.Count ?? 0;
                                    var sphereCount = draw?.Spheres?.Count ?? 0;
                                    Console.WriteLine($"StartupAwareSharkyBot: sending DrawRequest with text={textCount}, lines={lineCount}, spheres={sphereCount}");
                                }
                                catch
                                {
                                    Console.WriteLine("StartupAwareSharkyBot: sending DrawRequest (counts unavailable)");
                                }
                                _owner._gameConnection.SendRequest(drawReq).GetAwaiter().GetResult();
                                Console.WriteLine("StartupAwareSharkyBot: DrawRequest sent");
                                debugService.ResetDrawRequest();
                                     // System.Diagnostics.Debugger.Break();
                            }

                            if (hasSpawns)
                            {
                                try
                                {
                                    var spawnCount = spawnReq.Debug.Debug.Count;
                                    Console.WriteLine($"StartupAwareSharkyBot: sending SpawnRequest with commands={spawnCount}");
                                }
                                catch
                                {
                                    Console.WriteLine("StartupAwareSharkyBot: sending SpawnRequest (counts unavailable)");
                                }
                                _owner._gameConnection.SendRequest(spawnReq).GetAwaiter().GetResult();
                                Console.WriteLine("StartupAwareSharkyBot: SpawnRequest sent");
                                debugService.ResetSpawnRequest();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"StartupAwareSharkyBot: failed to send debug/spawn requests: {ex.Message}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception.ToString());
                }

                // B3 Macro Hatchery test freeze (owner spec 2026-10-16 18:35):
                // once the build milestone resolves, strip every manager action
                // so the scene stays inspectable. Debug draws never ride this
                // list — they go through the separate DrawRequest sent above —
                // so the banner and circles keep rendering while frozen.
                if (Settings.MacroHatcheryTestFrozen)
                {
                    actions.Clear();
                }

                return actions;
            }

            public void OnEnd(ResponseObservation observation, Result result)
            {
            }

            public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
            {
                _owner.OnStart(gameInfo, data, pingResponse, observation, playerId, opponentId);
            }
        }

        /// <summary>
        /// Set build choices for a specific race.
        /// </summary>
        public void SetBuildChoices(Race race, BuildChoices buildChoices)
        {
            _defaultBot.BuildChoices[race] = buildChoices;
        }

        /// <summary>
        /// Access build choices dictionary for configuration.
        /// </summary>
        public Dictionary<Race, BuildChoices> BuildChoices
        {
            get { return _defaultBot.BuildChoices; }
        }

        /// <summary>
        /// Access MicroTaskData for managing micro tasks.
        /// </summary>
        public MicroTaskData MicroTaskData
        {
            get { return _defaultBot.MicroTaskData; }
        }
    }
}
