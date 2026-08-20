using System;
using System.Collections.Generic;
using System.Linq;
using BabySharkBot.Services;
using SC2APIProtocol;

#nullable enable

namespace BabySharkBot.Setup
{
    public static class MapLabelRegistrationHelper
    {
        public static void RegisterLabels(
            MawBaseLocationData mapData,
            int startIndex,
            MineralLabelService? mineralLabelService = null,
            VespeneLabelService? vespeneLabelService = null)
        {
            if (mapData == null || startIndex < 0)
            {
                return;
            }

            RegisterMineralLabels(mapData, startIndex, mineralLabelService);
            RegisterVespeneLabels(mapData, startIndex, vespeneLabelService);
        }

        private static void RegisterMineralLabels(MawBaseLocationData mapData, int startIndex, MineralLabelService? mineralLabelService)
        {
            if (mineralLabelService == null)
            {
                return;
            }

            if (mapData.TeamPatchAssignments == null
                || startIndex < 0
                || startIndex >= mapData.TeamPatchAssignments.Count
                || mapData.TeamPatchAssignments[startIndex] == null
                || mapData.TeamPatchAssignments[startIndex].Count == 0)
            {
                Console.WriteLine($"MapLabelRegistrationHelper: No current-spawn team assignments for start[{startIndex}]; mineral labels not registered.");
                return;
            }

            var minerals = mapData.TeamPatchAssignments[startIndex]
                .SelectMany(assignment => assignment?.Minerals ?? new List<OrderedMineral>())
                .OrderBy(mineral => mineral?.Index)
                .ToList();

            if (minerals.Count != 8
                || minerals.Any(mineral => mineral?.Position == null || string.IsNullOrWhiteSpace(mineral.FinalLabel)))
            {
                Console.WriteLine($"MapLabelRegistrationHelper: Current-spawn team assignments for start[{startIndex}] are incomplete; mineral labels not registered.");
                return;
            }

            foreach (var mineral in minerals)
            {
                var displayLabel = mineral.Label;
                mapData.MineralFinalLabelsByPosition[$"{mineral.Position.X:F2},{mineral.Position.Y:F2}"] = displayLabel;

                mineralLabelService.SetMineralLabel(displayLabel, new Point
                {
                    X = mineral.Position.X,
                    Y = mineral.Position.Y,
                    Z = mineral.Position.Z + 0.5f
                }, ProcessVisableUnits.GetFinalLabelColor(mineral.FinalLabel), mineral.UnitTag);
            }
        }

        private static void RegisterVespeneLabels(MawBaseLocationData mapData, int startIndex, VespeneLabelService? vespeneLabelService)
        {
            if (vespeneLabelService == null)
            {
                return;
            }

            var vespenes = ResolveVespenes(mapData, startIndex);
            foreach (var vespene in vespenes)
            {
                if (vespene?.Position == null)
                {
                    continue;
                }

                var label = vespene.Label;
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }
                mapData.VespeneFinalLabelsByPosition ??= new Dictionary<string, string>();
                mapData.VespeneFinalLabelsByPosition[$"{vespene.Position.X:F2},{vespene.Position.Y:F2}"] = label;

                vespeneLabelService.SetVespeneLabel(label, new Point
                {
                    X = vespene.Position.X,
                    Y = vespene.Position.Y,
                    Z = vespene.Position.Z + 1.0f
                }, ProcessVisableUnits.GetFinalLabelColor(label));
            }
        }

        private static List<OrderedVespene> ResolveVespenes(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData.OrderedMainVespene.Count > startIndex)
            {
                return mapData.OrderedMainVespene[startIndex];
            }

            return new List<OrderedVespene>();
        }

        private static float DistanceSquared(float x1, float y1, float x2, float y2)
        {
            var dx = x1 - x2;
            var dy = y1 - y2;
            return dx * dx + dy * dy;
        }
    }
}
