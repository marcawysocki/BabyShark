The following is the legacy code that produced Worker Labels in SC2.  It had the problem that it reinvented labels that did not match the original labels created at startup.
Several attempts were made to go from legacy to the environment we're centralized worker label service  Which do not compile after several attempts to get them to compile they don't produce labels in Starcraft 2
I see also LabelWorker and WorkerLabel which also may be part of the issue od getting the labels drawn in SC2.



SpeedMiningTask.cs

using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Maw.Setup;
using Newtonsoft.Json;
using System.IO;
using Maw.Managers;
using Maw.Services;

namespace Maw.MicroTasks.Mining
{
    public class SpeedMiningTask
    {
        // When non-negative, prefer this start index from gameInfo.StartRaw.StartLocations when selecting start anchor.
        public int PreferredStartId { get; set; } = 0;
        readonly WorkerPatchAssignmentService _assignmentService;
        readonly GameConnection? _gameConnection;
        readonly BaseManager _baseManager;
        readonly IActionService _actionService;

        // Feature flags / tuning (hook into Maw.Settings later)
        // Enable push acceleration by default for Maw lightweight bots so initial pre-split uses primaries/pushers.
        public bool EnablePushAcceleration { get; set; } = true;
        // When true, force a simple pre-split using raw observed workers (useful for testing when label map is missing).
        public bool ForcePreSplitForTesting { get; set; } = true;
        public int DesiredWorkerCount = 12;
        public int PrimaryCount = 8;
        public int PusherCount => Math.Max(0, DesiredWorkerCount - PrimaryCount);

        // Runtime state
        bool _initialized;
        Dictionary<string, ulong> _labelToTag = new Dictionary<string, ulong>();
        StartLocationData _startAssignment = null!;
        string _mapName = "";
        double _anchorX;
        double _anchorY;

        // frame tracking
        int _frame0GameLoop = -1;
        bool _preSplitIssued;
        // no runtime debugger breaks

        public SpeedMiningTask(WorkerPatchAssignmentService assignmentService, GameConnection? gameConnection, BaseManager baseManager, IActionService actionService)
        {
            _assignmentService = assignmentService ?? throw new ArgumentNullException(nameof(assignmentService));
            // GameConnection may not be available at construction time for lightweight Maw bots; accept nullable and defer usage.
            _gameConnection = gameConnection;
            _baseManager = baseManager ?? throw new ArgumentNullException(nameof(baseManager));
            _actionService = actionService ?? throw new ArgumentNullException(nameof(actionService));
        }

