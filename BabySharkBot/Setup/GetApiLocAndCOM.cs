using SC2APIProtocol;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BabySharkBot.Setup
{
    public static class GetApiLocAndCOM
    {
        public static void LoadCurrentSettings(ResponseGameInfo gameInfo, MawBaseLocationData mapData)
        {
            var currentIndex = ResolveCurrentSpawnIndex(gameInfo);
            Settings.CurrentSpawnIndex = currentIndex;
            Settings.CurrentSpawnLocation = ResolveCurrentSpawnLocation(gameInfo);
            Globals.CurrentMapData = mapData;
            Globals.CurrentStartIndex = currentIndex;
            if (mapData?.M1IsFar != null && currentIndex >= 0 && currentIndex < mapData.M1IsFar.Length)
            {
                Settings.M1IsFar = mapData.M1IsFar;
            }
            if (mapData?.M8IsFar != null && currentIndex >= 0 && currentIndex < mapData.M8IsFar.Length)
            {
                Settings.M8IsFar = mapData.M8IsFar;
            }
        }

        public static int ResolveCurrentSpawnIndex(ResponseGameInfo gameInfo)
        {
            var apiStarts = gameInfo?.StartRaw?.StartLocations;
            if (apiStarts == null || apiStarts.Count == 0)
            {
                return 0;
            }

            if (Settings.CurrentSpawnIndex >= 0 && Settings.CurrentSpawnIndex < apiStarts.Count)
            {
                return Settings.CurrentSpawnIndex;
            }

            if (Settings.CurrentSpawnLocation != null)
            {
                var bestIndex = 0;
                var bestDistance = float.MaxValue;

                for (var i = 0; i < apiStarts.Count; i++)
                {
                    var start = apiStarts[i];
                    if (start == null)
                    {
                        continue;
                    }

                    var dx = Settings.CurrentSpawnLocation.X - start.X;
                    var dy = Settings.CurrentSpawnLocation.Y - start.Y;
                    var distance = dx * dx + dy * dy;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = i;
                    }
                }

                return bestIndex;
            }

            return 0;
        }

        public static Vector2Dto ResolveCurrentSpawnLocation(ResponseGameInfo gameInfo)
        {
            var apiStarts = gameInfo?.StartRaw?.StartLocations;
            if (apiStarts == null || apiStarts.Count == 0)
            {
                return new Vector2Dto();
            }

            var index = ResolveCurrentSpawnIndex(gameInfo);
            var spawn = apiStarts.Count > index ? apiStarts[index] : apiStarts.FirstOrDefault();
            return spawn == null ? new Vector2Dto() : new Vector2Dto(spawn.X, spawn.Y, 0f);
        }

        public static Vector2Dto ResolveCurrentCOM(MawBaseLocationData mapData, int currentSpawnIndex)
        {
            if (mapData?.MineralCenterOfMass != null && currentSpawnIndex >= 0 && mapData.MineralCenterOfMass.Count > currentSpawnIndex)
            {
                return mapData.MineralCenterOfMass[currentSpawnIndex] ?? new Vector2Dto();
            }

            return new Vector2Dto();
        }

        public static bool ResolveCurrentBaseHasBeenPlayed(MawBaseLocationData mapData, int currentSpawnIndex)
        {
            if (mapData == null || currentSpawnIndex < 0) return false;

            if (Settings.WorkerCount == 8)
            {
                return mapData.BaseHasBeenPlayed8 != null
                    && currentSpawnIndex < mapData.BaseHasBeenPlayed8.Length
                    && mapData.BaseHasBeenPlayed8[currentSpawnIndex];
            }
            else if (Settings.WorkerCount == 12)
            {
                return mapData.BaseHasBeenPlayed12 != null
                    && currentSpawnIndex < mapData.BaseHasBeenPlayed12.Length
                    && mapData.BaseHasBeenPlayed12[currentSpawnIndex];
            }

            return mapData.BaseHasBeenPlayed != null
                && currentSpawnIndex < mapData.BaseHasBeenPlayed.Length
                && mapData.BaseHasBeenPlayed[currentSpawnIndex];
        }
    }
}
