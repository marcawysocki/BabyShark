using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MemoryPack;
using SC2APIProtocol;
using Sharky;
using BabySharkBot.MicroTasks;
using BabySharkBot.Services;

#nullable enable

namespace BabySharkBot.Setup
{
    /// <summary>
    /// Processes a known map when a new spawn location is encountered for the first time.
    /// Handles worker labeling and team assignments for the new spawn.
    /// Syncs Near/Far mineral classification from preloaded map data.
    /// </summary>
    public class SecondaryMapData
    {
        public bool WorkInProcess { get; set; } = true;

        private readonly Dictionary<string, SecondarySpawnData> _secondarySpawnData = new Dictionary<string, SecondarySpawnData>();

        private sealed class SecondarySpawnData
        {
            public List<WorkerEntryDto> Workers { get; set; } = new List<WorkerEntryDto>();
            public List<OrderedMineral> OrderedMinerals { get; set; } = new List<OrderedMineral>();
            public List<OrderedVespene> OrderedVespenes { get; set; } = new List<OrderedVespene>();
            public List<TeamPatchAssignmentDto> TeamAssignments { get; set; } = new List<TeamPatchAssignmentDto>();
        }

        public MawBaseLocationData GetNewMiningData(
            ResponseGameInfo gameInfo,
            ResponseData data,
            ResponseObservation observation,
            int startIndex,
            WorkerLabelService? workerLabelService = null,
            CrosshairService? crosshairService = null,
            MineralLabelService? mineralLabelService = null,
            VespeneLabelService? vespeneLabelService = null,
            ExpansionCOMService? expansionCOMService = null,
            ExpansionPointService? expansionPointService = null,
            ExpansionPointDrawService? expansionPointDrawService = null,
            ProvisionalExpansionService? provisionalExpansionService = null,
            Sharky.Pathing.MapDataService? mapDataService = null,
            MawBaseLocationData? existingMapData = null)
        {
            Console.WriteLine("SecondaryMapData: processing new spawn on a known map");

            var mapData = Globals.CurrentMapData;
            if (mapData == null)
            {
                throw new InvalidOperationException("SecondaryMapData: map data was not preloaded");
            }

            if (startIndex < 0)
            {
                Console.WriteLine("SecondaryMapData: unable to resolve spawn index; no matching start townhall found");
                return mapData;
            }

            var startKey = $"start-{startIndex}";

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
                Console.WriteLine($"SecondaryMapData: completed W12-through-W1 worker ordering for start[{startIndex}] with {workerEntries.Count} workers");
            }

            var orderedMinerals = mapData.OrderedMainMinerals.Count > startIndex
                ? mapData.OrderedMainMinerals[startIndex]
                : new List<OrderedMineral>();

            var orderedVespenes = mapData.OrderedMainVespene.Count > startIndex
                ? mapData.OrderedMainVespene[startIndex]
                : new List<OrderedVespene>();

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

            var teamAssignments = new List<TeamPatchAssignmentDto>();
            if (mapData.AssignmentsByWorkerCount.TryGetValue(workerCount, out var assignmentsByStart) && assignmentsByStart.Count > startIndex)
            {
                teamAssignments = assignmentsByStart[startIndex];
            }
            else if (mapData.TeamPatchAssignments.Count > startIndex)
            {
                teamAssignments = mapData.TeamPatchAssignments[startIndex];
            }

            _secondarySpawnData[startKey] = new SecondarySpawnData
            {
                Workers = workerEntries,
                OrderedMinerals = orderedMinerals,
                OrderedVespenes = orderedVespenes,
                TeamAssignments = teamAssignments
            };

            if (mapData.SecondaryStartingUnits.Count <= startIndex)
            {
                mapData.SecondaryStartingUnits.AddRange(Enumerable.Repeat(new List<WorkerEntryDto>(), startIndex - mapData.SecondaryStartingUnits.Count + 1));
            }
            if (mapData.SecondaryOrderedMainMinerals.Count <= startIndex)
            {
                mapData.SecondaryOrderedMainMinerals.AddRange(Enumerable.Repeat(new List<OrderedMineral>(), startIndex - mapData.SecondaryOrderedMainMinerals.Count + 1));
            }
            if (mapData.SecondaryMineralCenterOfMass.Count <= startIndex)
            {
                mapData.SecondaryMineralCenterOfMass.AddRange(Enumerable.Repeat(new Vector2Dto(), startIndex - mapData.SecondaryMineralCenterOfMass.Count + 1));
            }
            if (mapData.SecondaryTeamPatchAssignments.Count <= startIndex)
            {
                mapData.SecondaryTeamPatchAssignments.AddRange(Enumerable.Repeat(new List<TeamPatchAssignmentDto>(), startIndex - mapData.SecondaryTeamPatchAssignments.Count + 1));
            }

