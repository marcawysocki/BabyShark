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
        // T1 arrival test battery (owner spec 2026-10-24 18:16): --t1test=N (0-9)
        // selects startup-book variants for T1/T4/T5 that measure T1's time to the
        // TA SM Harvest position. Variant = N / 2: 0 = T4/T5 mineral-walk clear of
        // T1, no bumping; 1 = control (current chase-bump book); 2 = T4/T5 move to
        // SA then follow T1 from frame 6; 3 = T1 via the SM-line halfway Star +
        // T4/T5 mineral-walk to SA then follow at star arrival; 4 = same with
        // T4/T5 moving to SA instead of mineral walking. Spawn: even N = NW
        // (seed 1), odd N = SE (seed 2). -1 = disabled (normal books).
        public static int T1TestNumber = -1;
        // T4 mineral-walk duration before the TB bump move (owner test suite
        // 2026-10-27 15:24): --t4walk=N frames of SA mineral-walk, then the
        // move to TB that crosses T1's SMH line. -1 = built-in default (6).
        public static int T4WalkFrames = -1;
        // --t4angle=D (owner test suite 2026-10-28 08:14): T4 STARTS with a
        // plain MOVE (not a mineral walk) along its latched start -> SA-mineral
        // heading rotated by D degrees (+ = counterclockwise in map coords,
        // 0 = the old mineral-walk heading), switching to the TB bump move at
        // frame 8. int.MinValue = disabled (normal mineral-walk book).
        public static int T4AngleDegrees = int.MinValue;
        // --gatelead=N (owner test suite 2026-10-28 08:51): fire T1's SMH Smart
        // N frames BEFORE SMH arrival (threshold = 0.2u + N x 0.15u cruise) so
        // the order-latency overlaps the last stretch of travel instead of
        // starting at the stand. 0 = on-arrival (champion N=8 control).
        public static int SmhSmartGateLeadFrames = 0;
        // --t1trace: per-frame T1 position/ability/carrying dump, relative
        // frames 5-80 (diagnoses the Smart -> pickup walk-in).
        public static bool T1PositionTrace = false;
        // Resolved once per run by the mining manager (role "T1" -> unit tag);
        // the telemetry service matches this tag because T1's WorkerEntryDto
        // Label arrives empty.
        public static ulong T1TraceUnitTag = 0;
        // Early-suicide trigger (owner spec 2026-10-28 08:14): --suicide-end
        // now fires when the named role has RETURNED ITS FIRST CARGO (the
        // optimization metric is time-to-first-return), instead of the
        // 200-mineral bank. --suicide-200 selects the old bank trigger;
        // --suicide-role=NAME moves the role (default T1; later Y1).
        public static bool EarlySuicideOnRoleReturn = true;
        public static string EarlySuicideRole = "T1";
        // Bump choreography guide overlay (owner spec 2026-10-26 05:45): --bumpviz
        // draws persistent per-frame debug geometry for both start locations —
        // hatchery footprint circles (+0.4u), mineral harvest-distance circles in
        // mineral colors (+0.4u), the TA SM Return -> SM Harvest line, frame-0
        // worker cross hatches, and telemetry circles (T1/T4/T5 distances from
        // their start at milestone frames). Ground-height lines/circles so nothing
        // floats; re-sent every frame so the overlay persists.
        public static bool BumpChoreographyViz = false;
        // --naturaltest (owner spec 2026-10-28 14:13, "Test 1"): passive
        // observation build. ALL worker mining commands are suppressed (the
        // build manager seeds no instruction sets, so the mining manager has
        // nothing to execute) and the engine's natural auto-mining picks the
        // harvest/return stand points. Production only: drone to 13, overlord
        // at 100 bank, drone to 14, overlord at the next 100, then drones to
        // 32. No suicide, no buildings. Telemetry recording continues
        // unchanged (worker_commands_*.jsonl WorkerObservation records carry
        // per-frame positions/orders, now with the raw order target tag).
        public static bool NaturalObservationMode = false;

        // --smsweep (owner spec 2026-10-29 08:39): SM-harvest-point angular
        // sweep measurement. Two workers per far mineral (SB, BB, BA, YB, YA)
        // leapfrog Smart commands from approach points 1.87u out, sweeping the
        // approach angle +5 deg until the stand point's distance to hatchery
        // circle 1 (2.91u) worsens, back down in 2 deg steps to the best, then
        // 1 deg fill. The SmSweepService owns ALL worker commands in this mode;
        // books, collision detours, return interrupts and temp-drone onboarding
        // are suppressed exactly like NaturalObservationMode.
        public static bool SmSweepMode = false;

        // --smstage (owner spec 2026-10-29 12:11): verify/tipping/staging test
        // built on the --smsweep results. For each far mineral (SB, BB, BA, YB,
        // YA): (1) verify the measured ideal stand point — Smart from the 1.87u
        // approach at the sweep's best angle, worker must stand on the point and
        // actually harvest (mineral contents drop); (2) tipping scan — approach
        // at best+k deg (1 deg steps, interleaved +/-) until the stand misses
        // the ideal point by > 0.15u, giving the greatest +/- angle that still
        // hits; (3) staging area — place the worker at the ideal stand + r u
        // toward the hatchery (r = 0.30..0.60, plus flanks), Stop, and watch
        // whether it holds without wandering; from promising spots a Smart must
        // still hit the ideal point. SmStageService owns ALL worker commands.
        public static bool SmStageMode = false;
        // Optional subset filter (e.g. --smminerals=SB,BB); empty = all five.
        public static string SmStageMinerals = string.Empty;

        // --smhandoff (owner spec 2026-10-29 16:37): staging with 2 workers per
        // far mineral and harvest frames. Worker A harvests (MOVE to the 1.87u
        // approach at the sweep best angle, then SMART); worker B holds at the
        // staging spot (ideal stand + SmHandoffRadius u toward the hatchery,
        // bearing 0) until A has ACCUMULATED 32 harvest frames (stationary +
        // gather 1183 — same convention as HarvestFrameTracker), then B
        // receives a SMART on the mineral. Measures the handoff: B stand vs
        // ideal, the wait-to-harvest seam (mineral idle frames), and mutual
        // drift/interference between the staged holder and the active
        // harvester. SmHandoffService owns ALL worker commands.
        public static bool SmHandoffMode = false;
        // Optional subset filter (e.g. --smminerals=SB,BB); empty = all five.
        public static string SmHandoffMinerals = string.Empty;
        // Staging radius from the ideal stand toward the hatchery (default 0.40).
        public static double SmHandoffRadius = 0.40;
        // --smbearing= (owner spec 2026-10-29 18:09 follow-up): stage B at this
        // offset (degrees) from the mineral's sweep best-angle corridor instead
        // of dead on the hatchery line (0 = original behavior). The sign is
        // auto-chosen per mineral — whichever side puts more distance between
        // B's spot and A's 1.87u approach point. Motivation: at bearing 0 the
        // staging spot sits on A's walk-in corridor (best angles are 2-16 deg),
        // which shoved B (hold drifts up to 1.37u) and stalled A (SB NW gap 16).
        public static double SmHandoffBearing = 0.0;
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