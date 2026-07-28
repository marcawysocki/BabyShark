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
        private readonly Dictionary<ulong, bool> _previousCarryingState = new Dictionary<ulong, bool>();
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
            
            // Map OrderedMineral back to Unit list for the generic initializer
            // Note: We only have tags and positions in OrderedMineral, so we create dummy Units with enough info
            var dummyMinerals = mainMinerals.Select(om => new Unit { 
                Tag = 0, // Tag is unknown at this phase of map data loading
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

        /// <summary>
        /// Called at the start of the game. Checks if map data was successfully loaded.
        /// If not, initializes map data from the current game observation.
        /// </summary>
        private void DrawMineralTargetPoints()
        {
            if (!ManagerDebugService.IsDebugEnabled || _mapData?.OrderedMainMinerals == null)
            {
                return;
            }

            try
            {
                const float debugHeight = 12f;

                for (var startIndex = 0; startIndex < _mapData.OrderedMainMinerals.Count; startIndex++)
                {
                    var orderedList = _mapData.OrderedMainMinerals[startIndex];
                    if (orderedList == null)
                    {
                        continue;
                    }

                    var hatcheryPosition = _mapData.StartingTownHall != null && _mapData.StartingTownHall.Length > startIndex
                        ? _mapData.StartingTownHall[startIndex]
                        : null;

                    if (hatcheryPosition != null)
                    {
                        DrawCircle(hatcheryPosition, 2.75f, new Color { R = 255, G = 255, B = 255 }, debugHeight);
                        ManagerDebugService.DrawText("H", new Point { X = hatcheryPosition.X, Y = hatcheryPosition.Y, Z = debugHeight }, new Color { R = 255, G = 255, B = 255 }, 10);
                    }

                    foreach (var mineral in orderedList)
                    {
                        if (mineral?.Position == null)
                        {
                            continue;
                        }

                        var color = mineral.Index switch
                        {
                            1 or 2 => new Color { R = 0, G = 255, B = 255 },
                            3 or 4 => new Color { R = 255, G = 0, B = 255 },
                            5 or 6 => new Color { R = 0, G = 0, B = 255 },
                            7 or 8 => new Color { R = 255, G = 255, B = 0 },
                            _ => new Color { R = 255, G = 255, B = 255 }
                        };

                        DrawCircle(mineral.Position, 1.0f, color, debugHeight);

                        if (hatcheryPosition != null)
                        {
                            var hatcheryPoint = new Point
                            {
                                X = hatcheryPosition.X,
                                Y = hatcheryPosition.Y,
                                Z = debugHeight
                            };

                            var mineralPoint = new Point
                            {
                                X = mineral.Position.X,
                                Y = mineral.Position.Y,
                                Z = debugHeight
                            };

                            ManagerDebugService.DrawLine(hatcheryPoint, mineralPoint, color);
                        }

                        var harvestPoint = new Point
                        {
                            X = mineral.HarvestPoint.X,
                            Y = mineral.HarvestPoint.Y,
                            Z = debugHeight
                        };

                        var returnPoint = new Point
                        {
                            X = mineral.ReturnPoint.X,
                            Y = mineral.ReturnPoint.Y,
                            Z = debugHeight
                        };

                        var smHarvestPoint = new Point
                        {
                            X = mineral.SmHarvestPoint.X,
                            Y = mineral.SmHarvestPoint.Y,
                            Z = mineral.SmHarvestPoint.Z + 0.5f
                        };

                        var smReturnPoint = new Point
                        {
                            X = mineral.SmReturnPoint.X,
                            Y = mineral.SmReturnPoint.Y,
                            Z = mineral.SmReturnPoint.Z + 0.5f
                        };

                        ManagerDebugService.DrawText("h", harvestPoint, color, 10);
                        ManagerDebugService.DrawText("r", returnPoint, color, 10);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawMineralTargetPoints: Error drawing mineral target points: {ex.Message}");
            }
        }

        private void DrawCircle(Vector2Dto center, float radius, Color color, float z, int segments = 24)
        {
            if (center == null || segments < 3)
            {
                return;
            }

            var step = Math.PI * 2.0 / segments;
            Point previous = null;

            for (var i = 0; i <= segments; i++)
            {
                var angle = i * step;
                var point = new Point
                {
                    X = center.X + (float)(Math.Cos(angle) * radius),
                    Y = center.Y + (float)(Math.Sin(angle) * radius),
                    Z = z
                };

                if (previous != null)
                {
                    ManagerDebugService.DrawLine(previous, point, color);
                }

                previous = point;
            }
        }

        private void DrawWorkerLabels(ResponseObservation observation)
        {
            if (_workerLabelService == null || observation?.Observation?.RawData?.Units == null)
            {
                return;
            }

            var frame = observation.Observation.GameLoop;
            if (frame == 0 || frame == 5)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawWorkerLabels: Frame {frame} processing start.");
            }

            var workers = observation.Observation.RawData.Units.Where(u =>
                u != null && u.Alliance == Alliance.Self && (
                    u.UnitType == (uint)UnitTypes.ZERG_DRONE ||
                    u.UnitType == (uint)UnitTypes.TERRAN_SCV ||
                    u.UnitType == (uint)UnitTypes.PROTOSS_PROBE
                )).ToList();

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var storedWorkers = GetStoredWorkersForStart(startIndex);
            var fallbackByPosition = BuildWorkerLabelFallbackMap(storedWorkers);
            var finalLabelByTag = BuildWorkerFinalLabelMap(storedWorkers);
            var white = new Color { R = 255, G = 255, B = 255 };

            foreach (var worker in workers)
            {
                try
                {
                    var label = _workerLabelService.GetLabel(worker.Tag);
                    var source = "Service";

                    if (string.IsNullOrWhiteSpace(label) || IsLegacyWorkerLabelForDebugBreak(label))
                    {
                        label = ResolveWorkerFinalLabelByTag(worker.Tag, finalLabelByTag);
                        source = "FinalLabelMap";
                    }

                    if (string.IsNullOrWhiteSpace(label) && worker.Pos != null)
                    {
                        label = ResolveWorkerLabelByPosition(worker.Pos.X, worker.Pos.Y, fallbackByPosition);
                        source = "PositionFallback";
                    }

                    if (frame == 0 || frame == 5)
                    {
                        Console.WriteLine($"  - Draw: Tag={worker.Tag}, Label='{label}', Source={source}, Pos=({worker.Pos.X:F2},{worker.Pos.Y:F2})");
                    }

                    if (string.IsNullOrWhiteSpace(label) || worker.Pos == null)
                    {
                        continue;
                    }

                    ManagerDebugService.DrawText(label, new Point { X = worker.Pos.X, Y = worker.Pos.Y, Z = worker.Pos.Z + 0.5f }, white, 12);

                    if (IsLegacyWorkerLabelForDebugBreak(label) && GetWorkerLabelOrderingCompleted(startIndex))
                    {
                        //Debugger.Break();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"BabySharkMiningManager.DrawWorkerLabels: Error drawing worker label for tag {worker.Tag}: {ex.Message}");
                }
            }
        }

        private void DrawCenterOfMassLocations()
        {
            if (!ManagerDebugService.IsDebugEnabled || _crosshairService == null)
            {
                return;
            }

            try
            {
                var allCOMs = _crosshairService.GetAllCOMs();
                foreach (var kvp in allCOMs)
                {
                    var comPos = kvp.Value?.Position;
                    var color = kvp.Value?.Color ?? new Color { R = 255, G = 255, B = 255 };
                    if (comPos == null)
                    {
                        continue;
                    }

                    ManagerDebugService.DrawLine(new Point { X = comPos.X - 2f, Y = comPos.Y, Z = comPos.Z }, new Point { X = comPos.X + 2f, Y = comPos.Y, Z = comPos.Z }, color);
                    ManagerDebugService.DrawLine(new Point { X = comPos.X, Y = comPos.Y - 2f, Z = comPos.Z }, new Point { X = comPos.X, Y = comPos.Y + 2f, Z = comPos.Z }, color);
                    ManagerDebugService.DrawSphere(comPos, 0.5f, color);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawCenterOfMassLocations: Error: {ex.Message}");
            }
        }

        private void DrawExpansionCOMCrosshairs()
        {
            if (!ManagerDebugService.IsDebugEnabled || _expansionCOMService == null)
            {
                return;
            }

            try
            {
                var expansionCOMs = _expansionCOMService.Get();
                foreach (var kvp in expansionCOMs)
                {
                    var comPos = kvp.Value;
                    var blueColor = new Color { R = 0, G = 0, B = 255 };
                    ManagerDebugService.DrawLine(new Point { X = comPos.X - 2f, Y = comPos.Y, Z = comPos.Z }, new Point { X = comPos.X + 2f, Y = comPos.Y, Z = comPos.Z }, blueColor);
                    ManagerDebugService.DrawLine(new Point { X = comPos.X, Y = comPos.Y - 2f, Z = comPos.Z }, new Point { X = comPos.X, Y = comPos.Y + 2f, Z = comPos.Z }, blueColor);
                    ManagerDebugService.DrawSphere(comPos, 0.75f, blueColor);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawExpansionCOMCrosshairs: Error: {ex.Message}");
            }
        }

        private void DrawWorkerInstructions(ResponseObservation observation)
        {
            if (!ManagerDebugService.IsDebugEnabled || observation?.Observation?.RawData?.Units == null)
            {
                return;
            }

            try
            {
                // Keep the original debug flow alive: this method is intentionally conservative and only renders labels/paths.
                var workers = observation.Observation.RawData.Units.Where(u =>
                    u != null && u.Alliance == Alliance.Self && (
                        u.UnitType == (uint)UnitTypes.ZERG_DRONE ||
                        u.UnitType == (uint)UnitTypes.TERRAN_SCV ||
                        u.UnitType == (uint)UnitTypes.PROTOSS_PROBE
                    )).ToList();

                foreach (var worker in workers)
                {
                    if (worker?.Pos == null)
                    {
                        continue;
                    }

                    var start = new Point { X = worker.Pos.X, Y = worker.Pos.Y, Z = worker.Pos.Z + 0.25f };
                    var end = new Point { X = worker.Pos.X, Y = worker.Pos.Y, Z = worker.Pos.Z + 1.25f };
                    DrawArrow(start, end, new Color { R = 255, G = 255, B = 255 });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawWorkerInstructions: Error: {ex.Message}");
            }
        }

        private Dictionary<string, List<MineralNode>> _expansionMinerals = new Dictionary<string, List<MineralNode>>();
        private Dictionary<string, List<MiningTeam>> _expansionTeams = new Dictionary<string, List<MiningTeam>>();
        private Dictionary<ulong, string> _workerTeamAssignment = new Dictionary<ulong, string>();

        public void InitializeExpansionMining(Point2D expansionPosition, List<Unit> minerals)
        {
            var expansionKey = GetPointKey(expansionPosition);
            
            // Create mineral nodes with proper ordering
            var mineralNodes = CreateOrderedMineralNodes(expansionPosition, minerals);
            _expansionMinerals[expansionKey] = mineralNodes;

            // Create teams based on mineral pairs
            var teams = new List<MiningTeam>();
            
            // Pair up minerals (M1-M2, M3-M4, etc.)
            for (int i = 0; i < mineralNodes.Count - 1; i += 2)
            {
                var team = new MiningTeam
                {
                    TeamId = $"{expansionPosition.X:F1}_{expansionPosition.Y:F1}_T{i/2 + 1}",
                    MineralA = mineralNodes[i],
                    MineralB = mineralNodes[i + 1],
                    IsJITTeam = false,
                    ExpansionPosition = new Vector2Dto(expansionPosition.X, expansionPosition.Y),
                    TeamIndex = i/2
                };
                teams.Add(team);
            }

            // Handle odd mineral
            if (mineralNodes.Count % 2 != 0)
            {
                var lastTeam = new MiningTeam
                {
                    TeamId = $"{expansionPosition.X:F1}_{expansionPosition.Y:F1}_T{mineralNodes.Count/2 + 1}",
                    MineralA = mineralNodes.Last(),
                    MineralB = null,
                    IsJITTeam = false,
                    ExpansionPosition = new Vector2Dto(expansionPosition.X, expansionPosition.Y),
                    TeamIndex = mineralNodes.Count/2
                };
                teams.Add(lastTeam);
            }
            
            _expansionTeams[expansionKey] = teams;
        }

        private string GetPointKey(Point2D point)
        {
            return $"{(float)System.Math.Round(point.X, 1)},{(float)System.Math.Round(point.Y, 1)}";
        }

        private List<MineralNode> CreateOrderedMineralNodes(Point2D expansionPosition, List<Unit> minerals)
        {
            var mineralNodes = new List<MineralNode>();
            
            foreach (var mineral in minerals)
            {
                var distance = Distance(expansionPosition, mineral.Pos.ToPoint2D());
                var node = new MineralNode
                {
                    Position = new Vector2Dto(mineral.Pos.X, mineral.Pos.Y, mineral.Pos.Z),
                    MineralUnitTag = mineral.Tag,
                    IsLargeMineral = IsRichMineral(mineral.UnitType),
                    AngleFromCenter = CalculateAngleFromCenter(expansionPosition, mineral.Pos.ToPoint2D()),
                    DistanceFromTownHall = distance,
                    IsNearMineral = distance < 10.0f
                };
                mineralNodes.Add(node);
            }

            // Sort by angle to get clockwise ordering
            mineralNodes.Sort((a, b) => a.AngleFromCenter.CompareTo(b.AngleFromCenter));
            
            // Assign identifiers M1, M2, etc.
            for (int i = 0; i < mineralNodes.Count; i++)
            {
                mineralNodes[i].Identifier = $"M{i + 1}";
            }
            
            return mineralNodes;
        }

        private bool IsRichMineral(uint unitType)
        {
            var unitTypeStr = ((UnitTypes)unitType).ToString();
            return unitTypeStr.Contains("RICH", StringComparison.OrdinalIgnoreCase);
        }

        private float CalculateAngleFromCenter(Point2D center, Point2D point)
        {
            float deltaX = point.X - center.X;
            float deltaY = point.Y - center.Y;
            return (float)System.Math.Atan2(deltaY, deltaX);
        }

        private float Distance(Point2D a, Point2D b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (float)System.Math.Sqrt(dx * dx + dy * dy);
        }

        public void AssignWorkerToJITTeam(Unit worker, Point2D expansionPosition)
        {
            var expansionKey = GetPointKey(expansionPosition);
            
            if (!_expansionTeams.ContainsKey(expansionKey))
                return;

            var teams = _expansionTeams[expansionKey];
            if (teams.Count == 0)
                return;

            // Find the team with the fewest workers
            var targetTeam = teams.OrderBy(t => t.WorkerTags.Count).First();
            
            // Check if worker is already assigned to this team
            if (!targetTeam.WorkerTags.Contains(worker.Tag))
            {
                targetTeam.WorkerTags.Add(worker.Tag);
                _workerTeamAssignment[worker.Tag] = targetTeam.TeamId;
            }

            // Check if this team now has 3 workers and should switch to JIT mining
            if (targetTeam.WorkerTags.Count == 3 && !targetTeam.IsJITTeam)
            {
                targetTeam.IsJITTeam = true;
                // Initialize worker tracking
                foreach (var tag in targetTeam.WorkerTags)
                {
                    if (!targetTeam.WorkerLastMinedA.ContainsKey(tag))
                    {
                        targetTeam.WorkerLastMinedA[tag] = false;
                    }
                }
            }
        }

        private Dictionary<string, MiningTeamState> _teamMiningStates = new Dictionary<string, MiningTeamState>();

        private class MiningTeamState
        {
            public ulong MineralA_Worker { get; set; }
            public ulong MineralB_Worker { get; set; }
            public ulong WaitingWorker { get; set; }
            public Dictionary<ulong, bool> LastMinedA { get; set; } = new Dictionary<ulong, bool>();
        }

        public Point2D GetJITMiningTarget(Unit worker, Point2D expansionPosition, Point2D currentMineralPosition)
        {
            var expansionKey = GetPointKey(expansionPosition);
            
            if (!_expansionTeams.ContainsKey(expansionKey))
                return currentMineralPosition;

            var team = FindWorkerTeam(worker, expansionKey);
            if (team == null || team.Workers.Count != 3)
                return currentMineralPosition;

            if (!_teamMiningStates.TryGetValue(team.TeamId, out var state))
            {
                state = new MiningTeamState();
                var workers = team.WorkerTags;
                state.MineralA_Worker = workers[0];
                state.MineralB_Worker = workers[1];
                state.WaitingWorker = workers[2];
                foreach(var w in workers) state.LastMinedA[w] = false;
                _teamMiningStates[team.TeamId] = state;
            }

            // Determine if worker just returned cargo
            // This is usually handled by the Task checking Buffs, 
            // but here we just need to return the next target.
            
            if (worker.Tag == state.MineralA_Worker) return team.MineralA?.Position != null ? new Point2D { X = team.MineralA.Position.X, Y = team.MineralA.Position.Y } : currentMineralPosition;
            if (worker.Tag == state.MineralB_Worker) return team.MineralB?.Position != null ? new Point2D { X = team.MineralB.Position.X, Y = team.MineralB.Position.Y } : currentMineralPosition;
            
            // If waiting worker, return the Wait Point
            return new Point2D { X = team.JitWaitPoint.X, Y = team.JitWaitPoint.Y };
        }

        public void RegisterCargoReturn(ulong workerTag, string teamId)
        {
            if (!_teamMiningStates.TryGetValue(teamId, out var state)) return;
            
            // When a worker returns cargo, they switch roles with the waiting worker
            if (workerTag == state.MineralA_Worker)
            {
                state.LastMinedA[workerTag] = true;
                var switcher = state.WaitingWorker;
                state.WaitingWorker = workerTag;
                state.MineralA_Worker = switcher;
            }
            else if (workerTag == state.MineralB_Worker)
            {
                state.LastMinedA[workerTag] = false;
                var switcher = state.WaitingWorker;
                state.WaitingWorker = workerTag;
                state.MineralB_Worker = switcher;
            }
        }

        private MiningTeam FindWorkerTeam(Unit worker, string expansionKey)
        {
            if (!_expansionTeams.ContainsKey(expansionKey))
                return null;

            if (!_workerTeamAssignment.ContainsKey(worker.Tag))
                return null;

            var teamId = _workerTeamAssignment[worker.Tag];
            
            foreach (var team in _expansionTeams[expansionKey])
            {
                if (team.TeamId == teamId)
                    return team;
            }
            return null;
        }

        public bool IsJITTeamWorker(Unit worker)
        {
            return _workerTeamAssignment.ContainsKey(worker.Tag);
        }

        public bool ShouldUseJITMining(Unit worker)
        {
            if (!_workerTeamAssignment.ContainsKey(worker.Tag))
                return false;

            // Find the team this worker belongs to
            foreach (var kvp in _expansionTeams)
            {
                foreach (var team in kvp.Value)
                {
                    if (team.WorkerTags.Contains(worker.Tag))
                    {
                        return team.IsJITTeam;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Called every frame to handle mining-related actions.
        /// Draws worker labels, center of mass, and worker instruction arrows using Sharky's debug drawing.
        /// </summary>
        public IEnumerable<SC2Action> OnFrame(ResponseObservation observation)
        {
            // Debug break to verify BabySharkMiningManager is executing
            if (!Settings.ccaMining && _currentFrame >= 35 && !_handoffBreakTriggered)
            {
                // This will trigger on the very first frame after takeover
                Console.WriteLine("BabySharkMiningManager: Entering takeover frame loop.");
                try { System.Diagnostics.Debugger.Break(); } catch { }
            }

            ConsecrationofMyStarCraftIIBotProject.Invoke();
            _currentFrame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            // If we were paused for debug draw, but the pause expiration has passed, resume drawing and logic
            List<SC2Action> resumeActions = null;
            if (_pausedAfterWorkerInstructions && _pauseUntilFrame >= 0 && _currentFrame >= _pauseUntilFrame)
            {
                _pausedAfterWorkerInstructions = false;
                _pauseUntilFrame = -1;
                Console.WriteLine($"BabySharkMiningManager: resuming after debug-draw pause at frame {_currentFrame}");
                // Trigger the cca service once immediately after resuming so it can re-evaluate and issue orders.
                try
                {
                    var resumeStartIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                    var resumeLiveWorkers = GetLiveWorkers(observation, resumeStartIndex);
                    var ra = _ccaMiningService.BuildBumpOrders(_currentFrame, _mapData, resumeStartIndex, resumeLiveWorkers);
                    if (ra != null && ra.Any())
                    {
                        Console.WriteLine("BabySharkMiningManager: cca service returned actions on resume");
                        resumeActions = ra.ToList();
                    }
                    else
                    {
                        // If no actions returned (frame gating), force a cca run on the next RunJustInTimeMining regardless of frame%5
                        _forceCcaOnce = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"BabySharkMiningManager: error invoking cca service on resume: {ex.Message}");
                }
            }
            ProcessFrameObservation(observation);

            if (!_handoffBreakTriggered && !Settings.ccaMining && _currentFrame >= 35)
            {
                _handoffBreakTriggered = true;
                Console.WriteLine($"BabySharkMiningManager: Takeover verified at frame {_currentFrame}. Triggering handoff verification break.");
                try { System.Diagnostics.Debugger.Break(); } catch { }
            }

            // If resume triggered cca actions, return them immediately so they execute this frame.
            if (resumeActions != null && resumeActions.Count > 0)
            {
                return resumeActions.Cast<SC2Action>().ToList();
            }

            var actions = new List<SC2Action>();
            if (Settings.ccaMining)
            {
                // In CCA mode (frames 0-35), RunJustInTimeMining (which calls CCA service) handles bumping.
                actions.AddRange(RunJustInTimeMining(observation));
            }
            else
            {
                // After frame 35, once ccaMining is false, ExecuteJustInTimeMining takes over steady state JIT rotations.
                // WE MUST ALWAYS ENTER THIS BRANCH AFTER THE HANDOFF.
                actions.AddRange(ExecuteJustInTimeMining(observation));
            }

            UpdateScoutedMinerals(observation);
            UpdateMineralReturnRate(observation);
            PrintMineralReturnRateSummary(observation);
            PrintTwelveDroneMilestone(observation);

            if (ManagerDebugService.IsDebugEnabled && observation?.Observation?.RawData?.Units != null)
            {
                try
                {
                    if (!_didInitialLabelBreak && _currentFrame > 10)
                    {
                        _didInitialLabelBreak = true;
                        Console.WriteLine("BabySharkMiningManager: Triggering initial label debug break.");
                        System.Diagnostics.Debugger.Break();
                    }

                    var workers = observation.Observation.RawData.Units.Where(u =>
                        u != null && u.Alliance == Alliance.Self && (
                            u.UnitType == (uint)UnitTypes.ZERG_DRONE ||
                            u.UnitType == (uint)UnitTypes.TERRAN_SCV ||
                            u.UnitType == (uint)UnitTypes.PROTOSS_PROBE
                        )
                    ).ToList();

                    Console.WriteLine($"BabySharkMiningManager.OnFrame: Found {workers.Count} workers, drawing labels");

                    this.DrawWorkerLabels(observation);
                    this.DrawCenterOfMassLocations();
                    this.DrawExpansionCOMCrosshairs();
                    this.DrawCenterOfMass();
                    this.DrawMineralLabels();
                    this.DrawMineralTargetPoints();
                    this.DrawExpansionMineralLabels();
                    this.DrawVespeneLabels();
                    this.DrawExpansionPoints();
                    this.DrawSpawningPoolPlacement();
                    this.PauseAfterDebugDraw();
                    this.DrawWorkerInstructions(observation);
                    BreakWhenSpawnLabelsShouldBeVisible(observation);
                    _workerInstructionDrawCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"BabySharkMiningManager: Error drawing debug visualizations: {ex.Message}\n{ex.StackTrace}");
                }
            }

            return actions;
        }

        private List<SC2Action> RunJustInTimeMining(ResponseObservation observation)
        {
            var actions = new List<SC2Action>();
            if (!Settings.ccaMining || observation?.Observation?.RawData?.Units == null || _mapData == null)
            {
                return actions;
            }

            var frame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            
            // The CCA service handles its own frame gating (frame%5) internally if needed, 
            // but we call it every frame to ensure it can update state (like InitialPositions).
            // However, BuildBumpOrders returns commands only on 5th frames.
            try
            {
                var startIndex = GetActiveStartIndex(observation);
                var liveWorkers = GetLiveWorkers(observation, startIndex);
                var ccaActions = _ccaMiningService.BuildBumpOrders(frame, _mapData, startIndex, liveWorkers);
                if (ccaActions != null && ccaActions.Any())
                {
                    return ccaActions.Cast<SC2Action>().ToList();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager: cca service error: {ex.Message}");
            }

            // While ccaMining is true, we ONLY allow the CCA service to issue commands.
            // If it returns nothing (e.g. frame % 5 != 0), we issue NO actions.
            return actions;
        }

        private List<SC2Action> BuildBumpActions(ResponseObservation observation, int frame, TeamPatchAssignmentDto assignment, OrderedMineral mineralA, OrderedMineral mineralB, List<WorkerEntryDto> liveWorkers)
        {
            var actions = new List<SC2Action>();
            if (assignment == null || mineralA?.Position == null || mineralB?.Position == null || liveWorkers == null || liveWorkers.Count == 0)
            {
                return actions;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var primary = ResolveWorkerBySuffix(assignment.Workers, liveWorkers, 1);
            var secondary = ResolveWorkerBySuffix(assignment.Workers, liveWorkers, 2);
            if (primary?.Position == null || secondary?.Position == null)
            {
                return actions;
            }

            var pairDistance = Distance(primary.Position, secondary.Position);
            if (pairDistance > 0.75f)
            {
                return actions;
            }

            var begin = new Vector2Dto((primary.Position.X + secondary.Position.X) * 0.5f, (primary.Position.Y + secondary.Position.Y) * 0.5f, primary.Position.Z);
            var primaryTarget = new Vector2Dto((begin.X + mineralA.Position.X) * 0.5f, (begin.Y + mineralA.Position.Y) * 0.5f, begin.Z);
            var secondaryTarget = new Vector2Dto((begin.X + primaryTarget.X) * 0.5f, (begin.Y + primaryTarget.Y) * 0.5f, begin.Z);

            AddAction(actions, IssueMoveAction(primary, primaryTarget));
            AddAction(actions, IssueMoveAction(secondary, secondaryTarget));
            return actions;
        }

        private List<SC2Action> BuildFallbackResourceActions(ResponseObservation observation, int frame, TeamPatchAssignmentDto assignment, OrderedMineral mineralA, OrderedMineral mineralB, List<WorkerEntryDto> liveWorkers)
        {
            var actions = new List<SC2Action>();
            if (assignment == null || mineralA?.Position == null || mineralB?.Position == null || liveWorkers == null || liveWorkers.Count == 0)
            {
                return actions;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            foreach (var worker in assignment.Workers ?? new List<WorkerEntryDto>())
            {
                if (worker == null)
                {
                    continue;
                }

                var liveWorker = ResolveWorkerByLabel(worker.FinalLabel ?? worker.Label ?? worker.StartLabel, liveWorkers);
                if (liveWorker?.Position == null)
                {
                    continue;
                }

                var target = ResolveFallbackTarget(assignment.TeamNumber, worker.FinalLabel ?? worker.Label ?? worker.StartLabel, liveWorker.Position, mineralA, mineralB);
                AddAction(actions, IssueMoveAction(liveWorker, target));
            }
            return actions;
        }

        private static Vector2Dto ResolveFallbackTarget(int teamNumber, string workerLabel, Vector2Dto liveWorkerPosition, OrderedMineral mineralA, OrderedMineral mineralB)
        {
            var suffix = GetWorkerSuffix(workerLabel);
            if (suffix == 2 && mineralB?.Position != null)
            {
                return mineralB.Position;
            }

            if (suffix == 1 || suffix == 3)
            {
                return mineralA?.Position ?? liveWorkerPosition;
            }

            if (mineralA?.Position != null)
            {
                return mineralA.Position;
            }

            return liveWorkerPosition;
        }

        private List<WorkerEntryDto> GetLiveWorkers(ResponseObservation observation, int startIndex)
        {
            var results = new List<WorkerEntryDto>();
            var units = observation?.Observation?.RawData?.Units;
            if (units == null)
            {
                return results;
            }

            foreach (var u in units.Where(u => u != null && u.Alliance == Alliance.Self && (u.UnitType == (uint)UnitTypes.ZERG_DRONE || u.UnitType == (uint)UnitTypes.TERRAN_SCV || u.UnitType == (uint)UnitTypes.PROTOSS_PROBE)))
            {
                var label = _workerLabelService.GetLabel(u.Tag);
                results.Add(new WorkerEntryDto
                {
                    UnitTag = u.Tag,
                    UnitType = u.UnitType,
                    Position = new Vector2Dto(u.Pos.X, u.Pos.Y, u.Pos.Z),
                    Label = label,
                    StartLabel = label,
                    FinalLabel = label
                });
            }

            return results;
        }

        private List<WorkerEntryDto> ResolveCurrentWorkersForTeam(List<WorkerEntryDto> allWorkers, List<WorkerEntryDto> teamAssignments)
        {
            var result = new List<WorkerEntryDto>();
            if (allWorkers == null || teamAssignments == null)
            {
                return result;
            }

            foreach (var assignment in teamAssignments)
            {
                if (assignment == null)
                {
                    continue;
                }

                var worker = allWorkers.FirstOrDefault(u => u != null && (
                    string.Equals(u.FinalLabel, assignment.FinalLabel, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(u.StartLabel, assignment.StartLabel, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(u.Label, assignment.Label, StringComparison.OrdinalIgnoreCase)));

                if (worker != null)
                {
                    result.Add(worker);
                }
            }

            return result;
        }

        private static WorkerEntryDto ResolveWorkerByLabel(string label, IReadOnlyList<WorkerEntryDto> allWorkers)
        {
            if (string.IsNullOrWhiteSpace(label) || allWorkers == null)
            {
                return null;
            }

            return allWorkers.FirstOrDefault(w => string.Equals(w?.FinalLabel ?? w?.Label ?? w?.StartLabel, label, StringComparison.OrdinalIgnoreCase));
        }

        private static WorkerEntryDto ResolveWorker(IEnumerable<WorkerEntryDto> teamWorkers, IReadOnlyList<WorkerEntryDto> allWorkers, int workerSuffix)
        {
            var label = $"W{workerSuffix}";
            var worker = allWorkers?.FirstOrDefault(w => string.Equals(w?.FinalLabel ?? w?.Label ?? w?.StartLabel, label, StringComparison.OrdinalIgnoreCase));
            if (worker != null)
            {
                return worker;
            }

            return teamWorkers?.FirstOrDefault(w => string.Equals(w?.FinalLabel ?? w?.Label ?? w?.StartLabel, label, StringComparison.OrdinalIgnoreCase));
        }

        private static float Distance(Vector2Dto first, Vector2Dto second)
        {
            if (first == null || second == null)
            {
                return float.MaxValue;
            }

            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private static WorkerEntryDto ResolveWorkerBySuffix(IEnumerable<WorkerEntryDto> teamWorkers, IReadOnlyList<WorkerEntryDto> liveWorkers, int suffix)
        {
            var label = $"W{suffix}";
            return ResolveWorker(teamWorkers, liveWorkers, suffix) ?? liveWorkers.FirstOrDefault(w => string.Equals(w?.FinalLabel ?? w?.Label ?? w?.StartLabel, label, StringComparison.OrdinalIgnoreCase));
        }

        private void AddAction(List<SC2Action> actions, IEnumerable<SC2Action> action)
        {
            if (action == null)
            {
                return;
            }

            actions.AddRange(action);
        }

        private IEnumerable<SC2Action> IssueMoveAction(WorkerEntryDto worker, Vector2Dto target)
        {
            if (worker?.UnitTag == 0 || target == null)
            {
                return Array.Empty<SC2Action>();
            }

            return new[]
            {
                new SC2Action
                {
                    ActionRaw = new ActionRaw
                    {
                        UnitCommand = new ActionRawUnitCommand
                        {
                            AbilityId = 16,
                            TargetWorldSpacePos = new Point2D { X = target.X, Y = target.Y },
                            UnitTags = { worker.UnitTag }
                        }
                    }
                }
            };
        }

        private static bool GetBumpEnabled(int teamNumber)
        {
            return teamNumber switch
            {
                1 => Settings.T1Bump,
                2 => Settings.S1Bump,
                3 => Settings.B1Bump,
                4 => Settings.Y1Bump,
                _ => false
            };
        }

        private static int GetWorkerSuffix(string workerLabel)
        {
            if (string.IsNullOrWhiteSpace(workerLabel) || workerLabel.Length < 2)
            {
                return 0;
            }

            return int.TryParse(workerLabel.Substring(1), out var suffix) ? suffix : 0;
        }

        public void ProcessFrameObservation(ResponseObservation observation)
        {
            if (observation?.Observation?.RawData?.Units == null)
            {
                return;
            }

            var workerEntries = ProcessVisableUnits.ProcessVisibleUnits(observation, _workerLabelService, _mineralLabelService, _vespeneLabelService, _spawningPoolPlacementService);
            if (_mapData == null)
            {
                return;
            }

            var currentStartIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var currentAssignments = OngoingMapData.ResolveTeamAssignments(_mapData, currentStartIndex);
            _ccaMiningService.RecordSpawnObservation(_mapData, currentStartIndex, currentAssignments, _workerLabelService, workerEntries: workerEntries);
            Settings.ccaMining = true;
        }

        private List<SC2Action> ExecuteInitialMiningManeuvers(ResponseObservation observation, int elapsedFrames)
        {
            var actions = new List<SC2Action>();
            if (!ManagerDebugService.IsDebugEnabled)
            {
                return actions;
            }

            if (elapsedFrames % 5 != 0)
            {
                return actions;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var teamAssignments = ResolveTeamAssignments(startIndex);
            if (teamAssignments.Count == 0)
            {
                return actions;
            }

            var selfUnits = observation?.Observation?.RawData?.Units?
                .Where(u => u != null && u.Alliance == Alliance.Self)
                .ToList() ?? new List<Unit>();

            var workers = selfUnits
                .Where(u => u.UnitType == (uint)UnitTypes.ZERG_DRONE ||
                            u.UnitType == (uint)UnitTypes.TERRAN_SCV ||
                            u.UnitType == (uint)UnitTypes.PROTOSS_PROBE)
                .ToList();

            if (workers.Count == 0)
            {
                return actions;
            }

            Console.WriteLine($"BabySharkMiningManager: initial maneuvers frame={elapsedFrames} startIndex={startIndex} teamAssignments={teamAssignments.Count}");

            foreach (var assignment in teamAssignments)
            {
                if (assignment == null || assignment.Workers == null || assignment.Workers.Count == 0 || assignment.Minerals == null || assignment.Minerals.Count < 2)
                {
                    continue;
                }

                var mineralA = assignment.Minerals[0];
                var mineralB = assignment.Minerals[1];
                if (mineralA?.Position == null || mineralB?.Position == null)
                {
                    continue;
                }

                var lineTarget = new Vector2Dto(
                    (mineralA.Position.X + mineralB.Position.X) * 0.5f,
                    (mineralA.Position.Y + mineralB.Position.Y) * 0.5f,
                    mineralA.Position.Z);

                var quarterTarget = new Vector2Dto(
                    mineralA.Position.X * 0.25f + lineTarget.X * 0.75f,
                    mineralA.Position.Y * 0.25f + lineTarget.Y * 0.75f,
                    mineralA.Position.Z);

                var teamWorkers = ResolveCurrentWorkersForTeam(workers, assignment.Workers);
                if (teamWorkers.Count == 0)
                {
                    continue;
                }

                var teamPrefix = GetTeamPrefix(assignment.TeamNumber);
                if (string.IsNullOrWhiteSpace(teamPrefix))
                {
                    continue;
                }

                if (teamPrefix == "T" || teamPrefix == "Y")
                {
                    var worker1 = teamWorkers.FirstOrDefault(w => string.Equals(GetWorkerFinalLabel(w), $"{teamPrefix}1", StringComparison.OrdinalIgnoreCase));
                    var worker2 = teamWorkers.FirstOrDefault(w => string.Equals(GetWorkerFinalLabel(w), $"{teamPrefix}2", StringComparison.OrdinalIgnoreCase));
                    var worker3 = teamWorkers.FirstOrDefault(w => string.Equals(GetWorkerFinalLabel(w), $"{teamPrefix}3", StringComparison.OrdinalIgnoreCase));

                    AddAction(actions, IssueMoveToPoint(worker1, lineTarget));
                    AddAction(actions, IssueMoveToPoint(worker3, quarterTarget));
                    if (worker2 != null)
                    {
                        AddAction(actions, IssueGatherCommand(observation, worker2, mineralA, mineralB));
                    }
                }
                else
                {
                    var worker1 = teamWorkers.FirstOrDefault(w => string.Equals(GetWorkerFinalLabel(w), $"{teamPrefix}1", StringComparison.OrdinalIgnoreCase));
                    var worker2 = teamWorkers.FirstOrDefault(w => string.Equals(GetWorkerFinalLabel(w), $"{teamPrefix}2", StringComparison.OrdinalIgnoreCase));
                    var worker3 = teamWorkers.FirstOrDefault(w => string.Equals(GetWorkerFinalLabel(w), $"{teamPrefix}3", StringComparison.OrdinalIgnoreCase));

                    AddAction(actions, IssueMoveToPoint(worker1, lineTarget));
                    if (worker2 != null)
                    {
                        AddAction(actions, IssueMoveToPoint(worker2, quarterTarget));
                    }
                    AddAction(actions, IssueGatherCommand(observation, worker3, mineralA, mineralB));
                }
            }

            return actions;
        }

        private List<SC2Action> ExecuteJustInTimeMining(ResponseObservation observation)
        {
            // BREAKPOINT: Triggers every frame during steady-state JIT mining
            try { System.Diagnostics.Debugger.Break(); } catch { }

            var actions = new List<SC2Action>();

            // For the main base JIT logic, we use the stable StartIndex from Globals/Settings.
            // This prevents "Teal" teams from other bases (expansions) being incorrectly processed here.
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var teamAssignments = ResolveTeamAssignments(startIndex);
            if (teamAssignments.Count == 0)
            {
                return actions;
            }

            var selfUnits = observation?.Observation?.RawData?.Units?
                .Where(u => u != null && u.Alliance == Alliance.Self)
                .ToList() ?? new List<Unit>();

            var workers = selfUnits
                .Where(u => u.UnitType == (uint)UnitTypes.ZERG_DRONE ||
                            u.UnitType == (uint)UnitTypes.TERRAN_SCV ||
                            u.UnitType == (uint)UnitTypes.PROTOSS_PROBE)
                .ToList();

            if (workers.Count == 0)
            {
                return actions;
            }

            var townhall = _mapData.StartingTownHall[startIndex];
            if (townhall == null) return actions;
            var townhallPos2D = new Point2D { X = townhall.X, Y = townhall.Y };

            foreach (var assignment in teamAssignments)
            {
                if (assignment == null || assignment.Workers == null || assignment.Workers.Count == 0 || assignment.Minerals == null || assignment.Minerals.Count < 2)
                {
                    continue;
                }

                var teamWorkers = ResolveCurrentWorkersForTeam(workers, assignment.Workers);
                if (teamWorkers.Count == 0)
                {
                    continue;
                }

                foreach (var worker in teamWorkers)
                {
                    // Skip the prepositioned builder if any (selected by JitPrepositionService)
                    // We don't have direct access to the service here, but the Task handles the exclusion.
                    // For safety, we just process all assigned workers here.

                    var carrying = worker.BuffIds.Any(b => b == 271 || b == 272); // Mineral/Vespene buffs

                    _previousCarryingState.TryGetValue(worker.Tag, out var wasCarrying);
                    if (carrying && !wasCarrying)
                    {
                        Console.WriteLine($"BabySharkMiningManager: Worker {worker.Tag} ({_workerLabelService?.GetLabel(worker.Tag)}) just completed mining.");
                        try { System.Diagnostics.Debugger.Break(); } catch { }
                    }
                    _previousCarryingState[worker.Tag] = carrying;
                    
                    if (carrying)
                    {
                        // Notify the manager to rotate the team state
                        if (!wasCarrying)
                        {
                            var teamId = assignment.TeamId;
                            RegisterCargoReturn(worker.Tag, teamId);
                        }

                        // Return cargo at the JIT return point
                        AddAction(actions, new SC2Action { 
                            ActionRaw = new ActionRaw { 
                                UnitCommand = new ActionRawUnitCommand {
                                    AbilityId = (int)Abilities.SMART,
                                    UnitTags = { worker.Tag },
                                    TargetWorldSpacePos = new Point2D { X = assignment.JitReturnPoint.X, Y = assignment.JitReturnPoint.Y }
                                }
                            }
                        });
                    }
                    else
                    {
                        // Note: Task version calls RegisterCargoReturn based on wasCarrying state.
                        // Here we just get the next target from the manager state.
                        var nextTargetPos = GetJITMiningTarget(worker, townhallPos2D, new Point2D());
                        
                        // Resolve if the target is Mineral A or B to issue HARVEST_GATHER
                        var mineral = assignment.Minerals.FirstOrDefault(m => Math.Abs(nextTargetPos.X - m.Position.X) < 0.1f && Math.Abs(nextTargetPos.Y - m.Position.Y) < 0.1f);
                        if (mineral != null)
                        {
                            AddAction(actions, new SC2Action { 
                                ActionRaw = new ActionRaw { 
                                    UnitCommand = new ActionRawUnitCommand {
                                        AbilityId = (int)Abilities.HARVEST_GATHER,
                                        UnitTags = { worker.Tag },
                                        TargetUnitTag = mineral.UnitTag
                                    }
                                }
                            });
                        }
                        else
                        {
                            AddAction(actions, new SC2Action { 
                                ActionRaw = new ActionRaw { 
                                    UnitCommand = new ActionRawUnitCommand {
                                        AbilityId = (int)Abilities.SMART,
                                        UnitTags = { worker.Tag },
                                        TargetWorldSpacePos = nextTargetPos
                                    }
                                }
                            });
                        }
                    }
                }
            }

            return actions;
        }

        private List<TeamPatchAssignmentDto> ResolveTeamAssignments(int startIndex)
        {
            return OngoingMapData.ResolveTeamAssignments(_mapData, startIndex);
        }

        private List<Unit> ResolveCurrentWorkersForTeam(List<Unit> allWorkers, List<WorkerEntryDto> teamAssignments)
        {
            var result = new List<Unit>();
            if (allWorkers == null || teamAssignments == null)
            {
                return result;
            }

            foreach (var assignment in teamAssignments)
            {
                if (assignment == null)
                {
                    continue;
                }

                var worker = allWorkers.FirstOrDefault(u => u != null && (
                    string.Equals(_workerLabelService?.GetLabel(u.Tag), assignment.FinalLabel, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_workerLabelService?.GetLabel(u.Tag), assignment.StartLabel, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_workerLabelService?.GetLabel(u.Tag), assignment.Label, StringComparison.OrdinalIgnoreCase)));

                if (worker != null)
                {
                    result.Add(worker);
                }
            }

            return result;
        }

        private Dictionary<ulong, string> BuildWorkerFinalLabelMap(IEnumerable<WorkerEntryDto> storedWorkers)
        {
            var result = new Dictionary<ulong, string>();
            foreach (var worker in storedWorkers ?? Enumerable.Empty<WorkerEntryDto>())
            {
                if (worker == null || worker.UnitTag == 0 || string.IsNullOrWhiteSpace(worker.FinalLabel))
                {
                    continue;
                }

                result[worker.UnitTag] = worker.FinalLabel;
            }

            return result;
        }

        private string ResolveWorkerFinalLabelByTag(ulong tag, IReadOnlyDictionary<ulong, string> finalLabelByTag)
        {
            if (finalLabelByTag == null || finalLabelByTag.Count == 0)
            {
                return string.Empty;
            }

            return finalLabelByTag.TryGetValue(tag, out var label) ? label : string.Empty;
        }

        private string GetWorkerFinalLabel(Unit unit)
        {
            return _workerLabelService?.GetLabel(unit?.Tag ?? 0) ?? string.Empty;
        }

        private string GetTeamPrefix(int teamNumber)
        {
            if (Settings.WorkerCount == 12)
            {
                return teamNumber switch
                {
                    1 => "T",
                    2 => "S",
                    3 => "B",
                    4 => "Y",
                    _ => string.Empty
                };
            }

            // 8 Workers or other non-standard starts
            return teamNumber switch
            {
                1 => "G", // Green
                2 => "P", // Purple
                3 => "O", // Orange
                4 => "R", // Red
                _ => string.Empty
            };
        }

        private SC2Action? IssueMoveToPoint(Unit worker, Vector2Dto point)
        {
            if (worker?.Tag == 0 || point == null)
            {
                return null;
            }

            return new SC2Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.MOVE,
                        UnitTags = { worker.Tag },
                        TargetWorldSpacePos = new Point2D { X = point.X, Y = point.Y }
                    }
                }
            };
        }

        private SC2Action? IssueGatherCommand(ResponseObservation observation, Unit worker, OrderedMineral mineralA, OrderedMineral mineralB)
        {
            if (worker?.Tag == 0 || mineralA?.Position == null)
            {
                return null;
            }

            var mineralTag = ResolveMineralTag(observation, mineralA?.Position);
            if (mineralTag == 0)
            {
                mineralTag = ResolveMineralTag(observation, mineralB?.Position);
            }
            if (mineralTag == 0)
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
                        UnitTags = { worker.Tag },
                        TargetUnitTag = mineralTag
                    }
                }
            };
        }

        private static void AddAction(List<SC2Action> actions, SC2Action? action)
        {
            if (action != null)
            {
                actions.Add(action);
            }
        }

        private ulong ResolveMineralTag(ResponseObservation observation, Vector2Dto position)
        {
            if (observation?.Observation?.RawData?.Units == null || position == null)
            {
                return 0;
            }

            var nearest = observation.Observation.RawData.Units
                .Where(u => u != null && IsMineralType((UnitTypes)u.UnitType))
                .Select(u => new { Unit = u, Distance = Math.Pow(u.Pos.X - position.X, 2) + Math.Pow(u.Pos.Y - position.Y, 2) })
                .OrderBy(v => v.Distance)
                .FirstOrDefault();

            if (nearest == null || nearest.Distance >= 4)
            {
                return 0;
            }

            return nearest.Unit.Tag;
        }

        private void UpdateScoutedMinerals(ResponseObservation observation)
        {
            if (_mapData == null || _mapData.Minerals == null || _mapData.MineralTagToIndex == null)
                return;

            var units = observation?.Observation?.RawData?.Units;
            if (units == null)
                return;

            foreach (var unit in units)
            {
                try
                {
                    if (unit?.Pos == null)
                        continue;

                    var ut = (UnitTypes)unit.UnitType;
                    if (!IsMineralType(ut))
                        continue;

                    if (unit.DisplayType != DisplayType.Visible)
                        continue;

                    if (unit.Tag == 0 || !_mapData.MineralTagToIndex.TryGetValue(unit.Tag, out var mineralIndex))
                        continue;

                    if (mineralIndex < 0 || mineralIndex >= _mapData.Minerals.Count)
                        continue;

                    var mineral = _mapData.Minerals[mineralIndex];
                    var contents = unit.HasMineralContents ? unit.MineralContents : 0;

                    if (contents != mineral.MaxMineralContents)
                    {
                        _mapData.MismatchedMinerals = true;
                    }

                    if (contents > mineral.MaxMineralContents)
                    {
                        Console.WriteLine($"BabySharkMiningManager: Mineral[{mineralIndex}] tag={unit.Tag} contents updated {mineral.MaxMineralContents} -> {contents}");
                        mineral.MaxMineralContents = contents;
                    }

                    if (_mapData.MineralTypeMaxContents.ContainsKey(unit.UnitType) && contents > _mapData.MineralTypeMaxContents[unit.UnitType])
                    {
                        _mapData.MineralTypeMaxContents[unit.UnitType] = contents;
                    }

                    if (contents > mineral.MineralContents)
                    {
                        mineral.MineralContents = contents;
                    }

                    mineral.UnitTag = unit.Tag;
                    mineral.UnitType = unit.UnitType;
                    if (mineral.Position == null)
                    {
                        mineral.Position = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"BabySharkMiningManager.UpdateScoutedMinerals: Error for tag {unit?.Tag}: {ex.Message}");
                }
            }
        }

        private bool IsMineralType(UnitTypes unitType)
        {
            return unitType == UnitTypes.NEUTRAL_MINERALFIELD ||
                   unitType == UnitTypes.NEUTRAL_MINERALFIELD750 ||
                   unitType == UnitTypes.NEUTRAL_RICHMINERALFIELD ||
                   unitType == UnitTypes.NEUTRAL_RICHMINERALFIELD750 ||
                   unitType == UnitTypes.NEUTRAL_PURIFIERMINERALFIELD ||
                   unitType == UnitTypes.NEUTRAL_PURIFIERMINERALFIELD750 ||
                   unitType == UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD ||
                   unitType == UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD750 ||
                   unitType == UnitTypes.NEUTRAL_LABMINERALFIELD ||
                   unitType == UnitTypes.NEUTRAL_LABMINERALFIELD750 ||
                   unitType == UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD ||
                   unitType == UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD750;
        }

        private void UpdateMineralReturnRate(ResponseObservation observation)
        {
            if (_mineralReturnRateTrackerService == null || observation?.Observation?.Score?.ScoreDetails == null || observation?.Observation?.RawData?.Units == null)
            {
                return;
            }

            var droneCount = observation.Observation.RawData.Units.Count(u => u != null && u.Alliance == Alliance.Self && u.UnitType == (uint)UnitTypes.ZERG_DRONE);
            if (droneCount < 12 || droneCount > 16)
            {
                return;
            }

            var collectionRateMinerals = observation.Observation.Score.ScoreDetails.CollectionRateMinerals;
            if (collectionRateMinerals > 0)
            {
                _mineralReturnRateTrackerService.Record(droneCount, collectionRateMinerals);
            }
        }

        private void PrintMineralReturnRateSummary(ResponseObservation observation)
        {
            if (_mineralReturnRateTrackerService == null || observation?.Observation == null)
            {
                return;
            }

            var frame = (int)observation.Observation.GameLoop;
            if (frame - _lastMineralReturnRateConsoleFrame < 5)
            {
                return;
            }

            _lastMineralReturnRateConsoleFrame = frame;
            Console.WriteLine($"BabySharkMiningManager: Mineral return rates -> {_mineralReturnRateTrackerService.GetSummary()}");
        }

        private void PrintTwelveDroneMilestone(ResponseObservation observation)
        {
            if (_printedTwelveDroneMilestone || _frameToTimeConverter == null || observation?.Observation?.Score?.ScoreDetails == null || observation?.Observation?.RawData?.Units == null)
            {
                return;
            }

            var droneCount = observation.Observation.RawData.Units.Count(u => u != null && u.Alliance == Alliance.Self && u.UnitType == (uint)UnitTypes.ZERG_DRONE);
            if (droneCount != 12)
            {
                return;
            }

            var scoreDetails = observation.Observation.Score.ScoreDetails;
            if (scoreDetails.CollectionRateMinerals < 200 || scoreDetails.CollectedMinerals < 150)
            {
                return;
            }

            _printedTwelveDroneMilestone = true;
            var time = _frameToTimeConverter.GetTime((int)observation.Observation.GameLoop);
            Console.WriteLine($"BabySharkMiningManager: 12 drones hit collected minerals={scoreDetails.CollectedMinerals:F0}, collection rate={scoreDetails.CollectionRateMinerals:F1} at {time:hh\\:mm\\:ss}");
        }

        /// <summary>
        /// Draw center of mass visualization for mineral patches and vespene geysers.
        /// Shows as a crosshair with lines and a center sphere.
        /// </summary>
        private void DrawCenterOfMass()
        {
            if (!ManagerDebugService.IsDebugEnabled)
                return;

            // This method will draw COM when map data becomes available
            // For now, we'll add support as map data integration is completed
            // TODO: Integrate with MapDataSnapshot to draw mineral and vespene COM
        }

        private void PauseAfterDebugDraw()
        {
            if (ManagerDebugService.IsDebugEnabled && !_pausedAfterWorkerInstructions && _workerInstructionDrawCount >= 2)
            {
                _pausedAfterWorkerInstructions = true;
                // Pause only for a small number of frames to allow debugger inspection;
                // set expiration so the system resumes automatically after developer continues execution.
                _pauseUntilFrame = _currentFrame + 5;
                Console.WriteLine($"BabySharkMiningManager: pausing after debug draw at frame {_currentFrame} until {_pauseUntilFrame}");
                //System.Diagnostics.Debugger.Break();
            }
        }

        /// <summary>
        /// Draw a simple arrow or direction line from start to end position.
        /// Used for visualizing worker instructions.
        /// </summary>
        private void DrawArrow(Point start, Point end, Color color, float arrowHeadSize = 0.5f)
        {
            if (!ManagerDebugService.IsDebugEnabled)
                return;

            // Draw the main line
            ManagerDebugService.DrawLine(start, end, color);

            // Calculate direction vector
            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float dz = end.Z - start.Z;
            float length = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (length > 0.01f)
            {
                // Normalize
                dx /= length;
                dy /= length;
                dz /= length;

                // Create arrowhead: perpendicular lines from the endpoint
                float perp1X = -dy;
                float perp1Y = dx;

                // Arrowhead point 1
                var arrowPoint1 = new Point
                {
                    X = end.X - dx * arrowHeadSize + perp1X * arrowHeadSize * 0.5f,
                    Y = end.Y - dy * arrowHeadSize + perp1Y * arrowHeadSize * 0.5f,
                    Z = end.Z - dz * arrowHeadSize
                };

                // Arrowhead point 2
                var arrowPoint2 = new Point
                {
                    X = end.X - dx * arrowHeadSize - perp1X * arrowHeadSize * 0.5f,
                    Y = end.Y - dy * arrowHeadSize - perp1Y * arrowHeadSize * 0.5f,
                    Z = end.Z - dz * arrowHeadSize
                };

                // Draw arrowhead lines
                ManagerDebugService.DrawLine(end, arrowPoint1, color);
                ManagerDebugService.DrawLine(end, arrowPoint2, color);
            }
        }

        /// <summary>
        /// Draw F1-F4 (far) and N1-N4 (near) mineral labels on the game client.
        /// </summary>
        private void DrawMineralLabels()
        {
            if (!ManagerDebugService.IsDebugEnabled)
            {
                return;
            }

            if (_mineralLabelService == null)
            {
                Console.WriteLine("BabySharkMiningManager.DrawMineralLabels: MineralLabelService is null");
                return;
            }

            if (_mapData == null || _mapData.OrderedMainMinerals == null)
            {
                Console.WriteLine("BabySharkMiningManager.DrawMineralLabels: Map data or OrderedMainMinerals not available");
                return;
            }

            try
            {
                var serviceMineralLabels = _mineralLabelService.GetAllMineralLabels();
                
                if (serviceMineralLabels.Count > 0 && !_didInitialLabelBreak)
                {
                    Console.WriteLine($"BabySharkMiningManager.DrawMineralLabels: Found {serviceMineralLabels.Count} labels, breaking.");
                    _didInitialLabelBreak = true;
                    //System.Diagnostics.Debugger.Break();
                }

                if (serviceMineralLabels.Count > 0)
                {
                    foreach (var kvp in serviceMineralLabels)
                    {
                        var label = kvp.Key;
                        var mineralData = kvp.Value;

                        if (mineralData.Position != null)
                        {
                            ManagerDebugService.DrawText(label, mineralData.Position, mineralData.Color, 12);
                            if (_currentFrame % 100 == 0)
                            {
                                Console.WriteLine($"BabySharkMiningManager.DrawMineralLabels: Drew '{label}' at ({mineralData.Position.X:F2},{mineralData.Position.Y:F2}) color RGB({mineralData.Color.R},{mineralData.Color.G},{mineralData.Color.B})");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawMineralLabels: Error drawing mineral labels: {ex.Message}");
            }
        }

        /// <summary>
        /// Draw expansion mineral labels (M1-E1, M14-E1, M2-E2, etc.) on the game client.
        /// These labels show which minerals belong to which expansion cluster.
        /// </summary>
        private void DrawExpansionMineralLabels()
        {
            if (!ManagerDebugService.IsDebugEnabled)
            {
                return;
            }

            if (_mapData == null || _mapData.ExpansionMineralLabels == null || _mapData.ExpansionMineralLabels.Count == 0)
            {
                return;
            }

            try
            {
                var expansionMineralLabels = _mapData.ExpansionMineralLabels;
                //Console.WriteLine($"BabySharkMiningManager.DrawExpansionMineralLabels: Drawing {expansionMineralLabels.Count} expansion mineral labels");

                // Draw each expansion mineral label using ManagerDebugService.DrawText
                foreach (var kvp in expansionMineralLabels)
                {
                    string posKey = kvp.Key;  // "X,Y,Z" format
                    string label = kvp.Value;  // "M1-E1", "M14-E1", etc.

                    try
                    {
                        // Parse position from key: "X,Y,Z"
                        var parts = posKey.Split(',');
                        if (parts.Length >= 2 && float.TryParse(parts[0], out float x) && float.TryParse(parts[1], out float y))
                        {
                            float z = 0;
                            if (parts.Length >= 3)
                            {
                                float.TryParse(parts[2], out z);
                            }

                            var textPos = new Point { X = x, Y = y, Z = z + 0.5f };  // Offset slightly above ground
                            var labelColor = new Color { R = 150, G = 200, B = 150 };  // Light green for expansion minerals

                            ManagerDebugService.DrawText(label, textPos, labelColor, 12);
                           // Console.WriteLine($"BabySharkMiningManager.DrawExpansionMineralLabels: Drew '{label}' at ({x:F2},{y:F2},{z:F2})");
                        }
                        else
                        {
                            //Console.WriteLine($"BabySharkMiningManager.DrawExpansionMineralLabels: Could not parse position key '{posKey}' for label '{label}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        //Console.WriteLine($"BabySharkMiningManager.DrawExpansionMineralLabels: Error drawing label '{label}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"BabySharkMiningManager.DrawExpansionMineralLabels: Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Draw V1, V2, V3, etc. vespene labels on the game client.
        /// V1 = green (closest to W4), V2 = blue (next closest), V3+ = purple
        /// </summary>
        private void DrawVespeneLabels()
        {
            if (_vespeneLabelService == null)
            {
                Console.WriteLine("BabySharkMiningManager.DrawVespeneLabels: VespeneLabelService is null");
                return;
            }

            if (_mapData == null || _mapData.OrderedMainVespene == null)
            {
                Console.WriteLine("BabySharkMiningManager.DrawVespeneLabels: Map data or OrderedMainVespene not available");
                return;
            }

            try
            {
                var serviceVespeneLabels = _vespeneLabelService.GetAllVespeneLabels();
                Console.WriteLine($"BabySharkMiningManager.DrawVespeneLabels: Drawing {serviceVespeneLabels.Count} vespene labels");

                if (serviceVespeneLabels.Count > 0)
                {
                    foreach (var kvp in serviceVespeneLabels)
                    {
                        var label = kvp.Key;
                        var vespeneData = kvp.Value;

                        if (vespeneData.Position != null)
                        {
                            ManagerDebugService.DrawText(label, vespeneData.Position, vespeneData.Color, 12);
                            Console.WriteLine($"BabySharkMiningManager.DrawVespeneLabels: Drew '{label}' at ({vespeneData.Position.X:F2},{vespeneData.Position.Y:F2})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawVespeneLabels: Error drawing vespene labels: {ex.Message}");
            }
        }

        /// <summary>
        /// Draw expansion townhall placement points.
        /// Uses Sharky's DrawSphere and DrawText primitives.
        /// - Standard expansions: Green sphere + label
        /// - Contested expansions: Yellow/Orange spheres for alternate placements
        /// </summary>
        private void DrawExpansionPoints()
        {
            if (!ManagerDebugService.IsDebugEnabled)
            {
                return;
            }

            if (_expansionPointDrawService == null)
            {
                //Console.WriteLine("BabySharkMiningManager.DrawExpansionPoints: ExpansionPointDrawService is null");
                return;
            }

            try
            {
                var allPoints = _expansionPointDrawService.GetAllPoints();

                if (allPoints.Count == 0)
                {
                    return;
                }

                //Console.WriteLine($"BabySharkMiningManager.DrawExpansionPoints: Drawing {allPoints.Count} expansion points");

                foreach (var kvp in allPoints)
                {
                    var label = kvp.Key;
                    var pointData = kvp.Value;

                    if (pointData.Position != null)
                    {
                        var position = pointData.Position;

                        // Draw sphere at expansion point using registered Z (ground level, not floating)
                        ManagerDebugService.DrawSphere(position, 0.75f, pointData.Color);

                        // Draw label
                        ManagerDebugService.DrawText(label, position, pointData.Color, 12);

                        //Console.WriteLine($"BabySharkMiningManager.DrawExpansionPoints: Drew '{label}' at ({position.X:F2},{position.Y:F2},{position.Z:F2})");
                    }
                }
                var allObjectsShouldBeDrawnHere = "Show me";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawExpansionPoints: Error drawing expansion points: {ex.Message}");
            }
        }

        private void DrawSpawningPoolPlacement()
        {
            if (!ManagerDebugService.IsDebugEnabled || _spawningPoolPlacementService == null || _mapData == null)
            {
                return;
            }

            try
            {
                var placement = _spawningPoolPlacementService.GetPlacement(_mapData, 0);
                if (placement == null)
                {
                    return;
                }

                _spawningPoolPlacementService.DrawPlacement(placement);
                Console.WriteLine($"BabySharkMiningManager: Drew spawning pool placement at ({placement.X:F2},{placement.Y:F2})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawSpawningPoolPlacement: Error: {ex.Message}");
            }
        }

        private void BreakWhenSpawnLabelsShouldBeVisible(ResponseObservation observation)
        {
            if (_spawnLabelDebugBreakTriggered)
            {
                return;
            }

            var frame = observation.Observation.GameLoop;
            if (frame >= 5)
            {
                var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                if (GetWorkerLabelOrderingCompleted(startIndex))
                {
                    _spawnLabelDebugBreakTriggered = true;
                    Console.WriteLine($"BabySharkMiningManager: Worker labels for spawn {startIndex} should be visible at frame {frame}. Triggering debug break.");
                    try
                    {
                        //Debugger.Break();
                    }
                    catch { }
                }
            }
        }

        private bool HasAllLegacyMineralLabelsVisible()
        {
            if (_mineralLabelService == null)
            {
                return false;
            }

            var labels = _mineralLabelService.GetAllMineralLabels();
            if (labels == null || labels.Count == 0)
            {
                return false;
            }

            return labels.Keys.Any(label => string.Equals(label, "M8", StringComparison.OrdinalIgnoreCase))
                && labels.Keys.Any(label => string.Equals(label, "M7", StringComparison.OrdinalIgnoreCase))
                && labels.Keys.Any(label => string.Equals(label, "M6", StringComparison.OrdinalIgnoreCase))
                && labels.Keys.Any(label => string.Equals(label, "M5", StringComparison.OrdinalIgnoreCase))
                && labels.Keys.Any(label => string.Equals(label, "M4", StringComparison.OrdinalIgnoreCase))
                && labels.Keys.Any(label => string.Equals(label, "M3", StringComparison.OrdinalIgnoreCase))
                && labels.Keys.Any(label => string.Equals(label, "M2", StringComparison.OrdinalIgnoreCase))
                && labels.Keys.Any(label => string.Equals(label, "M1", StringComparison.OrdinalIgnoreCase));
        }

        private bool HasAllLegacyVespeneLabelsVisible()
        {
            if (_vespeneLabelService == null)
            {
                return false;
            }

            var labels = _vespeneLabelService.GetAllVespeneLabels();
            if (labels == null || labels.Count == 0)
            {
                return false;
            }

            return labels.Keys.Any(label => string.Equals(label, "V1", StringComparison.OrdinalIgnoreCase))
                && labels.Keys.Any(label => string.Equals(label, "V2", StringComparison.OrdinalIgnoreCase));
        }

        private bool HasVisibleSpawningPool(ResponseObservation observation)
        {
            if (observation?.Observation?.RawData?.Units == null)
            {
                return false;
            }

            return observation.Observation.RawData.Units.Any(u =>
                u != null &&
                u.DisplayType == DisplayType.Visible &&
                u.UnitType == (uint)UnitTypes.ZERG_SPAWNINGPOOL);
        }

        public string GetMineralReturnRateSummary()
        {
            return _mineralReturnRateTrackerService?.GetSummary() ?? "Mineral return rate tracker not initialized";
        }

        private int GetActiveStartIndex(ResponseObservation observation)
        {
            return Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
        }

        private List<WorkerEntryDto> GetStoredWorkersForStart(int startIndex)
        {
            if (_mapData == null)
            {
                return new List<WorkerEntryDto>();
            }

            var workerCount = Settings.WorkerCount;
            if (workerCount != 8 && workerCount != 12)
            {
                workerCount = 12;
            }

            var results = new List<WorkerEntryDto>();
            if (_mapData.StartingUnits != null && startIndex >= 0 && startIndex < _mapData.StartingUnits.Count)
            {
                results.AddRange(_mapData.StartingUnits[startIndex] ?? Enumerable.Empty<WorkerEntryDto>());
            }

            return results;
        }

        private Dictionary<string, string> BuildWorkerLabelFallbackMap(IEnumerable<WorkerEntryDto> storedWorkers)
        {
            var result = new Dictionary<string, string>();
            foreach (var worker in storedWorkers ?? Enumerable.Empty<WorkerEntryDto>())
            {
                if (worker?.Position == null || string.IsNullOrWhiteSpace(worker.FinalLabel))
                {
                    continue;
                }

                result[$"{worker.Position.X:F2},{worker.Position.Y:F2}"] = worker.FinalLabel;
            }

            return result;
        }

        private string ResolveWorkerLabelByPosition(float x, float y, IReadOnlyDictionary<string, string> fallbackByPosition)
        {
            if (fallbackByPosition == null || fallbackByPosition.Count == 0)
            {
                return string.Empty;
            }

            var key = $"{x:F2},{y:F2}";
            if (fallbackByPosition.TryGetValue(key, out var label))
            {
                return label;
            }

            var nearest = fallbackByPosition
                .Select(kvp => new
                {
                    Label = kvp.Value,
                    Distance = ParseDistanceSquared(kvp.Key, x, y)
                })
                .OrderBy(v => v.Distance)
                .FirstOrDefault();

            return nearest != null && nearest.Distance < 4f ? nearest.Label : string.Empty;
        }

        private static float ParseDistanceSquared(string positionKey, float x, float y)
        {
            var parts = positionKey.Split(',');
            if (parts.Length < 2 || !float.TryParse(parts[0], out var px) || !float.TryParse(parts[1], out var py))
            {
                return float.MaxValue;
            }

            var dx = px - x;
            var dy = py - y;
            return dx * dx + dy * dy;
        }

        private bool GetWorkerLabelOrderingCompleted(int startIndex)
        {
            if (startIndex < 0)
            {
                return false;
            }

            var workers = GetStoredWorkersForStart(startIndex);
            if (workers == null || workers.Count == 0)
            {
                return false;
            }

            return workers.Any(w => !string.IsNullOrWhiteSpace(w?.FinalLabel));
        }

        private static bool IsLegacyWorkerLabelForDebugBreak(string label)
        {
            return string.Equals(label, "W12", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W11", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W10", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W9", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W8", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W7", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W6", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W5", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "W1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLegacyMineralLabelForDebugBreak(string label)
        {
            return string.Equals(label, "M8", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "M7", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "M6", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "M5", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "M4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "M3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "M2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "M1", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Called when the game ends.
        /// </summary>
        public void OnEnd(ResponseObservation observation, Result result)
        {
            Console.WriteLine($"BabySharkMiningManager: OnEnd called with result: {result}");
        }

        /// <summary>
        /// Public helper to draw all debug visualizations. Safe to call from an external manager.
        /// This method performs no game actions and only emits debug draw calls.
        /// </summary>
        public void DrawDebugVisuals(ResponseObservation observation)
        {
            try
            {
                if (!ManagerDebugService.IsDebugEnabled)
                {
                    return;
                }

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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BabySharkMiningManager.DrawDebugVisuals: Error drawing debug visuals: {ex.Message}");
            }
        }
    }
}

