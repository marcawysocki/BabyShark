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
            VespeneLabelService? vespeneLabelService = null,
            SpawningPoolPlacementService? spawningPoolPlacementService = null)
        {
            if (mapData == null || startIndex < 0)
            {
                return;
            }

            RegisterMineralLabels(mapData, startIndex, mineralLabelService);
            RegisterVespeneLabels(mapData, startIndex, vespeneLabelService);
            RegisterSpawningPoolLabel(mapData, startIndex, spawningPoolPlacementService);
        }

        private static void RegisterMineralLabels(MawBaseLocationData mapData, int startIndex, MineralLabelService? mineralLabelService)
        {
            if (mineralLabelService == null)
            {
                return;
            }

            var minerals = ResolveMinerals(mapData, startIndex);
            foreach (var mineral in minerals)
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
                mineral.Label = label;
                mapData.MineralFinalLabelsByPosition ??= new Dictionary<string, string>();
                mapData.MineralFinalLabelsByPosition[$"{mineral.Position.X:F2},{mineral.Position.Y:F2}"] = label;

                mineralLabelService.SetMineralLabel(label, new Point
                {
                    X = mineral.Position.X,
                    Y = mineral.Position.Y,
                    Z = mineral.Position.Z + 0.5f
                }, ProcessVisableUnits.GetFinalLabelColor(label), mineral.UnitTag);
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

                var label = ResolveVespeneLabel(vespene);
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

        private static void RegisterSpawningPoolLabel(MawBaseLocationData mapData, int startIndex, SpawningPoolPlacementService? spawningPoolPlacementService)
        {
            if (spawningPoolPlacementService == null)
            {
                return;
            }

            var placement = spawningPoolPlacementService.GetPlacement(mapData, startIndex);
            if (placement == null)
            {
                return;
            }

            if (mapData?.OrderedMainVespene != null && mapData.OrderedMainVespene.Count > startIndex)
            {
                var v2 = mapData.OrderedMainVespene[startIndex].FirstOrDefault(v => v != null && v.Label == "V2")?.Position;
                if (v2 != null)
                {
                    var dx = placement.X - v2.X;
                    var dy = placement.Y - v2.Y;
                    var dist = Math.Sqrt(dx * dx + dy * dy);
                    Console.WriteLine($"MapLabelRegistrationHelper: SpawningPool to V2 distance={dist:F2}");
                }
            }

            spawningPoolPlacementService.DrawPlacement(placement);
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

        private static List<OrderedVespene> ResolveVespenes(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData.OrderedMainVespene.Count > startIndex)
            {
                return mapData.OrderedMainVespene[startIndex];
            }

            return new List<OrderedVespene>();
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

            return mineral.Index switch
            {
                1 => "M8",
                2 => "M7",
                3 => "M6",
                4 => "M5",
                5 => "M4",
                6 => "M3",
                7 => "M2",
                8 => "M1",
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

        private static float DistanceSquared(float x1, float y1, float x2, float y2)
        {
            var dx = x1 - x2;
            var dy = y1 - y2;
            return dx * dx + dy * dy;
        }
    }
}