        // Called once at OnStart (frame-0). Populate assignments and label->tag map.
        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            try
            {
                Console.WriteLine("[SpeedMiningTask] OnStart");

                // Load per-map assignment (fall back to deterministic assignment inside the service)
                var startLocation = gameInfo?.StartRaw?.StartLocations?.FirstOrDefault();
                // capture map/anchor for potential persistence
                _mapName = gameInfo?.MapName ?? "";
                if (startLocation != null)
                {
                    _anchorX = startLocation.X;
                    _anchorY = startLocation.Y;
                }
                    if (startLocation != null)
                    {
                        // worker assignment service expects anchor coordinates (x,y) and a start id (0)
                        _startAssignment = _assignmentService.GetAssignmentForStart(gameInfo.MapName, startLocation.X, startLocation.Y, 0);
                    }
                else
                {
                    _startAssignment = null;
                }

                // Build label->tag map from initial observation using GameConnection's labels
                _labelToTag.Clear();
                try
                {
                    var rawUnits = observation?.Observation?.RawData?.Units;
                    if (rawUnits != null && _gameConnection != null)
                    {
                        var workers = rawUnits.Where(u => u != null && u.Alliance == Alliance.Self).ToList();
                        foreach (var worker in workers)
                        {
                            var label = _gameConnection.GetWorkerLabel(worker.Tag);
                            if (!string.IsNullOrEmpty(label))
                            {
                                _labelToTag[label] = worker.Tag;
                            }
                        }
                    }
                }
                catch
                {
                    // best-effort only
                }

                _initialized = true;
                _preSplitIssued = false;
                _frame0GameLoop = -1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeedMiningTask] OnStart failed: {ex.Message}");
                _initialized = false;
            }
        }

        // Called every frame. Returns actions to issue this frame.
        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();
            var hasObs = observation?.Observation != null;
            var gameLoop = hasObs ? (int)observation.Observation.GameLoop : -1;
            Console.WriteLine($"[SpeedMiningTask] OnFrame called initialized={_initialized} preSplitIssued={_preSplitIssued} frame={gameLoop}");

            // If we weren't initialized at OnStart, attempt a lightweight initialization from the first available observation.
            if (!_initialized && hasObs)
            {
                try
                {
                    Console.WriteLine("[SpeedMiningTask] Auto-initializing from OnFrame observation");
                    _labelToTag.Clear();
                    var rawUnits = observation.Observation.RawData?.Units;
                    if (rawUnits != null && _gameConnection != null)
                    {
                        var workers = rawUnits.Where(u => u != null && u.Alliance == Alliance.Self).ToList();
                        foreach (var worker in workers)
                        {
                            var label = _gameConnection.GetWorkerLabel(worker.Tag);
                            if (!string.IsNullOrEmpty(label))
                            {
                                _labelToTag[label] = worker.Tag;
                            }
                        }
                        Console.WriteLine($"[SpeedMiningTask] Auto-built label map: {string.Join(',', _labelToTag.Select(kv => kv.Key + ':' + kv.Value))}");
                    }
                    _initialized = true;
                    _frame0GameLoop = gameLoop;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SpeedMiningTask] Auto-initialize failed: {ex.Message}");
                    return actions;
                }
            }

            if (!_initialized || !hasObs) return actions;

            if (_frame0GameLoop == -1) _frame0GameLoop = gameLoop;
            var frameDelta = gameLoop - _frame0GameLoop;

            // Frame-accurate pre-split: issue one-time micro-move orders for selected workers in frames 0..5
            if (!_preSplitIssued && frameDelta <= 5 && (EnablePushAcceleration || ForcePreSplitForTesting))
            {
                // Choose up to DesiredWorkerCount tags from the label map (A..L priority)
                var selected = _labelToTag.Values.Take(DesiredWorkerCount).ToList();

                // Fallback: if label map is empty or yielded no tags, select first self workers from raw observation directly.
                if (selected.Count == 0)
                {
                    try
                    {
                        var rawUnits = observation.Observation.RawData?.Units;
                        if (rawUnits != null)
                        {
                            selected = rawUnits.Where(u => u != null && u.Alliance == Alliance.Self).Select(u => u.Tag).Take(DesiredWorkerCount).ToList();
                            Console.WriteLine($"[SpeedMiningTask] Fallback selected tags from raw units: {string.Join(',', selected)}");
                        }
                    }
                    catch { }
                }
                // Use MOVE for initial pre-split so workers perform a short movement forward.
                const int AbilityMove = 16; // MOVE

                // Pre-split selected tags log for debugging
                Console.WriteLine($"[SpeedMiningTask] Pre-split selected tags: {string.Join(',', selected)}");

                // If no labels were captured at OnStart, attempt to build the label->tag map from this observation (best-effort)
                if (selected.Count == 0)
                {
                    try
                    {
                        var rawUnits = observation?.Observation?.RawData?.Units;
                        if (rawUnits != null)
                        {
                            var workers = rawUnits.Where(u => u != null && u.Alliance == Alliance.Self).OrderBy(u => u.Pos.X).ThenBy(u => u.Pos.Y).ToList();
                            for (int i = 0; i < workers.Count && i < 26; i++)
                            {
                                var label = (char)('A' + i);
                                _labelToTag[label.ToString()] = workers[i].Tag; // FIX: convert char to string
                            }
                            selected = _labelToTag.Values.Take(DesiredWorkerCount).ToList();
                            Console.WriteLine($"[SpeedMiningTask] Rebuilt label map, selected now: {string.Join(',', selected)}");
                        }
                    }
                    catch { }
                }

                for (int idx = 0; idx < selected.Count; idx++)
                {
                    var tag = selected[idx];
                    // find current unit position from observation raw units (best-effort)
                    var unit = observation.Observation.RawData.Units.FirstOrDefault(u => u != null && u.Tag == tag);
                    if (unit == null) continue;

                    Console.WriteLine($"[SpeedMiningTask] Tag {tag} pos=({unit.Pos.X:F2},{unit.Pos.Y:F2}) primary={ (idx<PrimaryCount) }");

                    // Primaries: issue SMART (acts as gather/move) with a small forward offset
                    // Pushers: issue MOVE with a slightly larger offset to close the gap
                    var isPrimary = idx < PrimaryCount;
                    var offsetX = isPrimary ? 0.3f : 0.6f;
                    var target = new Point { X = unit.Pos.X + offsetX, Y = unit.Pos.Y, Z = unit.Pos.Z };

                    var ability = AbilityMove;
                    var action = _actionService.CreateUnitCommand(tag, ability, target.X, target.Y, queue: false);
                    if (action != null)
                    {
                        actions.Add(action);
                        Console.WriteLine($"[SpeedMiningTask] Issued order to tag {tag} ability={ability} target=({target.X:F2},{target.Y:F2})");
                    }
                }

                if (actions.Count > 0)
                {
                    _preSplitIssued = true;
                    Console.WriteLine($"[SpeedMiningTask] Pre-split actions created: {actions.Count}");
                    Console.WriteLine($"[SpeedMiningTask] Action tags: {string.Join(',', actions.Select(a => string.Join('|', a.ActionRaw.UnitCommand.UnitTags)))}");
                    try
                    {
                        // Write a short-lived file so runs without attached consoles/debuggers can be inspected.
                        var path = "speedmining_presplit.log";
                        var details = $"Frame0={_frame0GameLoop}, actions={actions.Count}, tags={string.Join(',', actions.Select(a => string.Join('|', a.ActionRaw.UnitCommand.UnitTags)))}\n";
                        System.IO.File.WriteAllText(path, details);
                    }
                    catch { }
                    // no debugger break here
                }

                // return immediately to avoid flooding with further commands this frame
                return actions;
            }

            // Later frames: will implement primaries/pushers and queued move-back sequences.
            // For now, keep safe no-op and return any pre-split actions already created.
            return actions;
        }

        // Map an assignment label (string) to a unit tag if available.
        ulong? GetTagForLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            if (_labelToTag.TryGetValue(label, out var tag)) return tag;
            return null;
        }

        // Export current label->unit entries to a JSON file for inspection.
        public void WriteLabelsSnapshot(ResponseObservation observation, string path)
        {
            try
            {
                var entries = new List<LabelEntry>();
                var rawUnits = observation?.Observation?.RawData?.Units;
                if (rawUnits != null)
                {
                    var tagToUnit = new Dictionary<ulong, SC2APIProtocol.Unit>(rawUnits.Count);
                    foreach (var u in rawUnits)
                    {
                        if (u == null) continue;
                        tagToUnit[u.Tag] = u;
                    }

                    foreach (var kv in _labelToTag)
                    {
                        var label = kv.Key;
                        var tag = kv.Value;
                        if (tagToUnit.TryGetValue(tag, out var unit) && unit?.Pos != null)
                        {
                            entries.Add(new LabelEntry
                            {
                                X = Math.Round(unit.Pos.X, 3),
                                Y = Math.Round(unit.Pos.Y, 3),
                                UnitType = $"{unit.UnitType}",
                                Label = label
                            });
                        }
                    }

                    // If no entries found, fall back to creating labels from observed workers
                    if (entries.Count == 0)
                    {
                        var workers = rawUnits.Where(u => u != null && u.Alliance == Alliance.Self).ToList();
                        foreach (var worker in workers)
                        {
                            var label = _gameConnection?.GetWorkerLabel(worker.Tag);
                            if (!string.IsNullOrEmpty(label) && worker?.Pos != null)
                            {
                                entries.Add(new LabelEntry
                                {
                                    X = Math.Round(worker.Pos.X, 3),
                                    Y = Math.Round(worker.Pos.Y, 3),
                                    UnitType = $"{worker.UnitType}",
                                    Label = label
                                });
                            }
                        }
                    }
                }

                var json = JsonConvert.SerializeObject(entries, Formatting.Indented);
                File.WriteAllText(path, json);

                // Also persist into the canonical per-map JSON so labels are available for future runs
                try
                {
                    if (!string.IsNullOrEmpty(_mapName))
                    {
                        var start = new StartLocationData
                        {
                            StartId = 0,
                            Anchor = new double[] { Math.Round(_anchorX, 3), Math.Round(_anchorY, 3) },
                            Labels = entries,
                            Assignments = new Dictionary<string, AssignmentEntry>(),
                            ApproachVectors = new Dictionary<string, float[]>(),
                            DropOffPoints = new Dictionary<string, float[]>()
                        };

                        var mapData = new MapSpeedMiningData
                        {
                            MapName = _mapName,
                            SpeedMiningVersion = Maw.Settings.SpeedMiningVersion,
                            StartLocations = new List<StartLocationData> { start },
                            Telemetry = new TelemetryInfo { SampleRuns = 0, LastUpdatedUtc = DateTime.UtcNow.ToString("o") }
                        };

                        _assignmentService.PersistMapSpeedMining(mapData, _mapName, _anchorX, _anchorY);
                        Console.WriteLine($"[SpeedMiningTask] Persisted labels to canonical map file for {_mapName}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SpeedMiningTask] Persist labels snapshot failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeedMiningTask] WriteLabelsSnapshot failed: {ex.Message}");
            }
        }
    }

    static class Extensions
    {
        public static bool IsNullOrEmpty<TKey, TValue>(this IDictionary<TKey, TValue> d)
        {
            return d == null || d.Count == 0;
        }
    }
}



