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
    /// Loads and saves map data for the bot.
    /// Manages the persistence of <see cref="MawBaseLocationData"/> to .dat files using MemoryPack.
    /// Handles asynchronous loading to prevent blocking the game start.
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
            Console.WriteLine($"[MAP DATA 01] load started map={mapName}");

            try
            {
                var safeMapName = SanitizeMapNameForFilename(mapName);
                var dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "data", "base");
                versionPath = Path.Combine(dataFolder, $"{safeMapName}.Version{Settings.SpeedMiningVersion}.dat");
                Console.WriteLine($"[MAP DATA 02] expected schema version={Settings.SpeedMiningVersion}");
                Console.WriteLine($"[MAP DATA 03] data folder={dataFolder}");
                Console.WriteLine($"[MAP DATA 04] version path constructed");
                Console.WriteLine($"MapDataManager: versionPath={versionPath}");
                Console.WriteLine($"[MAP DATA 05] file exists={File.Exists(versionPath)}");
                Console.WriteLine($"[MAP DATA 06] reading file bytes");

                var bytes = File.ReadAllBytes(versionPath);
                Console.WriteLine($"[MAP DATA 07] bytes read length={bytes.Length}");
                Console.WriteLine($"[MAP DATA 08] MemoryPack deserialize started");
                var loaded = MemoryPackSerializer.Deserialize<MawBaseLocationData>(bytes);
                Console.WriteLine($"[MAP DATA 09] MemoryPack deserialize completed loaded={loaded != null}");
                var loadedVersion = loaded?.BaseDtosVersion;
                var expectedVersion = Settings.SpeedMiningVersion;
                var versionValid = loaded != null && string.Equals(loadedVersion, expectedVersion, StringComparison.Ordinal);
                Console.WriteLine($"[MAP DATA 10] loaded schema version={(loadedVersion ?? "<missing>")}");
                Console.WriteLine($"[MAP DATA 11] schema validation expected={expectedVersion} valid={versionValid}");

                Globals.currentMap = Path.Combine(dataFolder, safeMapName);
                Globals.currentMapCurrentVersion = versionPath;
                Globals.CurrentMapData = versionValid ? loaded : null;
                Settings.MapDataLoaded = versionValid;
                Settings.SerializeDataLoaded = true;
                Console.WriteLine($"[MAP DATA 12] shared map state assigned dataAccepted={Globals.CurrentMapData != null}");
                Console.WriteLine($"MapDataManager: loaded BaseDtosVersion={(loadedVersion ?? "<missing>")}, expected={expectedVersion}, valid={versionValid}");
                Console.WriteLine($"[MAP DATA 13] flags MapDataLoaded={Settings.MapDataLoaded} SerializeDataLoaded={Settings.SerializeDataLoaded}");
                Console.WriteLine($"[MAP DATA 14] load completed; InitialMapDataRequired={!Settings.MapDataLoaded}");
            }
            catch (FileNotFoundException)
            {
                Settings.MapDataLoaded = false;
                Settings.SerializeDataLoaded = true;
                Console.WriteLine($"[MAP DATA ERROR] file not found path={versionPath}");
                Console.WriteLine($"[MAP DATA 14] load completed; InitialMapDataRequired=True MapDataLoaded={Settings.MapDataLoaded} SerializeDataLoaded={Settings.SerializeDataLoaded}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MAP DATA ERROR] load failed map={mapName} type={ex.GetType().Name} message={ex.Message}");
                Settings.MapDataLoaded = false;
                Settings.SerializeDataLoaded = true;
                Console.WriteLine($"[MAP DATA 14] load completed; InitialMapDataRequired=True MapDataLoaded={Settings.MapDataLoaded} SerializeDataLoaded={Settings.SerializeDataLoaded}");
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
