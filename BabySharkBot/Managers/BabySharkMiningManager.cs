using SC2APIProtocol;
using Sharky;
using Sharky.Extensions;
using Sharky.Managers;
using Sharky.Pathing;
using BabySharkBot.Setup;
using BabySharkBot.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SC2Action = SC2APIProtocol.Action;

#nullable enable

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Custom mining manager for BabyShark that replaces Sharky's default mining manager.
    /// Orchestrates mineral patch assignment and JIT prepositioning.
    /// Handles map data initialization, worker mining coordination, and custom debug drawing.
    /// Provides visualization for:
    /// - Worker labels with names, roles, and targets
    /// - Center of mass (minerals and vespene clusters)
    /// - Worker instructions (arrows/lines showing where workers are headed)
    /// </summary>
    public class BabySharkMiningManager : IManager
    {
        public bool NeverSkip { get; set; } = true;
        public bool SkipFrame { get; set; } = false;
        public double LongestFrame { get; set; } = 0;
        public double TotalFrameTime { get; set; } = 0;

        private InitialMapData _initialMapData;
        private SecondaryMapData _secondaryMapData;
        private OngoingMapData _ongoingMapData;
        private readonly ActiveUnitData _activeUnitData;
        private readonly SharkyUnitData _sharkyUnitData;
        private readonly HashSet<UnitTypes> WorkerTypes = new() { UnitTypes.ZERG_DRONE, UnitTypes.TERRAN_SCV, UnitTypes.PROTOSS_PROBE };
        private bool _initialMiningManeuvers = true;
        private int _openingFrame = -1;
        private WorkerLabelService _workerLabelService;
        private CrosshairService _crosshairService;
        private MineralLabelService _mineralLabelService;
        private VespeneLabelService _vespeneLabelService;
        private ExpansionCOMService _expansionCOMService;
        private ExpansionPointService _expansionPointService;
        private ExpansionPointDrawService _expansionPointDrawService;
        private ProvisionalExpansionService _provisionalExpansionService;
        private MineralReturnRateTrackerService _mineralReturnRateTrackerService;
        private FrameToTimeConverter _frameToTimeConverter;
        private SpawningPoolPlacementService _spawningPoolPlacementService;
        private Sharky.Pathing.MapDataService _mapDataService;
        private MawBaseLocationData? _mapData;  // Store loaded map data for visualization
        private readonly chrisCrossAppleSause _ccaMiningService;
        private int _lastMineralReturnRateConsoleFrame = -999999;
        private bool _printedTwelveDroneMilestone = false;
        private bool _pausedAfterWorkerInstructions = false;
        private bool _didInitialLabelBreak = false;
        private int _workerInstructionDrawCount = 0;
        private bool _spawnLabelDebugBreakTriggered = false;
        private float _lastTotalCollected = -1f;
        private int _lastFunctionalDroneCount = -1;
        private int _currentFrame = -1;
        private int _pauseUntilFrame = -1;
        private bool _forceCcaOnce = false;
        private bool _handoffBreakTriggered = false;

        private bool _cargoReturnDebugBreakTriggered = false;
        private int _lastReachabilityConsoleFrame = -999999;
        private int _lastCargoEvaluationConsoleFrame = -999999;
        
        // JIT per-worker state (replaces MiningTeamState)
        private class JitWorkerState
        {
            public int TeamNumber { get; set; }
            public string TeamId { get; set; } = string.Empty;
            public ulong CurrentMineralTag { get; set; }
            public ulong AlternateMineralTag { get; set; }
            public Vector2Dto CurrentMineralPos { get; set; } = new Vector2Dto();
            public Vector2Dto AlternateMineralPos { get; set; } = new Vector2Dto();
            public bool WasCarrying { get; set; }
        }

        private Dictionary<ulong, bool> _previousCarryingState = new Dictionary<ulong, bool>();
        private readonly HashSet<ulong> _cargoReturnSequenceActive = new HashSet<ulong>();
        private readonly Dictionary<ulong, CargoReturnSequenceState> _pendingCargoReturnSequences = new Dictionary<ulong, CargoReturnSequenceState>();
        private readonly Dictionary<ulong, ulong> _defaultSpeedMiningMineralByWorker = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, bool> _assignedWorkerCarryingState = new Dictionary<ulong, bool>();
        private readonly HashSet<string> _firstCycleRoleThreeMineralWalks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private sealed class CargoReturnSequenceState
        {
            public Vector2Dto ReturnPoint { get; set; } = new Vector2Dto();
            public ulong TownhallTag { get; set; }
            public int CreatedFrame { get; set; }
        }
        private readonly Dictionary<ulong, JitWorkerState> _jitWorkerStates = new Dictionary<ulong, JitWorkerState>();
        private Dictionary<ulong, PinkWorkerState> _pinkWorkerStates = new();
        private bool _speedMiningActive = false;

        private class PinkWorkerState
        {
            public string PrimaryPrefix { get; set; } = ""; // e.g., "S", "Y", "B"
            public string SecondaryPrefix { get; set; } = ""; // Cross-team helper prefix
            public bool IsTransitionComplete { get; set; } = false;
        }

        private Dictionary<ulong, bool> _workerLastMinedA = new Dictionary<ulong, bool>();
        private Dictionary<string, List<MineralNode>> _expansionMinerals = new Dictionary<string, List<MineralNode>>();
        private Dictionary<string, List<MiningTeam>> _expansionTeams = new Dictionary<string, List<MiningTeam>>();
        private Dictionary<ulong, string> _workerTeamAssignment = new Dictionary<ulong, string>();

        // Event raised when initial mining has been started (CCA handed off)
        public event System.Action? OnMiningStarted;

        public void SignalMiningStarted()
        {
            try
            {
                Settings.ccaMining = false;
                Console.WriteLine("BabySharkMiningManager: Initial mining handoff complete (Settings.ccaMining = false)");
                OnMiningStarted?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.SignalMiningStarted: {ex.Message}");
            }
        }

        public BabySharkMiningManager(
            ActiveUnitData activeUnitData,
            SharkyUnitData sharkyUnitData,
            WorkerLabelService? workerLabelService = null, 
            CrosshairService? crosshairService = null, 
            MineralLabelService? mineralLabelService = null, 
            VespeneLabelService? vespeneLabelService = null, 
            ExpansionCOMService? expansionCOMService = null, 
            ExpansionPointService? expansionPointService = null, 
            ExpansionPointDrawService? expansionPointDrawService = null, 
            ProvisionalExpansionService? provisionalExpansionService = null, 
            MineralReturnRateTrackerService? mineralReturnRateTrackerService = null, 
            FrameToTimeConverter? frameToTimeConverter = null, 
            Sharky.Pathing.MapDataService? mapDataService = null, 
            SpawningPoolPlacementService? spawningPoolPlacementService = null, 
            chrisCrossAppleSause? ccaMiningService = null)
        {
            _initialMapData = new InitialMapData();
            _secondaryMapData = new SecondaryMapData();
            _ongoingMapData = new OngoingMapData();
            _activeUnitData = activeUnitData;
            _sharkyUnitData = sharkyUnitData;
            _workerLabelService = workerLabelService ?? new WorkerLabelService();
            _crosshairService = crosshairService ?? new CrosshairService();
            _mineralLabelService = mineralLabelService ?? new MineralLabelService();
            _vespeneLabelService = vespeneLabelService ?? new VespeneLabelService();
            _expansionCOMService = expansionCOMService ?? new ExpansionCOMService();
            _expansionPointService = expansionPointService ?? new ExpansionPointService();
            _expansionPointDrawService = expansionPointDrawService ?? new ExpansionPointDrawService();
            _provisionalExpansionService = provisionalExpansionService ?? new ProvisionalExpansionService();
            _mineralReturnRateTrackerService = mineralReturnRateTrackerService ?? new MineralReturnRateTrackerService();
            _frameToTimeConverter = frameToTimeConverter ?? new FrameToTimeConverter(new SharkyOptions());
            _mapDataService = mapDataService ?? new Sharky.Pathing.MapDataService(new Sharky.Pathing.MapData());
            _spawningPoolPlacementService = spawningPoolPlacementService ?? new SpawningPoolPlacementService(new DebugService(new SharkyOptions(), new ActiveUnitData(), new MacroData()));
            _ccaMiningService = ccaMiningService ?? new chrisCrossAppleSause();
            _mapData = null;
        }

        public WorkerLabelService WorkerLabelService => _workerLabelService;
        public MineralLabelService MineralLabelService => _mineralLabelService;
        public VespeneLabelService VespeneLabelService => _vespeneLabelService;
        public CrosshairService CrosshairService => _crosshairService;
        public ExpansionPointService ExpansionPointService => _expansionPointService;
        public ExpansionPointDrawService ExpansionPointDrawService => _expansionPointDrawService;
        public chrisCrossAppleSause CcaMiningService => _ccaMiningService;
        public MawBaseLocationData CurrentMapData => _mapData;

        public void SetCurrentMapData(MawBaseLocationData mapData)
        {
            _mapData = mapData;
            if (_mapData != null)
            {
                InitializeMainBaseMining();
            }
        }

        private void InitializeMainBaseMining()
        {
            if (_mapData == null || _mapData.OrderedMainMinerals == null)
                return;

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (startIndex < 0 || startIndex >= _mapData.OrderedMainMinerals.Count)
                return;

            var mainMinerals = _mapData.OrderedMainMinerals[startIndex];
            var townhallPos = _mapData.StartingTownHall[startIndex];
            if (townhallPos == null) return;

            var townhallPoint2D = new Point2D { X = townhallPos.X, Y = townhallPos.Y };
            var dummyMinerals = mainMinerals.Select(om => new Unit { 
                Tag = 0,
                Pos = new Point { X = om.Position.X, Y = om.Position.Y, Z = om.Position.Z },
                UnitType = (uint)UnitTypes.NEUTRAL_MINERALFIELD
            }).ToList();

            InitializeExpansionMining(townhallPoint2D, dummyMinerals);
        }

        public void ResetStartupState()
        {
            _pausedAfterWorkerInstructions = false;
            _workerInstructionDrawCount = 0;
            _initialMiningManeuvers = true;
            _openingFrame = -1;
            _cargoReturnDebugBreakTriggered = false;
            _lastReachabilityConsoleFrame = -999999;
            _lastCargoEvaluationConsoleFrame = -999999;
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, String opponentId)
        {
            _firstCycleRoleThreeMineralWalks.Clear();
            _currentFrame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (_mapData == null || snapshot == null || startIndex < 0)
            {
                return;
            }

            var liveWorkers = snapshot.SelfUnits.Values
                .Where(worker => worker != null && worker.Position != null && WorkerTypes.Contains((UnitTypes)worker.UnitType))
                .ToList();

            Console.WriteLine($"BabySharkMiningManager: OnStart observed starting workers at frame {_currentFrame}; liveWorkers={liveWorkers.Count}. Labels are owned by BabySharkBuildManager.");
        }

        private void DrawMineralTargetPoints()
        {
            if (!ManagerDebugService.IsDebugEnabled || _mapData?.OrderedMainMinerals == null) return;
            try
            {
                const float debugHeight = 12f;
                for (var startIndex = 0; startIndex < _mapData.OrderedMainMinerals.Count; startIndex++)
                {
                    var orderedList = _mapData.OrderedMainMinerals[startIndex];
                    if (orderedList == null) continue;

                    var hatcheryPosition = _mapData.StartingTownHall != null && _mapData.StartingTownHall.Length > startIndex ? _mapData.StartingTownHall[startIndex] : null;
                    if (hatcheryPosition != null)
                    {
                        DrawCircle(hatcheryPosition, 2.75f, new Color { R = 255, G = 255, B = 255 }, debugHeight);
                    }

                    foreach (var mineral in orderedList)
                    {
                        if (mineral?.Position == null || mineral.HarvestPoint == null || mineral.ReturnPoint == null) continue;
                        var finalLabel = !string.IsNullOrWhiteSpace(mineral.FinalLabel)
                            ? mineral.FinalLabel
                            : mineral.Label;
                        var color = !string.IsNullOrWhiteSpace(finalLabel)
                            ? ProcessVisableUnits.GetFinalLabelColor(finalLabel)
                            : new Color { R = 255, G = 255, B = 255 };
                        DrawCircle(mineral.Position, 1.0f, color, debugHeight);
                        ManagerDebugService.DrawText("h", new Point { X = mineral.HarvestPoint.X, Y = mineral.HarvestPoint.Y, Z = debugHeight }, color, 10);
                        ManagerDebugService.DrawText("r", new Point { X = mineral.ReturnPoint.X, Y = mineral.ReturnPoint.Y, Z = debugHeight }, color, 10);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error in DrawMineralTargetPoints: {ex.Message}"); }
        }

        private void DrawCircle(Vector2Dto center, float radius, Color color, float z, int segments = 24)
        {
            if (center == null || segments < 3) return;
            var step = Math.PI * 2.0 / segments;
            Point? previous = null;
            for (var i = 0; i <= segments; i++)
            {
                var angle = i * step;
                var point = new Point { X = center.X + (float)(Math.Cos(angle) * radius), Y = center.Y + (float)(Math.Sin(angle) * radius), Z = z };
                if (previous != null) ManagerDebugService.DrawLine(previous, point, color);
                previous = point;
            }
        }

        private void DrawAllUnitLabels(ResponseObservation observation)
        {
            if (_workerLabelService == null) return;

            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var storedWorkers = GetStoredWorkersForStart(startIndex);
            var labelsByTag = new Dictionary<ulong, string>();

            // Start with the complete registered label set. Do not iterate only visible units:
            // a label must be redrawn every frame even when its unit is temporarily absent from
            // the current observation snapshot.
            foreach (var label in _workerLabelService.GetAllLabels())
            {
                if (label.Value != 0 && !string.IsNullOrWhiteSpace(label.Key))
                {
                    labelsByTag[label.Value] = label.Key;
                }
            }

            // Persisted final labels are also part of the label set and may be registered after
            // the current frame's unit snapshot was built.
            foreach (var worker in storedWorkers ?? new List<WorkerEntryDto>())
            {
                if (worker == null || worker.UnitTag == 0) continue;
                var label = worker.FinalLabel ?? worker.Label ?? worker.StartLabel;
                if (!string.IsNullOrWhiteSpace(label) && !labelsByTag.ContainsKey(worker.UnitTag))
                {
                    labelsByTag[worker.UnitTag] = label;
                }
            }

            foreach (var labelByTag in labelsByTag)
            {
                var tag = labelByTag.Key;
                var label = labelByTag.Value;
                var workerEntry = storedWorkers?.FirstOrDefault(worker => worker != null && worker.UnitTag == tag);
                var liveAssignmentWorker = ResolveBuildGreedyAssignments(startIndex)
                    .SelectMany(assignment => assignment.Workers ?? new List<WorkerEntryDto>())
                    .FirstOrDefault(worker => worker != null && worker.UnitTag == tag);
                var displayWorker = liveAssignmentWorker ?? workerEntry;
                var displayLabel = FormatWorkerDisplayLabel(displayWorker, label, storedWorkers);
                var displayColor = ResolveWorkerDisplayColor(tag, label, startIndex);
                Vector2Dto position = null;

                if (snapshot?.SelfUnits != null && snapshot.SelfUnits.TryGetValue(tag, out var entry))
                {
                    position = entry?.Position;
                }


                if (position == null)
                {
                    position = storedWorkers?.FirstOrDefault(w => w != null && w.UnitTag == tag)?.Position;
                }

                if (position == null) continue;

                ManagerDebugService.DrawText(displayLabel, new Point
                {
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z + 0.5f
                }, displayColor, 12);
            }
        }

        private Color ResolveWorkerDisplayColor(ulong workerTag, string label, int startIndex)
        {
            var assignedWorkerEntry = ResolveBuildGreedyAssignments(startIndex)
                .SelectMany(assignment => assignment.Workers ?? new List<WorkerEntryDto>())
                .FirstOrDefault(worker => worker != null && worker.UnitTag == workerTag);
            var storedWorker = GetStoredWorkersForStart(startIndex)
                ?.FirstOrDefault(worker => worker != null && worker.UnitTag == workerTag);
            var startLabel = assignedWorkerEntry?.StartLabel
                ?? assignedWorkerEntry?.Label
                ?? storedWorker?.StartLabel
                ?? storedWorker?.Label;
            var assignedWorkerCount = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)?.Count ?? 0;
            if (assignedWorkerCount == 12)
            {
                var assignedWorker = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                    ?.FirstOrDefault(worker => worker != null && worker.UnitID == workerTag);
                var target = assignedWorker?.MiningTargets?.ElementAtOrDefault(assignedWorker.Mti);
                if (!string.IsNullOrWhiteSpace(target?.ToResourceLabel)
                    && target.ToResourceLabel.Length == 2)
                {
                    var teamColor = TeamColorService.GetColorByPrefix(target.ToResourceLabel.Substring(0, 1));
                    if (target.ToResourceLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase))
                    {
                        return teamColor;
                    }

                    if (target.ToResourceLabel.EndsWith("B", StringComparison.OrdinalIgnoreCase))
                    {
                        return new Color { R = 255, G = 255, B = 255 };
                    }
                }
            }

            return ProcessVisableUnits.GetFinalLabelColor(label);
        }

        private static string FormatWorkerDisplayLabel(WorkerEntryDto worker, string finalLabel, List<WorkerEntryDto> storedWorkers)
        {
            if (worker == null || string.IsNullOrWhiteSpace(finalLabel))
            {
                return finalLabel ?? string.Empty;
            }

            // StartLabel is the canonical greedy list index: W1 is list/display 1,
            // W8/W12 is list/display 8/12. Never derive this prefix from observation order.
            var workerIndex = ParseWorkerIndex(worker.StartLabel ?? worker.Label);
            return workerIndex > 0 ? $"{workerIndex}-{finalLabel}" : finalLabel;
        }

        private static int ParseWorkerIndex(string label)
        {
            if (string.IsNullOrWhiteSpace(label) || label.Length < 2 || label[0] != 'W')
            {
                return 0;
            }

            return int.TryParse(label.Substring(1), out var index) ? index : 0;
        }

        private void DrawWorkerInstructions(ResponseObservation observation)
        {
            var snapshot = Globals.CurrentObservation;
            if (!ManagerDebugService.IsDebugEnabled || snapshot == null) return;

            foreach (var kvp in snapshot.SelfUnits)
            {
                var entry = kvp.Value;
                var ut = (UnitTypes)entry.UnitType;
                if (!WorkerTypes.Contains(ut)) continue;

                var start = new Point { X = entry.Position.X, Y = entry.Position.Y, Z = entry.Position.Z + 0.25f };
                var end = new Point { X = entry.Position.X, Y = entry.Position.Y, Z = entry.Position.Z + 1.25f };
                DrawArrow(start, end, new Color { R = 255, G = 255, B = 255 });
            }
        }

        private void DrawCenterOfMassLocations()
        {
            if (!ManagerDebugService.IsDebugEnabled || _crosshairService == null) return;
            var allCOMs = _crosshairService.GetAllCOMs();
            foreach (var kvp in allCOMs)
            {
                var comPos = kvp.Value?.Position;
                var color = kvp.Value?.Color ?? new Color { R = 255, G = 255, B = 255 };
                if (comPos == null) continue;
                ManagerDebugService.DrawLine(new Point { X = comPos.X - 2f, Y = comPos.Y, Z = comPos.Z }, new Point { X = comPos.X + 2f, Y = comPos.Y, Z = comPos.Z }, color);
                ManagerDebugService.DrawLine(new Point { X = comPos.X, Y = comPos.Y - 2f, Z = comPos.Z }, new Point { X = comPos.X, Y = comPos.Y + 2f, Z = comPos.Z }, color);
            }
        }

        private void DrawExpansionCOMCrosshairs()
        {
            if (!ManagerDebugService.IsDebugEnabled || _expansionCOMService == null) return;
            var expansionCOMs = _expansionCOMService.Get();
            foreach (var kvp in expansionCOMs)
            {
                var comPos = kvp.Value;
                if (comPos == null) continue;
                var blueColor = new Color { R = 0, G = 0, B = 255 };
                ManagerDebugService.DrawLine(new Point { X = comPos.X - 2f, Y = comPos.Y, Z = comPos.Z }, new Point { X = comPos.X + 2f, Y = comPos.Y, Z = comPos.Z }, blueColor);
                ManagerDebugService.DrawLine(new Point { X = comPos.X, Y = comPos.Y - 2f, Z = comPos.Z }, new Point { X = comPos.X, Y = comPos.Y + 2f, Z = comPos.Z }, blueColor);
            }
        }

        private Dictionary<ulong, int> _workerIdleFrames = new Dictionary<ulong, int>();

        public IEnumerable<SC2Action> OnFrame(ResponseObservation observation)
        {
            _currentFrame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            var relativeFrame = Settings.GetRelativeFrame(_currentFrame);
            var snapshot = Globals.CurrentObservation;
            if (_currentFrame - _lastReachabilityConsoleFrame >= 25)
            {
                _lastReachabilityConsoleFrame = _currentFrame;
                Console.WriteLine($"[MINING REACH] OnFrame frame={_currentFrame} relative={relativeFrame} snapshot={snapshot != null} mapData={_mapData != null} cca={Settings.ccaMining} simulated={Settings.SimulatedStartActive} buildOwns={Settings.BuildOwnsWorkerCommands} availableWorkers={Settings.AvailableWorker.Count} workerCount={Settings.WorkerCount}");
            }

            if (snapshot == null)
            {
                Console.WriteLine($"[MINING REACH] skipped frame={_currentFrame}: Globals.CurrentObservation is null");
                return Array.Empty<SC2Action>();
            }

            ProcessFrameObservation(observation);

            if (!_handoffBreakTriggered && !Settings.ccaMining && relativeFrame >= 35)
            {
                _handoffBreakTriggered = true;
                Console.WriteLine($"BabySharkMiningManager: Steady-state JIT verified at frame {_currentFrame}");
            }

            var actions = new List<SC2Action>();
            if (Settings.SimulatedStartActive || Settings.BuildOwnsWorkerCommands)
            {
                Console.WriteLine($"[MINING REACH] commands skipped frame={_currentFrame}: simulated={Settings.SimulatedStartActive} buildOwns={Settings.BuildOwnsWorkerCommands}");
                return actions;
            }

            // Update phase state (Speed Mining) based on current functional drone count
            var droneCount = snapshot.SelfUnits.Values.Count(u => u != null && u.UnitType == (uint)UnitTypes.ZERG_DRONE && u.IsCompleted);
            UpdatePhaseState(droneCount);

            // CCA owns frames 0-35 exclusively; steady-state executes only build assignments.
            if (!Settings.ccaMining)
            {
                actions.AddRange(ExecuteJustInTimeMining(observation));

            }

            UpdateScoutedMinerals(observation);
            UpdateMineralReturnRate(observation);
            PrintMineralReturnRateSummary(observation);
            PrintTwelveDroneMilestone(observation);
            LogMiningCommands(actions);
            return actions;
        }

        private void RegisterCurrentSpawnLabels(int startIndex, List<WorkerEntryDto> liveWorkers)
        {
            // BuildManager owns worker labels. MiningManager only consumes the
            // current-spawn assignment records and registers resource visuals.
            if (_mapData == null || startIndex < 0)
            {
                return;
            }

            MapLabelRegistrationHelper.RegisterLabels(
                _mapData,
                startIndex,
                _mineralLabelService,
                _vespeneLabelService);
        }

        private static float DistanceSquared(Vector2Dto first, Vector2Dto second)
        {
            if (first == null || second == null) return float.MaxValue;
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }

        public void DrawDebugVisuals(ResponseObservation observation)
        {
            if (!ManagerDebugService.IsDebugEnabled) return;

            DrawMineralTargetPoints();
            DrawAllUnitLabels(observation);
            DrawWorkerInstructions(observation);
            DrawCenterOfMassLocations();
            DrawExpansionCOMCrosshairs();
            DrawCenterOfMass();
            DrawMineralLabels();
            DrawExpansionMineralLabels();
            DrawVespeneLabels();
            DrawExpansionPoints();
            DrawSpawningPoolPlacement();
            BreakWhenSpawnLabelsShouldBeVisible(observation);
        }

        public void ProcessFrameObservation(ResponseObservation observation)
        {
            var snapshot = Globals.CurrentObservation;
            if (snapshot == null) return;

            if (_mapData == null) return;
            if (!TryResolveObservedSpawn(out var currentStartIndex, out _))
            {
                Console.WriteLine($"[MINING TARGET] ProcessFrameObservation suppressed frame={_currentFrame}: observed self town hall does not match cached starts.");
                return;
            }

            var workerEntries = snapshot.SelfUnits.Values
                .Where(worker => worker != null && WorkerTypes.Contains((UnitTypes)worker.UnitType) && worker.UnitTag != 0)
                .ToList();
            var liveWorkers = snapshot.SelfUnits.Values
                .Where(worker => worker != null && WorkerTypes.Contains((UnitTypes)worker.UnitType) && worker.UnitTag != 0)
                .Select(worker => ToObservedUnit(worker))
                .ToList();
            var buildAssignments = ResolveBuildGreedyAssignments(currentStartIndex);
            UpdateAssignedWorkerObservationState(currentStartIndex, snapshot);
            RefreshBuildPlanLiveTags(buildAssignments, liveWorkers, currentStartIndex);
            var currentAssignments = ResolveBuildGreedyAssignments(currentStartIndex);
            var relativeFrame = Settings.GetRelativeFrame(_currentFrame);
            
            if (relativeFrame % 100 == 0)
            {
                Console.WriteLine($"BabySharkMiningManager.ProcessFrameObservation: Frame={_currentFrame} (relative {relativeFrame}), StartIndex={currentStartIndex}, AssignmentsFound={currentAssignments.Count}");
            }

            _ccaMiningService.RecordSpawnObservation(_mapData, currentStartIndex, new List<List<TeamPatchAssignmentDto>> { currentAssignments }, _workerLabelService, workerEntries: workerEntries);

            var townhall = _mapData.StartingTownHall[currentStartIndex];
            if (townhall == null) return;

            // Initialize per-worker JIT states from the authoritative team assignments
            InitializeJitWorkerStates(currentAssignments);

            // Synchronize team assignments between TeamPatchAssignmentDto and the manager's internal tracking
            var key = GetPointKey(new Point2D { X = townhall.X, Y = townhall.Y });
            
            // Ensure expansion mining is initialized for this townhall location if not already
            if (!_expansionTeams.ContainsKey(key))
            {
                var minerals = currentAssignments.SelectMany(a => a.Minerals)
                    .Select(m => new Unit { Tag = m.UnitTag, Pos = new Point { X = m.Position.X, Y = m.Position.Y, Z = m.Position.Z }, UnitType = (uint)UnitTypes.NEUTRAL_MINERALFIELD })
                    .ToList();
                InitializeExpansionMining(new Point2D { X = townhall.X, Y = townhall.Y }, minerals);
            }

            foreach (var assignment in currentAssignments)
            {
                if (assignment == null) continue;

                // Ensure the corresponding MiningTeam in _expansionTeams is also synchronized
                if (_expansionTeams.TryGetValue(key, out var teams))
                {
                    var team = teams.FirstOrDefault(t => t.TeamId == assignment.TeamId);
                    if (team != null)
                    {
                        foreach (var worker in assignment.Workers)
                        {
                            if (worker.UnitTag != 0 && !team.WorkerTags.Contains(worker.UnitTag))
                            {
                                team.WorkerTags.Add(worker.UnitTag);
                                if (team.WorkerTags.Count == 3) team.IsJITTeam = true;
                            }
                        }
                    }
                }

                foreach (var worker in assignment.Workers)
                {
                    if (worker.UnitTag != 0 && !_workerTeamAssignment.ContainsKey(worker.UnitTag))
                    {
                        _workerTeamAssignment[worker.UnitTag] = assignment.TeamId;
                        Console.WriteLine($"BabySharkMiningManager: Mapped worker tag {worker.UnitTag} ({worker.FinalLabel}) to Team {assignment.TeamId}");
                    }
                }
            }
        }

        private void UpdatePhaseState(int totalWorkers)
        {
            bool wasSpeedMining = _speedMiningActive;
            _speedMiningActive = TeamColorService.IsSpeedMiningPhase(totalWorkers);

            if (_speedMiningActive && !wasSpeedMining)
            {
                Console.WriteLine($"BabySharkMiningManager: SPEED MINING ACTIVATED at {totalWorkers} workers (Trigger: Worker 16)");
                // Transition pink workers (S4, Y4, B4) to their final team roles
                TransitionPinkWorkersToSpeedMining();
            }
        }

        private void TransitionPinkWorkersToSpeedMining()
        {
            foreach (var kvp in _pinkWorkerStates)
            {
                var state = kvp.Value;
                if (!state.IsTransitionComplete)
                {
                    // Pink workers change to their team color and role when they switch to the correct mineral
                    // during their regular "A/B" switch cycle.
                    state.IsTransitionComplete = true;
                    
                    // Signal to WorkerLabelService to update color if needed
                    var label = _workerLabelService.GetLabel(kvp.Key);
                    if (!string.IsNullOrEmpty(label))
                    {
                        Console.WriteLine($"PinkWorker {kvp.Key} ({label}): Transitioned to speed mining team.");
                    }
                }
            }
        }

        private OrderedMineral? ResolvePinkMineral(
            string workerLabel, 
            List<TeamPatchAssignmentDto> allTeams,
            JitWorkerState state,
            bool carrying)
        {
            // Pink workers mine across team boundaries before speed mining
            if (_speedMiningActive) return null; // Let normal team logic handle it

            string primary, secondary;
            switch (workerLabel)
            {
                case "S4": primary = "SB"; secondary = "TB"; break;
                case "Y4": primary = "YB"; secondary = "BB"; break;
                case "B4": primary = "SA"; secondary = "BA"; break;
                default: return null;
            }

            // Standard A/B alternating JIT
            var targetLabel = carrying ? secondary : primary;
            return allTeams.SelectMany(t => t.Minerals)
                .FirstOrDefault(m => m.FinalLabel == targetLabel);
        }

        private void InitializeJitWorkerStates(List<TeamPatchAssignmentDto> assignments)
        {
            if (assignments == null) return;
            foreach (var assignment in assignments)
            {
                if (assignment?.Workers == null || assignment.Minerals?.Count < 2) continue;
                
                var teamPrefix = assignment.TeamNumber switch
                {
                    1 => "T",
                    2 => "S",
                    3 => "B",
                    4 => "Y",
                    _ => string.Empty
                };
                var mineralA = assignment.Minerals.FirstOrDefault(mineral =>
                    string.Equals(mineral.FinalLabel, $"{teamPrefix}A", StringComparison.OrdinalIgnoreCase));
                var mineralB = assignment.Minerals.FirstOrDefault(mineral =>
                    string.Equals(mineral.FinalLabel, $"{teamPrefix}B", StringComparison.OrdinalIgnoreCase));
                if (mineralA == null || mineralB == null)
                {
                    Console.WriteLine($"[MINING TARGET] JIT state skipped team={assignment.TeamId}: missing canonical {teamPrefix}A/{teamPrefix}B labels.");
                    continue;
                }
                
                foreach (var worker in assignment.Workers)
                {
                    if (worker.UnitTag == 0) continue;

                    var label = worker.FinalLabel ?? worker.Label ?? string.Empty;
                    var startsOnA = label.EndsWith("1", StringComparison.OrdinalIgnoreCase)
                        || label.EndsWith("3", StringComparison.OrdinalIgnoreCase);

                    if (!_jitWorkerStates.TryGetValue(worker.UnitTag, out var state))
                    {
                        state = new JitWorkerState
                        {
                            TeamNumber = assignment.TeamNumber,
                            TeamId = assignment.TeamId,
                            WasCarrying = false
                        };
                        _jitWorkerStates[worker.UnitTag] = state;
                    }

                    // Only assign initial targets if this worker has never received them.
                    // Once assigned (including after a cargo-return swap), leave them alone.
                    if (state.CurrentMineralTag == 0 && mineralA.UnitTag != 0)
                    {
                        state.CurrentMineralTag = startsOnA ? mineralA.UnitTag : mineralB.UnitTag;
                        state.CurrentMineralPos = startsOnA ? mineralA.Position : mineralB.Position;
                    }
                    if (state.AlternateMineralTag == 0 && mineralB.UnitTag != 0)
                    {
                        state.AlternateMineralTag = startsOnA ? mineralB.UnitTag : mineralA.UnitTag;
                        state.AlternateMineralPos = startsOnA ? mineralB.Position : mineralA.Position;
                    }
                }
            }
        }

        private void UpdateAssignedWorkerObservationState(int startIndex, ObservationSnapshotDto snapshot)
        {
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex);
            if (assignedWorkers == null)
            {
                return;
            }

            foreach (var assignedWorker in assignedWorkers)
            {
                if (!snapshot.SelfUnits.TryGetValue(assignedWorker.UnitID, out var observedWorker))
                {
                    continue;
                }

                _assignedWorkerCarryingState.TryGetValue(assignedWorker.UnitID, out var wasAssignedWorkerCarrying);
                assignedWorker.CurrentXY = observedWorker.Position;
                assignedWorker.CurrentTargetUnitID = observedWorker.TargetUnitTag;
                assignedWorker.CurrentAbilityID = (uint)(observedWorker.OrderAbilityIds?.FirstOrDefault() ?? 0);
                assignedWorker.IsCarrying = observedWorker.IsCarrying;
                _assignedWorkerCarryingState[assignedWorker.UnitID] = observedWorker.IsCarrying;

                if (assignedWorker.MiningTargets.Count <= 1)
                {
                    assignedWorker.Mti = 0;
                    continue;
                }

                if (wasAssignedWorkerCarrying && !observedWorker.IsCarrying)
                {
                    var oldIndex = assignedWorker.Mti;
                    var oldCount = assignedWorker.MiningTargets.Count;
                    AdvanceAssignedWorkerTarget(assignedWorker);
                    Console.WriteLine($"[ASSIGNED TARGET] worker={assignedWorker.UnitID} cargo-return mti={oldIndex}->{assignedWorker.Mti} targets={oldCount}->{assignedWorker.MiningTargets.Count} current={(assignedWorker.MiningTargets.ElementAtOrDefault(assignedWorker.Mti)?.ToResourceLabel ?? "<none>")} carrying={wasAssignedWorkerCarrying}->{assignedWorker.IsCarrying}");
                }
            }
        }

        private static void AdvanceAssignedWorkerTarget(AssignedWorkerDto assignedWorker)
        {
            if (assignedWorker.MiningTargets.Count <= 1)
            {
                assignedWorker.Mti = 0;
                return;
            }

            if (assignedWorker.MiningTargets.Count == 2)
            {
                // The 12-worker A/B plan is a true cycle: A -> B -> A.
                assignedWorker.Mti = assignedWorker.Mti == 0 ? 1 : 0;
                return;
            }

            if (assignedWorker.Mti < assignedWorker.MiningTargets.Count - 1)
            {
                assignedWorker.Mti++;
                return;
            }

            var switchTargets = assignedWorker.MiningTargets
                .Where(target => target.IsABSwitch)
                .ToList();
            if (switchTargets.Count == 0)
            {
                assignedWorker.Mti = assignedWorker.MiningTargets.Count - 1;
                return;
            }

            assignedWorker.MiningTargets = switchTargets;
            assignedWorker.Mti = 0;
        }

        private List<TeamPatchAssignmentDto> ResolveBuildGreedyAssignments(int startIndex)
        {
            if (_mapData?.TeamPatchAssignments == null
                || startIndex < 0
                || startIndex >= _mapData.TeamPatchAssignments.Count)
            {
                return new List<TeamPatchAssignmentDto>();
            }

            return _mapData.TeamPatchAssignments[startIndex]
                ?.Where(assignment => assignment != null)
                .ToList()
                ?? new List<TeamPatchAssignmentDto>();
        }

        private List<SC2Action> ExecuteJustInTimeMining(ResponseObservation observation)
        {
            var actions = new List<SC2Action>();
            var snapshot = Globals.CurrentObservation;
            if (snapshot == null)
            {
                Console.WriteLine($"[MINING REACH] JIT skipped frame={_currentFrame}: snapshot is null");
                return actions;
            }

            if (!TryResolveObservedSpawn(out var startIndex, out var townhallPosition))
            {
                Console.WriteLine($"[MINING TARGET] JIT suppressed frame={_currentFrame}: observed self town hall does not match cached starts.");
                return actions;
            }

            var rawTeamAssignments = ResolveBuildGreedyAssignments(startIndex);
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();
            var liveWorkers = snapshot.SelfUnits.Values
                .Where(worker => worker != null && WorkerTypes.Contains((UnitTypes)worker.UnitType) && worker.UnitTag != 0)
                .Select(worker => ToObservedUnit(worker))
                .ToList();
            RefreshBuildPlanLiveTags(rawTeamAssignments, liveWorkers, startIndex);

            var teamAssignments = rawTeamAssignments;
            if (_currentFrame % 25 == 0)
            {
                var directCount = _mapData?.TeamPatchAssignments?.ElementAtOrDefault(startIndex)?.Count ?? 0;
                Console.WriteLine($"[MINING ASSIGNMENTS] frame={_currentFrame} source=BuildGreedyMineralChain startIndex={startIndex} raw={rawTeamAssignments.Count} validated={teamAssignments.Count} direct={directCount} workerCount={Settings.WorkerCount}");
            }

            if (liveWorkers.Count == 0)
            {
                Console.WriteLine($"[MINING REACH] JIT skipped frame={_currentFrame}: liveWorkers=0 startIndex={startIndex} snapshotWorkers={snapshot.SelfUnits.Count} availableWorkers={snapshot.AvailableWorkers.Count}");
                return actions;
            }

            // Find the current-game town hall for the assigned return route.
            var townhallEntry = snapshot.CurrentTownHalls.Values
                .FirstOrDefault(unit => unit != null
                    && unit.Position != null
                    && Math.Abs(unit.Position.X - townhallPosition.X) < 1.0f
                    && Math.Abs(unit.Position.Y - townhallPosition.Y) < 1.0f);
            var townhallUnit = townhallEntry == null ? null : ToObservedUnit(townhallEntry);

            if (_currentFrame - _lastCargoEvaluationConsoleFrame >= 25)
            {
                _lastCargoEvaluationConsoleFrame = _currentFrame;
                var carryingWorkers = liveWorkers.Count(worker => worker.BuffIds.Any(b => b == 271 || b == 272));
                var previousCarryingWorkers = liveWorkers.Count(worker => _previousCarryingState.TryGetValue(worker.Tag, out var wasCarrying) && wasCarrying);
                Console.WriteLine($"[MINING REACH] cargo-eval frame={_currentFrame} liveWorkers={liveWorkers.Count} carrying={carryingWorkers} previousCarrying={previousCarryingWorkers} teamAssignments={teamAssignments.Count} townhallTag={townhallUnit?.Tag ?? 0} source=raw-observation");
            }

            return ExecuteAssignedWorkerTargets(assignedWorkers, liveWorkers, townhallUnit, teamAssignments);

            foreach (var assignment in teamAssignments)
            {
                if (assignment?.Workers == null || assignment.Minerals == null || assignment.Minerals.Count == 0)
                    continue;

                var teamWorkers = ResolveCurrentWorkersForTeamRaw(liveWorkers, assignment.Workers);
                if (teamWorkers.Count == 0) continue;

                // 3-worker teams = JIT rotation; 2-worker teams = static speed mining
                var isJitTeam = assignment.Workers.Count >= 3;

                foreach (var worker in teamWorkers)
                {
                    var label = _workerLabelService.GetLabel(worker.Tag) ?? "";
                    var carrying = worker.BuffIds.Any(b => b == 271 || b == 272);
                    _previousCarryingState.TryGetValue(worker.Tag, out var wasCarrying);

                    _jitWorkerStates.TryGetValue(worker.Tag, out var state);

                    // Per-worker A/B swap on cargo return transition
                    // CRITICAL FIX: Only swap A/B for JIT teams (3+ workers). 
                    // Speed-mining teams (2 workers) stay on their mineral.
                    if (!carrying && wasCarrying && isJitTeam)
                    {
                        OnWorkerCargoReturned(worker.Tag);
                    }

                    // A queued return sequence is complete once the worker is observed without cargo.
                    if (!carrying)
                    {
                        _cargoReturnSequenceActive.Remove(worker.Tag);
                    }

                    _previousCarryingState[worker.Tag] = carrying;

                    // A cargo pickup is a one-frame transition. Stop the current gather order, then
                    // queue the geometry-specific return MOVE and the town-hall SMART handoff.
                    // The queued commands preserve the MOVE -> SMART speed-mining cue while making
                    // the return point explicit for both A/B-switch and speed-mining workers.
                    if (carrying && !wasCarrying && townhallUnit != null && !_cargoReturnSequenceActive.Contains(worker.Tag))
                    {
                        var cargoReturnPoint = ResolveCargoReturnPoint(
                            assignment,
                            state,
                            null,
                            isJitTeam);

                        if (cargoReturnPoint != null && IssueCargoReturnSequence(actions, worker.Tag, cargoReturnPoint, townhallUnit.Tag))
                        {
                            _cargoReturnSequenceActive.Add(worker.Tag);
                            continue;
                        }
                    }

                    if (carrying && _cargoReturnSequenceActive.Contains(worker.Tag))
                    {
                        continue;
                    }

                    if (isJitTeam)
                    {
                        if (carrying && townhallUnit != null)
                        {
                            var returnPos = new Point2D { X = assignment.JitReturnPoint.X, Y = assignment.JitReturnPoint.Y };
                            if (Distance(worker.Pos.ToPoint2D(), returnPos) < 0.15f)
                            {
                                AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.SMART, UnitTags = { worker.Tag }, TargetUnitTag = townhallUnit.Tag } } });
                            }
                            else
                            {
                                AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { worker.Tag }, TargetWorldSpacePos = returnPos } } });
                            }
                        }
                        else if (!carrying && state != null)
                        {
                            var mineral = ResolveMineralForWorker(assignment, state);
                            if (mineral != null)
                            {
                                var harvestPos = new Point2D { X = mineral.HarvestPoint.X, Y = mineral.HarvestPoint.Y };
                                if (Distance(worker.Pos.ToPoint2D(), harvestPos) < 0.15f)
                                {
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.HARVEST_GATHER, UnitTags = { worker.Tag }, TargetUnitTag = mineral.UnitTag } } });
                                }
                                else
                                {
                                    // FIX: Add Mineral-Walking (SMART) to Steady-State
                                    if (Distance(worker.Pos.ToPoint2D(), harvestPos) < 2.5f && mineral.UnitTag != 0)
                                    {
                                        AddAction(actions, new SC2Action 
                                        { 
                                            ActionRaw = new ActionRaw 
                                            { 
                                                UnitCommand = new ActionRawUnitCommand 
                                                { 
                                                    AbilityId = (int)Abilities.SMART, 
                                                    UnitTags = { worker.Tag }, 
                                                    TargetUnitTag = mineral.UnitTag 
                                                } 
                                            } 
                                        });
                                    }
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { worker.Tag }, TargetWorldSpacePos = harvestPos } } });
                                }
                            }
                        }
                    }
                    else // Speed Mining for 2-worker teams
                    {
                        if (state != null)
                        {
                            var mineral = ResolveMineralForWorker(assignment, state);
                            if (mineral != null)
                            {
                                if (carrying && townhallUnit != null && mineral.UnitTag != 0 && HasNonZeroPoint(mineral.ReturnPoint))
                                {
                                    var returnPos = new Point2D { X = mineral.ReturnPoint.X, Y = mineral.ReturnPoint.Y };
                                    if (Distance(worker.Pos.ToPoint2D(), returnPos) < 0.15f)
                                    {
                                        AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.SMART, UnitTags = { worker.Tag }, TargetUnitTag = townhallUnit.Tag } } });
                                    }
                                    else
                                    {
                                        AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { worker.Tag }, TargetWorldSpacePos = returnPos } } });
                                    }
                                    continue;
                                }

                                var harvestPos = new Point2D { X = mineral.HarvestPoint.X, Y = mineral.HarvestPoint.Y };
                                if (Distance(worker.Pos.ToPoint2D(), harvestPos) < 0.15f)
                                {
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.HARVEST_GATHER, UnitTags = { worker.Tag }, TargetUnitTag = mineral.UnitTag } } });
                                }
                                else
                                {
                                    // FIX: Add Mineral-Walking (SMART) to Steady-State
                                    if (Distance(worker.Pos.ToPoint2D(), harvestPos) < 2.5f && mineral.UnitTag != 0)
                                    {
                                        AddAction(actions, new SC2Action 
                                        { 
                                            ActionRaw = new ActionRaw 
                                            { 
                                                UnitCommand = new ActionRawUnitCommand 
                                                { 
                                                    AbilityId = (int)Abilities.SMART, 
                                                    UnitTags = { worker.Tag }, 
                                                    TargetUnitTag = mineral.UnitTag 
                                                } 
                                            } 
                                        });
                                    }
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { worker.Tag }, TargetWorldSpacePos = harvestPos } } });
                                }
                            }
                        }
                    }
                }
            }

            return actions;
        }

        private sealed class SnapshotUnit
        {
            public ulong Tag { get; init; }
            public uint UnitType { get; init; }
            public Point Pos { get; init; }
            public List<uint> BuffIds { get; init; } = new();
            public List<int> OrderAbilityIds { get; init; } = new();
            public ulong TargetUnitTag { get; init; }
            public bool IsCarrying { get; init; }
            public bool WasCarrying { get; init; }
            public string Label { get; init; } = string.Empty;
        }

        private static SnapshotUnit ToObservedUnit(WorkerEntryDto worker)
        {
            return new SnapshotUnit
            {
                Tag = worker.UnitTag,
                UnitType = worker.UnitType,
                Pos = new Point { X = worker.Position.X, Y = worker.Position.Y, Z = worker.Position.Z },
                BuffIds = worker.IsCarrying ? new List<uint> { 271 } : new List<uint>(),
                OrderAbilityIds = worker.OrderAbilityIds?.ToList() ?? new List<int>(),
                TargetUnitTag = worker.TargetUnitTag,
                IsCarrying = worker.IsCarrying,
                WasCarrying = worker.WasCarrying,
                Label = worker.Label ?? worker.FinalLabel ?? worker.StartLabel ?? string.Empty
            };
        }

        private bool TryResolveObservedSpawn(out int startIndex, out Vector2Dto townhall)
        {
            startIndex = -1;
            townhall = null;
            if (_mapData?.StartingTownHall == null || Globals.CurrentObservation?.CurrentTownHalls == null)
            {
                return false;
            }

            var observedTownhall = Globals.CurrentObservation.CurrentTownHalls.Values.FirstOrDefault();
            if (observedTownhall == null || observedTownhall.Position == null)
            {
                return false;
            }

            var closestDistance = float.MaxValue;
            for (var index = 0; index < _mapData.StartingTownHall.Length; index++)
            {
                var candidate = _mapData.StartingTownHall[index];
                if (candidate == null) continue;

                var distance = DistanceSquared(
                    observedTownhall.Position,
                    candidate);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    startIndex = index;
                    townhall = candidate;
                }
            }

            if (startIndex < 0 || closestDistance > 9f)
            {
                startIndex = -1;
                townhall = null;
                return false;
            }

            if (Globals.CurrentStartIndex != startIndex || Settings.CurrentSpawnIndex != startIndex)
            {
                Console.WriteLine($"[MINING TARGET] corrected current spawn to startIndex={startIndex} townhall=({townhall.X:F2},{townhall.Y:F2}) frame={_currentFrame}");
                Globals.CurrentStartIndex = startIndex;
                Settings.CurrentSpawnIndex = startIndex;
            }

            return true;
        }

        private bool IsCurrentSpawnPoint(Vector2Dto point, Vector2Dto townhall)
        {
            if (point == null || townhall == null)
            {
                return false;
            }

            // Main-base mineral coordinates must be close to the observed town hall;
            // this rejects cached coordinates from the opposite start location.
            return DistanceSquared(point, townhall) < 400f;
        }

        private static bool HasNonZeroPoint(Vector2Dto point)
        {
            return point != null && (point.X != 0f || point.Y != 0f);
        }

        private static Vector2Dto? ResolveNonZeroPoint(Vector2Dto preferred, Vector2Dto fallback)
        {
            return preferred != null && (preferred.X != 0f || preferred.Y != 0f)
                ? preferred
                : fallback != null && (fallback.X != 0f || fallback.Y != 0f)
                    ? fallback
                    : null;
        }

        private List<SC2Action> ExecuteAssignedWorkerTargets(
            List<AssignedWorkerDto> assignedWorkers,
            List<SnapshotUnit> liveWorkers,
            SnapshotUnit townhallUnit,
            List<TeamPatchAssignmentDto> teamAssignments)
        {
            var actions = new List<SC2Action>();
            var hatcheryReturnPredecessors = BuildHatcheryReturnPredecessors(assignedWorkers, liveWorkers);
            foreach (var assignedWorker in assignedWorkers ?? new List<AssignedWorkerDto>())
            {
                var worker = liveWorkers.FirstOrDefault(candidate => candidate.Tag == assignedWorker.UnitID);
                if (worker == null || assignedWorker.MiningTargets == null || assignedWorker.MiningTargets.Count == 0)
                {
                    continue;
                }

                var target = assignedWorker.MiningTargets.ElementAtOrDefault(assignedWorker.Mti);
                if (target == null)
                {
                    continue;
                }

                if (assignedWorker.MiningTargets.Count == 2
                    && IsRoleThree(assignedWorker.Role)
                    && assignedWorker.Mti == 0
                    && !HasTeamRoleOneCompleted(assignedWorker, assignedWorkers))
                {
                    continue;
                }

                var carrying = worker.IsCarrying;
                var justPickedUp = carrying && worker.WasCarrying == false;
                var justReturnedCargo = !carrying && worker.WasCarrying;
                var hasActiveOrder = worker.OrderAbilityIds.Any(IsMiningOrder);

                if (carrying)
                {
                    if (justPickedUp)
                    {
                        TryIssueFirstCycleRoleThreeMineralWalk(actions, assignedWorker, assignedWorkers, liveWorkers, target);
                        var returnPoint = target.ReturnPoint;
                        if (townhallUnit != null && HasNonZeroPoint(returnPoint)
                            && CanEnterHatcheryReturn(worker, assignedWorker, hatcheryReturnPredecessors, liveWorkers))
                        {
                            AddCargoReturnSequence(actions, worker, returnPoint, townhallUnit.Tag);
                        }
                    }
                    else if (!hasActiveOrder)
                    {
                        var returnPoint = target.ReturnPoint;
                        if (townhallUnit != null && HasNonZeroPoint(returnPoint)
                            && CanEnterHatcheryReturn(worker, assignedWorker, hatcheryReturnPredecessors, liveWorkers))
                        {
                            AddCargoReturnSequence(actions, worker, returnPoint, townhallUnit.Tag);
                        }
                    }

                    continue;
                }

                if (!justReturnedCargo && hasActiveOrder)
                {
                    continue;
                }

                var harvestPoint = target.HarvestPoint;
                if (!HasNonZeroPoint(harvestPoint) || target.ResourceUnitId == 0)
                {
                    continue;
                }

                AddHarvestSequence(actions, worker, harvestPoint, target.ResourceUnitId);
            }

            return actions;
        }

        private void TryIssueFirstCycleRoleThreeMineralWalk(
            List<SC2Action> actions,
            AssignedWorkerDto roleOne,
            List<AssignedWorkerDto> assignedWorkers,
            List<SnapshotUnit> liveWorkers,
            MiningTargetDto roleOneTarget)
        {
            if (assignedWorkers?.Count != 12
                || roleOne == null
                || string.IsNullOrWhiteSpace(roleOne.Role)
                || !roleOne.Role.EndsWith("1", StringComparison.OrdinalIgnoreCase)
                || roleOne.Mti != 0
                || roleOneTarget == null
                || !roleOneTarget.ToResourceLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var teamPrefix = roleOne.Role.Substring(0, 1);
            if (!_firstCycleRoleThreeMineralWalks.Add(teamPrefix))
            {
                return;
            }

            var roleThree = assignedWorkers.FirstOrDefault(worker =>
                worker != null
                && string.Equals(worker.Role, $"{teamPrefix}3", StringComparison.OrdinalIgnoreCase));
            var roleThreeTarget = roleThree?.MiningTargets?.ElementAtOrDefault(roleThree.Mti);
            var roleThreeLive = roleThree == null
                ? null
                : liveWorkers?.FirstOrDefault(worker => worker.Tag == roleThree.UnitID);
            if (roleThree == null || roleThreeTarget == null || roleThreeLive == null
                || roleThreeTarget.ResourceUnitId == 0
                || !roleThreeTarget.ToResourceLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                _firstCycleRoleThreeMineralWalks.Remove(teamPrefix);
                return;
            }

            AddAction(actions, IssueSmart(roleThreeLive, roleThreeTarget.ResourceUnitId, false));
            Console.WriteLine($"[FIRST CYCLE MINERAL WALK] leader={roleOne.Role}:{roleOne.UnitID} bumper={roleThree.Role}:{roleThree.UnitID} target={roleThreeTarget.ToResourceLabel} before-leader-hatchery-return=true");
        }

        private Dictionary<ulong, ulong> BuildHatcheryReturnPredecessors(
            List<AssignedWorkerDto> assignedWorkers,
            List<SnapshotUnit> liveWorkers)
        {
            var predecessors = new Dictionary<ulong, ulong>();
            var candidates = (assignedWorkers ?? new List<AssignedWorkerDto>())
                .Select(assigned => new
                {
                    Assigned = assigned,
                    Live = liveWorkers?.FirstOrDefault(worker => worker.Tag == assigned.UnitID),
                    Target = assigned?.MiningTargets?.ElementAtOrDefault(assigned.Mti)
                })
                .Where(item => item.Assigned != null && item.Live != null && HasNonZeroPoint(item.Target?.ReturnPoint))
                .OrderBy(item => ReturnRoleRank(item.Assigned.Role))
                .ThenBy(item => item.Assigned.UnitID)
                .ToList();

            foreach (var candidate in candidates)
            {
                var predecessor = candidates.FirstOrDefault(previous =>
                    previous.Assigned.UnitID != candidate.Assigned.UnitID
                    && ReturnRoleRank(previous.Assigned.Role) <= ReturnRoleRank(candidate.Assigned.Role)
                    && DistanceSquared(previous.Target.ReturnPoint, candidate.Target.ReturnPoint) <= 0.35f * 0.35f);
                if (predecessor != null)
                {
                    predecessors[candidate.Assigned.UnitID] = predecessor.Assigned.UnitID;
                }
            }

            return predecessors;
        }

        private bool CanEnterHatcheryReturn(
            SnapshotUnit worker,
            AssignedWorkerDto assignedWorker,
            Dictionary<ulong, ulong> predecessors,
            List<SnapshotUnit> liveWorkers)
        {
            if (worker == null || assignedWorker == null || predecessors == null
                || !predecessors.TryGetValue(assignedWorker.UnitID, out var predecessorTag))
            {
                return true;
            }

            var predecessor = liveWorkers?.FirstOrDefault(candidate => candidate.Tag == predecessorTag);
            var returnPoint = assignedWorker.MiningTargets?.ElementAtOrDefault(assignedWorker.Mti)?.ReturnPoint;
            var predecessorHoldingReturn = predecessor != null
                && predecessor.IsCarrying
                && (returnPoint == null || DistanceSquared(new Vector2Dto(predecessor.Pos.X, predecessor.Pos.Y, predecessor.Pos.Z), returnPoint) <= 0.75f * 0.75f);
            if (predecessorHoldingReturn)
            {
                Console.WriteLine($"[HATCHERY RETURN] worker={worker.Tag} role={assignedWorker.Role} held behind predecessor={predecessorTag} policy=leader-then-bumper");
                return false;
            }

            return true;
        }

        private static int ReturnRoleRank(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return 99;
            return role.EndsWith("1", StringComparison.OrdinalIgnoreCase) ? 1
                : role.EndsWith("3", StringComparison.OrdinalIgnoreCase) ? 2
                : role.EndsWith("2", StringComparison.OrdinalIgnoreCase) ? 3
                : 4;
        }

        private void AddCargoReturnSequence(List<SC2Action> actions, SnapshotUnit worker, Vector2Dto returnPoint, ulong townhallTag)
        {
            AddAction(actions, IssueMoveToPoint(worker, returnPoint, true));
            AddAction(actions, IssueHarvestReturn(worker.Tag, true));

            Console.WriteLine($"[MINING TRANSITION Return Cargo] worker={worker.Tag} Label={worker.Label} return-sequence=queued-MOVE,queued-HARVEST_RETURN");
        }

        private void AddHarvestSequence(List<SC2Action> actions, SnapshotUnit worker, Vector2Dto harvestPoint, ulong mineralTag)
        {
            if (worker?.Tag == 0 || mineralTag == 0)
            {
                return;
            }

            // During steady-state mining, let the mineral-target SMART command
            // own worker pathing. Do not inject STOP/MOVE corrections that make
            // workers path around one another and lose their mining momentum.
            AddAction(actions, IssueSmart(worker, mineralTag, false));
            Console.WriteLine($"[MINING TRANSITION2] worker={worker.Tag} Label={worker.Label} harvest-sequence=direct-SMART worker-aware-pass-through=true");
        }

        private static bool IsRoleThree(string role)
        {
            return !string.IsNullOrWhiteSpace(role)
                && role.Length == 2
                && role[1] == '3'
                && (role[0] == 'T' || role[0] == 'S' || role[0] == 'B' || role[0] == 'Y');
        }

        private static bool HasTeamRoleOneCompleted(AssignedWorkerDto roleThree, List<AssignedWorkerDto> assignedWorkers)
        {
            if (roleThree == null || assignedWorkers == null || string.IsNullOrWhiteSpace(roleThree.Role))
            {
                return false;
            }

            var teamPrefix = roleThree.Role.Substring(0, 1);
            var roleOne = assignedWorkers.FirstOrDefault(worker =>
                worker != null
                && string.Equals(worker.Role, $"{teamPrefix}1", StringComparison.OrdinalIgnoreCase));
            return roleOne != null && roleOne.Mti > 0;
        }

        private static bool IsMiningOrder(int abilityId)
        {
            return abilityId == (int)Abilities.MOVE
                || abilityId == (int)Abilities.SMART
                || abilityId == (int)Abilities.HARVEST_GATHER
                || abilityId == (int)Abilities.HARVEST_GATHER_DRONE
                || abilityId == (int)Abilities.HARVEST_GATHER_PROBE
                || abilityId == (int)Abilities.HARVEST_GATHER_SCV
                || abilityId == (int)Abilities.HARVEST_RETURN;
        }

        private List<SC2Action> ExecuteCargoPickupTransitions(
            List<SnapshotUnit> liveWorkers,
            List<TeamPatchAssignmentDto> teamAssignments,
            SnapshotUnit? townhallUnit,
            int startIndex)
        {
            var actions = new List<SC2Action>();
            if (liveWorkers == null || liveWorkers.Count == 0)
            {
                return actions;
            }

            var defaultMinerals = _mapData?.OrderedMainMinerals?.ElementAtOrDefault(startIndex)
                ?.Where(mineral => mineral != null
                    && mineral.UnitTag != 0
                    && IsCurrentSpawnPoint(mineral.Position, _mapData?.StartingTownHall?.ElementAtOrDefault(startIndex)))
                .ToList() ?? new List<OrderedMineral>();

            for (var workerIndex = 0; workerIndex < liveWorkers.Count; workerIndex++)
            {
                var worker = liveWorkers[workerIndex];
                var carrying = worker.BuffIds.Any(b => b == 271 || b == 272);
                _previousCarryingState.TryGetValue(worker.Tag, out var wasCarrying);

                if (!carrying)
                {
                    _cargoReturnSequenceActive.Remove(worker.Tag);
                    _pendingCargoReturnSequences.Remove(worker.Tag);
                }

                if (carrying
                    && _pendingCargoReturnSequences.TryGetValue(worker.Tag, out var pendingSequence)
                    && pendingSequence.CreatedFrame < _currentFrame)
                {
                    IssueQueuedCargoReturnContinuation(actions, worker.Tag, pendingSequence);
                    Console.WriteLine($"[MINING CARGO] frame={_currentFrame} worker={worker.Tag} observedWorkers={liveWorkers.Count} configuredWorkers={Settings.WorkerCount} townhall={pendingSequence.TownhallTag} stage=MOVE queued=false; stage=HARVEST_RETURN queued=true");
                    _pendingCargoReturnSequences.Remove(worker.Tag);
                    _previousCarryingState[worker.Tag] = carrying;
                    continue;
                }

                if (carrying && !wasCarrying && townhallUnit != null && !_cargoReturnSequenceActive.Contains(worker.Tag))
                {
                    var assignment = teamAssignments
                        .FirstOrDefault(team => team?.Workers?.Any(candidate => candidate.UnitTag == worker.Tag) == true);
                    if (assignment == null)
                    {
                        continue;
                    }

                    var state = _jitWorkerStates.TryGetValue(worker.Tag, out var jitState) ? jitState : null;
                    var assignedMineral = assignment.Workers.Count >= 3 || state == null
                        ? null
                        : ResolveMineralForWorker(assignment, state);
                    var returnPoint = assignment.Workers.Count >= 3
                        ? assignment.JitReturnPoint
                        : assignedMineral?.ReturnPoint;

                    if (returnPoint != null && HasNonZeroPoint(returnPoint) && IssueCargoReturnSequence(actions, worker.Tag, returnPoint, townhallUnit.Tag))
                    {
                        _cargoReturnSequenceActive.Add(worker.Tag);
                        Console.WriteLine($"[MINING CARGO] frame={_currentFrame} worker={worker.Tag} observedWorkers={liveWorkers.Count} configuredWorkers={Settings.WorkerCount} townhall={townhallUnit.Tag} stage=STOP queued=false; stage=MOVE queued=true");
                        if (!_cargoReturnDebugBreakTriggered)
                        {
                            _cargoReturnDebugBreakTriggered = true;
                            System.Diagnostics.Debugger.Break();
                        }
                        _pendingCargoReturnSequences[worker.Tag] = new CargoReturnSequenceState
                        {
                            ReturnPoint = returnPoint,
                            TownhallTag = townhallUnit.Tag,
                            CreatedFrame = _currentFrame
                        };
                    }
                }

                _previousCarryingState[worker.Tag] = carrying;
            }

            return actions;
        }

        private List<SC2Action> ExecuteDefaultSpeedMining(
            List<SnapshotUnit> liveWorkers,
            SnapshotUnit? townhallUnit,
            int startIndex)
        {
            var actions = new List<SC2Action>();
            var minerals = _mapData?.OrderedMainMinerals?.ElementAtOrDefault(startIndex)
                ?.Where(mineral => mineral != null
                    && mineral.UnitTag != 0
                    && IsCurrentSpawnPoint(mineral.Position, _mapData?.StartingTownHall?.ElementAtOrDefault(startIndex)))
                .ToList() ?? new List<OrderedMineral>();

            if (minerals.Count == 0)
            {
                return actions;
            }

            for (var workerIndex = 0; workerIndex < liveWorkers.Count; workerIndex++)
            {
                var worker = liveWorkers[workerIndex];
                var mineral = ResolveDefaultSpeedMiningMineral(worker.Tag, minerals, workerIndex);
                if (mineral == null) continue;

                var carrying = worker.BuffIds.Any(b => b == 271 || b == 272);
                _previousCarryingState.TryGetValue(worker.Tag, out var wasCarrying);

                if (!carrying)
                {
                    _cargoReturnSequenceActive.Remove(worker.Tag);
                    _pendingCargoReturnSequences.Remove(worker.Tag);
                }

                if (carrying
                    && _pendingCargoReturnSequences.TryGetValue(worker.Tag, out var pendingSequence)
                    && pendingSequence.CreatedFrame < _currentFrame)
                {
                    IssueQueuedCargoReturnContinuation(actions, worker.Tag, pendingSequence);
                    Console.WriteLine($"[MINING CARGO] frame={_currentFrame} worker={worker.Tag} observedWorkers={liveWorkers.Count} configuredWorkers={Settings.WorkerCount} townhall={pendingSequence.TownhallTag} stage=MOVE queued=false; stage=HARVEST_RETURN queued=true");
                    _pendingCargoReturnSequences.Remove(worker.Tag);
                    _previousCarryingState[worker.Tag] = carrying;
                    continue;
                }

                if (carrying && !wasCarrying && townhallUnit != null && !_cargoReturnSequenceActive.Contains(worker.Tag))
                {
                    var returnPoint = ResolveNonZeroPoint(mineral.SmReturnPoint, mineral.ReturnPoint);
                    if (returnPoint != null && IssueCargoReturnSequence(actions, worker.Tag, returnPoint, townhallUnit.Tag))
                    {
                        _cargoReturnSequenceActive.Add(worker.Tag);
                        Console.WriteLine($"[MINING CARGO] frame={_currentFrame} worker={worker.Tag} observedWorkers={liveWorkers.Count} configuredWorkers={Settings.WorkerCount} townhall={townhallUnit.Tag} stage=STOP queued=false; stage=MOVE queued=true");
                        if (!_cargoReturnDebugBreakTriggered)
                        {
                            _cargoReturnDebugBreakTriggered = true;
                            System.Diagnostics.Debugger.Break();
                        }
                        _pendingCargoReturnSequences[worker.Tag] = new CargoReturnSequenceState
                        {
                            ReturnPoint = returnPoint,
                            TownhallTag = townhallUnit.Tag,
                            CreatedFrame = _currentFrame
                        };
                        _previousCarryingState[worker.Tag] = carrying;
                        continue;
                    }
                }

                _previousCarryingState[worker.Tag] = carrying;
                if (carrying && _cargoReturnSequenceActive.Contains(worker.Tag))
                {
                    continue;
                }

                if (carrying && townhallUnit != null)
                {
                    var returnPoint = ResolveNonZeroPoint(mineral.SmReturnPoint, mineral.ReturnPoint);
                    if (returnPoint != null)
                    {
                        AddAction(actions, IssueMoveToPoint(worker, returnPoint));
                    }
                    continue;
                }

                if (!carrying && mineral.HarvestPoint != null)
                {
                    var harvestPointDto = ResolveNonZeroPoint(mineral.SmHarvestPoint, mineral.HarvestPoint);
                    if (harvestPointDto == null)
                    {
                        continue;
                    }

                    var harvestPoint = new Point2D { X = harvestPointDto.X, Y = harvestPointDto.Y };
                    if (Distance(worker.Pos.ToPoint2D(), harvestPoint) < 0.15f)
                    {
                        actions.Add(new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.SMART, UnitTags = { worker.Tag }, TargetUnitTag = mineral.UnitTag } } });
                    }
                    else
                    {
                        AddAction(actions, IssueMoveToPoint(worker, new Vector2Dto(harvestPoint.X, harvestPoint.Y)));
                    }
                }
            }

            return actions;
        }

        private OrderedMineral? ResolveDefaultSpeedMiningMineral(ulong workerTag, List<OrderedMineral> minerals, int workerIndex)
        {
            if (_defaultSpeedMiningMineralByWorker.TryGetValue(workerTag, out var mineralTag))
            {
                var existing = minerals.FirstOrDefault(mineral => mineral.UnitTag == mineralTag);
                if (existing != null) return existing;
            }

            var selected = minerals[workerIndex % minerals.Count];
            _defaultSpeedMiningMineralByWorker[workerTag] = selected.UnitTag;
            return selected;
        }

        private Vector2Dto? ResolveCargoReturnPoint(
            TeamPatchAssignmentDto assignment,
            JitWorkerState? state,
            OrderedMineral? pinkMineral,
            bool isJitTeam)
        {
                    if (pinkMineral != null && pinkMineral.UnitTag != 0 && HasNonZeroPoint(pinkMineral.HarvestPoint) && HasNonZeroPoint(pinkMineral.ReturnPoint))

            {
                var pinkReturnPoint = ResolveNonZeroPoint(pinkMineral.SmReturnPoint, pinkMineral.ReturnPoint);
                if (pinkReturnPoint != null)
                {
                    return pinkReturnPoint;
                }
            }

            if (isJitTeam && assignment != null)
            {
                var jitReturnPoint = ResolveNonZeroPoint(assignment.JitReturnPoint, null);
                if (jitReturnPoint != null)
                {
                    return jitReturnPoint;
                }
            }

            var speedMiningMineral = state == null ? null : ResolveMineralForWorker(assignment, state);
            return speedMiningMineral == null || speedMiningMineral.UnitTag == 0
                ? null
                : ResolveNonZeroPoint(speedMiningMineral.SmReturnPoint, speedMiningMineral.ReturnPoint);
        }

        private static bool IssueCargoReturnSequence(
            List<SC2Action> actions,
            ulong workerTag,
            Vector2Dto returnPoint,
            ulong townhallTag)
        {
            if (actions == null || workerTag == 0 || townhallTag == 0 || returnPoint == null)
            {
                return false;
            }

            // GameConnection/MicroManager allow only one unqueued action per worker per frame.
            // Send STOP now and queue MOVE; the next frame sends MOVE followed by queued SMART.
            actions.Add(new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.STOP,
                        UnitTags = { workerTag },
                        QueueCommand = false
                    }
                }
            });
            actions.Add(new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.MOVE,
                        UnitTags = { workerTag },
                        TargetWorldSpacePos = new Point2D { X = returnPoint.X, Y = returnPoint.Y },
                        QueueCommand = true
                    }
                }
            });
            return true;
        }

        private static void IssueQueuedCargoReturnContinuation(
            List<SC2Action> actions,
            ulong workerTag,
            CargoReturnSequenceState sequence)
        {
            actions.Add(new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.MOVE,
                        UnitTags = { workerTag },
                        TargetWorldSpacePos = new Point2D { X = sequence.ReturnPoint.X, Y = sequence.ReturnPoint.Y },
                        QueueCommand = false
                    }
                }
            });
            actions.Add(new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.HARVEST_RETURN,
                        UnitTags = { workerTag },
                        QueueCommand = true
                    }
                }
            });
        }

        private OrderedMineral? ResolveMineralForWorker(TeamPatchAssignmentDto assignment, JitWorkerState state)
        {
            if (assignment == null || state == null) return null;

            if (state.CurrentMineralTag == 0)
            {
                return null;
            }

            return assignment.Minerals.FirstOrDefault(mineral =>
                mineral.UnitTag != 0
                && mineral.UnitTag == state.CurrentMineralTag);
        }

        private void OnWorkerCargoReturned(ulong workerTag)
        {
            if (!_jitWorkerStates.TryGetValue(workerTag, out var state)) return;

            // Swap A/B
            var tempTag = state.CurrentMineralTag;
            state.CurrentMineralTag = state.AlternateMineralTag;
            state.AlternateMineralTag = tempTag;

            var tempPos = state.CurrentMineralPos;
            state.CurrentMineralPos = state.AlternateMineralPos;
            state.AlternateMineralPos = tempPos;
        }

        private List<TeamPatchAssignmentDto> ResolveTeamAssignments(int startIndex) => OngoingMapData.ResolveTeamAssignments(_mapData, startIndex);

        private List<SnapshotUnit> ResolveCurrentWorkersForTeamRaw(List<SnapshotUnit> allWorkers, List<WorkerEntryDto> teamAssignments)
        {
            var result = new List<SnapshotUnit>();
            if (allWorkers == null || teamAssignments == null) return result;
            foreach (var assignment in teamAssignments)
            {
                if (assignment.UnitTag != 0)
                {
                    var workerByTag = allWorkers.FirstOrDefault(u => u != null && u.Tag == assignment.UnitTag);
                    if (workerByTag != null)
                    {
                        result.Add(workerByTag);
                        continue;
                    }
                }


            }
            return result;
        }

        private Dictionary<ulong, string> BuildWorkerFinalLabelMap(IEnumerable<WorkerEntryDto>? storedWorkers)
        {
            var result = new Dictionary<ulong, string>();
            foreach (var worker in storedWorkers ?? Enumerable.Empty<WorkerEntryDto>())
            {
                if (worker != null && worker.UnitTag != 0 && !string.IsNullOrWhiteSpace(worker.FinalLabel)) result[worker.UnitTag] = worker.FinalLabel;
            }
            return result;
        }

        private string ResolveWorkerFinalLabelByTag(ulong tag, IReadOnlyDictionary<ulong, string> finalLabelByTag) => (finalLabelByTag != null && finalLabelByTag.TryGetValue(tag, out var label)) ? label : string.Empty;

        private string GetWorkerFinalLabel(Unit unit) => _workerLabelService?.GetLabel(unit?.Tag ?? 0) ?? string.Empty;

        private string GetTeamPrefix(int teamNumber)
        {
            return teamNumber switch { 1 => "T", 2 => "S", 3 => "B", 4 => "Y", _ => string.Empty };
        }

        private SC2Action? IssueStop(SnapshotUnit worker)
        {
            if (worker?.Tag == 0) return null;
            return new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.STOP,
                        UnitTags = { worker.Tag },
                        QueueCommand = false
                    }
                }
            };
        }

        private SC2Action? IssueMoveToPoint(SnapshotUnit worker, Vector2Dto point, bool queued)
        {
            if (worker?.Tag == 0 || point == null) return null;
            return new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.MOVE,
                        UnitTags = { worker.Tag },
                        TargetWorldSpacePos = new Point2D { X = point.X, Y = point.Y },
                        QueueCommand = queued
                    }
                }
            };
        }

        private static SC2Action? IssueHarvestReturn(ulong workerTag, bool queued)
        {
            if (workerTag == 0)
            {
                return null;
            }

            return new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.HARVEST_RETURN,
                        UnitTags = { workerTag },
                        QueueCommand = queued
                    }
                }
            };
        }

        private SC2Action? IssueSmart(SnapshotUnit worker, ulong targetTag, bool queued)

        {
            if (worker?.Tag == 0 || targetTag == 0) return null;
            return new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.SMART,
                        UnitTags = { worker.Tag },
                        TargetUnitTag = targetTag,
                        QueueCommand = queued
                    }
                }
            };
        }

        private SC2Action? IssueMoveToPoint(SnapshotUnit worker, Vector2Dto point)
        {
            return IssueMoveToPoint(worker, point, false);
        }

        private void LogMiningCommands(IEnumerable<SC2Action> actions)
        {
            foreach (var action in actions ?? Enumerable.Empty<SC2Action>())
            {
                var command = action?.ActionRaw?.UnitCommand;
                if (command == null || command.UnitTags == null || command.UnitTags.Count == 0)
                {
                    continue;
                }

                var commandName = command.AbilityId switch
                {
                    (int)Abilities.MOVE => "MOVE",
                    (int)Abilities.HARVEST_GATHER => "HARVEST_GATHER",
                    (int)Abilities.HARVEST_RETURN => "HARVEST_RETURN",
                    (int)Abilities.SMART => "SMART",
                    _ => string.Empty
                };
                if (string.IsNullOrEmpty(commandName))
                {
                    continue;
                }

                var target = command.TargetWorldSpacePos == null
                    ? string.Empty
                    : $" pos=({command.TargetWorldSpacePos.X:F2},{command.TargetWorldSpacePos.Y:F2})";
                var targetTag = command.TargetUnitTag != 0 ? $" targetTag={command.TargetUnitTag}" : string.Empty;
                var queued = command.QueueCommand ? " queued=true" : " queued=false";
                foreach (var workerTag in command.UnitTags)
                {
                    var workerLabel = _workerLabelService?.GetLabel(workerTag) ?? string.Empty;
                    Console.WriteLine($"[MINING COMMAND1] frame={_currentFrame} worker={workerTag} Label={workerLabel} command={commandName}{target}{targetTag}{queued}");
                }
            }
        }

        private static void AddAction(List<SC2Action> actions, SC2Action? action) { if (action != null) actions.Add(action); }

        private ulong ResolveMineralTag(Vector2Dto position)
        {
            if (Globals.CurrentObservation?.Minerals == null || position == null) return 0;
            var nearest = Globals.CurrentObservation.Minerals.Values
                .Where(mineral => mineral != null && mineral.Position != null && mineral.UnitTag != 0)
                .Select(mineral => new { Mineral = mineral, Distance = Math.Pow(mineral.Position.X - position.X, 2) + Math.Pow(mineral.Position.Y - position.Y, 2) })
                .OrderBy(value => value.Distance)
                .FirstOrDefault();
            return nearest == null || nearest.Distance >= 4 ? 0 : nearest.Mineral.UnitTag;
        }

        private void SynchronizeVisibleKnownMinerals(int startIndex)
        {
            if (_mapData == null
                || Globals.CurrentObservation?.Minerals == null
                || startIndex < 0
                || _mapData.StartingTownHall == null
                || startIndex >= _mapData.StartingTownHall.Length)
            {
                return;
            }

            var townhall = _mapData.StartingTownHall[startIndex];
            if (townhall == null)
            {
                return;
            }

            var visibleMinerals = Globals.CurrentObservation.Minerals.Values
                .Where(mineral => mineral != null && mineral.Position != null && mineral.UnitTag != 0)
                .Select(mineral => new Unit { Tag = mineral.UnitTag, UnitType = mineral.UnitType, Pos = new Point { X = mineral.Position.X, Y = mineral.Position.Y, Z = mineral.Position.Z } })
                .ToList();
            var knownMinerals = _mapData.OrderedMainMinerals?.ElementAtOrDefault(startIndex)
                ?.Where(mineral => mineral?.Position != null)
                .ToList() ?? new List<OrderedMineral>();

            var visibleNearHatchery = visibleMinerals
                .Where(unit => DistanceSquared(new Vector2Dto(unit.Pos.X, unit.Pos.Y), townhall) <= 25f)
                .ToList();
            var matchedCount = 0;

            foreach (var knownMineral in knownMinerals)
            {
                var match = visibleMinerals
                    .Select(unit => new
                    {
                        Unit = unit,
                        Distance = DistanceSquared(
                            new Vector2Dto(unit.Pos.X, unit.Pos.Y),
                            knownMineral.Position)
                    })
                    .Where(candidate => candidate.Distance <= 0.25f)
                    .OrderBy(candidate => candidate.Distance)
                    .FirstOrDefault();

                if (match == null)
                {
                    continue;
                }

                knownMineral.UnitTag = match.Unit.Tag;
                matchedCount++;
            }

            var currentAssignments = ResolveTeamAssignments(startIndex);
            foreach (var assignment in currentAssignments)
            {
                foreach (var assignedMineral in assignment?.Minerals ?? new List<OrderedMineral>())
                {
                    var knownMatch = knownMinerals.FirstOrDefault(mineral =>
                        mineral.Position != null
                        && assignedMineral?.Position != null
                        && DistanceSquared(mineral.Position, assignedMineral.Position) <= 0.01f);
                    if (knownMatch != null)
                    {
                        assignedMineral.UnitTag = knownMatch.UnitTag;
                    }
                }
            }

            if (_currentFrame % 25 == 0)
            {
                Console.WriteLine($"[MINERAL SYNC] frame={_currentFrame} startIndex={startIndex} visible={visibleMinerals.Count} visibleWithin5={visibleNearHatchery.Count} known={knownMinerals.Count} matched={matchedCount} assignments={currentAssignments.Count} hatchery=({townhall.X:F2},{townhall.Y:F2})");
            }
        }

        private void RefreshBuildPlanLiveTags(
            List<TeamPatchAssignmentDto> assignments,
            List<SnapshotUnit> liveWorkers,
            int startIndex)
        {
            if (assignments == null || liveWorkers == null || Globals.CurrentObservation?.Minerals == null)
            {
                return;
            }

            var visibleMinerals = Globals.CurrentObservation.Minerals.Values
                .Where(mineral => mineral != null && mineral.UnitTag != 0 && mineral.Position != null)
                .Select(mineral => new Unit
                {
                    Tag = mineral.UnitTag,
                    UnitType = mineral.UnitType,
                    Pos = new Point { X = mineral.Position.X, Y = mineral.Position.Y, Z = mineral.Position.Z }
                })
                .ToList();
            var currentTownhall = _mapData?.StartingTownHall?.ElementAtOrDefault(startIndex);
            var visibleByPosition = visibleMinerals
                .Where(unit => currentTownhall == null
                    || DistanceSquared(new Vector2Dto(unit.Pos.X, unit.Pos.Y), currentTownhall) <= 400f)
                .ToList();

            var assignedWorkerTags = new HashSet<ulong>();
            var assignedMineralTags = new HashSet<ulong>();
            var refreshedMinerals = 0;
            var refreshedWorkers = 0;

            foreach (var assignment in assignments)
            {
                foreach (var workerPlan in assignment?.Workers ?? new List<WorkerEntryDto>())
                {
                    var liveWorker = ResolveLiveWorkerForPlan(workerPlan, liveWorkers, assignedWorkerTags);
                    if (liveWorker == null)
                    {
                        continue;
                    }

                    assignedWorkerTags.Add(liveWorker.Tag);
                    workerPlan.UnitTag = liveWorker.Tag;
                    workerPlan.Position = new Vector2Dto(liveWorker.Pos.X, liveWorker.Pos.Y, liveWorker.Pos.Z);
                    workerPlan.UnitType = liveWorker.UnitType;
                    workerPlan.IsCarrying = liveWorker.BuffIds.Any(b => b == 271 || b == 272);
                    refreshedWorkers++;
                }

                foreach (var mineralPlan in assignment?.Minerals ?? new List<OrderedMineral>())
                {
                    if (mineralPlan?.Position == null)
                    {
                        continue;
                    }

                    var liveMineral = visibleByPosition
                        .Where(unit => !assignedMineralTags.Contains(unit.Tag))
                        .Select(unit => new
                        {
                            Unit = unit,
                            Distance = DistanceSquared(new Vector2Dto(unit.Pos.X, unit.Pos.Y), mineralPlan.Position)
                        })
                        .Where(candidate => candidate.Distance <= 0.25f)
                        .OrderBy(candidate => candidate.Distance)
                        .FirstOrDefault();
                    if (liveMineral == null)
                    {
                        mineralPlan.UnitTag = 0;
                        continue;
                    }

                    assignedMineralTags.Add(liveMineral.Unit.Tag);
                    mineralPlan.UnitTag = liveMineral.Unit.Tag;
                    refreshedMinerals++;
                }
            }

            if (_currentFrame % 25 == 0)
            {
                Console.WriteLine($"[MINING LIVE TAGS] frame={_currentFrame} startIndex={startIndex} workers={refreshedWorkers}/{liveWorkers.Count} minerals={refreshedMinerals} visibleMinerals={visibleByPosition.Count} assignments={assignments.Count}");
            }
        }

        private SnapshotUnit? ResolveLiveWorkerForPlan(
            WorkerEntryDto workerPlan,
            List<SnapshotUnit> liveWorkers,
            HashSet<ulong> assignedWorkerTags)
        {
            if (workerPlan == null)
            {
                return null;
            }

            var liveWorker = workerPlan.UnitTag != 0
                ? liveWorkers.FirstOrDefault(worker => worker.Tag == workerPlan.UnitTag)
                : null;
            return liveWorker != null && !assignedWorkerTags.Contains(liveWorker.Tag)
                ? liveWorker
                : null;
        }

        private void UpdateScoutedMinerals(ResponseObservation observation)
        {
            var snapshot = Globals.CurrentObservation;
            if (_mapData?.Minerals == null || _mapData.MineralTagToIndex == null || snapshot == null) return;
            
            foreach (var mineralDto in snapshot.Minerals.Values)
            {
                try
                {
                    if (mineralDto.UnitTag == 0 || !_mapData.MineralTagToIndex.TryGetValue(mineralDto.UnitTag, out var idx)) continue;
                    var mineral = _mapData.Minerals[idx];
                    var contents = mineralDto.MineralContents;
                    if (contents != mineral.MaxMineralContents) _mapData.MismatchedMinerals = true;
                    if (contents > mineral.MaxMineralContents) mineral.MaxMineralContents = contents;
                    if (contents > mineral.MineralContents) mineral.MineralContents = contents;
                    mineral.UnitTag = mineralDto.UnitTag;
                    mineral.UnitType = mineralDto.UnitType;
                    if (mineral.Position == null) mineral.Position = mineralDto.Position;
                }
                catch (Exception ex) { Console.WriteLine($"Error in UpdateScoutedMinerals: {ex.Message}"); }
            }
        }

        private bool IsMineralType(UnitTypes unitType)
        {
            return unitType == UnitTypes.NEUTRAL_MINERALFIELD || unitType == UnitTypes.NEUTRAL_MINERALFIELD750 || unitType == UnitTypes.NEUTRAL_RICHMINERALFIELD || unitType == UnitTypes.NEUTRAL_RICHMINERALFIELD750 || unitType == UnitTypes.NEUTRAL_PURIFIERMINERALFIELD || unitType == UnitTypes.NEUTRAL_PURIFIERMINERALFIELD750 || unitType == UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD || unitType == UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD750 || unitType == UnitTypes.NEUTRAL_LABMINERALFIELD || unitType == UnitTypes.NEUTRAL_LABMINERALFIELD750 || unitType == UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD || unitType == UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD750;
        }

        private void UpdateMineralReturnRate(ResponseObservation observation)
        {
            if (_mineralReturnRateTrackerService == null) return;

            var scoreDetails = observation?.Observation?.Score?.ScoreDetails;
            if (scoreDetails == null)
            {
                // Diagnostic log
                if (_currentFrame % 100 == 0) Console.WriteLine($"DEBUG: ScoreDetails null at frame {_currentFrame}");
                return;
            }

            var snapshot = Globals.CurrentObservation;
            if (snapshot == null) return;

            var droneCount = snapshot.SelfUnits.Values.Count(u => u != null && u.UnitType == (uint)UnitTypes.ZERG_DRONE && u.IsCompleted);
            
            if (_lastFunctionalDroneCount != -1 && droneCount > _lastFunctionalDroneCount)
            {
                Console.WriteLine($"[MILESTONE] Worker morph complete. New functional drone count: {droneCount} at frame {_currentFrame}");
                Console.WriteLine($"Mineral Return Rate Summary: {_mineralReturnRateTrackerService.GetSummary()}");
                //System.Diagnostics.Debugger.Break();
            }
            _lastFunctionalDroneCount = droneCount;

            // Total Collected = Bank + Total Spent (summed across categories)
            var bank = observation.Observation.PlayerCommon.Minerals;
            var spent = scoreDetails.TotalUsedMinerals.None + scoreDetails.TotalUsedMinerals.Army + scoreDetails.TotalUsedMinerals.Economy + scoreDetails.TotalUsedMinerals.Technology + scoreDetails.TotalUsedMinerals.Upgrade;
            var totalCollected = bank + spent;

            float deltaCollected = 0;
            if (_lastTotalCollected >= 0)
            {
                deltaCollected = totalCollected - _lastTotalCollected;
            }
            _lastTotalCollected = totalCollected;

            if (droneCount >= 8 && droneCount <= 16)
            {
                var COLLECTION_RATE = scoreDetails.CollectionRateMinerals;
                // Diagnostic log
                if (_currentFrame % 100 == 0) Console.WriteLine($"DEBUG: frame={_currentFrame} drones={droneCount} rate={COLLECTION_RATE} total={totalCollected}");
                
                _mineralReturnRateTrackerService.Record(droneCount, COLLECTION_RATE, deltaCollected);
            }
        }

        private void PrintTwelveDroneMilestone(ResponseObservation observation)
        {
            var snapshot = Globals.CurrentObservation;
            if (_printedTwelveDroneMilestone || snapshot == null) return;
            if (snapshot.SelfUnits.Values.Count(u => u != null && u.UnitType == (uint)UnitTypes.ZERG_DRONE && u.IsCompleted) >= 12)
            {
                _printedTwelveDroneMilestone = true;
                Console.WriteLine($"BabySharkMiningManager: 12-drone milestone reached at frame {_currentFrame}");
            }
        }

        private void PrintMineralReturnRateSummary(ResponseObservation observation)
        {
            // Diagnostic heartbeat - every 100 frames to avoid spamming too much
            if (_currentFrame % 100 == 0)
            {
                Console.WriteLine($"DEBUG: frame={_currentFrame} tracker={_mineralReturnRateTrackerService != null} lastPrint={_lastMineralReturnRateConsoleFrame}");
            }

            if (_mineralReturnRateTrackerService != null && _currentFrame - _lastMineralReturnRateConsoleFrame >= 50) // Reduced from 500
            {
                _lastMineralReturnRateConsoleFrame = _currentFrame;
                Console.WriteLine($"Mineral Return Rate Summary: {_mineralReturnRateTrackerService.GetSummary()}");
            }
        }

        private void BreakWhenSpawnLabelsShouldBeVisible(ResponseObservation observation)
        {
            if (_spawnLabelDebugBreakTriggered || observation?.Observation == null || _currentFrame <= 20) return;
            if (_workerLabelService.GetAllLabels().Any(l => l.Key.StartsWith("T") || l.Key.StartsWith("S") || l.Key.StartsWith("B") || l.Key.StartsWith("Y") || l.Key.StartsWith("G") || l.Key.StartsWith("P") || l.Key.StartsWith("O") || l.Key.StartsWith("R")))
            {
                _spawnLabelDebugBreakTriggered = true;
                Console.WriteLine($"BabySharkMiningManager: Spawn labels detected at frame {_currentFrame}.");
            }
        }

        private void DrawCenterOfMass()
        {
            if (_mapData != null) foreach (var com in _mapData.MineralCenterOfMass) { if (com == null) continue; DrawCircle(com, 0.5f, new Color { R = 0, G = 255, B = 0 }, 12f); }
        }

        private void DrawMineralLabels()
        {
            if (_mineralLabelService == null || _mapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedMinerals = _mapData.TeamPatchAssignments[startIndex]
                .SelectMany(assignment => assignment.Minerals)
                .OrderBy(mineral => mineral.Index)
                .ToList();

            foreach (var mineral in assignedMinerals)
            {
                ManagerDebugService.DrawText(mineral.Label, new Point
                {
                    X = mineral.Position.X,
                    Y = mineral.Position.Y,
                    Z = mineral.Position.Z + 0.5f
                }, ProcessVisableUnits.GetFinalLabelColor(mineral.FinalLabel), 10);
            }
        }

        private void DrawExpansionMineralLabels()
        {
            if (_mapData?.ExpansionMineralLabels != null) 
            {
                foreach (var kvp in _mapData.ExpansionMineralLabels) 
                { 
                    try
                    {
                        var pos = ParsePoint(kvp.Key); 
                        ManagerDebugService.DrawText(kvp.Value, new Point { X = pos.X, Y = pos.Y, Z = 12.5f }, new Color { R = 255, G = 255, B = 255 }, 10); 
                    }
                    catch { }
                }
            }
        }

        private void DrawVespeneLabels()
        {
            if (_vespeneLabelService != null) foreach (var kvp in _vespeneLabelService.GetAllVespeneLabels()) ManagerDebugService.DrawText(kvp.Value.Label, new Point { X = kvp.Value.Position.X, Y = kvp.Value.Position.Y, Z = kvp.Value.Position.Z + 0.5f }, kvp.Value.Color, 10);
        }

        private void DrawExpansionPoints()
        {
            if (_mapData?.ExpansionPoints != null) foreach (var kvp in _mapData.ExpansionPoints) { if (kvp.Value?.ExpansionPoint == null) continue; DrawCircle(kvp.Value.ExpansionPoint, 2.5f, new Color { R = 0, G = 0, B = 255 }, 12f); ManagerDebugService.DrawText($"Exp {kvp.Key}", new Point { X = kvp.Value.ExpansionPoint.X, Y = kvp.Value.ExpansionPoint.Y, Z = 13f }, new Color { R = 255, G = 255, B = 255 }, 12); }
        }

        private void DrawSpawningPoolPlacement()
        {
            if (_mapData?.SpawningPoolPlacements != null) foreach (var pos in _mapData.SpawningPoolPlacements) { if (pos == null) continue; DrawCircle(pos, 1.5f, new Color { R = 255, G = 0, B = 0 }, 12f); ManagerDebugService.DrawText("Pool", new Point { X = pos.X, Y = pos.Y, Z = 12.5f }, new Color { R = 255, G = 0, B = 0 }, 10); }
        }

        private Point2D ParsePoint(string key) { var parts = key.Split(','); return new Point2D { X = float.Parse(parts[0]), Y = float.Parse(parts[1]) }; }

        private Dictionary<string, string> BuildWorkerLabelFallbackMap(List<WorkerEntryDto>? storedWorkers)
        {
            var result = new Dictionary<string, string>();
            foreach (var worker in storedWorkers ?? Enumerable.Empty<WorkerEntryDto>())
            {
                if (worker?.Position != null && !string.IsNullOrWhiteSpace(worker.FinalLabel)) result[$"{(float)Math.Round(worker.Position.X, 1)},{(float)Math.Round(worker.Position.Y, 1)}"] = worker.FinalLabel;
            }
            return result;
        }

        private string ResolveWorkerLabelByPosition(float x, float y, Dictionary<string, string> fallbackByPosition)
        {
            if (fallbackByPosition == null || fallbackByPosition.Count == 0) return string.Empty;
            var nearest = fallbackByPosition.Select(kvp => new { Label = kvp.Value, Distance = ParseDistanceSquared(kvp.Key, x, y) }).OrderBy(v => v.Distance).FirstOrDefault();
            return nearest != null && nearest.Distance < 4f ? nearest.Label : string.Empty;
        }

        private static float ParseDistanceSquared(string positionKey, float x, float y) { var parts = positionKey.Split(','); if (parts.Length < 2 || !float.TryParse(parts[0], out var px) || !float.TryParse(parts[1], out var py)) return float.MaxValue; var dx = px - x; var dy = py - y; return dx * dx + dy * dy; }

        private bool GetWorkerLabelOrderingCompleted(int startIndex) => (startIndex >= 0 && GetStoredWorkersForStart(startIndex)?.Any(w => !string.IsNullOrWhiteSpace(w?.FinalLabel)) == true);

        private static bool IsLegacyWorkerLabelForDebugBreak(string label) => string.Equals(label, "W12", StringComparison.OrdinalIgnoreCase) || label.StartsWith("W", StringComparison.OrdinalIgnoreCase);

        private int GetActiveStartIndex()
        {
            if (_mapData?.StartingTownHall == null || Globals.CurrentObservation?.CurrentTownHalls == null) return -1;
            foreach (var townhall in Globals.CurrentObservation.CurrentTownHalls.Values)
            {
                if (townhall?.Position == null) continue;
                for (var i = 0; i < _mapData.StartingTownHall.Length; i++)
                {
                    if (Math.Abs(townhall.Position.X - _mapData.StartingTownHall[i].X) < 1.0f
                        && Math.Abs(townhall.Position.Y - _mapData.StartingTownHall[i].Y) < 1.0f)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private List<WorkerEntryDto>? GetStoredWorkersForStart(int startIndex) => (startIndex >= 0 && _mapData?.StartingUnits != null && startIndex < _mapData.StartingUnits.Count) ? _mapData.StartingUnits[startIndex] : null;

        private void DrawArrow(Point start, Point end, Color color) { ManagerDebugService.DrawLine(start, end, color); }

        private (Vector2Dto ReturnPoint, Vector2Dto WaitPoint) CalculateJitPoints(MineralNode mA, MineralNode mB, Vector2Dto townhall)
        {
            if (mA?.Position == null || mB?.Position == null || townhall == null) return (new Vector2Dto(), new Vector2Dto());
            var avgX = (mA.Position.X + mB.Position.X) * 0.5f;
            var avgY = (mA.Position.Y + mB.Position.Y) * 0.5f;
            var dirX = avgX - townhall.X;
            var dirY = avgY - townhall.Y;
            var mag = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
            var returnX = townhall.X + (dirX / mag) * 2.8f;
            var returnY = townhall.Y + (dirY / mag) * 2.8f;
            var waitX = returnX + (dirX / mag) * 1.5f;
            var waitY = returnY + (dirY / mag) * 1.5f;
            return (new Vector2Dto(returnX, returnY, townhall.Z), new Vector2Dto(waitX, waitY, townhall.Z));
        }

        public void InitializeExpansionMining(Point2D expansionPosition, List<Unit> minerals)
        {
            var expansionKey = GetPointKey(expansionPosition);
            var mineralNodes = CreateOrderedMineralNodes(expansionPosition, minerals);
            _expansionMinerals[expansionKey] = mineralNodes;
            var teams = new List<MiningTeam>();
            var townhallDto = new Vector2Dto(expansionPosition.X, expansionPosition.Y);
            
            for (int i = 0; i < mineralNodes.Count - 1; i += 2) 
            {
                var mA = mineralNodes[i];
                var mB = mineralNodes[i + 1];
                var jitPoints = CalculateJitPoints(mA, mB, townhallDto);
                teams.Add(new MiningTeam { 
                    TeamId = $"{expansionPosition.X:F1}_{expansionPosition.Y:F1}_T{i/2 + 1}", 
                    MineralA = mA, 
                    MineralB = mB, 
                    IsJITTeam = false, 
                    ExpansionPosition = townhallDto, 
                    TeamIndex = i/2,
                    JitWaitPoint = jitPoints.WaitPoint
                });
            }
            if (mineralNodes.Count % 2 != 0) 
            {
                var mA = mineralNodes.Last();
                teams.Add(new MiningTeam { 
                    TeamId = $"{expansionPosition.X:F1}_{expansionPosition.Y:F1}_T{mineralNodes.Count/2 + 1}", 
                    MineralA = mA, 
                    MineralB = null, 
                    IsJITTeam = false, 
                    ExpansionPosition = townhallDto, 
                    TeamIndex = mineralNodes.Count/2,
                    JitWaitPoint = new Vector2Dto(mA.Position.X, mA.Position.Y) // Fallback for single mineral team
                });
            }
            _expansionTeams[expansionKey] = teams;
        }

        private string GetPointKey(Point2D point) => $"{(float)System.Math.Round(point.X, 1)},{(float)System.Math.Round(point.Y, 1)}";

        private List<MineralNode> CreateOrderedMineralNodes(Point2D expansionPosition, List<Unit> minerals)
        {
            var mineralNodes = new List<MineralNode>();
            foreach (var mineral in minerals)
            {
                var distance = Distance(expansionPosition, mineral.Pos.ToPoint2D());
                mineralNodes.Add(new MineralNode { Position = new Vector2Dto(mineral.Pos.X, mineral.Pos.Y, mineral.Pos.Z), MineralUnitTag = mineral.Tag, IsLargeMineral = IsRichMineral(mineral.UnitType), AngleFromCenter = CalculateAngleFromCenter(expansionPosition, mineral.Pos.ToPoint2D()), DistanceFromTownHall = distance, IsNearMineral = distance < 10.0f });
            }
            mineralNodes.Sort((a, b) => a.AngleFromCenter.CompareTo(b.AngleFromCenter));
            for (int i = 0; i < mineralNodes.Count; i++) mineralNodes[i].Identifier = $"M{i + 1}";
            return mineralNodes;
        }

        private bool IsRichMineral(uint unitType) => ((UnitTypes)unitType).ToString().Contains("RICH", StringComparison.OrdinalIgnoreCase);

        private float CalculateAngleFromCenter(Point2D center, Point2D point) => (float)System.Math.Atan2(point.Y - center.Y, point.X - center.X);

        private float Distance(Point2D a, Point2D b) => (float)System.Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

        private float Distance(Point2D a, Point b) => (float)System.Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

        public void AssignWorkerToJITTeam(Unit worker, Point2D expansionPosition)
        {
            var key = GetPointKey(expansionPosition);
            if (!_expansionTeams.TryGetValue(key, out var teams) || teams.Count == 0) return;
            var targetTeam = teams.OrderBy(t => t.WorkerTags.Count).First();
            if (!targetTeam.WorkerTags.Contains(worker.Tag)) { targetTeam.WorkerTags.Add(worker.Tag); _workerTeamAssignment[worker.Tag] = targetTeam.TeamId; }
            if (targetTeam.WorkerTags.Count == 3 && !targetTeam.IsJITTeam) { targetTeam.IsJITTeam = true; foreach (var tag in targetTeam.WorkerTags) if (!targetTeam.WorkerLastMinedA.ContainsKey(tag)) targetTeam.WorkerLastMinedA[tag] = false; }
        }

        private MiningTeam? FindWorkerTeam(Unit worker, string expansionKey)
        {
            if (_expansionTeams.TryGetValue(expansionKey, out var teams) && _workerTeamAssignment.TryGetValue(worker.Tag, out var teamId)) return teams.FirstOrDefault(t => t.TeamId == teamId);
            return null;
        }

        public Point2D GetJITMiningTarget(Unit worker, Point2D expansionPosition, Point2D currentMineralPosition)
        {
            if (!_jitWorkerStates.TryGetValue(worker.Tag, out var state)) return currentMineralPosition;
            
            var key = GetPointKey(expansionPosition);
            if (!_expansionTeams.TryGetValue(key, out var teams) || teams.Count == 0) return currentMineralPosition;
            var team = teams.FirstOrDefault(t => t.TeamId == state.TeamId);
            if (team == null) return currentMineralPosition;

            var mineral = (team.MineralA?.MineralUnitTag == state.CurrentMineralTag) ? team.MineralA : 
                         (team.MineralB?.MineralUnitTag == state.CurrentMineralTag) ? team.MineralB : null;

            if (mineral != null) return new Point2D { X = mineral.Position.X, Y = mineral.Position.Y };
            
            return new Point2D { X = team.JitWaitPoint.X, Y = team.JitWaitPoint.Y };
        }

        public bool IsJITTeamWorker(Unit worker) => _jitWorkerStates.ContainsKey(worker.Tag);

        public bool ShouldUseJITMining(Unit worker)
        {
            if (!_jitWorkerStates.TryGetValue(worker.Tag, out var state)) return false;
            foreach (var kvp in _expansionTeams) foreach (var team in kvp.Value) if (team.TeamId == state.TeamId) return team.IsJITTeam;
            return false;
        }

        private List<WorkerEntryDto> GetLiveWorkers(int startIndex)
        {
            return Globals.CurrentObservation?.SelfUnits.Values
                .Where(worker => worker != null && WorkerTypes.Contains((UnitTypes)worker.UnitType) && worker.UnitTag != 0)
                .ToList() ?? new List<WorkerEntryDto>();
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
            Console.WriteLine($"BabySharkMiningManager: OnEnd called with result: {result}");
        }
    }
}
