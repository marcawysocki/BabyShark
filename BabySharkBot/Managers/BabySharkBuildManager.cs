using System;
using System.Collections.Generic;
using SC2APIProtocol;
using Sharky;
using Sharky.Managers;
using Sharky.DefaultBot;
using BabySharkBot.Services;
using BabySharkBot.Setup;
using System.Linq;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Manager that executes BabyShark builds. Replaces Sharky's BuildManager + ProductionCommander pipeline.
    /// Integrates with DefaultSharkyBot's MacroData to express production desires.
    /// Registered after ObservationManager so build decisions consume the current observation snapshot.
    /// </summary>
    public class BabySharkBuildManager : IManager
    {
        public bool NeverSkip { get; set; } = true;
        public bool SkipFrame { get; set; } = false;
        public double LongestFrame { get; set; } = 0;
        public double TotalFrameTime { get; set; } = 0;

        private readonly DefaultSharkyBot _defaultBot;
        private BabySharkBot.Builds.BabySharkBuild? _activeBuild;
        private WorkerLabelService _workerLabelService;
        private MineralLabelService _mineralLabelService;
        private VespeneLabelService _vespeneLabelService;
        private bool _started;
        private bool _labelsInitialized;
        private int _greedyChainStartIndex = -1;

        public BabySharkBuildManager(DefaultSharkyBot defaultBot)
        {
            _defaultBot = defaultBot;
        }

        public void SetBuild(BabySharkBot.Builds.BabySharkBuild build)
        {
            _activeBuild = build;
            _started = false;
            _labelsInitialized = false;
            _greedyChainStartIndex = -1;
        }

        public void ConfigureLabelServices(
            WorkerLabelService workerLabelService,
            MineralLabelService mineralLabelService,
            VespeneLabelService vespeneLabelService)
        {
            _workerLabelService = workerLabelService;
            _activeBuild?.ConfigureWorkerLabelService(workerLabelService);
            _mineralLabelService = mineralLabelService;
            _vespeneLabelService = vespeneLabelService;
            _labelsInitialized = false;
        }

        public BabySharkBot.Builds.BabySharkBuild? ActiveBuild => _activeBuild;

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            if (_activeBuild == null) return Array.Empty<SC2APIProtocol.Action>();

            var frame = (int)observation.Observation.GameLoop;
            BuildGreedyMineralChainFromObservation();
            UpdateRuntimeLabels();

            if (!_started)
            {
                _activeBuild.OnStart(frame);
                _started = true;
            }

            var actions = _activeBuild.OnFrame(observation) ?? Array.Empty<SC2APIProtocol.Action>();

            if (_activeBuild.ShouldTransition(frame))
            {
                Console.WriteLine($"BabySharkBuildManager: Build {_activeBuild.BuildName} requested transition at frame {frame}");
            }

            return actions;
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            if (_activeBuild == null || _started)
            {
                return;
            }

            var frame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            BuildGreedyMineralChainFromObservation();
            UpdateRuntimeLabels();
            _activeBuild.OnStart(frame);
            _started = true;
        }

        private void BuildGreedyMineralChainFromObservation()
        {
            var mapData = Globals.CurrentMapData;
            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (mapData == null || snapshot == null || startIndex < 0 || _greedyChainStartIndex == startIndex)
            {
                return;
            }

            if (mapData.StartingTownHall == null || startIndex >= mapData.StartingTownHall.Length)
            {
                return;
            }

            var townhall = mapData.StartingTownHall[startIndex];
            var com = mapData.MineralCenterOfMass != null && mapData.MineralCenterOfMass.Count > startIndex
                ? mapData.MineralCenterOfMass[startIndex]
                : null;
            // Mineral identity comes from the persisted per-spawn greedy order, never
            // from the current observation dictionary or the new SC2 unit tags.
            var canonicalMinerals = mapData.SecondaryOrderedMainMinerals?.ElementAtOrDefault(startIndex);
            if (canonicalMinerals == null || canonicalMinerals.Count == 0)
            {
                canonicalMinerals = mapData.OrderedMainMinerals?.ElementAtOrDefault(startIndex);
            }

            var visibleMinerals = snapshot.Minerals?.Values
                .Where(mineral => mineral?.Position != null && mineral.UnitTag != 0)
                .ToList() ?? new List<MineralDto>();
            var workers = snapshot.SelfUnits?.Values
                .Where(worker => worker?.Position != null && IsWorkerType(worker.UnitType))
                .ToList() ?? new List<WorkerEntryDto>();

            if (townhall == null || com == null || canonicalMinerals == null || canonicalMinerals.Count == 0 || workers.Count == 0)
            {
                return;
            }

            // Copy the canonical list without changing its order or indices. Only live
            // observation fields are refreshed; position, labels, pairings, and geometry
            // remain tied to the known mineral position.
            var ordered = canonicalMinerals.ToList();
            foreach (var mineral in ordered)
            {
                var liveMineral = visibleMinerals
                    .Select(observed => new
                    {
                        Mineral = observed,
                        Distance = DistanceSquared(observed.Position, mineral.Position)
                    })
                    .Where(candidate => candidate.Distance <= 0.01f)
                    .OrderBy(candidate => candidate.Distance)
                    .FirstOrDefault();

                mineral.UnitTag = liveMineral?.Mineral.UnitTag ?? 0;
                if (liveMineral != null)
                {
                    mineral.Resources = (uint)Math.Max(0, liveMineral.Mineral.MineralContents);
                }
            }

            // The greedy chain is rebuilt from live mineral tags each game, but its route
            // geometry is persistent. Preserve cached geometry by position and calculate it
            // only when no cached geometry exists; never leave movement points at (0, 0).
            var cachedMinerals = mapData.SecondaryOrderedMainMinerals?.ElementAtOrDefault(startIndex)
                ?? mapData.OrderedMainMinerals?.ElementAtOrDefault(startIndex)
                ?? new List<OrderedMineral>();
            foreach (var mineral in ordered)
            {
                var cached = cachedMinerals.FirstOrDefault(existing =>
                    existing?.Position != null
                    && DistanceSquared(existing.Position, mineral.Position) <= 0.01f);
                if (cached != null && HasNonZeroPoint(cached.HarvestPoint) && HasNonZeroPoint(cached.ReturnPoint))
                {
                    mineral.HarvestPoint = cached.HarvestPoint;
                    mineral.SmHarvestPoint = HasNonZeroPoint(cached.SmHarvestPoint) ? cached.SmHarvestPoint : cached.HarvestPoint;
                    mineral.ReturnPoint = cached.ReturnPoint;
                    mineral.SmReturnPoint = HasNonZeroPoint(cached.SmReturnPoint) ? cached.SmReturnPoint : cached.ReturnPoint;
                    mineral.Label = cached.Label;
                    mineral.FinalLabel = cached.FinalLabel;
                    mineral.TeamLabel = cached.TeamLabel;
                    mineral.TeamNumber = cached.TeamNumber;
                }
                else
                {
                    var points = BuildMineralLinePoints(mineral.Position, townhall);
                    mineral.HarvestPoint = points.HarvestPoint;
                    mineral.SmHarvestPoint = points.SmHarvestPoint;
                    mineral.ReturnPoint = points.ReturnPoint;
                    mineral.SmReturnPoint = points.SmReturnPoint;
                }
            }

            mapData.OrderedMainMinerals ??= new List<List<OrderedMineral>>();
            while (mapData.OrderedMainMinerals.Count <= startIndex)
            {
                mapData.OrderedMainMinerals.Add(new List<OrderedMineral>());
            }
            mapData.OrderedMainMinerals[startIndex] = ordered;
            mapData.StartingMinerals ??= new List<List<OrderedMineral>>();
            while (mapData.StartingMinerals.Count <= startIndex)
            {
                mapData.StartingMinerals.Add(new List<OrderedMineral>());
            }
            mapData.StartingMinerals[startIndex] = ordered;
            mapData.TeamPatchAssignments ??= new List<List<TeamPatchAssignmentDto>>();
            var workersForAssignment = workers
                .OrderByDescending(worker => DistanceSquared(worker.Position, com))
                .Select((worker, index) => new WorkerEntryDto
                {
                    UnitTag = worker.UnitTag,
                    UnitType = worker.UnitType,
                    Position = worker.Position,
                    Label = $"W{workers.Count - index}",
                    StartLabel = $"W{workers.Count - index}",
                    FinalLabel = $"W{workers.Count - index}"
                })
                .ToList();
            // Rebuild the current game's assignment records from the current worker tags.
            // The cached geometry is reusable; SC2 worker tags are not.
            while (mapData.TeamPatchAssignments.Count <= startIndex)
            {
                mapData.TeamPatchAssignments.Add(new List<TeamPatchAssignmentDto>());
            }
            mapData.TeamPatchAssignments[startIndex] = new List<TeamPatchAssignmentDto>();
            mapData.TeamPatchAssignments[startIndex] = TeamLabelRegistrationHelper.EnsureTeamLabelsForStart(
                mapData,
                startIndex,
                ordered,
                workersForAssignment,
                com,
                _workerLabelService,
                mapData.TeamPatchAssignments);
            var currentTownHallUnitId = Globals.CurrentObservation?.CurrentTownHalls?.Values
                .FirstOrDefault(unit => unit?.Position != null
                    && DistanceSquared(unit.Position, townhall) <= 1f)?.UnitTag ?? 0;
            PopulateAssignedWorkersAndCrossTable(mapData, startIndex, mapData.TeamPatchAssignments[startIndex], townhall, currentTownHallUnitId);
            _greedyChainStartIndex = startIndex;

            Console.WriteLine($"BabySharkBuildManager: built greedy mineral chain for start[{startIndex}] from {ordered.Count} observed minerals before target indexing.");
        }

        private static void PopulateAssignedWorkersAndCrossTable(
            MawBaseLocationData mapData,
            int startIndex,
            List<TeamPatchAssignmentDto> assignments,
            Vector2Dto townhall,
            ulong townHallUnitId)
        {
            while (mapData.AssignedWorkers.Count <= startIndex)
            {
                mapData.AssignedWorkers.Add(new List<AssignedWorkerDto>());
            }

            var allMineralsByLabel = (assignments ?? new List<TeamPatchAssignmentDto>())
                .SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                .Where(mineral => !string.IsNullOrWhiteSpace(mineral.FinalLabel))
                .GroupBy(mineral => mineral.FinalLabel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var workerCount = (assignments ?? new List<TeamPatchAssignmentDto>())
                .SelectMany(assignment => assignment.Workers ?? new List<WorkerEntryDto>())
                .Count();
            var assignedWorkers = new List<AssignedWorkerDto>();
            foreach (var assignment in assignments ?? new List<TeamPatchAssignmentDto>())
            {
                foreach (var worker in assignment.Workers ?? new List<WorkerEntryDto>())
                {
                    var assigned = new AssignedWorkerDto
                    {
                        UnitID = worker.UnitTag,
                        CurrentXY = worker.Position,
                        Role = worker.FinalLabel,
                        TownHallUnitID = townHallUnitId,
                        Mti = 0
                    };

                    foreach (var targetLabel in GetInstructionLabels(worker.FinalLabel, assignment.TeamNumber, workerCount))
                    {
                        if (!allMineralsByLabel.TryGetValue(targetLabel, out var targetMineral))
                        {
                            Console.WriteLine($"[ASSIGNMENT ERROR] role={worker.FinalLabel} target={targetLabel} is not present in BuildManager labels");
                            continue;
                        }

                        var isInitial = assigned.MiningTargets.Count == 0;
                        assigned.MiningTargets.Add(CreateMiningTarget(
                            targetMineral,
                            targetMineral,
                            townhall,
                            townHallUnitId,
                            !isInitial,
                            !isInitial));
                    }

                    assignedWorkers.Add(assigned);
                }
            }

            mapData.AssignedWorkers[startIndex] = assignedWorkers;

            while (mapData.MiningTargetCrossTables.Count <= startIndex)
            {
                mapData.MiningTargetCrossTables.Add(new MiningTargetCrossTableDto());
            }

            var labels = assignments?
                .SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                .Where(mineral => !string.IsNullOrWhiteSpace(mineral.FinalLabel))
                .Select(mineral => mineral.FinalLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            var crossTable = new MiningTargetCrossTableDto
            {
                StartIndex = startIndex,
                Calculated = true,
                ResourceLabels = labels
            };

            foreach (var fromLabel in labels)
            {
                var fromMineral = assignments.SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                    .First(mineral => string.Equals(mineral.FinalLabel, fromLabel, StringComparison.OrdinalIgnoreCase));
                var routes = new List<MiningTargetDto>();
                foreach (var toLabel in labels)
                {
                    var toMineral = assignments.SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                        .First(mineral => string.Equals(mineral.FinalLabel, toLabel, StringComparison.OrdinalIgnoreCase));
                    routes.Add(CreateMiningTarget(fromMineral, toMineral, townhall, townHallUnitId, string.Equals(fromLabel, toLabel, StringComparison.OrdinalIgnoreCase), !string.Equals(fromLabel, toLabel, StringComparison.OrdinalIgnoreCase)));
                }
                crossTable.Routes.Add(routes);
            }

            mapData.MiningTargetCrossTables[startIndex] = crossTable;
            Console.WriteLine($"BabySharkBuildManager: AssignedWorkers start[{startIndex}]={assignedWorkers.Count}, cross-table labels={labels.Count}, calculated={crossTable.Calculated}");
        }

        private static IReadOnlyList<string> GetInstructionLabels(string role, int teamNumber, int workerCount)
        {
            if (workerCount != 8)
            {
                return Array.Empty<string>();
            }

            return role switch
            {
                "T1" => new[] { "TA", "TA", "SA" },
                "T2" => new[] { "TB", "TA", "SA" },
                "S1" => new[] { "SA", "SA", "TA" },
                "S2" => new[] { "SB" },
                "B1" => new[] { "BA", "BA", "YA" },
                "B2" => new[] { "BB" },
                "Y1" => new[] { "YA", "YA", "BA" },
                "Y2" => new[] { "YB", "YA", "SA" },
                _ => Array.Empty<string>()
            };
        }

        private static MiningTargetDto CreateMiningTarget(OrderedMineral from, OrderedMineral to, Vector2Dto townhall, ulong townHallUnitId, bool speedMining, bool abSwitch)
        {
            var harvest = speedMining ? from.HarvestPoint : to.HarvestPoint;
            var returnPoint = speedMining ? from.ReturnPoint : to.ReturnPoint;
            return new MiningTargetDto
            {
                ResourceLabel = to.FinalLabel,
                FromResourceLabel = from.FinalLabel,
                ToResourceLabel = to.FinalLabel,
                ResourceUnitId = to.UnitTag,
                ResourcePosition = to.Position,
                TownHallUnitId = townHallUnitId,
                HarvestPoint = harvest,
                ReturnPoint = returnPoint,
                IsSpeedMining = speedMining,
                IsABSwitch = abSwitch,
                IsInitialMineralAssignment = !abSwitch
            };
        }

        private static bool HasNonZeroPoint(Vector2Dto point)
        {
            return point != null && (point.X != 0f || point.Y != 0f);
        }

        private static (Vector2Dto HarvestPoint, Vector2Dto SmHarvestPoint, Vector2Dto ReturnPoint, Vector2Dto SmReturnPoint) BuildMineralLinePoints(Vector2Dto resource, Vector2Dto townhall)
        {
            var dx = resource.X - townhall.X;
            var dy = resource.Y - townhall.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= 0.001f)
            {
                return (new Vector2Dto(resource.X, resource.Y, resource.Z), new Vector2Dto(resource.X, resource.Y, resource.Z), new Vector2Dto(townhall.X, townhall.Y, townhall.Z), new Vector2Dto(townhall.X, townhall.Y, townhall.Z));
            }

            var ux = dx / distance;
            var uy = dy / distance;
            var harvest = new Vector2Dto(resource.X - ux, resource.Y - uy, resource.Z);
            var smHarvest = new Vector2Dto(resource.X - ux * 2.75f, resource.Y - uy * 2.75f, resource.Z);
            var ret = new Vector2Dto(townhall.X + ux * 2.75f, townhall.Y + uy * 2.75f, townhall.Z);
            var smReturn = new Vector2Dto(townhall.X + ux, townhall.Y + uy, townhall.Z);
            return (harvest, smHarvest, ret, smReturn);
        }

        private static float DistanceSquared(Vector2Dto first, Vector2Dto second)
        {
            if (first == null || second == null)
            {
                return float.MaxValue;
            }

            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }

        private static float Distance(Vector2Dto first, Vector2Dto second)
        {
            return MathF.Sqrt(DistanceSquared(first, second));
        }

        private void UpdateRuntimeLabels()
        {
            var snapshot = Globals.CurrentObservation;
            if (_labelsInitialized || snapshot == null || Globals.CurrentMapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (startIndex < 0)
            {
                return;
            }

            var liveWorkers = snapshot.SelfUnits.Values
                .Where(worker => worker != null && IsWorkerType(worker.UnitType) && worker.UnitTag != 0)
                .ToList();

            var mineralCom = Globals.CurrentMapData.MineralCenterOfMass != null
                && Globals.CurrentMapData.MineralCenterOfMass.Count > startIndex
                ? Globals.CurrentMapData.MineralCenterOfMass[startIndex]
                : null;
            var orderedMinerals = Globals.CurrentMapData.OrderedMainMinerals != null
                && Globals.CurrentMapData.OrderedMainMinerals.Count > startIndex
                ? Globals.CurrentMapData.OrderedMainMinerals[startIndex]
                : null;

            if (_workerLabelService != null && mineralCom != null && liveWorkers.Count > 0)
            {
                var workerTuples = liveWorkers.Select(worker =>
                    (worker.UnitTag, worker.Position.X, worker.Position.Y, worker.Position.Z, worker.UnitType));
                var startingWorkers = WorkerLabelChainHelper.BuildGreedyWorkerEntries(workerTuples, mineralCom, _workerLabelService);

                if (orderedMinerals != null && startingWorkers.Count == liveWorkers.Count)
                {
                    TeamLabelRegistrationHelper.EnsureTeamLabelsForStart(
                        Globals.CurrentMapData,
                        startIndex,
                        orderedMinerals,
                        startingWorkers,
                        mineralCom,
                        _workerLabelService,
                        Globals.CurrentMapData.TeamPatchAssignments);
                }
            }

            MapLabelRegistrationHelper.RegisterLabels(
                Globals.CurrentMapData,
                startIndex,
                _mineralLabelService,
                _vespeneLabelService);

            RegisterUnitTypeLabels(snapshot);
            _labelsInitialized = true;
        }

        private void RegisterUnitTypeLabels(ObservationSnapshotDto snapshot)
        {
            if (_workerLabelService == null) return;

            var hatcheryNumber = 0;
            var overlordNumber = 0;
            foreach (var unit in snapshot.CurrentTownHalls.Values.OrderBy(unit => unit.UnitTag))
            {
                hatcheryNumber++;
                _workerLabelService.SetLabel($"H{hatcheryNumber}", unit.UnitTag);
            }

            foreach (var unit in snapshot.SelfUnits.Values
                .Where(unit => unit.UnitType == (uint)UnitTypes.ZERG_OVERLORD || unit.UnitType == (uint)UnitTypes.ZERG_OVERLORDTRANSPORT)
                .OrderBy(unit => unit.UnitTag))
            {
                overlordNumber++;
                _workerLabelService.SetLabel($"OV{overlordNumber}", unit.UnitTag);
            }
        }

        private static bool IsWorkerType(uint unitType)
        {
            return unitType == (uint)UnitTypes.ZERG_DRONE
                || unitType == (uint)UnitTypes.TERRAN_SCV
                || unitType == (uint)UnitTypes.PROTOSS_PROBE;
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
        }
    }
}
