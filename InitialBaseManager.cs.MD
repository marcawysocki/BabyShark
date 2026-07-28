using System;
using System.IO;
using System.Text;
using System.Globalization;
using MemoryPack;
using Newtonsoft.Json;
using SC2APIProtocol;

namespace Maw.Managers
{
    // Orchestrator that loads base data at startup, asks BaseManager to generate when necessary,
    // and hands the data to the lightweight BaseManager.
    public class InitialBaseManager
    {
        readonly BaseManager _baseManager;

        public InitialBaseManager()
        {
            _baseManager = new BaseManager();
        }

        // Called during bootstrap; ensures BaseManager has data loaded.
        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId, Maw.Setup.WorkerLabelService? workerLabelService = null, Maw.Setup.CrosshairService? crosshairService = null)
        {
            Console.WriteLine("InitialBaseManager: OnStart");
            // Try to load persisted BaseLocationData from MessagePack first. If missing or version mismatch,
            // generate via BaseManager and persist the MessagePack file. When Settings.DebugMode is enabled
            // also write a human-readable JSON copy for testing purposes.

            var folder = GetGeneratedBaseDataFolder();
            Directory.CreateDirectory(folder);
            var startLoc = gameInfo?.StartRaw?.StartLocations?.FirstOrDefault();

            MawBaseLocationData baseData = null;

            if (startLoc != null)
            {


                



        
                Maw.Globals.currentMapStartLocation = GetGeneratedBaseDataFileName(gameInfo.MapName, folder, startLoc) + ".dat";

                // Ensure there is a per-map "current version" marker file path available.
                // This file is map-level (no coordinates) and uses the project SpeedMiningVersion.
                try
                {
                    if (!string.IsNullOrEmpty(Maw.Globals.currentMap))
                    {
                        Maw.Globals.currentMapCurrentVersion = Maw.Globals.currentMap + $".Version{Settings.SpeedMiningVersion}.dat";
                    }
                }
                catch { }
                try
                {
                    if (File.Exists(Maw.Globals.currentMapStartLocation))
                    {
                        var bytes = File.ReadAllBytes(Maw.Globals.currentMapStartLocation);
                        MawBaseLocationData doc = null;
                        try
                        {
                            // Use MemoryPack for fast binary deserialization.
                            doc = MemoryPackSerializer.Deserialize<MawBaseLocationData>(bytes);
                        }
                        catch (Exception memEx)
                        {
                            Console.WriteLine($"InitialBaseManager: MemoryPack deserialize failed: {memEx.Message}");
                        }

                        if (doc != null && doc.Version == Settings.SpeedMiningVersion)
                        {
                            Console.WriteLine($"InitialBaseManager: loaded existing base data {Maw.Globals.currentMapStartLocation}");
                            baseData = doc;
                        }
                        else
                        {
                            Console.WriteLine($"InitialBaseManager: loaded data but version mismatch or null - will regenerate");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"InitialBaseManager: failed to load existing base data file: {ex.Message}");
                }

                // If no persisted base data was loaded, generate map mining data only when the
                // per-map version marker file does not exist. This avoids regenerating for
                // maps that already have been processed.
                if (baseData == null)
                {
                    try
                    {
                        var versionPath = Maw.Globals.currentMapCurrentVersion;
                        if (string.IsNullOrEmpty(versionPath) || !File.Exists(versionPath))
                        {
                            var miningDataCreator = new InitialMapData();
                            baseData = miningDataCreator.GetNewMiningData(gameInfo, data, observation, startLoc, workerLabelService, crosshairService);

                            // Persist the generated baseData to the per-start file so future runs can load it.
                            try
                            {
                                if (!string.IsNullOrEmpty(Maw.Globals.currentMapStartLocation) && baseData != null)
                                {
                                    var bytes = MemoryPackSerializer.Serialize(baseData);
                                    File.WriteAllBytes(Maw.Globals.currentMapStartLocation, bytes);
                                }

                                // Create the simple version marker file to indicate generation completed
                                if (!string.IsNullOrEmpty(versionPath))
                                {
                                    File.WriteAllText(versionPath, Settings.SpeedMiningVersion);
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"InitialBaseManager: GetNewMiningData failed: {ex.Message}");
                    }
                }
            }
            else
            {
                // No start location available; fallback to generating in-memory only
                //baseData = _heavy.EnsureBaseData(gameInfo, data, observation) ?? new BaseLocationData() { Version = "0" };
            }

            _baseManager.LoadBaseLocationData(baseData);
            _baseManager.OnStart(gameInfo, data, pingResponse, observation, playerId, opponentId);
        }

        private static string GetGeneratedBaseDataFolder()
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "data", "base");
        }

        // Canonical filename: use the actual map name (sanitized only for invalid file chars) and start coords.
        private static string GetGeneratedBaseDataFileName(string map, string folder, Point2D startLocation)
        {
            var safeMap = SanitizeMapNameForFilename(map);
            var sx = startLocation.X.ToString("F1", CultureInfo.InvariantCulture);
            var sy = startLocation.Y.ToString("F1", CultureInfo.InvariantCulture);

            Maw.Globals.currentMap = Path.Combine(folder, $"{safeMap}");
            Maw.Globals.currentMapCoordinates = $"-{sx}-{sy}";
            Maw.Globals.currentMapVespineBase = Path.Combine(folder, $"{safeMap}-{sx}-{sy}.Vespine.dat");
            Maw.Globals.currentMapVespineFullMap = Path.Combine(folder, $"{safeMap}.Vespine.dat");
            Maw.Globals.currentMapMineralsBase = Path.Combine(folder, $"{safeMap}-{sx}-{sy}.Minerals.dat");
            Maw.Globals.currentMapMineralsFullMap = Path.Combine(folder, $"{safeMap}.Minerals.dat");
            // Per-map version marker file (map-level, no coordinates). Used to skip
            // regenerating mining data for maps that already have a generated version.
            Maw.Globals.currentMapCurrentVersion = Path.Combine(folder, $"{safeMap}.Version{Settings.SpeedMiningVersion}.dat");



            return Path.Combine(folder, $"{safeMap}-{sx}-{sy}.base.dat");
        }

        private static string SanitizeMapNameForFilename(string map)
        {
            if (string.IsNullOrWhiteSpace(map)) return "unknown_map";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(map.Length);
            foreach (var c in map)
            {
                if (Array.IndexOf(invalid, c) >= 0) sb.Append('_'); else sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public BaseManager GetBaseManager() => _baseManager;
    }
}