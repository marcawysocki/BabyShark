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
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
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
        private readonly CollisionCalculator _collisionCalculator;
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
        private readonly ExtractorTrickService? _extractorTrickService;
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
        private int _allMiningConsecutiveFrames;
        private const int AllMiningConfirmationFrames = 2;

        // Upper bound on instruction rows a single worker may execute within one frame.
        // Legitimate same-frame chains are short (a NoCondition store/use row followed by a
        // RelativeFrame=0 command row); this cap only guards against a Jump row cycling.
        private const int MaxInstructionsPerWorkerFrame = 16;

        private bool _cargoReturnDebugBreakTriggered = false;
        private int _lastReachabilityConsoleFrame = -999999;
        
        // JIT per-worker state (replaces MiningTeamState)
        private class JitWorkerState
        {
            public int TeamNumber { get; set; }
            public string TeamId { get; set; } = string.Empty;
            public ulong CurrentMineralTag { get; set; }
            public ulong AlternateMineralTag { get; set; }
            public Vector2Dto CurrentMineralPos { get; set; } = new Vector2Dto();
            public Vector2Dto AlternateMineralPos { get; set; } = new Vector2Dto();
        }

        private readonly Dictionary<ulong, string> _assignmentRoleByWorkerTag = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> _previousObservedPrimaryAbilityByWorkerTag = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, GatherCycleState> _gatherCycleStates = new Dictionary<ulong, GatherCycleState>();
        private readonly Dictionary<ulong, MineralHarvestTimingState> _mineralHarvestTimings = new Dictionary<ulong, MineralHarvestTimingState>();

        private sealed class GatherCycleState
        {
            public ulong ResourceTag { get; set; }
            public int StartFrame { get; set; }
        }

        private sealed class MineralHarvestTimingState
        {
            public int StartHarvestFrame { get; set; } = -1;
            public int EndHarvestFrame { get; set; } = -1;
            public int CycleFrames { get; set; }
        }

        private readonly Dictionary<ulong, JitWorkerState> _jitWorkerStates = new Dictionary<ulong, JitWorkerState>();
        private readonly object _instructionTraceLock = new object();
        private readonly JsonSerializerOptions _instructionTraceJsonOptions = new JsonSerializerOptions { WriteIndented = false };
        private readonly string _instructionTraceFile;
        private readonly Dictionary<ulong, string> _lastInstructionTraceState = new Dictionary<ulong, string>();
        private long _miningCommandSequence;
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
            CollisionCalculator? collisionCalculator = null,
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
             chrisCrossAppleSause? ccaMiningService = null,
             ExtractorTrickService? extractorTrickService = null)
        {
            _initialMapData = new InitialMapData();
            _secondaryMapData = new SecondaryMapData();
            _ongoingMapData = new OngoingMapData();
            _activeUnitData = activeUnitData;
            _sharkyUnitData = sharkyUnitData;
            _collisionCalculator = collisionCalculator ?? new CollisionCalculator();
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
            _extractorTrickService = extractorTrickService;
            _mapData = null;
            try
            {
                var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "mining_tests");
                Directory.CreateDirectory(folder);
                _instructionTraceFile = Path.Combine(folder, $"worker_instruction_lists_{DateTime.Now:yyyyMMddHHmmss}.jsonl");
            }
            catch
            {
                _instructionTraceFile = null;
            }
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
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, String opponentId)
        {
            _assignmentRoleByWorkerTag.Clear();
            _lastInstructionTraceState.Clear();
            _miningCommandSequence = 0;
            _previousObservedPrimaryAbilityByWorkerTag.Clear();
            _gatherCycleStates.Clear();
            _mineralHarvestTimings.Clear();
            _scheduledMoveReplays.Clear();
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

        private sealed class TeamPointMarker
        {
            public string Role { get; init; } = string.Empty;
            public Vector2Dto Point { get; init; } = new();
        }

        private readonly Dictionary<ulong, TeamPointMarker> _lastExplicitPointByWorker = new();

        private void DrawTeamPointDiagnostics()
        {
            if (!ManagerDebugService.IsDebugEnabled || _mapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignments = _mapData.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            var assignedWorkers = _mapData.AssignedWorkers?.ElementAtOrDefault(startIndex);
            if (assignments == null || assignedWorkers == null)
            {
                return;
            }

            foreach (var assignment in assignments)
            {
                var prefix = GetTeamPrefix(assignment?.TeamNumber ?? 0);
                if (assignment == null || string.IsNullOrWhiteSpace(prefix))
                {
                    continue;
                }

                var markers = new List<TeamPointMarker>();
                foreach (var worker in assignment.Workers ?? new List<WorkerEntryDto>())
                {
                    var role = worker?.FinalLabel ?? worker?.Label ?? string.Empty;
                    if (worker == null || string.IsNullOrWhiteSpace(role))
                    {
                        continue;
                    }

                    var runtimeWorker = Settings.RuntimeWorkers.TryGetValue(worker.UnitTag, out var state)
                        ? state
                        : null;
                    var assignedWorker = assignedWorkers.FirstOrDefault(candidate => candidate?.UnitID == worker.UnitTag);
                    var marker = ResolveFirstInstructionMarker(role, assignedWorker, runtimeWorker);
                    if (marker != null)
                    {
                        _lastExplicitPointByWorker[worker.UnitTag] = marker;
                        markers.Add(marker);
                    }
                    else if (_lastExplicitPointByWorker.TryGetValue(worker.UnitTag, out var lastMarker)
                        && runtimeWorker?.CurInstrIdx > 0)
                    {
                        markers.Add(lastMarker);
                    }
                }

                var color = TeamColorService.GetColorByPrefix(prefix);
                foreach (var group in GroupTeamPointMarkers(markers))
                {
                    ManagerDebugService.DrawText(
                        FormatTeamPointMarker(group.Select(marker => marker.Role)),
                        new Point { X = group[0].Point.X, Y = group[0].Point.Y, Z = group[0].Point.Z + 0.75f },
                        color,
                        14);
                }
            }
        }

        private TeamPointMarker? ResolveFirstInstructionMarker(
            string role,
            AssignedWorkerDto worker,
            RuntimeWorkerState runtimeWorker)
        {
            if (worker == null || runtimeWorker == null || runtimeWorker.Instructions.Count == 0)
            {
                return null;
            }

            var instruction = runtimeWorker.Instructions.ElementAtOrDefault(runtimeWorker.CurInstrIdx);
            if (instruction == null
                || (instruction.Command != WorkerInstructionCommand.StoreTargetPoint
                    && instruction.Command != WorkerInstructionCommand.UseTargetPoint)
                || !instruction.NoCondition
                || string.IsNullOrWhiteSpace(instruction.TargetPointReference))
            {
                return null;
            }

            var point = ResolveInstructionPoint(runtimeWorker, worker, instruction);
            return HasNonZeroPoint(point)
                ? new TeamPointMarker { Role = role, Point = point }
                : null;
        }

        private static IEnumerable<List<TeamPointMarker>> GroupTeamPointMarkers(List<TeamPointMarker> markers)
        {
            var groups = new List<List<TeamPointMarker>>();
            foreach (var marker in markers)
            {
                var group = groups.FirstOrDefault(existing =>
                    DistanceSquared(existing[0].Point, marker.Point) <= 0.01f);
                if (group == null)
                {
                    groups.Add(new List<TeamPointMarker> { marker });
                }
                else
                {
                    group.Add(marker);
                }
            }

            return groups;
        }

        private static string FormatTeamPointMarker(IEnumerable<string> roles)
        {
            var digits = roles
                .Where(role => role?.Length == 2 && role[1] >= '1' && role[1] <= '3')
                .Select(role => role[1])
                .Distinct()
                .OrderBy(digit => digit)
                .ToList();
            if (digits.Count >= 3)
            {
                return "#";
            }

            if (digits.Count == 2)
            {
                var pair = $"{digits[0]}{digits[1]}";
                return pair switch
                {
                    "12" => "½",
                    "13" => "⅓",
                    "23" => "⅔",
                    _ => $"{digits[0]}/{digits[1]}"
                };
            }

            return digits.Count == 1 ? digits[0].ToString() : string.Empty;
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
        private readonly Dictionary<int, List<SC2Action>> _scheduledMoveReplays = new Dictionary<int, List<SC2Action>>();

        public IEnumerable<SC2Action> OnFrame(ResponseObservation observation)
        {
            _currentFrame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            if (_currentFrame > 0 && _currentFrame % 5 == 0)
            {
               Debugger.Break();
            }

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
            WriteInstructionTrace();

            var actions = new List<SC2Action>();
            if (Settings.SimulatedStartActive || Settings.BuildOwnsWorkerCommands)
            {
                Console.WriteLine($"[MINING REACH] commands skipped frame={_currentFrame}: simulated={Settings.SimulatedStartActive} buildOwns={Settings.BuildOwnsWorkerCommands}");
                return actions;
            }

            // Update phase state (Speed Mining) based on current functional worker count.
            var workerCount = snapshot.SelfUnits.Values.Count(u => u != null && WorkerTypes.Contains((UnitTypes)u.UnitType) && u.IsCompleted);
            UpdatePhaseState(workerCount);
            var commandContexts = new List<InstructionCommandContext>();
            actions.AddRange(ExecuteRuntimeWorkerInstructions(observation, commandContexts));

            UpdateScoutedMinerals(observation);
            UpdateMineralReturnRate(observation);
            PrintMineralReturnRateSummary(observation);
            PrintTwelveDroneMilestone(observation);
            LogMiningCommands(actions, commandContexts);
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

            DrawTeamPointDiagnostics();
            DrawAllUnitLabels(observation);
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
            UpdateExtractorInstructionTransitions(currentStartIndex);
            UpdateGatherCycleStates(liveWorkers);
            RefreshBuildPlanLiveTags(buildAssignments, liveWorkers, currentStartIndex);
            var currentAssignments = ResolveBuildGreedyAssignments(currentStartIndex);
            var assignedWorkersForLabels = _mapData?.AssignedWorkers?.ElementAtOrDefault(currentStartIndex)
                ?? new List<AssignedWorkerDto>();
            foreach (var assignment in assignedWorkersForLabels)
            {
                if (assignment?.UnitID != 0 && !string.IsNullOrWhiteSpace(assignment.Role))
                {
                    _assignmentRoleByWorkerTag[assignment.UnitID] = assignment.Role;
                }
            }

            var relativeFrame = Settings.GetRelativeFrame(_currentFrame);
            
            if (relativeFrame % 100 == 0)
            {
                Console.WriteLine($"BabySharkMiningManager.ProcessFrameObservation: Frame={_currentFrame} (relative {relativeFrame}), StartIndex={currentStartIndex}, AssignmentsFound={currentAssignments.Count}");
            }

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

        private List<WorkerEntryDto> BuildCcaWorkerEntries(int startIndex, ObservationSnapshotDto snapshot)
        {
            var assignments = OngoingMapData.ResolveTeamAssignments(_mapData, startIndex);
            var workers = new List<WorkerEntryDto>();
            foreach (var assignedWorker in assignments.SelectMany(assignment => assignment.Workers ?? new List<WorkerEntryDto>()))
            {
                if (assignedWorker?.UnitTag == 0
                    || !snapshot.SelfUnits.TryGetValue(assignedWorker.UnitTag, out var liveWorker)
                    || liveWorker == null
                    || liveWorker.Position == null)
                {
                    continue;
                }

                liveWorker.Label = assignedWorker.FinalLabel;
                liveWorker.StartLabel = assignedWorker.StartLabel;
                liveWorker.FinalLabel = assignedWorker.FinalLabel;
                workers.Add(liveWorker);
            }

            return workers;
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
                            TeamId = assignment.TeamId
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

                assignedWorker.CurrentXY = observedWorker.Position;
                assignedWorker.CurrentTargetUnitID = observedWorker.TargetUnitTag;
                assignedWorker.CurrentAbilityID = (uint)(observedWorker.OrderAbilityIds?.FirstOrDefault() ?? 0);
                if (assignedWorker.MiningTargets.Count <= 1)
                {
                    assignedWorker.Mti = 0;
                    continue;
                }

                if (!Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitID, out var runtimeWorker)
                    || runtimeWorker.CurrentAbilityId != (int)Abilities.HARVEST_RETURN_DRONE
                    || runtimeWorker.PreviousAbilityId != (int)Abilities.MOVE)
                {
                    continue;
                }

                if (_extractorTrickService?.TryLoadPrepW(assignedWorker, _currentFrame) == true)
                {
                    continue;
                }

                var currentIndex = assignedWorker.Mti;
                var target = assignedWorker.MiningTargets.ElementAtOrDefault(currentIndex);
                Console.WriteLine($"[ASSIGNED TARGET] worker={assignedWorker.UnitID} mti={currentIndex} targets={assignedWorker.MiningTargets.Count} current={(target?.ToResourceLabel ?? "<none")} source=instruction-list");
            }
        }

        private void UpdateExtractorInstructionTransitions(int startIndex)
        {
            if (_extractorTrickService == null)
            {
                return;
            }

            foreach (var assignedWorker in _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex) ?? new List<AssignedWorkerDto>())
            {
                if (assignedWorker == null || assignedWorker.UnitID == 0)
                {
                    continue;
                }

                _extractorTrickService.TryLoadPrepMr(assignedWorker, _currentFrame);
            }
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

        private void WriteInstructionTrace()
        {
            if (string.IsNullOrEmpty(_instructionTraceFile))
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();
            foreach (var assignedWorker in assignedWorkers)
            {
                if (assignedWorker == null
                    || assignedWorker.UnitID == 0
                    || !Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitID, out var runtimeWorker))
                {
                    continue;
                }

                var state = string.Join("|", new object[]
                {
                    runtimeWorker.CurInstrIdx,
                    assignedWorker.Mti,
                    runtimeWorker.PreviousAbilityId,
                    runtimeWorker.CurrentAbilityId,
                    runtimeWorker.IsCarrying,
                    runtimeWorker.WasCarrying
                });
                var isFirstSnapshot = !_lastInstructionTraceState.ContainsKey(runtimeWorker.UnitTag);
                var stateChanged = !_lastInstructionTraceState.TryGetValue(runtimeWorker.UnitTag, out var previousState)
                    || !string.Equals(previousState, state, StringComparison.Ordinal);
                if (!isFirstSnapshot && !stateChanged)
                {
                    continue;
                }

                _lastInstructionTraceState[runtimeWorker.UnitTag] = state;
                AppendInstructionTrace(new
                {
                    RecordType = isFirstSnapshot ? "WorkerInstructionList" : "WorkerInstructionState",
                    TimestampUtc = DateTime.UtcNow,
                    GameFrame = _currentFrame,
                    StartIndex = startIndex,
                    WorkerTag = runtimeWorker.UnitTag,
                    WorkerLabel = assignedWorker.Role,
                    InstructionSet = runtimeWorker.Instructions.FirstOrDefault()?.InstructionSet ?? string.Empty,
                    CurrentInstructionIndex = runtimeWorker.CurInstrIdx,
                    InstructionStartFrame = runtimeWorker.InstructionStartFrame,
                    CurrentTargetIndex = assignedWorker.Mti,
                    PreviousAbilityId = runtimeWorker.PreviousAbilityId,
                    CurrentAbilityId = runtimeWorker.CurrentAbilityId,
                    IsCarrying = runtimeWorker.IsCarrying,
                    WasCarrying = runtimeWorker.WasCarrying,
                    Instructions = isFirstSnapshot
                        ? runtimeWorker.Instructions.Select((instruction, index) => new
                        {
                            Index = index,
                            Set = instruction?.InstructionSet ?? string.Empty,
                            Command = instruction?.Command.ToString() ?? string.Empty,
                            Point = instruction?.Point.ToString() ?? string.Empty,
                            TargetId = instruction?.TargetId ?? 0,
                            RelativeFrame = instruction?.RelativeFrame ?? -1,
                            PreviousAbilityId = instruction?.PreviousAbilityId ?? -1,
                            TargetAbilityId = instruction?.TargetAbilityId ?? -1,
                            NextTargetIndex = instruction?.NextTargetIndex ?? -1,
                            JumpToInstructionIndex = instruction?.JumpToInstructionIndex ?? -1,
                            Queue = instruction?.Queue ?? false,
                            PositionTolerance = instruction?.PositionTolerance ?? 0f
                        }).ToList() : null
                });
            }
        }

        private void AppendInstructionTrace(object record)
        {
            try
            {
                var line = JsonSerializer.Serialize(record, _instructionTraceJsonOptions) + Environment.NewLine;
                lock (_instructionTraceLock)
                {
                    File.AppendAllText(_instructionTraceFile, line);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Executes each worker's instruction list, consuming every row whose condition is already
        /// satisfied this frame and stopping at the first row that must wait for a frame, an ability
        /// transition, or a position.
        /// </summary>
        /// <param name="observation">Current frame observation.</param>
        /// <param name="contexts">
        /// One entry per emitted action, in emission order, naming the instruction row that produced
        /// it. Kept separate from the action list because a single worker can emit more than one
        /// action in one frame, so context cannot be recovered from the advanced index afterwards.
        /// </param>
        private List<SC2Action> ExecuteRuntimeWorkerInstructions(
            ResponseObservation observation,
            List<InstructionCommandContext> contexts)
        {
            var actions = new List<SC2Action>();
            var snapshot = Globals.CurrentObservation;
            if (snapshot == null)
            {
                return actions;
            }

            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(
                Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex)
                ?? new List<AssignedWorkerDto>();

            foreach (var assignedWorker in assignedWorkers)
            {
                if (assignedWorker == null || assignedWorker.UnitID == 0
                    || !snapshot.SelfUnits.TryGetValue(assignedWorker.UnitID, out var observedWorker)
                    || observedWorker == null)
                {
                    continue;
                }

                if (!Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitID, out var runtimeWorker))
                {
                    continue;
                }

                runtimeWorker.Position = assignedWorker.CurrentXY ?? runtimeWorker.Position;

                // A worker keeps consuming rows while each row's condition is already met, and stops
                // only at a row that must wait for a frame, an ability transition, or a position.
                // Without this loop a NoCondition store/use row consumes the frame's only execution
                // slot, so the RelativeFrame=0 move that follows it is delayed to the next frame and
                // no command is ever issued on frame 0.
                for (var executed = 0; executed < MaxInstructionsPerWorkerFrame; executed++)
                {
                    if (runtimeWorker.CurInstrIdx < 0 || runtimeWorker.CurInstrIdx > runtimeWorker.Instructions.Count)
                    {
                        ReportInstructionFailure(runtimeWorker, "invalid instruction index", null);
                        break;
                    }

                    if (runtimeWorker.CurInstrIdx >= runtimeWorker.Instructions.Count)
                    {
                        ReportInstructionFailure(runtimeWorker, "instruction list ended without a jump row", null);
                        break;
                    }

                    if (!AdvanceSatisfiedControlInstructions(runtimeWorker, assignedWorker))
                    {
                        break;
                    }

                    if (runtimeWorker.CurInstrIdx >= runtimeWorker.Instructions.Count)
                    {
                        ReportInstructionFailure(runtimeWorker, "instruction list ended after control row", null);
                        break;
                    }

                    var instruction = runtimeWorker.Instructions[runtimeWorker.CurInstrIdx];
                    if (instruction == null)
                    {
                        ReportInstructionFailure(runtimeWorker, "current instruction is null", null);
                        break;
                    }

                    var relativeFrame = Settings.GetRelativeFrame(_currentFrame) - runtimeWorker.InstructionStartFrame;
                    var frameSatisfied = instruction.RelativeFrame >= 0 && relativeFrame >= instruction.RelativeFrame;
                    var abilitySatisfied = instruction.TargetAbilityId >= 0
                        && (instruction.PreviousAbilityId < 0 || runtimeWorker.PreviousAbilityId == instruction.PreviousAbilityId)
                        && runtimeWorker.CurrentAbilityId == instruction.TargetAbilityId;
                    var targetPoint = ResolveInstructionPoint(runtimeWorker, assignedWorker, instruction);
                    if ((instruction.StoreTargetPoint || instruction.Command == WorkerInstructionCommand.UseTargetPoint)
                        && targetPoint == null)
                    {
                        ReportInstructionFailure(runtimeWorker, "stored target point could not be resolved", instruction);
                        break;
                    }

                    var positionSatisfied = targetPoint != null
                        && DistanceSquared(runtimeWorker.Position, targetPoint) <= instruction.PositionTolerance * instruction.PositionTolerance;
                    if (instruction.NoCondition)
                    {
                        positionSatisfied = true;
                    }

                    if (!frameSatisfied && !abilitySatisfied && !positionSatisfied)
                    {
                        break;
                    }

                    var action = ExecuteWorkerInstruction(runtimeWorker, instruction, assignedWorker, targetPoint, out var followUpAction);
                    if (action == null
                        && instruction.Command != WorkerInstructionCommand.StoreTargetPoint
                        && instruction.Command != WorkerInstructionCommand.UseTargetPoint)
                    {
                        ReportInstructionFailure(runtimeWorker, "instruction execution returned no action", instruction);
                        break;
                    }

                    if (string.Equals(instruction.InstructionSet, "CCAw", StringComparison.OrdinalIgnoreCase)
                        && instruction.Command == WorkerInstructionCommand.Move
                        && targetPoint != null)
                    {
                        Console.WriteLine($"[CCAW MOVE] frame={_currentFrame} worker={runtimeWorker.UnitTag} role={assignedWorker.Role} point=({targetPoint.X:F2},{targetPoint.Y:F2}) relative={relativeFrame} instruction={runtimeWorker.CurInstrIdx}");
                    }
                    else if (string.Equals(instruction.InstructionSet, "jitRM", StringComparison.OrdinalIgnoreCase)
                        && runtimeWorker.CurInstrIdx == 0
                        && relativeFrame >= 0)
                    {
                        Console.WriteLine($"[JITRM EXECUTE +0] frame={_currentFrame} worker={runtimeWorker.UnitTag} target={assignedWorker.Mti} command={instruction.Command} relative={relativeFrame}");
                    }

                    var executedIndex = runtimeWorker.CurInstrIdx;
                    if (action != null)
                    {
                        actions.Add(action);
                        contexts.Add(new InstructionCommandContext
                        {
                            WorkerTag = runtimeWorker.UnitTag,
                            InstructionSet = instruction.InstructionSet,
                            InstructionIndex = executedIndex,
                            InstructionCommand = instruction.Command.ToString(),
                            InstructionPoint = instruction.Point.ToString()
                        });
                    }
                    if (followUpAction != null)
                    {
                        actions.Add(followUpAction);
                        contexts.Add(new InstructionCommandContext
                        {
                            WorkerTag = runtimeWorker.UnitTag,
                            InstructionSet = instruction.InstructionSet,
                            InstructionIndex = executedIndex,
                            InstructionCommand = instruction.Command.ToString(),
                            InstructionPoint = instruction.Point.ToString()
                        });
                    }

                    runtimeWorker.CurInstrIdx++;
                    if (runtimeWorker.CurInstrIdx < runtimeWorker.Instructions.Count)
                    {
                        runtimeWorker.TargetAbilityId = runtimeWorker.Instructions[runtimeWorker.CurInstrIdx].TargetAbilityId;
                    }
                }
            }

            return actions;
        }

        private bool AdvanceSatisfiedControlInstructions(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker)
        {
            while (runtimeWorker.CurInstrIdx >= 0 && runtimeWorker.CurInstrIdx < runtimeWorker.Instructions.Count)
            {
                var instruction = runtimeWorker.Instructions[runtimeWorker.CurInstrIdx];
                if (instruction == null)
                {
                    ReportInstructionFailure(runtimeWorker, "control instruction is null", null);
                    return false;
                }

                var relativeFrame = Settings.GetRelativeFrame(_currentFrame) - runtimeWorker.InstructionStartFrame;
                var frameSatisfied = instruction.RelativeFrame >= 0 && relativeFrame >= instruction.RelativeFrame;
                var abilitySatisfied = instruction.TargetAbilityId >= 0
                    && (instruction.PreviousAbilityId < 0 || runtimeWorker.PreviousAbilityId == instruction.PreviousAbilityId)
                    && runtimeWorker.CurrentAbilityId == instruction.TargetAbilityId;
                var unconditionalJump = instruction.Command == WorkerInstructionCommand.Jump
                    && instruction.RelativeFrame < 0
                    && instruction.TargetAbilityId < 0;
                if (!frameSatisfied && !abilitySatisfied && !unconditionalJump)
                {
                    return true;
                }

                if (instruction.Command == WorkerInstructionCommand.Wait)
                {
                    if (instruction.NextTargetIndex >= 0 && instruction.NextTargetIndex < assignedWorker.MiningTargets.Count)
                    {
                        assignedWorker.Mti = instruction.NextTargetIndex;
                    }

                    runtimeWorker.CurInstrIdx++;
                    runtimeWorker.InstructionStartFrame = Settings.GetRelativeFrame(_currentFrame);
                    continue;
                }

                if (instruction.Command != WorkerInstructionCommand.Jump)
                {
                    return true;
                }

                if (instruction.JumpToInstructionIndex < 0 || instruction.JumpToInstructionIndex >= runtimeWorker.Instructions.Count)
                {
                    ReportInstructionFailure(runtimeWorker, "jump row has invalid target index", instruction);
                    return false;
                }

                runtimeWorker.CurInstrIdx = instruction.JumpToInstructionIndex;
                runtimeWorker.InstructionStartFrame = Settings.GetRelativeFrame(_currentFrame);
            }

            return runtimeWorker.CurInstrIdx < runtimeWorker.Instructions.Count;
        }

        private SC2Action? ExecuteWorkerInstruction(
            RuntimeWorkerState runtimeWorker,
            WorkerInstruction instruction,
            AssignedWorkerDto assignedWorker,
            Vector2Dto? targetPoint,
            out SC2Action? followUpAction)
        {
            followUpAction = null;
            if (runtimeWorker.UnitTag == 0)
            {
                return null;
            }

            if (instruction.StoreTargetPoint && targetPoint != null)
            {
                runtimeWorker.StoredTargetPoint = new Vector2Dto(targetPoint.X, targetPoint.Y, targetPoint.Z);
            }

            if (instruction.Command == WorkerInstructionCommand.GatherAndMove && targetPoint != null && instruction.TargetId != 0)
            {
                var gatherAction = CreateInstructionGatherAction(runtimeWorker.UnitTag, instruction.TargetId, false, instruction.TargetAbilityId);
                if (gatherAction == null)
                {
                    return null;
                }

                followUpAction = CreateInstructionMoveAction(
                    runtimeWorker.UnitTag,
                    new Point2D { X = targetPoint.X, Y = targetPoint.Y },
                    false);
                return gatherAction;
            }

            if (instruction.Command == WorkerInstructionCommand.MoveAndGather && targetPoint != null && instruction.TargetId != 0)
            {
                followUpAction = CreateInstructionGatherAction(runtimeWorker.UnitTag, instruction.TargetId, true);
                return CreateInstructionMoveAction(
                    runtimeWorker.UnitTag,
                    new Point2D { X = targetPoint.X, Y = targetPoint.Y },
                    false);
            }

            return instruction.Command switch
            {
                WorkerInstructionCommand.Move when targetPoint != null => CreateInstructionMoveAction(
                    runtimeWorker.UnitTag,
                    new Point2D { X = targetPoint.X, Y = targetPoint.Y },
                    instruction.Queue),
                WorkerInstructionCommand.Gather when instruction.TargetId != 0 => CreateInstructionGatherAction(
                    runtimeWorker.UnitTag,
                    instruction.TargetId,
                    instruction.Queue),
                WorkerInstructionCommand.BuildExtractor when instruction.TargetId != 0 => CreateInstructionBuildExtractorAction(
                    runtimeWorker.UnitTag,
                    instruction.TargetId,
                    instruction.Queue),
                WorkerInstructionCommand.Return => CreateInstructionReturnAction(runtimeWorker.UnitTag, instruction.Queue),
                WorkerInstructionCommand.StoreTargetPoint => null,
                WorkerInstructionCommand.UseTargetPoint => StoreReferencedTargetPoint(runtimeWorker, targetPoint, instruction),
                WorkerInstructionCommand.LoadInstructionSet => LoadNextInstructionSet(runtimeWorker, instruction, assignedWorker),
                _ => null
            };
        }

        private SC2Action? StoreReferencedTargetPoint(RuntimeWorkerState runtimeWorker, Vector2Dto? targetPoint, WorkerInstruction instruction)
        {
            if (targetPoint == null || runtimeWorker == null || string.IsNullOrWhiteSpace(instruction.TargetPointReference))
            {
                return null;
            }

            runtimeWorker.StoredTargetPoint = new Vector2Dto(targetPoint.X, targetPoint.Y, targetPoint.Z);
            return null;
        }

        private SC2Action? LoadNextInstructionSet(RuntimeWorkerState runtimeWorker, WorkerInstruction instruction, AssignedWorkerDto assignedWorker)
        {
            if (string.IsNullOrWhiteSpace(instruction.NextInstructionSet)
                || !Settings.RuntimeInstructionSets.TryGetValue(instruction.NextInstructionSet, out var nextInstructions))
            {
                return null;
            }

            runtimeWorker.LoadInstructions(instruction.NextInstructionSet, nextInstructions, Settings.GetRelativeFrame(_currentFrame));
            if (runtimeWorker.Instructions.Count == 0)
            {
                return null;
            }

            var firstInstruction = runtimeWorker.Instructions[0];
            var targetPoint = ResolveInstructionPoint(runtimeWorker, assignedWorker, firstInstruction);
            return ExecuteWorkerInstruction(runtimeWorker, firstInstruction, assignedWorker, targetPoint, out _);
        }

        private Vector2Dto? ResolveInstructionPoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if (instruction == null || instruction.Point == WorkerInstructionPoint.None || assignedWorker == null)
            {
                return null;
            }

            var target = assignedWorker.MiningTargets?.FirstOrDefault(candidate => candidate != null && candidate.ResourceUnitId == instruction.TargetId);
            if (target == null)
            {
                return null;
            }

            // BEGIN PROTECTED EXISTING BEHAVIOR: CCAw resolves only the current-spawn team.
            // This isolated lookup is not a generic instruction-point template. New steps must
            // preserve it and implement separate resolution rules instead of changing this path.
            var startIndex = Globals.CurrentStartIndex >= 0
                ? Globals.CurrentStartIndex
                : Settings.CurrentSpawnIndex;
            var currentAssignments = OngoingMapData.ResolveTeamAssignments(_mapData, startIndex);
            var teamAssignment = currentAssignments
                .FirstOrDefault(assignment => assignment?.Workers?.Any(worker => worker?.UnitTag == assignedWorker.UnitID) == true);

            // List-driven resolution: a StoreTargetPoint / UseTargetPoint row carries its own
            // "{miningBase}-{labelA}-{labelB}.{Property}" reference. The Mining Manager reads the
            // stored coordinate from the pair table -- it does not look ahead, does not calculate
            // points, and does not use the retired per-target Jit fields (jitHarvestA/B are not
            // valid; workers get instructions from a list, not an A/B swap).
            if (!string.IsNullOrWhiteSpace(instruction.TargetPointReference))
            {
                var referencedPoint = ResolvePairTablePoint(instruction.TargetPointReference);
                if (referencedPoint != null)
                {
                    return referencedPoint;
                }
            }

            return instruction.Point switch
            {
                WorkerInstructionPoint.Harvest => _speedMiningActive && HasNonZeroPoint(target.SmHarvestPoint) ? target.SmHarvestPoint : target.HarvestPoint,
                WorkerInstructionPoint.Return => _speedMiningActive && HasNonZeroPoint(target.SmReturnPoint) ? target.SmReturnPoint : target.ReturnPoint,
                WorkerInstructionPoint.StoredTarget when HasNonZeroPoint(runtimeWorker?.StoredTargetPoint) => runtimeWorker.StoredTargetPoint,
                WorkerInstructionPoint.Staging when teamAssignment != null
                    => ResolveCcaWaitPoint(teamAssignment, assignedWorker.Role),
                WorkerInstructionPoint.BumpPartner => ResolveObservedBumpPartner(assignedWorker, assignedWorker.Role),
                WorkerInstructionPoint.BumpMidpoint => ResolveObservedBumpMidpoint(assignedWorker, assignedWorker.Role, target),
                WorkerInstructionPoint.BumpHarvestCircle => ResolveBumpHarvestCirclePoint(assignedWorker, target),
                WorkerInstructionPoint.BumpCcaWaitCircle when teamAssignment != null
                    => ResolveCcaWaitPoint(teamAssignment, assignedWorker.Role),
                _ => null
            };
        }

        // Reference form: "{miningBase}-{labelA}-{labelB}.{Property}", canonical order,
        // matching the PairKey written by BabySharkBuildManager.PopulateJitPairReturnCalculations.
        private static Vector2Dto? ResolvePairTablePoint(string reference)
        {
            var dotIndex = reference.LastIndexOf('.');
            if (dotIndex <= 0 || dotIndex == reference.Length - 1)
            {
                return null;
            }

            var pairKey = reference.Substring(0, dotIndex);
            var property = reference.Substring(dotIndex + 1);
            var startIndex = Globals.CurrentStartIndex >= 0
                ? Globals.CurrentStartIndex
                : Settings.CurrentSpawnIndex;
            var pairs = Globals.CurrentMapData?.MainJitPairReturnCalculations?.ElementAtOrDefault(startIndex);
            var pair = pairs?.FirstOrDefault(candidate => candidate != null
                && string.Equals(candidate.PairKey, pairKey, StringComparison.OrdinalIgnoreCase));
            if (pair == null)
            {
                return null;
            }

            return property switch
            {
                "HarvestA" when HasNonZeroPoint(pair.HarvestA) => pair.HarvestA,
                "HarvestB" when HasNonZeroPoint(pair.HarvestB) => pair.HarvestB,
                "ReturnPoint" when HasNonZeroPoint(pair.ReturnPoint) => pair.ReturnPoint,
                "WaitPointA" when HasNonZeroPoint(pair.WaitPointA) => pair.WaitPointA,
                "WaitPointB" when HasNonZeroPoint(pair.WaitPointB) => pair.WaitPointB,
                _ => null
            };
        }

        private Vector2Dto? ResolveObservedBumpPartner(AssignedWorkerDto assignedWorker, string workerRole)
        {
            if (!Settings.IsMagannatha12WorkerOverride || assignedWorker == null)
            {
                return null;
            }

            var partnerRole = workerRole.Equals("T3", StringComparison.OrdinalIgnoreCase) ? "T1"
                : workerRole.Equals("Y3", StringComparison.OrdinalIgnoreCase) ? "Y1"
                : string.Empty;
            if (string.IsNullOrWhiteSpace(partnerRole))
            {
                return null;
            }

            var partner = _mapData?.AssignedWorkers?.ElementAtOrDefault(
                    Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex)
                ?.FirstOrDefault(worker => string.Equals(worker?.Role, partnerRole, StringComparison.OrdinalIgnoreCase));
            return partner?.CurrentXY != null && (partner.CurrentXY.X != 0f || partner.CurrentXY.Y != 0f)
                ? partner.CurrentXY
                : null;
        }

        private Vector2Dto? ResolveObservedBumpMidpoint(
            AssignedWorkerDto assignedWorker,
            string workerRole,
            MiningTargetDto target)
        {
            if (!Settings.IsMagannatha12WorkerOverride || assignedWorker == null || target == null)
            {
                return null;
            }

            var partnerRole = workerRole.Equals("T1", StringComparison.OrdinalIgnoreCase) ? "T3"
                : workerRole.Equals("Y1", StringComparison.OrdinalIgnoreCase) ? "Y3"
                : string.Empty;
            if (string.IsNullOrWhiteSpace(partnerRole) || target.ResourceUnitId == 0)
            {
                return null;
            }

            var partner = _mapData?.AssignedWorkers?.ElementAtOrDefault(
                    Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex)
                ?.FirstOrDefault(worker => string.Equals(worker?.Role, partnerRole, StringComparison.OrdinalIgnoreCase));
            if (partner?.CurrentXY == null || (partner.CurrentXY.X == 0f && partner.CurrentXY.Y == 0f))
            {
                return null;
            }

            if (!Globals.CurrentObservation.Minerals.TryGetValue(target.ResourceUnitId, out var observedMineral)
                || observedMineral?.Position == null)
            {
                return null;
            }

            return new Vector2Dto(
                (partner.CurrentXY.X + observedMineral.Position.X) / 2f,
                (partner.CurrentXY.Y + observedMineral.Position.Y) / 2f,
                observedMineral.Position.Z);
        }

        private Vector2Dto? ResolveBumpHarvestCirclePoint(AssignedWorkerDto assignedWorker, MiningTargetDto target)
        {
            if (assignedWorker?.CurrentXY == null || target?.HarvestPoint == null || target.ResourceUnitId == 0)
            {
                return null;
            }

            if (!Globals.CurrentObservation.Minerals.TryGetValue(target.ResourceUnitId, out var observedMineral)
                || observedMineral?.Position == null)
            {
                return null;
            }

            var directionX = target.HarvestPoint.X - observedMineral.Position.X;
            var directionY = target.HarvestPoint.Y - observedMineral.Position.Y;
            var distance = MathF.Sqrt(directionX * directionX + directionY * directionY);
            if (distance <= 0.0001f)
            {
                return null;
            }

            var workerDirectionX = assignedWorker.CurrentXY.X - observedMineral.Position.X;
            var workerDirectionY = assignedWorker.CurrentXY.Y - observedMineral.Position.Y;
            var workerDistance = MathF.Sqrt(workerDirectionX * workerDirectionX + workerDirectionY * workerDirectionY);
            if (workerDistance <= 0.0001f)
            {
                workerDirectionX = directionX;
                workerDirectionY = directionY;
                workerDistance = distance;
            }

            var harvestRadius = distance;
            return new Vector2Dto(
                observedMineral.Position.X + workerDirectionX / workerDistance * harvestRadius,
                observedMineral.Position.Y + workerDirectionY / workerDistance * harvestRadius,
                target.HarvestPoint.Z);
        }

        private static Vector2Dto? ResolveCcaWaitPoint(
            TeamPatchAssignmentDto teamAssignment,
            string workerRole)
        {
            if (teamAssignment?.Minerals == null || string.IsNullOrWhiteSpace(workerRole))
            {
                return null;
            }

            var teamPrefix = workerRole.Length >= 1
                ? workerRole.Substring(0, 1)
                : string.Empty;
            if (string.IsNullOrWhiteSpace(teamPrefix))
            {
                return null;
            }

            var aMineral = teamAssignment.Minerals.FirstOrDefault(mineral =>
                string.Equals(mineral?.FinalLabel, $"{teamPrefix}A", StringComparison.OrdinalIgnoreCase));
            if (aMineral != null && HasNonZeroPoint(aMineral.CcaWaitPoint))
            {
                return aMineral.CcaWaitPoint;
            }

            if (HasNonZeroPoint(teamAssignment.JitWaitPoint))
            {
                return teamAssignment.JitWaitPoint;
            }

            Console.WriteLine($"[CCAW POINT MISSING] role={workerRole} team={teamAssignment.TeamId} target={teamPrefix}A source=BaseDtos");
            return null;
        }

        private void ReportInstructionFailure(RuntimeWorkerState runtimeWorker, string reason, WorkerInstruction instruction)
        {
            Console.WriteLine($"[WORKER INSTRUCTION FAILURE] frame={_currentFrame} worker={runtimeWorker.UnitTag} index={runtimeWorker.CurInstrIdx} set={instruction?.InstructionSet ?? "<none>"} reason={reason} previousAbility={runtimeWorker.PreviousAbilityId} currentAbility={runtimeWorker.CurrentAbilityId} targetAbility={runtimeWorker.TargetAbilityId}");
            //Debugger.Break();
        }

        /// <summary>
        /// Instruction row that produced an emitted command, used to attribute each logged command to
        /// the row that ran rather than re-deriving it from the post-increment index.
        /// </summary>
        private sealed class InstructionCommandContext
        {
            public ulong WorkerTag { get; init; }
            public string InstructionSet { get; init; } = string.Empty;
            public int InstructionIndex { get; init; }
            public string InstructionCommand { get; init; } = string.Empty;
            public string InstructionPoint { get; init; } = string.Empty;
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

            if (startIndex < 0 || closestDistance > 9f || townhall == null)
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

        private string ResolveWorkerRole(AssignedWorkerDto assignedWorker, ulong workerTag)
        {
            if (!string.IsNullOrWhiteSpace(assignedWorker?.Role))
            {
                return assignedWorker.Role;
            }

            if (workerTag != 0 && _assignmentRoleByWorkerTag.TryGetValue(workerTag, out var cachedRole))
            {
                return cachedRole;
            }

            return _workerLabelService?.GetLabel(workerTag) ?? string.Empty;
        }

        private static bool IsRoleThree(string role)
        {
            return !string.IsNullOrWhiteSpace(role)
                && role.Length == 2
                && role[1] == '3'
                && (role[0] == 'T' || role[0] == 'S' || role[0] == 'B' || role[0] == 'Y');
        }

        private static bool IsRoleTargetCompatible(string role, string targetLabel)
        {
            return !string.IsNullOrWhiteSpace(role)
                && role.Length == 2
                && !string.IsNullOrWhiteSpace(targetLabel)
                && targetLabel.Length == 2
                && char.ToUpperInvariant(role[0]) == char.ToUpperInvariant(targetLabel[0]);
        }

        private static void CopyMineralToMiningTarget(OrderedMineral mineral, MiningTargetDto target)
        {
            target.ResourceLabel = mineral.FinalLabel;
            target.FromResourceLabel = mineral.FinalLabel;
            target.ToResourceLabel = mineral.FinalLabel;
            target.ResourceUnitId = mineral.UnitTag;
            target.ResourcePosition = mineral.Position;
            target.FromHarvestPoint = mineral.HarvestPoint;
            target.ToHarvestPoint = mineral.HarvestPoint;
            target.HarvestPoint = mineral.HarvestPoint;
            target.SmHarvestPoint = mineral.SmHarvestPoint;
            target.ReturnPoint = mineral.ReturnPoint;
            target.SmReturnPoint = mineral.SmReturnPoint;
            target.IsSpeedMining = false;
            target.IsABSwitch = false;
            target.IsInitialMineralAssignment = true;
        }

        private static MiningTargetDto ResolveAuthorizedTarget(
            AssignedWorkerDto assignedWorker,
            MiningTargetDto target,
            string workerRole,
            List<TeamPatchAssignmentDto> teamAssignments)
        {
            if (!Settings.IsMagannathaMap
                || assignedWorker == null
                || string.IsNullOrWhiteSpace(workerRole)
                || workerRole.Length != 2
                || teamAssignments == null)
            {
                return target;
            }

            var startsOnA = workerRole.EndsWith("1", StringComparison.OrdinalIgnoreCase)
                || workerRole.EndsWith("3", StringComparison.OrdinalIgnoreCase);
            var desiredSuffix = (assignedWorker.Mti % 2 == 0) == startsOnA ? "A" : "B";
            var desiredLabel = $"{workerRole.Substring(0, 1)}{desiredSuffix}";
            var authoritativeMineral = teamAssignments
                .SelectMany(assignment => assignment?.Minerals ?? new List<OrderedMineral>())
                .FirstOrDefault(mineral => string.Equals(mineral?.FinalLabel, desiredLabel, StringComparison.OrdinalIgnoreCase));
            if (authoritativeMineral == null)
            {
                return null;
            }

            if (target == null || !string.Equals(target.ToResourceLabel, desiredLabel, StringComparison.OrdinalIgnoreCase))
            {
                if (target == null)
                {
                    return null;
                }

                CopyMineralToMiningTarget(authoritativeMineral, target);
                Console.WriteLine($"[MINING TARGET CORRECTED] map=Magannatha worker={assignedWorker.UnitID} role={workerRole} mti={assignedWorker.Mti} correctedTarget={desiredLabel}");
            }

            return target;
        }

        private void UpdateGatherCycleStates(List<SnapshotUnit> liveWorkers)
        {
            var liveTags = new HashSet<ulong>();
            foreach (var worker in liveWorkers ?? new List<SnapshotUnit>())
            {
                liveTags.Add(worker.Tag);
                var gatherOrder = worker.OrderAbilityIds.Any(IsGatherOrder);
                if (!_gatherCycleStates.TryGetValue(worker.Tag, out var state))
                {
                    if (gatherOrder && worker.TargetUnitTag != 0)
                    {
                        _gatherCycleStates[worker.Tag] = new GatherCycleState
                        {
                            ResourceTag = worker.TargetUnitTag,
                            StartFrame = _currentFrame
                        };
                    }
                    continue;
                }

                if (!gatherOrder || worker.TargetUnitTag == 0)
                {
                    RecordMineralHarvestEnd(state.ResourceTag, state.StartFrame, _currentFrame);
                    _gatherCycleStates.Remove(worker.Tag);
                    continue;
                }

                if (state.ResourceTag != worker.TargetUnitTag)
                {
                    RecordMineralHarvestEnd(state.ResourceTag, state.StartFrame, _currentFrame);
                    _gatherCycleStates[worker.Tag] = new GatherCycleState
                    {
                        ResourceTag = worker.TargetUnitTag,
                        StartFrame = _currentFrame
                    };
                }
            }

            foreach (var workerTag in _gatherCycleStates.Keys.Where(tag => !liveTags.Contains(tag)).ToList())
            {
                var state = _gatherCycleStates[workerTag];
                RecordMineralHarvestEnd(state.ResourceTag, state.StartFrame, _currentFrame);
                _gatherCycleStates.Remove(workerTag);
            }
        }

        private void RecordMineralHarvestEnd(ulong resourceTag, int startFrame, int endFrame)
        {
            if (resourceTag == 0 || startFrame < 0 || endFrame <= startFrame)
            {
                return;
            }

            if (!_mineralHarvestTimings.TryGetValue(resourceTag, out var timing))
            {
                timing = new MineralHarvestTimingState();
                _mineralHarvestTimings[resourceTag] = timing;
            }

            timing.StartHarvestFrame = startFrame;
            timing.EndHarvestFrame = endFrame;
            timing.CycleFrames = endFrame - startFrame;
        }

        private bool IsGatheringNearCompletion(ulong workerTag, ulong resourceTag)
        {
            if (!_gatherCycleStates.TryGetValue(workerTag, out var state)
                || state.ResourceTag != resourceTag
                || !_mineralHarvestTimings.TryGetValue(resourceTag, out var timing)
                || timing.CycleFrames <= 0)
            {
                return false;
            }

            const int safetyWindowFrames = 10;
            var elapsedFrames = _currentFrame - state.StartFrame;
            var remainingFrames = timing.CycleFrames - elapsedFrames;
            return remainingFrames >= 0 && remainingFrames < safetyWindowFrames + 1;
        }

        private bool IsValidMineralWaitState(
            AssignedWorkerDto assignedWorker,
            SnapshotUnit worker,
            MiningTargetDto target,
            List<AssignedWorkerDto> assignedWorkers,
            List<SnapshotUnit> liveWorkers)
        {
            if (assignedWorker == null
                || worker == null
                || target == null
                || worker.IsCarrying
                || !IsWorkerAtAssignedHarvestPoint(worker, target))
            {
                return false;
            }

            return (assignedWorkers ?? new List<AssignedWorkerDto>()).Any(teammate =>
            {
                if (teammate == null
                    || teammate.UnitID == assignedWorker.UnitID
                    || teammate.MiningTargets == null)
                {
                    return false;
                }

                var teammateTarget = teammate.MiningTargets.ElementAtOrDefault(teammate.Mti);
                var liveTeammate = liveWorkers?.FirstOrDefault(candidate => candidate.Tag == teammate.UnitID);
                return liveTeammate != null
                    && !liveTeammate.IsCarrying
                    && teammateTarget != null
                    && teammateTarget.ResourceUnitId == target.ResourceUnitId
                    && liveTeammate.TargetUnitTag == target.ResourceUnitId
                    && liveTeammate.OrderAbilityIds.Any(IsGatherOrder)
                    && IsGatheringNearCompletion(liveTeammate.Tag, target.ResourceUnitId);
            });
        }

        private static bool IsWorkerAtAssignedHarvestPoint(SnapshotUnit worker, MiningTargetDto target)
        {
            return worker != null
                && target != null
                && HasNonZeroPoint(target.HarvestPoint)
                && DistanceSquared(
                    new Vector2Dto(worker.Pos.X, worker.Pos.Y),
                    target.HarvestPoint) <= 0.75f * 0.75f;
        }

        private static bool IsGatherOrder(int abilityId)
        {
            return abilityId == (int)Abilities.HARVEST_GATHER
                || abilityId == (int)Abilities.HARVEST_GATHER_DRONE
                || abilityId == (int)Abilities.HARVEST_GATHER_PROBE
                || abilityId == (int)Abilities.HARVEST_GATHER_SCV;
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

        private static SC2Action? CreateInstructionMoveAction(ulong workerTag, Point2D target, bool queued)
        {
            if (workerTag == 0 || target == null)
            {
                return null;
            }

            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.MOVE,
                TargetWorldSpacePos = target,
                QueueCommand = queued
            };
            command.UnitTags.Add(workerTag);
            return new SC2Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        private static SC2Action? CreateInstructionGatherAction(
            ulong workerTag,
            ulong mineralTag,
            bool queued,
            int abilityId = (int)Abilities.HARVEST_GATHER)
        {
            if (workerTag == 0 || mineralTag == 0)
            {
                return null;
            }

            var command = new ActionRawUnitCommand
            {
                AbilityId = abilityId,
                TargetUnitTag = mineralTag,
                QueueCommand = queued
            };
            command.UnitTags.Add(workerTag);
            return new SC2Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        private static SC2Action? CreateInstructionReturnAction(ulong workerTag, bool queued)
        {
            if (workerTag == 0)
            {
                return null;
            }

            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.HARVEST_RETURN,
                QueueCommand = queued
            };
            command.UnitTags.Add(workerTag);
            return new SC2Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        private static SC2Action? CreateInstructionBuildExtractorAction(ulong workerTag, ulong geyserTag, bool queued)
        {
            if (workerTag == 0 || geyserTag == 0)
            {
                return null;
            }

            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.BUILD_EXTRACTOR,
                TargetUnitTag = geyserTag,
                QueueCommand = queued
            };
            command.UnitTags.Add(workerTag);
            return new SC2Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        private void FilterDuplicateFrame15GatherActions(List<SC2Action> actions, int relativeFrame)
        {
            if (relativeFrame != 15 || actions == null)
            {
                return;
            }

            var seenWorkers = new HashSet<ulong>();
            actions.RemoveAll(action =>
            {
                var command = action?.ActionRaw?.UnitCommand;
                if (command?.AbilityId != (int)Abilities.HARVEST_GATHER || command.UnitTags == null)
                {
                    return false;
                }

                var duplicate = command.UnitTags.Any(tag => !seenWorkers.Add(tag));
                return duplicate;
            });
        }

        /// <summary>
        /// Logs every command issued to a worker this frame. Each command receives a unique trace id
        /// that is written to both the console and the instruction trace file, so the two outputs can
        /// be correlated directly instead of by frame and worker tag alone.
        /// </summary>
        /// <param name="actions">Actions emitted this frame, in emission order.</param>
        /// <param name="contexts">
        /// Instruction row that produced each action, positionally aligned with <paramref name="actions"/>.
        /// </param>
        private void LogMiningCommands(IReadOnlyList<SC2Action> actions, IReadOnlyList<InstructionCommandContext> contexts)
        {
            if (actions == null)
            {
                return;
            }

            for (var actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                var command = actions[actionIndex]?.ActionRaw?.UnitCommand;
                if (command == null || command.UnitTags == null || command.UnitTags.Count == 0)
                {
                    continue;
                }

                // Every issued command must be logged, so unknown abilities fall back to their
                // enum name instead of being dropped.
                var commandName = command.AbilityId switch
                {
                    (int)Abilities.MOVE => "MOVE",
                    (int)Abilities.HARVEST_GATHER => "HARVEST_GATHER",
                    (int)Abilities.HARVEST_RETURN => "HARVEST_RETURN",
                    (int)Abilities.SMART => "SMART",
                    _ => ((Abilities)command.AbilityId).ToString()
                };
                if (string.IsNullOrEmpty(commandName))
                {
                    continue;
                }

                var context = contexts != null && actionIndex < contexts.Count ? contexts[actionIndex] : null;
                var instructionSet = context?.InstructionSet ?? string.Empty;
                var instructionIndex = context == null ? -1 : context.InstructionIndex;
                var instructionCommand = context?.InstructionCommand ?? string.Empty;
                var instructionPoint = context?.InstructionPoint ?? string.Empty;

                var target = command.TargetWorldSpacePos == null
                    ? string.Empty
                    : $" pos=({command.TargetWorldSpacePos.X:F2},{command.TargetWorldSpacePos.Y:F2})";
                var targetTag = command.TargetUnitTag != 0 ? $" targetTag={command.TargetUnitTag}" : string.Empty;
                var queued = command.QueueCommand ? " queued=true" : " queued=false";
                var relativeFrame = Settings.GetRelativeFrame(_currentFrame);

                foreach (var workerTag in command.UnitTags)
                {
                    var workerLabel = ResolveWorkerRole(null, workerTag);
                    var commandTraceId = System.Threading.Interlocked.Increment(ref _miningCommandSequence);

                    Console.WriteLine($"[MINING COMMAND{commandTraceId}] frame={_currentFrame} relative={relativeFrame} worker={workerTag} Label={workerLabel} command={commandName}{target}{targetTag}{queued} set={instructionSet} idx={instructionIndex} instr={instructionCommand} point={instructionPoint}");

                    AppendInstructionTrace(new
                    {
                        RecordType = "MiningCommand",
                        TimestampUtc = DateTime.UtcNow,
                        GameFrame = _currentFrame,
                        RelativeFrame = relativeFrame,
                        CommandTraceId = commandTraceId,
                        WorkerTag = workerTag,
                        WorkerLabel = workerLabel,
                        InstructionSet = instructionSet,
                        InstructionIndex = instructionIndex,
                        InstructionCommand = instructionCommand,
                        InstructionPoint = instructionPoint,
                        AbilityId = command.AbilityId,
                        Ability = commandName,
                        QueueCommand = command.QueueCommand,
                        TargetUnitTag = command.TargetUnitTag == 0 ? (ulong?)null : command.TargetUnitTag,
                        TargetX = command.TargetWorldSpacePos?.X,
                        TargetY = command.TargetWorldSpacePos?.Y
                    });
                }
            }
        }

        private List<SC2Action> TakeScheduledMoveReplays(int frame)
        {
            if (!_scheduledMoveReplays.TryGetValue(frame, out var scheduledActions))
            {
                return new List<SC2Action>();
            }

            _scheduledMoveReplays.Remove(frame);
            return scheduledActions;
        }

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
            if (_mapData != null) foreach (var com in _mapData.MineralCenterOfMass) { if (com == null) continue; DrawDebugCircle(com, 0.5f, new Color { R = 0, G = 255, B = 0 }, 12f); }
        }

        private void DrawMineralLabels()
        {
            if (_mineralLabelService == null || _mapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (startIndex < 0 || startIndex >= _mapData.TeamPatchAssignments.Count)
            {
                return;
            }

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
            if (_mapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var currentVespenes = _mapData.OrderedMainVespene?.ElementAtOrDefault(startIndex);
            if (currentVespenes == null)
            {
                return;
            }

            foreach (var vespene in currentVespenes)
            {
                if (vespene?.Position == null || string.IsNullOrWhiteSpace(vespene.Label))
                {
                    continue;
                }

                ManagerDebugService.DrawText(vespene.Label, new Point
                {
                    X = vespene.Position.X,
                    Y = vespene.Position.Y,
                    Z = vespene.Position.Z + 1.5f
                }, ProcessVisableUnits.GetFinalLabelColor(vespene.Label), 12);
            }
        }

        private void DrawDebugCircle(Vector2Dto center, float radius, Color color, float z, int segments = 24)
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

        private void DrawExpansionPoints()
        {
            if (_mapData?.ExpansionPoints != null) foreach (var kvp in _mapData.ExpansionPoints) { if (kvp.Value?.ExpansionPoint == null) continue; DrawDebugCircle(kvp.Value.ExpansionPoint, 2.5f, new Color { R = 0, G = 0, B = 255 }, 12f); ManagerDebugService.DrawText($"Exp {kvp.Key}", new Point { X = kvp.Value.ExpansionPoint.X, Y = kvp.Value.ExpansionPoint.Y, Z = 13f }, new Color { R = 255, G = 255, B = 255 }, 12); }
        }

        private void DrawSpawningPoolPlacement()
        {
            if (_mapData?.SpawningPoolPlacements == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var placement = _mapData.SpawningPoolPlacements.ElementAtOrDefault(startIndex);
            if (placement == null)
            {
                return;
            }

            var color = new Color { R = 255, G = 0, B = 0 };
            DrawDebugCircle(placement, 1.5f, color, 12f);
            ManagerDebugService.DrawText("SpawningPool", new Point
            {
                X = placement.X,
                Y = placement.Y,
                Z = 12.5f
            }, color, 12);
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
