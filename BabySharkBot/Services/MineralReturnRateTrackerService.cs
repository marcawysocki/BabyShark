using System;
using System.Collections.Generic;
using System.Linq;

namespace BabySharkBot.Services
{
    public class MineralReturnRateTrackerService
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, BucketStats> _statsByDroneCount = new();

        public MineralReturnRateTrackerService()
        {
            for (int droneCount = 8; droneCount <= 11; droneCount++)
            {
                _statsByDroneCount[droneCount] = new BucketStats();
            }
        }

        public void Record(int droneCount, float collectionRateMinerals)
        {
            if (droneCount < 8 || droneCount > 11)
            {
                return;
            }

            if (collectionRateMinerals <= 0)
            {
                return;
            }

            lock (_lock)
            {
                _statsByDroneCount[droneCount].Add(collectionRateMinerals);
            }
        }

        public string GetSummary()
        {
            lock (_lock)
            {
                var parts = _statsByDroneCount
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp =>
                    {
                        var stats = kvp.Value;
                        if (stats.Samples == 0)
                        {
                            return $"{kvp.Key} drones: n/a";
                        }

                        return $"{kvp.Key} drones: avg={stats.Average:F1} ppm samples={stats.Samples}";
                    });

                return string.Join(" | ", parts);
            }
        }

        public bool TryGetAverage(int droneCount, out float average)
        {
            lock (_lock)
            {
                if (_statsByDroneCount.TryGetValue(droneCount, out var stats) && stats.Samples > 0)
                {
                    average = (float)stats.Average;
                    return true;
                }
            }

            average = 0f;
            return false;
        }

        private sealed class BucketStats
        {
            public int Samples { get; private set; }
            public double TotalRate { get; private set; }
            public float Average => Samples == 0 ? 0f : (float)(TotalRate / Samples);

            public void Add(float rate)
            {
                Samples++;
                TotalRate += rate;
            }
        }
    }
}
