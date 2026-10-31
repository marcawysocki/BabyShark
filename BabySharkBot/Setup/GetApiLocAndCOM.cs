using SC2APIProtocol;
using Sharky;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BabySharkBot.Setup
{
    public static class GetApiLocAndCOM
    {
        public static void LoadCurrentSettings(ResponseGameInfo gameInfo, MawBaseLocationData mapData)
        {
            LoadCurrentSettings(gameInfo, mapData, null);
        }

        public static void LoadCurrentSettings(ResponseGameInfo gameInfo, MawBaseLocationData mapData, ResponseObservation observation)
        {
            var currentIndex = ResolveCurrentSpawnIndex(gameInfo, mapData, observation);
            Settings.CurrentSpawnIndex = currentIndex;
            Settings.CurrentSpawnLocation = ResolveCurrentSpawnLocation(gameInfo, mapData, currentIndex);
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

        // Telemetry log naming (owner spec 2026-10-23): resolve the current
        // start location to "NW" / "SE" for the mining-test file names. SE is
        // the FIRST entry of the owner's SE-first clockwise start order
        // (HardcodedStartOrders); when the map has no hardcoded table, fall
        // back to the API start list — on the diagonal two-start maps the own
        // start's X below the start-list midpoint means NW, above means SE.
        // Empty string when the spawn has not been resolved yet.
        public static string ResolveStartLocationTag(ResponseGameInfo gameInfo)
        {
            try
            {
                var own = Settings.CurrentSpawnLocation;
                if (own == null || (own.X == 0f && own.Y == 0f))
                {
                    return string.Empty;
                }

                var hardcoded = HardcodedStartOrders.Get(gameInfo?.MapName);
                if (hardcoded?.Starts != null && hardcoded.Starts.Count > 0)
                {
                    var ordered = HardcodedStartOrders.OrderClockwiseFromSouthEast(hardcoded.Starts);
                    var se = ordered.FirstOrDefault();
                    if (se != null)
                    {
                        const float tolerance = 5f;
                        var dx = own.X - se.X;
                        var dy = own.Y - se.Y;
                        return dx * dx + dy * dy <= tolerance * tolerance ? "SE" : "NW";
                    }
                }

                var apiStarts = gameInfo?.StartRaw?.StartLocations;
                if (apiStarts == null || apiStarts.Count < 2)
                {
                    return string.Empty;
                }

                var midX = apiStarts.Average(s => s.X);
                return own.X < midX ? "NW" : "SE";
            }
            catch
            {
                return string.Empty;
            }
        }

        public static int ResolveCurrentSpawnIndex(ResponseGameInfo gameInfo, MawBaseLocationData mapData, ResponseObservation observation)
        {
            var observedTownHall = observation?.Observation?.RawData?.Units?
                .FirstOrDefault(u => u != null
                    && u.Alliance == Alliance.Self
                    && IsTownHall(u.UnitType))?.Pos;

            if (observedTownHall != null && mapData?.StartingTownHall != null)
            {
                const float tolerance = 3f;
                var closestIndex = -1;
                var closestDistance = float.MaxValue;

                for (var i = 0; i < mapData.StartingTownHall.Length; i++)
                {
                    var townHall = mapData.StartingTownHall[i];
                    if (townHall == null)
                    {
                        continue;
                    }

                    var dx = observedTownHall.X - townHall.X;
                    var dy = observedTownHall.Y - townHall.Y;
                    var distance = dx * dx + dy * dy;
                    if (distance <= tolerance * tolerance && distance < closestDistance)
                    {
                        closestIndex = i;
                        closestDistance = distance;
                    }
                }

                if (closestIndex >= 0)
                {
                    return closestIndex;
                }

                return -1;
            }

            if (mapData?.StartingTownHall != null
                && Settings.CurrentSpawnIndex >= 0
                && Settings.CurrentSpawnIndex < mapData.StartingTownHall.Length)
            {
                return Settings.CurrentSpawnIndex;
            }

            return ResolveCurrentSpawnIndex(gameInfo);
        }

        public static int ResolveCurrentSpawnIndex(ResponseGameInfo gameInfo)
        {
            // HARDCODED START ORDER (owner spec 2026-10-16): when the map has
            // a table, the hardcoded SE-first clockwise order decides the
            // slots — the API order (apiStarts) can change with map updates
            // (e.g. starting at location 1 instead of 0) and must never be
            // indexed directly here. AGENT VERIFY: see HardcodedStartOrders.
            var hardcoded = HardcodedStartOrders.Get(gameInfo?.MapName);
            if (hardcoded != null)
            {
                var ordered = HardcodedStartOrders.OrderClockwiseFromSouthEast(hardcoded.Starts);
                var reference = Settings.CurrentSpawnLocation;
                if (reference != null)
                {
                    var bestIndex = 0;
                    var bestDistance = float.MaxValue;
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        var dx = reference.X - ordered[i].X;
                        var dy = reference.Y - ordered[i].Y;
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
            return ResolveCurrentSpawnLocation(gameInfo, null, ResolveCurrentSpawnIndex(gameInfo));
        }

        private static Vector2Dto ResolveCurrentSpawnLocation(ResponseGameInfo gameInfo, MawBaseLocationData mapData, int index)
        {
            if (mapData?.StartingTownHall != null && index >= 0 && index < mapData.StartingTownHall.Length)
            {
                var townHall = mapData.StartingTownHall[index];
                if (townHall != null)
                {
                    return new Vector2Dto(townHall.X, townHall.Y, townHall.Z);
                }
            }

            // Hardcoded table wins over the API order here too (same reason:
            // apiStarts indexing must not decide the slot on updated maps).
            var hardcoded = HardcodedStartOrders.Get(gameInfo?.MapName);
            if (hardcoded != null)
            {
                var ordered = HardcodedStartOrders.OrderClockwiseFromSouthEast(hardcoded.Starts);
                var hardcodedSpawn = index >= 0 && ordered.Count > index ? ordered[index] : ordered.FirstOrDefault();
                return hardcodedSpawn == null ? new Vector2Dto() : new Vector2Dto(hardcodedSpawn.X, hardcodedSpawn.Y, 0f);
            }

            var apiStarts = gameInfo?.StartRaw?.StartLocations;
            if (apiStarts == null || apiStarts.Count == 0)
            {
                return new Vector2Dto();
            }

            var spawn = index >= 0 && apiStarts.Count > index ? apiStarts[index] : apiStarts.FirstOrDefault();
            return spawn == null ? new Vector2Dto() : new Vector2Dto(spawn.X, spawn.Y, 0f);
        }

        private static bool IsTownHall(uint unitType)
        {
            return unitType == (uint)UnitTypes.ZERG_HATCHERY
                || unitType == (uint)UnitTypes.TERRAN_COMMANDCENTER
                || unitType == (uint)UnitTypes.PROTOSS_NEXUS;
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
