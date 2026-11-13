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

            // --patternjit (owner spec 2026-11-09 17:59): PatternJIT harness,
            // teams of three per mineral pair, flat W->role map; bite 1 gives
            // ONLY S2 a book (staymin thru shape to SB) and moves the suicide
            // trigger to S2's first 1184 flip. Everyone else auto-harvests.
            if (args.Any(a => string.Equals(a, "--patternjit", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitMode = true;
                // Bite-1 lock (owner spec 2026-11-10 15:27): "On the first set
                // we lock in frame" — the sweep winner is a direct Smart on SB
                // at frame 16 (flip f67, stand closest to the {SA,SB} return
                // point). A later --sbsmart=N still overrides; --sbsmart=-1
                // disables for pure stand-point probes.
                Settings.PatternJitSbSmartFrame = 16;
                // Bite 4 (owner spec 2026-11-10 18:48): S1 speed-mines SA on
                // the same shape (f0 Move to the stand + direct Smart);
                // --sasmart=N overrides. S1+S3 take turns on SA (S3 stages on
                // the 32-harvest-frame release gate); S2 stays on SB; the run
                // ends at the 200-mineral bank.
                Settings.PatternJitSaSmartFrame = 16;
                Console.WriteLine("PATTERN-JIT ENABLED — W1=S3 W2=T3 W3=T1 W4=T2 W5=S1 W6=S2 W7=B1 W8=B2 W9=Y1 W10=Y2 W11=Y3 W12=B3; bite 4: S1 SA stand+Smart, S3 SA wait+HRG, S2 SB locked, suicide at 200 bank");
            }

            // --sbsmh=X,Y (owner spec 2026-11-09 21:36): override the PatternJIT
            // SB stand point without a rebuild, e.g. --sbsmh=132.00,19.60
            // (invariant-culture floats).
            var sbSmhArg = args.FirstOrDefault(a => a.StartsWith("--sbsmh=", StringComparison.OrdinalIgnoreCase));
            if (sbSmhArg != null)
            {
                var parts = sbSmhArg.Substring("--sbsmh=".Length).Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sx)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sy))
                {
                    Settings.PatternJitSbSmhX = sx;
                    Settings.PatternJitSbSmhY = sy;
                    Console.WriteLine($"PATTERN-JIT SB STAND OVERRIDE — smH=({sx:F3},{sy:F3})");
                }
                else
                {
                    Console.WriteLine($"PATTERN-JIT SB STAND OVERRIDE REJECTED — could not parse '{sbSmhArg}' (want --sbsmh=X,Y)");
                }
            }

            // --sbsmart=N (owner spec 2026-11-10 09:22: "f22 issue a Smart on
            // SB, run test again with f21, f20, f19"): S2 gets a direct Smart
            // on SB at exactly frame N.
            var sbSmartArg = args.FirstOrDefault(a => a.StartsWith("--sbsmart=", StringComparison.OrdinalIgnoreCase));
            if (sbSmartArg != null)
            {
                if (int.TryParse(sbSmartArg.Substring("--sbsmart=".Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var smartFrame))
                {
                    Settings.PatternJitSbSmartFrame = smartFrame;
                    Console.WriteLine($"PATTERN-JIT SB SMART — direct Smart on SB at frame {smartFrame}");
                }
                else
                {
                    Console.WriteLine($"PATTERN-JIT SB SMART REJECTED — could not parse '{sbSmartArg}' (want --sbsmart=N)");
                }
            }

            // --sasmh=X,Y (bite 4, owner spec 2026-11-10 18:48): S1's SA stand
            // override for the west-angle sweep (invariant-culture floats).
            var saSmhArg = args.FirstOrDefault(a => a.StartsWith("--sasmh=", StringComparison.OrdinalIgnoreCase));
            if (saSmhArg != null)
            {
                var parts = saSmhArg.Substring("--sasmh=".Length).Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sax)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var say))
                {
                    Settings.PatternJitSaSmhX = sax;
                    Settings.PatternJitSaSmhY = say;
                    Console.WriteLine($"PATTERN-JIT SA STAND OVERRIDE — smH=({sax:F3},{say:F3})");
                }
                else
                {
                    Console.WriteLine($"PATTERN-JIT SA STAND OVERRIDE REJECTED — could not parse '{saSmhArg}' (want --sasmh=X,Y)");
                }
            }

            // --sasmart=N (bite 4): frame of S1's direct Smart on SA.
            var saSmartArg = args.FirstOrDefault(a => a.StartsWith("--sasmart=", StringComparison.OrdinalIgnoreCase));
            if (saSmartArg != null)
            {
                if (int.TryParse(saSmartArg.Substring("--sasmart=".Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var saSmartFrame))
                {
                    Settings.PatternJitSaSmartFrame = saSmartFrame;
                    Console.WriteLine($"PATTERN-JIT SA SMART — direct Smart on SA at frame {saSmartFrame}");
                }
                else
                {
                    Console.WriteLine($"PATTERN-JIT SA SMART REJECTED — could not parse '{saSmartArg}' (want --sasmart=N)");
                }
            }

            // --sasawait=X,Y (bite 4): S3's SA wait spot override.
            var saWaitArg = args.FirstOrDefault(a => a.StartsWith("--sasawait=", StringComparison.OrdinalIgnoreCase));
            if (saWaitArg != null)
            {
                var parts = saWaitArg.Substring("--sasawait=".Length).Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var swx)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var swy))
                {
                    Settings.PatternJitSa3WaitX = swx;
                    Settings.PatternJitSa3WaitY = swy;
                    Console.WriteLine($"PATTERN-JIT SA WAIT OVERRIDE — S3 wait=({swx:F3},{swy:F3})");
                }
                else
                {
                    Console.WriteLine($"PATTERN-JIT SA WAIT OVERRIDE REJECTED — could not parse '{saWaitArg}' (want --sasawait=X,Y)");
                }
            }

            // --t2walk=SB|BB|BA (bite 5a, owner spec 2026-11-10 21:00): T2
            // mineral-walks toward the named mineral instead of parking —
            // walk-line telemetry for the S1 bump geometry. BA added for the
            // owner probe 2026-11-11 07:56 ("Mineral Walk T2 to BA").
            var t2WalkArg = args.FirstOrDefault(a => a.StartsWith("--t2walk=", StringComparison.OrdinalIgnoreCase));
            if (t2WalkArg != null)
            {
                var walkLabel = t2WalkArg.Substring("--t2walk=".Length).Trim().ToUpperInvariant();
                if (walkLabel == "SB" || walkLabel == "BB" || walkLabel == "BA")
                {
                    Settings.PatternJitT2WalkLabel = walkLabel;
                    Console.WriteLine($"PATTERN-JIT T2 WALK — T2 mineral-walks toward {walkLabel} (telemetry only, no arrival intent)");
                }
                else
                {
                    Console.WriteLine($"PATTERN-JIT T2 WALK REJECTED — could not parse '{t2WalkArg}' (want --t2walk=SB, --t2walk=BB or --t2walk=BA)");
                }
            }

            // --t2bump / --t2meet=N / --t2push=N (bite 5b, owner spec
            // 2026-11-10 21:35): the T2->S1 bump choreography. --t2bump
            // implies T2's mineral walk (default SB) since the walk IS the
            // bump approach.
            if (args.Any(a => string.Equals(a, "--t2bump", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitT2Bump = true;
                if (string.IsNullOrWhiteSpace(Settings.PatternJitT2WalkLabel))
                {
                    Settings.PatternJitT2WalkLabel = "SB";
                }
                Console.WriteLine($"PATTERN-JIT T2 BUMP — S1 angled start, meet f{Settings.PatternJitT2MeetFrame}, push {Settings.PatternJitT2PushFrames}f, T2 -> TB -> park");
            }

            // --juggler (bite 5c, owner spec 2026-11-10 21:00): SA/SB
            // three-worker rotation books + corridor cross-bump protocol.
            if (args.Any(a => string.Equals(a, "--juggler", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitJuggler = true;
                Console.WriteLine("PATTERN-JIT JUGGLER — S1{SA,SB} S2{SB,SA} S3{SA,SB}, shared {SA,SB} return point, corridor mineral-walk crossings, suicide at 200 bank");
            }
            var t2MeetArg = args.FirstOrDefault(a => a.StartsWith("--t2meet=", StringComparison.OrdinalIgnoreCase));
            if (t2MeetArg != null
                && int.TryParse(t2MeetArg.Substring("--t2meet=".Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var t2MeetFrame))
            {
                Settings.PatternJitT2MeetFrame = t2MeetFrame;
                Console.WriteLine($"PATTERN-JIT T2 MEET — meet frame {t2MeetFrame}");
            }
            var t2PushArg = args.FirstOrDefault(a => a.StartsWith("--t2push=", StringComparison.OrdinalIgnoreCase));
            if (t2PushArg != null
                && int.TryParse(t2PushArg.Substring("--t2push=".Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var t2PushFrames))
            {
                Settings.PatternJitT2PushFrames = t2PushFrames;
                Console.WriteLine($"PATTERN-JIT T2 PUSH — {t2PushFrames} frames");
            }

            // --moveprobe (owner spec 2026-11-11 08:36): f8 STOP T2 + Move S1
            // to 0.5W/0.6N of SA, f9 the same Move to T2.
            if (args.Any(a => string.Equals(a, "--moveprobe", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitMoveProbe = true;
                Console.WriteLine($"PATTERN-JIT MOVE PROBE — f{Settings.PatternJitProbeStopFrame} STOP T2 + Move S1 to SA+({Settings.PatternJitProbeDx:F2},{Settings.PatternJitProbeDy:F2}), f{Settings.PatternJitProbeT2MoveFrame} same Move to T2");
            }
            if (args.Any(a => string.Equals(a, "--s3stopbump", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitS3StopBump = true;
                Console.WriteLine("PATTERN-JIT S3 STOP-BUMP — probe drives S3: walk SB, peel 1u west of SA, stop at S1's seat, Smart SA at flip+1");
            }
            if (args.Any(a => string.Equals(a, "--minofftest", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitMinOffTest = true;
                Console.WriteLine("PATTERN-JIT MIN-OFF TEST — Move one parked worker to N 0.05 / W 1.05 of SB, BB, BA, YB, YA and log rest positions");
            }
            var smartProbeArg = args.FirstOrDefault(a => a.StartsWith("--smartprobe=", StringComparison.OrdinalIgnoreCase));
            if (smartProbeArg != null
                && float.TryParse(smartProbeArg.Substring("--smartprobe=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var smartProbeDist))
            {
                Settings.PatternJitSmartProbe = true;
                Settings.PatternJitSmartProbeDist = smartProbeDist;
                Console.WriteLine($"PATTERN-JIT SMART PROBE — park 8 workers at ideal+{smartProbeDist:F1}u toward hatchery, Smart, log harvest stand");
            }
            var thruProbeArg = args.FirstOrDefault(a => a.StartsWith("--thruprobe=", StringComparison.OrdinalIgnoreCase));
            if (thruProbeArg != null
                && float.TryParse(thruProbeArg.Substring("--thruprobe=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var thruProbeDepth))
            {
                Settings.PatternJitThruProbe = true;
                Settings.PatternJitThruProbeDepth = thruProbeDepth;
                Console.WriteLine($"PATTERN-JIT THRU PROBE — thru depth {thruProbeDepth:F2}u past stand toward center, Smart at 1.15u gate, log thru->harvest frames");
            }
            var standPastArg = args.FirstOrDefault(a => a.StartsWith("--standpast=", StringComparison.OrdinalIgnoreCase));
            if (standPastArg != null
                && float.TryParse(standPastArg.Substring("--standpast=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var standPastDist))
            {
                Settings.PatternJitStandPastProbe = true;
                Settings.PatternJitStandPastDist = standPastDist;
                Console.WriteLine($"PATTERN-JIT STAND-PAST PROBE — Move one worker per mineral to stand+{standPastDist:F2}u toward center, log rest vs stand");
            }
            if (args.Any(a => string.Equals(a, "--retprobe", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitReturnProbe = true;
                Console.WriteLine("PATTERN-JIT RETURN PROBE — S2 on SB, Smart-return hatchery-distance ladder 4.0->1.0 step 0.25, 11 leashed");
            }
            if (args.Any(a => string.Equals(a, "--sbloop", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitSbLoop = true;
                Console.WriteLine("PATTERN-JIT SB LOOP — continuous S2 SB speed mining, both tuned legs, per-trip cycle timing");
            }
            var probeFramesArg = args.FirstOrDefault(a => a.StartsWith("--probeframes=", StringComparison.OrdinalIgnoreCase));
            if (probeFramesArg != null)
            {
                var parts = probeFramesArg.Substring("--probeframes=".Length).Split(',');
                if (parts.Length == 2
                    && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pfStop)
                    && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pfMove))
                {
                    Settings.PatternJitProbeStopFrame = pfStop;
                    Settings.PatternJitProbeT2MoveFrame = pfMove;
                    Console.WriteLine($"PATTERN-JIT MOVE PROBE FRAMES — stop f{pfStop}, T2 move f{pfMove}");
                }
            }
            var probePointArg = args.FirstOrDefault(a => a.StartsWith("--probepoint=", StringComparison.OrdinalIgnoreCase));
            if (probePointArg != null)
            {
                var parts = probePointArg.Substring("--probepoint=".Length).Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pdx)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pdy))
                {
                    Settings.PatternJitProbeDx = pdx;
                    Settings.PatternJitProbeDy = pdy;
                    Console.WriteLine($"PATTERN-JIT MOVE PROBE POINT — SA+({pdx:F3},{pdy:F3})");
                }
            }
            // --sustainpush (owner spec 2026-11-13 12:10): T2 Moves to E1u/
            // S1u of S1's live position every frame of the probe window —
            // sustained mutual push instead of a stop bump. --pushoff=dx,dy
            // overrides the E1u/S1u default.
            if (args.Any(a => string.Equals(a, "--sustainpush", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitSustainPush = true;
                Console.WriteLine($"PATTERN-JIT SUSTAIN PUSH — T2 Move to S1+({Settings.PatternJitPushDx:F2},{Settings.PatternJitPushDy:F2}) each frame f{Settings.PatternJitProbeStopFrame}..f{Settings.PatternJitProbeT2MoveFrame}, TB peel f{Settings.PatternJitProbeT2MoveFrame + 1}");
            }
            var pushOffArg = args.FirstOrDefault(a => a.StartsWith("--pushoff=", StringComparison.OrdinalIgnoreCase));
            if (pushOffArg != null)
            {
                var parts = pushOffArg.Substring("--pushoff=".Length).Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var poffx)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var poffy))
                {
                    Settings.PatternJitPushDx = poffx;
                    Settings.PatternJitPushDy = poffy;
                    Console.WriteLine($"PATTERN-JIT SUSTAIN PUSH OFFSET — S1+({poffx:F3},{poffy:F3})");
                }
            }
            var s1RedirArg = args.FirstOrDefault(a => a.StartsWith("--s1redir=", StringComparison.OrdinalIgnoreCase));
            if (s1RedirArg != null
                && int.TryParse(s1RedirArg.Substring("--s1redir=".Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s1RedirFrame))
            {
                Settings.PatternJitS1RedirectFrame = s1RedirFrame;
                Console.WriteLine($"PATTERN-JIT S1 REDIRECT — probe Move fires f{s1RedirFrame}");
            }
            // --s1stopbump (owner spec 2026-11-13 13:57): one stop bump on
            // S2's return line, then S1 turns NW — SA-side return, then back
            // to SA before S3's flip. --s1bumppos=t sets the intercept
            // fraction from the SB seat toward the SMR (default 0.35).
            if (args.Any(a => string.Equals(a, "--s1stopbump", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitS1StopBump = true;
                Console.WriteLine($"PATTERN-JIT S1 STOP-BUMP — intercept t={Settings.PatternJitS1BumpPos:F2} on S2 return line, NW return, SA arrival vs S3 flip");
            }
            var s1BumpPosArg = args.FirstOrDefault(a => a.StartsWith("--s1bumppos=", StringComparison.OrdinalIgnoreCase));
            if (s1BumpPosArg != null
                && float.TryParse(s1BumpPosArg.Substring("--s1bumppos=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s1BumpPos))
            {
                Settings.PatternJitS1BumpPos = s1BumpPos;
                Console.WriteLine($"PATTERN-JIT S1 STOP-BUMP POS — t={s1BumpPos:F2}");
            }
            if (args.Any(a => string.Equals(a, "--hatchbump", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PatternJitHatchBump = true;
                Console.WriteLine("PATTERN-JIT HATCH BUMP — S1 plants at S2's deposit point; S2 move-thru f(deposit+1), S1 move-thru SA f(deposit+2)");
            }
            var s1StopAtArg = args.FirstOrDefault(a => a.StartsWith("--s1stopat=", StringComparison.OrdinalIgnoreCase));
            if (s1StopAtArg != null
                && int.TryParse(s1StopAtArg.Substring("--s1stopat=".Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s1StopAtFrame))
            {
                Settings.PatternJitS1StopAtFrame = s1StopAtFrame;
                Console.WriteLine($"PATTERN-JIT S1 STOP-AT — STOP S1 f{s1StopAtFrame}, Smart return f{s1StopAtFrame + 1}");
            }

            // --s2sa=N (owner spec 2026-11-11 09:13): S2-on-SA frame battery
            // — S2 Moves to the SA stand at f0, direct Smart on SA at frame N.
            var s2saArg = args.FirstOrDefault(a => a.StartsWith("--s2sa=", StringComparison.OrdinalIgnoreCase));
            if (s2saArg != null
                && int.TryParse(s2saArg.Substring("--s2sa=".Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s2saFrame))
            {
                Settings.PatternJitS2SaSmartFrame = s2saFrame;
                Console.WriteLine($"PATTERN-JIT S2-SA BATTERY — S2 Move to SA stand f0, Smart SA at frame {s2saFrame}");
            }

            // --s1f0=X,Y (owner spec 2026-11-11 07:56): absolute frame-0 Move
            // target for S1 — a pure move probe (pair with --sasmart=-1 so no
            // later Smart overrides the walk; D6 addendum rule 4).
            var s1F0Arg = args.FirstOrDefault(a => a.StartsWith("--s1f0=", StringComparison.OrdinalIgnoreCase));
            if (s1F0Arg != null)
            {
                var parts = s1F0Arg.Substring("--s1f0=".Length).Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s1x)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s1y))
                {
                    Settings.PatternJitS1F0X = s1x;
                    Settings.PatternJitS1F0Y = s1y;
                    Console.WriteLine($"PATTERN-JIT S1 F0 MOVE — target=({s1x:F3},{s1y:F3})");
                }
                else
                {
                    Console.WriteLine($"PATTERN-JIT S1 F0 MOVE REJECTED — could not parse '{s1F0Arg}' (want --s1f0=X,Y)");
                }
            }

            // --s1bumpoff=X (owner spec 2026-11-11 22:24): S1's bump-target
            // east offset from S2's straight-line SB SMR — battery walks it
            // west from +1.0 in 0.25u steps.
            var s1BumpOffArg = args.FirstOrDefault(a => a.StartsWith("--s1bumpoff=", StringComparison.OrdinalIgnoreCase));
            if (s1BumpOffArg != null
                && float.TryParse(s1BumpOffArg.Substring("--s1bumpoff=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s1BumpOff))
            {
                Settings.PatternJitS1BumpOffsetEast = s1BumpOff;
                Console.WriteLine($"PATTERN-JIT S1 BUMP OFFSET — SMR + {s1BumpOff:F2}u east");
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
            // PatternJIT mutates live map data (e.g. the SB stand override) —
            // never persist those harness-only mutations into the cached dat.
            if (Settings.PatternJitMode)
            {
                Console.WriteLine("BabySharkBot: PATTERN-JIT active — skipping BaseDtos dat save so the cached dat stays clean.");
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
                || string.Equals(arg, "--live", StringComparison.OrdinalIgnoreCase))
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
                || string.Equals(arg, "--patternjit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--nocolavoid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--t1trace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--naturaltest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--smsweep", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--smstage", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--smhandoff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--f0test", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--f0gate=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--sbsmh=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--sbsmart=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--sasmh=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--sasmart=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--sasawait=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--t2walk=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--t2bump", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--juggler", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--t2meet=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--t2push=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--s1f0=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--s2sa=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--moveprobe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--s3stopbump", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--minofftest", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--smartprobe=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--thruprobe=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--standpast=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--retprobe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--sbloop", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--probeframes=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--probepoint=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--sustainpush", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--pushoff=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--s1redir=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--s1stopbump", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--hatchbump", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--s1stopat=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--s1bumppos=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--s1bumpoff=", StringComparison.OrdinalIgnoreCase)
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
