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
    /// Detects worker pass-through events and uses verified mineral SMART targets
    /// so workers can pass through each other without corrective movement.
    /// </summary>
    public sealed class WorkerAwareCollisionManager : IManager
    {
        private const float WorkerPassThroughRange = 0.35f;
        private readonly BabySharkMiningManager _miningManager;
        private readonly HashSet<ulong> _workersInCollision = new();
        private readonly HashSet<ulong> _temporaryMineralWalkers = new();
        private int _lastLogFrame = -1;

        public bool NeverSkip { get; set; } = true;
        public bool SkipFrame { get; set; }
        public double LongestFrame { get; set; }
        public double TotalFrameTime { get; set; }

        public WorkerAwareCollisionManager(BabySharkMiningManager miningManager)
        {
            _miningManager = miningManager ?? throw new ArgumentNullException(nameof(miningManager));
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            _workersInCollision.Clear();
            _temporaryMineralWalkers.Clear();
            _lastLogFrame = -1;
        }

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            _workersInCollision.Clear();
            _temporaryMineralWalkers.Clear();
            return Array.Empty<SC2APIProtocol.Action>();

            /*
            var frame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            if (Settings.ccaMining || Settings.SimulatedStartActive)
            {
                _workersInCollision.Clear();
                _temporaryMineralWalkers.Clear();
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

            var actions = new List<SC2APIProtocol.Action>();
            var closePairs = 0;
            var currentWorkerTags = workers.Select(worker => worker.UnitTag).ToHashSet();
            _workersInCollision.RemoveWhere(tag => !currentWorkerTags.Contains(tag));
            _temporaryMineralWalkers.RemoveWhere(tag => !currentWorkerTags.Contains(tag));

            foreach (var worker in workers)
            {
                var nearWorker = workers.Any(other => other.UnitTag != worker.UnitTag
                    && DistanceSquared(worker.Position, other.Position) <= WorkerPassThroughRange * WorkerPassThroughRange);
                var nearCarryingWorker = workers.Any(other => other.UnitTag != worker.UnitTag
                    && other.IsCarrying
                    && DistanceSquared(worker.Position, other.Position) <= WorkerPassThroughRange * WorkerPassThroughRange);
                var mineralWalkCollision = !worker.IsCarrying && nearCarryingWorker;
                var wasInCollision = _workersInCollision.Contains(worker.UnitTag);
                var wasTemporaryMineralWalker = _temporaryMineralWalkers.Contains(worker.UnitTag);
                if (nearWorker)
                {
                    closePairs++;
                }

                if (mineralWalkCollision)
                {
                    if (!wasInCollision
                        && _miningManager.TryCreateMineralWalkSmart(
                            worker.UnitTag,
                            false,
                            out var mineralWalkAction,
                            out var mineralTag))
                    {
                        actions.Add(mineralWalkAction);
                        _temporaryMineralWalkers.Add(worker.UnitTag);
                        Console.WriteLine($"[WORKER COLLISION] frame={frame} worker={worker.UnitTag} command=SMART targetMineral={mineralTag} carrying=false event=MINERAL_WALK_START");
                    }

                    _workersInCollision.Add(worker.UnitTag);
                }
                else if (!worker.IsCarrying)
                {
                    _workersInCollision.Remove(worker.UnitTag);
                }

                if (wasTemporaryMineralWalker
                    && (!nearCarryingWorker || (worker.IsCarrying && IsAtTownhallFootprint(worker, snapshot))))
                {
                    if (_miningManager.TryCreateCollisionResumeAction(
                        worker.UnitTag,
                        worker.IsCarrying,
                        out var resumeAction,
                        out var resumeReason))
                    {
                        actions.Add(resumeAction);
                        Console.WriteLine($"[WORKER COLLISION] frame={frame} worker={worker.UnitTag} command=RESUME reason={resumeReason} carrying={worker.IsCarrying}");
                    }

                    _temporaryMineralWalkers.Remove(worker.UnitTag);
                }
            }

            if (closePairs > 0 && frame != _lastLogFrame)
            {
                _lastLogFrame = frame;
                Console.WriteLine($"[WORKER COLLISION] frame={frame} assignedWorkers={workers.Count} closePairs={closePairs} range={WorkerPassThroughRange:F2} policy=MINERAL_WALK_SMART");
            }

            return actions;
            */
        }

        public void OnEnd(ResponseObservation observation, Result result)


        {
        }

        private static bool IsAtTownhallFootprint(WorkerEntryDto worker, ObservationSnapshotDto snapshot)
        {
            if (worker == null || snapshot?.CurrentTownHalls == null)
            {
                return false;
            }

            return snapshot.CurrentTownHalls.Values.Any(townhall => townhall != null
                && townhall.Position != null
                && DistanceSquared(
                    worker.Position,
                    townhall.Position) <= 2.75f * 2.75f);
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
