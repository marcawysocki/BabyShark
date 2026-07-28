using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using BabySharkBot.Setup;
using Sharky;
using SC2APIProtocol;
using Sharky.Pathing;

#nullable enable

namespace BabySharkBot.Services
{
    /// <summary>
    /// Service to compute expansion townhall placement points using scoring algorithm.
    /// 
    /// Algorithm:
    /// 1. For each candidate point in grid around COM:
    ///    - Penalty: -100 if within clearance of mineral or vespene
    ///    - Penalty: negative if 5x5 footprint is not pathable
    ///    - Bonus: +10 - distance for each mineral within 10 tiles
    ///    - Bonus: +10 - distance for each vespene within 10 tiles
    /// 2. Find highest-scoring point(s)
    /// 3. Detect contested: if 2 peaks near COM with similar scores
    /// </summary>
    public class ExpansionPointService
    {
        private Dictionary<int, ExpansionPointModel> _expansionPoints = new Dictionary<int, ExpansionPointModel>();
        private ResponseGameInfo? _gameInfo;
        private ResponseData? _data;
        private MapDataService? _mapDataService;

        // Scoring parameters
        private const float MINERAL_CLEARANCE = 6.0378f; //6.0207973 + 0.017
        private const float VESPENE_CLEARANCE = 6.77f;
        private const int FOOTPRINT_SIZE = 5;  // 5x5 townhall
        private const float CONTESTED_DETECTION_RADIUS = 6f;  // Peaks within 6 tiles of COM
        private const float CONTESTED_SCORE_THRESHOLD = 0.95f;  // Peaks with 95%+ of max score are "similar"

        public ExpansionPointService()
        {
            Console.WriteLine("ExpansionPointService initialized");
        }

        /// <summary>
        /// Initialize the service with game data.
        /// </summary>
        public void Initialize(ResponseGameInfo gameInfo, ResponseData data, MapDataService? mapDataService = null)
        {
            _gameInfo = gameInfo;
            _data = data;
            _mapDataService = mapDataService;
            Console.WriteLine("ExpansionPointService: Initialized with game data");
        }

