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
        // Collision pass-through detection as a service (not a competing manager). Runs
        // inside the instruction-list loop so a mineral-walk detour suspends the LIST
        // without advancing the instruction pointer and the list resumes after it.
        private readonly WorkerCollisionAvoidanceService _collisionAvoidanceService = new WorkerCollisionAvoidanceService();

        // Per-frame visual alert colors for collision-managed and bump-walk workers.
        // Orange while a detour or the T3/Y3 bump approach owns the worker, red for the
        // entire time the worker is within 1u of the worker it is avoiding. Draw-time
        // only (ResolveWorkerDisplayColor); labels and list logic are never mutated.
        private enum WorkerVisualAlert { None, Orange, Red }
        private const float CollisionRedAlertRange = 1.0f;
        private const int BumpOrangeMaxRelativeFrame = 25;
        private static readonly Color CollisionOrangeColor = new Color { R = 255, G = 165, B = 0 };
        private static readonly Color CollisionRedColor = new Color { R = 255, G = 0, B = 0 };
        private readonly Dictionary<ulong, WorkerVisualAlert> _workerVisualAlerts = new Dictionary<ulong, WorkerVisualAlert>();
        private bool _anyWorkerRed;
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
        // Current mineral bank, refreshed every frame in OnFrame — the gate for the
        // S2 spawning-pool preposition return (owner spec 2026-10-13).
        private int _currentMinerals;
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
        // Stationary-based harvest-frame tracking: counts frames only while a worker
        // is not moving with 1183 at a mineral, and releases staged workers 15 frames
        // before the active harvester's 1184 flip. Source of truth for staging decisions.
        private readonly HarvestFrameTracker _harvestFrameTracker = new HarvestFrameTracker();

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
        // 5x5 wait-role mineral walk: first frame each worker's WaitWalkClearGate held
        // (blocked), for the absolute-frame timeout escape. The inverted-gate jump resets
        // InstructionStartFrame every loop pass, so the hold age must be tracked here.
        private readonly Dictionary<ulong, int> _waitWalkGateHoldStartFrames = new Dictionary<ulong, int>();
        private const float WaitWalkArrivalRadius = 2.5f;
        // "Clear of the partner's path" means the partner is nearly at its mineral (the
        // shared corridor behind it is empty), or the walker has gone past the partner.
        // The earlier 0.15 "meaningfully ahead" bar released the walkers into the
        // partner's corridor and bounced Y1 off its acceleration line instead of letting
        // the bumper push it through.
        private const float WaitWalkPartnerClearProgress = 0.75f;
        private const float WaitWalkPastPartnerMargin = 0.10f;
        private const int WaitWalkGateTimeoutFrames = 70;

        private readonly object _instructionTraceLock = new object();
        private readonly JsonSerializerOptions _instructionTraceJsonOptions = new JsonSerializerOptions { WriteIndented = false };
        private readonly string _instructionTraceFile;
        private readonly Dictionary<ulong, string> _lastInstructionTraceState = new Dictionary<ulong, string>();
        private long _miningCommandSequence;
        private readonly HashSet<ulong> _returnInterruptIssued = new HashSet<ulong>();
        private readonly Dictionary<ulong, (Vector2Dto Target, int Frame)> _returnInterruptPendingMove = new Dictionary<ulong, (Vector2Dto Target, int Frame)>();
        // 5x5 Version 2 A-miner rule: the FIRST return after picking up the A mineral
        // goes out on the speed-mining return point (per the "A mineral starting
        // workers" spec image); every later return uses the mineral's normal return
        // point. One SM return per worker, tracked here.
        private readonly HashSet<ulong> _returnInterruptSmFirstTripUsed = new HashSet<ulong>();
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
            _harvestFrameTracker.Reset();
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
            // Collision/bump visual alerts take precedence over team colors for the
            // frame they are active (orange = detour or bump approach, red = within 1u
            // of the avoided worker). Draw-time only — the label itself is unchanged.
            if (_workerVisualAlerts.TryGetValue(workerTag, out var alert))
            {
                if (alert == WorkerVisualAlert.Red)
                {
                    return CollisionRedColor;
                }

                if (alert == WorkerVisualAlert.Orange)
                {
                    return CollisionOrangeColor;
                }
            }

            var assignedWorkerEntry = ResolveBuildGreedyAssignments(startIndex)
                .SelectMany(assignment => assignment.Workers ?? new List<WorkerEntryDto>())
                .FirstOrDefault(worker => worker != null && worker.UnitTag == workerTag);
            var storedWorker = GetStoredWorkersForStart(startIndex)
                ?.FirstOrDefault(worker => worker != null && worker.UnitTag == workerTag);
            var startLabel = assignedWorkerEntry?.StartLabel
                ?? assignedWorkerEntry?.Label
                ?? storedWorker?.StartLabel
                ?? storedWorker?.Label;
            var assignedWorker = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?.FirstOrDefault(worker => worker != null && worker.UnitID == workerTag);

            // Far-B mineral overrides (owner spec 2026-10-13): a worker takes the
            // SB/BB/YB color while its current mining target is that mineral —
            // regardless of team or worker count. Swing workers (T5/Y5) pick the
            // color up for their one far-B chapter and drop it on the next.
            var currentTargetLabel = assignedWorker?.MiningTargets?.ElementAtOrDefault(assignedWorker.Mti)?.ResourceLabel;
            if (string.Equals(currentTargetLabel, "SB", StringComparison.OrdinalIgnoreCase)) return TeamColorService.Salmon;
            if (string.Equals(currentTargetLabel, "BB", StringComparison.OrdinalIgnoreCase)) return TeamColorService.Green;
            if (string.Equals(currentTargetLabel, "YB", StringComparison.OrdinalIgnoreCase)) return TeamColorService.Orange;
            if (string.Equals(currentTargetLabel, "SA", StringComparison.OrdinalIgnoreCase)) return TeamColorService.Purple;

            var assignedWorkerCount = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)?.Count ?? 0;
            if (assignedWorkerCount == 12)
            {
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
        // Morph guard: one [WORKER MORPHED] line per expended worker (B2's tag lives
        // on as the spawning pool; it must never be commanded again).
        private readonly HashSet<ulong> _morphedWorkerReported = new HashSet<ulong>();
        // Terminal books (B2's book ends on its BUILD_SPAWNINGPOOL row) report their
        // exhaustion once instead of failing every frame.
        private readonly HashSet<ulong> _exhaustedBookReported = new HashSet<ulong>();

        // 13-B3 extractor trick driver (owner spec 2026-10-15 22:20). Pure
        // observation-driven state machine around the book's terminal
        // BUILD_EXTRACTOR row: phase 1 = build issued, keep trying the IMMEDIATE
        // larva morph until the observation shows it begun; phase 2 = cancel issued,
        // wait for the cancelled extractor to restore the drone, then load the
        // build manager's "b3ret" BB loop. It never touches MiningTargets, orders,
        // or any other worker — the book and the two guards above own B3 while it
        // walks/morphs.
        private const string B3ReturnInstructionSet = "b3ret";
        private const int B3TrickMorphTimeoutFrames = 900;
        // Grace period after the extractor unit is first observed before the abort
        // may fire (run9 postmortem): the extractor commit is what frees the supply
        // point the larva morph needs, and the morph is issued the same frame — an
        // abort without this grace kills a healthy trick one frame old.
        private const int B3TrickAbortGraceFrames = 120;
        private int _b3TrickPhase; // 0 idle, 1 build issued, 2 cancel issued, 3 done
        private ulong _b3TrickWorkerTag;
        private ulong _b3TrickLarvaTag;
        private int _b3TrickBuildFrame = -1;
        private int _b3TrickExtractorFirstSeenFrame = -1;
        // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): armed the frame
        // the terminal BuildHatchery row actually issues (execution sets it);
        // the watcher below resolves SUCCESS (a hatchery under construction is
        // observed at the site) or FAILURE (none within the verify window) and
        // freezes the bot either way so the owner can inspect the spot.
        private const string B3MacroHatcheryInstructionSet = "b3mh";
        private const int B3MhBuildVerifyFrames = 45;
        private int _b3MhBuildIssuedFrame = -1;
        private bool _b3MhResolved;
        private string _b3MhBanner = string.Empty;
        // Previous frame's mineral bank, for morph-spend drop detection: the
        // larva morph spends 50 the frame it engages, a sudden ~50 drop at any
        // bank level (run10: 120 -> 70, never below 50) — income is ~1/frame,
        // so a drop in [45,70) is unambiguously the morph (the extractor's own
        // 75 spend and the ideal both-same-frame 125 drop are excluded; the
        // latter lands the bank under 50, caught by the bank check).
        private int _b3TrickLastMinerals = -1;

        public IEnumerable<SC2Action> OnFrame(ResponseObservation observation)
        {
            _currentFrame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            _currentMinerals = observation?.Observation?.PlayerCommon == null ? 0 : (int)observation.Observation.PlayerCommon.Minerals;
            if ((_currentFrame > 0 && _currentFrame % 5 == 0) || _anyWorkerRed)
            {
               //Debugger.Break();
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
            UpdateCollisionVisualStates();
            WriteInstructionTrace();

            // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): resolve
            // the build milestone (success/failure) BEFORE the frozen check so
            // the watcher keeps running while frozen. The freeze strips every
            // unit command from this manager; the BabySharkAI frame loop
            // strips the other managers' — debug draws (banner, circles) pass
            // through so the frozen scene stays annotated. The SC2 protocol
            // has no true pause: this is the closest the bot can do, plus a
            // Debugger.Break at the milestone so a VS session stops exactly
            // there for stepping.
            UpdateB3MacroHatcheryTest(observation);
            if (Settings.MacroHatcheryTestFrozen)
            {
                DrawB3MacroHatcheryBanner();
                return Array.Empty<SC2Action>();
            }

            var actions = new List<SC2Action>();
            if (Settings.SimulatedStartActive || Settings.BuildOwnsWorkerCommands)
            {
                Console.WriteLine($"[MINING REACH] commands skipped frame={_currentFrame}: simulated={Settings.SimulatedStartActive} buildOwns={Settings.BuildOwnsWorkerCommands}");
                return actions;
            }

            // Update phase state (Speed Mining) based on current functional worker count.
            var workerCount = snapshot.SelfUnits.Values.Count(u => u != null && WorkerTypes.Contains((UnitTypes)u.UnitType) && u.IsCompleted);
            UpdatePhaseState(workerCount);
            // 13-B3 extractor trick driver runs BEFORE the book execution pass so a
            // completed cancel restores + reloads B3's book the same frame.
            actions.AddRange(UpdateB3ExtractorTrick(observation));
            var commandContexts = new List<InstructionCommandContext>();
            actions.AddRange(ExecuteRuntimeWorkerInstructions(observation, commandContexts));
            actions.AddRange(IssueReturnInterrupts(observation, actions, commandContexts));

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
            UpdateHarvestFrameTracking(liveWorkers);
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
                            TargetAbilityId2 = instruction?.TargetAbilityId2 ?? -1,
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

                // Morph guard (owner spec 2026-10-13): a Zerg drone ordered to build
                // morphs INTO the building — B2's tag becomes the spawning pool and
                // stops being a worker — but the assigned-worker list still carries
                // the tag. Skip (reporting once) any assigned worker whose live unit
                // is no longer a worker type: commanding the pool would spam orders
                // at a building and an unqueued move could cancel its construction.
                if (!WorkerTypes.Contains((UnitTypes)observedWorker.UnitType))
                {
                    if (_morphedWorkerReported.Add(assignedWorker.UnitID))
                    {
                        Console.WriteLine($"[WORKER MORPHED] frame={_currentFrame} tag={assignedWorker.UnitID} role={assignedWorker.Role} unitType={observedWorker.UnitType} — no longer a worker; book execution skipped");
                    }
                    continue;
                }

                // Construction-walk guard (owner spec 13-B3 extractor trick, 2026-10-15):
                // while a drone's active order is a BUILD ability it is walking to the
                // construction site and has NOT committed the morph yet. An unqueued
                // collision-avoidance mineral-walk during this window cancels the morph
                // (the 2026-10-15 14:04 run: frame 924 Avoid.Y2 mineral-walk killed
                // 13-B3's BUILD_EXTRACTOR one frame after the book issued it, and the
                // leftover gather order looped BB mining — "mines once, returns to BB").
                // Leave builders untouched until the unit type flips; the morph guard
                // above takes over from there. Same protection covers B2's pool walk.
                if (observedWorker.OrderAbilityIds != null
                    && observedWorker.OrderAbilityIds.Count > 0
                    && (observedWorker.OrderAbilityIds[0] == (int)Abilities.BUILD_EXTRACTOR
                        || observedWorker.OrderAbilityIds[0] == (int)Abilities.BUILD_SPAWNINGPOOL
                        // B3 Macro Hatchery test (owner spec 2026-10-16 18:35):
                        // same protection for the hatchery build walk — an
                        // unqueued detour SMART would cancel the morph walk.
                        || observedWorker.OrderAbilityIds[0] == (int)Abilities.BUILD_HATCHERY))
                {
                    continue;
                }

                if (!Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitID, out var runtimeWorker))
                {
                    continue;
                }

                runtimeWorker.Position = assignedWorker.CurrentXY ?? runtimeWorker.Position;

                // 13-B3 extractor trick (run 2026-10-15 23:09): while the book is
                // gate-held on the VaWalkRedirect row, a collision detour would
                // replace the ONE walk gather with a detour SMART (frame 890: B3's
                // walk to the walk mineral was overwritten by a walk to BB) and the
                // redirect gate can never fire — the book issues no commands in this
                // window to re-establish the walk, so B3 stranded on its self-
                // perpetuating BB harvest. Skip collision intervention for this
                // worker ONLY while that row is pending; the other worker of the
                // pair still detours around B3 via its own pass.
                // Trick-commit window (owner spec 2026-10-16 00:45, run4 postmortem):
                // the run4 game the 935 collision SMART killed 13-B3's build WALK
                // (the drone's order is plain MOVE until it is in build range, so
                // the construction-walk guard above did not cover it) — the
                // extractor never existed, the larva morph spent 50 for nothing,
                // and the driver sat in phase 1 until the silent timeout. From the
                // frame the va set's BuildExtractor row arms the driver until the
                // cancel restores the drone (phase 1 or 2), collision must not
                // touch this worker at all.
                if (Settings.WorkerCollisionAvoidance
                    && !IsGateHeldVaWalkRedirect(runtimeWorker)
                    && !IsB3TrickWorkerCommitted(runtimeWorker)
                    && !IsB3MacroHatcheryWalker(runtimeWorker))
                {
                    var intervention = _collisionAvoidanceService.GetIntervention(
                        runtimeWorker,
                        assignedWorker,
                        assignedWorkers,
                        snapshot,
                        _currentFrame,
                        out var collisionAction);
                    if (intervention == WorkerCollisionIntervention.BeginDetour
                        || intervention == WorkerCollisionIntervention.ContinueDetour)
                    {
                        if (collisionAction != null)
                        {
                            actions.Add(collisionAction);
                            contexts.Add(new InstructionCommandContext
                            {
                                WorkerTag = runtimeWorker.UnitTag,
                                InstructionSet = "collision",
                                InstructionIndex = runtimeWorker.CurInstrIdx,
                                InstructionCommand = "MineralWalkSmart",
                                InstructionPoint = string.Empty
                            });
                        }

                        // The mineral walk owns the worker's commands while the detour
                        // runs. The instruction pointer is deliberately left untouched so
                        // the LIST resumes at the suspended row once the pair is clear.
                        continue;
                    }

                    if (intervention == WorkerCollisionIntervention.EndDetour
                        && TryResumeInstructionAfterCollisionDetour(runtimeWorker, assignedWorker, actions, contexts))
                    {
                        continue;
                    }
                }

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
                        ReportBookExhaustedOnce(runtimeWorker, "instruction list ended without a jump row");
                        break;
                    }

                    if (!AdvanceSatisfiedControlInstructions(runtimeWorker, assignedWorker))
                    {
                        break;
                    }

                    if (runtimeWorker.CurInstrIdx >= runtimeWorker.Instructions.Count)
                    {
                        ReportBookExhaustedOnce(runtimeWorker, "instruction list ended after control row");
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
                        && (runtimeWorker.CurrentAbilityId == instruction.TargetAbilityId
                            || (instruction.TargetAbilityId2 >= 0 && runtimeWorker.CurrentAbilityId == instruction.TargetAbilityId2));
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
                        // Lost-tread self-heal: SC2 completes a move early when the destination
                        // cell is blocked (a parked worker sitting on the gate point), leaving
                        // the worker idle short of the gate with no row left to re-issue the
                        // walk — observed as workers freezing near the hatchery after a few
                        // cycles. A position-gated Gather/Return targets a unit, so the game
                        // walks the worker into range itself; fire it instead of stalling.
                        if (instruction.RelativeFrame < 0
                            && instruction.PositionTolerance > 0
                            && targetPoint != null
                            && runtimeWorker.CurrentAbilityId < 0
                            && (instruction.Command == WorkerInstructionCommand.Gather
                                || instruction.Command == WorkerInstructionCommand.Return))
                        {
                            // A Gather self-heal must re-clear the mineral-readiness
                            // gate — healing a Gather straight onto a busy mineral is
                            // one of the random-mineral leak paths (owner report
                            // 2026-10-16: workers landing on unrelated minerals).
                            if (instruction.Command == WorkerInstructionCommand.Gather
                                && !IsGatherRowSendReady(instruction))
                            {
                                break;
                            }
                            positionSatisfied = true;
                        }
                        else
                        {
                            break;
                        }
                    }

                    // Gather-send gate (owner spec 2026-10-16, worker collision pass):
                    // hold a condition-gated Gather whose mineral is busy beyond the
                    // release-lead window instead of issuing the Smart command early
                    // — an early gather is what strands the worker on a busy mineral
                    // and gets it re-pathed to a random one. The row is retried every
                    // frame, so the Smart lands exactly when the mineral frees up
                    // (or is about to, within the lead).
                    if (!IsGatherRowSendReady(instruction))
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
                    // Two consecutive rows can resolve to the same in-game command in one
                    // frame (e.g. the bump5x5T4 book: row 1 BumpSmBumper fail-opens to the
                    // partner's live position on the same frame row 2 BumpPartner becomes
                    // due). The engine executes only the last one anyway, so skip the
                    // redundant emit instead of logging a fake "conflicting command" pair.
                    if (action != null && !IsDuplicateWorkerFrameAction(actions, action, runtimeWorker.UnitTag))
                    {
                        actions.Add(action);
                        contexts.Add(new InstructionCommandContext
                        {
                            WorkerTag = runtimeWorker.UnitTag,
                            InstructionSet = instruction.InstructionSet,
                            InstructionIndex = executedIndex,
                            InstructionCommand = instruction.Command.ToString(),
                            InstructionPoint = instruction.Point.ToString(),
                            TargetPointReference = instruction.TargetPointReference
                        });
                    }
                    if (followUpAction != null && !IsDuplicateWorkerFrameAction(actions, followUpAction, runtimeWorker.UnitTag))
                    {
                        actions.Add(followUpAction);
                        contexts.Add(new InstructionCommandContext
                        {
                            WorkerTag = runtimeWorker.UnitTag,
                            InstructionSet = instruction.InstructionSet,
                            InstructionIndex = executedIndex,
                            InstructionCommand = instruction.Command.ToString(),
                            InstructionPoint = instruction.Point.ToString(),
                            TargetPointReference = instruction.TargetPointReference
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

        /// <summary>
        /// Re-issues the instruction row a collision detour interrupted. After a mineral
        /// walk the frame gates of the suspended row are still satisfied and position
        /// gates fire once the worker walks back into range, but an ability-gated or
        /// position-gated row the walk left behind (e.g. the queued Return waiting at the
        /// hatchery turn point) would wait forever because the observed ability chain no
        /// longer matches the row's gate. Firing exactly one suspended row re-establishes
        /// the command the list intended; the normal gates take over from the next frame.
        /// Returns false when the suspended row is a control row (Wait/Jump/store/load) —
        /// those carry no command to re-issue, so the normal control pass resumes the
        /// list from this frame on.
        /// </summary>
        private bool TryResumeInstructionAfterCollisionDetour(
            RuntimeWorkerState runtimeWorker,
            AssignedWorkerDto assignedWorker,
            List<SC2Action> actions,
            List<InstructionCommandContext> contexts)
        {
            if (runtimeWorker.CurInstrIdx < 0 || runtimeWorker.CurInstrIdx >= runtimeWorker.Instructions.Count)
            {
                return false;
            }

            var instruction = runtimeWorker.Instructions[runtimeWorker.CurInstrIdx];
            if (instruction == null)
            {
                return false;
            }

            switch (instruction.Command)
            {
                case WorkerInstructionCommand.Move:
                case WorkerInstructionCommand.Gather:
                case WorkerInstructionCommand.Return:
                case WorkerInstructionCommand.Stop:
                case WorkerInstructionCommand.GatherAndMove:
                case WorkerInstructionCommand.MoveAndGather:
                case WorkerInstructionCommand.BuildExtractor:
                case WorkerInstructionCommand.BuildSpawningPool:
                    break;
                default:
                    return false;
            }

            // A Gather the detour interrupted must re-clear the mineral-readiness
            // gate before being re-issued — resuming it onto a mineral that went
            // busy during the walk leaks the worker to a random mineral the same
            // way the self-heal did. Returning false hands the row back to the
            // normal loop, where the gather-send gate holds it frame by frame.
            if (!IsGatherRowSendReady(instruction))
            {
                return false;
            }

            var resumedIndex = runtimeWorker.CurInstrIdx;
            var targetPoint = ResolveInstructionPoint(runtimeWorker, assignedWorker, instruction);
            var action = ExecuteWorkerInstruction(runtimeWorker, instruction, assignedWorker, targetPoint, out var followUpAction);
            if (action == null)
            {
                return false;
            }

            actions.Add(action);
            contexts.Add(new InstructionCommandContext
            {
                WorkerTag = runtimeWorker.UnitTag,
                InstructionSet = instruction.InstructionSet,
                InstructionIndex = resumedIndex,
                InstructionCommand = "Resume" + instruction.Command,
                InstructionPoint = instruction.Point.ToString(),
                TargetPointReference = instruction.TargetPointReference
            });
            if (followUpAction != null)
            {
                actions.Add(followUpAction);
                contexts.Add(new InstructionCommandContext
                {
                    WorkerTag = runtimeWorker.UnitTag,
                    InstructionSet = instruction.InstructionSet,
                    InstructionIndex = resumedIndex,
                    InstructionCommand = "Resume" + instruction.Command,
                    InstructionPoint = instruction.Point.ToString(),
                    TargetPointReference = instruction.TargetPointReference
                });
            }

            runtimeWorker.CurInstrIdx++;
            if (runtimeWorker.CurInstrIdx < runtimeWorker.Instructions.Count)
            {
                runtimeWorker.TargetAbilityId = runtimeWorker.Instructions[runtimeWorker.CurInstrIdx].TargetAbilityId;
            }

            Console.WriteLine($"[WORKER COLLISION] frame={_currentFrame} worker={runtimeWorker.UnitTag} command=RESUME-LIST resumedInstruction={resumedIndex} resumedCommand={instruction.Command} nextIndex={runtimeWorker.CurInstrIdx}/{(runtimeWorker.Instructions?.Count ?? 0)}");
            return true;
        }

        /// <summary>
        /// Rebuilds the per-frame visual alert colors so collision-managed and bump-walk
        /// workers can be watched in the debug draw. Runs every frame after observation
        /// processing (independent of who owns worker commands), so the T3/Y3 bump
        /// workers are orange from the very first frame of their set.
        /// </summary>
        private void UpdateCollisionVisualStates()
        {
            _workerVisualAlerts.Clear();
            _anyWorkerRed = false;

            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();
            if (snapshot?.SelfUnits == null || assignedWorkers.Count == 0)
            {
                return;
            }

            foreach (var assignedWorker in assignedWorkers)
            {
                if (assignedWorker == null || assignedWorker.UnitID == 0)
                {
                    continue;
                }

                if (!snapshot.SelfUnits.TryGetValue(assignedWorker.UnitID, out var observed)
                    || observed?.Position == null)
                {
                    continue;
                }

                var alert = ResolveCollisionVisualAlert(assignedWorker, observed, assignedWorkers, snapshot);
                if (alert != WorkerVisualAlert.None)
                {
                    _workerVisualAlerts[assignedWorker.UnitID] = alert;
                    if (alert == WorkerVisualAlert.Red)
                    {
                        _anyWorkerRed = true;
                    }
                }
            }
        }

        /// <summary>
        /// Orange/red rules for one worker:
        /// - Collision detour: orange while the mineral walk owns the worker, red for the
        ///   entire time it is within 1u of a carrying worker it is passing through.
        /// - Initial bump set: orange for the bump approach, red within 1u of the
        ///   role-1 partner (5x5 Version 2: T4->T1, Y4->Y1; experiment 1: T3->T1,
        ///   Y3->Y1). Back to normal team colors as soon as the worker is under the
        ///   move/stop rows, past relative frame 25 of the bump set, or no longer
        ///   within 1u (falls back to orange).
        /// </summary>
        private WorkerVisualAlert ResolveCollisionVisualAlert(
            AssignedWorkerDto assignedWorker,
            WorkerEntryDto observed,
            List<AssignedWorkerDto> assignedWorkers,
            ObservationSnapshotDto snapshot)
        {
            Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitID, out var runtimeWorker);
            if (runtimeWorker != null && runtimeWorker.CollisionDetourActive)
            {
                return IsWithinRangeOfCarryingWorker(assignedWorker.UnitID, observed.Position, assignedWorkers, snapshot)
                    ? WorkerVisualAlert.Red
                    : WorkerVisualAlert.Orange;
            }

            if (runtimeWorker == null
                || runtimeWorker.Instructions == null
                || runtimeWorker.Instructions.Count == 0
                || runtimeWorker.CurInstrIdx < 0
                || runtimeWorker.CurInstrIdx >= runtimeWorker.Instructions.Count)
            {
                return WorkerVisualAlert.None;
            }

            var current = runtimeWorker.Instructions[runtimeWorker.CurInstrIdx];
            var setName = current?.InstructionSet ?? string.Empty;
            if (!setName.StartsWith("bump", StringComparison.OrdinalIgnoreCase))
            {
                return WorkerVisualAlert.None;
            }

            var relativeFrame = Settings.GetRelativeFrame(_currentFrame) - runtimeWorker.InstructionStartFrame;
            if (relativeFrame >= BumpOrangeMaxRelativeFrame)
            {
                return WorkerVisualAlert.None;
            }

            // Under the move (or the alignment stop) the worker is back on its scripted
            // path — normal team colors from here.
            if (current.Command == WorkerInstructionCommand.Move || current.Command == WorkerInstructionCommand.Stop)
            {
                return WorkerVisualAlert.None;
            }

            // Bump partner for the red-alert proximity check. 5x5 Version 2 bump roles
            // are T4 (bumps T1) and Y4 (bumps Y1); experiment 1 bumps with T3/Y3.
            string partnerRole;
            if (Settings.IsMagannatha5x5Rotation)
            {
                partnerRole = string.Equals(assignedWorker.Role, "T4", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : string.Equals(assignedWorker.Role, "Y4", StringComparison.OrdinalIgnoreCase) ? "Y1"
                    : string.Empty;
            }
            else
            {
                partnerRole = string.Equals(assignedWorker.Role, "T3", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : string.Equals(assignedWorker.Role, "Y3", StringComparison.OrdinalIgnoreCase) ? "Y1"
                    : string.Empty;
            }
            if (string.IsNullOrWhiteSpace(partnerRole))
            {
                return WorkerVisualAlert.None;
            }

            var partner = assignedWorkers.FirstOrDefault(candidate =>
                string.Equals(candidate?.Role, partnerRole, StringComparison.OrdinalIgnoreCase));
            if (partner?.CurrentXY != null
                && DistanceSquared(observed.Position, partner.CurrentXY) <= CollisionRedAlertRange * CollisionRedAlertRange)
            {
                return WorkerVisualAlert.Red;
            }

            return WorkerVisualAlert.Orange;
        }

        private static bool IsWithinRangeOfCarryingWorker(
            ulong workerTag,
            Vector2Dto position,
            List<AssignedWorkerDto> assignedWorkers,
            ObservationSnapshotDto snapshot)
        {
            var rangeSquared = CollisionRedAlertRange * CollisionRedAlertRange;
            foreach (var other in assignedWorkers)
            {
                if (other == null || other.UnitID == 0 || other.UnitID == workerTag || other.CurrentXY == null)
                {
                    continue;
                }

                if (!snapshot.SelfUnits.TryGetValue(other.UnitID, out var observedOther)
                    || observedOther == null
                    || !observedOther.IsCarrying)
                {
                    continue;
                }

                if (DistanceSquared(position, other.CurrentXY) <= rangeSquared)
                {
                    return true;
                }
            }

            return false;
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
                    && (runtimeWorker.CurrentAbilityId == instruction.TargetAbilityId
                        || (instruction.TargetAbilityId2 >= 0 && runtimeWorker.CurrentAbilityId == instruction.TargetAbilityId2));

                // Inverted-gate jump (bump walk loop): while the gate is unresolved the
                // jump fires and re-issues the harvest; when the gate resolves the row
                // passes through to the stop row. Only one command per frame comes out
                // of the loop, so the harvest is re-issued once per frame, not spammed
                // several times in the frame the jump runs.
                if (instruction.Command == WorkerInstructionCommand.Jump && instruction.InvertGate)
                {
                    if (instruction.JumpToInstructionIndex < 0 || instruction.JumpToInstructionIndex >= runtimeWorker.Instructions.Count)
                    {
                        ReportInstructionFailure(runtimeWorker, "inverted-gate jump row has invalid target index", instruction);
                        return false;
                    }

                    var gatePoint = ResolveInstructionPoint(runtimeWorker, assignedWorker, instruction);
                    if (gatePoint != null)
                    {
                        runtimeWorker.CurInstrIdx++;
                        runtimeWorker.InstructionStartFrame = Settings.GetRelativeFrame(_currentFrame);
                        continue;
                    }

                    runtimeWorker.CurInstrIdx = instruction.JumpToInstructionIndex;
                    runtimeWorker.InstructionStartFrame = Settings.GetRelativeFrame(_currentFrame);
                    return false;
                }

                var unconditionalJump = instruction.Command == WorkerInstructionCommand.Jump
                    && instruction.RelativeFrame < 0
                    && instruction.TargetAbilityId < 0;
                // Harvest-release gate (new logic): a Wait row flagged HarvestReleaseGate
                // passes when the row's target mineral is free (no active harvester) or
                // its active harvester will flip to 1184 within the release lead window.
                var harvestReleaseReady = instruction.Command == WorkerInstructionCommand.Wait
                    && instruction.HarvestReleaseGate
                    && IsHarvestReleaseReady(instruction.TargetId);
                // Mineral gate (owner spec 2026-10-13, B2 pool build): a Wait row
                // carrying RequireMinerals passes only when the mineral bank reaches
                // the threshold. Mineral-gated rows ignore the cargo-handoff edge —
                // the handoff wait immediately in front of the gate would otherwise
                // consume the deposit frame and leak the gate.
                var mineralGateSatisfied = instruction.Command == WorkerInstructionCommand.Wait
                    && instruction.RequireMinerals >= 0
                    && _currentMinerals >= instruction.RequireMinerals;
                // Absolute frame gate (owner spec 2026-10-13, Y2 BB trip): a Wait
                // row carrying RequireFrame passes only once the game reaches that
                // frame, so a chapter can be pinned to an absolute window.
                var frameGateSatisfied = instruction.Command == WorkerInstructionCommand.Wait
                    && instruction.RequireFrame >= 0
                    && _currentFrame >= instruction.RequireFrame;
                // Gated waits (mineral or frame) never pass on the cargo-handoff
                // edge — the handoff wait in front of them would leak them open.
                var gatedWait = instruction.Command == WorkerInstructionCommand.Wait
                    && (instruction.RequireMinerals >= 0 || instruction.RequireFrame >= 0);
                // A Wait row following a return-cargo command has no ability signature for
                // "the deposit finished": the drone's order list just goes idle. Let it pass
                // on the observed cargo transition instead, so the next set's moves cannot
                // overwrite a return that is still walking to the hatchery.
                var cargoHandedOff = instruction.Command == WorkerInstructionCommand.Wait
                    && !gatedWait
                    && runtimeWorker.CargoReturned;
                if (!frameSatisfied && !abilitySatisfied && !unconditionalJump && !cargoHandedOff && !harvestReleaseReady && !mineralGateSatisfied && !frameGateSatisfied)
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

            // 13-B3 extractor trick (owner spec 2026-10-15): the larva morphs
            // IMMEDIATELY — same frame as the extractor build (75 + 50 = the 125 the
            // gated wait confirmed). When no idle larva is visible this frame the
            // trick driver retries from the next frame; the build itself arms the
            // driver either way. The geyser tag is re-resolved LIVE from the
            // snapshot (run 23:09: the persisted OrderedMainVespene.UnitTag came
            // from the map-data recording game, so the engine silently dropped the
            // build order); the row's persisted tag is only a fallback.
            if (instruction.Command == WorkerInstructionCommand.BuildExtractor)
            {
                var geyserTag = ResolveLiveVaGeyserTag();
                if (geyserTag == 0)
                {
                    geyserTag = instruction.TargetId;
                }
                var extractorBuildAction = CreateInstructionBuildExtractorAction(
                    runtimeWorker.UnitTag,
                    geyserTag,
                    instruction.Queue);
                if (extractorBuildAction != null)
                {
                    followUpAction = TryCreateB3LarvaMorphAction();
                    _b3TrickWorkerTag = runtimeWorker.UnitTag;
                    _b3TrickBuildFrame = _currentFrame;
                    if (_b3TrickPhase == 0)
                    {
                        _b3TrickPhase = 1;
                    }
                    Console.WriteLine($"[EXTRACTOR TRICK] build issued worker={runtimeWorker.UnitTag} geyser={geyserTag} (rowTag={instruction.TargetId}) minerals={_currentMinerals} morphSameFrame={followUpAction != null}");
                }
                return extractorBuildAction;
            }

            // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): the terminal
            // morph. The site re-resolves from map data every frame (never the
            // row's persisted anything); a null site leaves the row
            // unexecuted so it retries next frame. Arming the milestone watcher
            // here (not in the driver) because only this row knows the build
            // actually ISSUED — a null action means it did not.
            if (instruction.Command == WorkerInstructionCommand.BuildHatchery)
            {
                var hatcherySite = ResolveMacroHatcherySite();
                if (hatcherySite == null)
                {
                    return null;
                }

                var hatcheryBuildAction = CreateInstructionBuildHatcheryAction(
                    runtimeWorker.UnitTag,
                    hatcherySite,
                    instruction.Queue);
                if (hatcheryBuildAction != null)
                {
                    _b3MhBuildIssuedFrame = _currentFrame;
                    Console.WriteLine($"[MACRO HATCHERY TEST] BUILD issued worker={runtimeWorker.UnitTag} site=({hatcherySite.X:F2},{hatcherySite.Y:F2}) minerals={_currentMinerals} frame={_currentFrame}");
                }

                return hatcheryBuildAction;
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
                WorkerInstructionCommand.BuildSpawningPool => CreateInstructionBuildSpawningPoolAction(
                    runtimeWorker.UnitTag,
                    instruction.Queue),
                WorkerInstructionCommand.Return => CreateInstructionReturnAction(
                    runtimeWorker.UnitTag,
                    assignedWorker?.MiningTargets?.FirstOrDefault(candidate => candidate != null && candidate.ResourceUnitId == instruction.TargetId)?.TownHallUnitId ?? 0,
                    instruction.Queue),
                WorkerInstructionCommand.Stop => CreateInstructionStopAction(runtimeWorker.UnitTag),
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

            // The alignment gate resolves from live observation (self, partner role-1,
            // hatchery, and the A mineral), not from the worker's stored mining target,
            // so it must run before the MiningTargets lookup below.
            if (instruction.Point == WorkerInstructionPoint.BumpAlignedGate)
            {
                return ResolveBumpAlignedGatePoint(assignedWorker, instruction);
            }

            // Same for the 5x5 wait-role clearance gate: it compares live positions of
            // the walker, its partner miners, the hatchery, and the walk mineral — none
            // of which is the row TargetId's stored mining target point.
            if (instruction.Point == WorkerInstructionPoint.WaitWalkClearGate)
            {
                return ResolveWaitWalkClearGatePoint(assignedWorker, instruction);
            }

            // 5x5 T1 first set: the SM-line midpoint and the T4 bumper gate are computed
            // from the pair table and live observation, not from the row TargetId's
            // stored mining target, so they run before the MiningTargets lookup below.
            if (instruction.Point == WorkerInstructionPoint.SmHarvestReturnMidpoint)
            {
                return ResolveSmHarvestReturnMidpoint(assignedWorker, instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.BumpSmBumper)
            {
                return ResolveBumpSmBumperPoint(runtimeWorker, assignedWorker, instruction);
            }

            // 5x5 A-miner first trip (owner spec 2026-10-09): the optimal first-harvest
            // approach point — computed from the worker's LIVE position (frame 0 = the
            // start position), never a pair-table read. T1/Y1 no longer come through
            // here — they store the direct (speed-mining) Harvest point instead (owner
            // request 2026-10-13); this resolver serves the remaining A-miners.
            if (instruction.Point == WorkerInstructionPoint.OptimalFirstHarvest)
            {
                return ResolveOptimalFirstHarvestPoint(runtimeWorker, assignedWorker, instruction);
            }

            // 5x5 B2 pool build (owner spec 2026-10-13): the pool placement is map
            // data, not a mining target, so it resolves before the MiningTargets
            // lookup below (same pattern as the gates above).
            if (instruction.Point == WorkerInstructionPoint.PoolPlacement)
            {
                return ResolveSpawningPoolPlacement();
            }

            // 13-B3 extractor trick (owner spec 2026-10-15): VA geometry resolves from
            // the persisted vespene/mineral orderings, never from the row TargetId's
            // mining target — B3's target list holds only BB.
            if (instruction.Point == WorkerInstructionPoint.VaGeyser)
            {
                var vaGeyser = ResolveVaGeyserEntry();
                return vaGeyser?.Position != null && HasNonZeroPoint(vaGeyser.Position)
                    ? vaGeyser.Position
                    : null;
            }

            if (instruction.Point == WorkerInstructionPoint.VaWalkRedirect)
            {
                return ResolveVaWalkRedirectPoint(runtimeWorker, instruction);
            }

            // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): the site is
            // map data, not a mining target — resolve before the MiningTargets
            // lookup below (same pattern as PoolPlacement above).
            if (instruction.Point == WorkerInstructionPoint.MacroHatcherySite)
            {
                return ResolveMacroHatcherySite();
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
                var referencedPoint = ResolvePairTablePoint(ResolveB2PoolReturnReference(assignedWorker, instruction.TargetPointReference));
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
                WorkerInstructionPoint.CcaWait
                    => ResolveMineralCcaWaitPoint(currentAssignments, target.ResourceUnitId),
                WorkerInstructionPoint.BumpPartner => ResolveObservedBumpPartner(assignedWorker, assignedWorker.Role),
                WorkerInstructionPoint.BumpMidpoint => ResolveObservedBumpMidpoint(assignedWorker, assignedWorker.Role, target),
                WorkerInstructionPoint.BumpHarvestCircle => ResolveBumpHarvestCirclePoint(assignedWorker, target),
                WorkerInstructionPoint.BumpCcaWaitCircle when teamAssignment != null
                    => ResolveCcaWaitPoint(teamAssignment, assignedWorker.Role),
                _ => null
            };
        }

        // Owner spec 2026-10-13 (retargeted from S2/SB to B2/BB): B2 mines BB twice,
        // then pool — "BB, BB, BB-pool". Once the mineral bank reaches
        // SpawningPoolReturnMineralsThreshold, B2's {BB,BB} self-pair return point
        // redirects to the {BB,Pool} pair row — the return point calculated on the
        // shortest BB -> spawning-pool path — so B2 deposits its cargo at the hatchery
        // already prepositioned on the way to build the pool (the orange line in the
        // owner screenshot). Both of B2's loop returns (and the startup return) carry
        // the {BB,BB}.ReturnPoint reference, so every return after the threshold
        // prepositions. Falls back to the original reference when the gate is not met
        // or the {BB,Pool} row has no geometry.
        private string ResolveB2PoolReturnReference(AssignedWorkerDto assignedWorker, string reference)
        {
            const string selfSuffix = "-BB-BB.ReturnPoint";
            if (!Settings.IsMagannatha5x5Rotation
                || assignedWorker == null
                || !string.Equals(assignedWorker.Role, "B2", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(reference)
                || !reference.EndsWith(selfSuffix, StringComparison.OrdinalIgnoreCase)
                || _currentMinerals < SpawningPoolReturnMineralsThreshold)
            {
                return reference;
            }

            var poolReference = string.Concat(
                reference.Substring(0, reference.Length - selfSuffix.Length),
                "-BB-Pool.ReturnPoint");
            return ResolvePairTablePoint(poolReference) != null ? poolReference : reference;
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

            var property = reference.Substring(dotIndex + 1);
            var pair = ResolvePairTableRow(reference);
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

        // The pair-row lookup behind ResolvePairTablePoint, exposed for resolvers that
        // combine several of the row's points (the T1 SM midpoint, the T4 bumper gate).
        private static JitPairReturnCalculationDto? ResolvePairTableRow(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            var dotIndex = reference.LastIndexOf('.');
            if (dotIndex <= 0 || dotIndex == reference.Length - 1)
            {
                return null;
            }

            var pairKey = reference.Substring(0, dotIndex);
            var startIndex = Globals.CurrentStartIndex >= 0
                ? Globals.CurrentStartIndex
                : Settings.CurrentSpawnIndex;
            var pairs = Globals.CurrentMapData?.MainJitPairReturnCalculations?.ElementAtOrDefault(startIndex);
            return pairs?.FirstOrDefault(candidate => candidate != null
                && string.Equals(candidate.PairKey, pairKey, StringComparison.OrdinalIgnoreCase));
        }

        // 5x5 T1 first set ("T1 - T4 Bump Set"): the initial walk target is the half-way
        // point between the TA speed-mining harvest point and the TA speed-mining return
        // point — the midpoint of the {TA,TA} self-pair row's HarvestA and ReturnPoint,
        // the two ends of the SM line segment. T1 parks here, in front of the T4 bumper,
        // before its +8 frame move to the SM harvest point.
        private static Vector2Dto? ResolveSmHarvestReturnMidpoint(AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if (!Settings.IsMagannatha5x5Rotation || assignedWorker == null || instruction == null)
            {
                return null;
            }

            var pair = ResolvePairTableRow(instruction.TargetPointReference);
            if (pair == null || !HasNonZeroPoint(pair.HarvestA) || !HasNonZeroPoint(pair.ReturnPoint))
            {
                return null;
            }

            return new Vector2Dto(
                (pair.HarvestA.X + pair.ReturnPoint.X) / 2f,
                (pair.HarvestA.Y + pair.ReturnPoint.Y) / 2f,
                pair.HarvestA.Z);
        }

        // 5x5 T4/Y4 bumper gate ("T1 - T4 Bump Set"). The bumper mineral-walks its walk
        // mineral to get behind its partner (T4: ordered [3] toward T1, Y4: ordered [5]
        // toward Y1); the bumper point is where that mineral-walk line (hatchery -> walk
        // mineral) passes the partner's A-mineral speed-mining line, computed as the
        // closest point on the hatchery->walk-mineral segment to the {TA,TA}/{YA,YA} SM
        // harvest point carried by the row's pair reference. Resolves to the partner's
        // live position once the bumper is within BumpBumperGateTolerance of the bumper
        // point — the Move row then switches the bumper off the mineral walk and onto a
        // bump move behind its partner. Null while the bumper is still short of the
        // bumper point; fails open to the partner's live position at relative frame
        // BumpBumperFailOpenRelativeFrame so a blocked or misaligned walk can never park
        // the bumper on the walk mineral forever.
        private Vector2Dto? ResolveBumpSmBumperPoint(
            RuntimeWorkerState runtimeWorker,
            AssignedWorkerDto assignedWorker,
            WorkerInstruction instruction)
        {
            if (!Settings.IsMagannatha5x5Rotation || assignedWorker == null || instruction == null)
            {
                return null;
            }

            // 5x5 Version 2 bump roles: T4 (bumps T1, walk mineral ordered [3]) and
            // Y4 (bumps Y1, walk mineral ordered [5]).
            string partnerRole;
            int walkMineralIndex;
            if (string.Equals(assignedWorker.Role, "T4", StringComparison.OrdinalIgnoreCase))
            {
                partnerRole = "T1";
                walkMineralIndex = 3;
            }
            else if (string.Equals(assignedWorker.Role, "Y4", StringComparison.OrdinalIgnoreCase))
            {
                partnerRole = "Y1";
                walkMineralIndex = 5;
            }
            else
            {
                return null;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();
            var partner = assignedWorkers.FirstOrDefault(worker =>
                string.Equals(worker?.Role, partnerRole, StringComparison.OrdinalIgnoreCase));
            var partnerPosition = partner?.CurrentXY != null && HasNonZeroPoint(partner.CurrentXY) ? partner.CurrentXY : null;

            var relativeFrame = runtimeWorker != null
                ? Settings.GetRelativeFrame(_currentFrame) - runtimeWorker.InstructionStartFrame
                : 0;
            if (relativeFrame >= BumpBumperFailOpenRelativeFrame)
            {
                return partnerPosition;
            }

            if (partnerPosition == null || !HasNonZeroPoint(assignedWorker.CurrentXY))
            {
                return null;
            }

            // Walk mineral: same role-keyed lookup the build manager uses when it
            // assembles the bump set (T4: ordered [3], Y4: ordered [5]).
            var walkMineral = OngoingMapData.ResolveTeamAssignments(_mapData, startIndex)
                .SelectMany(assignment => assignment?.Minerals ?? new List<OrderedMineral>())
                .FirstOrDefault(mineral => mineral != null && mineral.Index == walkMineralIndex);
            Vector2Dto? walkMineralPosition = null;
            if (walkMineral?.UnitTag != 0
                && Globals.CurrentObservation?.Minerals != null
                && Globals.CurrentObservation.Minerals.TryGetValue(walkMineral.UnitTag, out var observedWalkMineral)
                && observedWalkMineral?.Position != null)
            {
                walkMineralPosition = observedWalkMineral.Position;
            }

            // Hatchery position, same fallback chain as the alignment gate.
            var townHallTag = assignedWorker.TownHallUnitID;
            Vector2Dto hatcheryPosition = null;
            if (townHallTag != 0 && Globals.CurrentObservation?.CurrentTownHalls != null
                && Globals.CurrentObservation.CurrentTownHalls.TryGetValue(townHallTag, out var townHall)
                && townHall?.Position != null)
            {
                hatcheryPosition = townHall.Position;
            }

            if (hatcheryPosition == null && _mapData?.StartingTownHall != null
                && startIndex >= 0 && startIndex < _mapData.StartingTownHall.Length)
            {
                hatcheryPosition = _mapData.StartingTownHall[startIndex];
            }

            // A-mineral speed-mining harvest point from the row's {TA,TA}/{YA,YA} pair
            // reference.
            var pair = ResolvePairTableRow(instruction.TargetPointReference);
            var smHarvest = pair != null && HasNonZeroPoint(pair.HarvestA) ? pair.HarvestA : null;
            if (walkMineralPosition == null || hatcheryPosition == null || smHarvest == null)
            {
                return null;
            }

            // Bumper point: closest point on the hatchery->walk-mineral segment to the
            // SM harvest point (projection clamped to the segment).
            var directionX = walkMineralPosition.X - hatcheryPosition.X;
            var directionY = walkMineralPosition.Y - hatcheryPosition.Y;
            var lengthSquared = directionX * directionX + directionY * directionY;
            if (lengthSquared <= 0.0001f)
            {
                return null;
            }

            var projection = ((smHarvest.X - hatcheryPosition.X) * directionX
                + (smHarvest.Y - hatcheryPosition.Y) * directionY) / lengthSquared;
            projection = MathF.Max(0f, MathF.Min(1f, projection));
            var bumperX = hatcheryPosition.X + projection * directionX;
            var bumperY = hatcheryPosition.Y + projection * directionY;

            var offsetX = assignedWorker.CurrentXY.X - bumperX;
            var offsetY = assignedWorker.CurrentXY.Y - bumperY;
            var distanceSquared = offsetX * offsetX + offsetY * offsetY;
            if (distanceSquared > BumpBumperGateTolerance * BumpBumperGateTolerance)
            {
                return null;
            }

            return partnerPosition;
        }

        // 5x5 A-miner first trip (owner spec 2026-10-09): the optimal first-harvest
        // approach point, used by every A-miner except T1/Y1 (those store the direct
        // Harvest point per owner request 2026-10-13 — see
        // BabySharkBuildManager.Build5x5AMinerStartupInstructions). The SM harvest
        // point is NOT forced on the first trip — the objective is to be harvesting
        // (and then returning at the SM return point) as soon as possible, so the
        // point minimizes TOTAL travel
        //   |worker position -> P| + |P -> SM return point|
        // over the candidate standoff points around the mineral: the stored SM harvest
        // point, the stored normal harvest point, and samples on the harvest circle
        // (mineral center, radius = the stored harvest point's standoff distance).
        // The worker's position at resolution time is the origin — the store row is
        // NoCondition and resolves on frame 0, i.e. at the worker's start position.
        // Null only when the target geometry is missing, which holds the store row
        // (and with it the walk) until the geometry exists.
        private Vector2Dto? ResolveOptimalFirstHarvestPoint(
            RuntimeWorkerState runtimeWorker,
            AssignedWorkerDto assignedWorker,
            WorkerInstruction instruction)
        {
            if (assignedWorker == null || instruction == null)
            {
                return null;
            }

            var target = assignedWorker.MiningTargets?.FirstOrDefault(candidate =>
                candidate != null && candidate.ResourceUnitId == instruction.TargetId);
            if (target == null || target.ResourcePosition == null || !HasNonZeroPoint(target.ResourcePosition))
            {
                return null;
            }

            var start = runtimeWorker?.Position;
            var smReturn = HasNonZeroPoint(target.SmReturnPoint) ? target.SmReturnPoint : target.ReturnPoint;
            if (start == null || !HasNonZeroPoint(smReturn))
            {
                return null;
            }

            var center = target.ResourcePosition;
            var radiusSquared = HasNonZeroPoint(target.HarvestPoint)
                ? DistanceSquared(center, target.HarvestPoint)
                : HasNonZeroPoint(target.SmHarvestPoint) ? DistanceSquared(center, target.SmHarvestPoint) : 0f;
            if (radiusSquared <= 0.0001f)
            {
                return null;
            }
            var radius = Math.Sqrt(radiusSquared);

            var best = new Vector2Dto(
                HasNonZeroPoint(target.SmHarvestPoint) ? target.SmHarvestPoint.X : target.HarvestPoint.X,
                HasNonZeroPoint(target.SmHarvestPoint) ? target.SmHarvestPoint.Y : target.HarvestPoint.Y,
                0);
            var bestSource = HasNonZeroPoint(target.SmHarvestPoint) ? "sm-harvest" : "harvest";
            var bestCost = double.MaxValue;
            ConsiderCandidate(target.SmHarvestPoint, "sm-harvest");
            ConsiderCandidate(target.HarvestPoint, "harvest");

            const int sampleCount = 16;
            for (var i = 0; i < sampleCount; i++)
            {
                var angle = 2.0 * Math.PI * i / sampleCount;
                ConsiderCandidate(new Vector2Dto(
                    (float)(center.X + radius * Math.Cos(angle)),
                    (float)(center.Y + radius * Math.Sin(angle)),
                    0), $"circle-{i}");
            }

            Console.WriteLine($"[OPTIMAL HARVEST] frame={_currentFrame} worker={assignedWorker.Role} mineral={target.ResourceLabel} start=({start.X:F2},{start.Y:F2}) smReturn=({smReturn.X:F2},{smReturn.Y:F2}) chosen={bestSource} point=({best.X:F3},{best.Y:F3}) total={bestCost:F3}");

            return best;

            void ConsiderCandidate(Vector2Dto candidate, string source)
            {
                if (candidate == null || !HasNonZeroPoint(candidate))
                {
                    return;
                }
                var cost = Math.Sqrt(DistanceSquared(start, candidate)) + Math.Sqrt(DistanceSquared(candidate, smReturn));
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = candidate;
                    bestSource = source;
                }
            }
        }

        // Experiment-1 bump alignment gate. Resolves to the worker's own position when
        // the bump walk is done (role 3 and its role-1 partner both on the
        // hatchery-to-A-mineral line, role 3 closer to the hatchery); null while the
        // pair is still unaligned, which holds the gate row — and the inverted-gate
        // jump behind it — in the walk loop.
        private Vector2Dto? ResolveBumpAlignedGatePoint(AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if ((!Settings.IsMagannatha12WorkerOverride && !Settings.IsMagannatha5x5Rotation) || assignedWorker == null)
            {
                return null;
            }

            var workerRole = assignedWorker.Role ?? string.Empty;
            // 5x5 Version 2 bump partners: T4 (W2) bumps T1 toward TA, Y4 (W11) bumps
            // Y1 toward YA. T3 no longer bumps — it is a first miner on SA. The gate
            // geometry is unchanged: both workers on the hatchery-to-A-mineral line
            // with the bumper closer to the hatchery (i.e. behind the primary it
            // accelerates).
            string partnerRole;
            if (Settings.IsMagannatha5x5Rotation)
            {
                partnerRole = workerRole.Equals("T4", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : workerRole.Equals("Y4", StringComparison.OrdinalIgnoreCase) ? "Y1"
                    : string.Empty;
            }
            else
            {
                partnerRole = workerRole.Equals("T3", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : workerRole.Equals("Y3", StringComparison.OrdinalIgnoreCase) ? "Y1"
                    : string.Empty;
            }
            if (string.IsNullOrWhiteSpace(partnerRole))
            {
                return null;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();

            var partner = assignedWorkers.FirstOrDefault(worker =>
                string.Equals(worker?.Role, partnerRole, StringComparison.OrdinalIgnoreCase));
            var selfPosition = HasNonZeroPoint(assignedWorker.CurrentXY) ? assignedWorker.CurrentXY : null;
            var partnerPosition = partner?.CurrentXY != null && HasNonZeroPoint(partner.CurrentXY) ? partner.CurrentXY : null;

            var townHallTag = assignedWorker.TownHallUnitID != 0 ? assignedWorker.TownHallUnitID : partner?.TownHallUnitID ?? 0;
            Vector2Dto hatcheryPosition = null;
            if (townHallTag != 0 && Globals.CurrentObservation?.CurrentTownHalls != null
                && Globals.CurrentObservation.CurrentTownHalls.TryGetValue(townHallTag, out var townHall)
                && townHall?.Position != null)
            {
                hatcheryPosition = townHall.Position;
            }

            if (hatcheryPosition == null && startIndex >= 0 && _mapData?.StartingTownHall != null
                && startIndex < _mapData.StartingTownHall.Length)
            {
                hatcheryPosition = _mapData.StartingTownHall[startIndex];
            }

            Vector2Dto mineralPosition = null;
            if (instruction.TargetId != 0 && Globals.CurrentObservation?.Minerals != null
                && Globals.CurrentObservation.Minerals.TryGetValue(instruction.TargetId, out var mineral)
                && mineral?.Position != null)
            {
                mineralPosition = mineral.Position;
            }

            if (selfPosition == null || partnerPosition == null || hatcheryPosition == null || mineralPosition == null)
            {
                return null;
            }

            if (DistanceToLine(selfPosition, hatcheryPosition, mineralPosition) > BumpLineGateTolerance
                || DistanceToLine(partnerPosition, hatcheryPosition, mineralPosition) > BumpLineGateTolerance)
            {
                return null;
            }

            // Role 3 stands between the hatchery and role 1; role 1 stays closer to the mineral.
            if (Distance(hatcheryPosition, selfPosition) >= Distance(hatcheryPosition, partnerPosition))
            {
                return null;
            }

            return selfPosition;
        }

        // Clearance gate for the 5x5 wait-role mineral walk. RETIRED in Version 2:
        // the wait roles (T5/Y5/Y3 gated walks) no longer exist and no instruction
        // references this point, but the resolver stays as a no-op safeguard.
        // Resolves to the worker's own position — meaning "leave the walk, go to the wait
        // point" — when ANY of these holds:
        //   (a) Arrival: the walker reached the walk mineral (clear by definition).
        //   (b) Partners clear: for every partner that must not be blocked, EITHER the
        //       partner is nearly at its own mineral (>= WaitWalkPartnerClearProgress, so
        //       the shared corridor behind it is empty) OR the walker has gone past the
        //       partner on the corridor (self progress >= partner progress + margin).
        //       The walk continues until then — releasing while Y1/Y2 were still
        //       mid-corridor bounced Y1 off its acceleration line instead of letting the
        //       bumper push it through.
        //   (c) Timeout: the gate held for WaitWalkGateTimeoutFrames absolute frames — the
        //       inverted-gate jump resets InstructionStartFrame every loop pass, so the
        //       hold age is tracked in _waitWalkGateHoldStartFrames; the escape keeps a
        //       stalled partner from parking the walker on the walk mineral forever.
        // Unresolvable geometry (missing self position, unknown role) fails OPEN so the
        // wait set can never deadlock a worker it was never meant to gate.
        private Vector2Dto? ResolveWaitWalkClearGatePoint(AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if (!Settings.IsMagannatha5x5Rotation || assignedWorker == null)
            {
                return null;
            }

            var workerRole = assignedWorker.Role ?? string.Empty;
            string[] partnerRoles = workerRole.Equals("T5", StringComparison.OrdinalIgnoreCase) ? new[] { "T1" }
                : workerRole.Equals("Y5", StringComparison.OrdinalIgnoreCase) ? new[] { "Y1", "Y2" }
                : workerRole.Equals("Y3", StringComparison.OrdinalIgnoreCase) ? new[] { "Y1", "Y2" }
                : Array.Empty<string>();

            var selfPosition = HasNonZeroPoint(assignedWorker.CurrentXY) ? assignedWorker.CurrentXY : null;
            if (partnerRoles.Length == 0 || selfPosition == null)
            {
                _waitWalkGateHoldStartFrames.Remove(assignedWorker.UnitID);
                return selfPosition;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();

            Vector2Dto walkMineralPosition = null;
            if (instruction.TargetId != 0 && Globals.CurrentObservation?.Minerals != null
                && Globals.CurrentObservation.Minerals.TryGetValue(instruction.TargetId, out var walkMineral)
                && walkMineral?.Position != null)
            {
                walkMineralPosition = walkMineral.Position;
            }

            var townHallTag = assignedWorker.TownHallUnitID;
            Vector2Dto hatcheryPosition = null;
            if (townHallTag != 0 && Globals.CurrentObservation?.CurrentTownHalls != null
                && Globals.CurrentObservation.CurrentTownHalls.TryGetValue(townHallTag, out var townHall)
                && townHall?.Position != null)
            {
                hatcheryPosition = townHall.Position;
            }

            if (hatcheryPosition == null && startIndex >= 0 && _mapData?.StartingTownHall != null
                && startIndex < _mapData.StartingTownHall.Length)
            {
                hatcheryPosition = _mapData.StartingTownHall[startIndex];
            }

            // (a) Arrival at the walk mineral ends the walk regardless of partner progress.
            if (walkMineralPosition != null && Distance(selfPosition, walkMineralPosition) <= WaitWalkArrivalRadius)
            {
                _waitWalkGateHoldStartFrames.Remove(assignedWorker.UnitID);
                return selfPosition;
            }

            // (b) Every configured partner ahead on its own line.
            var selfProgress = hatcheryPosition != null && walkMineralPosition != null
                ? LineProgress(selfPosition, hatcheryPosition, walkMineralPosition)
                : 0f;
            var allPartnersClear = true;
            foreach (var partnerRole in partnerRoles)
            {
                var partner = assignedWorkers.FirstOrDefault(worker =>
                    string.Equals(worker?.Role, partnerRole, StringComparison.OrdinalIgnoreCase));
                var partnerPosition = partner?.CurrentXY != null && HasNonZeroPoint(partner.CurrentXY) ? partner.CurrentXY : null;
                var partnerMineralTag = partner?.MiningTargets?.FirstOrDefault()?.ResourceUnitId ?? 0;
                Vector2Dto partnerMineralPosition = null;
                if (partnerMineralTag != 0 && Globals.CurrentObservation?.Minerals != null
                    && Globals.CurrentObservation.Minerals.TryGetValue(partnerMineralTag, out var partnerMineral)
                    && partnerMineral?.Position != null)
                {
                    partnerMineralPosition = partnerMineral.Position;
                }

                if (partnerPosition == null || partnerMineralPosition == null || hatcheryPosition == null)
                {
                    // Partner not observable right now — never block on a missing partner.
                    continue;
                }

                var partnerProgress = LineProgress(partnerPosition, hatcheryPosition, partnerMineralPosition);
                // Continue the mineral walk until the walker is CLEAR of the partner's
                // path: the partner is nearly at its mineral (so the corridor behind it
                // is empty and the bumper's push can carry it through), or the walker has
                // gone past the partner on the shared corridor. Releasing while the
                // partner is still mid-corridor bounced Y1 off its acceleration line.
                var partnerClear = partnerProgress >= WaitWalkPartnerClearProgress
                    || selfProgress >= partnerProgress + WaitWalkPastPartnerMargin;
                if (!partnerClear)
                {
                    allPartnersClear = false;
                    break;
                }
            }

            if (allPartnersClear)
            {
                _waitWalkGateHoldStartFrames.Remove(assignedWorker.UnitID);
                return selfPosition;
            }

            // (c) Timeout escape so a stalled partner can never hold the walker forever.
            if (!_waitWalkGateHoldStartFrames.TryGetValue(assignedWorker.UnitID, out var holdStartFrame))
            {
                _waitWalkGateHoldStartFrames[assignedWorker.UnitID] = _currentFrame;
            }
            else if (_currentFrame - holdStartFrame > WaitWalkGateTimeoutFrames)
            {
                _waitWalkGateHoldStartFrames.Remove(assignedWorker.UnitID);
                Console.WriteLine($"[WAIT WALK GATE TIMEOUT] frame={_currentFrame} worker={assignedWorker.UnitID} role={workerRole} walkTag={instruction.TargetId}");
                return selfPosition;
            }

            return null;
        }

        // Normalized projection of a point onto the hatchery-to-mineral segment: 0 at the
        // hatchery end, 1 at the mineral end, clamped — a unit "ahead on the line" has a
        // higher progress than one still near the hatchery.
        private static float LineProgress(Vector2Dto point, Vector2Dto lineStart, Vector2Dto lineEnd)
        {
            var directionX = lineEnd.X - lineStart.X;
            var directionY = lineEnd.Y - lineStart.Y;
            var lengthSquared = directionX * directionX + directionY * directionY;
            if (lengthSquared <= 0.0001f)
            {
                return 0f;
            }

            var projection = ((point.X - lineStart.X) * directionX + (point.Y - lineStart.Y) * directionY) / lengthSquared;
            return MathF.Max(0f, MathF.Min(1f, projection));
        }

        private static float DistanceToLine(Vector2Dto point, Vector2Dto lineStart, Vector2Dto lineEnd)
        {
            var directionX = lineEnd.X - lineStart.X;
            var directionY = lineEnd.Y - lineStart.Y;
            var lengthSquared = directionX * directionX + directionY * directionY;
            if (lengthSquared <= 0.0001f)
            {
                return Distance(point, lineStart);
            }

            var projection = ((point.X - lineStart.X) * directionX + (point.Y - lineStart.Y) * directionY) / lengthSquared;
            var closestX = lineStart.X + projection * directionX;
            var closestY = lineStart.Y + projection * directionY;
            var dx = point.X - closestX;
            var dy = point.Y - closestY;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private const float BumpLineGateTolerance = 0.5f;

        // Bumper-gate geometry for the 5x5 T4/Y4 bump set ("T1 - T4 Bump Set"): how
        // close the bumper must be to the computed bumper point before the bump move
        // fires, and the relative frame at which the gate fails open — at the start of
        // the 8-25 partner chase window — so a blocked mineral walk can never park the
        // bumper on the walk mineral and the chase rows always get their frames.
        private const float BumpBumperGateTolerance = 1.0f;
        private const int BumpBumperFailOpenRelativeFrame = 8;

        // Owner spec 2026-10-13 (retargeted to B2): the mineral bank at which B2's BB
        // return redirects to the {BB,Pool} return point (the shortest BB -> spawning-
        // pool path) so the cargo return prepositions B2 to build the pool.
        // Owner spec 2026-10-13: at 165 minerals B2's {BB,BB} returns redirect to the
        // {BB,Pool} preposition row; at 200 (the pool's cost) B2's mineral-gated wait
        // releases and its book issues the BUILD_SPAWNINGPOOL command itself.
        private const int SpawningPoolReturnMineralsThreshold = 165;
        internal const int SpawningPoolBuildMinerals = 200;

        // 13-B3 extractor trick (owner spec 2026-10-15 22:20): the mineral bank the
        // VA wait row gates on — 75 for the extractor + 50 for the larva morph the
        // mining manager issues in the same frame as the build.
        internal const int ExtractorTrickBuildMinerals = 125;

        // BumpPartner resolves to the partner's observed X,Y. Experiment 1: T3->T1,
        // Y3->Y1. 5x5 "T1 - T4 Bump Set": T4->T1, Y4->Y1 — the frame 8-25 chase rows
        // re-issue a move to the partner's live position every frame, so the resolver
        // must track the partner wherever it currently is, not a stored point.
        private Vector2Dto? ResolveObservedBumpPartner(AssignedWorkerDto assignedWorker, string workerRole)
        {
            if ((!Settings.IsMagannatha12WorkerOverride && !Settings.IsMagannatha5x5Rotation) || assignedWorker == null)
            {
                return null;
            }

            string partnerRole;
            if (Settings.IsMagannatha5x5Rotation)
            {
                partnerRole = workerRole.Equals("T4", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : workerRole.Equals("Y4", StringComparison.OrdinalIgnoreCase) ? "Y1"
                    : string.Empty;
            }
            else
            {
                partnerRole = workerRole.Equals("T3", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : workerRole.Equals("Y3", StringComparison.OrdinalIgnoreCase) ? "Y1"
                    : string.Empty;
            }

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

        // New-logic staging point resolution (owner spec 2026-10-11): the CCA wait
        // point of the ROW's target mineral (looked up by UnitTag across all current
        // team assignments, so a swing worker stages near SA/TB/TA/SB regardless of
        // its own team), falling back to that team's shared JIT wait point. Returns
        // null only when neither is persisted — the staging Move row then emits no
        // command and the worker stands wherever it is, same as any unresolvable row.
        private static Vector2Dto? ResolveMineralCcaWaitPoint(
            List<TeamPatchAssignmentDto> currentAssignments,
            ulong mineralTag)
        {
            if (currentAssignments == null || mineralTag == 0)
            {
                return null;
            }

            foreach (var assignment in currentAssignments)
            {
                var mineral = assignment?.Minerals?.FirstOrDefault(candidate =>
                    candidate != null && candidate.UnitTag == mineralTag);
                if (mineral == null)
                {
                    continue;
                }

                if (HasNonZeroPoint(mineral.CcaWaitPoint))
                {
                    return mineral.CcaWaitPoint;
                }

                if (HasNonZeroPoint(assignment.JitWaitPoint))
                {
                    return assignment.JitWaitPoint;
                }

                return null;
            }

            return null;
        }

        private void ReportInstructionFailure(RuntimeWorkerState runtimeWorker, string reason, WorkerInstruction instruction)
        {
            Console.WriteLine($"[WORKER INSTRUCTION FAILURE] frame={_currentFrame} worker={runtimeWorker.UnitTag} index={runtimeWorker.CurInstrIdx} set={instruction?.InstructionSet ?? "<none>"} reason={reason} previousAbility={runtimeWorker.PreviousAbilityId} currentAbility={runtimeWorker.CurrentAbilityId} targetAbility={runtimeWorker.TargetAbilityId}");
            //Debugger.Break();
        }

        // An exhausted list is a terminal state, not a per-frame failure: B2's book
        // intentionally ends on its BUILD_SPAWNINGPOOL row because the drone morphs
        // into the pool. Log the exhaustion once per worker, then stay quiet.
        private void ReportBookExhaustedOnce(RuntimeWorkerState runtimeWorker, string reason)
        {
            if (runtimeWorker != null && _exhaustedBookReported.Add(runtimeWorker.UnitTag))
            {
                ReportInstructionFailure(runtimeWorker, reason, null);
            }
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
            // Row's pair-table reference ("{base}-{labelA}-{labelB}.{property}"), so the
            // console trace can print the real pair+phase (e.g. TA-TB.ReturnCargo)
            // instead of a bare sequence number.
            public string TargetPointReference { get; init; } = string.Empty;
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

        private static float Distance(Vector2Dto first, Vector2Dto second)
        {
            if (first == null || second == null)
            {
                return float.MaxValue;
            }

            return MathF.Sqrt(DistanceSquared(first, second));
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

        private bool IsHarvestReleaseReady(ulong mineralTag)
        {
            if (mineralTag == 0)
            {
                return true;
            }

            // Free right now -> send immediately; otherwise wait until the active
            // harvester's remaining harvest frames fit the staged worker's walk-in
            // time (ReleaseLeadFrames).
            return !_harvestFrameTracker.HasActiveHarvester(mineralTag)
                || _harvestFrameTracker.ShouldReleaseStagedWorker(mineralTag);
        }

        // Gather-send gate (owner spec 2026-10-16, worker collision pass): a
        // condition-gated Gather row may only be SENT when its target mineral is
        // free or will be free within the release lead — the same readiness the
        // staging Wait rows (HarvestReleaseGate) already enforce before a staged
        // worker leaves the wait point. Rows sent earlier strand the worker at the
        // busy mineral's footprint as an 1183 waiter, and once it stands there
        // past ~15 frames the game re-paths it to an UNRELATED mineral — the
        // "random mineral" leak. NoCondition gathers (extractor-trick mineral
        // walk, macro-hatchery walk, collision detours, the frame-0 Smart opens)
        // are choreography rather than mining and always pass; Return rows never
        // pass through here.
        private bool IsGatherRowSendReady(WorkerInstruction instruction)
        {
            if (instruction == null
                || instruction.Command != WorkerInstructionCommand.Gather
                || instruction.NoCondition
                || instruction.TargetId == 0)
            {
                return true;
            }

            return IsHarvestReleaseReady(instruction.TargetId);
        }

        private void UpdateHarvestFrameTracking(List<SnapshotUnit> liveWorkers)
        {
            var minerals = Globals.CurrentObservation?.Minerals;
            _harvestFrameTracker.Update(
                _currentFrame,
                (liveWorkers ?? new List<SnapshotUnit>()).Select(worker => (
                    worker.Tag,
                    new Vector2Dto(worker.Pos.X, worker.Pos.Y),
                    (IReadOnlyList<int>)worker.OrderAbilityIds,
                    worker.TargetUnitTag)),
                IsGatherOrder,
                mineralTag => minerals != null && minerals.TryGetValue(mineralTag, out var mineral)
                    ? mineral.Position
                    : null);
        }

        private bool IsGatheringNearCompletion(ulong workerTag, ulong resourceTag)
        {
            // Harvest-frame based: true when the active harvester will flip to 1184
            // within HarvestFrameTracker.ReleaseLeadFrames frames (32, owner spec
            // 2026-10-16), i.e. a worker dispatched from a staging point now takes
            // over while the previous harvester still has up to 32 harvest frames
            // left — early enough that the game never re-paths it away for standing
            // at a busy mineral.
            return _harvestFrameTracker.IsActiveHarvester(workerTag)
                && _harvestFrameTracker.ShouldReleaseStagedWorker(resourceTag);
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

        // Experiment-1 bump stop: cancels the mineral-walk harvest the frame the
        // alignment gate resolves. Single frame, not queued — the wait-point moves
        // behind it must override the walk, not line up behind it.
        private static SC2Action? CreateInstructionStopAction(ulong workerTag)
        {
            if (workerTag == 0)
            {
                return null;
            }

            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.STOP
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

        private static SC2Action? CreateInstructionReturnAction(ulong workerTag, ulong townHallTag, bool queued)
        {
            if (workerTag == 0 || townHallTag == 0)
            {
                return null;
            }

            // Smart on the town hall is the return-cargo command (canon: workers receive a
            // Smart command). HARVEST_RETURN (3667) declares Target: None -- the game drops
            // it when a unit target is attached and does nothing reliable without one, which
            // left the worker standing at the hatchery still carrying.
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.SMART,
                TargetUnitTag = townHallTag,
                QueueCommand = queued
            };
            command.UnitTags.Add(workerTag);
            return new SC2Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        /// <summary>
        /// Two-phase return interrupt. T1 ONLY (owner request): no other worker is
        /// interrupted — everyone else keeps the normal gather->return cycle.
        ///
        /// Evidence (worker_commands_BabyShark_20260927173714 and _20260927194345):
        /// the engine coalesces multiple commands for ONE unit in a single action
        /// batch. Frame 75 of the 19:43 run sent MOVE,STOP,MOVE for tag 4352376833
        /// and frame 76 still showed [1184,16] — byte-identical to the earlier run
        /// that sent MOVE alone. STOP by itself does work (frames 8/11 of the old
        /// trace cleared 1183). An unqueued MOVE also lands QUEUED behind an active
        /// 1184 instead of replacing it, leaving the worker frozen for ~8 frames.
        ///
        /// Therefore:
        ///   Phase 1 (pickup frame — the 1183->1184 order flip, detected via the
        ///     cargo-pickup edge): STOP is the ONLY command for the worker in
        ///     the batch — every other command for its tag is stripped first.
        ///   Phase 2 (next frame): the MOVE to the return point goes out alone,
        ///     queue YES per owner request (it is the only command for the worker
        ///     that frame, so the flag is belt-and-braces behind the phase-1 STOP).
        /// </summary>
        private List<SC2Action> IssueReturnInterrupts(
            ResponseObservation observation,
            List<SC2Action> alreadyIssued,
            List<InstructionCommandContext> contexts)
        {
            var interrupts = new List<SC2Action>();
            if (observation == null)
            {
                return interrupts;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();

            // Drop stale pending moves (worker left the list before phase 2 could run).
            if (_returnInterruptPendingMove.Count > 0)
            {
                var stale = _returnInterruptPendingMove
                    .Where(pair => _currentFrame - pair.Value.Frame > 5)
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (var tag in stale)
                {
                    _returnInterruptPendingMove.Remove(tag);
                }
            }

            // Phase 2: deliver the MOVE staged by last frame's STOP.
            foreach (var assignedWorker in assignedWorkers)
            {
                if (assignedWorker == null || assignedWorker.UnitID == 0)
                {
                    continue;
                }

                if (_returnInterruptPendingMove.TryGetValue(assignedWorker.UnitID, out var pending))
                {
                    _returnInterruptPendingMove.Remove(assignedWorker.UnitID);
                    var followUpMove = CreateInstructionMoveAction(
                        assignedWorker.UnitID,
                        new Point2D { X = pending.Target.X, Y = pending.Target.Y },
                        queued: true);
                    if (followUpMove != null)
                    {
                        interrupts.Add(followUpMove);
                        Console.WriteLine($"[RETURN INTERRUPT] frame={_currentFrame} worker={assignedWorker.UnitID} phase=MOVE target=({pending.Target.X:F4},{pending.Target.Y:F4})");
                    }
                }
            }

            foreach (var assignedWorker in assignedWorkers)
            {
                if (assignedWorker == null
                    || assignedWorker.UnitID == 0
                    || !Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitID, out var runtimeWorker))
                {
                    continue;
                }

                // Return-cargo interrupt is T1-only (owner request), and T1 is the one
                // A-miner whose pickup edges this interrupt drives — Y1/Y3 get their
                // Version 2 A-miner prologue (SM first return, second A harvest)
                // purely from their book rows. The pickup edge (cargo buff appears)
                // is the same moment the worker's order flips 1183 (HARVEST_GATHER_DRONE)
                // -> 1184 (HARVEST_RETURN_DRONE); every other worker keeps its normal
                // uninterrupted gather->return cycle. Version 2: the FIRST interrupt
                // targets the A mineral's speed-mining return point (see
                // ResolveInterruptReturnPoint); later pickups keep the normal point.
                var workerRole = ResolveWorkerRole(assignedWorker, assignedWorker.UnitID);
                if (!string.Equals(workerRole, "T1", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Re-arm after delivery so the next pickup interrupts again.
                if (!runtimeWorker.IsCarrying && runtimeWorker.WasCarrying)
                {
                    _returnInterruptIssued.Remove(assignedWorker.UnitID);
                    continue;
                }

                var pickupEdge = runtimeWorker.IsCarrying && !runtimeWorker.WasCarrying;
                if (!pickupEdge || !_returnInterruptIssued.Add(assignedWorker.UnitID))
                {
                    continue;
                }

                var returnPoint = ResolveInterruptReturnPoint(assignedWorker, startIndex);
                if (returnPoint == null)
                {
                    _returnInterruptIssued.Remove(assignedWorker.UnitID);
                    continue;
                }

                // Phase 1: make STOP the only command for this worker in the batch,
                // otherwise the engine coalesces and the STOP never executes.
                RemoveActionsForWorker(alreadyIssued, contexts, assignedWorker.UnitID);

                var stop = CreateInstructionStopAction(assignedWorker.UnitID);
                if (stop != null)
                {
                    interrupts.Add(stop);
                    _returnInterruptPendingMove[assignedWorker.UnitID] = (returnPoint, _currentFrame);
                    Console.WriteLine($"[RETURN INTERRUPT] frame={_currentFrame} worker={assignedWorker.UnitID} phase=STOP staged=({returnPoint.X:F4},{returnPoint.Y:F4})");
                }
            }

            return interrupts;
        }

        /// <summary>
        /// True when the candidate command is the exact same in-game command as one already
        /// staged for this worker this frame (same ability, same queue flag, same target unit,
        /// same world point). Used to keep back-to-back instruction rows that resolve to one
        /// command from emitting it twice — the engine would only execute the last emit, and
        /// the duplicate polluted the command trace as a bogus "conflicting command".
        /// </summary>
        private static bool IsDuplicateWorkerFrameAction(List<SC2Action> actions, SC2Action candidate, ulong workerTag)
        {
            var candidateCommand = candidate?.ActionRaw?.UnitCommand;
            if (actions == null || candidateCommand == null || workerTag == 0)
            {
                return false;
            }

            foreach (var existing in actions)
            {
                var existingCommand = existing?.ActionRaw?.UnitCommand;
                if (existingCommand == null
                    || existingCommand.AbilityId != candidateCommand.AbilityId
                    || existingCommand.QueueCommand != candidateCommand.QueueCommand
                    || existingCommand.TargetUnitTag != candidateCommand.TargetUnitTag
                    || existingCommand.UnitTags == null
                    || !existingCommand.UnitTags.Contains(workerTag))
                {
                    continue;
                }

                var existingPoint = existingCommand.TargetWorldSpacePos;
                var candidatePoint = candidateCommand.TargetWorldSpacePos;
                if (existingPoint == null && candidatePoint == null)
                {
                    return true;
                }
                if (existingPoint != null
                    && candidatePoint != null
                    && Math.Abs(existingPoint.X - candidatePoint.X) < 0.0001f
                    && Math.Abs(existingPoint.Y - candidatePoint.Y) < 0.0001f)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveActionsForWorker(List<SC2Action> actions, List<InstructionCommandContext> contexts, ulong workerTag)
        {
            if (actions == null || workerTag == 0)
            {
                return;
            }

            for (var i = actions.Count - 1; i >= 0; i--)
            {
                var command = actions[i]?.ActionRaw?.UnitCommand;
                if (command?.UnitTags == null || !command.UnitTags.Contains(workerTag))
                {
                    continue;
                }

                actions.RemoveAt(i);
                if (contexts != null && i < contexts.Count)
                {
                    contexts.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Canonical return point for the interrupt. Version 2 A-miner rule: the FIRST
        /// return after the first pickup goes to the mineral's speed-mining return point
        /// (SmReturnPoint — 1u off the hatchery toward the mineral, so the cargo hands
        /// off one step earlier); every later return uses the assigned mineral's normal
        /// ReturnPoint (same source the Return instruction uses). Falls back
        /// to the starting town hall position if the target has none.
        /// For the S1 trace this must resolve to X=128.5577, Y=21.961538 —
        /// the [RETURN INTERRUPT] console line prints what it resolved to.
        /// </summary>
        private Vector2Dto ResolveInterruptReturnPoint(AssignedWorkerDto assignedWorker, int startIndex)
        {
            var target = assignedWorker.MiningTargets?.ElementAtOrDefault(assignedWorker.Mti);
            if (target != null)
            {
                // 5x5 Version 2: first return on the speed-mining return point.
                if (!_returnInterruptSmFirstTripUsed.Contains(assignedWorker.UnitID)
                    && HasNonZeroPoint(target.SmReturnPoint))
                {
                    _returnInterruptSmFirstTripUsed.Add(assignedWorker.UnitID);
                    Console.WriteLine($"[RETURN INTERRUPT] frame={_currentFrame} worker={assignedWorker.UnitID} first-return=SM point=({target.SmReturnPoint.X:F4},{target.SmReturnPoint.Y:F4})");
                    return target.SmReturnPoint;
                }

                if (target.ReturnPoint != null)
                {
                    return target.ReturnPoint;
                }
            }

            var townHall = _mapData?.StartingTownHall != null && startIndex >= 0 && startIndex < _mapData.StartingTownHall.Length
                ? _mapData.StartingTownHall[startIndex]
                : null;
            return townHall != null ? new Vector2Dto(townHall.X, townHall.Y, 0) : null;
        }

        /// <summary>
        /// STOP must use this framework's ability id: (int)Abilities.STOP = 3665.
        /// Do NOT use 2054 — the engine silently drops it (unknown ability id),
        /// which is the exact "logged but never executed" failure.
        /// Uses the existing single-argument CreateInstructionStopAction above
        /// (QueueCommand defaults to false — exactly what the interrupt needs).
        /// </summary>
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

        // Owner spec 2026-10-13 (B2 pool): issues BUILD_SPAWNINGPOOL at the persisted
        // mini-wall placement for the current spawn — the same placement the {BB,Pool}
        // pair row was calculated from. B2's book only reaches this row after the
        // mineral-gated wait confirms the bank holds SpawningPoolBuildMinerals, and
        // B2 stands at the {BB,Pool} return preposition when it fires. A null
        // placement leaves the row unexecuted so the caller retries next frame.
        private static SC2Action? CreateInstructionBuildSpawningPoolAction(ulong workerTag, bool queued)
        {
            var placement = ResolveSpawningPoolPlacement();
            if (workerTag == 0 || placement == null)
            {
                return null;
            }

            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.BUILD_SPAWNINGPOOL,
                TargetWorldSpacePos = new Point2D { X = placement.X, Y = placement.Y },
                QueueCommand = queued
            };
            command.UnitTags.Add(workerTag);
            return new SC2Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        // The persisted mini-wall placement for the current spawn — the same
        // coordinate the {BB,Pool} pair row was calculated from. Shared by the
        // PoolPlacement point resolver (B2's Move-to-Pool rows) and the
        // BUILD_SPAWNINGPOOL action creator.
        private static Vector2Dto? ResolveSpawningPoolPlacement()
        {
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var placement = Globals.CurrentMapData?.SpawningPoolPlacements?.ElementAtOrDefault(startIndex);
            return placement != null && HasNonZeroPoint(placement) ? placement : null;
        }

        // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): the build/test
        // site for the current spawn. First the frame-0 latched final placement
        // the build manager stored in map data; when the latch has not run
        // (e.g. placement calc failed), fall back to the owner-verified
        // placements table so the test still targets the owner's spot.
        private static Vector2Dto? ResolveMacroHatcherySite()
        {
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var placement = Globals.CurrentMapData?.MacroHatcheryPlacements?.ElementAtOrDefault(startIndex);
            if (placement != null && HasNonZeroPoint(placement))
            {
                return placement;
            }

            var townhall = Globals.CurrentMapData?.StartingTownHall?.ElementAtOrDefault(startIndex);
            if (townhall == null)
            {
                return null;
            }

            var tableCenter = MacroHatcheryPlacementService.MacroHatcheryOwnerPlacements.Get(
                new Point2D { X = townhall.X, Y = townhall.Y });
            return tableCenter != null
                ? new Vector2Dto(tableCenter.X, tableCenter.Y, townhall.Z)
                : null;
        }

        // Issues BUILD_HATCHERY at the Macro Hatchery site (B3's terminal morph
        // row). A null site leaves the row unexecuted so the caller retries.
        private static SC2Action? CreateInstructionBuildHatcheryAction(ulong workerTag, Vector2Dto site, bool queued)
        {
            if (workerTag == 0 || site == null)
            {
                return null;
            }

            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.BUILD_HATCHERY,
                TargetWorldSpacePos = new Point2D { X = site.X, Y = site.Y },
                QueueCommand = queued
            };
            command.UnitTags.Add(workerTag);
            return new SC2Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        // --- 13-B3 extractor trick (owner spec 2026-10-15 22:20) -------------------

        // True while the worker's book waits on the VaWalkRedirect gate row (the
        // ONE redirect move that ends at VA). See the collision skip at the worker
        // loop for why this window must be detour-free.
        private static bool IsGateHeldVaWalkRedirect(RuntimeWorkerState runtimeWorker)
        {
            if (runtimeWorker == null
                || runtimeWorker.CurInstrIdx < 0
                || runtimeWorker.CurInstrIdx >= runtimeWorker.Instructions.Count)
            {
                return false;
            }

            var instruction = runtimeWorker.Instructions[runtimeWorker.CurInstrIdx];
            return instruction != null
                && instruction.Command == WorkerInstructionCommand.Move
                && instruction.Point == WorkerInstructionPoint.VaWalkRedirect;
        }

        // True while the extractor-trick driver owns this worker (phase 1 = build
        // issued / extractor under construction waiting for the morph-begin cancel;
        // phase 2 = cancel issued, waiting for the restored drone). Collision
        // avoidance must not issue this worker ANY detour in this window: an
        // unqueued SMART during the build walk cancels the build order (run4,
        // frame 935) and the trick silently falls apart.
        private bool IsB3TrickWorkerCommitted(RuntimeWorkerState runtimeWorker)
        {
            return runtimeWorker != null
                && runtimeWorker.UnitTag != 0
                && runtimeWorker.UnitTag == _b3TrickWorkerTag
                && (_b3TrickPhase == 1 || _b3TrickPhase == 2);
        }

        // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): true while B3's
        // book sits on the ONE mhwalk gather row — the 25-frame far-mineral
        // walk. A collision detour would replace the ONE walk with a detour
        // SMART and the RelativeFrame-25 return row would fire from a
        // suspended book (same failure as the 23:09 run). Skip collision
        // intervention for this worker ONLY while that row is pending.
        private static bool IsB3MacroHatcheryWalker(RuntimeWorkerState runtimeWorker)
        {
            if (runtimeWorker == null
                || runtimeWorker.CurInstrIdx < 0
                || runtimeWorker.CurInstrIdx >= runtimeWorker.Instructions.Count)
            {
                return false;
            }

            var instruction = runtimeWorker.Instructions[runtimeWorker.CurInstrIdx];
            return instruction != null
                && string.Equals(instruction.InstructionSet, "mhwalk", StringComparison.OrdinalIgnoreCase)
                && instruction.Command == WorkerInstructionCommand.Gather;
        }

        // The main-base VA geyser for the current spawn (persisted ordering + label).
        private static OrderedVespene ResolveVaGeyserEntry()
        {
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            return Globals.CurrentMapData?.OrderedMainVespene?.ElementAtOrDefault(startIndex)?
                .FirstOrDefault(candidate => candidate != null
                    && string.Equals(candidate.Label, "VA", StringComparison.OrdinalIgnoreCase));
        }

        // The LIVE unit tag of the VA geyser, matched by position against the
        // snapshot (same matching OngoingMapData uses for its tag refresh).
        // Persisted OrderedMainVespene.UnitTag values come from the map-data
        // recording game and are stale in a live game — the engine silently drops
        // a build order that targets a dead tag.
        private static ulong ResolveLiveVaGeyserTag()
        {
            var vaGeyser = ResolveVaGeyserEntry();
            if (vaGeyser?.Position == null || !HasNonZeroPoint(vaGeyser.Position))
            {
                return 0;
            }

            var snapshot = Globals.CurrentObservation;
            var live = snapshot?.Vespene?.Values.FirstOrDefault(vespene => vespene != null
                && Math.Abs(vespene.Position.X - vaGeyser.Position.X) < 1.0f
                && Math.Abs(vespene.Position.Y - vaGeyser.Position.Y) < 1.0f);
            return live?.UnitTag ?? 0;
        }

        // Point resolver for B3's ONE post-walk move to VA. Timing comes from the
        // book row's RelativeFrame (owner spec 2026-10-16 00:20): the xwalk move row
        // fires a fixed number of frames after the mineral walk starts — measured at
        // 74 frames end-to-end, the move issues at relative frame 72, two frames
        // before the walker reaches the mineral — so this resolver is UNGATED and
        // simply returns the VA geyser position (the earlier distance-based gate
        // missed the arrival window and let the worker mine the walk mineral).
        // Returns null only when the VA geometry is unavailable, in which case the
        // row waits silently as before.
        private static Vector2Dto? ResolveVaWalkRedirectPoint(RuntimeWorkerState runtimeWorker, WorkerInstruction instruction)
        {
            var vaGeyser = ResolveVaGeyserEntry();
            if (vaGeyser?.Position == null || !HasNonZeroPoint(vaGeyser.Position))
            {
                return null;
            }

            return new Vector2Dto(vaGeyser.Position.X, vaGeyser.Position.Y, vaGeyser.Position.Z);
        }

        // The immediate larva morph (owner spec 2026-10-15 15:53/15:57 — the larva
        // starts IMMEDIATELY, in the same frame as the extractor build). Reads the
        // last snapshot (the raw observation isn't threaded into instruction
        // execution); the trick driver re-checks against the raw observation and
        // retries next frame when this came back empty.
        private SC2Action? TryCreateB3LarvaMorphAction()
        {
            var snapshot = Globals.CurrentObservation;
            if (snapshot?.SelfUnits == null || _currentMinerals < 50)
            {
                return null;
            }

            var larva = snapshot.SelfUnits.Values.FirstOrDefault(unit => unit != null
                && unit.UnitType == (uint)UnitTypes.ZERG_LARVA
                && (unit.OrderAbilityIds == null || unit.OrderAbilityIds.Count == 0));
            if (larva == null || larva.UnitTag == 0)
            {
                return null;
            }

            _b3TrickLarvaTag = larva.UnitTag;
            Console.WriteLine($"[EXTRACTOR TRICK] larva morph same-frame larva={larva.UnitTag} minerals={_currentMinerals}");
            return CreateRawUnitCommandAction(Abilities.TRAIN_DRONE, larva.UnitTag);
        }

        // Per-frame trick driver. Phase 1 (build issued): keep trying the IMMEDIATE
        // larva morph until the observation shows it begun — verified by the bank
        // dropping below 50 (the morph's spend; owner spec 2026-10-15 22:50) with
        // the larva's TRAIN_DRONE order as confirmation — then cancel the extractor
        // THAT frame. Phase 2 (cancel issued): once the cancelled extractor restores
        // the drone, load the build manager's "b3ret" BB loop ("then loop BB"). A
        // timeout cancels a stuck extractor so the worker is never held hostage.
        // Only B3's tag is ever commanded here.
        private List<SC2Action> UpdateB3ExtractorTrick(ResponseObservation observation)
        {
            var actions = new List<SC2Action>();
            if ((_b3TrickPhase != 1 && _b3TrickPhase != 2) || _b3TrickWorkerTag == 0)
            {
                return actions;
            }

            var units = observation?.Observation?.RawData?.Units;
            if (units == null)
            {
                return actions;
            }

            if (_b3TrickPhase == 1)
            {
                // Cancel trigger (owner spec 2026-10-16 00:45): "after the extractor
                // starts AND a new worker begins then cancel the extractor". BOTH must
                // hold: the extractor unit exists (the drone's build morph committed —
                // during the build WALK the extractor is not in the observation yet)
                // and the larva morph has begun (its TRAIN_DRONE order observed, or
                // the bank below 50 — the morph's spend). Requiring both keeps the
                // drone on the geyser when the morph failed instead of cancelling an
                // extractor that was never paid for by a new worker.
                var morphOrderObserved = _b3TrickLarvaTag != 0
                    && units.Any(unit => unit != null
                        && unit.Tag == _b3TrickLarvaTag
                        && unit.Orders.Any(order => order.AbilityId == (uint)Abilities.TRAIN_DRONE));
                var morphSpendObserved = _currentMinerals < 50
                    || (_b3TrickLastMinerals >= 0
                        && _b3TrickLastMinerals - _currentMinerals >= 45
                        && _b3TrickLastMinerals - _currentMinerals < 70);
                var morphBegun = morphOrderObserved || morphSpendObserved;
                var extractor = FindVaExtractor(units);
                if (extractor == null)
                {
                    // Build re-issue (run 2026-10-16 19:02 postmortem): the book's
                    // redirect move, the mineral wait, and the build row can ALL
                    // satisfy on the same frame — the engine then receives
                    // MOVE + BUILD_EXTRACTOR for the drone in ONE batch and
                    // coalesces them away (the documented behavior behind
                    // IssueReturnInterrupts), so the extractor never exists, the
                    // book is already exhausted behind the terminal build row,
                    // and phase 1 used to sit silent until the 900-frame timeout
                    // while B3 mined BB. Until the extractor unit is OBSERVED,
                    // re-establish the build every frame the drone's own order
                    // is not already the build (one command for the drone per
                    // frame from this driver, which runs alone on B3 — never
                    // coalesced, and it self-heals the same-frame collapse, a
                    // collision SMART killing the walk, or a stale geyser tag).
                    var geyserTag = ResolveLiveVaGeyserTag();
                    var drone = units.FirstOrDefault(unit => unit != null && unit.Tag == _b3TrickWorkerTag);
                    var buildOrderHolds = drone != null
                        && drone.Orders.Any(order => order.AbilityId == (uint)Abilities.BUILD_EXTRACTOR);
                    if (geyserTag != 0 && drone != null && !buildOrderHolds)
                    {
                        var reissue = CreateInstructionBuildExtractorAction(_b3TrickWorkerTag, geyserTag, false);
                        if (reissue != null)
                        {
                            actions.Add(reissue);
                            Console.WriteLine($"[EXTRACTOR TRICK] build re-issued worker={_b3TrickWorkerTag} geyser={geyserTag} minerals={_currentMinerals} frame={_currentFrame}");
                        }
                    }
                }

                if (extractor != null && _b3TrickExtractorFirstSeenFrame < 0)
                {
                    _b3TrickExtractorFirstSeenFrame = _currentFrame;
                }
                if (extractor != null && morphBegun)
                {
                    actions.Add(CreateRawUnitCommandAction(Abilities.CANCEL_BUILDINPROGRESS, extractor.Tag));
                    _b3TrickPhase = 2;
                    Settings.B3ExtractorTrickMorphBegun = morphOrderObserved || units.Any(unit => unit != null
                        && unit.Tag == _b3TrickLarvaTag
                        && unit.Orders.Any(order => order.AbilityId == (uint)Abilities.TRAIN_DRONE));
                    Settings.B3ExtractorTrickDone = true;
                    Console.WriteLine($"[EXTRACTOR TRICK] cancel issued extractor={extractor.Tag} worker={_b3TrickWorkerTag} minerals={_currentMinerals} trigger: morphOrder={morphOrderObserved} bankBelow50={morphSpendObserved} morphBegun={Settings.B3ExtractorTrickMorphBegun} frame={_currentFrame}");
                }
                else
                {
                    var playerCommon = observation.Observation.PlayerCommon;
                    var foodLeft = playerCommon == null
                        ? 0
                        : (int)playerCommon.FoodCap - (int)playerCommon.FoodUsed;
                    var larva = units.FirstOrDefault(unit => unit != null
                        && unit.Alliance == Alliance.Self
                        && unit.UnitType == (uint)UnitTypes.ZERG_LARVA
                        && unit.Orders.Count == 0);
                    if (larva != null && foodLeft >= 1 && _currentMinerals >= 50)
                    {
                        _b3TrickLarvaTag = larva.Tag;
                        actions.Add(CreateRawUnitCommandAction(Abilities.TRAIN_DRONE, larva.Tag));
                        Console.WriteLine($"[EXTRACTOR TRICK] larva morph issued larva={larva.Tag} foodLeft={foodLeft} minerals={_currentMinerals} frame={_currentFrame}");
                    }

                    // Never let the extractor FINISH (owner spec 2026-10-16 00:45 "do
                    // not Finish the extractor"): this branch runs only while the
                    // morph has NOT begun — if the extractor is about to complete
                    // anyway (the morph kept losing the food race), abort: cancel
                    // it and send 13-B3 back to mining BB. A failed trick must not
                    // eat the drone into a finished extractor. The abort waits out
                    // B3TrickAbortGraceFrames after the extractor's first
                    // observation (run9 postmortem: the extractor commit is what
                    // frees the supply point the morph needs, so a just-committed
                    // extractor is a HEALTHY trick one frame old, not a failure).
                    var abortExtractor = FindVaExtractor(units, 0.9f);
                    var abortGraceExpired = _b3TrickExtractorFirstSeenFrame >= 0
                        && _currentFrame > _b3TrickExtractorFirstSeenFrame + B3TrickAbortGraceFrames;
                    if (abortExtractor != null
                        && (abortExtractor.BuildProgress >= 0.9f || abortGraceExpired))
                    {
                        actions.Add(CreateRawUnitCommandAction(Abilities.CANCEL_BUILDINPROGRESS, abortExtractor.Tag));
                        Settings.B3ExtractorTrickDone = true;
                        _b3TrickPhase = 2;
                        Console.WriteLine($"[EXTRACTOR TRICK] MORPH NEVER BEGAN — aborting at {(int)(abortExtractor.BuildProgress * 100)}% (grace expired) so the extractor never finishes worker={_b3TrickWorkerTag} frame={_currentFrame}");
                    }
                    else if (_b3TrickBuildFrame >= 0 && _currentFrame > _b3TrickBuildFrame + B3TrickMorphTimeoutFrames)
                    {
                        var stuckExtractor = FindVaExtractor(units);
                        if (stuckExtractor != null)
                        {
                            actions.Add(CreateRawUnitCommandAction(Abilities.CANCEL_BUILDINPROGRESS, stuckExtractor.Tag));
                            Console.WriteLine($"[EXTRACTOR TRICK] MORPH TIMEOUT — cancelling extractor worker={_b3TrickWorkerTag} frame={_currentFrame}");
                        }
                        else
                        {
                            // No extractor in the observation at timeout: the build
                            // order died before the drone committed (run4 frame 935:
                            // collision SMART killed the build walk) — say so loudly
                            // instead of silently falling into phase 2.
                            Console.WriteLine($"[EXTRACTOR TRICK] MORPH TIMEOUT — NO EXTRACTOR EXISTS worker={_b3TrickWorkerTag} frame={_currentFrame}");
                        }
                        Settings.B3ExtractorTrickDone = true;
                        _b3TrickPhase = 2;
                    }
                }
            }
            else if (_b3TrickPhase == 2)
            {
                var worker = units.FirstOrDefault(unit => unit != null && unit.Tag == _b3TrickWorkerTag);
                if (worker != null
                    && WorkerTypes.Contains((UnitTypes)worker.UnitType)
                    && Settings.RuntimeWorkers.TryGetValue(_b3TrickWorkerTag, out var runtimeWorker))
                {
                    // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): after
                    // the trick, prefer the "b3mh" book (mine BB once -> 25-frame
                    // hatchery-side mineral walk -> return cargo -> MH site ->
                    // wait 275 -> morph). "b3ret" remains the fallback when the
                    // hatchery-side mineral geometry was unavailable at
                    // onboarding.
                    var bookName = B3ReturnInstructionSet;
                    if (!Settings.RuntimeInstructionSets.TryGetValue(B3MacroHatcheryInstructionSet, out var book)
                        || book == null)
                    {
                        Settings.RuntimeInstructionSets.TryGetValue(B3ReturnInstructionSet, out book);
                    }
                    else
                    {
                        bookName = B3MacroHatcheryInstructionSet;
                    }

                    if (book != null)
                    {
                        runtimeWorker.LoadInstructions(bookName, book, Settings.GetRelativeFrame(_currentFrame));
                        _b3TrickPhase = 3;
                        Console.WriteLine($"[EXTRACTOR TRICK] cancel complete — {bookName} loaded worker={_b3TrickWorkerTag} frame={_currentFrame}");
                    }
                }
            }

            // Refresh the bank snapshot every phase-1/2 frame so the morph-spend
            // drop detection above compares THIS frame's bank against LAST frame's
            // (run10 postmortem 2026-10-16: morph spend 120 -> 70 never trips the
            // <50 bank check, but the 50-point drop is unambiguous).
            _b3TrickLastMinerals = _currentMinerals;

            return actions;
        }

        // A self extractor under construction at the VA geyser (the trick one).
        // maxProgress lets the abort check see extractors that are ABOUT to finish.
        private static Unit? FindVaExtractor(IEnumerable<Unit> units, float maxProgress = 1.0f)
        {
            var vaGeyser = ResolveVaGeyserEntry();
            if (vaGeyser?.Position == null || !HasNonZeroPoint(vaGeyser.Position))
            {
                return null;
            }

            var vaX = vaGeyser.Position.X;
            var vaY = vaGeyser.Position.Y;
            return units.FirstOrDefault(unit => unit != null
                && unit.Alliance == Alliance.Self
                && unit.UnitType == (uint)UnitTypes.ZERG_EXTRACTOR
                && unit.BuildProgress < maxProgress
                && DistanceSquared(new Vector2Dto(unit.Pos.X, unit.Pos.Y), new Vector2Dto(vaX, vaY)) <= 4.0f);
        }

        // --- B3 Macro Hatchery test (owner spec 2026-10-16 18:35) ---------------

        // Milestone watcher. SUCCESS: a self hatchery (the morphing drone's new
        // unit type) is observed within 3u of the site. FAILURE: the verify
        // window elapsed with none — the placement was rejected or the morph
        // never engaged (e.g. the bank held 275 while the morph prices at 300;
        // see MacroHatcheryBuildMinerals). Either way the bot freezes: loud
        // log, persistent on-screen banner, Debugger.Break for a VS session.
        private void UpdateB3MacroHatcheryTest(ResponseObservation observation)
        {
            if (_b3MhBuildIssuedFrame < 0 || _b3MhResolved)
            {
                return;
            }

            var site = ResolveMacroHatcherySite();
            var units = observation?.Observation?.RawData?.Units;
            if (units != null && site != null)
            {
                var hatchery = units.FirstOrDefault(unit => unit != null
                    && unit.Alliance == Alliance.Self
                    && unit.UnitType == (uint)UnitTypes.ZERG_HATCHERY
                    && DistanceSquared(new Vector2Dto(unit.Pos.X, unit.Pos.Y), site) <= 9.0f);
                if (hatchery != null)
                {
                    ResolveB3MacroHatcheryTest("SUCCESS",
                        $"hatchery tag={hatchery.Tag} progress={(int)(hatchery.BuildProgress * 100)}% at ({hatchery.Pos.X:F1},{hatchery.Pos.Y:F1})");
                    return;
                }
            }

            if (_currentFrame > _b3MhBuildIssuedFrame + B3MhBuildVerifyFrames)
            {
                ResolveB3MacroHatcheryTest("FAILURE",
                    site != null
                        ? $"no hatchery within {B3MhBuildVerifyFrames} frames of the build order — placement rejected at ({site.X:F1},{site.Y:F1}) or the morph never engaged (bank={_currentMinerals})"
                        : $"Macro Hatchery site never resolved — placement rejected or the morph never engaged (bank={_currentMinerals})");
            }
        }

        private void ResolveB3MacroHatcheryTest(string result, string detail)
        {
            _b3MhResolved = true;
            Settings.MacroHatcheryTestFrozen = true;
            _b3MhBanner = $"MACRO HATCHERY {result} — {detail}";
            Console.WriteLine($"[MACRO HATCHERY TEST] *** {result} *** {detail} frame={_currentFrame} — BOT FROZEN (unit commands stripped; debug draws alive)");
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
        }

        // Re-drawn every frame while frozen so the banner survives the freeze
        // (the build manager's circles keep drawing too).
        private void DrawB3MacroHatcheryBanner()
        {
            if (!ManagerDebugService.IsDebugEnabled || string.IsNullOrEmpty(_b3MhBanner))
            {
                return;
            }

            var site = ResolveMacroHatcherySite();
            var color = _b3MhBanner.Contains("SUCCESS")
                ? new Color { R = 0, G = 255, B = 0 }
                : new Color { R = 255, G = 0, B = 0 };
            var anchor = site != null
                ? new Point { X = site.X, Y = site.Y, Z = 16 }
                : new Point { X = 50, Y = 50, Z = 16 };
            ManagerDebugService.DrawText(_b3MhBanner, anchor, color, 20);
        }

        private static SC2Action CreateRawUnitCommandAction(Abilities ability, ulong unitTag)
        {
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)ability,
                QueueCommand = false
            };
            command.UnitTags.Add(unitTag);
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
                    // The bracket token names the REAL command context — "{pair}.{phase}.{row
                    // index}" from the instruction row's pair reference (e.g.
                    // "[Mining Command  TA-TB.ReturnCargo.7]") — never a made-up sequence
                    // number. The numeric trace id stays in the line for file correlation.
                    var commandSetLabel = DescribeCommandSet(context, command.AbilityId);

                    Console.WriteLine($"[Mining Command  {commandSetLabel}.{instructionIndex}] frame={_currentFrame} relative={relativeFrame} trace={commandTraceId} worker={workerTag} Label={workerLabel} command={commandName}{target}{targetTag}{queued} set={instructionSet} idx={instructionIndex} instr={instructionCommand} point={instructionPoint}");

                    AppendInstructionTrace(new
                    {
                        RecordType = "MiningCommand",
                        TimestampUtc = DateTime.UtcNow,
                        GameFrame = _currentFrame,
                        RelativeFrame = relativeFrame,
                        CommandTraceId = commandTraceId,
                        WorkerTag = workerTag,
                        WorkerLabel = workerLabel,
                        CommandSetLabel = $"{commandSetLabel}.{instructionIndex}",
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

        // Bracket-label for the console trace: "{labelA}-{labelB}.{phase}" parsed from the
        // instruction row's pair reference ("{base}-{A}-{B}.{property}", already in
        // canonical order) with the phase taken from the issued ability — return abilities
        // always read "ReturnCargo" even though the pair table stores "ReturnPoint".
        // Falls back to the bare instruction set name when the row has no reference
        // (e.g. collision-detour commands print "collision").
        private static string DescribeCommandSet(InstructionCommandContext? context, int abilityId)
        {
            var reference = context?.TargetPointReference;
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var dot = reference.LastIndexOf('.');
                if (dot > 0 && dot < reference.Length - 1)
                {
                    var key = reference.Substring(0, dot);
                    var property = reference.Substring(dot + 1);
                    var firstDash = key.IndexOf('-');
                    if (firstDash > 0 && firstDash < key.Length - 1)
                    {
                        var pair = key.Substring(firstDash + 1);
                        var phase = CommandPhaseForAbility(abilityId, property);
                        if (!string.IsNullOrEmpty(phase))
                        {
                            return $"{pair}.{phase}";
                        }
                    }
                }
            }
            return context?.InstructionSet ?? string.Empty;
        }

        private static string CommandPhaseForAbility(int abilityId, string property)
        {
            if (abilityId == (int)Abilities.HARVEST_RETURN || abilityId == (int)Abilities.HARVEST_RETURN_DRONE)
            {
                return "ReturnCargo";
            }
            if (string.Equals(property, "ReturnPoint", StringComparison.OrdinalIgnoreCase)) return "ReturnCargo";
            if (string.Equals(property, "HarvestA", StringComparison.OrdinalIgnoreCase)) return "HarvestA";
            if (string.Equals(property, "HarvestB", StringComparison.OrdinalIgnoreCase)) return "HarvestB";
            if (string.Equals(property, "WaitPointA", StringComparison.OrdinalIgnoreCase)) return "WaitPointA";
            if (string.Equals(property, "WaitPointB", StringComparison.OrdinalIgnoreCase)) return "WaitPointB";
            return string.Empty;
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
