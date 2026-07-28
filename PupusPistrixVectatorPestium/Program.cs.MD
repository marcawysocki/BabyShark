using SC2APIProtocol;
using Maw;
using Maw.BabySharkBot;
using System;
using System.Diagnostics;
using System.IO;
using PupusPistrixVectatorPestiumBot;

Console.WriteLine("Starting PupusPistrixVectatorPestium, Latin for Baby Shark, Bringer of Plagues");

// Lightweight CSV timing instrumentation
var startupStopwatch = Stopwatch.StartNew();
var csvPath = "startup_timing.csv";
if (!File.Exists(csvPath))
{
    File.WriteAllText(csvPath, "TimestampUtc,MonotonicMs,Event,Frame,DurationMs,Details\n");
}
void LogEvent(string ev, int? frame = null, double? durationMs = null, string details = "")
{
    var timestamp = DateTime.UtcNow.ToString("o");
    var mono = startupStopwatch.ElapsedMilliseconds;
    var frameStr = frame.HasValue ? frame.Value.ToString() : "";
    var durStr = durationMs.HasValue ? durationMs.Value.ToString("F2") : "";
    var safeDetails = details?.Replace("\"", "\"\"");
    var line = $"{timestamp},{mono},{ev},{frameStr},{durStr},\"{safeDetails}\"";
    try
    {
        File.AppendAllText(csvPath, line + Environment.NewLine);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to write log: {ex.Message}");
    }
    Console.WriteLine($"{mono}ms {ev} {details}");
}

LogEvent("ProgramStart");

// Diagnostic: verify MemoryPack runtime assembly presence
try
{
    var mpCorePath = Path.Combine(AppContext.BaseDirectory, "MemoryPack.Core.dll");
    LogEvent("MemoryPackCheck", details: $"exists={File.Exists(mpCorePath)}, path={mpCorePath}");
    var asm = System.Reflection.Assembly.LoadFrom(mpCorePath);
    LogEvent("MemoryPackLoaded", details: asm.FullName);
}
catch (Exception ex)
{
    LogEvent("MemoryPackLoadFailed", details: ex.Message);
}

// fully-qualify to disambiguate between Maw.GameConnection and Sharky.GameConnection
var gameConnection = new Maw.GameConnection();
LogEvent("GameConnectionCreated");

// initialize mining buffer before starting the run so buffering is ready
var miningPath = gameConnection.InitializeMiningBuffer(3, "mining");
Console.WriteLine("Mining CSV will be: " + miningPath);

var BabySharkBot  = new BabySharkBot (gameConnection);
LogEvent("BabySharkBot Constructed");

//var zergBuildChoices = new ZergBuildChoices(BabySharkBot );
//BabySharkBot.BuildChoices[Race.Zerg] = zergBuildChoices.BuildChoices;
//LogEvent("BuildChoicesRegistered", details: $"Zerg builds registered: {zergBuildChoices.BuildChoices.Builds?.Count ?? 0}");

var pupusPistrixVectatorPestium = BabySharkBot;
LogEvent("BotInitialized");

var maps = new[]
{
    "AbyssalReefAIE.SC2Map",
    "AcropolisAIE.SC2Map",
    "AutomatonAIE.SC2Map",
    "EphemeronAIE.SC2Map",
    "IncorporealAIE_v4.SC2Map",
    "InterloperAIE.SC2Map",
    "LastFantasyAIE.SC2Map",
    "LeyLinesAIE_v3.SC2Map",
    "MagannathaAIE_v2.SC2Map",
    "PersephoneAIE_v4.SC2Map",
    "PylonAIE_v4.SC2Map",
    "ThunderbirdAIE.SC2Map",
    "TorchesAIE_v4.SC2Map",
    "UltraloveAIE_v2.SC2Map"
};

//3 player maps "LastFantasyAIE.SC2Map","Catallena","Elysium","ICCupTestbugL","MerryGoRound","Mothership","Sanctuary","Sylphid","XelNagaFortress"


// Pick a random map from your list
var random = new Random();
var randomMap = maps[random.Next(maps.Length)];



var myRace = Race.Zerg;
if (args.Length == 0)
{
    LogEvent("RunSinglePlayerStart", details: "Single player");
    //gameConnection.RunSinglePlayer(pupusPistrixVectatorPestium, @"MagannathaAIE_v2.SC2Map", myRace, Race.Random, Difficulty.CheatInsane, AIBuild.RandomBuild).Wait();
    gameConnection.RunSinglePlayer(pupusPistrixVectatorPestium, @randomMap, myRace, Race.Random, Difficulty.CheatInsane, AIBuild.RandomBuild).Wait();
    //gameConnection.RunSinglePlayer(pupusPistrixVectatorPestium, @"LastFantasyAIE.SC2Map", myRace, Race.Random, Difficulty.CheatInsane, AIBuild.RandomBuild).Wait();

    LogEvent("RunSinglePlayerEnd");
}
else
{
    LogEvent("RunLadderStart", details: $"Args: {string.Join(" ", args)}");
    gameConnection.RunLadder(pupusPistrixVectatorPestium, myRace, args).Wait();
    LogEvent("RunLadderEnd");
}

LogEvent("ProgramEnd", details: $"Total startup ms: {startupStopwatch.ElapsedMilliseconds}");

// after creating gameConnection
// sample append usage (replace with actual data points from your bot loop)
gameConnection.AppendMiningRow($"3,{DateTime.UtcNow:o},0,50,0");

// This method is just for illustration and won't do anything functional
void BotLoopSimulation()
{
    // Replace with the actual logic to collect data for mining
    for (int i = 0; i < 10; i++)
    {
        // Simulate some data
        int frame = i * 10;
        double someValue = Math.Sin(i) * 100;

        // Append a row to the mining log
        gameConnection.AppendMiningRow($"3,{DateTime.UtcNow:o},{frame},50,{someValue}");
    }
}

// Uncomment this to run the simulation (though it doesn't do anything useful)
// BotLoopSimulation();

