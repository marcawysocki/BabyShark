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
        private SpawningPoolPlacementService _spawningPoolPlacementService;
        private bool _started;
        private bool _labelsInitialized;
        private int _greedyChainStartIndex = -1;
        private int _greedyChainWorkerCount = -1;

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
            _greedyChainWorkerCount = -1;
        }

        public void ConfigureLabelServices(
            WorkerLabelService workerLabelService,
            MineralLabelService mineralLabelService,
            VespeneLabelService vespeneLabelService,
            SpawningPoolPlacementService spawningPoolPlacementService)
        {
            _workerLabelService = workerLabelService;
            _activeBuild?.ConfigureWorkerLabelService(workerLabelService);
            _mineralLabelService = mineralLabelService;
            _vespeneLabelService = vespeneLabelService;
            _spawningPoolPlacementService = spawningPoolPlacementService;
            _labelsInitialized = false;
            _greedyChainWorkerCount = -1;
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

        private static List<OrderedMineral> BuildRuntimeGreedyMinerals(
            List<MineralDto> liveMinerals,
            List<WorkerEntryDto> liveWorkers,
            Vector2Dto mineralCenterOfMass,
            Vector2Dto townhallPosition,
            int startIndex)
        {
            if (liveMinerals == null || liveMinerals.Count == 0
                || liveWorkers == null || liveWorkers.Count == 0
                || mineralCenterOfMass == null || townhallPosition == null)
            {
                return new List<OrderedMineral>();
            }

            var workerTuples = liveWorkers
                .Where(worker => worker?.Position != null && worker.UnitTag != 0)
                .Select(worker => (worker.UnitTag, worker.Position.X, worker.Position.Y, worker.Position.Z, worker.UnitType));
            var greedyWorkers = WorkerLabelChainHelper.BuildGreedyWorkerEntries(workerTuples, mineralCenterOfMass, null);
            var w1Position = greedyWorkers.LastOrDefault()?.Position;
            if (w1Position == null)
            {
                return new List<OrderedMineral>();
            }

            var mineralPositions = liveMinerals
                .Where(mineral => mineral?.Position != null && mineral.UnitTag != 0)
                .ToList();
            if (mineralPositions.Count == 0)
            {
                return new List<OrderedMineral>();
            }

            var temporaryTraversal = BuildClosestTraversal(
                mineralPositions.Select(mineral => mineral.Position).ToList(),
                w1Position,
                false);
            if (temporaryTraversal.Count != mineralPositions.Count)
            {
                return new List<OrderedMineral>();
            }

            var highEndPosition = temporaryTraversal[^1];
            var finalTraversal = BuildClosestTraversal(
                mineralPositions.Select(mineral => mineral.Position).ToList(),
                highEndPosition,
                true);
            if (finalTraversal.Count != mineralPositions.Count)
            {
                return new List<OrderedMineral>();
            }

            finalTraversal.Reverse();
            var resourceValues = mineralPositions
                .Select(mineral => Math.Max(0, mineral.MineralContents))
                .Distinct()
                .OrderBy(value => value)
                .ToList();
            if (resourceValues.Count < 2)
            {
                Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] cannot classify greedy minerals without distinct observed resource values.");
                return new List<OrderedMineral>();
            }

            var largeResourceValue = resourceValues[^1];
            var result = new List<OrderedMineral>(finalTraversal.Count);
            for (var orderIndex = 0; orderIndex < finalTraversal.Count; orderIndex++)
            {
                var position = finalTraversal[orderIndex];
                var observed = mineralPositions.First(mineral => DistanceSquared(mineral.Position, position) <= 0.0001f);
                var resources = (uint)Math.Max(0, observed.MineralContents);
                var isLarge = resources == largeResourceValue;
                var linePoints = BuildMineralLinePoints(position, townhallPosition);
                result.Add(new OrderedMineral
                {
                    Position = new Vector2Dto(position.X, position.Y, position.Z),
                    HarvestPoint = linePoints.HarvestPoint,
                    SmHarvestPoint = linePoints.SmHarvestPoint,
                    ReturnPoint = linePoints.ReturnPoint,
                    SmReturnPoint = linePoints.SmReturnPoint,
                    Index = orderIndex + 1,
                    OriginalIndex = mineralPositions.IndexOf(observed),
                    DistanceFromCOM = Distance(observed.Position, mineralCenterOfMass),
                    DistanceToTownhall = Distance(observed.Position, townhallPosition),
                    Resources = resources,
                    IsNear = isLarge,
                    IsLarge = isLarge,
                    IsFar = !isLarge,
                    Size = isLarge ? MineralSize.Large : MineralSize.Small,
                    UnitTag = observed.UnitTag
                });
            }

            Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] built runtime greedy minerals={result.Count} from live observation.");
            return result;
        }

        private static List<Vector2Dto> BuildClosestTraversal(
            List<Vector2Dto> positions,
            Vector2Dto anchor,
            bool includeAnchor)
        {
            var remaining = positions
                .Select((position, index) => (position, index))
                .ToList();
            var traversal = new List<Vector2Dto>();
            var current = anchor;

            if (includeAnchor)
            {
                var anchorEntry = remaining
                    .OrderBy(entry => DistanceSquared(entry.position, current))
                    .ThenBy(entry => entry.position.X)
                    .ThenBy(entry => entry.position.Y)
                    .FirstOrDefault();
                if (anchorEntry.position == null || DistanceSquared(anchorEntry.position, current) > 0.0001f)
                {
                    return traversal;
                }

                traversal.Add(anchorEntry.position);
                remaining.Remove(anchorEntry);
                current = anchorEntry.position;
            }

            while (remaining.Count > 0)
            {
                var next = remaining
                    .OrderBy(entry => DistanceSquared(entry.position, current))
                    .ThenBy(entry => entry.position.X)
                    .ThenBy(entry => entry.position.Y)
                    .First();
                traversal.Add(next.position);
                remaining.Remove(next);
                current = next.position;
            }

            return traversal;
        }

        private void BuildGreedyMineralChainFromObservation()
        {
            var mapData = Globals.CurrentMapData;
            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var workerCount = snapshot.SelfUnits?.Values.Count(worker => worker != null && IsWorkerType(worker.UnitType)) ?? 0;
            if (mapData == null || snapshot == null || startIndex < 0
                || (_greedyChainStartIndex == startIndex && _greedyChainWorkerCount == workerCount))
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
            var visibleMinerals = snapshot.VisibleMinerals
                .Where(mineral => mineral?.IsVisible == true
                    && mineral.MineralContents != 0
                    && mineral.Position != null
                    && mineral.UnitTag != 0)
                .ToList();
            var workers = snapshot.SelfUnits?.Values
                .Where(worker => worker?.Position != null && IsWorkerType(worker.UnitType))
                .ToList() ?? new List<WorkerEntryDto>();

            if (townhall == null || com == null || visibleMinerals.Count != 8 || workers.Count == 0)
            {
                return;
            }

            var ordered = BuildRuntimeGreedyMinerals(visibleMinerals, workers, com, townhall, startIndex);
            if (ordered.Count != visibleMinerals.Count)
            {
                return;
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
            var workerTuples = workers
                .Where(worker => worker?.Position != null && worker.UnitTag != 0)
                .Select(worker => (worker.UnitTag, worker.Position.X, worker.Position.Y, worker.Position.Z, worker.UnitType));
            var workersForAssignment = WorkerLabelChainHelper.BuildGreedyWorkerEntries(workerTuples, com, _workerLabelService);
            OrderRuntimeVespeneChain(mapData, startIndex, snapshot.Vespene.Values.ToList(), workersForAssignment, townhall);
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
            _labelsInitialized = false;
            var currentTownHallUnitId = Globals.CurrentObservation?.CurrentTownHalls?.Values
                .FirstOrDefault(unit => unit?.Position != null
                    && DistanceSquared(unit.Position, townhall) <= 1f)?.UnitTag ?? 0;
            PopulateAssignedWorkersAndCrossTable(mapData, startIndex, mapData.TeamPatchAssignments[startIndex], townhall, currentTownHallUnitId);
            _greedyChainStartIndex = startIndex;
            _greedyChainWorkerCount = workersForAssignment.Count;

            Console.WriteLine($"BabySharkBuildManager: built greedy mineral chain for start[{startIndex}] from {ordered.Count} observed minerals for workerCount={_greedyChainWorkerCount}.");
        }

        private void OrderRuntimeVespeneChain(
            MawBaseLocationData mapData,
            int startIndex,
            List<OrderedVespene> observedVespenes,
            List<WorkerEntryDto> greedyWorkers,
            Vector2Dto townhall)
        {
            var anchorMineral = Globals.CurrentMapData?.OrderedMainMinerals?
                .ElementAtOrDefault(startIndex)?
                .FirstOrDefault(mineral => mineral?.Index == 3);
            if (anchorMineral?.Position == null || observedVespenes == null || observedVespenes.Count == 0 || townhall == null)
            {
                return;
            }

            var ordered = observedVespenes
                .Where(vespene => vespene?.Position != null && vespene.UnitTag != 0)
                .Select(vespene => new
                {
                    Vespene = vespene,
                    Distance = Distance(vespene.Position, anchorMineral.Position)
                })
                .OrderBy(item => item.Distance)
                .ToList();
            if (ordered.Count != 2)
            {
                return;
            }

            var result = new List<OrderedVespene>(2);
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                var linePoints = BuildMineralLinePoints(item.Vespene.Position, townhall);
                result.Add(new OrderedVespene
                {
                    Position = item.Vespene.Position,
                    HarvestPoint = linePoints.HarvestPoint,
                    ReturnPoint = linePoints.ReturnPoint,
                    Index = index + 1,
                    DistanceToW4 = item.Distance,
                    Label = index == 0 ? "VA" : "VB",
                    UnitTag = item.Vespene.UnitTag
                });
            }

            mapData.OrderedMainVespene ??= new List<List<OrderedVespene>>();
            while (mapData.OrderedMainVespene.Count <= startIndex)
            {
                mapData.OrderedMainVespene.Add(new List<OrderedVespene>());
            }

            if (result.Count < 2 || result[0].Label != "VA" || result[1].Label != "VB")
            {
                return;
            }

            mapData.OrderedMainVespene[startIndex] = result;
            mapData.VespeneFinalLabelsByPosition ??= new Dictionary<string, string>();
            foreach (var vespene in result)
            {
                mapData.VespeneFinalLabelsByPosition[$"{vespene.Position.X:F2},{vespene.Position.Y:F2}"] = vespene.Label;
                _vespeneLabelService?.SetVespeneLabel(vespene.Label, new Point
                {
                    X = vespene.Position.X,
                    Y = vespene.Position.Y,
                    Z = vespene.Position.Z + 1.0f
                }, ProcessVisableUnits.GetFinalLabelColor(vespene.Label));
            }

            var placement = _spawningPoolPlacementService?.CalculateSpawningPoolPlacement(
                townhall,
                mapData.MineralCenterOfMass[startIndex],
                result[1].Position);
            if (placement != null)
            {
                _spawningPoolPlacementService.DrawPlacement(placement);
            }

            Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] ordered VA/VB from runtime M[3] anchor and completed spawning-pool placement.");
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
            const float mineralHarvestOffset = 1.5f;
            var harvest = new Vector2Dto(resource.X - ux * mineralHarvestOffset, resource.Y - uy * mineralHarvestOffset, resource.Z);
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
            var currentAssignments = Globals.CurrentMapData.TeamPatchAssignments.ElementAtOrDefault(startIndex);
            var assignedMinerals = currentAssignments?
                .SelectMany(assignment => assignment.Minerals)
                .ToList();
            _labelsInitialized = currentAssignments != null
                && currentAssignments.Count == 4
                && assignedMinerals != null
                && assignedMinerals.Count == 8
                && assignedMinerals.All(mineral => mineral != null
                    && mineral.Position != null
                    && !string.IsNullOrWhiteSpace(mineral.FinalLabel));
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
