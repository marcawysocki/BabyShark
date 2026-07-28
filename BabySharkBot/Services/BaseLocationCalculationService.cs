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
    /// <summary>
    /// Reusable Sharky-style base location calculator.
    /// Mirrors the important parts of BaseManager's final-location logic so InitialMapData can serialize the same locations.
    /// </summary>
    public class BaseLocationCalculationService
    {
        private readonly ImageData _placementGrid;

        public BaseLocationCalculationService(ImageData placementGrid)
        {
            _placementGrid = placementGrid;
        }

        public BaseLocationResult CalculateBaseLocation(IEnumerable<Vector2Dto> mineralFields, IEnumerable<Vector2Dto> vespeneGeysers)
        {
            var minerals = mineralFields?.ToList() ?? new List<Vector2Dto>();
            var gases = vespeneGeysers?.ToList() ?? new List<Vector2Dto>();

            if (minerals.Count == 0)
            {
                return new BaseLocationResult { IsValid = false, ValidationNotes = "No minerals supplied" };
            }

            float x = 0;
            float y = 0;
            foreach (var field in minerals)
            {
                x += (int)field.X;
                y += (int)field.Y;
            }

            x /= minerals.Count;
            y /= minerals.Count;

            x = (int)x + 0.5f;
            y = (int)y + 0.5f;

            var baseLocation = new Point2D { X = x, Y = y };

            Vector2Dto? closest = null;
            var closestDistance = float.MaxValue;
            foreach (var mineralField in minerals)
            {
                var distance = Math.Abs(mineralField.X - baseLocation.X) + Math.Abs(mineralField.Y - baseLocation.Y);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = mineralField;
                }
            }

            if (closest != null)
            {
                if (closest.X < baseLocation.X)
                {
                    baseLocation.X += 2;
                }
                else if (closest.X > baseLocation.X)
                {
                    baseLocation.X -= 2;
                }

                if (closest.Y < baseLocation.Y)
                {
                    baseLocation.Y += 2;
                }
                else if (closest.Y > baseLocation.Y)
                {
                    baseLocation.Y -= 2;
                }
            }

            var bestPosition = FindBestPosition(baseLocation, minerals, gases);
            if (bestPosition == null)
            {
                return new BaseLocationResult
                {
                    IsValid = false,
                    ValidationNotes = "No valid base location found"
                };
            }

            return new BaseLocationResult
            {
                IsValid = true,
                Location = new Vector2Dto(bestPosition.X, bestPosition.Y),
                MineralLineLocation = CalculateMineralLineLocation(minerals),
                MineralFields = minerals,
                VespeneGeysers = gases,
                ValidationNotes = "Base location calculated using Sharky-style scoring"
            };
        }

        private Point2D FindBestPosition(Point2D approximateLocation, List<Vector2Dto> minerals, List<Vector2Dto> gases)
        {
            var bestScore = float.MaxValue;
            Point2D? best = null;

            for (int i = 0; i < 20; i++)
            {
                for (int j = 0; j == 0 || j < i; j++)
                {
                    EvaluateCandidate(new Point2D { X = approximateLocation.X + i - j, Y = approximateLocation.Y + j }, minerals, gases, ref bestScore, ref best);
                    EvaluateCandidate(new Point2D { X = approximateLocation.X + i - j, Y = approximateLocation.Y - j }, minerals, gases, ref bestScore, ref best);
                    EvaluateCandidate(new Point2D { X = approximateLocation.X - i + j, Y = approximateLocation.Y + j }, minerals, gases, ref bestScore, ref best);
                    EvaluateCandidate(new Point2D { X = approximateLocation.X - i + j, Y = approximateLocation.Y - j }, minerals, gases, ref bestScore, ref best);
                }
            }

            return best;
        }

        private void EvaluateCandidate(Point2D position, List<Vector2Dto> minerals, List<Vector2Dto> gases, ref float bestScore, ref Point2D? best)
        {
            var score = CheckPosition(position, minerals, gases);
            if (score < bestScore)
            {
                bestScore = score;
                best = position;
            }
        }

        private float CheckPosition(Point2D position, List<Vector2Dto> minerals, List<Vector2Dto> gases)
        {
            foreach (var mineralField in minerals)
            {
                if (Math.Abs(mineralField.X - position.X) + Math.Abs(mineralField.Y - position.Y) <= 10 &&
                    Math.Abs(mineralField.X - position.X) <= 5.5 && Math.Abs(mineralField.Y - position.Y) <= 5.5)
                {
                    return 100000000;
                }
            }

            foreach (var gas in gases)
            {
                if (Math.Abs(gas.X - position.X) + Math.Abs(gas.Y - position.Y) <= 11 &&
                    Math.Abs(gas.X - position.X) <= 6.1 && Math.Abs(gas.Y - position.Y) <= 6.1)
                {
                    return 100000000;
                }

                if (Vector2.DistanceSquared(new Vector2(gas.X, gas.Y), new Vector2(position.X, position.Y)) >= 121)
                {
                    return 100000000;
                }
            }

            for (float x = -2.5f; x < 2.5f + 0.1f; x++)
            {
                for (float y = -2.5f; y < 2.5f + 0.1f; y++)
                {
                    if (!GetTilePlacable((int)Math.Round(position.X + x), (int)Math.Round(position.Y + y)))
                    {
                        return 100000000;
                    }
                }
            }

            float maxDist = 0;
            foreach (var mineralField in minerals)
            {
                maxDist += Vector2.DistanceSquared(new Vector2(mineralField.X, mineralField.Y), new Vector2(position.X, position.Y));
            }

            foreach (var gas in gases)
            {
                maxDist += Vector2.DistanceSquared(new Vector2(gas.X, gas.Y), new Vector2(position.X, position.Y));
            }

            return maxDist;
        }

        private bool GetTilePlacable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= _placementGrid.Size.X || y >= _placementGrid.Size.Y)
            {
                return false;
            }

            int pixelID = x + y * _placementGrid.Size.X;
            int byteLocation = pixelID / 8;
            int bitLocation = pixelID % 8;
            var result = ((_placementGrid.Data[byteLocation] & 1 << (7 - bitLocation)) == 0) ? 0 : 1;
            return result != 0;
        }

        private Point2D CalculateMineralLineLocation(List<Vector2Dto> minerals)
        {
            var vectors = minerals.Select(m => new Vector2(m.X, m.Y));
            return new Point2D { X = vectors.Average(v => v.X), Y = vectors.Average(v => v.Y) };
        }
    }

    public class BaseLocationResult
    {
        public bool IsValid { get; set; }
        public Vector2Dto? Location { get; set; }
        public Point2D? MineralLineLocation { get; set; }
        public List<Vector2Dto> MineralFields { get; set; } = new List<Vector2Dto>();
        public List<Vector2Dto> VespeneGeysers { get; set; } = new List<Vector2Dto>();
        public string ValidationNotes { get; set; } = string.Empty;
    }
}
