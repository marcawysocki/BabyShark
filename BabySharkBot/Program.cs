using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using Sharky.MicroTasks.Zerg;
using BabySharkBot.Setup;
using BabySharkBot.Managers;
using MemoryPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AI = BabySharkBot;

namespace BabySharkBot
{
    class Program
    {
        static void Main(string[] args)
        {
            ConsecrationofMyStarCraftIIBotProject.Invoke();
            Console.WriteLine("Starting BabySharkBot");
            Console.WriteLine($"[BUILD ID] ROOT-20260813-SPAWN-FIX assembly={typeof(Program).Assembly.Location} base={AppContext.BaseDirectory}");
            ConfigureRlModes(args);

            Console.WriteLine($"BabySharkBot: 5x5 rotation experiment {(Settings.IsMagannatha5x5Rotation ? "ENABLED" : "disabled")}");

            var maps = new[]
            {
                //"AbyssalReefAIE.SC2Map",
                //"AcropolisAIE.SC2Map",
                //"AutomatonAIE.SC2Map",
                "EphemeronAIE.SC2Map",
                "IncorporealAIE_v4.SC2Map",
                //"InterloperAIE.SC2Map",
                //"LastFantasyAIE.SC2Map",
                "LeyLinesAIE_v3.SC2Map",
               "MagannathaAIE_v2.SC2Map",
                "PersephoneAIE_v4.SC2Map",
                "PylonAIE_v4.SC2Map",
               "ThunderbirdAIE.SC2Map",
                "TorchesAIE_v4.SC2Map",
                "UltraloveAIE_v2.SC2Map"
            };

            var random = new Random();
            //var randomMap = maps[random.Next(maps.Length)];

            //var randomMap = "Habitation Station.SC2Map";
            //var randomMap = "Cloud Kingdom.SC2Map";
            //var randomMap = "InterloperAIE.SC2Map";
            //var randomMap = "W8_InterloperAIE.SC2Map";
            //var randomMap = "TorchesAIE_v4.SC2Map";
            //var randomMap = "LastFantasyAIE.SC2Map";
            var randomMap = "MagannathaAIE_v2.SC2Map";

            // --dump-dat (owner request 2026-10-23): deserialize the persisted
            // map data and write it as JSON so tooling can inspect/adjust the
            // stored coordinates without a game running.
            if (args.Any(a => string.Equals(a, "--dump-dat", StringComparison.OrdinalIgnoreCase)))
            {
                DumpMapDataToJson(randomMap);
                return;
            }
            // --apply-dat=<adjustments.json> (owner request 2026-10-23): shift the
            // stored harvest coordinates for named minerals to the measured
            // worker stop positions, preserving the existing HarvestPoint ->
            // SmHarvestPoint offset. Backs up the dat first.
            var applyArg = args.FirstOrDefault(a => a.StartsWith("--apply-dat=", StringComparison.OrdinalIgnoreCase));
            if (applyArg != null)
            {
                ApplyDatAdjustments(randomMap, applyArg.Substring("--apply-dat=".Length));
                return;
            }
            //var randomMap = "AbyssalReefAIE.SC2Map";
            //var randomMap = "AcropolisAIE.SC2Map";
            //var randomMap = "AutomatonAIE.SC2Map";
            //var randomMap = "EphemeronAIE.SC2Map";
            //var randomMap = "IncorporealAIE_v4.SC2Map";
            //var randomMap = "LeyLinesAIE_v3.SC2Map";
            //var randomMap = "PersephoneAIE_v4.SC2Map";
            //var randomMap = "PylonAIE_v4.SC2Map";
            //var randomMap = "ThunderbirdAIE.SC2Map";
            //var randomMap = "UltraloveAIE_v2.SC2Map";

            const string botName = "Saint Padre Pio";

            // Harness-only flags (--suicide-end/--seed=N/--bump-test=...) must not
            // reroute dat naming into ladder parsing: LadderArgs.MapName comes out
            // empty for them, which silently switched the bot onto the recompute
            // path and wrote unknown_map.dat. Only real ladder args may override.
            var hasLadderArgsEarly = args.Any(a => !IsHarnessArg(a));
            var mapNameForData = randomMap;
            if (hasLadderArgsEarly)
            {
                var ladderArgs = new LadderArgs(args);
                mapNameForData = ladderArgs.MapName;
            }

            Settings.CurrentMapName = mapNameForData ?? string.Empty;
            Settings.SerializeDataLoaded = false;
            Settings.MapDataLoaded = false;

            // --naturaltest (owner spec 2026-10-28 14:13, "Test 1"): passive
            // observation — no worker mining commands at all; the engine's
            // natural auto-mining picks the stand points. Parsed HERE, before
            // the build selection below, so the NaturalObservation build is
            // actually the one that runs (the earlier parse sat after the
            // selection and the flag was still false there — the SE natural
            // run silently played BasicZerglingRush).
            Settings.NaturalObservationMode = args.Any(a => string.Equals(a, "--naturaltest", StringComparison.OrdinalIgnoreCase));
            if (Settings.NaturalObservationMode)
            {
                Console.WriteLine("NATURALTEST ENABLED — zero worker mining commands; natural engine mining observed; production schedule only");
            }

            // --flip68build (owner spec 2026-11-16 19:06): the build class writes
            // every worker's instruction book (flip68 choreography + suicide
            // rows); the mining manager runs ONLY the book executor. Parsed
            // HERE, before the build selection, for the same reason as
            // --naturaltest above.
            Settings.Flip68BuildMode = args.Any(a => string.Equals(a, "--flip68build", StringComparison.OrdinalIgnoreCase));
            if (Settings.Flip68BuildMode)
            {
                Console.WriteLine("FLIP68BUILD ENABLED — build owns worker instruction books; mining manager executes books only");
            }

            // --smsweep (owner spec 2026-10-29 08:39): SM-harvest-point angular
            // sweep. Parsed HERE, before the build selection below, for the same
            // reason as --naturaltest above (the Sharky pipeline picks the build
            // from Settings at SetBuild time).
            Settings.SmSweepMode = args.Any(a => string.Equals(a, "--smsweep", StringComparison.OrdinalIgnoreCase));
            if (Settings.SmSweepMode)
            {
                Console.WriteLine("SMSWEEP ENABLED — two workers per far mineral leapfrog Smart commands at 1.87u, 5/2/1 deg coarse-to-fine sweep; SmSweepService owns all worker commands");
            }

            // --smstage (owner spec 2026-10-29 12:11): verify the sweep's ideal
            // stand points (Smart from 1.87u + harvest check via mineral
            // contents), scan +/- 1 deg for the approach tipping point, and
            // probe the staging area around the stand point. Parsed HERE, before
            // the build selection, for the same reason as --smsweep.
            Settings.SmStageMode = args.Any(a => string.Equals(a, "--smstage", StringComparison.OrdinalIgnoreCase));
            if (Settings.SmStageMode)
            {
                var subsetArg = args.FirstOrDefault(a => a.StartsWith("--smminerals=", StringComparison.OrdinalIgnoreCase));
                Settings.SmStageMinerals = subsetArg != null ? subsetArg.Substring("--smminerals=".Length).Trim() : string.Empty;
                Console.WriteLine($"SMSTAGE ENABLED — verify + tipping scan + staging-area probe per far mineral{(string.IsNullOrEmpty(Settings.SmStageMinerals) ? "" : $" subset=[{Settings.SmStageMinerals}]")}; SmStageService owns all worker commands");
            }

            // --smhandoff (owner spec 2026-10-29 16:37): staging with 2 workers
            // per far mineral and harvest frames — A harvests, B holds at the
            // staging spot until A has 32 harvest frames, then B gets a Smart.
            // Parsed HERE, before the build selection, for the same reason as
            // --smsweep / --smstage.
            Settings.SmHandoffMode = args.Any(a => string.Equals(a, "--smhandoff", StringComparison.OrdinalIgnoreCase));
            if (Settings.SmHandoffMode)
            {
                var subsetArg = args.FirstOrDefault(a => a.StartsWith("--smminerals=", StringComparison.OrdinalIgnoreCase));
                Settings.SmHandoffMinerals = subsetArg != null ? subsetArg.Substring("--smminerals=".Length).Trim() : string.Empty;
                var radiusArg = args.FirstOrDefault(a => a.StartsWith("--smradius=", StringComparison.OrdinalIgnoreCase));
                if (radiusArg != null
                    && double.TryParse(radiusArg.Substring("--smradius=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var handoffRadius)
                    && handoffRadius > 0.05 && handoffRadius < 2.0)
                {
                    Settings.SmHandoffRadius = handoffRadius;
                }
                var bearingArg = args.FirstOrDefault(a => a.StartsWith("--smbearing=", StringComparison.OrdinalIgnoreCase));
                if (bearingArg != null
                    && double.TryParse(bearingArg.Substring("--smbearing=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var handoffBearing)
                    && handoffBearing >= 0.0 && handoffBearing <= 60.0)
                {
                    Settings.SmHandoffBearing = handoffBearing;
                }
                Console.WriteLine($"SMHANDOFF ENABLED — 2 workers per far mineral{(string.IsNullOrEmpty(Settings.SmHandoffMinerals) ? "" : $" subset=[{Settings.SmHandoffMinerals}]")}; A harvests, B holds at ideal+{Settings.SmHandoffRadius:F2}u{(Settings.SmHandoffBearing > 0 ? $" offset {Settings.SmHandoffBearing:F0}deg from the best-angle corridor (auto-sign)" : " toward hatchery")}, B Smart at A's 32 harvest frames; SmHandoffService owns all worker commands");
            }

            // --f0test (owner spec 2026-10-31 16:15): first-20-frames telemetry
            // test — T1 no-assist roll to TA SMH, Y1 roll to YA harvest point,
            // T4/T5 mineral-walk SA, B2/Y4 mineral-walk BA, staggered suicide.
            // Parsed HERE, before the build selection, for the same reason as
            // --smsweep/--smstage/--smhandoff.
            Settings.F0TelemetryMode = args.Any(a => string.Equals(a, "--f0test", StringComparison.OrdinalIgnoreCase));
            if (Settings.F0TelemetryMode)
            {
                var gateArg = args.FirstOrDefault(a => a.StartsWith("--f0gate=", StringComparison.OrdinalIgnoreCase));
                if (gateArg != null
                    && float.TryParse(gateArg.Substring("--f0gate=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gateRadius)
                    && gateRadius > 0.05f && gateRadius <= 3.0f)
                {
                    Settings.F0SmartGateRadius = gateRadius;
                }
                Settings.F0NoBump = args.Any(a => string.Equals(a, "--f0nobump", StringComparison.OrdinalIgnoreCase));
                var returnArg = args.FirstOrDefault(a => a.StartsWith("--f0return=", StringComparison.OrdinalIgnoreCase));
                Settings.F0ReturnAssist = returnArg != null
                    && string.Equals(returnArg.Substring("--f0return=".Length), "move", StringComparison.OrdinalIgnoreCase);
                var switchArg = args.FirstOrDefault(a => a.StartsWith("--f0switch=", StringComparison.OrdinalIgnoreCase));
                if (switchArg != null
                    && int.TryParse(switchArg.Substring("--f0switch=".Length), out var switchFrame)
                    && switchFrame >= 5 && switchFrame <= 18)
                {
                    Settings.F0SwitchFrame = switchFrame;
                }
                Settings.F0SwitchControl = args.Any(a => string.Equals(a, "--f0switchctl", StringComparison.OrdinalIgnoreCase));
                void ParseVia(string name, Action<double, double, int> apply)
                {
                    var viaArg = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
                    if (viaArg == null) return;
                    var parts = viaArg.Substring(name.Length + 1).Split(',');
                    if (parts.Length >= 1 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vd)
                        && parts.Length >= 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vdist)
                        && parts.Length >= 3 && int.TryParse(parts[2], out var vuntil))
                    {
                        apply(vd, vdist, vuntil);
                    }
                }
                ParseVia("--f0t1via", (vd, vdist, vuntil) => { Settings.F0T1ViaDeltaDeg = vd; Settings.F0T1ViaDist = vdist; Settings.F0T1ViaUntilFrame = vuntil; });
                ParseVia("--f0y1via", (vd, vdist, vuntil) => { Settings.F0Y1ViaDeltaDeg = vd; Settings.F0Y1ViaDist = vdist; Settings.F0Y1ViaUntilFrame = vuntil; });
                Console.WriteLine($"F0TEST ENABLED — first-20-frames telemetry: T1 roll->TA SMH, Y1 roll->YA HP, T4/T5 MW->SA, B2/Y4 MW->BA; staggered suicide; F0TelemetryService owns all worker commands; SMART gate {Settings.F0SmartGateRadius:F2}u{(Settings.F0NoBump ? "; NOBUMP control" : "")}{(Settings.F0T1ViaDeltaDeg != 0 ? $"; T1 via {Settings.F0T1ViaDeltaDeg:F0}deg/{Settings.F0T1ViaDist:F1}u until f{Settings.F0T1ViaUntilFrame}" : "")}{(Settings.F0Y1ViaDeltaDeg != 0 ? $"; Y1 via {Settings.F0Y1ViaDeltaDeg:F0}deg/{Settings.F0Y1ViaDist:F1}u until f{Settings.F0Y1ViaUntilFrame}" : "")}");
            }

            // Start loading map data as early as possible so it can overlap GameConnection startup.
            // --smhcorner must be parsed BEFORE this load: the cache filename and
            // schema version include the flag so corner SMH points never
            // contaminate the baseline cache (the full arg-parse block below
            // re-confirms it and prints the banner).
            Settings.SmhCornerStand = args.Any(a => string.Equals(a, "--smhcorner", StringComparison.OrdinalIgnoreCase));
            MapDataManager.TryLoadMapDataAsync(mapNameForData);

            var gameConnection = new GameConnection();
            var babySharkBot = new AI.BabySharkAI(gameConnection);

            var zergBuildChoices = new AI.ZergBuildChoices(babySharkBot.GetUnderlyingBot());

            // NOTE: the Sharky BuildChoices pipeline below is vestigial in this
            // bot — BabySharkBot.cs wires the real opening build via
            // BabySharkBuildManager.SetBuild (TwelvePoolBuild / BuildIne /
            // NaturalObservationBuild for --naturaltest).
            var chosenBuildName = zergBuildChoices.BuildChoices.Builds.Keys.FirstOrDefault(name => name.Contains("MutaliskRush"))
                ?? zergBuildChoices.BuildChoices.Builds.Keys.First();
            var fixedSequence = new System.Collections.Generic.List<System.Collections.Generic.List<string>>
            {
                new System.Collections.Generic.List<string> { chosenBuildName }
            };
            zergBuildChoices.BuildChoices.BuildSequences[Race.Terran.ToString()] = fixedSequence;
            zergBuildChoices.BuildChoices.BuildSequences[Race.Zerg.ToString()] = fixedSequence;
            zergBuildChoices.BuildChoices.BuildSequences[Race.Protoss.ToString()] = fixedSequence;
            zergBuildChoices.BuildChoices.BuildSequences[Race.Random.ToString()] = fixedSequence;
            zergBuildChoices.BuildChoices.BuildSequences["Transition"] = fixedSequence;

            Console.WriteLine($"BabySharkBot: using fixed Zerg build '{chosenBuildName}'");
            babySharkBot.SetBuildChoices(Race.Zerg, zergBuildChoices.BuildChoices);

            // Enable zerg specific tasks by default.  These can also be enabled or disabled for specific builds in the OnFrame or StartBuild methods
            // todo create MicroTaskData for my bot
            //defaultSharkyBot.MicroTaskData[typeof(BurrowBlockExpansionsTask).Name].Enable();
            //defaultSharkyBot.MicroTaskData[typeof(BurrowDronesFromHarras).Name].Enable();
            //defaultSharkyBot.MicroTaskData[typeof(CreepTumorTask).Name].Enable();
            //defaultSharkyBot.MicroTaskData[typeof(ChangelingScoutTask).Name].Enable();
            //defaultSharkyBot.MicroTaskData[typeof(QueenCreepTask).Name].Enable();
            //defaultSharkyBot.MicroTaskData[typeof(QueenDefendTask).Name].Enable();
            //defaultSharkyBot.MicroTaskData[typeof(QueenInjectTask).Name].Enable();

            var sharkyExampleBot = babySharkBot.CreateBot();

            Settings.BaseDtosLoadedBeforeGameConnection = Settings.SerializeDataLoaded && Settings.MapDataLoaded;
            Console.WriteLine($"BabySharkBot: BaseDtos loaded before GameConnection opened={Settings.BaseDtosLoadedBeforeGameConnection}");

            // --seed=N (owner request 2026-10-23): explicit game random seed so
            // repeated self-launched single-player runs can roll different
            // spawns (without a seed SC2 reuses the same layout every game).
            var seedArg = args.FirstOrDefault(a => a.StartsWith("--seed=", StringComparison.OrdinalIgnoreCase));
            var gameSeed = -1;
            if (seedArg != null
                && int.TryParse(seedArg.Substring("--seed=".Length), out var parsedSeed)
                && parsedSeed >= 0)
            {
                gameSeed = parsedSeed;
                Console.WriteLine($"GameSeed ENABLED — random seed {gameSeed}");
            }

            // --t1test=N (owner spec 2026-10-24): T1-arrival test battery. Even N
            // rolls NW (seed 1), odd N rolls SE (seed 2), unless --seed is given
            // explicitly. Variant (N / 2) swaps the T1/T4/T5 startup books.
            var t1TestArg = args.FirstOrDefault(a => a.StartsWith("--t1test=", StringComparison.OrdinalIgnoreCase));
            if (t1TestArg != null
                && int.TryParse(t1TestArg.Substring("--t1test=".Length), out var parsedT1Test)
                && parsedT1Test >= 0
                && parsedT1Test <= 35)
            {
                Settings.T1TestNumber = parsedT1Test;
                if (gameSeed < 0)
                {
                    gameSeed = parsedT1Test % 2 == 0 ? 1 : 2;
                }
                Console.WriteLine($"T1TEST {parsedT1Test} ENABLED — variant {parsedT1Test / 2} seed {gameSeed} ({(parsedT1Test % 2 == 0 ? "NW" : "SE")})");
            }

            // --t4walk=N (owner test suite 2026-10-27 15:24): T4 mineral-walks
            // toward SA for N frames before the move to TB (the bump crossing
            // T1's SMH line). Sweep 9/10/11/12 to find the sweet spot.
            var t4WalkArg = args.FirstOrDefault(a => a.StartsWith("--t4walk=", StringComparison.OrdinalIgnoreCase));
            if (t4WalkArg != null
                && int.TryParse(t4WalkArg.Substring("--t4walk=".Length), out var parsedT4Walk)
                && parsedT4Walk >= 0)
            {
                Settings.T4WalkFrames = parsedT4Walk;
                Console.WriteLine($"T4WALK ENABLED — T4 mineral-walks {parsedT4Walk} frames before the TB bump move");
            }

            // --t4angle=D (owner test suite 2026-10-28 08:14): T4 STARTS with a
            // plain MOVE along its start -> SA heading rotated by D degrees
            // (0 = the old mineral-walk heading), switching to the TB move at
            // frame 8. Sweep -10/-5/0/+5/+10 around the heading.
            var t4AngleArg = args.FirstOrDefault(a => a.StartsWith("--t4angle=", StringComparison.OrdinalIgnoreCase));
            if (t4AngleArg != null
                && int.TryParse(t4AngleArg.Substring("--t4angle=".Length), out var parsedT4Angle)
                && parsedT4Angle >= -180
                && parsedT4Angle <= 180)
            {
                Settings.T4AngleDegrees = parsedT4Angle;
                Console.WriteLine($"T4ANGLE ENABLED — T4 starts with a MOVE rotated {parsedT4Angle} deg from the SA heading, TB move at frame 8");
            }

            // --t1angle=D (owner test 2026-11-04 20:31): rotate T1's start-line
            // direction by D degrees (+ = CCW in map coords) around its latched
            // start; --meetframe=N: the T1/T4 meet-switch frame (default 11).
            var t1AngleArg = args.FirstOrDefault(a => a.StartsWith("--t1angle=", StringComparison.OrdinalIgnoreCase));
            if (t1AngleArg != null
                && float.TryParse(t1AngleArg.Substring("--t1angle=".Length), out var parsedT1Angle)
                && parsedT1Angle >= -45f
                && parsedT1Angle <= 45f)
            {
                Settings.T1StartAngleDeg = parsedT1Angle;
                Console.WriteLine($"T1ANGLE ENABLED — T1 start line rotated {parsedT1Angle} deg (CCW +)");
            }
            var meetFrameArg = args.FirstOrDefault(a => a.StartsWith("--meetframe=", StringComparison.OrdinalIgnoreCase));
            if (meetFrameArg != null
                && int.TryParse(meetFrameArg.Substring("--meetframe=".Length), out var parsedMeetFrame)
                && parsedMeetFrame >= 5
                && parsedMeetFrame <= 20)
            {
                Settings.BumpMeetFrame = parsedMeetFrame;
                Console.WriteLine($"MEETFRAME ENABLED — T1/T4 meet switch at frame {parsedMeetFrame}");
            }

            // --smrthru=U (owner question 2026-11-05 15:27): SMR thru-distance sweep.
            var smrThruArg = args.FirstOrDefault(a => a.StartsWith("--smrthru=", StringComparison.OrdinalIgnoreCase));
            if (smrThruArg != null
                && float.TryParse(smrThruArg.Substring("--smrthru=".Length), out var parsedSmrThru)
                && parsedSmrThru >= 0f
                && parsedSmrThru <= 4f)
            {
                Settings.SmrThruUnitsOverride = parsedSmrThru;
                Console.WriteLine($"SMRTHRU ENABLED — SMR thru distance {parsedSmrThru}u");
            }

            // --staymin (owner spec 2026-11-05 15:47): steady-state harness —
            // every worker repeats only its first mineral on the {X,X}
            // self-pair SMH/SMR points forever; rotation chapters skipped.
            if (args.Any(a => string.Equals(a, "--staymin", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.StayMineralLoop = true;
                Console.WriteLine("STAYMIN ENABLED — every worker repeats its first mineral forever (self-pair SMH/SMR loop)");
            }

            // --smhcorner (owner spec 2026-11-06 15:21): for minerals whose SMR
            // direction is x-dominant (SB,BB,BA,YB,YA at SE), stand at the
            // "left point" — the SMR-facing side/corner of the stand
            // rectangle — instead of the measured face-midpoint SMH.
            if (args.Any(a => string.Equals(a, "--smhcorner", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.SmhCornerStand = true;
                Console.WriteLine("SMHCORNER ENABLED — x-dominant minerals stand at the SMR-facing left point of the footprint");
            }

            // Pattern B (owner spec 2026-11-07 05:47): under --staymin the four
            // second miners switch to the team A mineral after their first
            // B-mineral flip (T4->TA, T5->SA, Y5->BA, Y4->YA). ON by default
            // with --staymin; --nopatternb gives the pure stay-mineral control.
            if (args.Any(a => string.Equals(a, "--nopatternb", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternBMineralSwitch = false;
                Console.WriteLine("PATTERN-B DISABLED — second miners stay on their B minerals forever");
            }

            // --patterna (owner spec 2026-11-07 08:16 "Do Pattern A"): the four
            // extra workers stage at A minerals from the start (Sharky-style)
            // instead of mining B minerals first. Implies no Pattern B switch —
            // there is no B-mineral first set to switch from.
            if (args.Any(a => string.Equals(a, "--patterna", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternAStart = true;
                Settings.PatternBMineralSwitch = false;
                Console.WriteLine("PATTERN-A ENABLED — T5->TA, T4->SA, Y4->BA, Y5->YA from the start (Sharky-style)");
            }

            // --patternc (owner spec 2026-11-07 19:52): "mostly B pattern, but
            // send Y5 to SA" with per-trip rotation. Start: TA:T1, TB:T2+T4,
            // SA:T3+Y5, SB:S2+T5, BB:B2 (late), BA:Y3, YB:Y2+Y4, YA:Y1; then
            // the first miners on SA/SB/BB rotate right (T3: SA/SB alternating,
            // S2: SB/BB, B2: BB/BA from trip 2) and the B-pattern pairs repeat
            // twice before their second worker rotates (T4: TB,TB then TA/TB;
            // Y4: YB,YB then YA/YB). Every pair ends with 3 workers.
            if (args.Any(a => string.Equals(a, "--patternc", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternCRotation = true;
                Settings.PatternBMineralSwitch = false;
                Console.WriteLine("PATTERN-C ENABLED — Y5->SA; T3 SA/SB, S2 SB/BB, B2 BB/BA rotate right from trip 2; T4 TB,TB then TA/TB; Y4 YB,YB then YA/YB");
            }

            // --patternd (owner spec 2026-11-08 08:55): return-swap rotation —
            // Pattern A startups unchanged (bumps included), but from trip 2
            // the fast workers rotate onto the slow B minerals: T1 TA/TB,
            // T2 TB/TA, Y1 YA/YB, Y2 YB/YA, S2 SB/BB, B2 BB/SB; T4 stages SB
            // then alternates BB/SB; T3 solo SA; T5/Y5/Y3/Y4 pinned. Implies
            // Pattern A start; no Pattern B switch. Sim: ~f324 vs A's f334.
            if (args.Any(a => string.Equals(a, "--patternd", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternDReturnSwap = true;
                Settings.PatternAStart = true;
                Settings.PatternBMineralSwitch = false;
                Console.WriteLine("PATTERN-D ENABLED — return-swap: T1 TA/TB, T2 TB/TA, Y1 YA/YB, Y2 YB/YA, S2 SB/BB, B2 BB/SB, T4 SB then BB/SB; T3 solo SA");
            }
            // --stepwindow=N (owner spec 2026-11-14 06:30): extend the
            // Debugger.Break() step-thru window to frame N.
            var stepWindowArg = args.FirstOrDefault(a => a.StartsWith("--stepwindow=", StringComparison.OrdinalIgnoreCase));
            if (stepWindowArg != null && int.TryParse(stepWindowArg.Substring("--stepwindow=".Length), out var parsedStepWindow) && parsedStepWindow > 0)
            {
                Settings.StepBreakWindowOverride = parsedStepWindow;
                Console.WriteLine($"STEP WINDOW OVERRIDE — Debugger.Break() window extended to f{parsedStepWindow}");
            }
            // --jitlog (owner spec 2026-11-14 14:36): JIT collision tracking,
            // log-only — flip ETAs, outbound ETAs vs occupancy, predicted
            // crossing frames.
            if (args.Any(a => string.Equals(a, "--jitlog", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.JitCollisionLog = true;
                Console.WriteLine("JIT COLLISION LOG — flip ETAs / occupancy / crossing predictions (log-only)");
            }
            // --jitact (owner go 2026-11-14 16:39): JIT action layer rule 1 —
            // redirect the closest outbound worker to a flipped mineral when
            // the predicted post-flip idle window exceeds --jitidle (def 25).
            if (args.Any(a => string.Equals(a, "--jitact", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.JitActionLayer = true;
                Settings.JitCollisionLog = true;
                Console.WriteLine("JIT ACTION LAYER — idle-window reassignment active");
            }
            var jitIdleArg = args.FirstOrDefault(a => a.StartsWith("--jitidle=", StringComparison.OrdinalIgnoreCase));
            if (jitIdleArg != null
                && int.TryParse(jitIdleArg.Substring("--jitidle=".Length), out var jitIdleFrames))
            {
                Settings.JitIdleThreshold = jitIdleFrames;
                Console.WriteLine($"JIT IDLE THRESHOLD — {jitIdleFrames}f");
            }

            // --nocolavoid (owner spec 2026-11-07 18:57: "every mineral walk to
            // avoid collision was unnecessary — disable worker aware collision
            // and test that"): hard-disable the collision-avoidance detour
            // service so crossings are resolved by the engine. Counter-test to
            // the 2026-10-12/14 design assumption that head-on Move pairs grind.
            if (args.Any(a => string.Equals(a, "--nocolavoid", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.WorkerCollisionAvoidance = false;
                Console.WriteLine("COLLISION AVOIDANCE DISABLED — engine resolves all worker crossings");
            }

            // --suicide-role=NAME (owner spec 2026-10-28 08:14): the --suicide-end
            // trigger role — default T1; later Y1 when T1's first return is locked.
            var suicideRoleArg = args.FirstOrDefault(a => a.StartsWith("--suicide-role=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(suicideRoleArg))
            {
                Settings.EarlySuicideRole = suicideRoleArg.Substring("--suicide-role=".Length).Trim();
                Console.WriteLine($"SUICIDE ROLE — {Settings.EarlySuicideRole}");
            }

            // --suicide-200 (owner spec 2026-10-28 08:14): keep the OLD 200-mineral
            // bank trigger for later testing; --suicide-end alone now means the
            // role's first returned cargo (time-to-first-return metric).
            Settings.EarlySuicideOnRoleReturn = !args.Any(a => string.Equals(a, "--suicide-200", StringComparison.OrdinalIgnoreCase));

            // --gatelead=N (owner test suite 2026-10-28 08:51): fire T1's SMH
            // Smart N frames before SMH arrival. 0 = on-arrival (control).
            var gateLeadArg = args.FirstOrDefault(a => a.StartsWith("--gatelead=", StringComparison.OrdinalIgnoreCase));
            if (gateLeadArg != null
                && int.TryParse(gateLeadArg.Substring("--gatelead=".Length), out var parsedGateLead)
                && parsedGateLead >= 0
                && parsedGateLead <= 5)
            {
                Settings.SmhSmartGateLeadFrames = parsedGateLead;
                Console.WriteLine($"GATELEAD ENABLED — T1's SMH Smart fires {parsedGateLead} frames before SMH arrival");
            }

            // --t1trace (owner test suite 2026-10-28 08:51): per-frame T1
            // position/ability dump over relative frames 5-80.
            Settings.T1PositionTrace = args.Any(a => string.Equals(a, "--t1trace", StringComparison.OrdinalIgnoreCase));

            // --bumpviz (owner spec 2026-10-26 05:45): persistent per-frame debug
            // geometry for the bump-choreography discussion (both start locations).
            Settings.BumpChoreographyViz = args.Any(a => string.Equals(a, "--bumpviz", StringComparison.OrdinalIgnoreCase));
            Settings.BumpVizHarvestBands = args.Any(a => string.Equals(a, "--hbands", StringComparison.OrdinalIgnoreCase));
            if (Settings.BumpVizHarvestBands)
            {
                Console.WriteLine("HARVEST BANDS VIZ — 3 hatchery circles (min/mid/max stand distance) + markers at the 8 harvest stands");
            }
            if (Settings.BumpChoreographyViz)
            {
                Console.WriteLine("BUMPVIZ ENABLED — hatchery/mineral/telemetry guide overlay drawn every frame (both starts)");
            }

            // --live (owner request 2026-10-26 06:46 "make it live so that I can
            // step thru"): launch the single-player game in REAL-TIME mode — the
            // simulation advances at human pace and the SC2 client can pause it,
            // instead of the bot driving every frame at max speed.
            var liveMode = args.Any(a => string.Equals(a, "--live", StringComparison.OrdinalIgnoreCase));
            if (liveMode)
            {
                Console.WriteLine("LIVE MODE ENABLED — real-time game, pause/step from the SC2 client");
            }

            var myRace = Race.Zerg;
            // Spawn guard removed 2026-10-20 (owner directive "Turn NW Start back
            // on"): NW and SE games both run in self-launched single-player games.
            if (args.Length == 0 || !hasLadderArgsEarly)
            {
                gameConnection.RunSinglePlayer(sharkyExampleBot, @randomMap, myRace, Race.Zerg, Difficulty.CheatInsane, AIBuild.RandomBuild, randomSeed: gameSeed, botName: botName, realTime: liveMode).Wait();
            }
            else
            {
                gameConnection.RunLadder(sharkyExampleBot, myRace, args, botName: botName).Wait();
            }

            var miningManager = babySharkBot.Managers.OfType<BabySharkMiningManager>().FirstOrDefault();
            // --flip68build rewrites the teal four's AssignedWorkers routes in
            // memory — never persist that harness-only mutation into the cached dat.
            if (Settings.Flip68BuildMode)
            {
                Console.WriteLine("BabySharkBot: FLIP68 active — skipping BaseDtos dat save so the cached dat stays clean.");
            }
            else
            {
                WriteBaseDataSnapshot(mapNameForData, miningManager?.CurrentMapData);
            }
        }

        private static bool IsHarnessArg(string arg)
        {
            if (string.IsNullOrEmpty(arg))
            {
                return false;
            }
            if (string.Equals(arg, "--suicide-end", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--no-spawn-guard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--bumpviz", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--hbands", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--live", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--flip68build", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // --bump-test=N / --bump-angle=X (=value form so the value token never
            // leaks into the ladder-args check).
            return arg.StartsWith("--bump-test=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--bump-angle=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--seed=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--t1test=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--t4walk=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--t4angle=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--t1angle=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--meetframe=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--smrthru=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--suicide-role=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--gatelead=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--suicide-200", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--staymin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--smhcorner", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--nopatternb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--patterna", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--patternc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--patternd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--nocolavoid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--t1trace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--naturaltest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--smsweep", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--smstage", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--smhandoff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--f0test", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--f0gate=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--stepwindow=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--jitlog", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--jitact", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--jitidle=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--f0nobump", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--f0return=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--f0switch=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--f0switchctl", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--f0t1via=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--f0y1via=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--smminerals=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--smradius=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--smbearing=", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeMapNameForFilename(string map)
        {
            if (string.IsNullOrWhiteSpace(map))
            {
                return "unknown_map";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = map.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
            return new string(chars).Trim();
        }

        private static void ConfigureRlModes(string[] args)
        {
            var hasRecordFlag = args.Any(a => string.Equals(a, "--record", StringComparison.OrdinalIgnoreCase));
            var hasTrainFlag = args.Any(a => string.Equals(a, "--train", StringComparison.OrdinalIgnoreCase));
            var hasEvaluateFlag = args.Any(a => string.Equals(a, "--evaluate", StringComparison.OrdinalIgnoreCase));

            var modelExists = File.Exists(Settings.RLModelPath);
            var metadataExists = File.Exists(Settings.RLMetadataPath);
            var trainingDataExists = Directory.Exists(Settings.RLTrainingDataPath);

            Settings.EnableImitationRecording = hasRecordFlag;
            Settings.EnableTraining = hasTrainFlag && trainingDataExists;
            Settings.EnableRLInference = (hasTrainFlag || hasEvaluateFlag || modelExists) && modelExists && metadataExists;

            // Autorun test harness (owner request 2026-10-17 22:41): end the
            // game at the 200-mineral bank so the optimization loop can restart.
            // 2026-11-05 (owner spec 15:47 "Suicide at 200 bank"): --suicide-200
            // alone now also enables the harness — previously it only flipped the
            // trigger selection and never ended anything by itself.
            Settings.EarlySuicideTestEnd = args.Any(a => string.Equals(a, "--suicide-end", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--suicide-200", StringComparison.OrdinalIgnoreCase));
            if (Settings.EarlySuicideTestEnd)
            {
                Console.WriteLine(Settings.EarlySuicideOnRoleReturn
                    ? "EarlySuicideTestEnd ENABLED — when T1 AND Y1 have both returned their first cargo the pool is cancelled and all workers attack our own hatchery"
                    : "EarlySuicideTestEnd ENABLED (200 mode) — at 200 minerals the pool is cancelled and all workers attack our own hatchery");
            }

            // Bump test harness (owner spec 2026-10-18; frame-schedule choreography
            // 2026-10-20): --bump-test=1|2|3 selects the opening configuration —
            // mode 1 = no-help baseline (helpers mineral-walk at frame 0); modes
            // 0/2/3 = the frame-schedule choreography (T4/T5 mineral-walk SA, Y4/B2
            // SB for frames 0-5, then all four follow T1/Y1's live position every
            // frame 6-18, mineral-walk SB at frame 19, and stage on their own B
            // mineral at frame 25) — mode 0 is the default, so no flag is needed.
            // --bump-angle=degrees is the retired stand-point rotation knob (now
            // inert). Both are single-player harness flags (=value form so the
            // ladder-arg filter above can recognize them).
            var bumpTestArg = args.FirstOrDefault(a => a.StartsWith("--bump-test=", StringComparison.OrdinalIgnoreCase));
            if (bumpTestArg != null
                && int.TryParse(bumpTestArg.Substring("--bump-test=".Length), out var bumpTestMode)
                && bumpTestMode >= 1 && bumpTestMode <= 3)
            {
                Settings.BumpTestMode = bumpTestMode;
                Console.WriteLine($"BumpTestMode ENABLED — mode {bumpTestMode} opening configuration");
            }
            else
            {
                // Always print the effective mode: mode 0 (no flag) IS the
                // frame-schedule choreography now (owner directive 2026-10-20
                // 19:03) — T4/T5 target SA frames 0-5 then follow T1 from
                // frame 6. Only mode 1 (--bump-test=1) keeps the no-help
                // baseline opening.
                Console.WriteLine($"BumpTestMode = {Settings.BumpTestMode} (0/2/3 = frame-schedule choreography; 1 = no-help baseline)");
            }
            var bumpAngleArg = args.FirstOrDefault(a => a.StartsWith("--bump-angle=", StringComparison.OrdinalIgnoreCase));
            if (bumpAngleArg != null
                && float.TryParse(bumpAngleArg.Substring("--bump-angle=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bumpAngleDegrees))
            {
                Settings.BumpAngleDegrees = bumpAngleDegrees;
                Console.WriteLine($"BumpAngleDegrees ENABLED — {bumpAngleDegrees:F1}° bumper stand-point rotation");
            }

            if (hasTrainFlag && !trainingDataExists)
            {
                Console.WriteLine($"RL disabled: missing training data folder '{Settings.RLTrainingDataPath}'");
            }

            if (!modelExists || !metadataExists)
            {
                Console.WriteLine($"RL inference disabled: modelExists={modelExists}, metadataExists={metadataExists}");
            }

            Console.WriteLine($"RL mode: record={Settings.EnableImitationRecording}, train={Settings.EnableTraining}, inference={Settings.EnableRLInference}");
        }

        private static void DumpMapDataToJson(string mapName)
        {
            var safeMapName = SanitizeMapNameForFilename(mapName);
            var dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "data", "base");
            var versionPath = Path.Combine(dataFolder, $"{safeMapName}.Version{Settings.SpeedMiningCacheVersion}.dat");
            var outputPath = Path.Combine(dataFolder, $"{safeMapName}.dump.json");
            Console.WriteLine($"--dump-dat: reading {versionPath}");
            var bytes = File.ReadAllBytes(versionPath);
            var data = MemoryPackSerializer.Deserialize<MawBaseLocationData>(bytes);
            if (data == null)
            {
                Console.WriteLine("--dump-dat: deserialize returned null");
                return;
            }
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(outputPath, json);
            Console.WriteLine($"--dump-dat: wrote {outputPath} ({json.Length} chars)");
        }

        private static void ApplyDatAdjustments(string mapName, string adjustmentsPath)
        {
            var safeMapName = SanitizeMapNameForFilename(mapName);
            var dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "data", "base");
            var versionPath = Path.Combine(dataFolder, $"{safeMapName}.Version{Settings.SpeedMiningCacheVersion}.dat");
            var backupPath = versionPath + ".bak";
            Console.WriteLine($"--apply-dat: reading {versionPath}");
            var bytes = File.ReadAllBytes(versionPath);
            var data = MemoryPackSerializer.Deserialize<MawBaseLocationData>(bytes);
            if (data == null)
            {
                Console.WriteLine("--apply-dat: deserialize returned null");
                return;
            }

            var doc = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(adjustmentsPath));
            var adjustments = doc["Adjustments"] as Newtonsoft.Json.Linq.JArray;
            if (adjustments == null)
            {
                Console.WriteLine("--apply-dat: no Adjustments array in " + adjustmentsPath);
                return;
            }

            foreach (var adj in adjustments)
            {
                var startIndex = (int)adj["StartIndex"];
                var label = (string)adj["MineralLabel"];
                // Optional absolute SM-point override (measured telemetry). When
                // SmHarvestX/SmHarvestY and/or SmReturnX/SmReturnY are present the
                // stored SmHarvestPoint/SmReturnPoint are set absolutely (Z is
                // preserved); HarvestPoint/ReturnPoint are NOT touched.
                if (adj["SmHarvestX"] != null || adj["SmReturnX"] != null)
                {
                    ApplySmPointOverride(data, startIndex, label, adj);
                }
                // Legacy harvest-shift (delta-moves SmHarvestPoint with it).
                if (adj["NewX"] != null && adj["NewY"] != null)
                {
                    ApplyHarvestShift(data, startIndex, label, (float)adj["NewX"], (float)adj["NewY"]);
                }
            }

            File.Copy(versionPath, backupPath, overwrite: true);
            var updated = MemoryPackSerializer.Serialize(data);
            File.WriteAllBytes(versionPath, updated);
            Console.WriteLine($"--apply-dat: backed up to {backupPath}");
            Console.WriteLine($"--apply-dat: wrote updated {versionPath} ({updated.Length} bytes)");
        }

        // Shifts every stored harvest coordinate for one mineral at one start to
        // the measured stop position. The SmHarvestPoint (small/standoff point)
        // keeps its existing offset from HarvestPoint by shifting by the same
        // delta, so whatever geometry convention produced the dat is preserved.
        private static void ApplyHarvestShift(MawBaseLocationData data, int startIndex, string label, float newX, float newY)
        {
            var total = 0;

            OrderedMineral MatchOrderedMineral(IEnumerable<OrderedMineral> minerals)
            {
                return minerals?.FirstOrDefault(m => string.Equals(m?.FinalLabel, label, StringComparison.OrdinalIgnoreCase));
            }

            void ShiftPair(Vector2Dto harvest, Vector2Dto smHarvest)
            {
                if (harvest == null)
                {
                    return;
                }
                var dx = newX - harvest.X;
                var dy = newY - harvest.Y;
                if (Math.Abs(dx) < 0.0005f && Math.Abs(dy) < 0.0005f)
                {
                    return;
                }
                harvest.X = newX;
                harvest.Y = newY;
                if (smHarvest != null)
                {
                    smHarvest.X += dx;
                    smHarvest.Y += dy;
                }
                total++;
            }

            // 1. Canonical ordered minerals.
            if (startIndex >= 0 && startIndex < (data.OrderedMainMinerals?.Count ?? 0))
            {
                var m = MatchOrderedMineral(data.OrderedMainMinerals[startIndex]);
                if (m != null)
                {
                    ShiftPair(m.HarvestPoint, m.SmHarvestPoint);
                }
            }

            // 2. Mineral cargo points — matched by unit tag when present, else by
            //    resource position (persisted cargo rows have UnitTag 0 on some
            //    schema versions, so position is the reliable key).
            if (startIndex >= 0 && startIndex < (data.OrderedMainMinerals?.Count ?? 0))
            {
                var m = MatchOrderedMineral(data.OrderedMainMinerals[startIndex]);
                if (m != null && startIndex < (data.MainMineralCargoPoints?.Count ?? 0))
                {
                    var cargoList = data.MainMineralCargoPoints[startIndex];
                    var cp = cargoList?.FirstOrDefault(c => c?.UnitTag != 0 && c.UnitTag == m.UnitTag);
                    if (cp == null && m.Position != null)
                    {
                        cp = cargoList?.FirstOrDefault(c =>
                            c?.ResourcePosition != null
                            && Math.Abs(c.ResourcePosition.X - m.Position.X) <= 0.001f
                            && Math.Abs(c.ResourcePosition.Y - m.Position.Y) <= 0.001f);
                    }
                    if (cp != null)
                    {
                        ShiftPair(cp.HarvestPoint, cp.SmHarvestPoint);
                    }
                }
            }

            // 3. Mining target cross-table routes (ResourceLabel == destination mineral).
            if (startIndex >= 0 && startIndex < (data.MiningTargetCrossTables?.Count ?? 0))
            {
                var table = data.MiningTargetCrossTables[startIndex];
                foreach (var route in table?.Routes?.SelectMany(r => r ?? new List<MiningTargetDto>()) ?? Enumerable.Empty<MiningTargetDto>())
                {
                    if (route == null)
                    {
                        continue;
                    }
                    if (string.Equals(route.ResourceLabel, label, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(route.ToResourceLabel, label, StringComparison.OrdinalIgnoreCase))
                    {
                        ShiftPair(route.HarvestPoint, route.SmHarvestPoint);
                    }
                }
            }

            // 4. Assigned-worker mining targets.
            if (startIndex >= 0 && startIndex < (data.AssignedWorkers?.Count ?? 0))
            {
                foreach (var target in data.AssignedWorkers[startIndex]?.SelectMany(w => w?.MiningTargets ?? new List<MiningTargetDto>()) ?? Enumerable.Empty<MiningTargetDto>())
                {
                    if (target == null)
                    {
                        continue;
                    }
                    if (string.Equals(target.ResourceLabel, label, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(target.ToResourceLabel, label, StringComparison.OrdinalIgnoreCase))
                    {
                        ShiftPair(target.HarvestPoint, target.SmHarvestPoint);
                    }
                }
            }

            // 5. Team patch assignment mineral copies.
            if (startIndex >= 0 && startIndex < (data.TeamPatchAssignments?.Count ?? 0))
            {
                foreach (var m in data.TeamPatchAssignments[startIndex]?.SelectMany(a => a?.Minerals ?? new List<OrderedMineral>()) ?? Enumerable.Empty<OrderedMineral>())
                {
                    if (m != null && string.Equals(m.FinalLabel, label, StringComparison.OrdinalIgnoreCase))
                    {
                        ShiftPair(m.HarvestPoint, m.SmHarvestPoint);
                    }
                }
            }

            Console.WriteLine($"--apply-dat: start={startIndex} {label} -> ({newX:F3},{newY:F3}) [{total} coordinate pairs shifted]");
        }

        // Absolute override of the stored SM harvest/return points from measured
        // telemetry (owner spec 2026-10-28: "SM Harvest is on the Mineral
        // footprint. T1 Starts very near SM Return Cargo."). Only
        // SmHarvestPoint/SmReturnPoint are written (X/Y; Z preserved) —
        // HarvestPoint/ReturnPoint keep their existing footprint geometry.
        private static void ApplySmPointOverride(MawBaseLocationData data, int startIndex, string label, Newtonsoft.Json.Linq.JToken adj)
        {
            var total = 0;

            void SetPair(Vector2Dto smHarvest, Vector2Dto smReturn)
            {
                if (smHarvest != null && adj["SmHarvestX"] != null && adj["SmHarvestY"] != null)
                {
                    smHarvest.X = (float)adj["SmHarvestX"];
                    smHarvest.Y = (float)adj["SmHarvestY"];
                    total++;
                }
                if (smReturn != null && adj["SmReturnX"] != null && adj["SmReturnY"] != null)
                {
                    smReturn.X = (float)adj["SmReturnX"];
                    smReturn.Y = (float)adj["SmReturnY"];
                    total++;
                }
            }

            // 1. Canonical ordered minerals.
            if (startIndex >= 0 && startIndex < (data.OrderedMainMinerals?.Count ?? 0))
            {
                var m = data.OrderedMainMinerals[startIndex]
                    ?.FirstOrDefault(mm => string.Equals(mm?.FinalLabel, label, StringComparison.OrdinalIgnoreCase));
                if (m != null)
                {
                    SetPair(m.SmHarvestPoint, m.SmReturnPoint);
                }
            }

            // 2. Mineral cargo points — matched by unit tag when present, else by
            //    resource position (same keys as ApplyHarvestShift).
            if (startIndex >= 0 && startIndex < (data.OrderedMainMinerals?.Count ?? 0)
                && startIndex < (data.MainMineralCargoPoints?.Count ?? 0))
            {
                var m = data.OrderedMainMinerals[startIndex]
                    ?.FirstOrDefault(mm => string.Equals(mm?.FinalLabel, label, StringComparison.OrdinalIgnoreCase));
                var cargoList = data.MainMineralCargoPoints[startIndex];
                var cp = m != null
                    ? cargoList?.FirstOrDefault(c => c?.UnitTag != 0 && c.UnitTag == m.UnitTag)
                    : null;
                if (cp == null && m?.Position != null)
                {
                    cp = cargoList?.FirstOrDefault(c =>
                        c?.ResourcePosition != null
                        && Math.Abs(c.ResourcePosition.X - m.Position.X) <= 0.001f
                        && Math.Abs(c.ResourcePosition.Y - m.Position.Y) <= 0.001f);
                }
                if (cp != null)
                {
                    SetPair(cp.SmHarvestPoint, cp.SmReturnPoint);
                }
            }

            // 3. Mining target cross-table routes.
            if (startIndex >= 0 && startIndex < (data.MiningTargetCrossTables?.Count ?? 0))
            {
                var table = data.MiningTargetCrossTables[startIndex];
                foreach (var route in table?.Routes?.SelectMany(r => r ?? new List<MiningTargetDto>()) ?? Enumerable.Empty<MiningTargetDto>())
                {
                    if (route == null)
                    {
                        continue;
                    }
                    if (string.Equals(route.ResourceLabel, label, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(route.ToResourceLabel, label, StringComparison.OrdinalIgnoreCase))
                    {
                        SetPair(route.SmHarvestPoint, route.SmReturnPoint);
                    }
                }
            }

            // 4. Assigned-worker mining targets.
            if (startIndex >= 0 && startIndex < (data.AssignedWorkers?.Count ?? 0))
            {
                foreach (var target in data.AssignedWorkers[startIndex]?.SelectMany(w => w?.MiningTargets ?? new List<MiningTargetDto>()) ?? Enumerable.Empty<MiningTargetDto>())
                {
                    if (target == null)
                    {
                        continue;
                    }
                    if (string.Equals(target.ResourceLabel, label, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(target.ToResourceLabel, label, StringComparison.OrdinalIgnoreCase))
                    {
                        SetPair(target.SmHarvestPoint, target.SmReturnPoint);
                    }
                }
            }

            // 5. Team patch assignment mineral copies.
            if (startIndex >= 0 && startIndex < (data.TeamPatchAssignments?.Count ?? 0))
            {
                foreach (var m in data.TeamPatchAssignments[startIndex]?.SelectMany(a => a?.Minerals ?? new List<OrderedMineral>()) ?? Enumerable.Empty<OrderedMineral>())
                {
                    if (m != null && string.Equals(m.FinalLabel, label, StringComparison.OrdinalIgnoreCase))
                    {
                        SetPair(m.SmHarvestPoint, m.SmReturnPoint);
                    }
                }
            }

            Console.WriteLine($"--apply-dat: start={startIndex} {label} SM override [{total} SM points set]"
                + (adj["SmHarvestX"] != null ? $" smH=({(float)adj["SmHarvestX"]:F3},{(float)adj["SmHarvestY"]:F3})" : "")
                + (adj["SmReturnX"] != null ? $" smR=({(float)adj["SmReturnX"]:F3},{(float)adj["SmReturnY"]:F3})" : ""));
        }

        private static void WriteBaseDataSnapshot(string mapName, MawBaseLocationData mapData)
        {
            if (mapData == null)
            {
                Console.WriteLine("BabySharkBot: no BaseDtos data was available to write.");
                return;
            }

            var safeMapName = SanitizeMapNameForFilename(mapName);
            var outputDirectory = Path.Combine(AppContext.BaseDirectory, "data", "base");
            Directory.CreateDirectory(outputDirectory);

            var outputPath = Path.Combine(outputDirectory, $"{safeMapName}.Version{Settings.SpeedMiningCacheVersion}.dat");
            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            MemoryPackSerializer.SerializeAsync(outputStream, mapData).GetAwaiter().GetResult();
            Console.WriteLine($"BabySharkBot: wrote BaseDtos MemoryPack data to {outputPath}");
        }
    }
}