        /// <summary>
        /// Compute expansion point(s) for a mineral cluster with associated geysers.
        /// Uses Sharky's base-location math to find the expansion center, then records it for serialization.
        /// </summary>
        public void ComputeExpansionPoint(
            int expansionIndex,
            Vector2Dto mineralClusterCenter,
            List<Vector2Dto> mineralPositions,
            List<Vector2Dto> geyserPositions,
            List<Vector2Dto> startLocations = null)
        {
            if (mineralPositions == null || mineralPositions.Count == 0)
            {
                Console.WriteLine($"ExpansionPointService.ComputeExpansionPoint: No minerals for expansion {expansionIndex}");
                return;
            }

            if (_mapDataService == null)
            {
                Console.WriteLine($"ExpansionPointService.ComputeExpansionPoint: MapDataService not initialized for expansion {expansionIndex}");
                return;
            }

            var model = new ExpansionPointModel(expansionIndex, mineralClusterCenter);
            model.MineralPositions = new List<Vector2Dto>(mineralPositions);
            model.GeyserPositions = new List<Vector2Dto>(geyserPositions ?? new List<Vector2Dto>());

            try
            {
                Console.WriteLine($"[EXPANSION-{expansionIndex}] Starting townhall placement search at COM ({mineralClusterCenter.X:F2}, {mineralClusterCenter.Y:F2})");

                int baseHeight = GetReferenceHeight(mineralClusterCenter, mineralPositions);
                var legalCandidates = SearchLegalCandidates(mineralClusterCenter, baseHeight, mineralPositions, geyserPositions);

                if (legalCandidates == null || legalCandidates.Count == 0)
                {
                    Console.WriteLine($"[EXPANSION-{expansionIndex}] No valid candidate points found");
                    //Debugger.Break();
                    model.IsValid = false;
                    model.ValidationNotes = "No valid candidate points in score map";
                    _expansionPoints[expansionIndex] = model;
                    return;
                }

                var orderedCandidates = legalCandidates
                    .OrderBy(c => c.Score)
                    .ThenBy(c => CalculateDistance(c.Position, mineralClusterCenter))
                    .ToList();

                var bestCandidate = orderedCandidates[0];
                var secondCandidate = orderedCandidates
                    .Skip(1)
                    .FirstOrDefault(c => CalculateDistance(c.Position, bestCandidate.Position) > 0.5f);

                model.ExpansionPoint = bestCandidate.Position;
                model.DistanceToCluster = CalculateDistance(bestCandidate.Position, mineralClusterCenter);
                model.IsValid = true;
                model.SpiralSearchIterations = (int)Math.Round(bestCandidate.SearchRadius / 0.25f);

                var primaryOption = new TownhallPlacementOption
                {
                    Point = bestCandidate.Position,
                    IsValid = true,
                    DistanceToCluster = model.DistanceToCluster,
                    DistanceToCentralNodes = bestCandidate.DistanceToNearestResource,
                    ValidationNotes = $"Best legal placement, score={bestCandidate.Score:F2}, radius={bestCandidate.SearchRadius:F2}"
                };
                model.PlacementOptions.Add(primaryOption);

                bool isContested = secondCandidate != null && IsContestedPlacement(bestCandidate, secondCandidate, mineralClusterCenter);
                model.IsContested = isContested;

                if (isContested)
                {
                    var contestedOption = new TownhallPlacementOption
                    {
                        Point = secondCandidate!.Position,
                        IsValid = true,
                        DistanceToCluster = CalculateDistance(secondCandidate.Position, mineralClusterCenter),
                        DistanceToCentralNodes = secondCandidate.DistanceToNearestResource,
                        ValidationNotes = $"Contested alternative placement, score={secondCandidate.Score:F2}, radius={secondCandidate.SearchRadius:F2}"
                    };

                    if (startLocations != null && startLocations.Count >= 2)
                    {
                        contestedOption.FavoredStartLocation = GetFavoredStartLocation(secondCandidate.Position, startLocations);
                        primaryOption.FavoredStartLocation = GetFavoredStartLocation(bestCandidate.Position, startLocations);
                    }

                    model.PlacementOptions.Add(contestedOption);
                    Console.WriteLine($"[CONTESTED-{expansionIndex}] Detected contested base with placements at ({bestCandidate.Position.X:F2}, {bestCandidate.Position.Y:F2}) and ({secondCandidate.Position.X:F2}, {secondCandidate.Position.Y:F2})");
                }
                else
                {
                    Console.WriteLine($"[STANDARD-{expansionIndex}] Standard base - using single best legal placement at ({bestCandidate.Position.X:F2}, {bestCandidate.Position.Y:F2}) with score {bestCandidate.Score:F2}");
                }

                model.ValidationNotes = isContested
                    ? $"Contested legal placements found. Best score={bestCandidate.Score:F2}, second score={(secondCandidate?.Score ?? 0):F2}"
                    : $"Standard legal placement found. Score={bestCandidate.Score:F2}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ExpansionPointService.ComputeExpansionPoint: Error computing expansion {expansionIndex}: {ex.Message}");
                model.IsValid = false;
                model.ValidationNotes = $"Exception: {ex.Message}";
            }

            _expansionPoints[expansionIndex] = model;
        }

        private class CandidatePlacement
        {
            public Vector2Dto Position { get; set; } = new Vector2Dto();
            public float Score { get; set; }
            public float SearchRadius { get; set; }
            public float DistanceToNearestResource { get; set; }
        }

