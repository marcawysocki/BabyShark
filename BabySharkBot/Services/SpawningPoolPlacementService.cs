using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BabySharkBot.Setup;
using SC2APIProtocol;
using Sharky;

#nullable enable

namespace BabySharkBot.Services
{
    public class SpawningPoolPlacementService
    {
        private readonly DebugService _debugService;
        private Point2D? _lastPlacement;

        public SpawningPoolPlacementService(DebugService debugService)
        {
            _debugService = debugService;
        }

        public Point2D? GetPlacement(BabySharkBot.Setup.MawBaseLocationData mapData, int startIndex = 0)
        {
            if (mapData == null || mapData.StartingTownHall == null || mapData.MineralCenterOfMass == null || mapData.OrderedMainVespene == null)
            {
                return null;
            }

            if (startIndex < 0 || startIndex >= mapData.StartingTownHall.Length)
            {
                return null;
            }

            if (mapData.SpawningPoolPlacements != null && mapData.SpawningPoolPlacements.Count > startIndex && mapData.SpawningPoolPlacements[startIndex] != null)
            {
                var storedPlacement = mapData.SpawningPoolPlacements[startIndex];
                _lastPlacement = new Point2D { X = storedPlacement.X, Y = storedPlacement.Y };
                return _lastPlacement;
            }

            if (mapData.MineralCenterOfMass.Count <= startIndex || mapData.MineralCenterOfMass[startIndex] == null)
            {
                return null;
            }

            if (mapData.OrderedMainVespene.Count <= startIndex || mapData.OrderedMainVespene[startIndex] == null || mapData.OrderedMainVespene[startIndex].Count == 0)
            {
                return null;
            }

            var hatchery = mapData.StartingTownHall[startIndex];
            var mineralCom = mapData.MineralCenterOfMass[startIndex];
            var vespeneVb = mapData.OrderedMainVespene[startIndex].FirstOrDefault(v => v != null && v.Label == "VB")?.Position
                ?? mapData.OrderedMainVespene[startIndex].Skip(1).FirstOrDefault()?.Position
                ?? mapData.OrderedMainVespene[startIndex].First().Position;

            if (hatchery == null || mineralCom == null || vespeneVb == null)
            {
                return null;
            }

            var placement = CalculateSpawningPoolPlacement(hatchery, mineralCom, vespeneVb);
            _lastPlacement = placement;
            return placement;
        }

        public Point2D? CalculateSpawningPoolPlacement(Vector2Dto hatcheryStart, Vector2Dto mineralCom, Vector2Dto vespeneV2)
        {
            if (hatcheryStart == null || mineralCom == null || vespeneV2 == null)
            {
                return null;
            }

            var hatcheryCenter = new Vector2(hatcheryStart.X, hatcheryStart.Y);
            var mineralCenter = new Vector2(mineralCom.X, mineralCom.Y);
            var geyserCenter = new Vector2(vespeneV2.X, vespeneV2.Y);

            var hatcheryToGeyser = Vector2.Distance(hatcheryCenter, geyserCenter);
            var hatcheryRadius = Math.Max(0f, hatcheryToGeyser - 1.5f);
            const float geyserRadius = 3f;

            var candidates = IntersectCircles(hatcheryCenter, hatcheryRadius, geyserCenter, geyserRadius);
            if (candidates.Count == 0)
            {
                return null;
            }

            var chosen = candidates[0];
            if (candidates.Count > 1)
            {
                var firstDistance = Vector2.DistanceSquared(candidates[0], mineralCenter);
                var secondDistance = Vector2.DistanceSquared(candidates[1], mineralCenter);
                chosen = secondDistance > firstDistance ? candidates[1] : candidates[0];
            }

            return new Point2D { X = chosen.X, Y = chosen.Y };
        }

        public void DrawPlacement(Point2D placement)
        {
            if (_debugService == null || placement == null)
            {
                return;
            }

            _debugService.DrawSphere(new Point { X = placement.X, Y = placement.Y, Z = 12 }, 1.5f, new Color { R = 255, G = 105, B = 180 });
            _debugService.DrawText("SpawningPool", new Point { X = placement.X, Y = placement.Y, Z = 12.5f }, new Color { R = 255, G = 105, B = 180 }, 12);
        }

        public Point2D? LastPlacement => _lastPlacement;

        private List<Vector2> IntersectCircles(Vector2 c0, float r0, Vector2 c1, float r1)
        {
            var results = new List<Vector2>();
            var d = Vector2.Distance(c0, c1);

            if (d <= 0.0001f || d > r0 + r1 || d < Math.Abs(r0 - r1))
            {
                return results;
            }

            var a = (r0 * r0 - r1 * r1 + d * d) / (2f * d);
            var hSq = Math.Max(0f, r0 * r0 - a * a);
            var h = (float)Math.Sqrt(hSq);

            var direction = (c1 - c0) / d;
            var midpoint = c0 + a * direction;
            var perpendicular = new Vector2(-direction.Y, direction.X);

            var p1 = midpoint + h * perpendicular;
            var p2 = midpoint - h * perpendicular;

            results.Add(p1);
            if (Vector2.DistanceSquared(p1, p2) > 0.0001f)
            {
                results.Add(p2);
            }

            return results;
        }
    }
}
