using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using BabySharkBot.Setup;

namespace BabySharkBot.Setup
{
    /// <summary>
    /// Loads map data asynchronously before the game loop starts.
    /// Uses canonical map name to construct file paths and loads .dat files in a background thread.
    /// Sets MapDataLoaded flag when complete.
    /// </summary>
    public class MapDataManager
    {
        private const string DataBasePath = "data/base";
        private static readonly object _lockObject = new object();

        /// <summary>
        /// Attempts to load map data for the given map name in a background thread.
        /// Returns immediately; actual loading happens asynchronously.
        /// Sets Settings.MapDataLoaded to true when successful, false on failure/timeout/not found.
        /// </summary>
        public static void TryLoadMapDataAsync(string mapName)
        {
            Settings.SerializeDataLoaded = false;
            if (string.IsNullOrEmpty(mapName))
            {
                Settings.MapDataLoaded = false;
                Settings.SerializeDataLoaded = true;
                return;
            }

            // Start background thread to load data without blocking
            Task.Run(() => TryLoadMapDataInternal(mapName));
        }

        private static void TryLoadMapDataInternal(string mapName)
        {
            var versionPath = string.Empty;

            try
            {
                var safeMapName = SanitizeMapNameForFilename(mapName);
                var dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "data", "base");
                versionPath = Path.Combine(dataFolder, $"{safeMapName}.Version{Settings.SpeedMiningVersion}.dat");

                var bytes = File.ReadAllBytes(versionPath);
                var loaded = MemoryPackSerializer.Deserialize<MawBaseLocationData>(bytes);

                Globals.currentMap = Path.Combine(dataFolder, safeMapName);
                Globals.currentMapCurrentVersion = versionPath;
                Globals.CurrentMapData = loaded;
                Settings.MapDataLoaded = loaded != null;
                Settings.SerializeDataLoaded = true;

                Console.WriteLine($"MapDataManager: versionPath={versionPath}");
                Console.WriteLine($"MapDataManager: Settings.MapDataLoaded={Settings.MapDataLoaded}");
            }
            catch (FileNotFoundException)
            {
                Settings.MapDataLoaded = false;
                Settings.SerializeDataLoaded = true;
                Console.WriteLine($"MapDataManager: versionPath={versionPath}");
                Console.WriteLine($"MapDataManager: Settings.MapDataLoaded={Settings.MapDataLoaded}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MapDataManager: Failed to load map data for {mapName}: {ex.Message}");
                Settings.MapDataLoaded = false;
                Settings.SerializeDataLoaded = true;
                Console.WriteLine($"MapDataManager: versionPath={versionPath}");
                Console.WriteLine($"MapDataManager: Settings.MapDataLoaded={Settings.MapDataLoaded}");
            }
        }

        private static string SanitizeMapNameForFilename(string map)
        {
            if (string.IsNullOrWhiteSpace(map)) return "unknown_map";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(map.Length);
            foreach (var c in map)
            {
                if (Array.IndexOf(invalid, c) >= 0) sb.Append('_'); else sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
