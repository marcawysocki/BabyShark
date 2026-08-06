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

            var orderedMinerals = ResolveMinerals(mapData, startIndex);
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
                    var label = om.FinalLabel ?? om.Label;
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        mineralLabelService?.SetMineralLabel(label, new Point { X = om.Position.X, Y = om.Position.Y, Z = om.Position.Z + 0.5f }, ProcessVisableUnits.GetFinalLabelColor(label), om.UnitTag);
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

            if (mapData.AssignmentsByWorkerCount != null
                && mapData.AssignmentsByWorkerCount.TryGetValue(Settings.WorkerCount, out var assignmentsByStart)
                && assignmentsByStart != null
                && assignmentsByStart.Count > startIndex
                && assignmentsByStart[startIndex] != null
                && assignmentsByStart[startIndex].Count > 0)
            {
                return assignmentsByStart[startIndex];
            }

            return new List<TeamPatchAssignmentDto>();
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
    }
}