GameConnection.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SC2APIProtocol;

namespace Maw
{
    public class GameConnection
    {
        // Simple shared CSV logger for startup / connection timing
        static readonly Stopwatch LogStopwatch = Stopwatch.StartNew();
        static readonly string LogCsvPath = "startup_timing.csv";
        static readonly object LogLock = new object();

        static GameConnection()
        {
            try
            {
                if (!File.Exists(LogCsvPath))
                {
                    File.WriteAllText(LogCsvPath, "TimestampUtc,MonotonicMs,Event,Frame,DurationMs,Details\n");
                }
            }
            catch
            {
                // Best-effort logging: swallow to avoid affecting runtime behavior.
            }
        }

        static void LogEvent(string ev, int? frame = null, double? durationMs = null, string details = "")
        {
            var timestamp = DateTime.UtcNow.ToString("o");
            var mono = LogStopwatch.ElapsedMilliseconds;
            var frameStr = frame.HasValue ? frame.Value.ToString() : "";
            var durStr = durationMs.HasValue ? durationMs.Value.ToString("F2") : "";
            var safeDetails = (details ?? "").Replace("\"", "\"\"");
            var line = $"{timestamp},{mono},{ev},{frameStr},{durStr},\"{safeDetails}\"";
            try
            {
                lock (LogLock)
                {
                    File.AppendAllText(LogCsvPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // swallow errors from logging to avoid impacting game loop
            }
            Console.WriteLine($"{mono}ms {ev} {details}");
        }

        protected ProtobufProxy Proxy = new ProtobufProxy();
        string address = "127.0.0.1";

        string starcraftExe;
        string starcraftDir;

        private Process _sc2Process;

        // In-memory mining CSV buffer to avoid frequent IO. Written to disk only when
        // (a) first time minerals > 0 is observed (initial file creation), and
        // (b) once at end to flush any remaining buffered rows.
        private StringBuilder _miningBuffer = new StringBuilder();
        private string _miningFilePath;
        private bool _miningFileCreated = false;
        private readonly object _miningLock = new object();
        private int _miningTestId = 0;

        // Worker debug label mapping (A,B,C,... -> unit tag)
        // Populated at frame-0 from the observation snapshot.
        private readonly Dictionary<string, ulong> _workerLabelMap = new Dictionary<string, ulong>();

        // Cache of unit type id -> readable name populated from game data response.
        private readonly Dictionary<uint, string> _unitTypeNames = new Dictionary<uint, string>();

        /// <summary>
        /// Get debug label for a worker tag if available, otherwise returns null.
        /// </summary>
        public string? GetWorkerLabel(ulong tag)
        {
            foreach (var kv in _workerLabelMap)
            {
                if (kv.Value == tag) return kv.Key;
            }
            return null;
        }

        /// <summary>
        /// Initialize in-memory mining CSV buffer and pick an output filename.
        /// No disk IO occurs here except recording path in logs. Call once before run.
        /// </summary>
        public string InitializeMiningBuffer(int testId, string prefix = "mining")
        {
            _miningTestId = testId;
            var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var fileName = $"{prefix}_test_{testId}_{ts}.csv";
            _miningFilePath = Path.GetFullPath(fileName);

            lock (_miningLock)
            {
                _miningBuffer.Clear();
                _miningBuffer.AppendLine("Test,TimestampUTC,GameSeconds,Minerals,Frame");
            }

            LogEvent("MiningBufferInitialized", details: _miningFilePath);
            return _miningFilePath;
        }

        /// <summary>
        /// Append a CSV row to the in-memory buffer.
        /// If minerals > 0 and we haven't created the file yet, create the file by writing
        /// the current buffer to disk once (to capture startup data). Subsequent rows remain buffered
        /// and are flushed at end via FlushMiningToDisk().
        /// </summary>
        public void AppendMiningRowBuffered(double gameSeconds, int minerals, int frame)
        {
            var line = $"{_miningTestId},{DateTime.UtcNow:o},{gameSeconds},{minerals},{frame}";

            lock (_miningLock)
            {
                _miningBuffer.AppendLine(line);

                if (!_miningFileCreated && minerals > 0)
                {
                    try
                    {
                        // One-time write of the accumulated buffer so far (minimizes IO during run).
                        File.WriteAllText(_miningFilePath, _miningBuffer.ToString());
                        _miningBuffer.Clear(); // keep continuing to buffer subsequent rows in memory
                        _miningFileCreated = true;
                        LogEvent("MiningFileCreated", details: _miningFilePath);
                    }
                    catch (Exception ex)
                    {
                        LogEvent("MiningFileCreateFail", details: ex.Message);
                        // swallow - continue buffering in memory (we will try again at flush)
                    }
                }
            }
        }

        /// <summary>
        /// Flush any remaining buffered mining rows to disk. Call once at end of run.
        /// If file was never created, this writes the full buffer at once.
        /// If file was created earlier, append remaining buffered rows in a single operation.
        /// </summary>
        public void FlushMiningToDisk()
        {
            lock (_miningLock)
            {
                try
                {
                    if (string.IsNullOrEmpty(_miningFilePath))
                    {
                        // nothing initialized
                        return;
                    }

                    if (!_miningFileCreated)
                    {
                        // write full buffer at once
                        File.WriteAllText(_miningFilePath, _miningBuffer.ToString());
                        _miningFileCreated = true;
                        LogEvent("MiningFileCreatedOnFlush", details: _miningFilePath);
                    }
                    else if (_miningBuffer.Length > 0)
                    {
                        // append remaining buffered rows in a single write
                        File.AppendAllText(_miningFilePath, _miningBuffer.ToString());
                        LogEvent("MiningFileAppendedOnFlush", details: _miningFilePath);
                    }
                }
                catch (Exception ex)
                {
                    LogEvent("MiningFlushFail", details: ex.Message);
                }
                finally
                {
                    _miningBuffer.Clear();
                }
            }
        }

        public void StartSC2Instance(int port)
        {
            LogEvent("StartSC2Instance", details: $"exe={starcraftExe}, port={port}");

            var exeDirectory = Path.GetDirectoryName(starcraftExe) ?? starcraftDir ?? Environment.CurrentDirectory;
            var processStartInfo = new ProcessStartInfo(starcraftExe)
            {
                Arguments = string.Format("-listen {0} -port {1} -displayMode 0", address, port),
                WorkingDirectory = Path.Combine(starcraftDir ?? exeDirectory, "Support64"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                _sc2Process = Process.Start(processStartInfo);
                if (_sc2Process != null)
                {
                    _sc2Process.EnableRaisingEvents = true;
                    _sc2Process.Exited += (s, e) =>
                    {
                        try { LogEvent("SC2ProcessExited", details: $"ExitCode={_sc2Process.ExitCode}"); } catch { }
                    };

                    _sc2Process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data)) LogEvent("SC2Stdout", details: e.Data);
                    };
                    _sc2Process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data)) LogEvent("SC2Stderr", details: e.Data);
                    };

