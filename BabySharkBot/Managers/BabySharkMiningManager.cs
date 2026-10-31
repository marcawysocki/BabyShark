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
        // Early suicide test end (owner spec 2026-10-17 22:41): collision
        // iteration harness only — once the bank first reaches 200 minerals,
        // cancel the spawning pool and send all drones to attack our own
        // hatchery so the test game resolves in seconds instead of minutes.
        private bool _suicideTestEndTriggered = false;
        // Role-first-return trigger (owner spec 2026-10-28 08:14): latched
        // once the role worker's IsCarrying has been observed true, so the
        // first carrying -> not-carrying transition (the hatchery deposit)
        // fires the suicide.
        private bool _suicideRoleHasCarried = false;
        // Generic ATTACK ability id (protocol constant; the external Abilities
        // enum in use here has no ATTACK member).
        private const int SuicideAttackAbilityId = 3674;
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

        // Harvest-wait heal cooldown (owner report 2026-10-25 21:36, B2 stops
        // mining): last frame a queued gather was re-armed for a worker parked
        // orderless at a 1183->1184 harvest-completion wait. Bounds re-arms to
        // one per HarvestWaitHealCooldownFrames even if the re-armed gather
        // fizzles again.
        private const int HarvestWaitHealCooldownFrames = 8;
        private readonly Dictionary<ulong, int> _harvestWaitHealLastFrame = new Dictionary<ulong, int>();

        private int GetHarvestWaitHealLastFrame(ulong workerTag)
        {
            return _harvestWaitHealLastFrame.TryGetValue(workerTag, out var frame) ? frame : -HarvestWaitHealCooldownFrames;
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

        // 5x5 chase-bump clearance gate (owner spec 2026-10-19): the frame-16 central-BB
        // mineral walk ends the moment the walker is BumpClearGateRadius (0.4u) from
        // EVERY other assigned worker. Timeout fail-open mirrors the wait-walk gate;
        // hold start is tracked per worker here for the same InstructionStartFrame-reset
        // reason.
        private readonly Dictionary<ulong, int> _bumpClearGateHoldStartFrames = new Dictionary<ulong, int>();
        private const float BumpClearGateRadius = 0.4f;
        private const int BumpClearGateTimeoutFrames = 70;

        // 5x5 chase-bump follow resolver state — RETIRED 2026-10-20 with the
        // distance triggers (owner spec "South New Worker Order"; owner report
        // on the triggers: "I don't think it was working as intended"). The
        // frame-schedule rows (walk 1-5, chase 6-18, SB walk 19, stage 25) gate
        // themselves; the only resolver state left is the Y-side clear-hold
        // start frames for the "mineral walk to SB until clear" deadline.
        private readonly Dictionary<ulong, int> _chaseBumpSetStartFrames = new Dictionary<ulong, int>();
        private const int BumpChaseMaxRelativeFrame = 20;

        // First-harvest wait (owner directive 2026-10-19 23:19): the four chase
        // helpers hold on the central SA mineral until their chased partner
        // finishes its first harvest. Absolute fail-open — the first harvest
        // always completes well before this frame; past it the wait never
        // holds (a broken partner observation cannot park the walker).
        private const int BumpFirstHarvestWaitMaxFrame = 300;
        private readonly HashSet<ulong> _bumpFirstHarvestHoldLogged = new HashSet<ulong>();

        private readonly object _instructionTraceLock = new object();
        private readonly JsonSerializerOptions _instructionTraceJsonOptions = new JsonSerializerOptions { WriteIndented = false };
        private string _instructionTraceFile;
        private bool _instructionTraceFileResolved;
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
        }

        // The trace file is named lazily on first write so the NW/SE start-location tag
        // (resolved in OnStart, after this constructor runs) can be included in the name.
        private void EnsureInstructionTraceFile()
        {
            if (_instructionTraceFileResolved)
            {
                return;
            }
            _instructionTraceFileResolved = true;
            try
            {
                var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "mining_tests");
                Directory.CreateDirectory(folder);
                var tag = MiningTestLogNaming.TagForFileName();
                _instructionTraceFile = Path.Combine(folder, $"worker_instruction_lists{tag}_{DateTime.Now:yyyyMMddHHmmss}.jsonl");
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
                UpdateVisualMineralLabelState(tag);
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

                // Mineral walk overrides every color cue (owner spec 2026-10-17
                // 08:11): the ENTIRE label is red for the whole walk.
                if (Settings.RuntimeWorkers.TryGetValue(tag, out var drawWorker)
                    && drawWorker.CollisionDetourActive)
                {
                    ManagerDebugService.DrawText(displayLabel, new Point
                    {
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z + 0.5f
                    }, CollisionRedColor, 12);
                    continue;
                }

                // Two-tone label (owner spec 2026-10-17 08:11): after a pickup
                // the number keeps the collected mineral's color while the
                // letters show the next mineral; after cargo return the whole
                // label is the next mineral's color. Drawn as the full label
                // in the letter color with the number prefix overdrawn.
                if (TryResolveTwoToneLabelColors(tag, displayLabel, out var prefixColor, out var restColor))
                {
                    var drawPoint = new Point
                    {
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z + 0.5f
                    };
                    ManagerDebugService.DrawText(displayLabel, drawPoint, restColor, 12);
                    var dashIndex = displayLabel.IndexOf('-');
                    if (dashIndex > 0)
                    {
                        ManagerDebugService.DrawText(displayLabel.Substring(0, dashIndex + 1), drawPoint, prefixColor, 12);
                    }
                    continue;
                }

                ManagerDebugService.DrawText(displayLabel, new Point
                {
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z + 0.5f
                }, displayColor, 12);
            }
        }

        // Label color-cue state machine (owner spec 2026-10-17 08:11). Pickup
        // (1183 -> 1184) snapshots the just-collected mineral (current Mti) and
        // the next mineral (Mti + 1, wrapping — both are frozen at pickup time
        // because Mti may advance before the deposit frame). Cargo return then
        // flips the label to the full next-mineral color until the next pickup.
        private void UpdateVisualMineralLabelState(ulong workerTag)
        {
            if (!Settings.RuntimeWorkers.TryGetValue(workerTag, out var runtimeWorker))
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0
                ? Globals.CurrentStartIndex
                : Settings.CurrentSpawnIndex;
            var assignedWorker = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?.FirstOrDefault(worker => worker != null && worker.UnitID == workerTag);
            var targets = assignedWorker?.MiningTargets;
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            if (runtimeWorker.PreviousAbilityId == (int)Abilities.HARVEST_GATHER_DRONE
                && runtimeWorker.CurrentAbilityId == (int)Abilities.HARVEST_RETURN_DRONE)
            {
                var collected = targets.ElementAtOrDefault(assignedWorker.Mti)?.ResourceLabel;
                if (!string.IsNullOrWhiteSpace(collected))
                {
                    var nextIndex = assignedWorker.Mti + 1;
                    if (nextIndex >= targets.Count)
                    {
                        nextIndex = 0;
                    }
                    runtimeWorker.VisualCollectedLabel = collected;
                    runtimeWorker.VisualNextLabel = targets.ElementAtOrDefault(nextIndex)?.ResourceLabel ?? collected;
                    runtimeWorker.VisualFullNextLabel = false;
                }
            }
            else if (runtimeWorker.CargoReturned
                && !string.IsNullOrWhiteSpace(runtimeWorker.VisualNextLabel))
            {
                runtimeWorker.VisualFullNextLabel = true;
            }
        }

        // Two-tone resolution for the label draw: returns true when the worker
        // has picked up at least once. Full-next mode yields both colors equal
        // (the full-label overdraw is a no-op); otherwise the prefix is the
        // collected mineral and the rest the next mineral.
        private static bool TryResolveTwoToneLabelColors(ulong workerTag, string displayLabel, out Color prefixColor, out Color restColor)
        {
            prefixColor = null;
            restColor = null;
            if (string.IsNullOrWhiteSpace(displayLabel)
                || !Settings.RuntimeWorkers.TryGetValue(workerTag, out var runtimeWorker)
                || string.IsNullOrWhiteSpace(runtimeWorker.VisualNextLabel))
            {
                return false;
            }

            var nextColor = MineralLabelColor(runtimeWorker.VisualNextLabel);
            if (runtimeWorker.VisualFullNextLabel)
            {
                prefixColor = nextColor;
                restColor = nextColor;
                return true;
            }

            if (string.IsNullOrWhiteSpace(runtimeWorker.VisualCollectedLabel))
            {
                return false;
            }

            prefixColor = MineralLabelColor(runtimeWorker.VisualCollectedLabel);
            restColor = nextColor;
            return true;
        }

        // Mineral label -> display color: the far-B overrides first (same
        // palette as ResolveWorkerDisplayColor), then the label's team prefix.
        private static Color MineralLabelColor(string label)
        {
            if (string.Equals(label, "SB", StringComparison.OrdinalIgnoreCase)) return TeamColorService.Salmon;
            if (string.Equals(label, "BB", StringComparison.OrdinalIgnoreCase)) return TeamColorService.Green;
            if (string.Equals(label, "YB", StringComparison.OrdinalIgnoreCase)) return TeamColorService.Orange;
            if (string.Equals(label, "SA", StringComparison.OrdinalIgnoreCase)) return TeamColorService.Purple;
            return TeamColorService.GetColorByPrefix(label.Substring(0, 1));
        }

        private Color ResolveWorkerDisplayColor(ulong workerTag, string label, int startIndex)
        {
            // Mineral walk (owner spec 2026-10-17 08:11): the ENTIRE label is red
            // for the whole time the worker mineral-walks — not just the old
            // within-1u phase. Takes precedence over every other color cue.
            if (Settings.RuntimeWorkers.TryGetValue(workerTag, out var detouringWorker)
                && detouringWorker.CollisionDetourActive)
            {
                return CollisionRedColor;
            }

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
        // Freeze the whole bot when the milestone resolves (owner spec
        // 2026-10-16 18:35, for placement inspection). DISABLED 2026-10-16
        // 21:43 (owner report "workers stop mining and are standing at the H1
        // Hatchery"): the placement is verified, the freeze now fires ~21s in
        // and blocks the worker-collision work — with this false the game
        // keeps running and only the log line + on-screen banner mark the
        // milestone. Flip to true when testing a NEW placement (Roach Warren).
        private const bool FreezeBotOnMacroHatcheryMilestone = false;
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
        // NW spawn guard REMOVED (owner directive 2026-10-20 "Turn NW Start back
        // on … do not gate just remove the guard"): NW and SE games both run and
        // are measured now; only one start location runs per game.

        // --smsweep (owner spec 2026-10-29 08:39): lazy singleton per manager
        // instance — the service owns all worker commands while the flag is on.
        private SmSweepService _smSweepService;
        private SmSweepService GetSmSweepService()
        {
            if (_smSweepService == null)
            {
                _smSweepService = new SmSweepService(_mapData);
                Console.WriteLine("[SMSWEEP] service created");
            }
            return _smSweepService;
        }

        // --smstage (owner spec 2026-10-29 12:11): same pattern — the stage
        // service owns all worker commands while the flag is on.
        private SmStageService _smStageService;
        private SmStageService GetSmStageService()
        {
            if (_smStageService == null)
            {
                _smStageService = new SmStageService(_mapData);
                Console.WriteLine("[SMSTAGE] service created");
            }
            return _smStageService;
        }

        // --smhandoff (owner spec 2026-10-29 16:37): same pattern — the handoff
        // service owns all worker commands while the flag is on.
        private SmHandoffService _smHandoffService;
        private SmHandoffService GetSmHandoffService()
        {
            if (_smHandoffService == null)
            {
                _smHandoffService = new SmHandoffService(_mapData);
                Console.WriteLine("[SMHANDOFF] service created");
            }
            return _smHandoffService;
        }

        public IEnumerable<SC2Action> OnFrame(ResponseObservation observation)
        {
            _currentFrame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            _currentMinerals = observation?.Observation?.PlayerCommon == null ? 0 : (int)observation.Observation.PlayerCommon.Minerals;
            // Owner request 2026-10-27 11:16: break EVERY frame 5..25 (was
            // every 5th frame) for stepping the T1/T4 bump choreography.
            // 2026-10-29 19:15 (owner "Can I step thru to see if you are thinking
            // correct"): the handoff test's interesting events run to ~f105
            // (32-harvest-frame Smart release, walk-ins, promotion), so in
            // --smhandoff mode the break window extends to frame 120.
            var stepBreakWindow = Settings.SmHandoffMode ? 120 : 25;
            if ((_currentFrame >= 5 && _currentFrame <= stepBreakWindow) || _anyWorkerRed)
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
            UpdateCollisionVisualStates();
            WriteInstructionTrace();
            TraceBumpContact();

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
            if (_b3MhResolved)
            {
                // Freeze disabled (collision-work phase): the game keeps running
                // past the milestone; keep the success/failure banner on screen
                // as a persistent annotation.
                DrawB3MacroHatcheryBanner();
            }

            // --smsweep (owner spec 2026-10-29 08:39): the sweep service owns
            // EVERY worker command this frame — skip the extractor trick driver,
            // runtime books, return interrupts and all normal mining logic
            // (ProcessFrameObservation above already ran label/tracking
            // updates, which are command-free).
            if (Settings.SmSweepMode)
            {
                var sweepActions = GetSmSweepService().OnFrame(observation, _currentFrame);
                LogMiningCommands(sweepActions, new List<InstructionCommandContext>());
                return sweepActions;
            }

            // --smstage (owner spec 2026-10-29 12:11): the stage service owns
            // EVERY worker command this frame — same suppression as --smsweep.
            if (Settings.SmStageMode)
            {
                var stageActions = GetSmStageService().OnFrame(observation, _currentFrame);
                LogMiningCommands(stageActions, new List<InstructionCommandContext>());
                return stageActions;
            }

            // --smhandoff (owner spec 2026-10-29 16:37): the handoff service
            // owns EVERY worker command this frame — same suppression.
            if (Settings.SmHandoffMode)
            {
                var handoffActions = GetSmHandoffService().OnFrame(observation, _currentFrame);
                LogMiningCommands(handoffActions, new List<InstructionCommandContext>());
                return handoffActions;
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

            // Early suicide test end (collision iteration harness, owner spec
            // 2026-10-17 22:41): when the bank first reaches 200 minerals this
            // manager takes over completely for the rest of the game — cancel
            // the spawning pool and have all drones attack our own hatchery so
            // the game ends quickly and the run loop can restart. Returning
            // the end actions here also suppresses the books/trick driver, so
            // nothing re-issues the pool build or any mining command.
            if (Settings.EarlySuicideTestEnd)
            {
                var endActions = TryBuildEarlySuicideTestEndActions(observation);
                if (endActions != null)
                {
                    LogMiningCommands(endActions, commandContexts);
                    return endActions;
                }
            }

            LogMiningCommands(actions, commandContexts);
            return actions;
        }

        // Returns null until the trigger frame; after that, every frame returns
        // the standing cancel-pool + attack-hatchery action set.
        private List<SC2Action> TryBuildEarlySuicideTestEndActions(ResponseObservation observation)
        {
            if (!_suicideTestEndTriggered)
            {
                if (Settings.EarlySuicideOnRoleReturn)
                {
                    // Owner spec 2026-10-28 08:14: fire when the named role
                    // (default T1) has RETURNED ITS FIRST CARGO — the metric
                    // is now time-to-first-return; the 200-mineral bank
                    // trigger is kept behind --suicide-200 for later.
                    if (!CheckEarlySuicideRoleReturnedCargo())
                    {
                        // Fail-safe: a broken test book can never park the
                        // harness — past frame 900 end the run anyway.
                        if (_currentFrame < 900)
                        {
                            return null;
                        }
                        Console.WriteLine($"[SUICIDE-END FAIL-OPEN] frame={_currentFrame} role={Settings.EarlySuicideRole} never returned first cargo — ending run anyway");
                    }
                }
                else if (_currentMinerals < 200 || _currentFrame <= 100)
                {
                    return null;
                }
                _suicideTestEndTriggered = true;
                Console.WriteLine($"[SUICIDE-END] frame={_currentFrame} bank={_currentMinerals} trigger={(Settings.EarlySuicideOnRoleReturn ? Settings.EarlySuicideRole + " first-return" : "200-mineral bank")} — cancelling pool, all drones attacking own hatchery");
            }

            var actions = new List<SC2Action>();
            var units = observation?.Observation?.RawData?.Units;
            if (units == null)
            {
                return actions;
            }

            foreach (var unit in units)
            {
                if (unit == null || unit.Alliance != Alliance.Self)
                {
                    continue;
                }
                if (unit.UnitType == (uint)UnitTypes.ZERG_SPAWNINGPOOL)
                {
                    actions.Add(CreateRawUnitCommandAction(Abilities.CANCEL_BUILDINPROGRESS, unit.Tag));
                }
            }

            var hatchery = units.FirstOrDefault(u => u != null
                && u.Alliance == Alliance.Self
                && u.UnitType == (uint)UnitTypes.ZERG_HATCHERY);
            var droneTags = units.Where(u => u != null
                    && u.Alliance == Alliance.Self
                    && u.UnitType == (uint)UnitTypes.ZERG_DRONE)
                .Select(u => u.Tag)
                .ToList();
            if (hatchery != null && droneTags.Count > 0)
            {
                var command = new ActionRawUnitCommand
                {
                    AbilityId = SuicideAttackAbilityId,
                    TargetUnitTag = hatchery.Tag,
                    QueueCommand = false
                };
                command.UnitTags.AddRange(droneTags);
                actions.Add(new SC2Action { ActionRaw = new ActionRaw { UnitCommand = command } });
            }
            return actions;
        }

        // Owner spec 2026-10-28 08:14: the early-suicide trigger moves from the
        // 200-mineral bank to "when the named role (default T1, later Y1 via
        // --suicide-role=) has returned its FIRST CARGO". Carrying state comes
        // from ObservationManager's live RuntimeWorkers update (mineral buff
        // 271/272). True exactly once: the first carrying -> not-carrying
        // transition after frame 40 (spawn-noise floor) — the deposit frame.
        private bool CheckEarlySuicideRoleReturnedCargo()
        {
            var startIndex = Globals.CurrentStartIndex >= 0
                ? Globals.CurrentStartIndex
                : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();
            var roleWorker = assignedWorkers.FirstOrDefault(candidate =>
                string.Equals(candidate?.Role, Settings.EarlySuicideRole, StringComparison.OrdinalIgnoreCase));
            if (roleWorker == null)
            {
                return false;
            }

            var carrying = Settings.RuntimeWorkers.TryGetValue(roleWorker.UnitID, out var runtimeWorker)
                && runtimeWorker.IsCarrying;
            if (carrying)
            {
                _suicideRoleHasCarried = true;
                return false;
            }

            if (_suicideRoleHasCarried && _currentFrame > 40)
            {
                Console.WriteLine($"[SUICIDE-END TRIGGER] frame={_currentFrame} role={Settings.EarlySuicideRole} tag={roleWorker.UnitID} first cargo returned");
                return true;
            }

            return false;
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
            DrawBumpChoreographyGuides(observation);
            BreakWhenSpawnLabelsShouldBeVisible(observation);
        }

        // ── Bump choreography guide overlay (owner spec 2026-10-26 05:45) ──
        // Persistent per-frame debug geometry, BOTH start locations, ground height
        // (nothing floating), re-sent every frame so the overlay persists:
        //   • two red circles per hatchery position (own + the other start): the
        //     return-cargo footprint (hatchery unit radius 2.5) and +0.4u.
        //   • per mineral: harvest-distance circle (mineral center -> SM Harvest
        //     point) and +0.4u, in the mineral's own color.
        //   • TA-colored line TA SM Return -> TA SM Harvest.
        //   • 0.2-wide cross hatches on every worker's frame-0 start position.
        //   • telemetry circles from THIS run: T1 (TA color, radii = T1 distance
        //     from its start at frames 0/5/10/15/20), T4 (TB color) and T5 (SA
        //     color) at frames 0/5/10/15 — run each seed to get both starts' sets.
        // Hatchery circle 1 radius = MEASURED worker return-cargo stand distance
        // (owner rule 1, 2026-10-29): mean deposit-stand distance from hatchery
        // center in the natural-observation test — 966 deposits SE = 2.910u,
        // 956 deposits NW = 2.909u (min 2.80 / max 3.06 on both starts). Replaces
        // the old 2.75u hatchery unit-radius guess. Circle 2 = +0.4u.
        private const float BumpVizHatcheryReturnRadius = 2.91f;
        private const float BumpVizRingExtra = 0.4f;
        private const float BumpVizCrossHalf = 0.1f;
        private static readonly int[] BumpVizCaptureFrames = { 0, 5, 10, 15, 20 };
        private static readonly int[] BumpVizDrawFrames = { 5, 10, 15 };
        private readonly Dictionary<string, Dictionary<int, Vector2Dto>> _bumpVizSnapshots = new();

        // Live move-target latches for the three choreo workers, written in
        // LogMiningCommands on every issued command (owner request 2026-10-27
        // 07:13 — the per-frame "current position -> move target" lines with a
        // cross hatch on the move-to coordinate). IsWalk = the latched command
        // is a mineral-walk gather (T4's line turns red; moves stay white).
        private readonly Dictionary<string, (float X, float Y, bool IsWalk)> _bumpVizLiveTargets = new();

        // T1 SM-line-extended roll state (owner spec 2026-10-27 11:16):
        // 0 = rolling toward the extension target, 1 = confirmed ON the
        // SM return->harvest line, 2 = knocked OFF the line (or reached its
        // end) -> target the SM harvest point ("then target the mineral").
        private readonly Dictionary<ulong, int> _choreoT1LineState = new();

        private void DrawBumpChoreographyGuides(ResponseObservation observation)
        {
            if (!Settings.BumpChoreographyViz || _mapData == null || !ManagerDebugService.IsDebugEnabled) return;

            // Snapshot live worker positions at the milestone frames (this run's
            // telemetry); circles for every captured milestone redraw every frame.
            // Workers are resolved from the PERSISTED patch assignment (FinalLabel
            // + UnitTag + frame-0 Position) and matched to live observation BY TAG
            // — live SelfUnits label fields are not reliably populated during the
            // first frames, which silently killed the rings before.
            var snapshot = Globals.CurrentObservation;
            var observationFrame = observation?.Observation == null ? _currentFrame : (int)observation.Observation.GameLoop;
            var relativeFrame = Settings.GetRelativeFrame(observationFrame);
            var vizWorkers = ResolveBumpVizWorkers();
            if (snapshot?.SelfUnits != null && vizWorkers.Count > 0)
            {
                foreach (var vizWorker in vizWorkers)
                {
                    if (Array.IndexOf(BumpVizCaptureFrames, relativeFrame) < 0) break;
                    if (!_bumpVizSnapshots.TryGetValue(vizWorker.Label, out var snaps))
                    {
                        snaps = new Dictionary<int, Vector2Dto>();
                        _bumpVizSnapshots[vizWorker.Label] = snaps;
                    }
                    if (snaps.ContainsKey(relativeFrame)) continue;
                    if (!snapshot.SelfUnits.TryGetValue(vizWorker.Tag, out var live) || live?.Position == null) continue;
                    snaps[relativeFrame] = live.Position;
                    Console.WriteLine($"[BUMPVIZ] {vizWorker.Label} captured f{relativeFrame} pos=({live.Position.X:F2},{live.Position.Y:F2}) r={Math.Sqrt(DistanceSquared(vizWorker.Start, live.Position)):F2}");
                }
            }

            var red = new Color { R = 255, G = 0, B = 0 };
            var white = new Color { R = 255, G = 255, B = 255 };

            // Hatchery footprint circles — where the hatchery IS and WOULD BE on the
            // other start (both entries of StartingTownHall, every run).
            if (_mapData.StartingTownHall != null)
            {
                for (var i = 0; i < _mapData.StartingTownHall.Length; i++)
                {
                    var townHall = _mapData.StartingTownHall[i];
                    if (townHall == null) continue;
                    var z = ResolveBumpVizGroundZ(i);
                    DrawDebugCircle(townHall, BumpVizHatcheryReturnRadius, red, z);
                    DrawDebugCircle(townHall, BumpVizHatcheryReturnRadius + BumpVizRingExtra, red, z);
                }
            }

            // Mineral circles (harvest distance and +0.4u, mineral colors), the TA
            // SM Return -> SM Harvest line, and worker start cross hatches — for
            // BOTH start locations' patches.
            if (_mapData.TeamPatchAssignments != null)
            {
                for (var start = 0; start < _mapData.TeamPatchAssignments.Count; start++)
                {
                    var assignments = _mapData.TeamPatchAssignments[start];
                    if (assignments == null) continue;
                    foreach (var assignment in assignments)
                    {
                        if (assignment == null) continue;

                        OrderedMineral taMineral = null;
                        OrderedMineral tbMineral = null;
                        if (assignment.Minerals != null)
                        {
                            foreach (var mineral in assignment.Minerals)
                            {
                                if (mineral?.Position == null || mineral.HarvestPoint == null) continue;
                                if (!HasNonZeroPoint(mineral.HarvestPoint)) continue;
                                var color = ProcessVisableUnits.GetFinalLabelColor(
                                    string.IsNullOrWhiteSpace(mineral.FinalLabel) ? mineral.Label : mineral.FinalLabel);
                                // Circle 1 = the SM Harvest stand point — where the
                                // worker stands still to harvest (owner rule 1,
                                // 2026-10-29): radius = mineral center -> SmHarvestPoint,
                                // HarvestPoint only as fallback. Circle 2 = the wait
                                // zone, +0.4u beyond that.
                                var standPoint = HasNonZeroPoint(mineral.SmHarvestPoint) ? mineral.SmHarvestPoint : mineral.HarvestPoint;
                                var harvestRadius = (float)Math.Sqrt(DistanceSquared(mineral.Position, standPoint));
                                DrawDebugCircle(mineral.Position, harvestRadius, color, mineral.Position.Z);
                                DrawDebugCircle(mineral.Position, harvestRadius + BumpVizRingExtra, color, mineral.Position.Z);
                                if (string.Equals(mineral.Label, "TA", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(mineral.FinalLabel, "TA", StringComparison.OrdinalIgnoreCase))
                                {
                                    taMineral = mineral;
                                }
                                if (string.Equals(mineral.Label, "TB", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(mineral.FinalLabel, "TB", StringComparison.OrdinalIgnoreCase))
                                {
                                    tbMineral = mineral;
                                }
                            }
                        }

                        // SM Return -> SM Harvest line in the mineral's own color
                        // (TA per owner spec 2026-10-26 18:45; TB added per owner
                        // rule 3, 2026-10-29 — the TB line must clear TA's bulge).
                        foreach (var lineMineral in new[] { taMineral, tbMineral })
                        {
                            if (lineMineral?.SmReturnPoint == null || lineMineral.SmHarvestPoint == null) continue;
                            var lineColor = ProcessVisableUnits.GetFinalLabelColor(
                                string.IsNullOrWhiteSpace(lineMineral.FinalLabel) ? lineMineral.Label : lineMineral.FinalLabel);
                            ManagerDebugService.DrawLine(
                                new Point { X = lineMineral.SmReturnPoint.X, Y = lineMineral.SmReturnPoint.Y, Z = ResolveBumpVizGroundZ(start) },
                                new Point { X = lineMineral.SmHarvestPoint.X, Y = lineMineral.SmHarvestPoint.Y, Z = lineMineral.Position.Z },
                                lineColor);
                        }

                        // Choreography guide lines (owner spec 2026-10-26 18:45,
                        // green line re-anchored on TA mineral per 20:47 Paint):
                        // T1's green line (T1 start through the TA mineral, +2u) and
                        // T5's SA-intersect move line — static, drawn against the
                        // latched start positions. Current start only.
                        var choreoStart = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                        if (start == choreoStart && taMineral?.SmHarvestPoint != null && taMineral.SmReturnPoint != null)
                        {
                            Vector2Dto StartOf(string label) =>
                                assignments
                                    .SelectMany(a => a?.Workers ?? Enumerable.Empty<WorkerEntryDto>())
                                    .FirstOrDefault(w => w != null
                                        && (string.Equals(w.FinalLabel, label, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(w.Label, label, StringComparison.OrdinalIgnoreCase)))
                                    ?.Position;

                            var t1Start = StartOf("T1");
                            var t5Start = StartOf("T5");
                            var saMineralViz = assignments
                                .SelectMany(a => a?.Minerals ?? Enumerable.Empty<OrderedMineral>())
                                .FirstOrDefault(m => m != null
                                    && (string.Equals(m.FinalLabel, "SA", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(m.Label, "SA", StringComparison.OrdinalIgnoreCase)));

                            // T1's line: from the latched start THROUGH the
                            // TA SM harvest point, extended 2u past it — the
                            // direct SMH line T1 rides (owner test suite
                            // 2026-10-27 15:24). The endpoint is computed
                            // under the t1Start null check below (same guard
                            // as the DrawLine).
                            var z0 = ResolveBumpVizGroundZ(start);
                            float endX = 0f;
                            float endY = 0f;
                            var haveEnd = false;
                            if (t1Start != null)
                            {
                                var smhViz = taMineral.SmHarvestPoint;
                                var gdx0 = smhViz.X - t1Start.X;
                                var gdy0 = smhViz.Y - t1Start.Y;
                                var glen0 = (float)Math.Sqrt(gdx0 * gdx0 + gdy0 * gdy0);
                                if (glen0 > 0.0001f)
                                {
                                    endX = smhViz.X + gdx0 / glen0 * ChoreoExtendUnits;
                                    endY = smhViz.Y + gdy0 / glen0 * ChoreoExtendUnits;
                                    haveEnd = true;
                                }
                            }

                            var green = new Color { R = 0, G = 255, B = 0 };
                            var purple = new Color { R = 200, G = 0, B = 200 };

                            if (t1Start != null && haveEnd)
                            {
                                ManagerDebugService.DrawLine(
                                    new Point { X = t1Start.X, Y = t1Start.Y, Z = z0 },
                                    new Point { X = endX, Y = endY, Z = z0 },
                                    green);
                            }

                            if (t5Start != null && saMineralViz != null)
                            {
                                // T5's static purple line now matches its book:
                                // a plain move TO Mineral SA (its SM point) —
                                // owner spec 2026-10-27 09:18.
                                var saPoint = HasNonZeroPoint(saMineralViz.SmHarvestPoint) ? saMineralViz.SmHarvestPoint
                                    : HasNonZeroPoint(saMineralViz.HarvestPoint) ? saMineralViz.HarvestPoint
                                    : saMineralViz.Position;
                                if (saPoint != null)
                                {
                                    ManagerDebugService.DrawLine(
                                        new Point { X = t5Start.X, Y = t5Start.Y, Z = z0 },
                                        new Point { X = saPoint.X, Y = saPoint.Y, Z = z0 },
                                        purple);
                                }
                            }
                        }

                        if (assignment.Workers != null)
                        {
                            foreach (var worker in assignment.Workers)
                            {
                                if (worker?.Position == null) continue;
                                var z = worker.Position.Z;
                                ManagerDebugService.DrawLine(
                                    new Point { X = worker.Position.X - BumpVizCrossHalf, Y = worker.Position.Y, Z = z },
                                    new Point { X = worker.Position.X + BumpVizCrossHalf, Y = worker.Position.Y, Z = z },
                                    white);
                                ManagerDebugService.DrawLine(
                                    new Point { X = worker.Position.X, Y = worker.Position.Y - BumpVizCrossHalf, Z = z },
                                    new Point { X = worker.Position.X, Y = worker.Position.Y + BumpVizCrossHalf, Z = z },
                                    white);
                            }
                        }
                    }
                }
            }

            // ── Owner rules 2026-10-29, rules 2+3 verification overlay ──
            // (a) WHITE cross hatches at the NATURAL-test SM Harvest centroids for
            //     TA and SA (both starts) so the rule-2 check is visible at frame
            //     0: does the dat SMH (circle 1 / SMH line) match where workers
            //     actually stood? Measured: TA off by 0.46u (SE) / 0.58u (NW),
            //     SA within 0.03u (SE) / 0.16u (NW).
            // (b) PROPOSED rule-3 TB pair in TB color — NOT yet persisted to the
            //     dat. Current dat TB segment clips TA's bulge (clearance 1.26u
            //     vs bulge max 1.38u toward TB). Proposal rotates TB SMH ~8-9 deg
            //     on the stand circle for 1.42u clearance; SMR = closest point on
            //     the hatchery circle 1. Leg cost +0.04u vs the dat pair.
            if (_mapData.TeamPatchAssignments != null)
            {
                for (var start = 0; start < _mapData.TeamPatchAssignments.Count; start++)
                {
                    var hatch = _mapData.StartingTownHall != null && start < _mapData.StartingTownHall.Length
                        ? _mapData.StartingTownHall[start]
                        : null;
                    if (hatch == null) continue;
                    var isSe = hatch.X > 100f;
                    var gz = ResolveBumpVizGroundZ(start);
                    float MineralZOf(string label)
                    {
                        var m = _mapData.TeamPatchAssignments[start]?
                            .SelectMany(a => a?.Minerals ?? Enumerable.Empty<OrderedMineral>())
                            .FirstOrDefault(mm => mm?.Position != null
                                && (string.Equals(mm.FinalLabel, label, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(mm.Label, label, StringComparison.OrdinalIgnoreCase)));
                        return m?.Position?.Z ?? gz;
                    }

                    var natTa = isSe ? (127.476f, 19.167f) : (24.451f, 132.891f);
                    var natSa = isSe ? (129.705f, 19.308f) : (22.398f, 132.750f);
                    DrawBumpVizHatch(natTa.Item1, natTa.Item2, MineralZOf("TA"), white);
                    DrawBumpVizHatch(natSa.Item1, natSa.Item2, MineralZOf("SA"), white);

                    var propSmh = isSe ? (128.469f, 18.296f) : (23.529f, 133.706f);
                    var propSmr = isSe ? (127.948f, 21.635f) : (24.052f, 130.365f);
                    var tbColor = ProcessVisableUnits.GetFinalLabelColor("TB");
                    ManagerDebugService.DrawLine(
                        new Point { X = propSmr.Item1, Y = propSmr.Item2, Z = gz },
                        new Point { X = propSmh.Item1, Y = propSmh.Item2, Z = MineralZOf("TB") },
                        tbColor);
                    DrawBumpVizHatch(propSmh.Item1, propSmh.Item2, MineralZOf("TB"), tbColor);
                    DrawBumpVizHatch(propSmr.Item1, propSmr.Item2, gz, tbColor);
                }
            }

            // Live "current position -> current move target" lines for the three
            // choreo workers (owner request 2026-10-27 07:13): T1 TEAL, T4 WHITE
            // when its latched command is a move / RED when mineral walking,
            // T5 PURPLE. Each line is redrawn FROM the worker's live position
            // every frame (the target latches in LogMiningCommands on every
            // issued command), with a 0.2u cross hatch on the move-to
            // coordinate. The static green/purple frame-0 lines above are
            // unchanged.
            var choreoStartIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var vizGroundZ = ResolveBumpVizGroundZ(choreoStartIndex);
            var teal = new Color { R = 0, G = 200, B = 200 };
            var vizPurple = new Color { R = 200, G = 0, B = 200 };
            DrawBumpVizLiveTarget("T1", teal, vizGroundZ);
            DrawBumpVizLiveTarget("T4", null, vizGroundZ);
            DrawBumpVizLiveTarget("T5", vizPurple, vizGroundZ);

            // Telemetry circles: radius 1 = distance from the worker's frame-0 start
            // at frame 5, radius 2 = frame 10, radius 3 = frame 15 (owner spec
            // 2026-10-26 06:24), centered on the FRAME-0 CAPTURED position. The
            // persisted worker.Position is refreshed live by the label tracker,
            // so it cannot be used as the center — snaps[0] is pinned at capture.
            // T1 rings in TA teal, T4 rings in a darker teal (TB shares TA's teal
            // in the label color table — darkened so the two ring sets never merge
            // on screen), T5 rings in SA purple.
            foreach (var vizWorker in vizWorkers)
            {
                var color = vizWorker.Label == "T1"
                    ? new Color { R = 0, G = 255, B = 255 }
                    : vizWorker.Label == "T4"
                        ? new Color { R = 0, G = 140, B = 140 }
                        : new Color { R = 128, G = 0, B = 128 };
                if (!_bumpVizSnapshots.TryGetValue(vizWorker.Label, out var snaps)) continue;
                if (!snaps.TryGetValue(0, out var startPos) || startPos == null) continue;
                foreach (var milestone in BumpVizDrawFrames)
                {
                    if (!snaps.TryGetValue(milestone, out var pos) || pos == null) continue;
                    DrawDebugCircle(startPos, (float)Math.Sqrt(DistanceSquared(startPos, pos)), color, startPos.Z);
                }
            }
        }

        // 0.2u axis-aligned cross hatch at a fixed map point (ground z — never
        // floating), used for the natural-test SMH markers and the proposed
        // rule-3 TB pair.
        private void DrawBumpVizHatch(float x, float y, float z, Color color, float half = 0.1f)
        {
            ManagerDebugService.DrawLine(
                new Point { X = x - half, Y = y, Z = z },
                new Point { X = x + half, Y = y, Z = z },
                color);
            ManagerDebugService.DrawLine(
                new Point { X = x, Y = y - half, Z = z },
                new Point { X = x, Y = y + half, Z = z },
                color);
        }

        // Owner request 2026-10-27 07:13 — one live line per choreo worker from
        // its CURRENT position to its last-issued move/gather target, with a
        // 0.2u cross hatch on the move-to coordinate. fixedColor null = T4's
        // white-for-move / red-for-mineral-walk rule from the latched command.
        private void DrawBumpVizLiveTarget(string role, Color? fixedColor, float z)
        {
            if (!_bumpVizLiveTargets.TryGetValue(role, out var latched))
            {
                return;
            }

            var live = LivePositionOfChoreoRole(role);
            if (live == null)
            {
                return;
            }

            var color = fixedColor
                ?? (latched.IsWalk
                    ? new Color { R = 255, G = 0, B = 0 }
                    : new Color { R = 255, G = 255, B = 255 });
            ManagerDebugService.DrawLine(
                new Point { X = live.X, Y = live.Y, Z = z },
                new Point { X = latched.X, Y = latched.Y, Z = z },
                color);
            const float hatchHalf = 0.1f;
            ManagerDebugService.DrawLine(
                new Point { X = latched.X - hatchHalf, Y = latched.Y - hatchHalf, Z = z },
                new Point { X = latched.X + hatchHalf, Y = latched.Y + hatchHalf, Z = z },
                color);
            ManagerDebugService.DrawLine(
                new Point { X = latched.X - hatchHalf, Y = latched.Y + hatchHalf, Z = z },
                new Point { X = latched.X + hatchHalf, Y = latched.Y - hatchHalf, Z = z },
                color);
        }

        // Live position of any observed unit (mineral first, then own units) —
        // used to latch gather/smart targets, which carry a unit tag and no
        // world-space point.
        private static Vector2Dto LivePositionOfUnitTag(ulong unitTag)
        {
            if (unitTag == 0)
            {
                return null;
            }

            var snapshot = Globals.CurrentObservation;
            if (snapshot == null)
            {
                return null;
            }

            if (snapshot.Minerals != null && snapshot.Minerals.TryGetValue(unitTag, out var mineral) && mineral?.Position != null)
            {
                return mineral.Position;
            }

            if (snapshot.SelfUnits != null && snapshot.SelfUnits.TryGetValue(unitTag, out var unit) && unit?.Position != null)
            {
                return unit.Position;
            }

            return null;
        }

        // The LIVE position of the mineral unit nearest a persisted mineral
        // position (search radius 2u). The persisted HarvestPoint/Position can
        // sit ~0.9u off the real unit — the owner screenshot (2026-10-27)
        // shows the green line anchored on the dat point running BETWEEN TA
        // and the neighboring patch. Anchoring on the observed unit keeps the
        // line through the mineral itself.
        private static Vector2Dto LiveMineralPositionNear(Vector2Dto datPosition)
        {
            var minerals = Globals.CurrentObservation?.Minerals;
            if (minerals == null || datPosition == null)
            {
                return null;
            }

            Vector2Dto best = null;
            var bestDistanceSquared = 4f; // 2u search radius
            foreach (var mineral in minerals.Values)
            {
                if (mineral?.Position == null)
                {
                    continue;
                }

                var d2 = DistanceSquared(mineral.Position, datPosition);
                if (d2 < bestDistanceSquared)
                {
                    bestDistanceSquared = d2;
                    best = mineral.Position;
                }
            }

            return best;
        }

        // The three measured workers (T1/T4/T5) for the CURRENT start, resolved
        // from the persisted patch assignment — FinalLabel for identity, UnitTag
        // for live matching, Position as the frame-0 circle center.
        private List<(string Label, ulong Tag, Vector2Dto Start)> ResolveBumpVizWorkers()
        {
            var result = new List<(string, ulong, Vector2Dto)>();
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignments = _mapData?.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            if (assignments == null) return result;
            foreach (var assignment in assignments)
            {
                foreach (var worker in assignment?.Workers ?? Enumerable.Empty<WorkerEntryDto>())
                {
                    if (worker?.Position == null || worker.UnitTag == 0) continue;
                    var label = worker.FinalLabel ?? worker.Label ?? string.Empty;
                    if (label == "T1" || label == "T4" || label == "T5")
                    {
                        result.Add((label, worker.UnitTag, worker.Position));
                    }
                }
            }
            return result;
        }

        private float ResolveBumpVizGroundZ(int startIndex)
        {
            var snapshot = Globals.CurrentObservation;
            if (snapshot?.CurrentTownHalls != null)
            {
                foreach (var townHall in snapshot.CurrentTownHalls.Values)
                {
                    if (townHall?.Position != null) return townHall.Position.Z;
                }
            }
            var mineral = _mapData?.TeamPatchAssignments?
                .ElementAtOrDefault(startIndex)?
                .SelectMany(assignment => assignment?.Minerals ?? Enumerable.Empty<OrderedMineral>())
                .FirstOrDefault(m => m?.Position != null);
            return mineral?.Position?.Z ?? 12f;
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
            EnsureInstructionTraceFile();
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
                // --naturaltest (owner spec 2026-10-28 14:28 "Do not give workers
                // instructions for this test"): collision detours are worker
                // instructions too — suppress the whole intervention so the
                // engine resolves collisions naturally.
                if (Settings.WorkerCollisionAvoidance
                    && !Settings.NaturalObservationMode
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

                    // Absolute frame floor (owner spec 2026-10-22, jitRMFineTune):
                    // a command row carrying RequireFrame may not execute before the
                    // ABSOLUTE game frame — frame 97 is the earliest the fine-tune
                    // return may issue, not a trigger. Once the floor passes the
                    // row's normal gates govern (the return stays position-gated).
                    // Wait rows keep their existing RequireFrame handling in the
                    // control-row pass.
                    if (instruction.RequireFrame >= 0
                        && instruction.Command != WorkerInstructionCommand.Wait
                        && _currentFrame < instruction.RequireFrame)
                    {
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

                    // A Move/Stop whose point cannot resolve yet is WAITING, not
                    // failing: a frame-gated chase row (BumpChasePartner) is
                    // frame-satisfied from its relative frame onward, so without
                    // this break it attempted execution every frame before the
                    // trigger fired, logged a no-action failure each frame, and
                    // never consumed (bumptest_chase2_run1: Y4/B2 spammed
                    // [WORKER INSTRUCTION FAILURE] on every frame 0-260+). The
                    // trigger-gated resolver returning null is the same state as
                    // an unsatisfied gate — wait silently; the row executes the
                    // frame the point resolves.
                    if (targetPoint == null
                        && (instruction.Command == WorkerInstructionCommand.Move
                            || instruction.Command == WorkerInstructionCommand.Stop))
                    {
                        break;
                    }

                    var positionSatisfied = targetPoint != null
                        && DistanceSquared(runtimeWorker.Position, targetPoint) <= instruction.PositionTolerance * instruction.PositionTolerance;
                    // NoCondition rows are choreography (mineral walks, build morphs,
                    // store/load rows) and pass every gate — EXCEPT an explicitly set
                    // RelativeFrame: a frame-gated NoCondition row (the mineral-walk
                    // stage startup, owner spec 2026-10-17 20:46) must hold until its
                    // frame so the walk starts on schedule while still bypassing the
                    // position/ability/gather-send gates.
                    if (instruction.NoCondition && instruction.RelativeFrame < 0)
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
                            // Missed-pickup skip (v15 NW/SE diagnosis 2026-10-26): the
                            // pickup flip (1183->1184) can happen while the book is
                            // suspended mid collision-detour — T1's detour Smart on a
                            // free TA turned into a real harvest (pickup f174) during
                            // the f103-203 detour suspension; when the book resumed it
                            // re-issued the gather row while the drone was already
                            // CARRYING, the Smart fizzled into [16] and dropped, and
                            // the drone sat orderless at this wait with the mineral
                            // buff still on. The 1183->1184 flip it waits for can never
                            // be observed again (CurrentAbilityId is -1, buff present =
                            // the pickup demonstrably already happened), and the
                            // !IsCarrying gate in the heal below can never pass. Skip
                            // the wait exactly as a natural pass would (Mti advance +
                            // index + start frame) so the return rows behind it issue
                            // the deposit. Narrow gates: this exact wait shape, no
                            // orders, carrying — a healthy harvesting drone always has
                            // a 1183/1184 order, and the buff is gone by the deposit
                            // frame, so this cannot fire mid-cycle.
                            if (instruction.Command == WorkerInstructionCommand.Wait
                                && instruction.PreviousAbilityId == (int)Abilities.HARVEST_GATHER_DRONE
                                && instruction.TargetAbilityId == (int)Abilities.HARVEST_RETURN_DRONE
                                && runtimeWorker.CurrentAbilityId < 0
                                && runtimeWorker.IsCarrying
                                && _currentFrame - GetHarvestWaitHealLastFrame(runtimeWorker.UnitTag) >= HarvestWaitHealCooldownFrames)
                            {
                                if (instruction.NextTargetIndex >= 0 && instruction.NextTargetIndex < assignedWorker.MiningTargets.Count)
                                {
                                    assignedWorker.Mti = instruction.NextTargetIndex;
                                }
                                Console.WriteLine($"[HARVEST WAIT MISSED-PICKUP SKIP] frame={_currentFrame} worker={runtimeWorker.UnitTag} role={assignedWorker.Role} idx={runtimeWorker.CurInstrIdx}");
                                _harvestWaitHealLastFrame[runtimeWorker.UnitTag] = _currentFrame;
                                runtimeWorker.CurInstrIdx++;
                                runtimeWorker.InstructionStartFrame = Settings.GetRelativeFrame(_currentFrame);
                                break;
                            }
                            // Harvest-wait deadlock heal (owner report 2026-10-25
                            // 21:36 "B2 stops mining"): an unqueued footprint Smart
                            // can fizzle into a bare [16] move that completes AT the
                            // CCA wait point and drops — v13-NW run, B2's f155 SMART
                            // on free BB produced [16], the drone went orderless at
                            // f163, and this 1183->1184 wait blocked forever (B2
                            // never mined again, never built the pool). S2 survives
                            // the same fizzles only because collision detours keep
                            // re-firing its gather row until one Smart sticks. Heal:
                            // while parked at the wait with no orders and no cargo,
                            // re-arm the gather as a QUEUED HARVEST_GATHER (the
                            // frame-22 startup shape that always engages on arrival),
                            // gated on the mineral being ACTUALLY free so the healed
                            // drone never becomes the standing 1183 waiter on a busy
                            // footprint. The book stays on the wait row; the queued
                            // gather engages 1183->1184 and the row passes normally.
                            if (instruction.Command == WorkerInstructionCommand.Wait
                                && instruction.PreviousAbilityId == (int)Abilities.HARVEST_GATHER_DRONE
                                && instruction.TargetAbilityId == (int)Abilities.HARVEST_RETURN_DRONE
                                && runtimeWorker.CurrentAbilityId < 0
                                && !runtimeWorker.IsCarrying
                                && _currentFrame - GetHarvestWaitHealLastFrame(runtimeWorker.UnitTag) >= HarvestWaitHealCooldownFrames)
                            {
                                var healMineralTag = 0ul;
                                for (var back = runtimeWorker.CurInstrIdx - 1;
                                     back >= 0 && back >= runtimeWorker.CurInstrIdx - 8;
                                     back--)
                                {
                                    var backRow = runtimeWorker.Instructions[back];
                                    if (backRow?.Command == WorkerInstructionCommand.Gather && backRow.TargetId != 0)
                                    {
                                        healMineralTag = backRow.TargetId;
                                        break;
                                    }
                                }

                                if (healMineralTag != 0
                                    && IsHarvestReleaseReady(healMineralTag, freeOnly: true))
                                {
                                    var healAction = CreateInstructionGatherAction(
                                        runtimeWorker.UnitTag,
                                        healMineralTag,
                                        queued: true,
                                        (int)Abilities.HARVEST_GATHER);
                                    if (healAction != null)
                                    {
                                        actions.Add(healAction);
                                        contexts.Add(new InstructionCommandContext
                                        {
                                            WorkerTag = runtimeWorker.UnitTag,
                                            InstructionSet = "heal",
                                            InstructionIndex = runtimeWorker.CurInstrIdx,
                                            InstructionCommand = "HarvestWaitHeal",
                                            InstructionPoint = string.Empty,
                                            TargetPointReference = string.Empty
                                        });
                                        _harvestWaitHealLastFrame[runtimeWorker.UnitTag] = _currentFrame;
                                        Console.WriteLine($"[HARVEST WAIT HEAL] frame={_currentFrame} worker={runtimeWorker.UnitTag} role={assignedWorker.Role} mineral={healMineralTag} curInstrIdx={runtimeWorker.CurInstrIdx}");
                                    }
                                }
                            }
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
                    else if (string.Equals(instruction.InstructionSet, "jitRMFineTune", StringComparison.OrdinalIgnoreCase)
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
            // are T4 (bumps T1) and Y4 (bumps Y1); the 2026-10-19 chase-bump
            // choreography adds T5 (chases T1) and B2 (chases Y1) — with the
            // 2026-10-21 W12/W10 label swap the Y5-keyed chase is run by W12 and
            // B2 (W10) runs the BB mineral walk; experiment 1 bumps with T3/Y3.
            string partnerRole;
            if (Settings.IsMagannatha5x5Rotation)
            {
                partnerRole = string.Equals(assignedWorker.Role, "T4", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : string.Equals(assignedWorker.Role, "T5", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : string.Equals(assignedWorker.Role, "Y4", StringComparison.OrdinalIgnoreCase) ? "Y1"
                    : string.Equals(assignedWorker.Role, "Y5", StringComparison.OrdinalIgnoreCase) ? "Y1"
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
                    var invertedTarget = instruction.JumpToPreviousRow
                        ? runtimeWorker.CurInstrIdx - 1
                        : instruction.JumpToInstructionIndex;
                    if (invertedTarget < 0 || invertedTarget >= runtimeWorker.Instructions.Count)
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

                    runtimeWorker.CurInstrIdx = invertedTarget;
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
                    && IsHarvestReleaseReady(instruction.TargetId, instruction.HarvestReleaseFreeOnly, instruction.HarvestReleaseFramesOnly);
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

                // Transition-frame suppression (owner spec 2026-10-25 10:36, "issue
                // a smart when it is close enough, not move it around the mineral
                // ... not to keep issuing moves"): when the NEXT row is an
                // InvertGate jump whose gate has RESOLVED, skip this row's
                // execution — the jump passes through and the row after it fires
                // alone, so the transition frame issues one command (the rolling
                // book's Smart), not two (t1test10e f22: MOVE+SMART same frame,
                // the move won arbitration and the smart never engaged). Scope is
                // narrow: only a command row immediately followed by an
                // InvertGate jump that loops back to it, and only on the frame
                // the gate resolves — the pass advances past both rows, so the
                // skip can never repeat. For the existing bump-walk/chase books
                // the skipped row is the redundant last re-issue of the loop
                // (one fewer duplicate command on the break frame — harmless).
                if (runtimeWorker.CurInstrIdx + 1 < runtimeWorker.Instructions.Count)
                {
                    var nextRow = runtimeWorker.Instructions[runtimeWorker.CurInstrIdx + 1];
                    if (nextRow != null
                        && nextRow.Command == WorkerInstructionCommand.Jump
                        && nextRow.InvertGate)
                    {
                        var nextJumpTarget = nextRow.JumpToPreviousRow
                            ? runtimeWorker.CurInstrIdx + 1 - 1
                            : nextRow.JumpToInstructionIndex;
                        if (nextJumpTarget >= 0 && nextJumpTarget <= runtimeWorker.CurInstrIdx)
                        {
                            var gatePoint = ResolveInstructionPoint(runtimeWorker, assignedWorker, nextRow);
                            if (gatePoint != null)
                            {
                                runtimeWorker.CurInstrIdx++;
                                runtimeWorker.InstructionStartFrame = Settings.GetRelativeFrame(_currentFrame);
                                continue;
                            }
                        }
                    }
                }

                if (instruction.Command != WorkerInstructionCommand.Jump)
                {
                    return true;
                }

                var jumpTargetIndex = instruction.JumpToPreviousRow
                    ? runtimeWorker.CurInstrIdx - 1
                    : instruction.JumpToInstructionIndex;
                if (jumpTargetIndex < 0 || jumpTargetIndex >= runtimeWorker.Instructions.Count)
                {
                    ReportInstructionFailure(runtimeWorker, "jump row has invalid target index", instruction);
                    return false;
                }

                runtimeWorker.CurInstrIdx = jumpTargetIndex;
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
                    instruction.Queue,
                    instruction.AbilityIdOverride >= 0 ? instruction.AbilityIdOverride : (int)Abilities.HARVEST_GATHER),
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

            // Same for the chase-bump clearance gate: it compares the walker's live
            // position against EVERY other assigned worker's live position — none of
            // which is the row TargetId's stored mining target point.
            if (instruction.Point == WorkerInstructionPoint.BumpClearOfWorkersGate)
            {
                return ResolveBumpClearOfWorkersGatePoint(assignedWorker, instruction);
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

            // Same for the chase-bump phase-1 chase: the trigger distances, the
            // frame-15 deadline, and the chased partner's live position are all
            // live observation across assigned workers — none of it is the row
            // TargetId's stored mining target (B2's rows carry the Y1-partner A
            // mineral YA, which is not in B2's single-target book, so the
            // MiningTargets lookup below returned null before the resolver ever
            // ran — bumptest_chase2_run2: B2 never captured its start, never
            // chased, and silently mined BB instead).
            if (instruction.Point == WorkerInstructionPoint.BumpChasePartner)
            {
                return ResolveBumpChasePartnerPoint(assignedWorker);
            }

            // T1 arrival test battery (owner spec 2026-10-24 18:16, --t1test=N):
            // the star-gated chase, the follow break-off gate, the clear-of-partner
            // gate, and the star-proximity redirect all resolve from LIVE partner /
            // worker state, never the row TargetId's stored mining target (same
            // pattern as BumpChasePartner above).
            if (instruction.Point == WorkerInstructionPoint.StarGatedChase)
            {
                return ResolveStarGatedChasePoint(runtimeWorker, assignedWorker);
            }

            if (instruction.Point == WorkerInstructionPoint.ChaseBreakOffGate)
            {
                return ResolveChaseBreakOffGatePoint(runtimeWorker, assignedWorker);
            }

            if (instruction.Point == WorkerInstructionPoint.ClearOfPartnerGate)
            {
                return ResolveClearOfPartnerGatePoint(runtimeWorker, assignedWorker);
            }

            if (instruction.Point == WorkerInstructionPoint.StarRedirect)
            {
                return ResolveStarRedirectPoint(runtimeWorker, assignedWorker, instruction);
            }

            // T1 rolling-line tests (owner spec 2026-10-25 09:24, --t1test=10+):
            // the rolling waypoint and the harvest-range gate resolve from LIVE
            // worker position and the row mineral's ResourcePosition — never the
            // row TargetId's stored mining target alone (same pattern as
            // StarRedirect above).
            if (instruction.Point == WorkerInstructionPoint.RollingLine)
            {
                return ResolveRollingLinePoint(runtimeWorker, assignedWorker, instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.HarvestRangeGate)
            {
                return ResolveHarvestRangeGatePoint(runtimeWorker, assignedWorker, instruction);
            }

            // Decel-bypass battery (owner spec 2026-10-25 16:17, --t1test=18/19,
            // variant 9): the approach move and the projection-lead harvest gate
            // resolve from LIVE worker position and the row mineral's
            // HarvestPoint — same pattern as the rolling tests above.
            if (instruction.Point == WorkerInstructionPoint.DecelPlan)
            {
                return ResolveDecelPlanPoint(runtimeWorker, assignedWorker, instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.LeadHarvestGate)
            {
                return ResolveLeadHarvestGatePoint(runtimeWorker, assignedWorker, instruction);
            }

            // Variant 14 rolling return leg (owner question 2026-10-25 "can
            // this technique be applied to the return trip"): the homing
            // rolling waypoint and its gate resolve from LIVE worker position
            // and the stored pair-table ReturnPoint — same pattern as the
            // harvest legs above.
            if (instruction.Point == WorkerInstructionPoint.RollingReturnPoint)
            {
                return ResolveRollingReturnPoint(runtimeWorker, assignedWorker, instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.ReturnRangeGate)
            {
                return ResolveReturnRangeGatePoint(runtimeWorker, assignedWorker, instruction);
            }

            // Bump choreography (owner spec 2026-10-26 18:45, variant 17): the
            // green-line target, the label-keyed SM harvest / Circle 2 staging
            // points, T5's SA-intersect move, and the T1-arrival / clear-of
            // gates all resolve from persisted patch data and LIVE role
            // positions — never the row TargetId's stored mining target.
            if (instruction.Point == WorkerInstructionPoint.ChoreoGreenLine)
            {
                return ResolveChoreoGreenLinePoint(runtimeWorker, instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.ChoreoSmHarvestLabel)
            {
                return ResolveChoreoSmHarvestLabelPoint(instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.ChoreoSmLineExtend)
            {
                return ResolveChoreoSmLineExtendPoint(runtimeWorker);
            }

            if (instruction.Point == WorkerInstructionPoint.ChoreoT4AngleStart)
            {
                return ResolveChoreoT4AngleStartPoint(runtimeWorker, instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.ChoreoConvergence)
            {
                return ResolveChoreoConvergencePoint(runtimeWorker);
            }

            if (instruction.Point == WorkerInstructionPoint.ChoreoSaIntersect)
            {
                return ResolveChoreoSaIntersectPoint(runtimeWorker, instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.ChoreoWaitCircle2Label)
            {
                return ResolveChoreoWaitCircle2LabelPoint(instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.ChoreoT1ArrivalGate)
            {
                return ResolveChoreoT1ArrivalGatePoint(runtimeWorker, instruction);
            }

            if (instruction.Point == WorkerInstructionPoint.ChoreoClearOfGate)
            {
                return ResolveChoreoClearOfGatePoint(runtimeWorker, instruction);
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
                var resolvedReference = ResolveB2PoolReturnReference(assignedWorker, instruction.TargetPointReference);
                var referencedPoint = ResolvePairTablePoint(resolvedReference);
                if (referencedPoint != null)
                {
                    // Y1/Y2 return-point spread (owner spec 2026-10-17 07:44): the
                    // two yellow-side miners' chapter lists cover the SAME three
                    // pair rows ({YA,YB}, {YB,BA}, {BA,YA} — Y1 in one order, Y2
                    // in the other), so both deposit at the same point on the
                    // hatchery circle and the two returners interfere with each
                    // other (and stack into Y3's walk-out line). Rotate each
                    // one's pair ReturnPoint around the hatchery center by a
                    // small per-role angle so their deposit points separate.
                    // Harvest/wait references are untouched — only ".ReturnPoint".
                    if (resolvedReference.EndsWith(".ReturnPoint", StringComparison.OrdinalIgnoreCase))
                    {
                        var spreadDegrees = ReturnPointSpreadDegrees(assignedWorker?.Role);
                        if (spreadDegrees != 0f)
                        {
                            var spreadPoint = SpreadPointAroundTownHall(assignedWorker, referencedPoint, spreadDegrees);
                            if (spreadPoint != null)
                            {
                                return spreadPoint;
                            }
                        }
                    }
                    return referencedPoint;
                }
            }

            return instruction.Point switch
            {
                WorkerInstructionPoint.Harvest => _speedMiningActive && HasNonZeroPoint(target.SmHarvestPoint) ? target.SmHarvestPoint : target.HarvestPoint,
                WorkerInstructionPoint.SmHarvestForce => HasNonZeroPoint(target.SmHarvestPoint) ? target.SmHarvestPoint : target.HarvestPoint,
                WorkerInstructionPoint.SmMidpoint => ResolveSmMidpointPoint(target),
                WorkerInstructionPoint.StarRedirect => ResolveStarRedirectPoint(runtimeWorker, assignedWorker, instruction),
                WorkerInstructionPoint.HarvestThrough => ResolveHarvestThroughPoint(target),
                WorkerInstructionPoint.Return => SpreadReturnPoint(assignedWorker, _speedMiningActive && HasNonZeroPoint(target.SmReturnPoint) ? target.SmReturnPoint : target.ReturnPoint),
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

        // Y1/Y2 return-point spread (owner spec 2026-10-17 07:44) — see the
        // call site in ResolveInstructionPoint. Degrees are applied as a
        // rotation of the pair ReturnPoint around the hatchery center (+=
        // counterclockwise); the two miners get opposite signs so their
        // deposit points land on opposite sides of the shared pair point.
        // 8 degrees at the ~2.85u hatchery-return radius is ~0.4u of arc per
        // worker, ~0.8u between them — enough to deposit without contact.
        // Generalized 2026-10-19 (owner screenshot: V5 bounced off the H1
        // rim on the first return — the whole V team deposits at one spot):
        // the V and C teams get their own slot on the return circle, 24° per
        // slot (1.2u of arc at the return radius — owner directive "wider"
        // 2026-10-19, up from the 16°/0.8u Y-pair spacing after V5 still
        // bounced); C is half-stepped off V's grid since C2 deposits in the
        // same cluster. T and B stay at 0 — measured 2026-10-19
        // (bumptest_chase2_sprd_run1: 349 vs 339.7): spreading T pushes its
        // deposit off its own mineral line and costs ~9 frames a trip, and
        // B2's pool preposition rides a ".ReturnPoint" reference that must
        // stay exact. Y keeps only the owner-spec 2026-10-17 Y1/Y2 pair.
        private const float Y1ReturnSpreadDegrees = 8f;
        private const float Y2ReturnSpreadDegrees = -8f;

        private static float ReturnPointSpreadDegrees(string role)
        {
            if (string.IsNullOrEmpty(role) || role.Length < 2)
            {
                return 0f;
            }

            var prefix = char.ToUpperInvariant(role[0]);
            if (!int.TryParse(role.Substring(1), out var slot) || slot < 1)
            {
                return 0f;
            }

            switch (prefix)
            {
                case 'V':
                    return (slot - 1) * 24f - 48f;
                case 'C':
                    return (slot - 1) * 24f - 48f + 12f;
                case 'Y':
                    // Y1/Y2 keep the owner-spec 2026-10-17 values exactly.
                    if (slot == 1)
                    {
                        return Y1ReturnSpreadDegrees;
                    }

                    if (slot == 2)
                    {
                        return Y2ReturnSpreadDegrees;
                    }

                    return 0f;
                default:
                    return 0f;
            }
        }

        // Applies the per-role return spread to any resolved return point
        // (reference rows, plain Return rows, and the T1 first-return
        // interrupt). Falls back to the unsprayed point whenever the town
        // hall or the spread cannot resolve so a spread glitch can never
        // stall a return row.
        private static Vector2Dto SpreadReturnPoint(AssignedWorkerDto assignedWorker, Vector2Dto point)
        {
            var spread = ReturnPointSpreadDegrees(assignedWorker?.Role);
            if (spread == 0f || point == null)
            {
                return point;
            }

            return SpreadPointAroundTownHall(assignedWorker, point, spread) ?? point;
        }

        // jitMHFineTune move-through point (owner spec 2026-10-22): the SM
        // harvest point mirrored across the mineral center, so the frame-20
        // walk passes THROUGH the mineral footprint at full speed instead of
        // decelerating at the standoff (the telemetry sit point sat ~0.2u
        // short of the stored point). Falls back to the plain harvest point
        // when the mineral center is unknown so the row can never deadlock.
        private Vector2Dto? ResolveHarvestThroughPoint(MiningTargetDto target)
        {
            if (target == null)
            {
                return null;
            }

            var harvest = _speedMiningActive && HasNonZeroPoint(target.SmHarvestPoint)
                ? target.SmHarvestPoint
                : target.HarvestPoint;
            if (!HasNonZeroPoint(harvest))
            {
                return null;
            }

            var center = target.ResourcePosition;
            if (!HasNonZeroPoint(center))
            {
                return harvest;
            }

            return new Vector2Dto(
                2f * center.X - harvest.X,
                2f * center.Y - harvest.Y,
                center.Z);
        }

        // Rotates a point around the worker's town hall center by the given
        // angle, preserving its distance from the center (the hatchery-return
        // radius). Null when no town hall is observable or the point sits on
        // the center.
        private static Vector2Dto SpreadPointAroundTownHall(AssignedWorkerDto assignedWorker, Vector2Dto point, float degrees)
        {
            var townhall = ResolveWorkerTownHallPosition(assignedWorker);
            if (townhall == null || point == null || degrees == 0f)
            {
                return null;
            }

            var dx = point.X - townhall.X;
            var dy = point.Y - townhall.Y;
            var radius = MathF.Sqrt(dx * dx + dy * dy);
            if (radius < 0.001f)
            {
                return null;
            }

            var angle = MathF.Atan2(dy, dx) + degrees * MathF.PI / 180f;
            return new Vector2Dto(
                townhall.X + MathF.Cos(angle) * radius,
                townhall.Y + MathF.Sin(angle) * radius,
                point.Z);
        }

        // Same town-hall resolution the collision service uses: prefer the
        // town hall recorded on the worker's mining targets, fall back to the
        // first observed one (single-hatchery 5x5 experiment).
        private static Vector2Dto ResolveWorkerTownHallPosition(AssignedWorkerDto assignedWorker)
        {
            var townhalls = Globals.CurrentObservation?.CurrentTownHalls;
            if (townhalls == null || townhalls.Count == 0)
            {
                return null;
            }

            var townHallUnitId = assignedWorker?.MiningTargets
                ?.Select(target => target?.TownHallUnitId ?? 0)
                .FirstOrDefault(id => id != 0)
                ?? 0;
            if (townHallUnitId != 0
                && townhalls.TryGetValue(townHallUnitId, out var townHall)
                && townHall?.Position != null)
            {
                return townHall.Position;
            }

            foreach (var candidate in townhalls.Values)
            {
                if (candidate?.Position != null)
                {
                    return candidate.Position;
                }
            }

            return null;
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

            // Bump angle sweep knob (owner spec 2026-10-18): rotate the bumper stand
            // point around the A-mineral SM harvest point by Settings.BumpAngleDegrees.
            // 0 = straight-on (the pre-sweep geometry, T1's direct walk to its harvest
            // point). Positive and negative angles sweep either side of the line; the
            // sweep starts severe and reduces toward 0, measuring T1/Y1 arrival each run.
            var angleRad = Settings.BumpAngleDegrees * MathF.PI / 180f;
            if (MathF.Abs(angleRad) > 0.0005f)
            {
                var cos = MathF.Cos(angleRad);
                var sin = MathF.Sin(angleRad);
                var vx = bumperX - smHarvest.X;
                var vy = bumperY - smHarvest.Y;
                bumperX = smHarvest.X + vx * cos - vy * sin;
                bumperY = smHarvest.Y + vx * sin + vy * cos;
            }

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
                // 5x5 Version 2 bump roles were T4 (bumps T1) and Y4 (bumps Y1); the
                // 2026-10-19 chase-bump choreography adds T5 (chases T1) and B2
                // (chases Y1) — with the 2026-10-21 W12/W10 label swap the
                // Y5-keyed chase is run by W12 and B2 (W10) runs the BB walk.
                partnerRole = workerRole.Equals("T4", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : workerRole.Equals("T5", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : workerRole.Equals("Y4", StringComparison.OrdinalIgnoreCase) ? "Y1"
                    : workerRole.Equals("Y5", StringComparison.OrdinalIgnoreCase) ? "Y1"
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

        // Chase-bump clearance gate (owner spec 2026-10-19): resolves to the walker's
        // own position once it is BumpClearGateRadius (0.4u) clear of EVERY role-1/2/3
        // worker — "when they are 0.4u or more away from any Role 1,2,3 worker, then
        // they may move to the staging area of their target mineral". The four chase-
        // bump walkers (T4/T5/Y4/Y5) are never blockers: they share the central SA
        // mineral and would otherwise gate each other forever (Y5's label ends in 5,
        // but here it is the Y-line pusher, not a role-5 swing miner — with the
        // W12/W10 label swap W12 runs this chase under the Y5 label). Null while any
        // role-1/2/3 worker is inside the radius, so the inverted-gate jump keeps re-
        // issuing the central-SA mineral walk. Timeout fail-open (logged) so a
        // permanently crowded SA can never park the walker on the gather forever; the
        // hold age is tracked per worker because the inverted-gate jump resets
        // InstructionStartFrame every loop pass. The phase-1 chase captures are
        // deliberately NOT released here: a backstop's chase trigger reads the
        // bumper's captured start, which must survive the bumper finishing the
        // choreography first (bumptest_chase2_run2: B2 was collision-detoured until
        // frame 68 while Y4 finished at 16 — releasing Y4's capture then would have
        // deadlocked B2's trigger). The dictionaries hold one small entry per worker
        // for the game duration.
        private Vector2Dto? ResolveBumpClearOfWorkersGatePoint(AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if (!Settings.IsMagannatha5x5Rotation || assignedWorker == null)
            {
                return null;
            }

            var selfPosition = HasNonZeroPoint(assignedWorker.CurrentXY) ? assignedWorker.CurrentXY : null;
            if (selfPosition == null)
            {
                return null;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();

            // Owner directive 2026-10-19 23:19 ("Four starting worker must wait
            // for the initial workers to finish harvesting"): the walker holds
            // on the central SA mineral until its chased partner (T4/T5: T1,
            // Y4/Y5: Y1) has FINISHED its first harvest — observed as carrying
            // or already returning. The partner's live state comes from the
            // runtime worker entry (Settings.RuntimeWorkers): the
            // AssignedWorkerDto's IsCarrying/CurrentAbilityId are onboarding
            // values and are never refreshed (only CurrentXY is, line ~1351).
            // The helpers mine nothing during the first trip; the earlier Y1
            // return and the freed mineral lines are the point. Absolute
            // fail-open at BumpFirstHarvestWaitMaxFrame so a broken partner
            // observation can never park the walker forever.
            var partnerRole = assignedWorker.Role != null && assignedWorker.Role.StartsWith("T", StringComparison.OrdinalIgnoreCase)
                ? "T1"
                : "Y1";
            var partner = assignedWorkers.FirstOrDefault(worker =>
                string.Equals(worker?.Role, partnerRole, StringComparison.OrdinalIgnoreCase));
            var partnerHarvestDone = partner != null
                && Settings.RuntimeWorkers.TryGetValue(partner.UnitID, out var partnerRuntime)
                && (partnerRuntime.IsCarrying
                    || partnerRuntime.CurrentAbilityId == (int)Abilities.HARVEST_RETURN_DRONE);
            if (partner != null && !partnerHarvestDone && _currentFrame <= BumpFirstHarvestWaitMaxFrame)
            {
                if (_bumpFirstHarvestHoldLogged.Add(assignedWorker.UnitID))
                {
                    Console.WriteLine($"[BUMP FIRST-HARVEST WAIT] frame={_currentFrame} worker={assignedWorker.UnitID} role={assignedWorker.Role} partner={partnerRole} partnerObserved={Settings.RuntimeWorkers.ContainsKey(partner.UnitID)}");
                }

                return null;
            }

            var radiusSquared = BumpClearGateRadius * BumpClearGateRadius;
            foreach (var other in assignedWorkers)
            {
                if (other == null || other.UnitID == assignedWorker.UnitID)
                {
                    continue;
                }

                // Only role-1/2/3 miners can hold a walker on the central mineral.
                if (!IsChaseBumpRole123Miner(other.Role))
                {
                    continue;
                }

                if (other.CurrentXY != null
                    && HasNonZeroPoint(other.CurrentXY)
                    && DistanceSquared(selfPosition, other.CurrentXY) < radiusSquared)
                {
                    if (!_bumpClearGateHoldStartFrames.TryGetValue(assignedWorker.UnitID, out var holdStartFrame))
                    {
                        _bumpClearGateHoldStartFrames[assignedWorker.UnitID] = _currentFrame;
                    }
                    else if (_currentFrame - holdStartFrame > BumpClearGateTimeoutFrames)
                    {
                        _bumpClearGateHoldStartFrames.Remove(assignedWorker.UnitID);
                        Console.WriteLine($"[BUMP CLEAR GATE TIMEOUT] frame={_currentFrame} worker={assignedWorker.UnitID} role={assignedWorker.Role} walkTag={instruction?.TargetId ?? 0}");
                        return selfPosition;
                    }

                    return null;
                }
            }

            _bumpClearGateHoldStartFrames.Remove(assignedWorker.UnitID);
            return selfPosition;
        }

        // Role-1/2/3 miner predicate for the chase-bump clearance gate: exactly
        // T1/T2/T3/Y1/Y2/Y3. The chase-bump walkers (T4/T5/Y4/Y5) are excluded by
        // construction — see ResolveBumpClearOfWorkersGatePoint.
        private static bool IsChaseBumpRole123Miner(string role)
        {
            return !string.IsNullOrWhiteSpace(role)
                && (role.Equals("T1", StringComparison.OrdinalIgnoreCase)
                    || role.Equals("T2", StringComparison.OrdinalIgnoreCase)
                    || role.Equals("T3", StringComparison.OrdinalIgnoreCase)
                    || role.Equals("Y1", StringComparison.OrdinalIgnoreCase)
                    || role.Equals("Y2", StringComparison.OrdinalIgnoreCase)
                    || role.Equals("Y3", StringComparison.OrdinalIgnoreCase));
        }

        // Chase-bump follow target — NEW FRAME SCHEDULE (owner spec 2026-10-20
        // "South New Worker Order", REPLACES the 2026-10-19 distance triggers:
        // "I don't think it was working as intended"). Resolves to the chased
        // partner's LIVE position (T4/T5: T1, Y4/Y5: Y1) — "every frame the Bump
        // teams move to either Y1 or T1 current position depending on team". The
        // chase Move rows are purely frame-gated (relative frames 6-18); this
        // resolver only:
        //   T4/T5 — returns T1's live position (null only while T1's position is
        //           unobserved).
        //   Y4/Y5 — "Mineral Walk to SB until they are clear of any workers":
        //           returns null while ANY role-1/2/3 worker (T1/T2/T3/Y1/Y2/Y3)
        //           is within BumpClearGateRadius (0.4u) of the walker, so the
        //           frame-gated chase row waits silently and the SB walk gather
        //           keeps carrying it; returns Y1's live position once clear.
        //           Deadline fail-open at relative frame BumpChaseMaxRelativeFrame
        //           (20) so a permanently crowded corridor can never park the
        //           walker out of the frame-19 SB walk row behind the chase rows.
        //           The set-start frame is captured on first evaluation because
        //           control-row passes reset InstructionStartFrame.
        private Vector2Dto? ResolveBumpChasePartnerPoint(AssignedWorkerDto assignedWorker)
        {
            if (!Settings.IsMagannatha5x5Rotation || assignedWorker == null)
            {
                return null;
            }

            var workerRole = assignedWorker.Role ?? string.Empty;
            string partnerRole;
            var clearGated = false;
            if (workerRole.Equals("T4", StringComparison.OrdinalIgnoreCase))
            {
                partnerRole = "T1";
            }
            else if (workerRole.Equals("T5", StringComparison.OrdinalIgnoreCase))
            {
                partnerRole = "T1";
            }
            else if (workerRole.Equals("Y4", StringComparison.OrdinalIgnoreCase))
            {
                partnerRole = "Y1";
                clearGated = true;
            }
            // W12/W10 label swap (owner spec 2026-10-21): the Y5-keyed Y1 chase is
            // run by W12 — same walk SB / chase Y1 / clear-gated hold.
            else if (workerRole.Equals("Y5", StringComparison.OrdinalIgnoreCase))
            {
                partnerRole = "Y1";
                clearGated = true;
            }
            else
            {
                return null;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();

            if (clearGated)
            {
                // Capture the set-start frame on the first evaluation so the
                // deadline is anchored regardless of InstructionStartFrame resets.
                if (!_chaseBumpSetStartFrames.TryGetValue(assignedWorker.UnitID, out _)
                    && HasNonZeroPoint(assignedWorker.CurrentXY))
                {
                    _chaseBumpSetStartFrames[assignedWorker.UnitID] = Settings.GetRelativeFrame(_currentFrame);
                }

                if (_chaseBumpSetStartFrames.TryGetValue(assignedWorker.UnitID, out var setStartFrame)
                    && Settings.GetRelativeFrame(_currentFrame) - setStartFrame > BumpChaseMaxRelativeFrame)
                {
                    if (_bumpFirstHarvestHoldLogged.Add(assignedWorker.UnitID))
                    {
                        Console.WriteLine($"[BUMP CLEAR-HOLD DEADLINE FAIL-OPEN] frame={_currentFrame} worker={assignedWorker.UnitID} role={workerRole} — still not clear of role-1/2/3 workers by relative frame {BumpChaseMaxRelativeFrame}; releasing to the frame-19 SB walk");
                    }
                }
                else if (!IsClearOfRole123Workers(assignedWorker, assignedWorkers))
                {
                    return null;
                }
            }

            // Chase the partner's live position.
            var partner = assignedWorkers.FirstOrDefault(worker =>
                string.Equals(worker?.Role, partnerRole, StringComparison.OrdinalIgnoreCase));
            if (partner?.CurrentXY == null || !HasNonZeroPoint(partner.CurrentXY))
            {
                return null;
            }

            return partner.CurrentXY;
        }

        // T1 arrival test battery (owner spec 2026-10-24 18:16, --t1test=N):
        // shared plumbing for the star-gated chase / break-off / clear gates.
        // The partner mapping mirrors ResolveBumpChasePartnerPoint (T4/T5 -> T1,
        // Y4/Y5 -> Y1). The relative-frame deadline anchor is captured on FIRST
        // evaluation per worker because control-row passes reset
        // InstructionStartFrame (same reason _chaseBumpSetStartFrames exists).
        private readonly System.Collections.Generic.Dictionary<ulong, int> _t1TestSetStartFrames = new();
        private readonly System.Collections.Generic.HashSet<ulong> _t1TestStarPassed = new();
        // Rolling-line tests (owner spec 2026-10-25 09:24): the worker's frame-0
        // position, captured on first RollingLine resolve — the origin of the
        // start->mineral line the rolling waypoint extends along.
        private readonly System.Collections.Generic.Dictionary<ulong, Vector2Dto> _t1TestRollingOrigins = new();

        private const float T1TestStarRadius = 0.2f;
        private const float T1TestBreakOffRadius = 0.5f;
        private const int T1TestStarFollowFailOpenFrame = 22;
        private const int T1TestBreakOffFailOpenFrame = 26;
        private const int T1TestClearFailOpenFrame = 19;
        private const int T1TestStarRedirectFailOpenFrame = 24;
        private const float T1TestRollingStep = 0.5f;
        private const float T1TestHarvestRangeRadius = 0.45f;
        private const int T1TestRollingHarvestFailOpenFrame = 40;

        // Bump choreography (owner spec 2026-10-26 18:45, variant 17,
        // --t1test=34/35). Starts latch on first resolve (frame 0); the T1
        // speed tracker feeds the one-frame-arrival projection.
        private readonly System.Collections.Generic.Dictionary<ulong, Vector2Dto> _choreoStartLatches = new();
        private readonly System.Collections.Generic.Dictionary<ulong, Vector2Dto> _choreoT1LastPositions = new();
        private float _choreoT1Speed = 0.1f;
        private int _choreoT1SpeedFrame = -1;

        private const float ChoreoExtendUnits = 2.0f;       // "extended out at least 2u" roll-through targets
        private const float ChoreoWaitCircleExtra = 0.4f;   // Circle 2 = harvest radius + 0.4u
        private const int ChoreoT1ArrivalEarliestFrame = 8; // roll-through passes the SM point ~f6-12; owner spec said 16 (star-dip plan)
        private const int ChoreoT1ArrivalFailOpenFrame = 30;
        private const float ChoreoClearRadius = 1.0f;       // "when T4 is clear" / "when T5 is clear of T3"
        private const int ChoreoClearFailOpenFrame = 45;
        private const float ChoreoArrivalMinSpeed = 0.15f;  // floor: one CRUISE frame (~0.15u), so a mid-approach bump can't starve the gate
        private const float ChoreoSmHarvestGateRadius = 0.2f; // T1's SMART gate: fire the Smart once T1 is within 0.2u of the TA SM harvest point
        private const float ChoreoArrivalMaxSpeed = 0.3f;

        // Decel-bypass battery (owner spec 2026-10-25 16:17, variant 9): per-
        // worker state for the DecelPlan approach. The origin is captured on
        // the first resolve (frame 0 = the start position); the past point
        // latches once (2u past the HarvestPoint at 0.7u proximity for the
        // gated miners, 0.5u past at frame 0 for T2) and is cached so the gate
        // row keeps evaluating; last position/frame feed the speed estimate
        // for the projection-lead gate.
        private readonly System.Collections.Generic.Dictionary<ulong, Vector2Dto> _decelPlanOrigins = new();
        private readonly System.Collections.Generic.Dictionary<ulong, Vector2Dto> _decelPlanPastPoints = new();
        private readonly System.Collections.Generic.Dictionary<ulong, Vector2Dto> _decelPlanLastPositions = new();
        private readonly System.Collections.Generic.Dictionary<ulong, int> _decelPlanLastFrames = new();

        private const float DecelPlanLatchRadius = 0.75f;  // ~1.5 frames before the observed ~0.58u decel onset (t12); 0.75 (not 0.7) so a lead-1 worker latches ~1 frame BEFORE the gate passes and gets the cruise (Y1 pattern, owner rerun 2026-10-25)
        private const float DecelPlanPastExtension = 2.0f; // owner spec: move 2u extended past the Harvest point
        private const float DecelPlanT2Extension = 0.5f;   // owner spec: T2's single frame-0 move is 0.5u past
        private const float LeadHarvestRangeRadius = 0.45f;
        private const int LeadHarvestFailOpenFrame = 40;
        private const float DecelPlanMinSpeed = 0.05f;
        private const float DecelPlanMaxSpeed = 0.2f;

        // Owner-named projection leads (2026-10-25 16:17): T3 1 frame, Y2 2,
        // Y3 3, S2 5; T1/Y1/B2 round out the 1-5 ladder so every lead is
        // sampled in one run ("we can learn multiple things on each test").
        private static int DecelPlanLeadForRole(string role)
        {
            if (role.Equals("T3", StringComparison.OrdinalIgnoreCase)
                || role.Equals("Y1", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (role.Equals("Y2", StringComparison.OrdinalIgnoreCase)
                || role.Equals("B2", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            if (role.Equals("Y3", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }
            if (role.Equals("T1", StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }
            if (role.Equals("S2", StringComparison.OrdinalIgnoreCase))
            {
                return 5;
            }
            return 1;
        }

        private static Vector2Dto ExtendPointPast(Vector2Dto origin, Vector2Dto anchor, float extension)
        {
            var dx = anchor.X - origin.X;
            var dy = anchor.Y - origin.Y;
            var length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.001f)
            {
                return anchor;
            }

            return new Vector2Dto(
                anchor.X + dx / length * extension,
                anchor.Y + dy / length * extension,
                anchor.Z);
        }

        private int T1TestRelativeFrame(ulong unitTag)
        {
            if (!_t1TestSetStartFrames.TryGetValue(unitTag, out var start))
            {
                start = Settings.GetRelativeFrame(_currentFrame);
                _t1TestSetStartFrames[unitTag] = start;
            }

            return Settings.GetRelativeFrame(_currentFrame) - start;
        }

        private AssignedWorkerDto FindT1TestPartner(AssignedWorkerDto assignedWorker)
        {
            var workerRole = assignedWorker?.Role ?? string.Empty;
            string partnerRole;
            if (workerRole.Equals("T4", StringComparison.OrdinalIgnoreCase)
                || workerRole.Equals("T5", StringComparison.OrdinalIgnoreCase))
            {
                partnerRole = "T1";
            }
            else if (workerRole.Equals("Y4", StringComparison.OrdinalIgnoreCase)
                || workerRole.Equals("Y5", StringComparison.OrdinalIgnoreCase))
            {
                partnerRole = "Y1";
            }
            else
            {
                return null;
            }

            var startIndex = Globals.CurrentStartIndex >= 0
                ? Globals.CurrentStartIndex
                : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();
            return assignedWorkers.FirstOrDefault(worker =>
                string.Equals(worker?.Role, partnerRole, StringComparison.OrdinalIgnoreCase));
        }

        private static MiningTargetDto T1TestPartnerFirstTarget(AssignedWorkerDto partner)
        {
            return partner?.MiningTargets?.FirstOrDefault(candidate => candidate != null);
        }

        private static Vector2Dto T1TestSmMidpoint(MiningTargetDto target)
        {
            if (target == null
                || !HasNonZeroPoint(target.SmHarvestPoint)
                || !HasNonZeroPoint(target.SmReturnPoint))
            {
                return null;
            }

            return new Vector2Dto(
                (target.SmHarvestPoint.X + target.SmReturnPoint.X) / 2f,
                (target.SmHarvestPoint.Y + target.SmReturnPoint.Y) / 2f,
                target.SmHarvestPoint.Z);
        }

        // StarGatedChase (variants 3/4): the chased partner's LIVE position, but
        // null until T1 is within 0.2u of ITS OWN TA SmMidpoint (the Green Star)
        // so T4/T5 keep mineral-walking to SA until T1 reaches the star; deadline
        // fail-open at relative frame 22 so a missed star can never park the
        // walker (the follow starts late instead of never).
        private Vector2Dto? ResolveStarGatedChasePoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker)
        {
            var partner = FindT1TestPartner(assignedWorker);
            var partnerTarget = T1TestPartnerFirstTarget(partner);
            var star = T1TestSmMidpoint(partnerTarget);
            if (star == null)
            {
                return null;
            }

            var relativeFrame = T1TestRelativeFrame(runtimeWorker.UnitTag);
            var partnerPosition = HasNonZeroPoint(partner?.CurrentXY) ? partner.CurrentXY : null;
            // One-shot latch: once T1 has reached the star the gate stays open —
            // T1 immediately leaves the 0.2u zone toward the SM harvest and an
            // unlatched gate would go null again and stall the walker.
            if (_t1TestStarPassed.Contains(runtimeWorker.UnitTag))
            {
                return partnerPosition ?? (HasNonZeroPoint(partnerTarget.SmHarvestPoint) ? partnerTarget.SmHarvestPoint : null);
            }

            var starReached = partnerPosition != null
                && DistanceSquared(partnerPosition, star) <= T1TestStarRadius * T1TestStarRadius;
            if (starReached)
            {
                _t1TestStarPassed.Add(runtimeWorker.UnitTag);
            }
            if (!starReached && relativeFrame < T1TestStarFollowFailOpenFrame)
            {
                return null;
            }

            return partnerPosition ?? (HasNonZeroPoint(partnerTarget.SmHarvestPoint) ? partnerTarget.SmHarvestPoint : null);
        }

        // ChaseBreakOffGate (variants 2/3/4, on an InvertGate Jump): resolves
        // (non-null) once the FOLLOWER is within 0.5u of the partner's SmHarvest
        // point — following closer would shove the partner off the mineral (owner
        // spec 2026-10-24 18:16) — OR at the relative frame 26 deadline; null
        // while the follow should continue, so the jump keeps re-chasing.
        private Vector2Dto? ResolveChaseBreakOffGatePoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker)
        {
            if (T1TestRelativeFrame(runtimeWorker.UnitTag) >= T1TestBreakOffFailOpenFrame)
            {
                return HasNonZeroPoint(runtimeWorker.Position) ? runtimeWorker.Position : new Vector2Dto(0.01f, 0.01f, 0f);
            }

            var partnerTarget = T1TestPartnerFirstTarget(FindT1TestPartner(assignedWorker));
            if (partnerTarget == null || !HasNonZeroPoint(partnerTarget.SmHarvestPoint))
            {
                return null;
            }

            if (HasNonZeroPoint(runtimeWorker.Position)
                && DistanceSquared(runtimeWorker.Position, partnerTarget.SmHarvestPoint) <= T1TestBreakOffRadius * T1TestBreakOffRadius)
            {
                return runtimeWorker.Position;
            }

            return null;
        }

        // ClearOfPartnerGate (variant 0, on an InvertGate Jump): resolves once
        // the walker is >= 0.4u clear of the partner (T1) — "mineral walk until
        // they are clear of T1's path, no bumping" — OR at the relative frame 19
        // deadline; null while T1 is inside the radius, so the jump keeps
        // re-issuing the mineral-walk gather.
        private Vector2Dto? ResolveClearOfPartnerGatePoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker)
        {
            if (T1TestRelativeFrame(runtimeWorker.UnitTag) >= T1TestClearFailOpenFrame)
            {
                return HasNonZeroPoint(runtimeWorker.Position) ? runtimeWorker.Position : new Vector2Dto(0.01f, 0.01f, 0f);
            }

            var partner = FindT1TestPartner(assignedWorker);
            var partnerPosition = HasNonZeroPoint(partner?.CurrentXY) ? partner.CurrentXY : null;
            if (partnerPosition == null)
            {
                return null;
            }

            if (HasNonZeroPoint(runtimeWorker.Position)
                && DistanceSquared(runtimeWorker.Position, partnerPosition) < BumpClearGateRadius * BumpClearGateRadius)
            {
                return null;
            }

            return HasNonZeroPoint(runtimeWorker.Position) ? runtimeWorker.Position : new Vector2Dto(0.01f, 0.01f, 0f);
        }

        // ---- Bump choreography resolvers (owner spec 2026-10-26 18:45, variant 17) ----

        // The current start's patch mineral with the given FinalLabel/Label.
        private OrderedMineral FindChoreoMineralByLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            var startIndex = Globals.CurrentStartIndex >= 0
                ? Globals.CurrentStartIndex
                : Settings.CurrentSpawnIndex;
            var assignments = _mapData?.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            if (assignments == null)
            {
                return null;
            }

            foreach (var assignment in assignments)
            {
                foreach (var mineral in assignment?.Minerals ?? Enumerable.Empty<OrderedMineral>())
                {
                    if (mineral != null
                        && (string.Equals(mineral.FinalLabel, label, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(mineral.Label, label, StringComparison.OrdinalIgnoreCase)))
                    {
                        return mineral;
                    }
                }
            }

            return null;
        }

        // The assigned worker with the given role at the current start, with a
        // usable live position.
        private Vector2Dto LivePositionOfChoreoRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return null;
            }

            var startIndex = Globals.CurrentStartIndex >= 0
                ? Globals.CurrentStartIndex
                : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();
            var worker = assignedWorkers.FirstOrDefault(candidate =>
                string.Equals(candidate?.Role, role, StringComparison.OrdinalIgnoreCase));
            return HasNonZeroPoint(worker?.CurrentXY) ? worker.CurrentXY : null;
        }

        // The assigned worker's unit tag for the given role (0 when unknown) —
        // used by --t1trace, whose telemetry path sees an empty worker Label.
        private ulong ChoreoRoleUnitTag(string role)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return 0;
            }

            var startIndex = Globals.CurrentStartIndex >= 0
                ? Globals.CurrentStartIndex
                : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                ?? new List<AssignedWorkerDto>();
            var worker = assignedWorkers.FirstOrDefault(candidate =>
                string.Equals(candidate?.Role, role, StringComparison.OrdinalIgnoreCase));
            return worker?.UnitID ?? 0;
        }

        // The worker's latched frame-0 position (first resolve = the start).
        private Vector2Dto ChoreoLatchedStart(RuntimeWorkerState runtimeWorker)
        {
            if (runtimeWorker == null)
            {
                return null;
            }

            if (_choreoStartLatches.TryGetValue(runtimeWorker.UnitTag, out var latched))
            {
                return latched;
            }

            if (!HasNonZeroPoint(runtimeWorker.Position))
            {
                return null;
            }

            _choreoStartLatches[runtimeWorker.UnitTag] = runtimeWorker.Position;
            return runtimeWorker.Position;
        }

        // ChoreoGreenLine — T1's frame-0 target: latched start through the TA
        // SM-line midpoint (the Green Star), extended 2u past the star.
        private Vector2Dto ResolveChoreoGreenLinePoint(RuntimeWorkerState runtimeWorker, WorkerInstruction instruction)
        {
            var start = ChoreoLatchedStart(runtimeWorker);
            var ta = FindChoreoMineralByLabel("TA");
            if (start == null || ta?.Position == null)
            {
                return null;
            }

            // Owner spec (Paint, 2026-10-26 20:47): the green line is anchored on
            // the TA mineral itself — T1 rolls THROUGH/PAST the mineral, 2u beyond
            // it along T1-start -> mineral. The anchor is the LIVE mineral unit
            // position (owner screenshot 2026-10-27: the dat HarvestPoint sits
            // BETWEEN TA and the neighboring patch, sending the roll target
            // ~0.7u off the real line); the persisted HarvestPoint/Position are
            // only fallbacks, and ResourcePosition is ~0.9u off the unit
            // (t1test10b diagnostic) so it never anchors the line.
            Vector2Dto anchor = null;
            var liveSeed = ta.Position != null ? ta.Position
                : HasNonZeroPoint(ta.HarvestPoint) ? ta.HarvestPoint
                : HasNonZeroPoint(ta.SmHarvestPoint) ? ta.SmHarvestPoint
                : null;
            if (liveSeed != null)
            {
                anchor = LiveMineralPositionNear(liveSeed);
            }

            if (anchor == null && HasNonZeroPoint(ta.HarvestPoint))
            {
                anchor = ta.HarvestPoint;
            }

            if (anchor == null && ta.Position != null && (ta.Position.X != 0f || ta.Position.Y != 0f))
            {
                anchor = ta.Position;
            }

            if (anchor == null && HasNonZeroPoint(ta.SmHarvestPoint))
            {
                anchor = ta.SmHarvestPoint;
            }

            if (anchor == null)
            {
                return null;
            }

            var dx = anchor.X - start.X;
            var dy = anchor.Y - start.Y;
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.0001f)
            {
                return null;
            }

            var target = new Vector2Dto(
                anchor.X + dx / len * ChoreoExtendUnits,
                anchor.Y + dy / len * ChoreoExtendUnits,
                anchor.Z);
            Console.WriteLine($"[CHOREO GREENLINE] worker={runtimeWorker.UnitTag} start=({start.X:F2},{start.Y:F2}) anchor=({anchor.X:F2},{anchor.Y:F2}) target=({target.X:F2},{target.Y:F2})");
            return target;
        }

        // ChoreoSmHarvestLabel — the TargetPointReference-labeled mineral's
        // SmHarvestPoint (fallback HarvestPoint).
        private Vector2Dto ResolveChoreoSmHarvestLabelPoint(WorkerInstruction instruction)
        {
            var mineral = FindChoreoMineralByLabel(instruction?.TargetPointReference);
            if (mineral == null)
            {
                return null;
            }

            if (HasNonZeroPoint(mineral.SmHarvestPoint))
            {
                return mineral.SmHarvestPoint;
            }

            return HasNonZeroPoint(mineral.HarvestPoint) ? mineral.HarvestPoint : null;
        }

        // ChoreoT4AngleStart — owner test suite 2026-10-28 08:14 ("Start T4
        // with a move instead of a mineral Walk, then at frame 8 a move to TB"):
        // the worker's latched frame-0 position, heading toward the row's
        // labeled mineral ("SA") CENTER rotated by Settings.T4AngleDegrees
        // (+ = counterclockwise in map coords, 0 = the old mineral-walk
        // heading), target 4u out so T4 does not decelerate before the
        // frame-8 TB switch. Resolved from the latched start, so both row
        // copies (frames 0 and 1) resolve identically.
        private const float ChoreoT4AngleMoveLength = 4f;

        private Vector2Dto ResolveChoreoT4AngleStartPoint(RuntimeWorkerState runtimeWorker, WorkerInstruction instruction)
        {
            var start = ChoreoLatchedStart(runtimeWorker);
            var headingMineral = FindChoreoMineralByLabel(
                string.IsNullOrWhiteSpace(instruction?.TargetPointReference) ? "SA" : instruction.TargetPointReference);
            if (start == null || headingMineral == null || !HasNonZeroPoint(headingMineral.Position))
            {
                return null;
            }

            var degrees = Settings.T4AngleDegrees == int.MinValue ? 0 : Settings.T4AngleDegrees;
            var dx = headingMineral.Position.X - start.X;
            var dy = headingMineral.Position.Y - start.Y;
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.0001f)
            {
                return null;
            }

            var rad = degrees * Math.PI / 180.0;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            var ux = dx / len;
            var uy = dy / len;
            var rx = (float)(ux * cos - uy * sin);
            var ry = (float)(ux * sin + uy * cos);

            var target = new Vector2Dto(
                start.X + rx * ChoreoT4AngleMoveLength,
                start.Y + ry * ChoreoT4AngleMoveLength,
                start.Z);

            var headingDeg = (float)(Math.Atan2(ry, rx) * 180.0 / Math.PI);
            Console.WriteLine($"[CHOREO T4-ANGLE-START] worker={runtimeWorker.UnitTag} deg={degrees} heading={headingDeg:F1} start=({start.X:F2},{start.Y:F2}) target=({target.X:F2},{target.Y:F2})");
            return target;
        }

        // ChoreoSmLineExtend — T1's frame-10 redirect target (owner spec
        // 2026-10-27 11:16 "switch to the harvest line extended 2u
        // intersecting SMH ... Let T4 bump T1 off that target line ... then
        // target the mineral"): the TA SmHarvestPoint extended
        // ChoreoExtendUnits along the SmReturn -> SmHarvest direction, so T1
        // rolls THROUGH the SM point at cruise speed instead of decelerating.
        // While T1 stays ON the line the extension target is re-issued every
        // frame; the per-worker latch below watches T1's perpendicular
        // distance to the SM line — once T4's bump knocks T1 off it (or T1
        // reaches the end of the extension without a bump), the resolver
        // switches to the SM harvest point itself and the arrival gate fires
        // the Smart from there. On-line latch 0.18u, knocked-off 0.30u,
        // end-of-line 0.4u from the extension target. Fallbacks for the line
        // direction: SmReturnPoint -> HarvestPoint -> latched start.
        private Vector2Dto ResolveChoreoSmLineExtendPoint(RuntimeWorkerState runtimeWorker)
        {
            var start = ChoreoLatchedStart(runtimeWorker);
            var ta = FindChoreoMineralByLabel("TA");
            if (start == null || ta == null)
            {
                return null;
            }

            var smHarvest = HasNonZeroPoint(ta.SmHarvestPoint) ? ta.SmHarvestPoint
                : HasNonZeroPoint(ta.HarvestPoint) ? ta.HarvestPoint
                : null;
            if (smHarvest == null)
            {
                return null;
            }

            // T1's DIRECT line to the SM point (owner test suite 2026-10-27
            // 15:24 "T1 stays on the SMH line"): the extension runs along
            // latched-start -> SmHarvestPoint. Fallbacks: SmReturnPoint ->
            // HarvestPoint (both near the hatchery side, so the direction is
            // nearly identical).
            Vector2Dto through;
            if (start != null)
            {
                through = start;
            }
            else if (HasNonZeroPoint(ta.SmReturnPoint))
            {
                through = ta.SmReturnPoint;
            }
            else if (HasNonZeroPoint(ta.HarvestPoint))
            {
                through = ta.HarvestPoint;
            }
            else
            {
                return null;
            }

            var dx = smHarvest.X - through.X;
            var dy = smHarvest.Y - through.Y;
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.0001f)
            {
                dx = smHarvest.X - start.X;
                dy = smHarvest.Y - start.Y;
                len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.0001f)
                {
                    return null;
                }
            }

            var target = new Vector2Dto(
                smHarvest.X + dx / len * ChoreoExtendUnits,
                smHarvest.Y + dy / len * ChoreoExtendUnits,
                smHarvest.Z);

            if (!_choreoT1LineState.TryGetValue(runtimeWorker.UnitTag, out var lineState))
            {
                lineState = 0;
            }

            var livePos = LivePositionOfUnitTag(runtimeWorker.UnitTag);
            if (livePos != null && lineState < 2)
            {
                // Perpendicular distance from T1 to the SM line through the
                // SM harvest point: |cross(dir, pos - smh)| / len.
                var perp = Math.Abs(dx * (livePos.Y - smHarvest.Y) - dy * (livePos.X - smHarvest.X)) / len;
                var distToExtTarget = (float)Math.Sqrt(
                    (livePos.X - target.X) * (livePos.X - target.X) +
                    (livePos.Y - target.Y) * (livePos.Y - target.Y));

                if (lineState == 0 && perp <= 0.18f)
                {
                    lineState = 1; // confirmed rolling on the line
                }
                if (lineState == 1 && perp >= 0.30f)
                {
                    lineState = 2; // T4's bump knocked T1 off the line
                }
                else if (distToExtTarget <= 0.4f)
                {
                    lineState = 2; // reached the extension end unbumped: circle back to the mineral
                }

                _choreoT1LineState[runtimeWorker.UnitTag] = lineState;
                Console.WriteLine($"[CHOREO SM-LINE-EXTEND] worker={runtimeWorker.UnitTag} state={lineState} perp={perp:F2} distToExt={distToExtTarget:F2} smh=({smHarvest.X:F2},{smHarvest.Y:F2}) target=({target.X:F2},{target.Y:F2})");
            }

            if (lineState == 2)
            {
                // Bumped off the line (or end of it): target the mineral.
                Console.WriteLine($"[CHOREO SM-LINE-EXTEND -> MINERAL] worker={runtimeWorker.UnitTag} targeting SMH ({smHarvest.X:F2},{smHarvest.Y:F2})");
                return new Vector2Dto(smHarvest.X, smHarvest.Y, smHarvest.Z);
            }

            return target;
        }

        // ChoreoConvergence — T1's frame-0 target (owner spec 2026-10-27
        // 09:18): the point on T1's latched start -> TA-mineral line closest
        // to the TA SM harvest point — where T1, T4 (mineral walk toward SA)
        // and T5 (move to SA) all arrive ~frame 5. Deliberately NOT extended:
        // T1 MEETS the other two there, takes T4's frame-6 bump move, and the
        // frame-6 row then redirects it to the TA SM harvest point.
        private Vector2Dto ResolveChoreoConvergencePoint(RuntimeWorkerState runtimeWorker)
        {
            var start = ChoreoLatchedStart(runtimeWorker);
            var ta = FindChoreoMineralByLabel("TA");
            if (start == null || ta == null)
            {
                return null;
            }

            var anchor = ChoreoTaAnchor(ta);
            var smHarvest = ChoreoSmHarvestOf(ta);
            if (anchor == null || smHarvest == null)
            {
                return null;
            }

            var convergence = ClosestPointOnLineTo(start, anchor, smHarvest);
            Console.WriteLine($"[CHOREO CONVERGENCE] worker={runtimeWorker.UnitTag} start=({start.X:F2},{start.Y:F2}) anchor=({anchor.X:F2},{anchor.Y:F2}) smh=({smHarvest.X:F2},{smHarvest.Y:F2}) target=({convergence?.X:F2},{convergence?.Y:F2})");
            return convergence ?? anchor;
        }

        // The TA mineral's anchor for the green line / convergence: the LIVE
        // unit position (the persisted HarvestPoint/Position can sit ~0.9u
        // off the real unit), falling back to HarvestPoint -> Position ->
        // SmHarvestPoint.
        private static Vector2Dto ChoreoTaAnchor(OrderedMineral ta)
        {
            if (ta == null)
            {
                return null;
            }

            var seed = ta.Position != null ? ta.Position
                : HasNonZeroPoint(ta.HarvestPoint) ? ta.HarvestPoint
                : HasNonZeroPoint(ta.SmHarvestPoint) ? ta.SmHarvestPoint
                : null;
            var live = seed != null ? LiveMineralPositionNear(seed) : null;
            if (live != null)
            {
                return live;
            }

            if (HasNonZeroPoint(ta.HarvestPoint))
            {
                return ta.HarvestPoint;
            }

            if (ta.Position != null && (ta.Position.X != 0f || ta.Position.Y != 0f))
            {
                return ta.Position;
            }

            return HasNonZeroPoint(ta.SmHarvestPoint) ? ta.SmHarvestPoint : null;
        }

        // The mineral's SM harvest point (fallback HarvestPoint) — the
        // convergence reference and the frame-6 T1 target.
        private static Vector2Dto ChoreoSmHarvestOf(OrderedMineral mineral)
        {
            if (mineral == null)
            {
                return null;
            }

            return HasNonZeroPoint(mineral.SmHarvestPoint) ? mineral.SmHarvestPoint
                : HasNonZeroPoint(mineral.HarvestPoint) ? mineral.HarvestPoint
                : null;
        }

        // The closest point on the segment a->b to point p.
        private static Vector2Dto ClosestPointOnLineTo(Vector2Dto a, Vector2Dto b, Vector2Dto p)
        {
            if (a == null || b == null || p == null)
            {
                return null;
            }

            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 0.0001f)
            {
                return null;
            }

            var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared;
            t = Math.Max(0f, Math.Min(1f, t));
            return new Vector2Dto(a.X + dx * t, a.Y + dy * t, a.Z);
        }

        // ChoreoSaIntersect — T5's frame-0 move: the ray from T5's latched
        // start toward SA's SmHarvestPoint, extended 2u past its crossing with
        // the TA SM (SmReturn->SmHarvest) line. Falls back to 2u past SA's
        // SmHarvestPoint when the ray misses the SM line.
        private Vector2Dto ResolveChoreoSaIntersectPoint(RuntimeWorkerState runtimeWorker, WorkerInstruction instruction)
        {
            var start = ChoreoLatchedStart(runtimeWorker);
            var sa = FindChoreoMineralByLabel("SA");
            var ta = FindChoreoMineralByLabel("TA");
            if (start == null || sa?.Position == null || ta?.Position == null)
            {
                return null;
            }

            var saPoint = HasNonZeroPoint(sa.SmHarvestPoint) ? sa.SmHarvestPoint
                : HasNonZeroPoint(sa.HarvestPoint) ? sa.HarvestPoint
                : sa.Position;
            var dx = saPoint.X - start.X;
            var dy = saPoint.Y - start.Y;
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.0001f)
            {
                return null;
            }

            var dirX = dx / len;
            var dirY = dy / len;

            // Ray (start, dir) x segment (SmReturn -> SmHarvest) crossing.
            Vector2Dto target = null;
            if (HasNonZeroPoint(ta.SmReturnPoint) && HasNonZeroPoint(ta.SmHarvestPoint))
            {
                var ax = ta.SmReturnPoint.X;
                var ay = ta.SmReturnPoint.Y;
                var bx = ta.SmHarvestPoint.X;
                var by = ta.SmHarvestPoint.Y;
                var segX = bx - ax;
                var segY = by - ay;
                var denom = dirX * segY - dirY * segX;
                if (Math.Abs(denom) > 0.0001f)
                {
                    var s = (start.X - ax) * segY - (start.Y - ay) * segX;
                    s = -s / denom;
                    var t = Math.Abs(segX) > Math.Abs(segY)
                        ? (ax + s * segX - start.X) / dirX
                        : (ay + s * segY - start.Y) / dirY;
                    if (t > 0f && s >= 0f && s <= 1f)
                    {
                        var crossX = start.X + t * dirX;
                        var crossY = start.Y + t * dirY;
                        target = new Vector2Dto(
                            crossX + dirX * ChoreoExtendUnits,
                            crossY + dirY * ChoreoExtendUnits,
                            saPoint.Z);
                        Console.WriteLine($"[CHOREO SA-INTERSECT] worker={runtimeWorker.UnitTag} start=({start.X:F2},{start.Y:F2}) crossing=({crossX:F2},{crossY:F2}) t={t:F2} s={s:F2} target=({target.X:F2},{target.Y:F2})");
                    }
                }
            }

            if (target == null)
            {
                target = new Vector2Dto(
                    saPoint.X + dirX * ChoreoExtendUnits,
                    saPoint.Y + dirY * ChoreoExtendUnits,
                    saPoint.Z);
                Console.WriteLine($"[CHOREO SA-INTERSECT FALLBACK] worker={runtimeWorker.UnitTag} start=({start.X:F2},{start.Y:F2}) target=({target.X:F2},{target.Y:F2})");
            }

            return target;
        }

        // ChoreoWaitCircle2Label — the labeled mineral's OUTER-circle stand
        // point: from the mineral center through the HarvestPoint direction at
        // (harvest radius + 0.4u) — Circle 2, where a worker waits for a busy
        // mineral.
        private Vector2Dto ResolveChoreoWaitCircle2LabelPoint(WorkerInstruction instruction)
        {
            var mineral = FindChoreoMineralByLabel(instruction?.TargetPointReference);
            if (mineral?.Position == null || !HasNonZeroPoint(mineral.HarvestPoint))
            {
                return null;
            }

            var dx = mineral.HarvestPoint.X - mineral.Position.X;
            var dy = mineral.HarvestPoint.Y - mineral.Position.Y;
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.0001f)
            {
                return null;
            }

            var radius = len + ChoreoWaitCircleExtra;
            return new Vector2Dto(
                mineral.Position.X + dx / len * radius,
                mineral.Position.Y + dy / len * radius,
                mineral.Position.Z);
        }

        // ChoreoT1ArrivalGate (on an InvertGate Jump) — TWO anchors, selected
        // by the row's TargetPointReference (owner spec 2026-10-27 09:18):
        //   "SMH" (T1's own book): null while T1 is NOT within
        //       ChoreoSmHarvestGateRadius of the TA SmHarvestPoint — T1's
        //       frame-6 target — with a fixed threshold (the speed-based one
        //       starved over the deceleration crawl, NW run 20:56). Non-null
        //       fires T1's Smart ON TA FROM the SM point.
        //   null/other (T4/T5's books): null while T1 is NOT within one
        //       frame's travel of the TA mineral STAND — where "will reach SM
        //       Harvest on the next frame" happens. Non-null releases T4/T5's
        //       SB mineral-walk.
        // Frame floor and the frame-30 fail-open apply to both.
        private Vector2Dto ResolveChoreoT1ArrivalGatePoint(RuntimeWorkerState runtimeWorker, WorkerInstruction instruction)
        {
            // GAME-relative frame (the owner's "from frame 16" is the run-relative
            // frame, not the per-row one — InstructionStartFrame resets on every
            // row advance).
            var gameRelativeFrame = Settings.GetRelativeFrame(_currentFrame);
            var t1Position = LivePositionOfChoreoRole("T1");
            if (Settings.T1PositionTrace && Settings.T1TraceUnitTag == 0)
            {
                var traceTag = ChoreoRoleUnitTag("T1");
                if (traceTag != 0)
                {
                    Settings.T1TraceUnitTag = traceTag;
                }
            }
            var fallback = t1Position ?? (HasNonZeroPoint(runtimeWorker.Position) ? runtimeWorker.Position : new Vector2Dto(0.01f, 0.01f, 0f));

            if (gameRelativeFrame >= ChoreoT1ArrivalFailOpenFrame)
            {
                Console.WriteLine($"[CHOREO T1-ARRIVAL FAIL-OPEN] frame={gameRelativeFrame} worker={runtimeWorker.UnitTag}");
                return fallback;
            }

            if (gameRelativeFrame < ChoreoT1ArrivalEarliestFrame || t1Position == null)
            {
                return null;
            }

            var ta = FindChoreoMineralByLabel("TA");
            if (ta == null)
            {
                return null;
            }

            var gateToSmh = string.Equals(instruction?.TargetPointReference, "SMH", StringComparison.OrdinalIgnoreCase);
            var anchor = gateToSmh
                ? ChoreoSmHarvestOf(ta)
                : HasNonZeroPoint(ta.HarvestPoint) ? ta.HarvestPoint
                : HasNonZeroPoint(ta.SmHarvestPoint) ? ta.SmHarvestPoint
                : null;
            if (anchor == null)
            {
                return null;
            }

            if (gateToSmh)
            {
                // Owner spec 2026-10-27 11:16: the Smart is the END of the
                // sequence — "Let T4 bump T1 off that target line ... then
                // target the mineral". While T1 is still rolling the SM line
                // (line state 0/1) the gate stays NULL even as T1 passes
                // through the SM point — the 2026-10-27 14:35 runs fired the
                // Smart at f10/f11 with T1 ~1.7u from the stand and still
                // accelerating (order dropped; standard mining re-gathered at
                // f25). Only the knock-off (state 2) arms it.
                if (!_choreoT1LineState.TryGetValue(runtimeWorker.UnitTag, out var lineState) || lineState < 2)
                {
                    return null;
                }
            }

            float threshold;
            var speed = _choreoT1Speed;
            if (gateToSmh)
            {
                // Fixed corridor radius plus the owner's --gatelead=N frames of
                // cruise (owner test suite 2026-10-28 08:51): fire the Smart N
                // frames BEFORE SMH arrival so order-latency overlaps travel.
                var lead = Math.Max(0, Settings.SmhSmartGateLeadFrames);
                threshold = ChoreoSmHarvestGateRadius + lead * 0.15f;
            }
            else
            {
                // T1's per-frame travel from its own live deltas (pushed workers
                // move faster than cruise, so a fixed threshold would fire early).
                // Updated once per game frame no matter how many gate rows ask.
                if (_choreoT1SpeedFrame != _currentFrame)
                {
                    _choreoT1SpeedFrame = _currentFrame;
                    if (_choreoT1LastPositions.TryGetValue(0ul, out var last))
                    {
                        var moved = (float)Math.Sqrt(DistanceSquared(last, t1Position));
                        if (moved > 0.001f)
                        {
                            _choreoT1Speed = moved;
                        }
                    }
                    _choreoT1LastPositions[0ul] = t1Position;
                }

                speed = _choreoT1Speed;

                // Floor at one CRUISE frame (0.15u): the speed-based estimate is
                // only trustworthy while T1 rolls at speed; if a bump slows it at
                // the wrong moment the old floor (0.08) left the gate unable to
                // fire over the last 0.13u (NW run 2026-10-26 20:56 — T1 crawled
                // 0.03 u/frame toward a point-move target and the gate starved).
                threshold = Math.Min(ChoreoArrivalMaxSpeed, Math.Max(ChoreoArrivalMinSpeed, speed * 1.3f));
            }

            var distance = (float)Math.Sqrt(DistanceSquared(t1Position, anchor));
            if (distance > threshold)
            {
                return null;
            }

            Console.WriteLine($"[CHOREO T1-ARRIVAL] mode={(gateToSmh ? "SMH" : "STAND")} frame={gameRelativeFrame} worker={runtimeWorker.UnitTag} t1=({t1Position.X:F2},{t1Position.Y:F2}) dist={distance:F2} speed={speed:F2} lead={Settings.SmhSmartGateLeadFrames} threshold={threshold:F2}");
            return t1Position;
        }

        // ChoreoClearOfGate (on an InvertGate Jump): null while the WORKER is
        // within 1.0u of the live role named in TargetPointReference ("T1" for
        // T4's exit walk, "T3" for T5's), non-null once clear; fail-open frame
        // 45 so the book can never park.
        private Vector2Dto ResolveChoreoClearOfGatePoint(RuntimeWorkerState runtimeWorker, WorkerInstruction instruction)
        {
            var gameRelativeFrame = Settings.GetRelativeFrame(_currentFrame);
            var fallback = HasNonZeroPoint(runtimeWorker.Position) ? runtimeWorker.Position : new Vector2Dto(0.01f, 0.01f, 0f);
            if (gameRelativeFrame >= ChoreoClearFailOpenFrame)
            {
                return fallback;
            }

            var other = LivePositionOfChoreoRole(instruction?.TargetPointReference);
            if (other == null)
            {
                return null;
            }

            if (HasNonZeroPoint(runtimeWorker.Position)
                && DistanceSquared(runtimeWorker.Position, other) < ChoreoClearRadius * ChoreoClearRadius)
            {
                return null;
            }

            Console.WriteLine($"[CHOREO CLEAR] frame={gameRelativeFrame} worker={runtimeWorker.UnitTag} clearOf={instruction?.TargetPointReference}");
            return fallback;
        }

        // StarRedirect (variants 3/4, T1's own book): the row TargetId mineral's
        // SmHarvestPoint while the worker is within 0.2u of the mineral's
        // SmMidpoint (the Green Star), null while still approaching — so a
        // frame-gated Move row carrying this point redirects T1 from the star to
        // the SM harvest only at star proximity; relative-frame-24 deadline
        // fail-open returns the SM harvest unconditionally so the book can never
        // stall.
        private Vector2Dto? ResolveStarRedirectPoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            var target = assignedWorker.MiningTargets?.FirstOrDefault(candidate => candidate != null && candidate.ResourceUnitId == instruction.TargetId)
                ?? T1TestPartnerFirstTarget(assignedWorker);
            if (target == null || !HasNonZeroPoint(target.SmHarvestPoint))
            {
                return null;
            }

            // One-shot latch: once the redirect has fired it stays fired — T1
            // immediately leaves the 0.2u star zone toward the SM harvest and an
            // unlatched gate would go null again and stall the rows behind it.
            if (_t1TestStarPassed.Contains(runtimeWorker.UnitTag))
            {
                return target.SmHarvestPoint;
            }

            if (T1TestRelativeFrame(runtimeWorker.UnitTag) >= T1TestStarRedirectFailOpenFrame)
            {
                _t1TestStarPassed.Add(runtimeWorker.UnitTag);
                return target.SmHarvestPoint;
            }

            var star = T1TestSmMidpoint(target);
            if (star == null)
            {
                _t1TestStarPassed.Add(runtimeWorker.UnitTag);
                return target.SmHarvestPoint;
            }

            if (HasNonZeroPoint(runtimeWorker.Position)
                && DistanceSquared(runtimeWorker.Position, star) <= T1TestStarRadius * T1TestStarRadius)
            {
                _t1TestStarPassed.Add(runtimeWorker.UnitTag);
                return target.SmHarvestPoint;
            }

            return null;
        }

        // The anchor for "close enough to harvest" and the rolling line's
        // direction: the mineral's HarvestPoint — the persisted gather spot,
        // which matches the mineral unit's actual position (observed stand
        // within 0.1u). ResourcePosition is NOT usable for this: for TA it is
        // (25.00,133.50), ~0.9u off the unit's real position (diagnostic run
        // t1test10b 2026-10-25), which parked the rolling waypoint past the
        // mineral and made the range gate unreachable.
        private static Vector2Dto T1TestMineralAnchor(MiningTargetDto target)
        {
            if (target == null)
            {
                return null;
            }

            return HasNonZeroPoint(target.HarvestPoint) ? target.HarvestPoint : target.ResourcePosition;
        }

        // RollingLine (owner spec 2026-10-25 09:24, tests 10/11): the worker's
        // current position + 0.5u along the unit direction from the worker's
        // frame-0 position (captured once on first resolve) through the row
        // mineral's ResourcePosition — the start->mineral line extended through
        // the mineral. Because the row re-resolves every frame the waypoint is
        // always 0.5u ahead, so the worker is permanently in transit and never
        // executes the arrival deceleration a fixed move target causes.
        private Vector2Dto ResolveRollingLinePoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            var target = assignedWorker.MiningTargets?.FirstOrDefault(candidate => candidate != null && candidate.ResourceUnitId == instruction.TargetId);
            var anchor = T1TestMineralAnchor(target);
            if (anchor == null || !HasNonZeroPoint(runtimeWorker.Position))
            {
                return null;
            }

            // NOTE: no stand-down here — a null point on a Move row makes the
            // row wait (caller break) BEFORE the gate row evaluates, deadlocking
            // the book with the worker parked on the mineral (t1test10c). The
            // gate handles the in-range transition; 0.45u puts the transition
            // move's target within 0.1u of the anchor so the handoff is clean.

            // Variant 7/8 (owner spec 2026-10-25 12:09): 2u rolling step — a
            // 0.5u target holds the worker in the game's close-approach speed
            // band (0.10-0.12 u/frame) and it never reaches the 0.15 cruise a
            // distant target gets. Variant 8 additionally sends the move ONLY
            // every 5th frame, separating target distance from command
            // frequency as the limiter. A null (waiting) row on off-cadence
            // frames is safe here: firing frames recur every 5, so the gate is
            // always re-evaluated; the in-range transition on a firing frame is
            // handled by the transition-frame suppression (the move is skipped,
            // the Smart fires alone).
            var relativeFrame = T1TestRelativeFrame(runtimeWorker.UnitTag);
            var step = T1TestRollingStep;
            if (Settings.T1TestNumber / 2 >= 7)
            {
                step = 2.0f;
                if (Settings.T1TestNumber / 2 == 8 && relativeFrame % 5 != 0)
                {
                    return null;
                }
            }

            // Variant 12+ (owner rerun 2026-10-25, "homing rolling" after T3's
            // tests 22/23 sail-past; rolled out to all 8 miners as variant 13
            // and to the return leg as variant 14): the waypoint is 2u toward
            // the mineral FROM THE WORKER'S CURRENT POSITION (direction
            // re-computed every frame), not 2u along the fixed frame-0-origin
            // line. The fixed line never converges after a perpendicular bump
            // — T4/T5's SA mineral walk repelled T3 off its line and the
            // rolling waypoint ran parallel to it at ~1u out until the gate
            // fail-open at frame 40 (Smart 3.15u out, pickup 146). Homing keeps
            // the anti-decel receding target but self-heals any deflection.
            float dirX;
            float dirY;
            if (Settings.T1TestNumber / 2 >= 12)
            {
                dirX = anchor.X - runtimeWorker.Position.X;
                dirY = anchor.Y - runtimeWorker.Position.Y;
            }
            else
            {
                if (!_t1TestRollingOrigins.TryGetValue(runtimeWorker.UnitTag, out var origin))
                {
                    origin = runtimeWorker.Position;
                    _t1TestRollingOrigins[runtimeWorker.UnitTag] = origin;
                }

                dirX = anchor.X - origin.X;
                dirY = anchor.Y - origin.Y;
            }

            var length = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
            if (length < 0.001f)
            {
                return null;
            }

            return new Vector2Dto(
                runtimeWorker.Position.X + dirX / length * step,
                runtimeWorker.Position.Y + dirY / length * step,
                runtimeWorker.Position.Z);
        }

        // HarvestRangeGate (owner spec 2026-10-25 09:24, on an InvertGate Jump in
        // the rolling book): resolves (non-null) once the worker is within 0.35u
        // of the row mineral's ResourcePosition — close enough for the harvest
        // command to engage immediately — OR at the relative frame 40 deadline;
        // null while still approaching, so the jump keeps re-issuing the rolling
        // move and the book can never stall.
        // Per-frame cache (owner rerun 2026-10-25, same fix as LeadHarvestGate):
        // the control loop can evaluate this gate twice in one frame; without
        // the cache a borderline frame issued the rolling MOVE and the Smart
        // together, and the move won arbitration.
        private readonly System.Collections.Generic.Dictionary<ulong, (int Frame, Vector2Dto? Result)> _harvestRangeGateFrameCache = new();

        private Vector2Dto? ResolveHarvestRangeGatePoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if (_harvestRangeGateFrameCache.TryGetValue(runtimeWorker.UnitTag, out var cached) && cached.Frame == _currentFrame)
            {
                return cached.Result;
            }

            var result = EvaluateHarvestRangeGate(runtimeWorker, assignedWorker, instruction);
            _harvestRangeGateFrameCache[runtimeWorker.UnitTag] = (_currentFrame, result);
            return result;
        }

        private Vector2Dto? EvaluateHarvestRangeGate(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if (T1TestRelativeFrame(runtimeWorker.UnitTag) >= T1TestRollingHarvestFailOpenFrame)
            {
                return HasNonZeroPoint(runtimeWorker.Position) ? runtimeWorker.Position : new Vector2Dto(0.01f, 0.01f, 0f);
            }

            var target = assignedWorker.MiningTargets?.FirstOrDefault(candidate => candidate != null && candidate.ResourceUnitId == instruction.TargetId);
            var anchor = T1TestMineralAnchor(target);
            if (anchor == null || !HasNonZeroPoint(runtimeWorker.Position))
            {
                return null;
            }

            var distSquared = DistanceSquared(runtimeWorker.Position, anchor);
            if (distSquared <= T1TestHarvestRangeRadius * T1TestHarvestRangeRadius)
            {
                return runtimeWorker.Position;
            }

            return null;
        }

        // DecelPlan (owner spec 2026-10-25 16:17, variant 9): the anti-
        // deceleration approach move. Until the worker is DecelPlanLatchRadius
        // (0.7u — ~1 frame before the observed ~0.58u decel onset) from the row
        // mineral's HarvestPoint the row resolves that anchor, the worker's
        // normal destination. At the latch the target switches to a FIXED point
        // DecelPlanPastExtension (2u) beyond the HarvestPoint along the captured
        // frame-0-origin -> HarvestPoint line and stays latched (the cache is
        // returned on every later resolve — re-issuing a fixed move target does
        // not restart the game's move), so the worker cruises through the
        // footprint at full speed and the LeadHarvestGate row below keeps
        // evaluating. T2 is the owner-spec special: ONE move at frame 0, 0.5u
        // past the HarvestPoint (latched on the first resolve, before the
        // proximity test), and no gate — its Smart is a plain queued frame row.
        private Vector2Dto ResolveDecelPlanPoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            var target = assignedWorker.MiningTargets?.FirstOrDefault(candidate => candidate != null && candidate.ResourceUnitId == instruction.TargetId);
            var anchor = T1TestMineralAnchor(target);
            if (anchor == null || !HasNonZeroPoint(runtimeWorker.Position))
            {
                return null;
            }

            var unitTag = runtimeWorker.UnitTag;
            var isT2 = (assignedWorker.Role ?? string.Empty).Equals("T2", StringComparison.OrdinalIgnoreCase);

            if (isT2)
            {
                if (_decelPlanPastPoints.TryGetValue(unitTag, out var cachedT2))
                {
                    return cachedT2;
                }

                var pastT2 = ExtendPointPast(runtimeWorker.Position, anchor, DecelPlanT2Extension);
                _decelPlanPastPoints[unitTag] = pastT2;
                return pastT2;
            }

            if (_decelPlanPastPoints.TryGetValue(unitTag, out var cached))
            {
                return cached;
            }

            if (!_decelPlanOrigins.TryGetValue(unitTag, out var origin))
            {
                origin = runtimeWorker.Position;
                _decelPlanOrigins[unitTag] = origin;
            }

            var latchRadiusSquared = DecelPlanLatchRadius * DecelPlanLatchRadius;
            if (DistanceSquared(runtimeWorker.Position, anchor) <= latchRadiusSquared)
            {
                var past = ExtendPointPast(origin, anchor, DecelPlanPastExtension);
                _decelPlanPastPoints[unitTag] = past;
                return past;
            }

            return anchor;
        }

        // LeadHarvestGate (owner spec 2026-10-25 16:17, variant 9): an
        // InvertGate-jump gate on the DecelPlan book. Resolves (non-null) once
        // the worker is within 0.45u of the row mineral's HarvestPoint OR is
        // projected to be within 0.45u in `lead` frames (lead by role, see
        // DecelPlanLeadForRole) — "issue a Smart k frames away" — or at the
        // relative-frame-40 deadline; null while still approaching, so the jump
        // keeps re-issuing the DecelPlan move. The projection uses a per-worker
        // speed estimate from the last two resolves, clamped to the observed
        // 0.05-0.2 u/frame band.
        // Per-frame cache (owner rerun 2026-10-25 "try the test on T3 Y1"):
        // the control loop can evaluate this gate twice in one frame (the
        // pre-command suppression check and the jump pass-through). On a
        // borderline frame the first check approved the DecelPlan move and the
        // second passed the gate, so MOVE+SMART went out together — the move
        // won in-game arbitration and the Smart never engaged (T3's sail-past,
        // tests 18/19). The cache makes every evaluation in a frame agree:
        // pass -> the move was already skipped, Smart alone; null -> the move
        // rides and the gate re-checks next frame. The speed-estimate dicts
        // update on the first evaluation only.
        private readonly System.Collections.Generic.Dictionary<ulong, (int Frame, Vector2Dto? Result)> _leadHarvestGateFrameCache = new();

        private Vector2Dto? ResolveLeadHarvestGatePoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if (_leadHarvestGateFrameCache.TryGetValue(runtimeWorker.UnitTag, out var cached) && cached.Frame == _currentFrame)
            {
                return cached.Result;
            }

            var result = EvaluateLeadHarvestGate(runtimeWorker, assignedWorker, instruction);
            _leadHarvestGateFrameCache[runtimeWorker.UnitTag] = (_currentFrame, result);
            return result;
        }

        private Vector2Dto? EvaluateLeadHarvestGate(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            var relativeFrame = T1TestRelativeFrame(runtimeWorker.UnitTag);
            if (relativeFrame >= LeadHarvestFailOpenFrame)
            {
                return HasNonZeroPoint(runtimeWorker.Position) ? runtimeWorker.Position : new Vector2Dto(0.01f, 0.01f, 0f);
            }

            var target = assignedWorker.MiningTargets?.FirstOrDefault(candidate => candidate != null && candidate.ResourceUnitId == instruction.TargetId);
            var anchor = T1TestMineralAnchor(target);
            if (anchor == null || !HasNonZeroPoint(runtimeWorker.Position))
            {
                return null;
            }

            var unitTag = runtimeWorker.UnitTag;
            var distance = (float)Math.Sqrt(DistanceSquared(runtimeWorker.Position, anchor));
            if (distance <= LeadHarvestRangeRadius)
            {
                return runtimeWorker.Position;
            }

            var speed = 0.15f; // base cruise fallback for the first evaluation
            if (_decelPlanLastPositions.TryGetValue(unitTag, out var lastPosition)
                && _decelPlanLastFrames.TryGetValue(unitTag, out var lastFrame))
            {
                var frameDelta = Settings.GetRelativeFrame(_currentFrame) - lastFrame;
                if (frameDelta > 0 && HasNonZeroPoint(lastPosition))
                {
                    var measured = (float)Math.Sqrt(
                        (runtimeWorker.Position.X - lastPosition.X) * (runtimeWorker.Position.X - lastPosition.X)
                        + (runtimeWorker.Position.Y - lastPosition.Y) * (runtimeWorker.Position.Y - lastPosition.Y))
                        / frameDelta;
                    speed = Math.Max(DecelPlanMinSpeed, Math.Min(DecelPlanMaxSpeed, measured));
                }
            }

            _decelPlanLastPositions[unitTag] = runtimeWorker.Position;
            _decelPlanLastFrames[unitTag] = Settings.GetRelativeFrame(_currentFrame);

            var lead = DecelPlanLeadForRole(assignedWorker.Role ?? string.Empty);
            if (distance - lead * speed <= LeadHarvestRangeRadius)
            {
                return runtimeWorker.Position;
            }

            return null;
        }

        // RollingReturnPoint (variant 14, owner question 2026-10-25 "can this
        // technique be applied to the return trip going back to the hatchery"):
        // the homing 2u rolling waypoint toward the return anchor — the worker's
        // StoredTargetPoint (the {X,X} self-pair ReturnPoint the setup row just
        // stored), falling back to the row mineral's SmReturnPoint/ReturnPoint
        // if the store never ran. Identical anti-deceleration geometry to the
        // variant-12 homing harvest leg: the direction re-computes from the
        // live position every frame, so a bump deflection self-heals and the
        // drone reaches the hatchery footprint at full 0.176 cruise.
        private Vector2Dto ResolveRollingReturnPoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            var anchor = ResolveReturnAnchor(runtimeWorker, assignedWorker, instruction);
            if (anchor == null || !HasNonZeroPoint(runtimeWorker.Position))
            {
                return null;
            }

            var dx = anchor.X - runtimeWorker.Position.X;
            var dy = anchor.Y - runtimeWorker.Position.Y;
            var length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.001f)
            {
                return null;
            }

            return new Vector2Dto(
                runtimeWorker.Position.X + dx / length * DecelPlanPastExtension,
                runtimeWorker.Position.Y + dy / length * DecelPlanPastExtension,
                runtimeWorker.Position.Z);
        }

        private Vector2Dto ResolveReturnAnchor(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if (HasNonZeroPoint(runtimeWorker.StoredTargetPoint))
            {
                return runtimeWorker.StoredTargetPoint;
            }

            var target = assignedWorker.MiningTargets?.FirstOrDefault(candidate => candidate != null && candidate.ResourceUnitId == instruction.TargetId);
            if (target == null)
            {
                return null;
            }

            return HasNonZeroPoint(target.SmReturnPoint) ? target.SmReturnPoint : target.ReturnPoint;
        }

        // ReturnRangeGate (variant 14): an InvertGate-jump gate on the rolling
        // return book. Resolves (non-null) once the worker is within 0.45u of
        // the return anchor, or 40 frames after the return leg BEGAN — the leg
        // starts ~100 frames into the game, so the harvest gates' frame-0
        // relative anchor cannot be reused; the per-worker start frame is
        // captured on this gate's first evaluation. Per-frame cached (same
        // MOVE+SMART arbitration fix as the harvest gates).
        private readonly System.Collections.Generic.Dictionary<ulong, (int Frame, Vector2Dto? Result)> _returnRangeGateFrameCache = new();
        private readonly System.Collections.Generic.Dictionary<ulong, int> _returnGateStartFrames = new();

        private Vector2Dto? ResolveReturnRangeGatePoint(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            if (_returnRangeGateFrameCache.TryGetValue(runtimeWorker.UnitTag, out var cached) && cached.Frame == _currentFrame)
            {
                return cached.Result;
            }

            var result = EvaluateReturnRangeGate(runtimeWorker, assignedWorker, instruction);
            _returnRangeGateFrameCache[runtimeWorker.UnitTag] = (_currentFrame, result);
            return result;
        }

        private Vector2Dto? EvaluateReturnRangeGate(RuntimeWorkerState runtimeWorker, AssignedWorkerDto assignedWorker, WorkerInstruction instruction)
        {
            var anchor = ResolveReturnAnchor(runtimeWorker, assignedWorker, instruction);
            if (anchor == null || !HasNonZeroPoint(runtimeWorker.Position))
            {
                return null;
            }

            var unitTag = runtimeWorker.UnitTag;
            if (!_returnGateStartFrames.TryGetValue(unitTag, out var start))
            {
                start = Settings.GetRelativeFrame(_currentFrame);
                _returnGateStartFrames[unitTag] = start;
            }

            if (Settings.GetRelativeFrame(_currentFrame) - start >= LeadHarvestFailOpenFrame)
            {
                return runtimeWorker.Position;
            }

            var distanceSquared = DistanceSquared(runtimeWorker.Position, anchor);
            if (distanceSquared <= LeadHarvestRangeRadius * LeadHarvestRangeRadius)
            {
                return runtimeWorker.Position;
            }

            return null;
        }

        // SmMidpoint resolution for the final switch: the Green Star — halfway
        // between the mineral's SM harvest and SM return points.
        private static Vector2Dto ResolveSmMidpointPoint(MiningTargetDto target)
        {
            var midpoint = T1TestSmMidpoint(target);
            if (midpoint != null)
            {
                return midpoint;
            }

            return HasNonZeroPoint(target?.SmHarvestPoint) ? target.SmHarvestPoint : target?.HarvestPoint;
        }

        // Shared "clear of the miners' corridors" test (owner spec 2026-10-20):
        // true when NO role-1/2/3 worker (T1/T2/T3/Y1/Y2/Y3) is within
        // BumpClearGateRadius (0.4u) of the walker. The chase-bump walkers
        // (T4/T5/Y4/Y5) are never blockers — they share the corridor and must
        // never gate each other.
        private static bool IsClearOfRole123Workers(AssignedWorkerDto assignedWorker, List<AssignedWorkerDto> assignedWorkers)
        {
            var selfPosition = HasNonZeroPoint(assignedWorker?.CurrentXY) ? assignedWorker.CurrentXY : null;
            if (selfPosition == null)
            {
                return false;
            }

            var radiusSquared = BumpClearGateRadius * BumpClearGateRadius;
            foreach (var other in assignedWorkers)
            {
                if (other == null || other.UnitID == assignedWorker.UnitID)
                {
                    continue;
                }

                if (!IsChaseBumpRole123Miner(other.Role))
                {
                    continue;
                }

                if (other.CurrentXY != null
                    && HasNonZeroPoint(other.CurrentXY)
                    && DistanceSquared(selfPosition, other.CurrentXY) < radiusSquared)
                {
                    return false;
                }
            }

            return true;
        }

        // Bump-contact diagnostic trace (owner directive 2026-10-19 "yes" to
        // instrumenting the bump): one line per frame in the choreography modes
        // (0/2/3 — everything except the mode-1 no-help baseline) for absolute
        // frames 0-200 (owner spec 2026-10-20 "write the frames to 200
        // in the console") — positions of the six choreography workers, the
        // four chaser→role-1 distances, and the TA/YA mineral positions (for
        // line-progress analysis). Worker contact ≈ center distance ≤ 0.75u
        // (two 0.375 radii). The trace also emits one [FIRST-RETURN] line each
        // for T1 and Y1 at their first cargo-return frame (owner spec: "write a
        // line each for T1 and Y1 first return cargo Frame") — detected from
        // the LIVE snapshot carrying flag (the AssignedWorkerDto IsCarrying is
        // an onboarding value and is never refreshed).
        private const int BumpTraceMaxFrame = 200;

        // First-return tracking for the trace's [FIRST-RETURN] lines (owner spec
        // 2026-10-20 "write a line each for T1 and Y1 first return cargo Frame").
        // Live-snapshot carrying flags, one slot per role; -1 = not yet returned.
        private int _t1FirstReturnFrame = -1;
        private int _y1FirstReturnFrame = -1;
        private readonly Dictionary<ulong, bool> _bumpPrevCarrying = new Dictionary<ulong, bool>();

        private void TraceBumpContact()
        {
            if ((Settings.BumpTestMode == 1)
                || _currentFrame < 0
                || _currentFrame > BumpTraceMaxFrame)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = _mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex);
            if (assignedWorkers == null)
            {
                return;
            }

            AssignedWorkerDto FindRole(string role) =>
                assignedWorkers.FirstOrDefault(worker =>
                    string.Equals(worker?.Role, role, StringComparison.OrdinalIgnoreCase));

            string Pos(AssignedWorkerDto worker) =>
                worker == null || !HasNonZeroPoint(worker.CurrentXY)
                    ? "—"
                    : $"({worker.CurrentXY.X:F2},{worker.CurrentXY.Y:F2}){(worker.IsCarrying ? "*" : "")}";

            float? Dist(string fromRole, string toRole)
            {
                var from = FindRole(fromRole);
                var to = FindRole(toRole);
                if (from == null || to == null
                    || !HasNonZeroPoint(from.CurrentXY) || !HasNonZeroPoint(to.CurrentXY))
                {
                    return null;
                }

                return Distance(from.CurrentXY, to.CurrentXY);
            }

            string MinPos(string role)
            {
                var worker = FindRole(role);
                var target = worker?.MiningTargets?.FirstOrDefault();
                return target == null || !HasNonZeroPoint(target.ResourcePosition)
                    ? "—"
                    : $"({target.ResourcePosition.X:F2},{target.ResourcePosition.Y:F2})";
            }

            TrackFirstReturn("T1", FindRole("T1"), ref _t1FirstReturnFrame);
            TrackFirstReturn("Y1", FindRole("Y1"), ref _y1FirstReturnFrame);

            Console.WriteLine(
                $"[BUMPTRACE] f={_currentFrame} rel={Settings.GetRelativeFrame(_currentFrame)}"
                + $" T1={Pos(FindRole("T1"))} Y1={Pos(FindRole("Y1"))}"
                + $" T4={Pos(FindRole("T4"))} T5={Pos(FindRole("T5"))}"
                + $" Y4={Pos(FindRole("Y4"))} Y5={Pos(FindRole("Y5"))}"
                + $" dT4T1={Dist("T4", "T1"):F2} dT5T1={Dist("T5", "T1"):F2}"
                + $" dY4Y1={Dist("Y4", "Y1"):F2} dY5Y1={Dist("Y5", "Y1"):F2}"
                + $" TA={MinPos("T1")} YA={MinPos("Y1")}");
        }

        // Watches the worker's LIVE snapshot carrying flag and logs the first
        // carrying→not-carrying transition — the first cargo return. Runs inside
        // the frame-gated trace, so a role that never returns inside the traced
        // window simply never emits a line.
        private void TrackFirstReturn(string role, AssignedWorkerDto worker, ref int firstReturnFrame)
        {
            if (worker == null
                || firstReturnFrame >= 0
                || Globals.CurrentObservation?.SelfUnits == null
                || !Globals.CurrentObservation.SelfUnits.TryGetValue(worker.UnitID, out var observed))
            {
                return;
            }

            var carrying = observed.IsCarrying;
            _bumpPrevCarrying.TryGetValue(worker.UnitID, out var previous);
            _bumpPrevCarrying[worker.UnitID] = carrying;
            if (previous && !carrying)
            {
                firstReturnFrame = _currentFrame;
                Console.WriteLine($"[FIRST-RETURN] role={role} frame={_currentFrame} rel={Settings.GetRelativeFrame(_currentFrame)}");
            }
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
                // 5x5 Version 2 bump roles were T4 (bumps T1) and Y4 (bumps Y1); the
                // 2026-10-19 chase-bump choreography adds T5 (chases T1) and B2
                // (chases Y1) — with the 2026-10-21 W12/W10 label swap the
                // Y5-keyed chase is run by W12 and B2 (W10) runs the BB walk.
                partnerRole = workerRole.Equals("T4", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : workerRole.Equals("T5", StringComparison.OrdinalIgnoreCase) ? "T1"
                    : workerRole.Equals("Y4", StringComparison.OrdinalIgnoreCase) ? "Y1"
                    : workerRole.Equals("Y5", StringComparison.OrdinalIgnoreCase) ? "Y1"
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

        private bool IsHarvestReleaseReady(ulong mineralTag, bool freeOnly = false, bool framesOnly = false)
        {
            if (mineralTag == 0)
            {
                return true;
            }

            // Frames-only rows (owner spec 2026-10-26 18:45, the T4->TB / T5->SB
            // staging handoffs): pass ONLY when an active harvester has
            // accumulated StagedReleaseHarvestFrames — never on a free mineral.
            // The free-clause would release the stager the moment it arrives at
            // the stage point, long before T2/S2 start mining.
            if (framesOnly)
            {
                return _harvestFrameTracker.HasActiveHarvester(mineralTag)
                    && _harvestFrameTracker.HasAccumulatedReleaseFrames(mineralTag);
            }

            // Free right now -> send immediately. Free-only rows (B2's staging,
            // owner report 2026-10-25 21:36) stop there: an occupied mineral must
            // be ACTUALLY free, not merely past 32 accumulated frames — B2's
            // one-shot Smart bounces off a harvester that is still standing on
            // the mineral and the book deadlocks without cargo.
            if (freeOnly)
            {
                return !_harvestFrameTracker.HasActiveHarvester(mineralTag);
            }

            // Free right now -> send immediately; otherwise stage until the active
            // harvester has ACCUMULATED StagedReleaseHarvestFrames harvest frames
            // (owner spec 2026-10-20 20:09 "All Workers needs to stage until the
            // active mineral has 32 Harvest frames" — Y4 was leaving YB because it
            // was told to harvest too soon under the old near-completion lead).
            return !_harvestFrameTracker.HasActiveHarvester(mineralTag)
                || _harvestFrameTracker.HasAccumulatedReleaseFrames(mineralTag);
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

            // --naturaltest (owner spec 2026-10-28 14:28 "Do not give workers
            // instructions for this test"): the T1 return-cargo interrupt is a
            // worker instruction — suppressed along with everything else.
            if (Settings.NaturalObservationMode)
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
                // Owner spec 2026-10-19 (V5 first-return bounce): spread applies
                // here too — the interrupt return is exactly a first-return
                // deposit, the most crowded moment of the game.
                if (!_returnInterruptSmFirstTripUsed.Contains(assignedWorker.UnitID)
                    && HasNonZeroPoint(target.SmReturnPoint))
                {
                    _returnInterruptSmFirstTripUsed.Add(assignedWorker.UnitID);
                    var spreadSmPoint = SpreadReturnPoint(assignedWorker, target.SmReturnPoint);
                    Console.WriteLine($"[RETURN INTERRUPT] frame={_currentFrame} worker={assignedWorker.UnitID} first-return=SM point=({spreadSmPoint.X:F4},{spreadSmPoint.Y:F4})");
                    return spreadSmPoint;
                }

                if (target.ReturnPoint != null)
                {
                    return SpreadReturnPoint(assignedWorker, target.ReturnPoint);
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
            if (FreezeBotOnMacroHatcheryMilestone)
            {
                Settings.MacroHatcheryTestFrozen = true;
            }
            _b3MhBanner = $"MACRO HATCHERY {result} — {detail}";
            Console.WriteLine($"[MACRO HATCHERY TEST] *** {result} *** {detail} frame={_currentFrame}{(FreezeBotOnMacroHatcheryMilestone ? " — BOT FROZEN (unit commands stripped; debug draws alive)" : " — game continues (freeze disabled)")}");
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
                    // Bump-viz live target latch (owner request 2026-10-27): the
                    // last move/gather target per choreo worker, drawn as a
                    // current-position line + cross hatch every frame.
                    if (workerLabel == "T1" || workerLabel == "T4" || workerLabel == "T5")
                    {
                        if (command.AbilityId == (int)Abilities.MOVE && command.TargetWorldSpacePos != null)
                        {
                            _bumpVizLiveTargets[workerLabel] = (command.TargetWorldSpacePos.X, command.TargetWorldSpacePos.Y, false);
                        }
                        else if ((command.AbilityId == (int)Abilities.HARVEST_GATHER || command.AbilityId == (int)Abilities.SMART)
                            && command.TargetUnitTag != 0)
                        {
                            var unitTargetPos = LivePositionOfUnitTag(command.TargetUnitTag);
                            if (unitTargetPos != null)
                            {
                                _bumpVizLiveTargets[workerLabel] = (unitTargetPos.X, unitTargetPos.Y, command.AbilityId == (int)Abilities.HARVEST_GATHER);
                            }
                        }
                    }
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
