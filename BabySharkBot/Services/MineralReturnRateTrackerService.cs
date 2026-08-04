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
            for (int droneCount = 8; droneCount <= 16; droneCount++)
            {
                _statsByDroneCount[droneCount] = new BucketStats();
            }
        }

        public void Record(int droneCount, float collectionRateMinerals, float deltaCollected = 0)
        {
            if (droneCount < 8 || droneCount > 16)
            {
                return;
            }

            lock (_lock)
            {
                var stats = _statsByDroneCount[droneCount];
                if (collectionRateMinerals > 0)
                {
                    stats.Add(collectionRateMinerals);
                }
                
                if (deltaCollected > 0)
                {
                    stats.AddCollected(deltaCollected);
                }
                
                stats.AddFrame();
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
                        if (stats.Samples == 0 && stats.TotalCollected == 0)
                        {
                            return $"{kvp.Key} drones: n/a";
                        }

                        // Calculate PPM based on total minerals collected and time spent in this bucket
                        // SC2 Faster speed is 22.4 frames per second
                        float seconds = stats.TotalFrames / 22.4f;
                        float ppm = seconds > 0 ? (stats.TotalCollected / seconds) * 60f : 0;

                        return $"{kvp.Key} drones: avg={ppm:F0} ppm total={stats.TotalCollected:F0}";
                    });

                return string.Join(" | ", parts);
            }
        }

        public bool TryGetAverage(int droneCount, out float average)
        {
            lock (_lock)
            {
                if (_statsByDroneCount.TryGetValue(droneCount, out var stats) && stats.TotalFrames > 0)
                {
                    float seconds = stats.TotalFrames / 22.4f;
                    average = seconds > 0 ? (stats.TotalCollected / seconds) * 60f : 0;
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
            public float TotalCollected { get; private set; }
            public int TotalFrames { get; private set; }
            public float Average => Samples == 0 ? 0f : (float)(TotalRate / Samples);

            public void Add(float rate)
            {
                Samples++;
                TotalRate += rate;
            }

            public void AddCollected(float amount)
            {
                TotalCollected += amount;
            }

            public void AddFrame()
            {
                TotalFrames++;
            }
        }
    }
}
