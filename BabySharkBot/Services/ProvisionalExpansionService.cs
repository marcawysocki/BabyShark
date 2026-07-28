using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BabySharkBot.Setup;
using Point2D = SC2APIProtocol.Point2D;

#nullable enable

namespace BabySharkBot.Services
{
    /// <summary>
    /// Tracks expansion candidates until scouting verifies them as true bases or mineral walls.
    /// </summary>
    public class ProvisionalExpansionService
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, ExpansionPointModel> _provisional = new();
        private readonly Dictionary<int, ExpansionPointModel> _verified = new();
        private readonly Dictionary<int, ExpansionPointModel> _rejected = new();
        private MawBaseLocationData? _mapData;

        public ProvisionalExpansionService()
        {
            Console.WriteLine("ProvisionalExpansionService initialized");
        }

        public void Initialize(MawBaseLocationData mapData)
        {
            _mapData = mapData;
            Console.WriteLine("ProvisionalExpansionService: Initialized with map data");

            lock (_lock)
            {
                _mapData.ProvisionalExpansionPoints.Clear();
                foreach (var kvp in _provisional)
                {
                    _mapData.ProvisionalExpansionPoints[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in _verified)
                {
                    _mapData.ExpansionPoints[kvp.Key] = kvp.Value;
                }
            }
        }

        public void AddProvisional(int expansionIndex, ExpansionPointModel model, bool suspectedWall = false)
        {
            if (model == null)
            {
                Console.WriteLine($"ProvisionalExpansionService.AddProvisional: null model for index {expansionIndex}");
                return;
            }

            lock (_lock)
            {
                model.Status = suspectedWall ? ExpansionPointStatus.SuspectedWall : ExpansionPointStatus.Provisional;
                _provisional[expansionIndex] = model;
                if (_mapData != null)
                {
                    _mapData.ProvisionalExpansionPoints[expansionIndex] = model;
                }

                Console.WriteLine($"ProvisionalExpansionService: Added {(suspectedWall ? "wall" : "expansion")} candidate {expansionIndex} as {model.Status}");
            }
        }

        public void VerifyExpansion(int expansionIndex, string notes = "")
        {
            lock (_lock)
            {
                if (!_provisional.TryGetValue(expansionIndex, out var model))
                {
                    Console.WriteLine($"ProvisionalExpansionService.VerifyExpansion: No provisional entry for {expansionIndex}");
                    return;
                }

                model.Status = ExpansionPointStatus.VerifiedExpansion;
                model.VerificationNotes = notes;
                _verified[expansionIndex] = model;
                _provisional.Remove(expansionIndex);

                if (_mapData != null)
                {
                    _mapData.ProvisionalExpansionPoints.Remove(expansionIndex);
                    _mapData.ExpansionPoints[expansionIndex] = model;
                }

                Console.WriteLine($"ProvisionalExpansionService: Verified expansion {expansionIndex}");
            }
        }

        public void VerifyWall(int expansionIndex, string notes = "")
        {
            lock (_lock)
            {
                if (!_provisional.TryGetValue(expansionIndex, out var model))
                {
                    Console.WriteLine($"ProvisionalExpansionService.VerifyWall: No provisional entry for {expansionIndex}");
                    return;
                }

                model.Status = ExpansionPointStatus.VerifiedWall;
                model.VerificationNotes = notes;
                _rejected[expansionIndex] = model;
                _provisional.Remove(expansionIndex);

                if (_mapData != null)
                {
                    _mapData.ProvisionalExpansionPoints.Remove(expansionIndex);
                    _mapData.ExpansionPoints.Remove(expansionIndex);
                }

                Console.WriteLine($"ProvisionalExpansionService: Verified wall {expansionIndex}");
            }
        }

        public void Reject(int expansionIndex, string notes = "")
        {
            lock (_lock)
            {
                if (!_provisional.TryGetValue(expansionIndex, out var model))
                {
                    return;
                }

                model.Status = ExpansionPointStatus.Rejected;
                model.VerificationNotes = notes;
                _rejected[expansionIndex] = model;
                _provisional.Remove(expansionIndex);

                if (_mapData != null)
                {
                    _mapData.ProvisionalExpansionPoints.Remove(expansionIndex);
                    _mapData.ExpansionPoints.Remove(expansionIndex);
                }

                Console.WriteLine($"ProvisionalExpansionService: Rejected candidate {expansionIndex}");
            }
        }

        public Dictionary<int, ExpansionPointModel> GetProvisional()
        {
            lock (_lock)
            {
                return new Dictionary<int, ExpansionPointModel>(_provisional);
            }
        }

        public Dictionary<int, Point2D> GetProvisionalScoutPoints()
        {
            lock (_lock)
            {
                return _provisional
                    .Where(kvp => kvp.Value != null && (kvp.Value.Status == ExpansionPointStatus.Provisional || kvp.Value.Status == ExpansionPointStatus.SuspectedWall) && kvp.Value.MineralClusterCenter != null)
                    .OrderBy(kvp => kvp.Key)
                    .ToDictionary(kvp => kvp.Key, kvp => new Point2D { X = kvp.Value.MineralClusterCenter.X, Y = kvp.Value.MineralClusterCenter.Y });
            }
        }

        public void MarkScoutComplete(Point2D point)
        {
            if (point == null)
            {
                return;
            }

            lock (_lock)
            {
                var completed = _provisional
                    .FirstOrDefault(kvp => kvp.Value != null && kvp.Value.MineralClusterCenter != null && Math.Abs(kvp.Value.MineralClusterCenter.X - point.X) < 0.01f && Math.Abs(kvp.Value.MineralClusterCenter.Y - point.Y) < 0.01f);

                if (completed.Value == null)
                {
                    return;
                }

                if (completed.Value.Status == ExpansionPointStatus.SuspectedWall)
                {
                    VerifyWall(completed.Key, "Scouted by overlord");
                }
                else
                {
                    VerifyExpansion(completed.Key, "Scouted by overlord");
                }
            }
        }

        public Dictionary<int, ExpansionPointModel> GetVerified()
        {
            lock (_lock)
            {
                return new Dictionary<int, ExpansionPointModel>(_verified);
            }
        }

        public Dictionary<int, ExpansionPointModel> GetRejected()
        {
            lock (_lock)
            {
                return new Dictionary<int, ExpansionPointModel>(_rejected);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _provisional.Clear();
                _verified.Clear();
                _rejected.Clear();
                _mapData?.ProvisionalExpansionPoints.Clear();
                Console.WriteLine("ProvisionalExpansionService: Cleared all expansion candidates");
            }
        }
    }
}