                    try
                    {
                        _sc2Process.BeginOutputReadLine();
                        _sc2Process.BeginErrorReadLine();
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogEvent("StartSC2InstanceFail", details: ex.Message);
                throw;
            }
        }

        // Add this helper to wait until a TCP connect succeeds (server is listening)
        private async Task WaitForSc2Listening(int port, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using var tcp = new TcpClient();
                    var connectTask = tcp.ConnectAsync(address, port);
                    var finished = await Task.WhenAny(connectTask, Task.Delay(500)).ConfigureAwait(false);
                    if (finished == connectTask && tcp.Connected)
                    {
                        return;
                    }
                }
                catch
                {
                    // ignore and retry
                }

                if (_sc2Process?.HasExited == true)
                {
                    throw new Exception($"SC2 exited while waiting for listen (ExitCode={_sc2Process.ExitCode})");
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for SC2 to open websocket port.");
        }

        // Replace Connect to probe for listening before first attempt
        public async Task Connect(int port, string address = "127.0.0.1")
        {
            LogEvent("ConnectStart", details: $"{address}:{port}");
            var overall = Stopwatch.StartNew();

            // If we started SC2 in this process, wait briefly for it to listen before trying the first connect.
            if (_sc2Process != null)
            {
                try
                {
                    await WaitForSc2Listening(port, 8000).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Log and continue to the regular retry loop; Proxy.Connect will still try and fail if needed.
                    LogEvent("WaitForListenFailed", details: ex.Message);
                }
            }

            for (int i = 0; i < 40; i++)
            {
                var attemptSw = Stopwatch.StartNew();
                try
                {
                    LogEvent("ConnectAttempt", details: $"attempt={i+1}");
                    await Proxy.Connect(address, port).ConfigureAwait(false);
                    attemptSw.Stop();
                    overall.Stop();
                    LogEvent("ConnectSuccess", durationMs: attemptSw.Elapsed.TotalMilliseconds, details: $"{address}:{port}");
                    LogEvent("ConnectOverall", durationMs: overall.Elapsed.TotalMilliseconds, details: $"{address}:{port}");

                    // Wait for proxy readiness (Ping)
                    try
                    {
                        // Reuse existing Ping call; if your Proxy has a separate Wait call, use it.
                        await Task.Delay(50).ConfigureAwait(false); // tiny breathing room after connect
                    }
                    catch (Exception ex)
                    {
                        LogEvent("ProxyNotReady", details: ex.Message);
                        try { Proxy.Dispose(); } catch { }
                        Proxy = new ProtobufProxy();
                        throw;
                    }

                    return;
                }
                catch (WebSocketException wsex)
                {
                    attemptSw.Stop();
                    LogEvent("ConnectAttemptFailed", durationMs: attemptSw.Elapsed.TotalMilliseconds, details: $"attempt={i+1}, ex={wsex.Message}");
                }
                catch (Exception ex)
                {
                    attemptSw.Stop();
                    LogEvent("ConnectAttemptFailed", durationMs: attemptSw.Elapsed.TotalMilliseconds, details: $"attempt={i+1}, ex={ex.Message}");
                }
                await Task.Delay(2000).ConfigureAwait(false);
            }

            LogEvent("ConnectFailed", details: $"{address}:{port}");
            throw new Exception("Unable to make a connection.");
        }

