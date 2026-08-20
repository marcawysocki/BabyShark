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
            CrosshairService? crosshairService = null,
            ExpansionCOMService? expansionCOMService = null,
            ExpansionPointService? expansionPointService = null,
            ExpansionPointDrawService? expansionPointDrawService = null,
            ProvisionalExpansionService? provisionalExpansionService = null,
            Sharky.Pathing.MapDataService? mapDataService = null,
            MawBaseLocationData? existingMapData = null)
        {
            var snapshot = Globals.CurrentObservation;
            if (snapshot == null) return Globals.CurrentMapData ?? new MawBaseLocationData();

            Console.WriteLine("SecondaryMapData: processing new spawn from snapshot");

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

            // Sync Near/Far flags from map data to settings
            if (mapData.M1IsFar != null && startIndex < mapData.M1IsFar.Length) Settings.M1IsFar = mapData.M1IsFar;
            if (mapData.M8IsFar != null && startIndex < mapData.M8IsFar.Length) Settings.M8IsFar = mapData.M8IsFar;

            // Labelling and chain establishment is now owned by chrisCrossAppleSause on Frame Zero
            // using the ObservationManager snapshot.

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

                vespene.Label = label;
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

        private static string ResolveVespeneLabel(OrderedVespene vespene)
        {
            if (vespene == null)
            {
                return string.Empty;
            }

            return vespene.Index switch
            {
                1 => "VA",
                2 => "VB",
                _ => $"V{vespene.Index}"
            };
        }
    }
}
