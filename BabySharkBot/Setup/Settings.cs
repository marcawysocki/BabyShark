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
        // 0.12: measured-SM override now also sets HarvestPoint = measured stand (the 1.0u-circle HP
        // sat inside the footprint collision margin on E/W faces and broke Y1's approach on cache regen).
        // 0.13: HP = ray-to-stand-rectangle intersection (1.375 x 0.875) toward the townhall;
        // HP=SMH cost T1 a frame on the bump approach (0.12 runs: T1 f23/f68/f99 vs tuned f22/f67/f97).
        // 0.14: SB's SMH defaults to the SMR-facing left-point corner (owner directive 2026-11-06
        // "Fix S2"; measured strict win: S2 pickup f69->f65, SB pair 6th pickup by f338).
        // 0.15: jit pair-table return points anchor on the SMH stand instead of the mineral
        // center for the fixed-SMH minerals (owner spec 2026-11-08 11:30: "SMR points should
        // have been calculated off the SMH not the mineral point"; TA/SA keep the mineral
        // point). Bumped so the cached pair rows regenerate with the new anchors.
            public const string SpeedMiningVersion = "0.15";
            // Cache key that also distinguishes flag-dependent generated
            // content: --smhcorner changes the stored SmHarvestPoints, so it
            // gets its own cache file instead of contaminating the baseline.
            public static string SpeedMiningCacheVersion => SmhCornerStand ? SpeedMiningVersion + "-smhcornerW" : SpeedMiningVersion;

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
        // 2026-11-16 (owner 19:06): "Magannatha SE flip68" build mode. The build
        // class seeds every worker's instruction book itself; the mining manager
        // ONLY runs the book executor (all PatternJIT side-drivers and the old
        // EarlySuicide harness are gated off). The book encodes the full flip68
        // choreography including the suicide attack as plain rows.
        public static bool Flip68BuildMode = false;
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
        // --t1angle=D (owner test 2026-11-04 20:31): rotate T1's start-line
        // (BumpWalkIntersectF10) direction by D degrees around T1's latched
        // start (+ = counterclockwise in map coords, same convention as
        // --t4angle). 0 = the diagram line (start thru the meet point).
        public static float T1StartAngleDeg = 0f;
        // --meetframe=N (owner test 2026-11-04 20:31): the relative frame T1
        // and T4 switch to the BumpSmhThruPoint move. -1 = built-in default
        // (11; f10 and f11 both seat T1 at f22 on SE, f9 seats f23).
        public static int BumpMeetFrame = -1;
        // --smrthru=U (owner question 2026-11-05 15:27: "how much Move thru
        // distance do we need... 0.3u past the target point or greater or
        // less?"): overrides the SMR thru distance (built-in 2.0u). Sweep to
        // find the minimum that still overrides the cargo-return
        // deceleration crawl at the hatchery rim. -1 = built-in default.
        public static float SmrThruUnitsOverride = -1f;
        // --staymin (owner spec 2026-11-05 15:47): steady-state mining harness —
        // every worker repeats ONLY its first (startup) mineral forever on the
        // {X,X} self-pair SMH/SMR points (T1:TA, T2/T4:TB, T3:SA, S2/T5:SB,
        // B2/Y5:BB, Y3:BA, Y2/Y4:YB, Y1:YA); the 5x5 rotation chapters, T1's
        // TB/TA detour, the swing books and B2's pool build are all skipped so
        // every worker runs an identical repeating cycle for leg-timing and
        // turn-around queueing tests. Pair with --suicide-end --suicide-200.
        public static bool StayMineralLoop = false;
        // --stepwindow=N (owner spec 2026-11-14 06:30): override the
        // Debugger.Break() frame window so a step-thru can run past the
        // hard-coded branch caps (e.g. 215 under the old suicide-end probe runs).
        public static int StepBreakWindowOverride = -1;
        // --jitlog (owner spec 2026-11-14 14:36): JIT collision-avoidance
        // tracking, log-only — per-worker gather engagement (flip ETA =
        // gather + 46), outbound ETAs vs occupancy, and predicted crossing
        // frames for returner×outbound and returner×returner pairs.
        public static bool JitCollisionLog = false;
        // --jitact (owner go 2026-11-14 16:39): JIT action layer, rule 1 —
        // on a mineral flip, if the predicted post-flip idle window (next
        // assigned worker's ETA minus the flip frame) exceeds
        // JitIdleThreshold frames, redirect the closest OUTBOUND worker whose
        // book targets the other mineral, provided the other mineral keeps a
        // seated miner and its own predicted idle stays under the threshold.
        // Implies JitCollisionLog (tracking state feeds the rule).
        public static bool JitActionLayer = false;
        // --jitidle=N: post-flip idle window threshold (frames) for --jitact.
        public static int JitIdleThreshold = 25;
        // Pattern B (owner spec 2026-11-07 05:47): under --staymin the four
        // second miners (T4/T5/Y5/Y4) switch permanently to the team A
        // mineral after their first B-mineral flip. ON by default under
        // --staymin; --nopatternb gives the pure stay-mineral control.
        public static bool PatternBMineralSwitch = true;
        // Pattern A (owner spec 2026-11-07 05:47/08:16, "mine the same way
        // Sharky does"): under --staymin the four extra workers skip their B
        // mineral entirely and take turns on an A mineral from the start —
        // T5 stages TA (T1), T4 stages SA (T3), Y4 stages BA (Y3), Y5 stages
        // YA (Y1). Bump choreography unchanged. --patterna enables; mutually
        // exclusive with Pattern B (no B-mineral first set to switch from).
        public static bool PatternAStart = false;
        // Pattern C (owner spec 2026-11-07 19:52): "mostly B pattern, but send
        // Y5 to SA" with per-trip rotation. Start: TA:T1, TB:T2+T4, SA:T3+Y5,
        // SB:S2+T5, BB:B2 (late arrival), BA:Y3, YB:Y2+Y4, YA:Y1 — then
        // "the first miners on SA, SB, and BB all rotate right": T3 alternates
        // SA/SB, S2 alternates SB/BB, B2 alternates BB/BA from trip 2 on.
        // "TA/TB and YA/YB start with a B pattern so the workers repeat the
        // same mineral twice and the second worker from TB and YB starts the
        // rotations": T4 = TB,TB then TA/TB alternating; Y4 = YB,YB then
        // YA/YB alternating. Pinned: T1=TA, T2=TB, Y5=SA, T5=SB, Y3=BA,
        // Y1=YA, Y2=YB. Result: every pair (TA/TB, SA/SB, BA/BB, YA/YB) ends
        // up with 3 workers. --patternc enables; implies no Pattern B switch.
        public static bool PatternCRotation = false;
        // --patternd (owner spec 2026-11-08 08:55, "a variation of Pattern A"):
        // return-swap rotation — the FAST workers rotate onto the slow B
        // minerals from trip 2 so no worker runs three slow trips:
        //   T1: TA,TB,TA,TB...   T2: TB,TA,TB,TA...   T5: pinned TA
        //   Y1: YA,YB,YA,YB...   Y2: YB,YA,YB,YA...   Y5: pinned YA
        //   S2: SB,BB,SB,BB...   B2: BB,SB,BB,SB...   T4: SB then BB,SB...
        //   T3: pinned SA (solo — T4 no longer parks at SA's SMH)
        //   Y3/Y4: pinned BA. T4's bump choreography is unchanged (walk rows
        //   are role-keyed); only its post-bump stage/harvest mineral moves
        //   SA -> SB. The crossing bump (T1 mineral-walks thru T2's return
        //   path) is a later stage — this flag is the schedule only.
        public static bool PatternDReturnSwap = false;
        // --smhcorner (owner spec 2026-11-06 14:30): "S2 should be returning
        // cargo much faster but takes a detour to the center of the mineral
        // instead of the left point which is a shorter walk in both
        // directions." Mineral fields have a 2x1 footprint (measured stand
        // offsets: 1.25u on east/west faces, 0.83u on north/south faces), so
        // for a diagonal hatchery approach the CLOSEST stand point is the
        // footprint CORNER, not the face midpoint the measured SMH captures.
        // When enabled, each mineral's SmHarvestPoint is recomputed as the
        // closest point of the stand rectangle to the SmReturnPoint.
        public static bool SmhCornerStand = false;
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
        // --hbands (owner request 2026-11-12 09:01): draw 3 circles around the
        // hatchery — shortest, middle, and longest radius to the 8 §77
        // engine-verified harvest stands — plus a marker at each stand.
        public static bool BumpVizHarvestBands = false;
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

        // --f0test (owner spec 2026-10-31 16:15): first-20-frames telemetry test.
        // T1 rolls 2u-past the TA SMH (no assist), Y1 rolls 2u-past the YA
        // harvest point; T4+T5 mineral-walk to SA, B2+Y4 mineral-walk to BA;
        // T2/T3/S2/Y2/Y3/Y5 suicide at frame 0; the six telemetry workers
        // suicide when both T1 and Y1 reached their minerals or at frame 19.
        // F0TelemetryService owns ALL worker commands; start-centered circles
        // at the frame 9/11/13 telemetry radii + T1/Y1 SMH lines + T5/B2 red
        // mineral-walk lines are drawn every frame (both starts).
        public static bool F0TelemetryMode = false;
        // --f0gate=X (2026-11-03): F0Test roller SMART handoff radius in game
        // units (the roller's SmartRangeRadius). SE-start battery (pickup =
        // first cargo buff, the honest time-to-mining metric):
        //   gate 0.45+bump f68/67 | 0.45 no-bump f68/68 | 0.90 f68/68 |
        //   1.20 f69/69 | 1.50 f75/72 | 2.10 f75/72 | 2.50 f74/72 (pure engine)
        //   E1 via-angle pin attempts (-24deg/-35deg): f69/f68 — no gain.
        // Conclusions: the 1183 "arrival" gains at wide gates were phantom
        // (order assignment, not seating); physical seating ~f20 + fixed
        // ~48-frame gather dominate; the f13 bump is worth 0-2 pickup frames.
        // Default 0.45 = the production champion value.
        public static float F0SmartGateRadius = 0.45f;
        // --f0nobump (2026-11-03): F0Test control — T4/B2 hold against the
        // wall instead of crossing the rollers' corridors (no bump).
        public static bool F0NoBump = false;
        // --f0t1via=deg,dist,untilFrame / --f0y1via=... (2026-11-03 E-series):
        // roller via-point — roll at (bearing-to-anchor + deg) for dist units
        // until untilFrame, then home on the true anchor. deg=0 disables.
        public static double F0T1ViaDeltaDeg = 0.0;
        public static double F0T1ViaDist = 1.2;
        public static int F0T1ViaUntilFrame = 8;
        public static double F0Y1ViaDeltaDeg = 0.0;
        public static double F0Y1ViaDist = 1.2;
        public static int F0Y1ViaUntilFrame = 8;
        // --f0return=move (2026-11-03): return-leg assist — on the cargo-pickup
        // frame the roller gets a MOVE 2u toward the hatchery, and one frame
        // later a SMART on the hatchery (engine return re-issue). Default off
        // (engine handles the return on its own).
        public static bool F0ReturnAssist = false;
        // --f0switch=N (2026-11-03): bump-switch test — T1 rolls toward T4's
        // f13 mineral-walk position (the collision point, start + 0.872u
        // toward the SA center) until frame N, when T1, T4 and T5 each get a
        // direct MOVE onto the TA SMH anchor (replacing T4's f13-18 follow).
        // -1 = off.
        public static int F0SwitchFrame = -1;
        // --f0switchctl (2026-11-03): with --f0switch=N, replaces T4/T5 with
        // hold-at-wall books (T1's roller behavior unchanged) so the
        // variant-minus-control T1 trajectory difference isolates the bump
        // displacement vector.
        public static bool F0SwitchControl = false;

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