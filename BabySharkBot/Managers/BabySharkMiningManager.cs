using SC2APIProtocol;
using Sharky;
using Sharky.Extensions;
using Sharky.Managers;
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
    /// Handles map data initialization, worker mining coordination, and custom debug drawing.
    /// Provides visualization for:
    /// - Worker labels with names, roles, and targets
    /// - Center of mass (minerals and vespene clusters)
    /// - Worker instructions (arrows/lines showing where workers are headed)
    /// </summary>
    public class BabySharkMiningManager : IManager
    {
        public bool NeverSkip { get; set; } = false;
        public bool SkipFrame { get; set; } = false;
        public double LongestFrame { get; set; } = 0;
        public double TotalFrameTime { get; set; } = 0;

        private InitialMapData _initialMapData;
        private SecondaryMapData _secondaryMapData;
        private OngoingMapData _ongoingMapData;
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
        private MawBaseLocationData _mapData;  // Store loaded map data for visualization
        private readonly chrisCrossAppleSause _ccaMiningService;
        private int _lastMineralReturnRateConsoleFrame = -999999;
        private bool _printedTwelveDroneMilestone = false;
        private bool _pausedAfterWorkerInstructions = false;
        private bool _didInitialLabelBreak = false;
        private int _workerInstructionDrawCount = 0;
        private bool _spawnLabelDebugBreakTriggered = false;
        private int _currentFrame = -1;
        private int _pauseUntilFrame = -1;
        private bool _forceCcaOnce = false;
        private bool _handoffBreakTriggered = false;
        
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
        private readonly Dictionary<ulong, JitWorkerState> _jitWorkerStates = new Dictionary<ulong, JitWorkerState>();
        private Dictionary<ulong, bool> _workerLastMinedA = new Dictionary<ulong, bool>();
        private Dictionary<string, List<MineralNode>> _expansionMinerals = new Dictionary<string, List<MineralNode>>();
        private Dictionary<string, List<MiningTeam>> _expansionTeams = new Dictionary<string, List<MiningTeam>>();
        private Dictionary<ulong, string> _workerTeamAssignment = new Dictionary<ulong, string>();

        // Event raised when initial mining has been started (CCA handed off)
        public event System.Action OnMiningStarted;

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

        public BabySharkMiningManager(WorkerLabelService workerLabelService = null, CrosshairService crosshairService = null, MineralLabelService mineralLabelService = null, VespeneLabelService vespeneLabelService = null, ExpansionCOMService expansionCOMService = null, ExpansionPointService expansionPointService = null, ExpansionPointDrawService expansionPointDrawService = null, ProvisionalExpansionService provisionalExpansionService = null, MineralReturnRateTrackerService mineralReturnRateTrackerService = null, FrameToTimeConverter frameToTimeConverter = null, Sharky.Pathing.MapDataService mapDataService = null, SpawningPoolPlacementService spawningPoolPlacementService = null, chrisCrossAppleSause ccaMiningService = null)
        {
            _initialMapData = new InitialMapData();
            _secondaryMapData = new SecondaryMapData();
            _ongoingMapData = new OngoingMapData();
            _workerLabelService = workerLabelService;
            _crosshairService = crosshairService;
            _mineralLabelService = mineralLabelService;
            _vespeneLabelService = vespeneLabelService;
            _expansionCOMService = expansionCOMService;
            _expansionPointService = expansionPointService;
            _expansionPointDrawService = expansionPointDrawService;
            _provisionalExpansionService = provisionalExpansionService;
            _mineralReturnRateTrackerService = mineralReturnRateTrackerService;
            _frameToTimeConverter = frameToTimeConverter;
            _mapDataService = mapDataService;
            _spawningPoolPlacementService = spawningPoolPlacementService;
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
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, String opponentId)
        {
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
                        if (mineral?.Position == null) continue;
                        var color = mineral.Index switch { 1 or 2 => new Color { R = 0, G = 255, B = 255 }, 3 or 4 => new Color { R = 255, G = 0, B = 255 }, 5 or 6 => new Color { R = 0, G = 0, B = 255 }, 7 or 8 => new Color { R = 255, G = 255, B = 0 }, _ => new Color { R = 255, G = 255, B = 255 } };
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

        private void DrawWorkerLabels(ResponseObservation observation)
        {
            if (_workerLabelService == null || observation?.Observation?.RawData?.Units == null) return;
            var workers = observation.Observation.RawData.Units.Where(u => u != null && u.Alliance == Alliance.Self && (u.UnitType == (uint)UnitTypes.ZERG_DRONE || u.UnitType == (uint)UnitTypes.TERRAN_SCV || u.UnitType == (uint)UnitTypes.PROTOSS_PROBE)).ToList();
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var storedWorkers = GetStoredWorkersForStart(startIndex);
            var fallbackByPosition = BuildWorkerLabelFallbackMap(storedWorkers);
            var finalLabelByTag = BuildWorkerFinalLabelMap(storedWorkers);
            foreach (var worker in workers)
            {
                var label = _workerLabelService.GetLabel(worker.Tag);
                if (string.IsNullOrWhiteSpace(label) || IsLegacyWorkerLabelForDebugBreak(label)) label = ResolveWorkerFinalLabelByTag(worker.Tag, finalLabelByTag);
                if (string.IsNullOrWhiteSpace(label) && worker.Pos != null) label = ResolveWorkerLabelByPosition(worker.Pos.X, worker.Pos.Y, fallbackByPosition);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    var color = ProcessVisableUnits.GetFinalLabelColor(label);
                    ManagerDebugService.DrawText(label, new Point { X = worker.Pos.X, Y = worker.Pos.Y, Z = worker.Pos.Z + 0.5f }, color, 12);
                }
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
                var blueColor = new Color { R = 0, G = 0, B = 255 };
                ManagerDebugService.DrawLine(new Point { X = comPos.X - 2f, Y = comPos.Y, Z = comPos.Z }, new Point { X = comPos.X + 2f, Y = comPos.Y, Z = comPos.Z }, blueColor);
                ManagerDebugService.DrawLine(new Point { X = comPos.X, Y = comPos.Y - 2f, Z = comPos.Z }, new Point { X = comPos.X, Y = comPos.Y + 2f, Z = comPos.Z }, blueColor);
            }
        }

        private void DrawWorkerInstructions(ResponseObservation observation)
        {
            if (!ManagerDebugService.IsDebugEnabled || observation?.Observation?.RawData?.Units == null) return;
            var workers = observation.Observation.RawData.Units.Where(u => u != null && u.Alliance == Alliance.Self && (u.UnitType == (uint)UnitTypes.ZERG_DRONE || u.UnitType == (uint)UnitTypes.TERRAN_SCV || u.UnitType == (uint)UnitTypes.PROTOSS_PROBE)).ToList();
            foreach (var worker in workers)
            {
                if (worker?.Pos == null) continue;
                var start = new Point { X = worker.Pos.X, Y = worker.Pos.Y, Z = worker.Pos.Z + 0.25f };
                var end = new Point { X = worker.Pos.X, Y = worker.Pos.Y, Z = worker.Pos.Z + 1.25f };
                DrawArrow(start, end, new Color { R = 255, G = 255, B = 255 });
            }
        }

        public IEnumerable<SC2Action> OnFrame(ResponseObservation observation)
        {
            _currentFrame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            ProcessFrameObservation(observation);

            if (!_handoffBreakTriggered && !Settings.ccaMining && _currentFrame >= 35)
            {
                _handoffBreakTriggered = true;
                Console.WriteLine($"BabySharkMiningManager: Steady-state JIT verified at frame {_currentFrame}");
            }

            var actions = new List<SC2Action>();

            // >>> CRITICAL: During CCA phase we emit ZERO unit commands. <<<
            // CcaManager owns frames 0-35 exclusively.
            if (!Settings.ccaMining)
            {
                actions.AddRange(ExecuteJustInTimeMining(observation));
            }

            UpdateScoutedMinerals(observation);
            UpdateMineralReturnRate(observation);
            PrintMineralReturnRateSummary(observation);
            PrintTwelveDroneMilestone(observation);
            
            // Debug drawing is now handled by DrawOnlyManager (NeverSkip = true)
            // so labels persist even when this manager is skipped.

            return actions;
        }

        public void DrawDebugVisuals(ResponseObservation observation)
        {
            try
            {
                if (!ManagerDebugService.IsDebugEnabled) return;
                DrawWorkerLabels(observation);

                DrawCenterOfMassLocations();
                DrawExpansionCOMCrosshairs();
                DrawCenterOfMass();
                DrawMineralLabels();
                DrawMineralTargetPoints();
                DrawExpansionMineralLabels();
                DrawVespeneLabels();
                DrawExpansionPoints();
                DrawSpawningPoolPlacement();
                DrawWorkerInstructions(observation);
                BreakWhenSpawnLabelsShouldBeVisible(observation);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawDebugVisuals: Error: {ex.Message}");
            }
        }

        public void ProcessFrameObservation(ResponseObservation observation)
        {
            if (observation?.Observation?.RawData?.Units == null) return;
            var workerEntries = ProcessVisableUnits.ProcessVisibleUnits(observation, _workerLabelService, _mineralLabelService, _vespeneLabelService, _spawningPoolPlacementService);
            if (_mapData == null) return;
            var currentStartIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var currentAssignments = OngoingMapData.ResolveTeamAssignments(_mapData, currentStartIndex);
            _ccaMiningService.RecordSpawnObservation(_mapData, currentStartIndex, currentAssignments, _workerLabelService, workerEntries: workerEntries);

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

        private void InitializeJitWorkerStates(List<TeamPatchAssignmentDto> assignments)
        {
            if (assignments == null) return;
            foreach (var assignment in assignments)
            {
                if (assignment?.Workers == null || assignment.Minerals?.Count < 2) continue;
                
                // FIX: Resolve mineralA as the NEAR mineral (IsNear=true) and mineralB as FAR.
                // The list order Minerals[0]/Minerals[1] does NOT reliably indicate near/far.
                var mineralA = assignment.Minerals.FirstOrDefault(m => m.IsNear) ?? assignment.Minerals[0];
                var mineralB = assignment.Minerals.FirstOrDefault(m => !m.IsNear && m != mineralA) ?? assignment.Minerals.Skip(1).FirstOrDefault() ?? mineralA;
                
                foreach (var worker in assignment.Workers)
                {
                    if (worker.UnitTag == 0 || _jitWorkerStates.ContainsKey(worker.UnitTag)) continue;
                    
                    var label = worker.FinalLabel ?? worker.Label ?? string.Empty;
                    // 1 & 3 start on A, 2 starts on B
                    var startsOnA = label.EndsWith("1") || label.EndsWith("3");
                    
                    _jitWorkerStates[worker.UnitTag] = new JitWorkerState
                    {
                        TeamNumber = assignment.TeamNumber,
                        TeamId = assignment.TeamId,
                        CurrentMineralTag = startsOnA ? mineralA.UnitTag : mineralB.UnitTag,
                        AlternateMineralTag = startsOnA ? mineralB.UnitTag : mineralA.UnitTag,
                        CurrentMineralPos = startsOnA ? mineralA.Position : mineralB.Position,
                        AlternateMineralPos = startsOnA ? mineralB.Position : mineralA.Position,
                        WasCarrying = false
                    };
                }
            }
        }

        private List<SC2Action> ExecuteJustInTimeMining(ResponseObservation observation)
        {
            var actions = new List<SC2Action>();
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var teamAssignments = ResolveTeamAssignments(startIndex);
            if (teamAssignments.Count == 0) return actions;
            var selfUnits = observation?.Observation?.RawData?.Units?
                .Where(u => u != null && u.Alliance == Alliance.Self).ToList() ?? new List<Unit>();
            var workers = selfUnits.Where(u =>
                u.UnitType == (uint)UnitTypes.ZERG_DRONE ||
                u.UnitType == (uint)UnitTypes.TERRAN_SCV ||
                u.UnitType == (uint)UnitTypes.PROTOSS_PROBE).ToList();
            if (workers.Count == 0) return actions;

            var townhall = _mapData?.StartingTownHall[startIndex];
            if (townhall == null) return actions;
            var townhallUnit = selfUnits.FirstOrDefault(u =>
                (u.UnitType == (uint)UnitTypes.ZERG_HATCHERY ||
                 u.UnitType == (uint)UnitTypes.TERRAN_COMMANDCENTER ||
                 u.UnitType == (uint)UnitTypes.PROTOSS_NEXUS) &&
                Math.Abs(u.Pos.X - townhall.X) < 1.0f &&
                Math.Abs(u.Pos.Y - townhall.Y) < 1.0f);

            foreach (var assignment in teamAssignments)
            {
                if (assignment?.Workers == null || assignment.Minerals == null || assignment.Minerals.Count == 0)
                    continue;

                var teamWorkers = ResolveCurrentWorkersForTeamRaw(workers, assignment.Workers);
                if (teamWorkers.Count == 0) continue;

                // 3-worker teams = JIT rotation; 2-worker teams = static speed mining
                var isJitTeam = assignment.Workers.Count >= 3;

                foreach (var worker in teamWorkers)
                {
                    var carrying = worker.BuffIds.Any(b => b == 271 || b == 272);
                    _previousCarryingState.TryGetValue(worker.Tag, out var wasCarrying);

                    // Per-worker A/B swap on cargo return transition
                    if (!carrying && wasCarrying)
                    {
                        OnWorkerCargoReturned(worker.Tag);
                    }
                    _previousCarryingState[worker.Tag] = carrying;

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
                        else if (!carrying && _jitWorkerStates.TryGetValue(worker.Tag, out var state))
                        {
                            var nextTargetTag = state.CurrentMineralTag;
                            var mineral = assignment.Minerals.FirstOrDefault(m => m.UnitTag == nextTargetTag);
                            if (mineral != null)
                            {
                                var harvestPos = new Point2D { X = mineral.HarvestPoint.X, Y = mineral.HarvestPoint.Y };
                                if (Distance(worker.Pos.ToPoint2D(), harvestPos) < 0.15f)
                                {
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.HARVEST_GATHER, UnitTags = { worker.Tag }, TargetUnitTag = mineral.UnitTag } } });
                                }
                                else
                                {
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { worker.Tag }, TargetWorldSpacePos = harvestPos } } });
                                }
                            }
                            else
                            {
                                var waitPos = new Point2D { X = assignment.JitWaitPoint.X, Y = assignment.JitWaitPoint.Y };
                                AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { worker.Tag }, TargetWorldSpacePos = waitPos } } });
                            }
                        }
                    }
                    else // Speed Mining for 2-worker teams
                    {
                        if (_jitWorkerStates.TryGetValue(worker.Tag, out var state))
                        {
                            var mineral = assignment.Minerals.FirstOrDefault(m => m.UnitTag == state.CurrentMineralTag);
                            if (mineral == null) continue;

                            if (carrying && townhallUnit != null)
                            {
                                var returnPos = new Point2D { X = mineral.ReturnPoint.X, Y = mineral.ReturnPoint.Y };
                                if (Distance(worker.Pos.ToPoint2D(), returnPos) < 0.2f)
                                {
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.SMART, UnitTags = { worker.Tag }, TargetUnitTag = townhallUnit.Tag } } });
                                }
                                else
                                {
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { worker.Tag }, TargetWorldSpacePos = returnPos } } });
                                }
                            }
                            else if (!carrying)
                            {
                                var harvestPos = new Point2D { X = mineral.HarvestPoint.X, Y = mineral.HarvestPoint.Y };
                                if (Distance(worker.Pos.ToPoint2D(), harvestPos) < 0.2f)
                                {
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.HARVEST_GATHER, UnitTags = { worker.Tag }, TargetUnitTag = mineral.UnitTag } } });
                                }
                                else
                                {
                                    AddAction(actions, new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { worker.Tag }, TargetWorldSpacePos = harvestPos } } });
                                }
                            }
                        }
                    }
                }
            }
            return actions;
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

        private List<Unit> ResolveCurrentWorkersForTeamRaw(List<Unit> allWorkers, List<WorkerEntryDto> teamAssignments)
        {
            var result = new List<Unit>();
            if (allWorkers == null || teamAssignments == null) return result;
            foreach (var assignment in teamAssignments)
            {
                // First attempt: match by UnitTag for high reliability if tag is known
                if (assignment.UnitTag != 0)
                {
                    var workerByTag = allWorkers.FirstOrDefault(u => u != null && u.Tag == assignment.UnitTag);
                    if (workerByTag != null)
                    {
                        result.Add(workerByTag);
                        continue;
                    }
                }

                // Second attempt: match by label strings
                var worker = allWorkers.FirstOrDefault(u => u != null && (string.Equals(_workerLabelService?.GetLabel(u.Tag), assignment.FinalLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(_workerLabelService?.GetLabel(u.Tag), assignment.StartLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(_workerLabelService?.GetLabel(u.Tag), assignment.Label, StringComparison.OrdinalIgnoreCase)));
                if (worker != null) result.Add(worker);
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
            if (Settings.WorkerCount == 12)
            {
                return teamNumber switch { 1 => "T", 2 => "S", 3 => "B", 4 => "Y", _ => string.Empty };
            }
            return teamNumber switch { 1 => "G", 2 => "P", 3 => "O", 4 => "R", _ => string.Empty };
        }

        private SC2Action? IssueMoveToPoint(Unit worker, Vector2Dto point)
        {
            if (worker?.Tag == 0 || point == null) return null;
            return new SC2Action { ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { worker.Tag }, TargetWorldSpacePos = new Point2D { X = point.X, Y = point.Y } } } };
        }

        private static void AddAction(List<SC2Action> actions, SC2Action? action) { if (action != null) actions.Add(action); }

        private ulong ResolveMineralTag(ResponseObservation observation, Vector2Dto position)
        {
            if (observation?.Observation?.RawData?.Units == null || position == null) return 0;
            var nearest = observation.Observation.RawData.Units.Where(u => u != null && IsMineralType((UnitTypes)u.UnitType)).Select(u => new { Unit = u, Distance = Math.Pow(u.Pos.X - position.X, 2) + Math.Pow(u.Pos.Y - position.Y, 2) }).OrderBy(v => v.Distance).FirstOrDefault();
            return (nearest == null || nearest.Distance >= 4) ? 0 : nearest.Unit.Tag;
        }

        private void UpdateScoutedMinerals(ResponseObservation observation)
        {
            if (_mapData?.Minerals == null || _mapData.MineralTagToIndex == null || observation?.Observation?.RawData?.Units == null) return;
            foreach (var unit in observation.Observation.RawData.Units)
            {
                try
                {
                    if (unit?.Pos == null || !IsMineralType((UnitTypes)unit.UnitType) || unit.DisplayType != DisplayType.Visible || unit.Tag == 0 || !_mapData.MineralTagToIndex.TryGetValue(unit.Tag, out var idx)) continue;
                    var mineral = _mapData.Minerals[idx];
                    var contents = unit.HasMineralContents ? unit.MineralContents : 0;
                    if (contents != mineral.MaxMineralContents) _mapData.MismatchedMinerals = true;
                    if (contents > mineral.MaxMineralContents) mineral.MaxMineralContents = contents;
                    if (contents > mineral.MineralContents) mineral.MineralContents = contents;
                    mineral.UnitTag = unit.Tag;
                    mineral.UnitType = unit.UnitType;
                    if (mineral.Position == null) mineral.Position = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z);
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
            if (_mineralReturnRateTrackerService == null || observation?.Observation?.Score?.ScoreDetails == null || observation?.Observation?.RawData?.Units == null) return;
            var droneCount = observation.Observation.RawData.Units.Count(u => u != null && u.Alliance == Alliance.Self && u.UnitType == (uint)UnitTypes.ZERG_DRONE);
            if (droneCount >= 12 && droneCount <= 16)
            {
                var COLLECTION_RATE = observation.Observation.Score.ScoreDetails.CollectionRateMinerals;
                if (COLLECTION_RATE > 0) _mineralReturnRateTrackerService.Record(droneCount, COLLECTION_RATE);
            }
        }

        private void PrintMineralReturnRateSummary(ResponseObservation observation)
        {
            if (_mineralReturnRateTrackerService != null && _currentFrame - _lastMineralReturnRateConsoleFrame >= 500)
            {
                _lastMineralReturnRateConsoleFrame = _currentFrame;
                Console.WriteLine($"Mineral Return Rate Summary: {_mineralReturnRateTrackerService.GetSummary()}");
            }
        }

        private void PrintTwelveDroneMilestone(ResponseObservation observation)
        {
            if (_printedTwelveDroneMilestone || observation?.Observation?.RawData?.Units == null) return;
            if (observation.Observation.RawData.Units.Count(u => u != null && u.Alliance == Alliance.Self && u.UnitType == (uint)UnitTypes.ZERG_DRONE) >= 12)
            {
                _printedTwelveDroneMilestone = true;
                Console.WriteLine($"BabySharkMiningManager: 12-drone milestone reached at frame {_currentFrame}");
            }
        }

        private void BreakWhenSpawnLabelsShouldBeVisible(ResponseObservation observation)
        {
            if (_spawnLabelDebugBreakTriggered || observation?.Observation == null || _currentFrame <= 20) return;
            if (_workerLabelService.GetAllLabels().Any(l => l.Key.StartsWith("T") || l.Key.StartsWith("S") || l.Key.StartsWith("B") || l.Key.StartsWith("Y")))
            {
                _spawnLabelDebugBreakTriggered = true;
                Console.WriteLine($"BabySharkMiningManager: Spawn labels detected at frame {_currentFrame}.");
            }
        }

        private void DrawCenterOfMass()
        {
            if (_mapData != null) foreach (var com in _mapData.MineralCenterOfMass) DrawCircle(com, 0.5f, new Color { R = 0, G = 255, B = 0 }, 12f);
        }

        private void DrawMineralLabels()
        {
            if (_mineralLabelService != null) foreach (var kvp in _mineralLabelService.GetAllMineralLabels()) ManagerDebugService.DrawText(kvp.Value.Label, new Point { X = kvp.Value.Position.X, Y = kvp.Value.Position.Y, Z = kvp.Value.Position.Z + 0.5f }, kvp.Value.Color, 10);
        }

        private void DrawExpansionMineralLabels()
        {
            if (_mapData?.ExpansionMineralLabels != null) foreach (var kvp in _mapData.ExpansionMineralLabels) { var pos = ParsePoint(kvp.Key); ManagerDebugService.DrawText(kvp.Value, new Point { X = pos.X, Y = pos.Y, Z = 12.5f }, new Color { R = 255, G = 255, B = 255 }, 10); }
        }

        private void DrawVespeneLabels()
        {
            if (_vespeneLabelService != null) foreach (var kvp in _vespeneLabelService.GetAllVespeneLabels()) ManagerDebugService.DrawText(kvp.Value.Label, new Point { X = kvp.Value.Position.X, Y = kvp.Value.Position.Y, Z = kvp.Value.Position.Z + 0.5f }, kvp.Value.Color, 10);
        }

        private void DrawExpansionPoints()
        {
            if (_mapData?.ExpansionPoints != null) foreach (var kvp in _mapData.ExpansionPoints) { DrawCircle(kvp.Value.ExpansionPoint, 2.5f, new Color { R = 0, G = 0, B = 255 }, 12f); ManagerDebugService.DrawText($"Exp {kvp.Key}", new Point { X = kvp.Value.ExpansionPoint.X, Y = kvp.Value.ExpansionPoint.Y, Z = 13f }, new Color { R = 255, G = 255, B = 255 }, 12); }
        }

        private void DrawSpawningPoolPlacement()
        {
            if (_mapData?.SpawningPoolPlacements != null) foreach (var pos in _mapData.SpawningPoolPlacements) { DrawCircle(pos, 1.5f, new Color { R = 255, G = 0, B = 0 }, 12f); ManagerDebugService.DrawText("Pool", new Point { X = pos.X, Y = pos.Y, Z = 12.5f }, new Color { R = 255, G = 0, B = 0 }, 10); }
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

        private int GetActiveStartIndex(ResponseObservation observation)
        {
            if (_mapData?.StartingTownHall == null) return -1;
            var selfTownhalls = observation.Observation.RawData.Units.Where(u => u.Alliance == Alliance.Self && (u.UnitType == (uint)UnitTypes.ZERG_HATCHERY || u.UnitType == (uint)UnitTypes.TERRAN_COMMANDCENTER || u.UnitType == (uint)UnitTypes.PROTOSS_NEXUS));
            foreach (var townhall in selfTownhalls) for (int i = 0; i < _mapData.StartingTownHall.Length; i++) if (Math.Abs(townhall.Pos.X - _mapData.StartingTownHall[i].X) < 1.0f && Math.Abs(townhall.Pos.Y - _mapData.StartingTownHall[i].Y) < 1.0f) return i;
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

        private List<WorkerEntryDto> GetLiveWorkers(ResponseObservation observation, int startIndex)
        {
            var results = new List<WorkerEntryDto>();
            var units = observation?.Observation?.RawData?.Units;
            if (units == null) return results;
            foreach (var u in units.Where(u => u != null && u.Alliance == Alliance.Self && (u.UnitType == (uint)UnitTypes.ZERG_DRONE || u.UnitType == (uint)UnitTypes.TERRAN_SCV || u.UnitType == (uint)UnitTypes.PROTOSS_PROBE)))
            {
                var label = _workerLabelService.GetLabel(u.Tag) ?? "";
                results.Add(new WorkerEntryDto { UnitTag = u.Tag, UnitType = u.UnitType, Position = new Vector2Dto(u.Pos.X, u.Pos.Y, u.Pos.Z), Label = label, StartLabel = label, FinalLabel = label });
            }
            return results;
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
            Console.WriteLine($"BabySharkMiningManager: OnEnd called with result: {result}");
        }
    }
}