            mapData.SecondaryStartingUnits[startIndex] = workerEntries;
            mapData.StartingUnits[startIndex] = workerEntries;
            mapData.SecondaryOrderedMainMinerals[startIndex] = orderedMinerals;
            mapData.SecondaryMineralCenterOfMass[startIndex] = mapData.MineralCenterOfMass.Count > startIndex ? mapData.MineralCenterOfMass[startIndex] : new Vector2Dto();
            mapData.SecondaryTeamPatchAssignments[startIndex] = teamAssignments;

            if (!mapData.AssignmentFlagsByStart.TryGetValue(startIndex, out var flags))
            {
                flags = new Dictionary<string, bool>();
                mapData.AssignmentFlagsByStart[startIndex] = flags;
            }

            var teamAssignmentsReady = flags.TryGetValue("TeamAssignmentsReady", out var ready) && ready;
            if (!teamAssignmentsReady || teamAssignments == null || teamAssignments.Count == 0)
            {
                TeamLabelRegistrationHelper.EnsureTeamLabelsForStart(mapData, startIndex, orderedMinerals, workerEntries, mapData.SecondaryMineralCenterOfMass[startIndex], workerLabelService, mapData.SecondaryTeamPatchAssignments);
                flags["TeamAssignmentsReady"] = true;
            }

            ApplyMineralLabels(orderedMinerals, mineralLabelService);
            ApplyVespeneLabels(orderedVespenes, vespeneLabelService);
            MapLabelRegistrationHelper.RegisterLabels(mapData, startIndex, mineralLabelService, vespeneLabelService, null);

            return mapData;
        }

        public static int ResolveSpawnIndex(MawBaseLocationData mapData, ResponseObservation observation)
        {
            if (mapData?.StartingTownHall == null || mapData.StartingTownHall.Length == 0)
            {
                return -1;
            }

            var apiLoc = observation?.Observation?.RawData?.Units?.FirstOrDefault(u => u != null && u.Alliance == Alliance.Self && (u.UnitType == 59u || u.UnitType == 18u || u.UnitType == 104u))?.Pos;
            if (apiLoc == null)
            {
                return -1;
            }

            const float tolerance = 3f;

            for (var i = 0; i < mapData.StartingTownHall.Length; i++)
            {
                var townhall = mapData.StartingTownHall[i];
                if (townhall == null)
                {
                    continue;
                }

                if (Math.Abs(apiLoc.X - townhall.X) <= tolerance && Math.Abs(apiLoc.Y - townhall.Y) <= tolerance)
                {
                    return i;
                }
            }

            return -1;
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

            return new Vector2Dto();
        }

        private static void ApplyMineralLabels(IEnumerable<OrderedMineral> minerals, MineralLabelService? mineralLabelService)
        {
            if (mineralLabelService == null)
            {
                return;
            }

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

                mineralLabelService.SetMineralLabel(label, new Point
                {
                    X = mineral.Position.X,
                    Y = mineral.Position.Y,
                    Z = mineral.Position.Z
                }, ProcessVisableUnits.GetFinalLabelColor(label), mineral.UnitTag);
            }
        }

        private static void ApplyVespeneLabels(IEnumerable<OrderedVespene> vespenes, VespeneLabelService? vespeneLabelService)
        {
            if (vespeneLabelService == null)
            {
                return;
            }

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

                vespeneLabelService.SetVespeneLabel(label, new Point
                {
                    X = vespene.Position.X,
                    Y = vespene.Position.Y,
                    Z = vespene.Position.Z + 1.0f
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

            if (!string.IsNullOrWhiteSpace(mineral.FinalLabel))
            {
                return mineral.FinalLabel;
            }

            if (!string.IsNullOrWhiteSpace(mineral.Label))
            {
                return mineral.Label;
            }

            return mineral.TeamLabel switch
            {
                "N1" => "M1",
                "F1" => "M2",
                "N2" => "M3",
                "F2" => "M4",
                "N3" => "M5",
                "F3" => "M6",
                "N4" => "M7",
                "F4" => "M8",
                _ => $"M{mineral.Index}"
            };
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