        /// <summary>
        /// Search legal expansion placements inside the COM radius-8 area.
        /// Candidates are generated around the expansion COM and then filtered by resource and footprint rules.
        /// </summary>
        private List<CandidatePlacement> SearchLegalCandidates(
            Vector2Dto mineralClusterCenter,
            int baseHeight,
            List<Vector2Dto> mineralPositions,
            List<Vector2Dto>? geyserPositions)
        {
            var candidates = new List<CandidatePlacement>();
            var seen = new HashSet<string>();

            const float maxSearchRadius = 7.2f;
            const float step = 0.5f;

            var startX = (float)Math.Floor((mineralClusterCenter.X - maxSearchRadius) * 2f) / 2f;
            var endX = (float)Math.Ceiling((mineralClusterCenter.X + maxSearchRadius) * 2f) / 2f;
            var startY = (float)Math.Floor((mineralClusterCenter.Y - maxSearchRadius) * 2f) / 2f;
            var endY = (float)Math.Ceiling((mineralClusterCenter.Y + maxSearchRadius) * 2f) / 2f;

            for (float x = startX; x <= endX; x += step)
            {
                for (float y = startY; y <= endY; y += step)
                {
                    var deltaX = x - mineralClusterCenter.X;
                    var deltaY = y - mineralClusterCenter.Y;
                    if ((deltaX * deltaX) + (deltaY * deltaY) > (maxSearchRadius * maxSearchRadius))
                    {
                        continue;
                    }

                    var candidate = new Vector2Dto(x, y, mineralClusterCenter.Z);
                    string key = $"{candidate.X:F2}|{candidate.Y:F2}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    if (!IsLegalTownhallPlacement(candidate, baseHeight, mineralPositions, geyserPositions, out float nearestResourceDistance))
                    {
                        continue;
                    }

                    float score = ScoreLegalTownhallPlacement(candidate, mineralPositions, geyserPositions);
                    candidates.Add(new CandidatePlacement
                    {
                        Position = candidate,
                        Score = score,
                        SearchRadius = (float)Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)),
                        DistanceToNearestResource = nearestResourceDistance
                    });
                }
            }

            Console.WriteLine($"ExpansionPointService: Found {candidates.Count} legal candidate(s)");
            return candidates;
        }

        /// <summary>
        /// Score a legal candidate using combined distance to all minerals and geysers.
        /// Lower is better.
        /// </summary>
        private float ScoreLegalTownhallPlacement(
            Vector2Dto candidate,
            List<Vector2Dto> mineralPositions,
            List<Vector2Dto>? geyserPositions)
        {
            float score = 0f;

            foreach (var mineral in mineralPositions)
            {
                float dist = CalculateDistance(candidate, mineral);
                if (dist <= MINERAL_CLEARANCE)
                {
                    score -= dist;
                }
                else
                {
                    score += dist;
                }
            }

            if (geyserPositions != null)
            {
                foreach (var geyser in geyserPositions)
                {
                    float dist = CalculateDistance(candidate, geyser);
                    score += dist;
                }
            }

            return score;
        }

        /// <summary>
        /// Validate the exact townhall footprint and resource clearances.
        /// </summary>
        private bool IsLegalTownhallPlacement(
            Vector2Dto candidate,
            int baseHeight,
            List<Vector2Dto> mineralPositions,
            List<Vector2Dto>? geyserPositions,
            out float nearestResourceDistance)
        {
            nearestResourceDistance = float.MaxValue;

            if (_mapDataService == null)
            {
                return false;
            }

            if (candidate.X < 0 || candidate.Y < 0 || candidate.X >= _mapDataService.MapData.MapWidth || candidate.Y >= _mapDataService.MapData.MapHeight)
            {
                return false;
            }

            float left = candidate.X - 2.5f;
            float right = candidate.X + 2.5f;
            float bottom = candidate.Y - 2.5f;
            float top = candidate.Y + 2.5f;

            int startX = (int)Math.Floor(left);
            int endX = (int)Math.Ceiling(right) - 1;
            int startY = (int)Math.Floor(bottom);
            int endY = (int)Math.Ceiling(top) - 1;

            for (int x = startX; x <= endX; x++)
            {
                for (int y = startY; y <= endY; y++)
                {

                    if (x < 0 || y < 0 || x >= _mapDataService.MapData.MapWidth || y >= _mapDataService.MapData.MapHeight)
                    {
                        return false;
                    }

                    var cell = _mapDataService.MapData.Map[x, y];
                    if (!cell.Walkable || !cell.Buildable || cell.TerrainHeight != baseHeight || cell.HasCreep)
                    {
                        return false;
                    }
                }
            }

            foreach (var mineral in mineralPositions)
            {
                float dist = CalculateDistance(candidate, mineral);
                nearestResourceDistance = Math.Min(nearestResourceDistance, dist);
                if (dist < MINERAL_CLEARANCE)
                {
                    return false;
                }
            }

            if (geyserPositions != null)
            {
                foreach (var geyser in geyserPositions)
                {
                    float dist = CalculateDistance(candidate, geyser);
                    nearestResourceDistance = Math.Min(nearestResourceDistance, dist);
                    if (dist < VESPENE_CLEARANCE)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Determine whether the second-best legal placement is a contested alternative.
        /// </summary>
        private bool IsContestedPlacement(CandidatePlacement best, CandidatePlacement second, Vector2Dto mineralClusterCenter)
        {
            var bestDist = CalculateDistance(best.Position, mineralClusterCenter);
            var secondDist = CalculateDistance(second.Position, mineralClusterCenter);
            var pairDist = CalculateDistance(best.Position, second.Position);

            if (bestDist > CONTESTED_DETECTION_RADIUS || secondDist > CONTESTED_DETECTION_RADIUS)
            {
                return false;
            }

            if (pairDist < 2f)
            {
                return false;
            }

            float scoreDifference = Math.Abs(best.Score - second.Score);
            float scoreThreshold = Math.Max(1f, best.Score * (1f - CONTESTED_SCORE_THRESHOLD));

            Console.WriteLine($"ExpansionPointService: Contested check best={best.Score:F2}, second={second.Score:F2}, diff={scoreDifference:F2}, threshold={scoreThreshold:F2}, pairDist={pairDist:F2}, bestDist={bestDist:F2}, secondDist={secondDist:F2}");

            return scoreDifference <= scoreThreshold;
        }

        /// <summary>
        /// Determine which start location a placement favors.
        /// </summary>
        private int GetFavoredStartLocation(Vector2Dto point, List<Vector2Dto> startLocations)
        {
            if (startLocations == null || startLocations.Count < 2)
            {
                return -1;
            }

            float dist0 = CalculateDistance(point, startLocations[0]);
            float dist1 = CalculateDistance(point, startLocations[1]);
            return dist0 <= dist1 ? 0 : 1;
        }

        /// <summary>
        /// Determine the reference terrain height for the expansion.
        /// </summary>
        private int GetReferenceHeight(Vector2Dto mineralClusterCenter, List<Vector2Dto> mineralPositions)
        {
            if (_mapDataService != null)
            {
                return _mapDataService.MapHeight(mineralClusterCenter.X, mineralClusterCenter.Y);
            }

            if (mineralPositions != null && mineralPositions.Count > 0)
            {
                return (int)Math.Round(mineralPositions[0].Z);
            }

            return (int)Math.Round(mineralClusterCenter.Z);
        }

        /// <summary>
        /// Calculate Euclidean distance between two points (ignoring Z for horizontal distance)
        /// </summary>
        private float CalculateDistance(Vector2Dto p1, Vector2Dto p2)
        {
            float dx = p1.X - p2.X;
            float dy = p1.Y - p2.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Get all computed expansion points
        /// </summary>
        public Dictionary<int, ExpansionPointModel> GetAllExpansionPoints()
        {
            return new Dictionary<int, ExpansionPointModel>(_expansionPoints);
        }

        /// <summary>
        /// Get specific expansion point by index
        /// </summary>
        public ExpansionPointModel? GetExpansionPoint(int expansionIndex)
        {
            if (_expansionPoints.TryGetValue(expansionIndex, out var model))
            {
                return model;
            }
            return null;
        }

        /// <summary>
        /// Clear all stored expansion points
        /// </summary>
        public void Clear()
        {
            _expansionPoints.Clear();
            Console.WriteLine("ExpansionPointService: Cleared all expansion points");
        }

        /// <summary>
        /// Get count of computed expansions
        /// </summary>
        public int Count => _expansionPoints.Count;
    }
}
