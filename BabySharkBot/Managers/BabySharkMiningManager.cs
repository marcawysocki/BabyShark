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
using System.Numerics;
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
        }

        private readonly HashSet<ulong> _cargoReturnSequenceActive = new HashSet<ulong>();
        private readonly HashSet<ulong> _awaitingCargoDeposit = new HashSet<ulong>();
        private readonly HashSet<ulong> _finalGatherWorkers = new HashSet<ulong>();
        private readonly Dictionary<ulong, int> _finalGatherFrames = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, SC2Action> _finalGatherActions = new Dictionary<ulong, SC2Action>();
        private readonly Dictionary<ulong, CargoReturnSequenceState> _pendingCargoReturnSequences = new Dictionary<ulong, CargoReturnSequenceState>();
        private readonly Dictionary<ulong, ulong> _defaultSpeedMiningMineralByWorker = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, string> _assignmentRoleByWorkerTag = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, GatherCycleState> _gatherCycleStates = new Dictionary<ulong, GatherCycleState>();
        private readonly Dictionary<ulong, MineralHarvestTimingState> _mineralHarvestTimings = new Dictionary<ulong, MineralHarvestTimingState>();
        private readonly HashSet<string> _firstCycleRoleThreeMineralWalks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, GatherCommandSequenceState> _gatherCommandSequences = new Dictionary<ulong, GatherCommandSequenceState>();

        private sealed class GatherCommandSequenceState
        {
            public ulong ResourceTag { get; set; }
            public int Stage { get; set; }
        }

        private sealed class CargoReturnSequenceState
        {
            public Vector2Dto ReturnPoint { get; set; } = new Vector2Dto();
            public Vector2Dto HarvestPoint { get; set; } = new Vector2Dto();
            public ulong MineralTag { get; set; }
            public ulong TownhallTag { get; set; }
            public int CreatedFrame { get; set; }
            public int MoveAgainFrame { get; set; }
            public int GatherAgainFrame { get; set; }
            public int GatherQueuedFrame { get; set; }
            public bool ReturnQueued { get; set; }
            public bool DepositObserved { get; set; }
            public int MoveRepeatCount { get; set; }
            public int GatherRepeatCount { get; set; }
        }

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
            chrisCrossAppleSause? ccaMiningService = null)
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

        public bool TryCreateMineralWalkSmart(
            ulong workerTag,
            bool carrying,
            out SC2Action action,
            out ulong mineralTag)
        {
            action = null!;
            mineralTag = 0;
            if (workerTag == 0 || Settings.ccaMining || Settings.SimulatedStartActive)
            {
                return false;
            }

            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex);
            var assignedWorker = assignedWorkers?.FirstOrDefault(worker => worker != null && worker.UnitID == workerTag);
            var teamAssignments = _mapData?.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            if (snapshot == null || assignedWorker == null || assignedWorker.MiningTargets == null)
            {
                return false;
            }

            var ownTarget = assignedWorker.MiningTargets.ElementAtOrDefault(assignedWorker.Mti);
            var workerRole = ResolveWorkerRole(assignedWorker, workerTag);
            ownTarget = ResolveAuthorizedTarget(assignedWorker, ownTarget, workerRole, teamAssignments);
            var ownMineralTag = ownTarget?.ResourceUnitId ?? 0;
            if (!carrying)
            {
                mineralTag = ownMineralTag;
            }
            else
            {
                mineralTag = ResolveEnemyMineralTag(snapshot, startIndex, ownMineralTag);
            }

            if (mineralTag == 0
                || !snapshot.Minerals.TryGetValue(mineralTag, out var mineral)
                || mineral == null
                || mineral.UnitTag != mineralTag)
            {
                mineralTag = 0;
                return false;
            }

            action = new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.SMART,
                        UnitTags = { workerTag },
                        TargetUnitTag = mineralTag,
                        QueueCommand = false
                    }
                }
            };
            return true;
        }

        private ulong ResolveEnemyMineralTag(ObservationSnapshotDto snapshot, int currentStartIndex, ulong ownMineralTag)
        {
            var oppositeStartIndex = currentStartIndex == 0 ? 1 : currentStartIndex == 1 ? 0 : -1;
            if (snapshot == null
                || _mapData?.StartingMinerals == null
                || oppositeStartIndex < 0
                || oppositeStartIndex >= _mapData.StartingMinerals.Count)
            {
                return 0;
            }

            var enemyMinerals = _mapData.StartingMinerals[oppositeStartIndex];
            if (enemyMinerals == null || enemyMinerals.Count == 0)
            {
                return 0;
            }

            foreach (var enemyMineral in enemyMinerals)
            {
                if (enemyMineral?.Position == null)
                {
                    continue;
                }

                var liveMineral = snapshot.Minerals.Values
                    .Where(mineral => mineral != null
                        && mineral.UnitTag != ownMineralTag
                        && mineral.Position != null)
                    .OrderBy(mineral => DistanceSquared(mineral.Position, enemyMineral.Position))
                    .FirstOrDefault();
                if (liveMineral != null
                    && DistanceSquared(liveMineral.Position, enemyMineral.Position) <= 1.0f)
                {
                    return liveMineral.UnitTag;
                }
            }

            return 0;
        }

        public bool TryCreateCollisionResumeAction(
            ulong workerTag,
            bool carrying,
            out SC2Action action,
            out string resumeReason)
        {
            action = null!;
            resumeReason = string.Empty;
            if (workerTag == 0 || Settings.ccaMining || Settings.SimulatedStartActive)
            {
                return false;
            }

            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex);
            var assignedWorker = assignedWorkers?.FirstOrDefault(worker => worker != null && worker.UnitID == workerTag);
            var teamAssignments = _mapData?.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            var liveWorker = snapshot?.SelfUnits?.Values.FirstOrDefault(worker => worker != null && worker.UnitTag == workerTag);
            var target = assignedWorker?.MiningTargets?.ElementAtOrDefault(assignedWorker.Mti);
            var workerRole = ResolveWorkerRole(assignedWorker, workerTag);
            target = ResolveAuthorizedTarget(assignedWorker, target, workerRole, teamAssignments);
            if (snapshot == null || assignedWorker == null || liveWorker == null || target == null || target.ResourceUnitId == 0)
            {
                return false;
            }

            if (carrying)
            {
                var assignedReturnPoint = ResolveAssignedReturnPoint(assignedWorker, target, teamAssignments);
                var townhall = snapshot.CurrentTownHalls?.Values.FirstOrDefault(candidate => candidate != null && candidate.UnitTag == target.TownHallUnitId)
                    ?? snapshot.CurrentTownHalls?.Values.FirstOrDefault();
                if (townhall == null || townhall.Position == null)
                {
                    return false;
                }

                var townhallDistanceSquared = DistanceSquared(
                    liveWorker.Position,
                    townhall.Position);
                if (townhallDistanceSquared <= 2.75f * 2.75f)
                {
                    action = IssueHarvestReturn(workerTag, false);
                    resumeReason = "hatchery-footprint-return-cargo";
                    return action != null;
                }

                if (!HasNonZeroPoint(assignedReturnPoint))
                {
                    return false;
                }

                action = CreateMoveAction(
                    workerTag,
                    new Point2D { X = assignedReturnPoint.X, Y = assignedReturnPoint.Y },
                    false);
                resumeReason = "resume-assigned-return-point";
                return action != null;
            }

            action = IssueHarvestGather(workerTag, target.ResourceUnitId, false);
            resumeReason = "resume-assigned-mineral";
            return action != null;
        }

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
            _assignmentRoleByWorkerTag.Clear();
            _gatherCycleStates.Clear();
            _mineralHarvestTimings.Clear();
            _gatherCommandSequences.Clear();
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

            var actions = FilterCarryingWorkerActions(
                TakeScheduledMoveReplays(_currentFrame),
                snapshot);
            var ccaWasActive = Settings.ccaMining;
            var ccaActions = new List<SC2Action>();
            if (ccaWasActive && _mapData != null)
            {
                var ccaStartIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                if (ccaStartIndex >= 0)
                {
                    var ccaWorkers = BuildCcaWorkerEntries(ccaStartIndex, snapshot);
                    ccaActions.AddRange(_ccaMiningService.BuildBumpOrders(
                        _currentFrame,
                        _mapData,
                        ccaStartIndex,
                        ccaWorkers));

                    var handoffFrame = Settings.IsMagannatha12WorkerOverride ? 105 : 35;
                    if ((ccaWorkers.Count == 8 || ccaWorkers.Count == 12 || Settings.WorkerCount == 8 || Settings.WorkerCount == 12)
                        && relativeFrame >= handoffFrame)
                    {
                        Console.WriteLine($"BabySharkMiningManager: CCA handoff at relative frame {relativeFrame} (threshold {handoffFrame}).");
                        SignalMiningStarted();
                    }
                    else
                    {
                        _allMiningConsecutiveFrames = 0;
                    }
                }
            }

            if (ccaWasActive)
            {
                actions.AddRange(ccaActions);
                FilterDuplicateFrame15GatherActions(actions, relativeFrame);
                UpdateScoutedMinerals(observation);
                UpdateMineralReturnRate(observation);
                PrintMineralReturnRateSummary(observation);
                PrintTwelveDroneMilestone(observation);
                LogMiningCommands(actions);
                return actions;
            }

            if (!_handoffBreakTriggered && !Settings.ccaMining && relativeFrame >= 35)
            {
                _handoffBreakTriggered = true;
                Console.WriteLine($"BabySharkMiningManager: Steady-state JIT verified at frame {_currentFrame}");
            }

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

            FilterPostFinalGatherActions(actions, snapshot);
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

                var wasAssignedWorkerCarrying = observedWorker.WasCarrying;
                assignedWorker.CurrentXY = observedWorker.Position;
                assignedWorker.CurrentTargetUnitID = observedWorker.TargetUnitTag;
                assignedWorker.CurrentAbilityID = (uint)(observedWorker.OrderAbilityIds?.FirstOrDefault() ?? 0);
                if (wasAssignedWorkerCarrying && !observedWorker.IsCarrying)
                {
                    CancelScheduledMoveReplays(assignedWorker.UnitID);
                }

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
                    Console.WriteLine($"[ASSIGNED TARGET] worker={assignedWorker.UnitID} cargo-return mti={oldIndex}->{assignedWorker.Mti} targets={oldCount}->{assignedWorker.MiningTargets.Count} current={(assignedWorker.MiningTargets.ElementAtOrDefault(assignedWorker.Mti)?.ToResourceLabel ?? "<none>")} carrying={wasAssignedWorkerCarrying}->false source=ObservationManager");
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
                var carryingWorkers = liveWorkers.Count(worker => worker.IsCarrying);
                var previousCarryingWorkers = liveWorkers.Count(worker => worker.WasCarrying);
                Console.WriteLine($"[MINING REACH] cargo-eval frame={_currentFrame} liveWorkers={liveWorkers.Count} carrying={carryingWorkers} previousCarrying={previousCarryingWorkers} teamAssignments={teamAssignments.Count} townhallTag={townhallUnit?.Tag ?? 0} source=raw-observation");
            }

            actions.AddRange(ExecuteAssignedWorkerTargets(assignedWorkers, liveWorkers, townhallUnit, teamAssignments));

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
                    if (actions.Any(action => action?.ActionRaw?.UnitCommand?.UnitTags?.Contains(worker.Tag) == true))
                    {
                        continue;
                    }

                    var label = _workerLabelService.GetLabel(worker.Tag) ?? "";
                    var carrying = worker.IsCarrying;
                    var wasCarrying = worker.WasCarrying;

                    _jitWorkerStates.TryGetValue(worker.Tag, out var state);

                    if (TryContinueCargoReturn(actions, worker, townhallUnit))
                    {
                        continue;
                    }

                    if (carrying && !wasCarrying && townhallUnit != null && !_cargoReturnSequenceActive.Contains(worker.Tag))
                    {
                        _awaitingCargoDeposit.Add(worker.Tag);
                        var cargoReturnPoint = ResolveCargoReturnPoint(
                            assignment,
                            state,
                            null,
                            isJitTeam);
                        var cargoMineral = state == null ? null : ResolveMineralForWorker(assignment, state);

                        if (cargoReturnPoint != null && IssueCargoReturnSequence(actions, worker.Tag, cargoReturnPoint, townhallUnit.Tag, cargoMineral))
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
                                var jitTarget = new MiningTargetDto
                                {
                                    ResourceUnitId = mineral.UnitTag,
                                    ResourcePosition = mineral.Position,
                                    FromHarvestPoint = mineral.HarvestPoint,
                                    ToHarvestPoint = mineral.HarvestPoint,
                                    HarvestPoint = mineral.HarvestPoint,
                                    SmHarvestPoint = mineral.SmHarvestPoint,
                                    ReturnPoint = mineral.ReturnPoint,
                                    SmReturnPoint = mineral.SmReturnPoint,
                                    ResourceLabel = mineral.FinalLabel,
                                    FromResourceLabel = mineral.FinalLabel,
                                    ToResourceLabel = mineral.FinalLabel,
                                    IsInitialMineralAssignment = true
                                };
                                AddSharkyGatherSequence(actions, worker, jitTarget);
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

        private Vector2Dto? ResolveAssignedReturnPoint(
            AssignedWorkerDto assignedWorker,
            MiningTargetDto target,
            List<TeamPatchAssignmentDto> teamAssignments)
        {
            if (assignedWorker == null || target == null)
            {
                return null;
            }

            var teamAssignment = teamAssignments?.FirstOrDefault(team =>
                team?.Workers?.Any(worker => worker != null && worker.UnitTag == assignedWorker.UnitID) == true);
            if (teamAssignment?.Workers?.Count >= 3)
            {
                var jitReturnPoint = ResolveNonZeroPoint(teamAssignment.JitReturnPoint, null);
                if (jitReturnPoint != null)
                {
                    return jitReturnPoint;
                }
            }

            return ResolveNonZeroPoint(target.ReturnPoint, null);
        }

        private Vector2Dto? ResolveCollisionStagingReturnPoint(
            AssignedWorkerDto assignedWorker,
            MiningTargetDto target,
            List<TeamPatchAssignmentDto> teamAssignments)
        {
            var assignedReturnPoint = ResolveAssignedReturnPoint(assignedWorker, target, teamAssignments);
            if (assignedWorker == null || target == null || teamAssignments == null)
            {
                return assignedReturnPoint;
            }

            var teamAssignment = teamAssignments.FirstOrDefault(team =>
                team?.Workers?.Any(worker => worker != null && worker.UnitTag == assignedWorker.UnitID) == true);
            if (teamAssignment?.Workers?.Count < 3)
            {
                return assignedReturnPoint;
            }

            var sourceIndex = ResolveCollisionSourceIndex(ResolveAssignedMineralIndex(assignedWorker, target, teamAssignment));
            if (sourceIndex <= 0)
            {
                return assignedReturnPoint;
            }

            var sourceMineral = teamAssignment.Minerals?
                .FirstOrDefault(mineral => mineral != null && mineral.Index == sourceIndex);
            return sourceMineral == null
                ? assignedReturnPoint
                : ResolveNonZeroPoint(sourceMineral.SmReturnPoint, assignedReturnPoint);
        }

        private static int ResolveAssignedMineralIndex(
            AssignedWorkerDto assignedWorker,
            MiningTargetDto target,
            TeamPatchAssignmentDto teamAssignment)
        {
            var mineral = teamAssignment?.Minerals?.FirstOrDefault(candidate =>
                candidate != null
                && candidate.UnitTag != 0
                && candidate.UnitTag == target.ResourceUnitId);
            return mineral?.Index ?? 0;
        }

        private static int ResolveCollisionSourceIndex(int assignedIndex)
        {
            return assignedIndex switch
            {
                2 => 1,
                4 => 3,
                5 => 6,
                7 => 8,
                _ => 0
            };
        }

        private bool ShouldStagePairedReturn(
            AssignedWorkerDto assignedWorker,
            MiningTargetDto target,
            List<AssignedWorkerDto> assignedWorkers,
            List<TeamPatchAssignmentDto> teamAssignments,
            List<SnapshotUnit> liveWorkers)
        {
            if (assignedWorker == null || target == null || liveWorkers == null)
            {
                return false;
            }

            var teamAssignment = teamAssignments?.FirstOrDefault(team =>
                team?.Workers?.Any(worker => worker != null && worker.UnitTag == assignedWorker.UnitID) == true);
            if (teamAssignment?.Workers?.Count < 3)
            {
                return false;
            }

            var sourceIndex = ResolveCollisionSourceIndex(ResolveAssignedMineralIndex(assignedWorker, target, teamAssignment));
            if (sourceIndex <= 0)
            {
                return false;
            }

            var sourceMineral = teamAssignment.Minerals?.FirstOrDefault(mineral => mineral != null && mineral.Index == sourceIndex);
            if (sourceMineral == null || !HasNonZeroPoint(sourceMineral.SmReturnPoint))
            {
                return false;
            }

            var teammateAssignment = assignedWorkers?.FirstOrDefault(worker =>
                worker != null
                && worker.UnitID != assignedWorker.UnitID
                && worker.MiningTargets?.Any(candidate => candidate != null && candidate.ResourceUnitId == sourceMineral.UnitTag) == true);
            var teammate = liveWorkers.FirstOrDefault(worker => worker.Tag == teammateAssignment?.UnitID);
            if (teammate == null || !teammate.IsCarrying || !HasNonZeroPoint(target.ReturnPoint))
            {
                return false;
            }

            var ownReturn = new Vector2(target.ReturnPoint.X, target.ReturnPoint.Y);
            var teammateReturn = new Vector2(sourceMineral.SmReturnPoint.X, sourceMineral.SmReturnPoint.Y);
            return _collisionCalculator.Collides(
                new Vector2(teammate.Pos.X, teammate.Pos.Y),
                0.75f,
                new Vector2(assignedWorker.CurrentXY.X, assignedWorker.CurrentXY.Y),
                ownReturn)
                || Vector2.DistanceSquared(ownReturn, teammateReturn) <= 1.0f;
        }

        private List<SC2Action> ExecuteAssignedWorkerTargets(
            List<AssignedWorkerDto> assignedWorkers,
            List<SnapshotUnit> liveWorkers,
            SnapshotUnit townhallUnit,
            List<TeamPatchAssignmentDto> teamAssignments)
        {
            var actions = new List<SC2Action>();
            foreach (var assignedWorker in assignedWorkers ?? new List<AssignedWorkerDto>())
            {
                var worker = liveWorkers.FirstOrDefault(candidate => candidate.Tag == assignedWorker.UnitID);
                if (worker == null || assignedWorker.MiningTargets == null || assignedWorker.MiningTargets.Count == 0)
                {
                    continue;
                }

                if (_finalGatherWorkers.Contains(worker.Tag) && !worker.IsCarrying)
                {
                    continue;
                }

                var target = assignedWorker.MiningTargets.ElementAtOrDefault(assignedWorker.Mti);
                if (target == null
                    || target.ResourceUnitId == 0
                    || Globals.CurrentObservation?.Minerals?.TryGetValue(target.ResourceUnitId, out var verifiedMineral) != true
                    || verifiedMineral == null
                    || verifiedMineral.UnitTag != target.ResourceUnitId)
                {
                    continue;
                }

                if (IsValidMineralWaitState(assignedWorker, worker, target, assignedWorkers, liveWorkers))
                {
                    continue;
                }

                var workerRole = ResolveWorkerRole(assignedWorker, worker.Tag);
                target = ResolveAuthorizedTarget(assignedWorker, target, workerRole, teamAssignments);
                if (target == null || target.ResourceUnitId == 0)
                {
                    Console.WriteLine($"[MINING TARGET BLOCKED] map=Magannatha worker={worker.Tag} role={workerRole} reason=no-authorized-target");
                    continue;
                }

                var carrying = worker.IsCarrying;
                var justPickedUp = carrying && worker.WasCarrying == false;
                var justReturnedCargo = !carrying && worker.WasCarrying;
                if (carrying)
                {
                    _finalGatherWorkers.Remove(worker.Tag);
                }
                var hasActiveMiningOrder = worker.OrderAbilityIds.Any(IsMiningOrder);
                var hasActiveGatherOrder = worker.OrderAbilityIds.Any(IsGatherOrder);
                var hasCorrectMineralTarget = worker.TargetUnitTag == target.ResourceUnitId;
                var assignedReturnPoint = ResolveAssignedReturnPoint(assignedWorker, target, teamAssignments);

                if (justReturnedCargo
                    && _cargoReturnSequenceActive.Contains(worker.Tag)
                    && teamAssignments?.FirstOrDefault(team => team?.Workers?.Any(candidate => candidate?.UnitTag == worker.Tag) == true)?.Workers?.Count >= 3)
                {
                    OnWorkerCargoReturned(worker.Tag);
                    UpdatePendingCargoReturnTarget(worker.Tag, assignedWorker, teamAssignments);
                }

                if (TryContinueCargoReturn(actions, worker, townhallUnit))
                {
                    continue;
                }

                if (_awaitingCargoDeposit.Contains(worker.Tag))
                {
                    var townhallDistanceSquared = Vector2.DistanceSquared(
                        new Vector2(worker.Pos.X, worker.Pos.Y),
                        new Vector2(townhallUnit.Pos.X, townhallUnit.Pos.Y));
                    if (carrying || townhallDistanceSquared > 9f)
                    {
                        if (carrying && HasNonZeroPoint(assignedReturnPoint) && townhallUnit != null)
                        {
                            AddCargoReturnPositioningSequence(actions, worker, assignedReturnPoint, townhallUnit.Tag);
                        }
                        continue;
                    }

                    _awaitingCargoDeposit.Remove(worker.Tag);
                }

                if (carrying)
                {
                    if (HasNonZeroPoint(assignedReturnPoint)
                        && DistanceSquared(
                            new Vector2Dto(worker.Pos.X, worker.Pos.Y),
                            assignedReturnPoint) <= 0.75f * 0.75f)
                    {
                        AddAction(actions, IssueHarvestReturn(worker.Tag, false));
                    }
                    else if ((justPickedUp && ShouldStagePairedReturn(assignedWorker, target, assignedWorkers, teamAssignments, liveWorkers))
                        || !hasActiveMiningOrder)
                    {
                        var returnPoint = justPickedUp
                            ? ResolveCollisionStagingReturnPoint(assignedWorker, target, teamAssignments)
                            : assignedReturnPoint;
                        if (townhallUnit != null)
                        {
                            AddCargoReturnPositioningSequence(actions, worker, returnPoint, townhallUnit.Tag);
                        }
                    }

                    continue;
                }

                if (justReturnedCargo)
                {
                    _gatherCommandSequences.Remove(worker.Tag);
                    CancelScheduledMoveReplays(worker.Tag);
                }

                if (!worker.IsCarrying)
                {
                    AddSharkyGatherSequence(actions, worker, target);
                }

                continue;
            }

            return actions;
        }

        private void AddCargoReturnPositioningSequence(
            List<SC2Action> actions,
            SnapshotUnit worker,
            Vector2Dto returnPoint,
            ulong townhallTag)
        {
            if (worker?.Tag == 0 || !HasNonZeroPoint(returnPoint))
            {
                return;
            }
            if (townhallTag != 0 && IssueCargoReturnSequence(actions, worker.Tag, returnPoint, townhallTag, null))
            {
                _cargoReturnSequenceActive.Add(worker.Tag);
            }
        }

        private void AddHarvestPositioningSequence(
            List<SC2Action> actions,
            SnapshotUnit worker,
            MiningTargetDto target)
        {
            if (worker?.Tag == 0 || target == null || !HasNonZeroPoint(target.HarvestPoint))
            {
                return;
            }

            var harvestPoint = target.HarvestPoint;
            var workerPosition = new Vector2(worker.Pos.X, worker.Pos.Y);
            var harvestPosition = new Vector2(harvestPoint.X, harvestPoint.Y);
            var awayFromHarvest = workerPosition - harvestPosition;
            var distance = awayFromHarvest.Length();
            if (distance <= 0.001f)
            {
                return;
            }

            awayFromHarvest /= distance;
            var stagingPoint = new Vector2Dto(
                harvestPoint.X + awayFromHarvest.X * 1.0f,
                harvestPoint.Y + awayFromHarvest.Y * 1.0f,
                worker.Pos.Z);

            AddMoveOnlySequence(actions, worker, stagingPoint, harvestPoint, $"harvest-ready role={ResolveWorkerRole(null, worker.Tag)}");
        }

        private void AddMoveOnlySequence(
            List<SC2Action> actions,
            SnapshotUnit worker,
            Vector2Dto stagingPoint,
            Vector2Dto finalPoint,
            string reason)
        {
            AddAction(actions, IssueMoveToPoint(worker, stagingPoint, false));
            AddAction(actions, IssueMoveToPoint(worker, stagingPoint, true));
            AddAction(actions, IssueMoveToPoint(worker, finalPoint, true));
            Console.WriteLine($"[MINING MOVE PREPOSITION] frame={_currentFrame} worker={worker.Tag} reason={reason} stage=({stagingPoint.X:F2},{stagingPoint.Y:F2}) final=({finalPoint.X:F2},{finalPoint.Y:F2}) commands=3");
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
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var teamAssignments = _mapData?.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            roleThreeTarget = ResolveAuthorizedTarget(roleThree, roleThreeTarget, roleThree?.Role ?? string.Empty, teamAssignments);
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
            Console.WriteLine($"[FIRST CYCLE MINERAL WALK] leader={roleOne.Role}:{roleOne.UnitID} bump er={roleThree.Role}:{roleThree.UnitID} target={roleThreeTarget.ToResourceLabel} before-leader-hatchery-return=true");
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

        private void AddCargoReturnSequence(
            List<SC2Action> actions,
            SnapshotUnit worker,
            MiningTargetDto target,
            SnapshotUnit townhall)
        {
            if (worker?.Tag == 0 || target == null || townhall?.Tag == 0)
            {
                return;
            }

            var returnPoint = ResolveNonZeroPoint(target.ReturnPoint, null);
            if (returnPoint == null)
            {
                return;
            }

            var workerPosition = new Vector2(worker.Pos.X, worker.Pos.Y);
            var townhallPosition = new Vector2(townhall.Pos.X, townhall.Pos.Y);
            var returnPosition = new Vector2(returnPoint.X, returnPoint.Y);
            var harvestPosition = new Vector2(target.HarvestPoint.X, target.HarvestPoint.Y);
            var routeAligned = _collisionCalculator.Collides(workerPosition, 2f, returnPosition, harvestPosition);
            var townhallDistanceSquared = Vector2.DistanceSquared(townhallPosition, workerPosition);

            if (townhallDistanceSquared > 20f || !routeAligned)
            {
                AddAction(actions, IssueSmart(worker, townhall.Tag, false));
            }
            else if (townhallDistanceSquared < 10f)
            {
                AddAction(actions, IssueHarvestReturn(worker.Tag, false));
                AddAction(actions, IssueMoveToPoint(worker, target.HarvestPoint, true));
            }
            else
            {
                AddAction(actions, IssueMoveToPoint(worker, returnPoint, false));
                AddAction(actions, IssueSmart(worker, townhall.Tag, true));
            }

            Console.WriteLine($"[MINING TRANSITION Return Cargo] worker={worker.Tag} Label={ResolveWorkerRole(null, worker.Tag)} routeAligned={routeAligned} return-policy=route-aware");
        }

        private void AddSharkyGatherSequence(
            List<SC2Action> actions,
            SnapshotUnit worker,
            MiningTargetDto target)
        {
            if (worker?.Tag == 0 || target == null || target.ResourceUnitId == 0
                || !HasNonZeroPoint(target.HarvestPoint)
                || !HasNonZeroPoint(target.ReturnPoint))
            {
                return;
            }

            var initialPoint = HasNonZeroPoint(target.SmHarvestPoint)
                ? target.SmHarvestPoint
                : target.HarvestPoint;
            var midPoint = new Vector2Dto(
                (initialPoint.X + target.HarvestPoint.X) * 0.5f,
                (initialPoint.Y + target.HarvestPoint.Y) * 0.5f,
                worker.Pos.Z);


            if (!_gatherCommandSequences.TryGetValue(worker.Tag, out var state)
                || state.ResourceTag != target.ResourceUnitId)
            {
                state = new GatherCommandSequenceState
                {
                    ResourceTag = target.ResourceUnitId,
                    Stage = 0
                };
                _gatherCommandSequences[worker.Tag] = state;
            }

            switch (state.Stage)
            {
                case 0:
                    AddSequenceAction(actions, IssueMoveToPoint(worker, initialPoint, false));
                    state.Stage = 1;
                    break;
                case 1:
                    AddSequenceAction(actions, IssueHarvestGather(worker.Tag, target.ResourceUnitId, false));
                    state.Stage = 2;
                    break;
                case 2:
                    AddSequenceAction(actions, IssueMoveToPoint(worker, midPoint, false));
                    AddSequenceAction(actions, IssueHarvestGather(worker.Tag, target.ResourceUnitId, true));
                    state.Stage = 3;
                    break;
                case 3:
                    AddSequenceAction(actions, IssueMoveToPoint(worker, target.HarvestPoint, false));
                    AddSequenceAction(actions, IssueHarvestGather(worker.Tag, target.ResourceUnitId, true));
                    state.Stage = 4;
                    break;
                default:
                    if (!worker.IsCarrying && !worker.OrderAbilityIds.Any(IsMiningOrder))
                    {
                        state.Stage = 0;
                    }
                    break;
            }
        }

        private void AddSequenceAction(List<SC2Action> actions, SC2Action? action)
        {
            if (action != null)
            {
                actions.Add(action);
            }
        }

        private void AddHarvestSequence(
            List<SC2Action> actions,
            SnapshotUnit worker,
            MiningTargetDto target,
            SnapshotUnit townhall)
        {
            if (worker?.Tag == 0 || target == null || target.ResourceUnitId == 0 || !HasNonZeroPoint(target.HarvestPoint))
            {
                return;
            }

            var workerPosition = new Vector2(worker.Pos.X, worker.Pos.Y);
            var mineralPosition = new Vector2(target.ResourcePosition.X, target.ResourcePosition.Y);
            var townhallPosition = townhall == null
                ? mineralPosition
                : new Vector2(townhall.Pos.X, townhall.Pos.Y);
            var harvestPosition = new Vector2(target.HarvestPoint.X, target.HarvestPoint.Y);
            var routeAligned = _collisionCalculator.Collides(workerPosition, 2f, townhallPosition, harvestPosition);
            var mineralDistanceSquared = Vector2.DistanceSquared(mineralPosition, workerPosition);
            var touchingWorker = false;

            var snapshot = Globals.CurrentObservation;
            if (snapshot?.SelfUnits != null)
            {
                touchingWorker = snapshot.SelfUnits.Values.Any(other => other != null
                    && other.UnitTag != worker.Tag
                    && other.Position != null
                    && Vector2.DistanceSquared(workerPosition, new Vector2(other.Position.X, other.Position.Y)) < 0.5f
                    && WorkerTypes.Contains((UnitTypes)other.UnitType) == false);
            }

            if (mineralDistanceSquared < 2f || mineralDistanceSquared > 6f || touchingWorker || !routeAligned)
            {
                AddAction(actions, IssueSmart(worker, target.ResourceUnitId, false));
                AddAction(actions, IssueMoveToPoint(worker, target.ReturnPoint, true));
            }
            else
            {
                AddAction(actions, IssueMoveToPoint(worker, target.HarvestPoint, false));
                AddAction(actions, IssueSmart(worker, target.ResourceUnitId, true));
            }

            Console.WriteLine($"[MINING TRANSITION2] worker={worker.Tag} Label={ResolveWorkerRole(null, worker.Tag)} routeAligned={routeAligned} distanceSquared={mineralDistanceSquared:F2} gather-policy=route-aware");
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
                var carrying = worker.IsCarrying;
                var wasCarrying = worker.WasCarrying;

                if (TryContinueCargoReturn(actions, worker, townhallUnit))
                {
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

                    if (returnPoint != null && HasNonZeroPoint(returnPoint) && IssueCargoReturnSequence(actions, worker.Tag, returnPoint, townhallUnit.Tag, assignedMineral))
                    {
                        _cargoReturnSequenceActive.Add(worker.Tag);
                        Console.WriteLine($"[MINING CARGO] frame={_currentFrame} worker={worker.Tag} observedWorkers={liveWorkers.Count} configuredWorkers={Settings.WorkerCount} townhall={townhallUnit.Tag} stage=MOVE queued=false moveAgainFrame={_currentFrame + 1}");
                    }
                }

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

                var carrying = worker.IsCarrying;
                var wasCarrying = worker.WasCarrying;

                if (TryContinueCargoReturn(actions, worker, townhallUnit))
                {
                    continue;
                }

                if (carrying && !wasCarrying && townhallUnit != null && !_cargoReturnSequenceActive.Contains(worker.Tag))
                {
                    var returnPoint = ResolveNonZeroPoint(mineral.SmReturnPoint, mineral.ReturnPoint);
                    if (returnPoint != null && IssueCargoReturnSequence(actions, worker.Tag, returnPoint, townhallUnit.Tag, mineral))
                    {
                        _cargoReturnSequenceActive.Add(worker.Tag);
                        Console.WriteLine($"[MINING CARGO] frame={_currentFrame} worker={worker.Tag} observedWorkers={liveWorkers.Count} configuredWorkers={Settings.WorkerCount} townhall={townhallUnit.Tag} stage=MOVE queued=false moveAgainFrame={_currentFrame + 1}");
                        continue;
                    }
                }

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

        private bool IssueCargoReturnSequence(
            List<SC2Action> actions,
            ulong workerTag,
            Vector2Dto returnPoint,
            ulong townhallTag,
            OrderedMineral mineral)
        {
            if (actions == null || workerTag == 0 || townhallTag == 0 || returnPoint == null)
            {
                return false;
            }

            var moveAction = CreateMoveAction(
                workerTag,
                new Point2D { X = returnPoint.X, Y = returnPoint.Y },
                true);
            actions.Add(moveAction);
            _pendingCargoReturnSequences[workerTag] = new CargoReturnSequenceState
            {
                ReturnPoint = returnPoint,
                HarvestPoint = mineral?.HarvestPoint ?? new Vector2Dto(),
                MineralTag = mineral?.UnitTag ?? 0,
                TownhallTag = townhallTag,
                CreatedFrame = _currentFrame,
                MoveAgainFrame = _currentFrame + 1,
                MoveRepeatCount = 0,
                GatherRepeatCount = 0
            };
            return true;
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

            var tempTag = state.CurrentMineralTag;
            state.CurrentMineralTag = state.AlternateMineralTag;
            state.AlternateMineralTag = tempTag;

            var tempPos = state.CurrentMineralPos;
            state.CurrentMineralPos = state.AlternateMineralPos;
            state.AlternateMineralPos = tempPos;
        }

        private void UpdatePendingCargoReturnTarget(
            ulong workerTag,
            AssignedWorkerDto assignedWorker,
            List<TeamPatchAssignmentDto> teamAssignments)
        {
            if (!_pendingCargoReturnSequences.TryGetValue(workerTag, out var sequence)
                || assignedWorker == null
                || assignedWorker.MiningTargets == null)
            {
                return;
            }

            var nextTarget = assignedWorker.MiningTargets.ElementAtOrDefault(assignedWorker.Mti);
            if (nextTarget == null || nextTarget.ResourceUnitId == 0 || !HasNonZeroPoint(nextTarget.HarvestPoint))
            {
                return;
            }

            sequence.MineralTag = nextTarget.ResourceUnitId;
            sequence.HarvestPoint = nextTarget.HarvestPoint;
            Console.WriteLine($"[MINING JIT RETURN] frame={_currentFrame} worker={workerTag} nextTarget={nextTarget.ToResourceLabel} footprint=({nextTarget.HarvestPoint.X:F2},{nextTarget.HarvestPoint.Y:F2})");
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
                        //UnitTags = { worker.Tag },
                        //QueueCommand = false
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

        private static SC2Action? IssueHarvestGather(ulong workerTag, ulong mineralTag, bool queued)
        {
            if (workerTag == 0 || mineralTag == 0)
            {
                return null;
            }

            return new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.HARVEST_GATHER,
                        UnitTags = { workerTag },
                        TargetUnitTag = mineralTag,
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
                    var workerLabel = ResolveWorkerRole(null, workerTag);
                    Console.WriteLine($"[MINING COMMAND1] frame={_currentFrame} worker={workerTag} Label={workerLabel} command={commandName}{target}{targetTag}{queued}");
                }
            }
        }

        private void FilterPostFinalGatherActions(List<SC2Action> actions, ObservationSnapshotDto snapshot)
        {
            if (actions == null || snapshot == null)
            {
                return;
            }

            actions.RemoveAll(action =>
            {
                var command = action?.ActionRaw?.UnitCommand;
                if (command?.UnitTags == null)
                {
                    return false;
                }

                var terminalTag = command.UnitTags.FirstOrDefault(tag => _finalGatherWorkers.Contains(tag));
                if (terminalTag == 0)
                {
                    return false;
                }

                if (_finalGatherFrames.TryGetValue(terminalTag, out var finalFrame)
                    && _currentFrame == finalFrame)
                {
                    return !_finalGatherActions.Values.Any(finalAction => ReferenceEquals(finalAction, action));
                }

                return true;
            });
        }

        private static void RemoveWorkerActions(List<SC2Action> actions, ulong workerTag)
        {
            actions?.RemoveAll(action =>
                action?.ActionRaw?.UnitCommand?.UnitTags?.Contains(workerTag) == true);
        }

        private void AddAction(List<SC2Action> actions, SC2Action? action)
        {
            if (action == null)
            {
                return;
            }

            actions.Add(action);
            ScheduleMoveReplay(action);
        }

        private void ScheduleMoveReplay(SC2Action action)
        {
            var command = action.ActionRaw?.UnitCommand;
            if (command?.AbilityId != (int)Abilities.MOVE || command.UnitTags == null || command.UnitTags.Count == 0)
            {
                return;
            }

            var replayFrame = _currentFrame + 1;
            if (!_scheduledMoveReplays.TryGetValue(replayFrame, out var scheduledActions))
            {
                scheduledActions = new List<SC2Action>();
                _scheduledMoveReplays[replayFrame] = scheduledActions;
            }

            scheduledActions.Add(CloneMoveAction(command));
        }

        private static SC2Action CreateMoveAction(ulong workerTag, Point2D target, bool queued)
        {
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

        private static SC2Action CloneMoveAction(ActionRawUnitCommand command)
        {
            var replay = new ActionRawUnitCommand
            {
                AbilityId = command.AbilityId,
                TargetUnitTag = command.TargetUnitTag,
                QueueCommand = command.QueueCommand
            };
            replay.UnitTags.AddRange(command.UnitTags);
            if (command.TargetWorldSpacePos != null)
            {
                replay.TargetWorldSpacePos = new Point2D
                {
                    X = command.TargetWorldSpacePos.X,
                    Y = command.TargetWorldSpacePos.Y
                };
            }

            return new SC2Action
            {
                ActionRaw = new ActionRaw { UnitCommand = replay }
            };
        }

        private List<SC2Action> FilterCarryingWorkerActions(
            IEnumerable<SC2Action> scheduledActions,
            ObservationSnapshotDto snapshot)
        {
            var filtered = new List<SC2Action>();
            foreach (var action in scheduledActions ?? Enumerable.Empty<SC2Action>())
            {
                var command = action?.ActionRaw?.UnitCommand;
                if (command?.UnitTags == null || command.UnitTags.Count == 0)
                {
                    continue;
                }

                var invalid = command.UnitTags.Any(tag =>
                    snapshot.SelfUnits.TryGetValue(tag, out var worker)
                    && worker.IsCarrying
                    && (!_cargoReturnSequenceActive.Contains(tag)
                        || (command.AbilityId != (int)Abilities.HARVEST_RETURN
                            && command.AbilityId != (int)Abilities.MOVE)));
                if (!invalid)
                {
                    filtered.Add(action);
                }
            }

            return filtered;
        }

        private bool TryContinueCargoReturn(
            List<SC2Action> actions,
            SnapshotUnit worker,
            SnapshotUnit townhallUnit)
        {
            if (worker == null || !_cargoReturnSequenceActive.Contains(worker.Tag)
                || !_pendingCargoReturnSequences.TryGetValue(worker.Tag, out var sequence))
            {
                return false;
            }

            var nearTownhall = townhallUnit != null
                && DistanceSquared(new Vector2Dto(worker.Pos.X, worker.Pos.Y), new Vector2Dto(townhallUnit.Pos.X, townhallUnit.Pos.Y)) <= 9f;
            if (!nearTownhall)
            {
                if (sequence.MoveAgainFrame == _currentFrame && sequence.MoveRepeatCount < 2)
                {
                    AddAction(actions, CreateMoveAction(worker.Tag, new Point2D { X = sequence.ReturnPoint.X, Y = sequence.ReturnPoint.Y }, true));
                    sequence.MoveRepeatCount++;
                    sequence.MoveAgainFrame = sequence.MoveRepeatCount == 1 ? _currentFrame + 13 : 0;
                }
                return true;
            }

            if (!sequence.ReturnQueued)
            {
                AddAction(actions, IssueHarvestReturn(worker.Tag, true));
                sequence.ReturnQueued = true;
                return true;
            }

            if (!sequence.DepositObserved)
            {
                if (worker.IsCarrying)
                {
                    return true;
                }

                sequence.DepositObserved = true;
                sequence.GatherAgainFrame = _currentFrame + 1;
                sequence.GatherQueuedFrame = _currentFrame + 15;
            }

            if (sequence.MineralTag == 0 || !HasNonZeroPoint(sequence.HarvestPoint))
            {
                _cargoReturnSequenceActive.Remove(worker.Tag);
                _pendingCargoReturnSequences.Remove(worker.Tag);
                return false;
            }

            if (sequence.GatherAgainFrame == _currentFrame && sequence.GatherRepeatCount < 2)
            {
                AddAction(actions, IssueHarvestGather(worker.Tag, sequence.MineralTag, false));
                AddAction(actions, CreateMoveAction(worker.Tag, new Point2D { X = sequence.HarvestPoint.X, Y = sequence.HarvestPoint.Y }, false));
                sequence.GatherRepeatCount++;
                sequence.GatherAgainFrame = sequence.GatherRepeatCount == 1 ? _currentFrame + 13 : 0;
                return true;
            }

            if (sequence.GatherQueuedFrame == _currentFrame)
            {
                CancelScheduledMoveReplays(worker.Tag);
                RemoveWorkerActions(actions, worker.Tag);
                var finalGatherAction = IssueHarvestGather(worker.Tag, sequence.MineralTag, true);
                AddAction(actions, finalGatherAction);
                _finalGatherActions[worker.Tag] = finalGatherAction;
                _finalGatherWorkers.Add(worker.Tag);
                _finalGatherFrames[worker.Tag] = _currentFrame;
                _cargoReturnSequenceActive.Remove(worker.Tag);
                _pendingCargoReturnSequences.Remove(worker.Tag);
                return true;
            }

            return true;
        }

        private void CancelScheduledMoveReplays(ulong workerTag)
        {
            foreach (var frame in _scheduledMoveReplays.Keys.ToList())
            {
                var actions = _scheduledMoveReplays[frame];
                actions.RemoveAll(action => action.ActionRaw?.UnitCommand?.UnitTags?.Contains(workerTag) == true);
                if (actions.Count == 0)
                {
                    _scheduledMoveReplays.Remove(frame);
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
            if (_mapData != null) foreach (var com in _mapData.MineralCenterOfMass) { if (com == null) continue; DrawCircle(com, 0.5f, new Color { R = 0, G = 255, B = 0 }, 12f); }
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

        private void DrawExpansionPoints()
        {
            if (_mapData?.ExpansionPoints != null) foreach (var kvp in _mapData.ExpansionPoints) { if (kvp.Value?.ExpansionPoint == null) continue; DrawCircle(kvp.Value.ExpansionPoint, 2.5f, new Color { R = 0, G = 0, B = 255 }, 12f); ManagerDebugService.DrawText($"Exp {kvp.Key}", new Point { X = kvp.Value.ExpansionPoint.X, Y = kvp.Value.ExpansionPoint.Y, Z = 13f }, new Color { R = 255, G = 255, B = 255 }, 12); }
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
            DrawCircle(placement, 1.5f, color, 12f);
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
