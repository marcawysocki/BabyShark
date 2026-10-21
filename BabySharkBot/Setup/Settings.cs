using System;
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
        // 0.11: hatchery footprint radius corrected from 5.5 to 2.75 in stored return points; legacy
        // MainMineralJitCargoPoints / ExpansionMineralJitCargoPoints generation removed.
            public const string SpeedMiningVersion = "0.11";

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
        /// Latched immediately before GameConnection startup to identify whether the cached
        /// BaseDtos data had completed successfully before the game connection opened.
        /// </summary>
        public static bool BaseDtosLoadedBeforeGameConnection = false;

        /// <summary>
        /// Flag set once the current spawn location has been processed for the current map.
        /// </summary>
        public static bool SpawnDataLoaded = false;
        public static Dictionary<int, Vector2Dto> CurrentW4PositionsByStart = new Dictionary<int, Vector2Dto>();
        public static int CurrentSpawnIndex = -1;
        public static int WorkerCount = 12; // Default to 12, updated during initialization
        public static string CurrentMapName = string.Empty;
        public static bool IsMagannathaMap => CurrentMapName.Contains("Magannatha", StringComparison.OrdinalIgnoreCase);
        public static bool IsMagannatha12WorkerOverride => IsMagannathaMap;
        // Experiment 2 ("5 by 5 starting teams"): two 6-worker teams (T1-T5 + S2, Y1-Y5 + B2)
        // mining 3 minerals in perpetual rotation on Magannatha. Takes precedence over the
        // experiment-1 (IsMagannatha12WorkerOverride) branches whenever it is active.
        public static bool IsMagannatha5x5Rotation = true;
        public static Vector2Dto CurrentSpawnLocation = new Vector2Dto();
        public static Vector2Dto CurrentSpawnCOM = new Vector2Dto();
        public static bool CurrentBaseHasBeenPlayed = false;
        public static bool CurrentBaseHasBeenPlayed8 = false;
        public static bool CurrentBaseHasBeenPlayed12 = false;

        // Current-frame unit availability populated by ObservationManager before other managers run.
        public static List<ulong> AvailableLarva { get; } = new List<ulong>();
        public static List<ulong> AvailableWorker { get; } = new List<ulong>();
        public static List<ulong> AvailableOverLord { get; } = new List<ulong>();
        public static List<ulong> AvailableQueen { get; } = new List<ulong>();

        public static bool ccaMining = false;
        public static bool SimulatedStartActive = false;
        public static Dictionary<ulong, RuntimeWorkerState> RuntimeWorkers { get; } = new Dictionary<ulong, RuntimeWorkerState>();
        public static Dictionary<string, List<WorkerInstruction>> RuntimeInstructionSets { get; } = new Dictionary<string, List<WorkerInstruction>>(StringComparer.OrdinalIgnoreCase);

        public static void ResetRuntimeWorkers()
        {
            RuntimeWorkers.Clear();
            RuntimeInstructionSets.Clear();
        }
        public static bool BuildOwnsWorkerCommands = false;
        // Master switch selecting the active opening build in BabySharkBot. True = the
        // 12-pool race (TwelvePoolBuild: no drone morphing past 12, pool at 200 minerals);
        // false = the BuildIne mining-test build.
        public static bool UseTwelvePoolBuild = true;
        // Master switch for the worker-collision pass-through service inside
        // BabySharkMiningManager. When true, a non-carrying worker about to bump a
        // carrying worker is SMART-walked through its own mineral and the worker's
        // instruction list resumes at the suspended row once the pair is clear.
        public static bool WorkerCollisionAvoidance = true;
        // Autorun test harness (owner request 2026-10-17 22:41): when the bank
        // reaches 200 minerals, suppress every other command, cancel the
        // spawning pool if it exists, and order all workers to attack our own
        // hatchery so the game ends immediately — the opponent otherwise takes
        // ~7 minutes to end the game, which blocks the frame-at-200
        // optimization loop. Enabled only via the --suicide-end command line
        // flag; never active in ladder or normal runs.
        public static bool EarlySuicideTestEnd = false;
        // Bump test harness (owner spec 2026-10-18; frame-schedule choreography
        // 2026-10-20 "South New Worker Order", REPLACES the retired 2026-10-19
        // distance-trigger chase):
        // BumpTestMode 0 (default) = the frame-schedule choreography: T4/T5 walk
        // SA and Y4/Y5 walk SB for frames 0-5 — Y4/Y5 until 0.4u clear of every
        // role-1/2/3 worker — then all four follow T1/Y1's live position every
        // frame 6-18, mineral-walk SB at frame 19, and stage on their own B
        // mineral at frame 25 (Y-side chaser B2->Y5 per the 2026-10-21 W12/W10
        // switch worker order).
        // 1 = no-help baseline (T4/T5/Y4/Y5 mineral-walk at frame 0).
        // 2/3 = aliases of 0 (modes 0/2/3 behave identically; owner directive
        // 2026-10-20 19:03: the choreography is the default opening, no flag).
        // BumpAngle is the retired stand-point rotation knob (now inert).
        public static int BumpTestMode = 0;
        public static float BumpAngleDegrees = 0f;
        // Spawn guard REMOVED 2026-10-20 (owner directive "Turn NW Start back
        // on"): NW games no longer exit; the flag and its only consumer are gone.
        // 13-B3 extractor trick (owner spec 2026-10-15): set by the mining manager's
        // trick driver, read by TwelvePoolBuild's drone morph gating. MorphBegun = the
        // trick larva's TRAIN_DRONE order was observed (supply 14/14); Done = the
        // extractor cancel was issued (either path) so temp morphing may resume.
        public static bool B3ExtractorTrickMorphBegun = false;
        public static bool B3ExtractorTrickDone = false;
        public static bool B3ExtractorTrickOverlordQueued = false;
        // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): set by the mining
        // manager's milestone watcher the frame the hatchery build resolves
        // (SUCCESS = hatchery under construction observed at the site; FAILURE =
        // none appeared within the verify window). While true every manager
        // strips its unit commands (debug draws still pass) so the owner can
        // inspect the frozen scene — the SC2 protocol has no true pause.
        public static bool MacroHatcheryTestFrozen = false;
        public static int StartFrame = 0;
        public static bool CreateWorkerFrameZero = true;

        public static int GetRelativeFrame(int currentFrame)
        {
            return currentFrame - StartFrame;
        }
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
        public static bool[] M1IsFar = new bool[0];
        public static bool[] M8IsFar = new bool[0];

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
        public static ObservationSnapshotDto CurrentObservation = new();
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