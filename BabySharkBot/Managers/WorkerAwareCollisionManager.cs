using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.Managers;
using BabySharkBot.Setup;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Observes mining-worker proximity without issuing avoidance commands.
    /// Workers are mineral-bound agents and must be allowed to pass through one another;
    /// BabySharkMiningManager remains the sole owner of mining MOVE/SMART commands.
    /// </summary>
    public sealed class WorkerAwareCollisionManager : IManager
    {
        private const float WorkerPassThroughRange = 0.35f;
        private const int EarlyMiningFrames = 34;
        private readonly Dictionary<ulong, Vector2Dto> _previousPositions = new();
        private int _lastLogFrame = -1;

        public bool NeverSkip { get; set; } = true;
        public bool SkipFrame { get; set; }
        public double LongestFrame { get; set; }
        public double TotalFrameTime { get; set; }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            _previousPositions.Clear();
            _lastLogFrame = -1;
        }

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var frame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            if (Settings.ccaMining || Settings.SimulatedStartActive || Settings.BuildOwnsWorkerCommands)
            {
                _previousPositions.Clear();
                return Array.Empty<SC2APIProtocol.Action>();
            }

            var snapshot = Globals.CurrentObservation;
            var mapData = Globals.CurrentMapData;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignedWorkers = mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex);
            if (snapshot == null || assignedWorkers == null || assignedWorkers.Count == 0)
            {
                return Array.Empty<SC2APIProtocol.Action>();
            }

            var assignedTags = assignedWorkers
                .Where(worker => worker != null && worker.UnitID != 0)
                .Select(worker => worker.UnitID)
                .ToHashSet();
            var workers = snapshot.SelfUnits.Values
                .Where(unit => unit != null
                    && assignedTags.Contains(unit.UnitTag)
                    && unit.UnitType == (uint)UnitTypes.ZERG_DRONE)
                .ToList();

            var closePairs = 0;
            foreach (var worker in workers)
            {
                if (_previousPositions.TryGetValue(worker.UnitTag, out var previous))
                {
                    var dx = worker.Position.X - previous.X;
                    var dy = worker.Position.Y - previous.Y;
                    var speedSquared = dx * dx + dy * dy;
                    var nearWorker = workers.Any(other => other.UnitTag != worker.UnitTag
                        && DistanceSquared(worker.Position, other.Position) <= WorkerPassThroughRange * WorkerPassThroughRange);
                    if (nearWorker)
                    {
                        closePairs++;
                    }

                    if (frame <= EarlyMiningFrames && nearWorker && !worker.IsCarrying)
                    {
                        // Deliberately do not issue a corrective MOVE. This is the
                        // worker-aware pass-through policy.
                        _ = speedSquared;
                    }
                }

                _previousPositions[worker.UnitTag] = worker.Position;
            }

            if (closePairs > 0 && frame != _lastLogFrame)
            {
                _lastLogFrame = frame;
                Console.WriteLine($"[WORKER COLLISION] frame={frame} assignedWorkers={workers.Count} closePairs={closePairs} range={WorkerPassThroughRange:F2} policy=PASS_THROUGH no-avoidance-MOVE=true");
            }

            return Array.Empty<SC2APIProtocol.Action>();
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
        }

        private static float DistanceSquared(Vector2Dto first, Vector2Dto second)
        {
            if (first == null || second == null)
            {
                return float.MaxValue;
            }

            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }
    }
}
