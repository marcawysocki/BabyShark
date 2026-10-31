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
