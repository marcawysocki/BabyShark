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
    /// <summary>
    /// Refreshes map data every frame or on specific events.
    /// Updates unit IDs and labels for visible minerals and vespene geysers.
    /// Ensures team assignments and worker labels remain synced during the game.
    /// </summary>
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
            var snapshot = Globals.CurrentObservation;
            if (snapshot == null) return mapData;

            Console.WriteLine("OngoingMapData: refreshing visible unit ids and labels from snapshot");

            mapData ??= Globals.CurrentMapData;
            if (mapData == null)
            {
                throw new InvalidOperationException("OngoingMapData: map data was not preloaded");
            }

            var startIndex = Globals.CurrentStartIndex;
            var workerCount = snapshot.AvailableWorkers.Count + snapshot.SelfUnits.Count(u => !snapshot.AvailableWorkers.Contains(u.Key) && u.Value.UnitType == (uint)UnitTypes.ZERG_DRONE); // Rough estimate
            Settings.WorkerCount = workerCount;

            var currentAssignments = ResolveTeamAssignments(mapData, startIndex);
            var orderedMinerals = currentAssignments
                .SelectMany(assignment => assignment?.Minerals ?? new List<OrderedMineral>())
                .Where(mineral => mineral?.Position != null && !string.IsNullOrWhiteSpace(mineral.FinalLabel))
                .GroupBy(mineral => mineral.UnitTag != 0
                    ? $"tag:{mineral.UnitTag}"
                    : $"pos:{mineral.Position.X:F2},{mineral.Position.Y:F2}")
                .Select(group => group.First())
                .OrderBy(mineral => mineral.Index)
                .ToList();
            var orderedVespene = ResolveVespene(mapData, startIndex);

            // Synchronize tags and labels from snapshot
            foreach (var om in orderedMinerals)
            {
                var liveMineral = snapshot.Minerals.Values.FirstOrDefault(m => 
                    Math.Abs(m.Position.X - om.Position.X) < 0.1f && 
                    Math.Abs(m.Position.Y - om.Position.Y) < 0.1f);
                
                if (liveMineral != null)
                {
                    om.UnitTag = liveMineral.UnitTag;
                    var displayLabel = om.Label;
                    if (!string.IsNullOrWhiteSpace(displayLabel))
                    {
                        mineralLabelService?.SetMineralLabel(displayLabel, new Point { X = om.Position.X, Y = om.Position.Y, Z = om.Position.Z + 0.5f }, ProcessVisableUnits.GetFinalLabelColor(om.FinalLabel), om.UnitTag);
                    }
                }
            }

            foreach (var ov in orderedVespene)
            {
                var liveVespene = snapshot.Vespene.Values.FirstOrDefault(v => 
                    Math.Abs(v.Position.X - ov.Position.X) < 0.1f && 
                    Math.Abs(v.Position.Y - ov.Position.Y) < 0.1f);
                
                if (liveVespene != null)
                {
                    ov.UnitTag = liveVespene.UnitTag;
                    if (!string.IsNullOrWhiteSpace(ov.Label))
                    {
                        vespeneLabelService?.SetVespeneLabel(ov.Label, new Point { X = ov.Position.X, Y = ov.Position.Y, Z = ov.Position.Z + 1.0f }, ProcessVisableUnits.GetFinalLabelColor(ov.Label));
                    }
                }
            }

            return mapData;
        }

        public static List<TeamPatchAssignmentDto> ResolveTeamAssignments(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData == null || startIndex < 0)
            {
                return new List<TeamPatchAssignmentDto>();
            }

            if (mapData.TeamPatchAssignments != null
                && mapData.TeamPatchAssignments.Count > startIndex
                && mapData.TeamPatchAssignments[startIndex] != null
                && mapData.TeamPatchAssignments[startIndex].Count > 0)
            {
                return mapData.TeamPatchAssignments[startIndex];
            }

            Console.WriteLine($"OngoingMapData: no current-spawn team assignments for start[{startIndex}].");
            return new List<TeamPatchAssignmentDto>();
        }

        private static List<OrderedVespene> ResolveVespene(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData.OrderedMainVespene.Count > startIndex && mapData.OrderedMainVespene[startIndex].Count > 0)
            {
                return mapData.OrderedMainVespene[startIndex];
            }

            return new List<OrderedVespene>();
        }
    }
}