        public async Task CreateGame(String mapName, Race opponentRace, Difficulty opponentDifficulty, AIBuild aIBuild, int randomSeed = -1, bool realTime = false, string botName = "")
        {
            LogEvent("CreateGameStart", details: $"map={mapName}, opponentRace={opponentRace}, difficulty={opponentDifficulty}");
            var sw = Stopwatch.StartNew();

            var createGame = new RequestCreateGame();
            createGame.Realtime = realTime;

            if (randomSeed >= 0)
            {
                createGame.RandomSeed = (uint)randomSeed;
            }

            string mapPath = Path.Combine(starcraftDir, "Maps", mapName);
            if (!File.Exists(mapPath))
            {
                LogEvent("CreateGameFail", details: $"map not found {mapPath}");
                throw new Exception("Could not find map at " + mapPath);
            }
            createGame.LocalMap = new LocalMap();
            createGame.LocalMap.MapPath = mapPath;

            var player1 = new PlayerSetup();
            if (!string.IsNullOrEmpty(botName))
            {
                player1.PlayerName = botName;
            }
            createGame.PlayerSetup.Add(player1);
            player1.Type = PlayerType.Participant;

            var player2 = new PlayerSetup();
            createGame.PlayerSetup.Add(player2);
            player2.Race = opponentRace;
            player2.Type = PlayerType.Computer;
            player2.Difficulty = opponentDifficulty;
            player2.AiBuild = aIBuild;

            var request = new Request();
            request.CreateGame = createGame;
            var response = await Proxy.SendRequest(request);

            sw.Stop();
            LogEvent("CreateGameEnd", durationMs: sw.Elapsed.TotalMilliseconds, details: $"map={mapName}");
        }

