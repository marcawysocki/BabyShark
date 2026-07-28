using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using Sharky.MicroTasks;
using Sharky.MicroTasks.Mining;
using BabySharkBot.Managers;
using BabySharkBot.Services;
using BabySharkBot.Setup;
using RLIntegration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace BabySharkBot.MicroTasks
{
    public class TeamPatchMiningTask : MiningTask
    {
        private readonly DefaultSharkyBot _defaultBot;
        private readonly BabySharkMiningManager _babySharkMiningManager;
        private readonly BaseData _baseData;
        private readonly SharkyUnitData _sharkyUnitData;
        private readonly ActiveUnitData _activeUnitData;
        private readonly Dictionary<ulong, Vector2> _homePositions = new Dictionary<ulong, Vector2>();
        private readonly Dictionary<ulong, bool> _lastCarrying = new Dictionary<ulong, bool>();
        private readonly ImitationRecorder _recorder;
        private readonly ImitationRecorder _scoreRecorder;
        private readonly MiningPatternAdvisor _patternAdvisor;
        private readonly BabySharkBot.Services.chrisCrossAppleSause _ccaService;
        private const int CycleLengthFrames = 90;
        private MiningCycleRecord _currentCycle;
        private bool _returningToFormation;
        private int _lastFrameSeen;

        private readonly JitPrepositionService _jitPrepositionService;

        public TeamPatchMiningTask(DefaultSharkyBot defaultSharkyBot, float priority, MiningDefenseService miningDefenseService, MineralMiner mineralMiner, GasMiner gasMiner, BabySharkMiningManager babySharkMiningManager)
            : base(defaultSharkyBot, priority, miningDefenseService, mineralMiner, gasMiner)
        {
            _defaultBot = defaultSharkyBot;
            _babySharkMiningManager = babySharkMiningManager;
            _baseData = defaultSharkyBot.BaseData;
            _sharkyUnitData = defaultSharkyBot.SharkyUnitData;
            _activeUnitData = defaultSharkyBot.ActiveUnitData;
            _recorder = new ImitationRecorder(Settings.MiningCycleRecordPath);
            _scoreRecorder = new ImitationRecorder(Settings.MiningCycleScorePath);
            _patternAdvisor = new MiningPatternAdvisor(System.IO.Path.ChangeExtension(Settings.MiningCycleScorePath, ".summary.json"));
            _ccaService = new BabySharkBot.Services.chrisCrossAppleSause();
            _jitPrepositionService = new JitPrepositionService(defaultSharkyBot, babySharkMiningManager);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushPendingCycle();
        }

        public override IEnumerable<SC2APIProtocol.Action> PerformActions(int frame)
        {
            _lastFrameSeen = frame;
            var mapData = _babySharkMiningManager?.CurrentMapData;
            if (mapData == null)
            {
                return Array.Empty<SC2APIProtocol.Action>();
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var currentAssignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);
            if (currentAssignments == null || currentAssignments.Count == 0)
            {
                return Array.Empty<SC2APIProtocol.Action>();
            }

            var commands = new List<SC2APIProtocol.Action>();

            // High priority: JIT Prepositioning for build orders. Use MacroData.Minerals instead of LastResponse.
            var prepositionCommands = _jitPrepositionService.Update(frame, (uint)_defaultBot.MacroData.Minerals);
            var prepositionedWorkerTag = _jitPrepositionService.SelectedWorkerTag;

            if (prepositionCommands.Any())
            {
                commands.AddRange(prepositionCommands);
            }

            // Update Phase based on enemy detection
            if (_activeUnitData?.EnemyUnits?.Any(e => !e.Value.UnitTypeData.Attributes.Contains(SC2APIProtocol.Attribute.Structure) && Vector2.DistanceSquared(e.Value.Position, new Vector2(mapData.StartingTownHall[startIndex].X, mapData.StartingTownHall[startIndex].Y)) < 1600) == true)
            {
                if (_ccaService.CurrentPhase != BabySharkBot.Services.chrisCrossAppleSause.TestPhase.CancelAndReturnHome)
                {
                    _ccaService.SetPhase(BabySharkBot.Services.chrisCrossAppleSause.TestPhase.CancelAndReturnHome);
                    Console.WriteLine("TeamPatchMiningTask: Enemy detected in base, canceling cycle.");
                }
            }

            // Handle Cancel/Return Home phase
            if (_ccaService.CurrentPhase == BabySharkBot.Services.chrisCrossAppleSause.TestPhase.CancelAndReturnHome)
            {
                InitializeHomePositions(mapData);
                var returnCommands = ResetToFormation(frame);
                if (AllWorkersAtHome())
                {
                    _ccaService.SetPhase(BabySharkBot.Services.chrisCrossAppleSause.TestPhase.Idle);
                    _currentCycle = null;
                }
                return returnCommands;
            }

            // Invoke bump orders on every frame if possible so the cca service runs during frame processing.
            var liveWorkers = GetLiveWorkers(mapData, startIndex);
            var bumpCommands = _ccaService.BuildBumpOrders(frame, mapData, startIndex, liveWorkers, UnitCommanders);

            if (bumpCommands != null && bumpCommands.Any())
            {
                return bumpCommands;
            }

            if (Settings.ccaMining)
            {
                // If CCA is active but it's not a 5th frame (or it returned no commands),
                // we MUST NOT issue any standard mining orders that would fight the CCA MOVE commands.
                return Array.Empty<SC2APIProtocol.Action>();
            }

            InitializeHomePositions(mapData);

            if (_returningToFormation)
            {
                var returnCommands = ResetToFormation(frame);
                if (AllWorkersAtHome())
                {
                    _returningToFormation = false;
                    _currentCycle = null;
                }

                return returnCommands;
            }

            if (!Settings.ccaMining)
            {
                // After frame 35, the BabySharkMiningManager takes over the steady-state JIT rotations.
                // The Task only handles the High-Priority prepositioning build orders.
                return commands;
            }

            StartOrContinueCycle(frame, mapData);

            var cyclePhase = (frame / CycleLengthFrames) % 3;

            foreach (var teamAssignment in currentAssignments)
            {
                if (teamAssignment == null) continue;

                // After frame 15, we use JIT or Speed Mining.
                // If ccaMining is active, it still uses these methods but the CCA service in Managers handles the bumping.
                if (frame >= 15)
                {
                    commands.AddRange(HandleJitOrSpeedMining(frame, teamAssignment, cyclePhase, _currentCycle?.PatternVariant ?? "baseline", prepositionedWorkerTag));
                }
                else
                {
                    commands.AddRange(IssueTeamPatchOrders(frame, teamAssignment, cyclePhase, _currentCycle?.PatternVariant ?? "baseline"));
                }
            }

            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> HandleJitOrSpeedMining(int frame, TeamPatchAssignmentDto teamAssignment, int cyclePhase, string patternVariant, ulong skipWorkerTag)
        {
            if (teamAssignment.Workers.Count == 3)
            {
                return HandleJITTeam(frame, teamAssignment, skipWorkerTag);
            }
            else
            {
                return IssueTeamPatchOrders(frame, teamAssignment, cyclePhase, patternVariant);
            }
        }

        private IEnumerable<SC2APIProtocol.Action> HandleJITTeam(int frame, TeamPatchAssignmentDto teamAssignment, ulong skipWorkerTag)
        {
            var commands = new List<SC2APIProtocol.Action>();
            
            var townhall = _baseData.SelfBases.FirstOrDefault()?.ResourceCenter;
            if (townhall == null) return commands;
            var townhallPos2D = new Point2D { X = townhall.Pos.X, Y = townhall.Pos.Y };

            foreach (var assignedWorker in teamAssignment.Workers)
            {
                if (assignedWorker.UnitTag == skipWorkerTag) continue;

                var commander = UnitCommanders.FirstOrDefault(c => c.UnitCalculation.Unit.Tag == assignedWorker.UnitTag);
                if (commander == null) continue;

                var carrying = IsCarryingResources(commander);
                var wasCarrying = _lastCarrying.GetValueOrDefault(commander.UnitCalculation.Unit.Tag, false);

                if (carrying)
                {
                    // Return cargo at the JIT return point
                    commands.AddRange(commander.Order(frame, Abilities.SMART, new Point2D { X = teamAssignment.JitReturnPoint.X, Y = teamAssignment.JitReturnPoint.Y }));
                }
                else
                {
                    // If we JUST returned cargo, notify the manager to rotate
                    if (wasCarrying)
                    {
                        _babySharkMiningManager.RegisterCargoReturn(commander.UnitCalculation.Unit.Tag, teamAssignment.TeamId);
                    }

                    // Move to the next JIT target (Mineral A, B, or Wait Point)
                    var nextTargetPos = _babySharkMiningManager.GetJITMiningTarget(commander.UnitCalculation.Unit, townhallPos2D, new Point2D());
                    
                    // Resolve if the target is Mineral A or B to issue HARVEST_GATHER instead of just SMART move
                    var mineral = teamAssignment.Minerals.FirstOrDefault(m => Math.Abs(nextTargetPos.X - m.Position.X) < 0.1f && Math.Abs(nextTargetPos.Y - m.Position.Y) < 0.1f);
                    if (mineral != null)
                    {
                        commands.AddRange(commander.Order(frame, Abilities.HARVEST_GATHER, null, mineral.UnitTag));
                    }
                    else
                    {
                        commands.AddRange(commander.Order(frame, Abilities.SMART, nextTargetPos));
                    }
                }

                _lastCarrying[commander.UnitCalculation.Unit.Tag] = carrying;
            }

            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> IssueTeamPatchOrders(int frame, TeamPatchAssignmentDto teamAssignment, int cyclePhase, string variant)
        {
            var commands = new List<SC2APIProtocol.Action>();
            if (teamAssignment?.Workers == null || teamAssignment.Minerals == null) return commands;

            foreach (var worker in teamAssignment.Workers)
            {
                var commander = UnitCommanders.FirstOrDefault(c => c.UnitCalculation.Unit.Tag == worker.UnitTag);
                if (commander == null) continue;

                var targetMineral = teamAssignment.Minerals[cyclePhase % teamAssignment.Minerals.Count];
                commands.AddRange(commander.Order(frame, Abilities.SMART, new Point2D { X = targetMineral.Position.X, Y = targetMineral.Position.Y }));
            }
            return commands;
        }

        private void InitializeHomePositions(MawBaseLocationData mapData) 
        {
            if (_homePositions.Count > 0) return;
            foreach (var startList in mapData.StartingUnits)
            {
                if (startList == null) continue;
                foreach (var w in startList)
                {
                    if (w.UnitTag != 0) _homePositions[w.UnitTag] = new Vector2(w.Position.X, w.Position.Y);
                }
            }
        }

        private IEnumerable<SC2APIProtocol.Action> ResetToFormation(int frame) 
        {
            var commands = new List<SC2APIProtocol.Action>();
            foreach (var commander in UnitCommanders)
            {
                if (_homePositions.TryGetValue(commander.UnitCalculation.Unit.Tag, out var home))
                {
                    commands.AddRange(commander.Order(frame, Abilities.MOVE, new Point2D { X = home.X, Y = home.Y }));
                }
            }
            return commands;
        }

        private bool AllWorkersAtHome() 
        {
            foreach (var commander in UnitCommanders)
            {
                if (_homePositions.TryGetValue(commander.UnitCalculation.Unit.Tag, out var home))
                {
                    if (Vector2.DistanceSquared(commander.UnitCalculation.Position, home) > 1.0f) return false;
                }
            }
            return true;
        }

        private bool ShouldResetCycle(MawBaseLocationData mapData, int frame) { return false; }
        private void StartOrContinueCycle(int frame, MawBaseLocationData mapData) { }
        private void FinalizeCycle(int frame, bool enemyDetected, string endReason) { }
        private void FlushPendingCycle() { }
        private bool IsCarryingResources(UnitCommander commander) { return commander.UnitCalculation.Unit.BuffIds.Any(b => b == 271 || b == 272); }
        private List<WorkerEntryDto> GetLiveWorkers(MawBaseLocationData mapData, int startIndex) 
        {
            var result = new List<WorkerEntryDto>();
            foreach (var commander in UnitCommanders)
            {
                var unit = commander.UnitCalculation.Unit;
                var label = _babySharkMiningManager.WorkerLabelService.GetLabel(unit.Tag) ?? string.Empty;
                result.Add(new WorkerEntryDto { UnitTag = unit.Tag, Position = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z), Label = label, FinalLabel = label });
            }
            return result;
        }
    }
}
