using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using BabySharkBot.MicroTasks;
using BabySharkBot.Services;

#nullable enable

namespace BabySharkBot.Setup
{
    public class OngoingMapData
    {
        public MawBaseLocationData RefreshMiningData(
            ResponseGameInfo gameInfo,
            ResponseObservation observation,
            MawBaseLocationData mapData,
            WorkerLabelService? workerLabelService = null,
            CrosshairService? crosshairService = null,
            MineralLabelService? mineralLabelService = null,
            VespeneLabelService? vespeneLabelService = null)
        {
            Console.WriteLine("OngoingMapData: refreshing visible unit ids and labels for known map/spawn");

            mapData ??= Globals.CurrentMapData;
            if (mapData == null)
            {
                throw new InvalidOperationException("OngoingMapData: map data was not preloaded");
            }

            mapData.MineralFinalLabelsByPosition ??= new Dictionary<string, string>();
            mapData.VespeneFinalLabelsByPosition ??= new Dictionary<string, string>();

            var startIndex = Globals.CurrentStartIndex;
            var selfUnits = observation?.Observation?.RawData?.Units?
                .Where(u => u != null && u.Alliance == Alliance.Self)
                .ToList() ?? new List<Unit>();

            var workers = selfUnits
                .Where(u => u.UnitType == (uint)UnitTypes.ZERG_DRONE || u.UnitType == (uint)UnitTypes.TERRAN_SCV || u.UnitType == (uint)UnitTypes.PROTOSS_PROBE)
                .OrderByDescending(u => u.Tag)
                .ToList();

            var workerCount = workers.Count;
            Settings.WorkerCount = workerCount;

            var workerEntries = WorkerLabelChainHelper.BuildWorkersInAW12ThroughW1Order(workers, ResolveSpawnCenter(mapData, startIndex), workerLabelService);
            if (workerEntries.Count > 0)
            {
                Console.WriteLine($"OngoingMapData: completed W12-through-W1 worker ordering for start[{startIndex}] with {workerEntries.Count} workers");
            }

            var orderedMinerals = ResolveMinerals(mapData, startIndex);
            var orderedVespene = ResolveVespene(mapData, startIndex);

            bool baseHasBeenPlayed = false;
            if (workerCount == 8)
            {
                baseHasBeenPlayed = mapData.BaseHasBeenPlayed8 != null && startIndex < mapData.BaseHasBeenPlayed8.Length && mapData.BaseHasBeenPlayed8[startIndex];
            }
            else if (workerCount == 12)
            {
                baseHasBeenPlayed = mapData.BaseHasBeenPlayed12 != null && startIndex < mapData.BaseHasBeenPlayed12.Length && mapData.BaseHasBeenPlayed12[startIndex];
            }

            if (baseHasBeenPlayed)
            {
                Settings.CurrentBaseHasBeenPlayed = true;
                if (workerCount == 12)
                {
                    if (mapData.TealM1IsFar != null && startIndex < mapData.TealM1IsFar.Length)
                    {
                        Settings.TealM1IsFar = mapData.TealM1IsFar;
                    }

                    if (mapData.YellowM8IsFar != null && startIndex < mapData.YellowM8IsFar.Length)
                    {
                        Settings.YellowM8IsFar = mapData.YellowM8IsFar;
                    }
                }
            }

            var teamAssignments = ResolveTeamAssignments(mapData, startIndex);

            if (teamAssignments == null || teamAssignments.Count == 0)
            {
                if (!mapData.AssignmentsByWorkerCount.ContainsKey(workerCount))
                {
                    mapData.AssignmentsByWorkerCount[workerCount] = new List<List<TeamPatchAssignmentDto>>();
                }
                
                var targetList = mapData.AssignmentsByWorkerCount[workerCount];
                while (targetList.Count <= startIndex)
                {
                    targetList.Add(new List<TeamPatchAssignmentDto>());
                }

                TeamLabelRegistrationHelper.EnsureTeamLabelsForStart(mapData, startIndex, orderedMinerals, workerEntries, ResolveSpawnCenter(mapData, startIndex), workerLabelService, targetList);
                teamAssignments = targetList[startIndex];
                
                if (mapData.SecondaryTeamPatchAssignments.Count <= startIndex)
                {
                    mapData.SecondaryTeamPatchAssignments.AddRange(Enumerable.Repeat(new List<TeamPatchAssignmentDto>(), startIndex - mapData.SecondaryTeamPatchAssignments.Count + 1));
                }
                mapData.SecondaryTeamPatchAssignments[startIndex] = teamAssignments;
            }

            ApplyVisibleMineralLabels(observation, orderedMinerals, mapData, mineralLabelService);

            ApplyVisibleVespeneLabels(observation, orderedVespene, mapData, vespeneLabelService);
            MapLabelRegistrationHelper.RegisterLabels(mapData, startIndex, mineralLabelService, vespeneLabelService, null);

            return mapData;
        }

