using System.Collections.Generic;

namespace BabySharkBot.Setup
{
    // Centralized settings for BabyShark library (shared by BaseManager, WorkerLabelService, etc.)
    public static class Settings
    {
        public static bool EnableTraining = false;
        public static bool EnableRLInference = false;
        public static bool EnableImitationRecording = false;
        public static string RLModelPath = "RLIntegration/model.pt";
        public static string RLMetadataPath = "RLIntegration/model.metadata.json";
        public static string RLTrainingDataPath = "RLIntegration/data";
        public static string MiningCycleRecordPath = "RLIntegration/data/mining_cycles.jsonl";
        public static string MiningCycleScorePath = "RLIntegration/data/mining_cycle_scores.jsonl";

        // Speed/mining/data schema version (semantic string). Update when heavy-generation or JSON schema changes.
            public const string SpeedMiningVersion = "0.01";

            // Debug mode: enables console logging and debug prints. Can be true even in Release builds if needed.
            public static bool DebugMode = true;
            public static bool DebugPrintEveryFrame = true;

            /// <summary>
            /// Flag set by MapDataManager when map data has been loaded (or failed to load) from disk.
            /// Initially false; set to true/false by background thread before game loop starts.
            /// </summary>
            public static bool MapDataLoaded = false;

        /// <summary>
        /// Flag set when the map-data serialization/deserialization thread has completed.
        /// </summary>
        public static bool SerializeDataLoaded = false;

        /// <summary>
        /// Flag set once the current spawn location has been processed for the current map.
        /// </summary>
        public static bool SpawnDataLoaded = false;
        public static Dictionary<int, Vector2Dto> CurrentW4PositionsByStart = new Dictionary<int, Vector2Dto>();
        public static int CurrentSpawnIndex = -1;
        public static int WorkerCount = 12; // Default to 12, updated during initialization
        public static Vector2Dto CurrentSpawnLocation = new Vector2Dto();
        public static Vector2Dto CurrentSpawnCOM = new Vector2Dto();
        public static bool CurrentBaseHasBeenPlayed = false;
        public static bool CurrentBaseHasBeenPlayed8 = false;
        public static bool CurrentBaseHasBeenPlayed12 = false;
        public static bool ccaMining = true;
        public static bool CreateWorkerFrameZero = true;
        /// <summary>
        /// Set to true once the BabySharkMiningManager has been created and registered with the bot.
        /// Used as a lightweight indicator that the mining manager is (or should be) running.
        /// </summary>
        public static bool MiningManagerStarted = false;
        public static bool M1Bump = false;
        public static bool M8Bump = false;
        public static bool T1Bump = true;
        public static bool S1Bump = true;
        public static bool B1Bump = true;
        public static bool Y1Bump = true;
        public static bool[] TealM1IsFar = new bool[0];
        public static bool[] YellowM8IsFar = new bool[0];

            /// <summary>
            /// Enable debug drawing to SC2 client (labels, points, lines, etc.).
            /// Automatically set to true in DEBUG builds via conditional compilation.
            /// Defaults to false for Release builds and headless ladder games.
            /// IMPORTANT: Drawing calls will crash headless/ladder environments. Never enable in production.
            /// </summary>
#if DEBUG
        public static bool EnableDebugDrawing = true;
#else
        public static bool EnableDebugDrawing = false;
#endif
    }
    public static class Globals
    {
        public static int helloWorld = 0;
        public static int isThisOnTheScreen = 0;
        public static string currentMap = "";
        public static string currentMapCurrentVersion = "";
        public static int CurrentStartIndex = -1;
        public static Dictionary<int, Vector2Dto> CurrentW4PositionsByStart = new Dictionary<int, Vector2Dto>();
        public static MawBaseLocationData CurrentMapData = null;
        public static string currentMapVespineBase = "";
        public static string currentMapVespineFullMap = "";
        public static string currentMapMineralsBase = "";
        public static string currentMapMineralsFullMap = "";
        public static string currentMapCoordinates = "";
        public static string currentMapStartLocation = "";
        public static string currentMapMiningBase = "";
        public static string currentMapMiningFullMap = "";
        // Expected number of possible start locations for this map (2 or 3)
        
    }
}