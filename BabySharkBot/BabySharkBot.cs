using Sharky;
using Sharky.Builds;
using Sharky.DefaultBot;
using Sharky.Managers;
using Sharky.MicroTasks;
using Sharky.MicroTasks.Mining;
using SC2APIProtocol;
using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Initialize BabySharkAI with a GameConnection instance.
        /// The actual Sharky managers are loaded through a proxy DefaultSharkyBot.
        /// </summary>
        public BabySharkAI(GameConnection gameConnection)
        {
            Instance = this;
            _gameConnection = gameConnection;
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

            // Essential managers - DebugManager is required for the first 15 frames
            DebugManager = _defaultBot.DebugManager;
            Managers.Add(DebugManager);

            // Essential Sharky managers for data and state tracking
            Managers.Add(_defaultBot.UnitDataManager);
            Managers.Add(_defaultBot.MapManager);
            Managers.Add(_defaultBot.EnemyRaceManager);
            Managers.Add(_defaultBot.BaseManager);
            Managers.Add(_defaultBot.TargetingManager);

            // Essential services
            DebugService = _defaultBot.DebugService;

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

            // Initialize CCA service early to share across managers
            var ccaService = new BabySharkBot.Services.chrisCrossAppleSause();

            // Create build manager
            var buildManager = new BabySharkBuildManager(_defaultBot);
            // Optionally set an initial build:
            buildManager.SetBuild(new BabySharkBot.Builds.BuildIne(_defaultBot));

            // Register only the necessary microtasks
            RegisterRequiredMicroTasks();
            InstallRlMicroControllerWrappers();

            // Replace Sharky's unit observation manager with BabyShark's copy
            var babySharkUnitManager = new BabySharkUnitManager(_defaultBot.ActiveUnitData, _defaultBot.SharkyUnitData, _defaultBot.BaseData, _defaultBot.EnemyData, _defaultBot.SharkyOptions, _defaultBot.TargetPriorityService, _defaultBot.CollisionCalculator, _defaultBot.MapDataService, _defaultBot.DebugService, _defaultBot.DamageService, _defaultBot.UnitDataService, _defaultBot.TargetingData);
            Managers.Add(babySharkUnitManager);

            // Create BabySharkMiningManager with shared CCA service instance
            _miningManager = new BabySharkMiningManager(workerLabelService, crosshairService, mineralLabelService, vespeneLabelService, expansionCOMService, expansionPointService, expansionPointDrawService, provisionalExpansionService, MineralReturnRateTrackerService, FrameToTimeConverter, mapDataService, SpawningPoolPlacementService, ccaService);
            Console.WriteLine("BabySharkAI: Created BabySharkMiningManager with shared CCA service instance");
            Managers.Add(_miningManager);

            // FIX: DrawOnlyManager ensures debug labels are sent every frame,
            // even when BabySharkMiningManager is skipped for performance.
            var drawOnlyManager = new DrawOnlyManager(_miningManager);
            Managers.Add(drawOnlyManager);
            Console.WriteLine("BabySharkAI: Registered DrawOnlyManager for persistent debug drawing");

            // Create and register CCA manager to run bump/order logic in the manager lifecycle.
            try
            {
                var ccaManager = new CcaManager(ccaService, _miningManager, buildManager);
                Managers.Add(ccaManager);
                Console.WriteLine($"BabySharkAI: Registered CcaManager. Total managers: {Managers.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkAI: Failed to register CcaManager: {ex.Message}");
            }

            Console.WriteLine("BabySharkAI initialized with essential BabyShark managers");
        }

        public CcaManager CcaManager => Managers.OfType<CcaManager>().FirstOrDefault();
        public chrisCrossAppleSause CcaMiningService => CcaManager?.CcaMiningService;

        /// <summary>
        /// Register only the necessary microtasks for BabyShark operations.
        /// This method populates MicroTaskData from a blank slate.
        /// </summary>
        private void RegisterRequiredMicroTasks()
        {
            try
            {
                var microTaskData = _defaultBot.MicroTaskData;
                
                // 1. Mining Tasks
                var mineralMiner = new MineralMiner(_defaultBot);
                var gasMiner = new GasMiner(_defaultBot);
                var miningDefenseService = new MiningDefenseService(_defaultBot, null);

                // CustomMiningTask overrides debug drawing to prevent Sharky's default labels
                var customMiningTask = new CustomMiningTask(
                    _defaultBot,
                    priority: 8,
                    miningDefenseService: miningDefenseService,
                    mineralMiner: mineralMiner,
                    gasMiner: gasMiner
                );
                microTaskData["MiningTask"] = customMiningTask;

                // TeamPatchMiningTask implements the team-based mining logic
                var teamPatchTask = new TeamPatchMiningTask(_defaultBot, priority: 8, miningDefenseService, mineralMiner, gasMiner, _miningManager);
                microTaskData[teamPatchTask.GetType().Name] = teamPatchTask;

                // 2. Scouting Tasks
                var babySharkOverlordScoutTask = new BabySharkOverlordScoutTask(_defaultBot, true, 0.9f, ProvisionalExpansionService, ExpansionCOMService);
                microTaskData["OverlordScoutTask"] = babySharkOverlordScoutTask;

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

                    var mapData = initialMapData.GetNewMiningData(gameInfo, data, observation, null, _miningManager.WorkerLabelService, _miningManager.CrosshairService, _miningManager.MineralLabelService, _miningManager.VespeneLabelService, ExpansionCOMService, _miningManager.ExpansionPointService, _miningManager.ExpansionPointDrawService, ProvisionalExpansionService, _defaultBot.MapDataService);
                    _miningManager.SetCurrentMapData(mapData);
                    Globals.CurrentMapData = mapData;
                    Settings.CurrentSpawnIndex = 0;
                    Globals.CurrentStartIndex = 0;
                    Settings.CurrentSpawnLocation = mapData?.StartingTownHall != null && mapData.StartingTownHall.Length > 0 && mapData.StartingTownHall[0] != null
                        ? mapData.StartingTownHall[0]
                        : new Vector2Dto();
                    Settings.CurrentSpawnCOM = mapData?.MineralCenterOfMass != null && mapData.MineralCenterOfMass.Count > 0 && mapData.MineralCenterOfMass[0] != null
                        ? mapData.MineralCenterOfMass[0]
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
                    GetApiLocAndCOM.LoadCurrentSettings(gameInfo, mapData);
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

            //System.Diagnostics.Debugger.Break();
            var workerEntries = ProcessVisableUnits.ProcessVisibleUnits(observation, _miningManager.WorkerLabelService, _miningManager.MineralLabelService, _miningManager.VespeneLabelService, SpawningPoolPlacementService);
            Settings.ccaMining = true;
            if (_miningManager.CurrentMapData != null)
            {
                var currentStartIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                _miningManager.CcaMiningService.EnableCcaMiningForCurrentSpawn(_miningManager.CurrentMapData, currentStartIndex);

                // Build a live worker list from the current observation and pass frame/workers so the cca service can run immediately.
                var frame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
                
                var currentAssignments = OngoingMapData.ResolveTeamAssignments(_miningManager.CurrentMapData, currentStartIndex);
                _miningManager.CcaMiningService.RecordSpawnObservation(_miningManager.CurrentMapData, currentStartIndex, currentAssignments, _miningManager.WorkerLabelService, frame: frame, workerEntries: workerEntries, mineralLabelService: _miningManager.MineralLabelService);
            }

            _miningManager.ProcessFrameObservation(observation);

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

        // Ensure the mining manager and a MicroManager are registered with the manager list.
        // This can be called by external services when they need microtasks or mining manager functionality at runtime.
        public bool EnsureManagersRegistered()
        {
            try
            {
                try
                {
                    System.Diagnostics.Debugger.Break();
                }
                catch { }

                Console.WriteLine("BabySharkAI.EnsureManagersRegistered: called");
                var miningPresent = Managers.Contains(_miningManager);
                var miningAdded = false;
                if (_miningManager != null && !miningPresent)
                {
                    Managers.Add(_miningManager);
                    miningAdded = true;
                    Console.WriteLine("BabySharkAI: Ensured BabySharkMiningManager registered as part of takeover.");
                    miningPresent = true;
                }

                var microPresent = Managers.Any(m => m.GetType().Name == "MicroManager");
                var microAdded = false;
                if (!microPresent)
                {
                    try
                    {
                        var microMgr = new Sharky.Managers.MicroManager(_defaultBot.ActiveUnitData, _defaultBot.MicroTaskData, _defaultBot.SharkyOptions, _defaultBot.DebugService);
                        Managers.Add(microMgr);
                        microAdded = true;
                        Console.WriteLine("BabySharkAI: Added MicroManager into Managers");
                        microPresent = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"BabySharkAI: Failed to add MicroManager: {ex.Message}");
                    }
                }

                var success = miningPresent && microPresent;
                Settings.MiningManagerStarted = success;
                if (!success)
                {
                    Console.WriteLine($"BabySharkAI: EnsureManagersRegistered partial success: miningPresent={miningPresent}, microPresent={microPresent}");
                }
                return success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkAI.EnsureManagersRegistered failed: {ex.Message}");
                return false;
            }
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

                // Break every 5th frame BEFORE any processing, so the previous frame's
                // debug labels are still visible on screen.
                if (observation.Observation.GameLoop % 5 == 0)
                {
                    //System.Diagnostics.Debugger.Break();
                }

                if (observation.Observation.GameLoop % 100 == 0)
                {
                    Console.WriteLine($"StartupAwareSharkyBot.OnFrame: frame={observation.Observation.GameLoop} managers={_owner.Managers.Count}");
                    foreach (var m in _owner.Managers)
                    {
                        Console.WriteLine($"  Manager: {m.GetType().Name}");
                    }
                }
                // Ensure requested managers are present before running the manager loop
                try
                {
                    if (Settings.MiningManagerStarted)
                    {
                        if (_owner._miningManager != null && !_owner.Managers.Contains(_owner._miningManager))
                        {
                            _owner.Managers.Add(_owner._miningManager);
                            Console.WriteLine("StartupAwareSharkyBot: Re-registered BabySharkMiningManager into Managers");
                        }

                        if (!_owner.Managers.Any(m => m.GetType().Name == "MicroManager"))
                        {
                            try
                            {
                                var microMgr = new Sharky.Managers.MicroManager(_owner._defaultBot.ActiveUnitData, _owner._defaultBot.MicroTaskData, _owner._defaultBot.SharkyOptions, _owner._defaultBot.DebugService);
                                _owner.Managers.Add(microMgr);
                                Console.WriteLine("StartupAwareSharkyBot: Added MicroManager into Managers");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"StartupAwareSharkyBot: Failed to add MicroManager: {ex.Message}");
                            }
                        }
                    }
                }
                catch
                {
                }

                if (_owner.Managers == null)
                {
                    return Array.Empty<SC2APIProtocol.Action>();
                }

                var actions = new List<SC2APIProtocol.Action>();
                var begin = System.Diagnostics.Stopwatch.GetTimestamp();

                try
                {
                    foreach (var manager in _owner.Managers)
                    {
                        if (!manager.NeverSkip && manager.SkipFrame)
                        {
                            manager.SkipFrame = false;
                            continue;
                        }

                        var beginManager = System.Diagnostics.Stopwatch.GetTimestamp();
                        try
                        {
                            var mgrActions = manager.OnFrame(observation);
                            if (mgrActions != null)
                            {
                                actions.AddRange(mgrActions);
                                foreach (var action in mgrActions)
                                {
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
