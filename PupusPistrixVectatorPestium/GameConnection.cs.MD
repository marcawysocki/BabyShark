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
using Maw.Setup;

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

        // (removed old _workerLabelMap - WorkerLabelService now provides centralized mapping)

        // Centralized worker label service (replaces duplicate labeling code)
        private readonly WorkerLabelService _workerLabelService = new WorkerLabelService();
        // Centralized crosshair service for worker visualization
        private readonly CrosshairService _crosshairService = new CrosshairService();
        // Latest raw units for lookups (used by the service's unit lookup delegate)
        private List<Unit> _lastRawUnits = new List<Unit>();

        // Cache of unit type id -> readable name populated from game data response.
        private readonly Dictionary<uint, string> _unitTypeNames = new Dictionary<uint, string>();

        /// <summary>
        /// Get debug label for a worker tag if available, otherwise returns null.
        /// </summary>
        public string? GetWorkerLabel(ulong tag)
        {
            return _workerLabelService?.GetLabel(tag);
        }

        // Expose the underlying WorkerLabelService so other components can query it directly.
        public WorkerLabelService WorkerLabelService => _workerLabelService;

        // Expose the underlying CrosshairService so other components can query it directly.
        public CrosshairService CrosshairService => _crosshairService;

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

                    // Initialize centralized WorkerLabelService for debug drawing only.
                    try
                    {
                        // Initialize service once; service will use _lastRawUnits for lookups.
                        _workerLabelService.Initialize(Proxy, tag => _lastRawUnits.FirstOrDefault(u => u != null && u.Tag == tag));

                        // Capture initial raw units for service lookups but DO NOT create or assign labels here.
                        _lastRawUnits = observation?.Observation?.RawData?.Units?.Where(u => u != null).ToList() ?? new List<Unit>();
                        // Also update the service's internal raw-units cache so debug prints using the
                        // service fallback can show coordinates even if delegate lookup isn't available yet.
                        try { _workerLabelService.UpdateRawUnits(_lastRawUnits); } catch { }
                    }
                    catch (Exception ex)
                    {
                        LogEvent("WorkerLabelInitFail", details: ex.Message);
                    }

                    // Initialize centralized CrosshairService for worker visualization.
                    try
                    {
                        // Initialize service once; service will use _lastRawUnits for lookups.
                        _crosshairService.Initialize(Proxy, tag => _lastRawUnits.FirstOrDefault(u => u != null && u.Tag == tag));

                        // Update the service's raw-units cache with initial units.
                        try { _crosshairService.UpdateRawUnits(_lastRawUnits); } catch { }
                    }
                    catch (Exception ex)
                    {
                        LogEvent("CrosshairInitFail", details: ex.Message);
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

                // Draw worker labels using WorkerLabelService so drawing logic is centralized.
                try
                {
                    var rawUnits = observation?.Observation?.RawData?.Units;
                    if (rawUnits != null)
                    {
                        // Keep the latest raw units for the service unit lookup delegate
                        _lastRawUnits = rawUnits.Where(u => u != null).ToList();
                        try { _workerLabelService.UpdateRawUnits(_lastRawUnits); } catch { }
                    }

                    var drawReq = _workerLabelService.BuildDebugRequest(_lastRawUnits);
                    if (drawReq != null)
                    {
                        await Proxy.SendRequest(drawReq);
                    }
                }
                catch (Exception ex)
                {
                    LogEvent("WorkerLabelDrawFailed", details: ex.Message);
                }

                // Draw crosshairs on worker units using CrosshairService so drawing logic is centralized.
                try
                {
                    var rawUnits = observation?.Observation?.RawData?.Units;
                    if (rawUnits != null)
                    {
                        // Keep the latest raw units for the service unit lookup delegate
                        _lastRawUnits = rawUnits.Where(u => u != null).ToList();
                        try { _crosshairService.UpdateRawUnits(_lastRawUnits); } catch { }
                    }

                    var drawReq = _crosshairService.BuildDebugRequest(_lastRawUnits);
                    if (drawReq != null)
                    {
                        await Proxy.SendRequest(drawReq);
                    }
                }
                catch (Exception ex)
                {
                    LogEvent("CrosshairDrawFailed", details: ex.Message);
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

        // Label assignment has been moved to WorkerLabelService. No duplicate labeling logic should remain in GameConnection.

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