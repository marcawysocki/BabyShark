using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;

#nullable enable

namespace BabySharkBot.Setup
{
    /// <summary>
    /// Helper for labeling workers in greedy traversal order (W12/W8 through W1).
    /// The input enumeration order is never used to resolve worker identity.
    /// </summary>
    public static class WorkerLabelChainHelper
    {
        public static List<WorkerEntryDto> BuildWorkersInAW12ThroughW1Order(
            IEnumerable<Unit> workers,
            Vector2Dto mineralCenterOfMass,
            WorkerLabelService? workerLabelService)
        {
            return BuildGreedyWorkerEntries(workers, mineralCenterOfMass, workerLabelService);
        }

        public static List<WorkerEntryDto> BuildWorkersInAW12ThroughW1Order(
            IEnumerable<(ulong Tag, float X, float Y, float Z, uint UnitType)> workers,
            Vector2Dto mineralCenterOfMass,
            WorkerLabelService? workerLabelService)
        {
            return BuildGreedyWorkerEntries(workers, mineralCenterOfMass, workerLabelService);
        }

        public static List<WorkerEntryDto> BuildGreedyWorkerEntries<T>(
            IEnumerable<T> workers,
            Vector2Dto mineralCenterOfMass,
            WorkerLabelService? workerLabelService,
            Func<T, ulong> getTag,
            Func<T, float> getX,
            Func<T, float> getY,
            Func<T, float> getZ,
            Func<T, uint> getUnitType)
        {
            var result = new List<WorkerEntryDto>();
            if (workers == null || mineralCenterOfMass == null)
            {
                return result;
            }

            var remaining = workers.ToList();
            if (remaining.Count == 0)
            {
                return result;
            }

            var current = remaining
                .OrderByDescending(u => DistanceSquared(getX(u), getY(u), mineralCenterOfMass.X, mineralCenterOfMass.Y))
                .ThenBy(u => getTag(u))
                .FirstOrDefault();

            // The canonical stored list is the greedy traversal itself: W8/W12 first,
            // descending to W1. Observation order is never retained as a list index.
            var greedyTraversal = new List<(T Unit, ulong Tag)>();
            while (current != null)
            {
                var tag = getTag(current);
                greedyTraversal.Add((current, tag));
                remaining.RemoveAll(u => getTag(u) == tag);
                if (remaining.Count == 0)
                {
                    break;
                }

                current = remaining
                    .OrderBy(u => DistanceSquared(getX(u), getY(u), getX(current), getY(current)))
                    .ThenBy(u => getTag(u))
                    .FirstOrDefault();
            }

            for (var traversalIndex = 0; traversalIndex < greedyTraversal.Count; traversalIndex++)
            {
                var item = greedyTraversal[traversalIndex];
                var labelIndex = greedyTraversal.Count - traversalIndex;
                var label = $"W{labelIndex}";
                workerLabelService?.SetLabel(label, item.Tag);
                result.Add(new WorkerEntryDto
                {
                    UnitTag = item.Tag,
                    Position = new Vector2Dto(getX(item.Unit), getY(item.Unit), getZ(item.Unit)),
                    UnitType = getUnitType(item.Unit),
                    Label = label,
                    StartLabel = label,
                    FinalLabel = label
                });
            }

            return result;
        }

        public static List<WorkerEntryDto> BuildGreedyWorkerEntries(
            IEnumerable<Unit> workers,
            Vector2Dto mineralCenterOfMass,
            WorkerLabelService? workerLabelService)
        {
            return BuildGreedyWorkerEntries(
                workers,
                mineralCenterOfMass,
                workerLabelService,
                u => u.Tag,
                u => u.Pos.X,
                u => u.Pos.Y,
                u => u.Pos.Z,
                u => u.UnitType);
        }

        public static List<WorkerEntryDto> BuildGreedyWorkerEntries(
            IEnumerable<(ulong Tag, float X, float Y, float Z, uint UnitType)> workers,
            Vector2Dto mineralCenterOfMass,
            WorkerLabelService? workerLabelService)
        {
            return BuildGreedyWorkerEntries(
                workers,
                mineralCenterOfMass,
                workerLabelService,
                w => w.Tag,
                w => w.X,
                w => w.Y,
                w => w.Z,
                w => w.UnitType);
        }

        private static float DistanceSquared(float x1, float y1, float x2, float y2)
        {
            var dx = x1 - x2;
            var dy = y1 - y2;
            return dx * dx + dy * dy;
        }
    }
}
