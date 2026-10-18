using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using Sharky.MicroTasks.Zerg;
using BabySharkBot.Setup;
using BabySharkBot.Managers;
using MemoryPack;
using System;
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

            var mapNameForData = randomMap;
            if (args.Length != 0)
            {
                var ladderArgs = new LadderArgs(args);
                mapNameForData = ladderArgs.MapName;
            }

            Settings.CurrentMapName = mapNameForData ?? string.Empty;
            Settings.SerializeDataLoaded = false;
            Settings.MapDataLoaded = false;

            // Start loading map data as early as possible so it can overlap GameConnection startup.
            MapDataManager.TryLoadMapDataAsync(mapNameForData);

            var gameConnection = new GameConnection();
            var babySharkBot = new AI.BabySharkAI(gameConnection);

            var zergBuildChoices = new AI.ZergBuildChoices(babySharkBot.GetUnderlyingBot());

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

            var myRace = Race.Zerg;
            // Harness-only flags (e.g. --suicide-end) are consumed above and
            // must not reroute the bot into ladder mode — the tuning loop
            // still wants the self-launched single-player game.
            var hasLadderArgs = args.Any(a => !IsHarnessArg(a));
            // Spawn guard: only in self-launched single-player games (on ladder,
            // exiting would forfeit). --no-spawn-guard opts out explicitly.
            Settings.SpawnGuardExitOnNw = !hasLadderArgs
                && !args.Any(a => string.Equals(a, "--no-spawn-guard", StringComparison.OrdinalIgnoreCase));
            if (args.Length == 0 || !hasLadderArgs)
            {
                gameConnection.RunSinglePlayer(sharkyExampleBot, @randomMap, myRace, Race.Zerg, Difficulty.CheatInsane, AIBuild.RandomBuild, botName: botName).Wait();
            }
            else
            {
                gameConnection.RunLadder(sharkyExampleBot, myRace, args, botName: botName).Wait();
            }

            var miningManager = babySharkBot.Managers.OfType<BabySharkMiningManager>().FirstOrDefault();
            WriteBaseDataSnapshot(mapNameForData, miningManager?.CurrentMapData);
        }

        private static bool IsHarnessArg(string arg)
        {
            if (string.IsNullOrEmpty(arg))
            {
                return false;
            }
            if (string.Equals(arg, "--suicide-end", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--no-spawn-guard", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // --bump-test=N / --bump-angle=X (=value form so the value token never
            // leaks into the ladder-args check).
            return arg.StartsWith("--bump-test=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--bump-angle=", StringComparison.OrdinalIgnoreCase);
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
            Settings.EarlySuicideTestEnd = args.Any(a => string.Equals(a, "--suicide-end", StringComparison.OrdinalIgnoreCase));
            if (Settings.EarlySuicideTestEnd)
            {
                Console.WriteLine("EarlySuicideTestEnd ENABLED — at 200 minerals the pool is cancelled and all workers attack our own hatchery");
            }

            // Bump test harness (owner spec 2026-10-18): --bump-test=1|2|3 selects the
            // opening configuration, --bump-angle=degrees rotates the bumper stand point
            // for the angle sweep. Both are single-player harness flags (=value form so
            // the ladder-arg filter above can recognize them).
            var bumpTestArg = args.FirstOrDefault(a => a.StartsWith("--bump-test=", StringComparison.OrdinalIgnoreCase));
            if (bumpTestArg != null
                && int.TryParse(bumpTestArg.Substring("--bump-test=".Length), out var bumpTestMode)
                && bumpTestMode >= 1 && bumpTestMode <= 3)
            {
                Settings.BumpTestMode = bumpTestMode;
                Console.WriteLine($"BumpTestMode ENABLED — mode {bumpTestMode} opening configuration");
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

            var outputPath = Path.Combine(outputDirectory, $"{safeMapName}.Version{Settings.SpeedMiningVersion}.dat");
            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            MemoryPackSerializer.SerializeAsync(outputStream, mapData).GetAwaiter().GetResult();
            Console.WriteLine($"BabySharkBot: wrote BaseDtos MemoryPack data to {outputPath}");
        }
    }
}