        private static Vector2Dto ResolveSpawnCenter(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData.SecondaryMineralCenterOfMass.Count > startIndex && mapData.SecondaryMineralCenterOfMass[startIndex] != null)
            {
                return mapData.SecondaryMineralCenterOfMass[startIndex];
            }

            if (mapData.MineralCenterOfMass.Count > startIndex && mapData.MineralCenterOfMass[startIndex] != null)
            {
                return mapData.MineralCenterOfMass[startIndex];
            }

            return null;
        }

        private static List<OrderedMineral> ResolveMinerals(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData.SecondaryOrderedMainMinerals.Count > startIndex && mapData.SecondaryOrderedMainMinerals[startIndex].Count > 0)
            {
                return mapData.SecondaryOrderedMainMinerals[startIndex];
            }

            if (mapData.OrderedMainMinerals.Count > startIndex)
            {
                return mapData.OrderedMainMinerals[startIndex];
            }

            return new List<OrderedMineral>();
        }

        private static List<OrderedVespene> ResolveVespene(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData.OrderedMainVespene.Count > startIndex && mapData.OrderedMainVespene[startIndex].Count > 0)
            {
                return mapData.OrderedMainVespene[startIndex];
            }

            return new List<OrderedVespene>();
        }

        public static List<TeamPatchAssignmentDto> ResolveTeamAssignments(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData == null || startIndex < 0) return new List<TeamPatchAssignmentDto>();

            if (mapData.AssignmentsByWorkerCount.TryGetValue(Settings.WorkerCount, out var assignmentsByStart) && assignmentsByStart.Count > startIndex && assignmentsByStart[startIndex].Count > 0)
            {
                return assignmentsByStart[startIndex];
            }

            if (mapData.SecondaryTeamPatchAssignments.Count > startIndex && mapData.SecondaryTeamPatchAssignments[startIndex].Count > 0)
            {
                return mapData.SecondaryTeamPatchAssignments[startIndex];
            }

            if (mapData.TeamPatchAssignments.Count > startIndex)
            {
                return mapData.TeamPatchAssignments[startIndex];
            }

            return new List<TeamPatchAssignmentDto>();
        }

        private static string BuildPositionKey(float x, float y)
        {
            return $"{x:F2},{y:F2}";
        }

        private static void ApplyVisibleMineralLabels(ResponseObservation observation, IEnumerable<OrderedMineral> minerals, MawBaseLocationData mapData, MineralLabelService mineralLabelService)
        {
            if (mineralLabelService == null)
            {
                return;
            }

            var visibleMinerals = observation?.Observation?.RawData?.Units?
                .Where(u => u != null && u.Alliance == Alliance.Neutral && u.DisplayType == DisplayType.Visible && IsMineralType(u.UnitType))
                .ToList() ?? new List<Unit>();

            foreach (var mineral in minerals ?? Enumerable.Empty<OrderedMineral>())
            {
                if (mineral?.Position == null)
                {
                    continue;
                }

                var label = ResolveMineralLabel(mineral);
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                mineral.FinalLabel = label;
                mapData.MineralFinalLabelsByPosition[BuildPositionKey(mineral.Position.X, mineral.Position.Y)] = label;
            }

            foreach (var unit in visibleMinerals)
            {
                var key = BuildPositionKey(unit.Pos.X, unit.Pos.Y);
                if (!mapData.MineralFinalLabelsByPosition.TryGetValue(key, out var label) || string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                var storedMineral = minerals?.FirstOrDefault(m => m?.Position != null && BuildPositionKey(m.Position.X, m.Position.Y) == key);
                if (storedMineral != null)
                {
                    storedMineral.Label = label;
                    storedMineral.FinalLabel = label;
                }

                mineralLabelService.SetMineralLabel(label, new Point
                {
                    X = unit.Pos.X,
                    Y = unit.Pos.Y,
                    Z = unit.Pos.Z + 0.5f
                }, ProcessVisableUnits.GetFinalLabelColor(label), unit.Tag);
            }
        }

        private static void ApplyVisibleVespeneLabels(ResponseObservation observation, IEnumerable<OrderedVespene> vespenes, MawBaseLocationData mapData, VespeneLabelService vespeneLabelService)
        {
            if (vespeneLabelService == null)
            {
                return;
            }

            var visibleVespenes = observation?.Observation?.RawData?.Units?
                .Where(u => u != null && u.Alliance == Alliance.Neutral && u.DisplayType == DisplayType.Visible && IsVespeneType(u.UnitType))
                .ToList() ?? new List<Unit>();

            foreach (var vespene in vespenes ?? Enumerable.Empty<OrderedVespene>())
            {
                if (vespene?.Position == null)
                {
                    continue;
                }

                var label = ResolveVespeneLabel(vespene);
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                mapData.VespeneFinalLabelsByPosition[BuildPositionKey(vespene.Position.X, vespene.Position.Y)] = label;
            }

            foreach (var unit in visibleVespenes)
            {
                var key = BuildPositionKey(unit.Pos.X, unit.Pos.Y);
                if (!mapData.VespeneFinalLabelsByPosition.TryGetValue(key, out var label) || string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                var storedVespene = vespenes?.FirstOrDefault(v => v?.Position != null && BuildPositionKey(v.Position.X, v.Position.Y) == key);
                if (storedVespene != null)
                {
                    storedVespene.Label = label;
                }

                vespeneLabelService.SetVespeneLabel(label, new Point
                {
                    X = unit.Pos.X,
                    Y = unit.Pos.Y,
                    Z = unit.Pos.Z + 1.0f
                }, ProcessVisableUnits.GetFinalLabelColor(label));
            }
        }

        private static bool IsMineralType(uint unitType)
        {
            return unitType == (uint)UnitTypes.NEUTRAL_MINERALFIELD
                || unitType == (uint)UnitTypes.NEUTRAL_MINERALFIELD750
                || unitType == (uint)UnitTypes.NEUTRAL_RICHMINERALFIELD
                || unitType == (uint)UnitTypes.NEUTRAL_RICHMINERALFIELD750
                || unitType == (uint)UnitTypes.NEUTRAL_PURIFIERMINERALFIELD
                || unitType == (uint)UnitTypes.NEUTRAL_PURIFIERMINERALFIELD750
                || unitType == (uint)UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD
                || unitType == (uint)UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD750
                || unitType == (uint)UnitTypes.NEUTRAL_LABMINERALFIELD
                || unitType == (uint)UnitTypes.NEUTRAL_LABMINERALFIELD750
                || unitType == (uint)UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD
                || unitType == (uint)UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD750;
        }

        private static bool IsVespeneType(uint unitType)
        {
            return unitType == (uint)UnitTypes.NEUTRAL_VESPENEGEYSER
                || unitType == (uint)UnitTypes.NEUTRAL_SPACEPLATFORMGEYSER
                || unitType == (uint)UnitTypes.NEUTRAL_SHAKURASVESPENEGEYSER
                || unitType == (uint)UnitTypes.NEUTRAL_RICHVESPENEGEYSER
                || unitType == (uint)UnitTypes.NEUTRAL_PURIFIERVESPENEGEYSER
                || unitType == (uint)UnitTypes.NEUTRAL_PROTOSSVESPENEGEYSER;
        }

        private static string ResolveMineralLabel(OrderedMineral mineral)
        {
            if (mineral == null)
            {
                return string.Empty;
            }

            var label = !string.IsNullOrWhiteSpace(mineral.FinalLabel) ? mineral.FinalLabel : (!string.IsNullOrWhiteSpace(mineral.Label) ? mineral.Label : $"M{mineral.Index}");

            // If the label is a team label (e.g., TA, GB), ensure it matches the current worker count prefix
            if (label.Length >= 2 && !label.StartsWith("M") && !label.StartsWith("V"))
            {
                var teamNum = label[0] switch
                {
                    'T' or 'G' => 1,
                    'S' or 'M' or 'P' => 2,
                    'B' or 'O' => 3,
                    'Y' or 'R' => 4,
                    _ => 0
                };

                if (teamNum > 0)
                {
                    var prefix = GetCurrentTeamPrefix(teamNum);
                    var suffix = label.Substring(1);
                    return $"{prefix}{suffix}";
                }
            }

            return label;
        }

        private static string GetCurrentTeamPrefix(int teamNumber)
        {
            if (Settings.WorkerCount == 12)
            {
                return teamNumber switch { 1 => "T", 2 => "S", 3 => "B", 4 => "Y", _ => "" };
            }
            return teamNumber switch { 1 => "G", 2 => "P", 3 => "O", 4 => "R", _ => "" };
        }

        private static string ResolveVespeneLabel(OrderedVespene vespene)
        {
            if (vespene == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(vespene.Label))
            {
                return vespene.Label;
            }

            return $"V{vespene.Index}";
        }

    }
}