        protected void readSettings()
        {
            LogEvent("ReadSettingsStart");
            var myDocuments = Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
            var executeInfo = Path.Combine(myDocuments, "Starcraft II", "ExecuteInfo.txt");
            if (File.Exists(executeInfo))
            {
                var lines = File.ReadAllLines(executeInfo);
                foreach (string line in lines)
                {
                    var argument = line.Substring(line.IndexOf('=') + 1).Trim();
                    if (line.Trim().StartsWith("executable"))
                    {
                        starcraftExe = argument;
                        starcraftDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(starcraftExe)));
                    }
                }
                LogEvent("ReadSettingsEnd", details: $"exe={starcraftExe}");
            }
            else
            {
                LogEvent("ReadSettingsFail", details: $"path={executeInfo}");
                throw new Exception("Unable to find ExecuteInfo.txt at " + executeInfo);
            }
        }

        public async Task<uint> JoinGame(Race race)
        {
            LogEvent("JoinGameStart", details: $"race={race}");
            var sw = Stopwatch.StartNew();

            var joinGame = new RequestJoinGame();
            joinGame.Race = race;

            joinGame.Options = new InterfaceOptions();
            joinGame.Options.FeatureLayer = new SpatialCameraSetup { CropToPlayableArea = true, AllowCheatingLayers = false, MinimapResolution = new Size2DI { X = 16, Y = 16 }, Resolution = new Size2DI { X = 128, Y = 128 }, Width = 10 };
            joinGame.Options.Raw = true;
            joinGame.Options.Score = true;
            joinGame.Options.ShowCloaked = true;
            joinGame.Options.ShowBurrowedShadows = true;
            joinGame.Options.RawCropToPlayableArea = true;
            joinGame.Options.RawAffectsSelection = true;

            var request = new Request();
            request.JoinGame = joinGame;
            var response = await Proxy.SendRequest(request);

            sw.Stop();
            LogEvent("JoinGameEnd", durationMs: sw.Elapsed.TotalMilliseconds, details: $"playerId={(response?.JoinGame?.PlayerId.ToString() ?? "null")}");
            return response.JoinGame.PlayerId;
        }

        public async Task<uint> JoinGameLadder(Race race, int startPort, string botName = "", string address = "127.0.0.1")
        {
            LogEvent("JoinGameLadderStart", details: $"race={race}, startPort={startPort}, botName={botName}, address={address}");
            var sw = Stopwatch.StartNew();

            var joinGame = new RequestJoinGame();
            joinGame.Race = race;
            joinGame.PlayerName = botName;
            joinGame.HostIp = address;

            joinGame.SharedPort = startPort + 1;
            joinGame.ServerPorts = new PortSet();
            joinGame.ServerPorts.GamePort = startPort + 2;
            joinGame.ServerPorts.BasePort = startPort + 3;

            joinGame.ClientPorts.Add(new PortSet());
            joinGame.ClientPorts[0].GamePort = startPort + 4;
            joinGame.ClientPorts[0].BasePort = startPort + 5;

            joinGame.Options = new InterfaceOptions();
            joinGame.Options.FeatureLayer = new SpatialCameraSetup { CropToPlayableArea = true, AllowCheatingLayers = false, MinimapResolution = new Size2DI { X = 16, Y = 16 }, Resolution = new Size2DI { X = 128, Y = 128 }, Width = 10 };
            joinGame.Options.Raw = true;
            joinGame.Options.Score = true;
            joinGame.Options.ShowCloaked = true;
            joinGame.Options.ShowBurrowedShadows = true;
            joinGame.Options.RawCropToPlayableArea = true;
            joinGame.Options.RawAffectsSelection = true;

            var request = new Request();
            request.JoinGame = joinGame;

            Console.WriteLine($"Proxy join request to {joinGame.HostIp}:{startPort} to {startPort+5}");

            var response = await Proxy.SendRequest(request);

            sw.Stop();
            LogEvent("JoinGameLadderEnd", durationMs: sw.Elapsed.TotalMilliseconds, details: $"playerId={(response?.JoinGame?.PlayerId.ToString() ?? "null")}");
            return response.JoinGame.PlayerId;
        }

        public async Task<ResponsePing> Ping()
        {
            LogEvent("PingStart");
            var sw = Stopwatch.StartNew();

            var request = new Request();
            request.Ping = new RequestPing();
            var response = await Proxy.SendRequest(request);

            sw.Stop();
            LogEvent("PingEnd", durationMs: sw.Elapsed.TotalMilliseconds);
            return response.Ping;
        }

        public async Task RequestLeaveGame()
        {
            LogEvent("RequestLeaveGame");
            await Proxy.SendRequest(new Request { LeaveGame = new RequestLeaveGame() });
        }

        public async Task SendRequest(Request request)
        {
            LogEvent("SendRequest", details: request?.GetType().Name);
            await Proxy.SendRequest(request);
        }

        public async Task<ResponseQuery> SendQuery(RequestQuery query)
        {
            LogEvent("SendQueryStart");
            var response = await Proxy.SendRequest(new Request { Query = query });
            LogEvent("SendQueryEnd");
            return response.Query;
        }

        // Maw-native Run overloads that accept IBabySharkBot (Maw bootstrap)
        public async Task Run(IBabySharkBot bot, uint playerId, string opponentID, string botName = "")
        {
            LogEvent("RunStart", details: $"playerId={playerId}, opponentID={opponentID}");
            var gameInfoReq = new Request();
            gameInfoReq.GameInfo = new RequestGameInfo();

            var swGameInfo = Stopwatch.StartNew();
            var gameInfoResponse = await Proxy.SendRequest(gameInfoReq);
            swGameInfo.Stop();
            LogEvent("GameInfoReceived", durationMs: swGameInfo.Elapsed.TotalMilliseconds);

            var gameDataRequest = new Request();
            gameDataRequest.Data = new RequestData();
            gameDataRequest.Data.UnitTypeId = true;
            gameDataRequest.Data.AbilityId = true;
            gameDataRequest.Data.BuffId = true;
            gameDataRequest.Data.EffectId = true;
            gameDataRequest.Data.UpgradeId = true;

            var swData = Stopwatch.StartNew();
            var dataResponse = await Proxy.SendRequest(gameDataRequest);

            // Populate unit-type name cache from data response (safe, no external deps)
            _unitTypeNames.Clear();
            try
            {
                if (dataResponse?.Data?.Units != null)
                {
                    foreach (var ut in dataResponse.Data.Units)
                    {
                        _unitTypeNames[(uint)ut.UnitId] = string.IsNullOrEmpty(ut.Name) ? $"Unit_{ut.UnitId}" : ut.Name;
                    }
                }
            }
            catch
            {
                // best-effort; fallbacks used in LabelWorkers
            }

            swData.Stop();
            LogEvent("GameDataReceived", durationMs: swData.Elapsed.TotalMilliseconds);

            var swPing = Stopwatch.StartNew();
            var pingResponse = await Ping();
            swPing.Stop();
            LogEvent("PingReceived", durationMs: swPing.Elapsed.TotalMilliseconds);

            var start = true;

            var observationRequest = new Request
            {
                Observation = new RequestObservation()
            };

            var stepRequest = new Request
            {
                Step = new RequestStep { Count = 1 }
            };

            double totalTime = 0;
            int frames = 0;

            double specificTime = 0;
            double longestFrameTime = 0;
            int actionCount = 0;
            // internal debug flag to break before sending the first step after pre-split actions
            bool _preSplitStepBreak = false;

            while (true)
            {
                var beginTotal = Stopwatch.GetTimestamp();

                if (!start)
                {
                    // If a pre-split summary file exists, break here before sending the step so you can inspect units
                    try
                    {
                        if (!_preSplitStepBreak && System.IO.File.Exists("speedmining_presplit.log"))
                        {
                            _preSplitStepBreak = true;
                            Console.WriteLine("[Run] Detected speedmining_presplit.log; breaking before Step so you can inspect unit state.");
                            try { if (!Debugger.IsAttached) Debugger.Launch(); } catch { }
                            try { Debugger.Break(); } catch { }
                        }
                    }
                    catch { }

                    await Proxy.SendRequest(stepRequest);
                }
                var begin = Stopwatch.GetTimestamp();
                var response = await Proxy.SendRequest(observationRequest);

                specificTime += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency * 1000.0;

                var observation = response.Observation;

                if (observation == null)
                {
                    LogEvent("ObservationNull");
                    Console.WriteLine($"Ended at {DateTime.UtcNow} UTC");
                    Console.WriteLine($"total actions: {actionCount}");
                    bot.OnEnd(observation, Result.Undecided);
                    break;
                }
                if (response.Status == Status.Ended || response.Status == Status.Quit)
                {
                    LogEvent("RunEndedByStatus", details: response.Status.ToString());
                    Console.WriteLine($"Ended at {DateTime.UtcNow} UTC");
                    Console.WriteLine($"total actions: {actionCount}, APM: {Math.Round(actionCount / (frames / (22.4 * 60)))}");

                    bot.OnEnd(observation, observation.PlayerResult[(int)playerId - 1].Result);
                    break;
                }

                if (start)
                {
                    foreach (var playerInfo in gameInfoResponse.GameInfo.PlayerInfo)
                    {
                        if (string.IsNullOrEmpty(playerInfo.PlayerName))
                        {
                            if (playerInfo.PlayerId == playerId)
                            {
                                // no-op
                            }
                            else
                            {
                                // no-op
                            }
                        }
                    }
                    LogEvent("OnStartCalling");
                    Console.WriteLine($"Started at {DateTime.UtcNow} UTC");

                    // Label workers A..L for debug visibility in logs during the run
                    try
                    {
                        LabelWorkers(observation, gameInfoResponse?.GameInfo);
                    }
                    catch (Exception ex)
                    {
                        LogEvent("WorkerLabelFail", details: ex.Message);
                    }

                    start = false;
                    var swOnStart = Stopwatch.StartNew();
                    bot.OnStart(gameInfoResponse.GameInfo, dataResponse.Data, pingResponse, observation, playerId, opponentID);
                    swOnStart.Stop();
                    LogEvent("OnStartComplete", frame: (int)observation.Observation.GameLoop, durationMs: swOnStart.Elapsed.TotalMilliseconds);
                }

                var actions = bot.OnFrame(observation);

                var generatedActions = actions.Count();
                try
                {
                    Console.WriteLine($"[Run] Bot generated {generatedActions} raw actions on frame {observation.Observation.GameLoop}");
                }
                catch { }

                // Diagnostic: log any actions that reference unit tags not present in this observation
                try
                {
                    var rawTags = observation.Observation.RawData.Units?.Where(u => u != null).Select(u => u.Tag).ToHashSet() ?? new HashSet<ulong>();
                    foreach (var act in actions)
                    {
                        var cmd = act?.ActionRaw?.UnitCommand;
                        if (cmd?.UnitTags != null)
                        {
                            var missing = cmd.UnitTags.Where(t => !rawTags.Contains(t)).ToList();
                            if (missing.Any())
                            {
                                Console.WriteLine($"[Run] Dropping action ability={cmd.AbilityId} because missing tags: {string.Join(',', missing)}");
                            }
                        }
                    }
                }
                catch { }

                actions = actions.Where(action => action?.ActionRaw?.UnitCommand?.UnitTags == null ||
                    (action?.ActionRaw?.UnitCommand?.UnitTags != null &&
                    !action.ActionRaw.UnitCommand.UnitTags.Any(tag => !observation.Observation.RawData.Units.Any(u => u.Tag == tag))));

                var filteredActions = new List<SC2APIProtocol.Action>();
                var tags = new List<ulong>();
                foreach (var action in actions)
                {
                    if (action?.ActionRaw?.UnitCommand?.UnitTags != null && !action.ActionRaw.UnitCommand.QueueCommand)
                    {
                        if (!tags.Any(tag => action.ActionRaw.UnitCommand.UnitTags.Any(t => t == tag)))
                        {
                            filteredActions.Add(action);
                            tags.AddRange(action.ActionRaw.UnitCommand.UnitTags);
                        }
                    }
                    else
                    {
                        filteredActions.Add(action);
                    }
                }

                var actionRequest = new Request();
                actionRequest.Action = new RequestAction();
                actionRequest.Action.Actions.AddRange(filteredActions);

                if (actionRequest.Action.Actions.Count > 0)
                {
                    // If these actions include worker move/gather orders, break here so you can inspect units just before they move.
                    bool hasWorkerMove = false;
                    try
                    {
                        var interestingAbilities = new int[] { 1, 16, 3666, 1183 }; // SMART, MOVE, HARVEST_GATHER, HARVEST_GATHER_DRONE
                        hasWorkerMove = actionRequest.Action.Actions.Any(a => a?.ActionRaw?.UnitCommand != null && interestingAbilities.Contains(a.ActionRaw.UnitCommand.AbilityId));
                        if (hasWorkerMove)
                        {
                            Console.WriteLine($"[Run] About to send worker move/gather actions (count={actionRequest.Action.Actions.Count}) on frame {observation.Observation.GameLoop}");
                        }
                    }
                    catch { }

                    var swSend = Stopwatch.StartNew();
                    await Proxy.SendRequest(actionRequest);
                    swSend.Stop();
                    actionCount += actionRequest.Action.Actions.Count;
                    LogEvent("ActionsSent", durationMs: swSend.Elapsed.TotalMilliseconds, details: $"count={actionRequest.Action.Actions.Count}");

                    // If we just sent worker move/gather actions, break here before sending the Step that applies them
                    try
                    {
                        if (hasWorkerMove && !_preSplitStepBreak)
                        {
                            _preSplitStepBreak = true; // ensure we only do this once
                            Console.WriteLine($"[Run] Sending immediate Step to apply worker actions on frame {observation.Observation.GameLoop}");
                            var swStep = Stopwatch.StartNew();
                            await Proxy.SendRequest(stepRequest);
                            swStep.Stop();
                            LogEvent("ImmediateStepSent", durationMs: swStep.Elapsed.TotalMilliseconds, details: $"frame={observation.Observation.GameLoop}");
                        }
                    }
                    catch { }
                }

                // If the bot provides debug draw/spawn requests (Sharky-style), send them so debug overlay appears in SC2.
                try
                {
                    if (bot is IDebugProvider debugProvider)
                    {
                        var drawReq = debugProvider.GetDebugDrawRequest();
                        if (drawReq != null)
                        {
                            await Proxy.SendRequest(drawReq);
                        }

                        var spawnReq = debugProvider.GetDebugSpawnRequest();
                        if (spawnReq != null)
                        {
                            await Proxy.SendRequest(spawnReq);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogEvent("DebugRequestsFailed", details: ex.Message);
                }

                // Draw worker labels (A,B,C,...) over each mapped worker's world position so they appear in-game.
                try
                {
                    var rawUnits = observation?.Observation?.RawData?.Units;
                    if (rawUnits != null && _workerLabelMap.Count > 0)
                    {
                        var labelReq = new Request();
                        labelReq.Debug = new RequestDebug();
                        var labelCmd = new DebugCommand();
                        labelCmd.Draw = new DebugDraw();

                        foreach (var kv in _workerLabelMap)
                        {
                            var label = kv.Key;
                            var tag = kv.Value;
                            var unit = rawUnits.FirstOrDefault(u => u != null && u.Tag == tag);
                            if (unit != null && unit.Pos != null)
                            {
                                var z = (unit.Pos.Z > 0) ? unit.Pos.Z + 1 : 12; // place above ground
                                var text = new DebugText
                                {
                                    Text = label,
                                    Size = 14,
                                    Color = new Color { R = 255, G = 255, B = 0 },
                                    WorldPos = new Point { X = unit.Pos.X, Y = unit.Pos.Y, Z = z }
                                };
                                labelCmd.Draw.Text.Add(text);
                            }
                        }

                        if (labelCmd.Draw.Text.Count > 0)
                        {
                            labelReq.Debug.Debug.Add(labelCmd);
                            await Proxy.SendRequest(labelReq);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogEvent("WorkerLabelDrawFailed", details: ex.Message);
                }

                var frameTotal = (Stopwatch.GetTimestamp() - beginTotal) / (double)Stopwatch.Frequency * 1000.0;
                totalTime += frameTotal;
                frames++;
                if (frames > 1 && frameTotal > longestFrameTime)
                {
                    longestFrameTime = frameTotal;
                    Console.WriteLine($"Longest Frame: #{frames}: {longestFrameTime:F2}ms, average: {(totalTime / frames):F2}ms");
                }
            }

            LogEvent("RunEnd", details: $"totalFrames={frames}, totalActions={actionCount}");
        }

        public async Task RunSinglePlayer(IBabySharkBot bot, string map, Race myRace, Race opponentRace, Difficulty opponentDifficulty, AIBuild aIBuild, int randomSeed = -1, string opponentID = "test", bool realTime = false, string botName = "bot")
        {
            LogEvent("RunSinglePlayerStart", details: $"map={map}");
            readSettings();
            StartSC2Instance(5678);
            await Connect(5678);
            await CreateGame(map, opponentRace, opponentDifficulty, aIBuild, randomSeed, realTime);
            var playerId = await JoinGame(myRace);
            await Run(bot, playerId, opponentID, botName);
            // flush remaining mining rows to disk once run completes
            FlushMiningToDisk();
            LogEvent("RunSinglePlayerEnd");
        }

        /// <summary>
        /// Inspect the raw observation and create a deterministic A..L label mapping for self workers.
        /// Outputs labels to console and startup_timing.csv via LogEvent so you can view them during a run.
        /// This is intentionally conservative (no in-game draw) and safe to call on frame-0.
        /// </summary>
        // Label workers. If gameInfo is available we can use StartRaw.StartLocations to determine orientation deterministically.
        private void LabelWorkers(ResponseObservation responseObservation, ResponseGameInfo? gameInfo = null)
        {
            // Unit type IDs as per project instructions
            const uint ZERG_DRONE = 104;
            const uint ZERG_HATCHERY = 86;
            const uint ZERG_OVERLORD = 106;
            const uint ZERG_LARVA = 151;

            _workerLabelMap.Clear();
            if (responseObservation?.Observation?.RawData?.Units == null) return;

            var allUnits = responseObservation.Observation.RawData.Units.Where(u => u != null).ToList();
            var selfUnits = allUnits.Where(u => u.Alliance == Alliance.Self).ToList();

            // Counters for each type
            int droneCount = 1, hatcheryCount = 1, overlordCount = 1, larvaCount = 1;

            foreach (var unit in selfUnits)
            {
                string label = null;
                if (unit.UnitType == ZERG_DRONE)
                {
                    label = $"D{droneCount++}";
                }
                else if (unit.UnitType == ZERG_HATCHERY)
                {
                    label = $"H{hatcheryCount++}";
                }
                else if (unit.UnitType == ZERG_OVERLORD)
                {
                    label = $"OV{overlordCount++}";
                }
                else if (unit.UnitType == ZERG_LARVA)
                {
                    label = $"L{larvaCount++}";
                }

                if (label != null)
                {
                    // Use only the first char as key for _workerLabelMap for compatibility with GetWorkerLabel
                    // (if you want to support full string labels, change _workerLabelMap to Dictionary<string, ulong>)
                    // For now, use the full label as key (change type to Dictionary<string, ulong>)
                    _workerLabelMap[label] = unit.Tag;

                    string typeName;
                    if (!_unitTypeNames.TryGetValue(unit.UnitType, out typeName))
                    {
                        if (unit.UnitType == ZERG_DRONE) typeName = "ZERG_DRONE";
                        else if (unit.UnitType == ZERG_HATCHERY) typeName = "ZERG_HATCHERY";
                        else if (unit.UnitType == ZERG_LARVA) typeName = "ZERG_LARVA";
                        else if (unit.UnitType == ZERG_OVERLORD) typeName = "ZERG_OVERLORD";
                        else typeName = $"Type_{unit.UnitType}";
                    }

                    var details = $"{label}: tag={unit.Tag}, type={typeName}, pos={unit.Pos.X:F2},{unit.Pos.Y:F2}";
                    LogEvent("WorkerLabel", frame: (int)responseObservation.Observation.GameLoop, details: details);
                    Console.WriteLine(details);
                }
            }

            if (!selfUnits.Any(u =>
                u.UnitType == ZERG_DRONE ||
                u.UnitType == ZERG_HATCHERY ||
                u.UnitType == ZERG_OVERLORD ||
                u.UnitType == ZERG_LARVA))
            {
                LogEvent("WorkerLabelNone", frame: (int?)responseObservation.Observation.GameLoop, details: "No self workers found in raw units.");
            }
        }

        // Public overload to accept ladder args array (called from Program.cs)
        public async Task RunLadder(IBabySharkBot bot, Race myRace, string[] args, string botName = "")
        {
            var clargs = new CLArgs(args);
            // Call the full overload including address (use default ladder server if not present)
            await RunLadder(bot, myRace, clargs.GamePort, clargs.StartPort, clargs.OpponentID, botName, clargs.LadderServer ?? "127.0.0.1").ConfigureAwait(false);
        }

        // Public helper used by Program.cs to append raw CSV lines to the mining buffer.
        // Keeps behavior consistent with the in-memory buffering strategy.
        public void AppendMiningRow(string csvLine)
        {
            if (string.IsNullOrWhiteSpace(csvLine))
            {
                return;
            }

            // Try to parse the conventional CSV layout: Test,TimestampUTC,GameSeconds,Minerals,Frame
            try
            {
                var parts = csvLine.Split(',');
                if (parts.Length >= 5)
                {
                    // parts[2] -> GameSeconds, parts[3] -> Minerals, parts[4] -> Frame
                    if (double.TryParse(parts[2], out var gameSeconds) &&
                        int.TryParse(parts[3], out var minerals) &&
                        int.TryParse(parts[4], out var frame))
                    {
                        AppendMiningRowBuffered(gameSeconds, minerals, frame);
                        return;
                    }
                }
            }
            catch
            {
                // fallthrough to raw append
            }

            // Fallback: append the raw CSV line into the in-memory buffer (preserves data)
            lock (_miningLock)
            {
                _miningBuffer.AppendLine(csvLine);
            }
        }

        // Run ladder given explicit ports/address — perform connect, join and run loop.
        public async Task RunLadder(IBabySharkBot bot, Race myRace, int gamePort, int startPort, string opponentID, string botName = "", string address = "127.0.0.1")
        {
            LogEvent("RunLadderStart", details: $"address={address}, gamePort={gamePort}");
            Console.WriteLine($"Connecting to {address}:{gamePort}");

            await Connect(gamePort, address).ConfigureAwait(false);
            var playerId = await JoinGameLadder(myRace, startPort, botName, address).ConfigureAwait(false);
            await Run(bot, playerId, opponentID, botName).ConfigureAwait(false);

            // flush mining rows if used
            FlushMiningToDisk();
            LogEvent("RunLadderEnd");
        }
    }
}

