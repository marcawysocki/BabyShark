using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.Builds;
using Sharky.DefaultBot;
using BabySharkBot.Services;
using BabySharkBot.Setup;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// 12-pool opening: race to 200 minerals for the Spawning Pool.
    /// - Larva NEVER morphs into drones beyond the 12th (on a standard 12-drone start
    ///   that means zero morphing; on an 8-drone start it tops up to 12).
    /// - Once minerals bank to 200, the pool is ordered at the SpawningPoolPlacement
    ///   mini-wall spot. The build command is re-issued to the same worker every frame
    ///   until the pool is observed under construction (so an overwritten order is
    ///   simply re-issued next frame), then the race is done.
    /// - Keeps the BuildIne mineral-rate CSV (mining_test_12_*.csv) so the race to 200
    ///   is measured the same way as the mining tests.
    /// </summary>
    public class TwelvePoolBuild : BabySharkBuild
    {
        private const int PoolCost = 200;
        private readonly DroneMorphService _droneMorph;
        private readonly SpawningPoolPlacementService _spawningPoolPlacement;

        private ulong _poolBuilderTag;
        private bool _poolPlacementResolved;
        private Point2D _poolPlacement;
        private bool _poolUnderConstruction;
        private bool _poolCompleted;
        private int _lastPoolLogFrame = -1;
        private readonly BuildOptions _buildOptions;

        // --- mineral-rate CSV logging (ported from BuildIne) ---
        public int TestNumber { get; set; } = 12;
        private int _prevMinerals = -1;
        private int _initialMinerals = -1;
        private int _initialFrame;
        private bool _initialRecordWritten;
        private string _logFile;
        private bool _logFileResolved;
        private readonly object _logLock = new object();
        private bool _frameZeroContentsCaptured;
        private int _frameZeroWorkerCount;
        private readonly int[] _frameZeroMineralContents = new int[8];
        private readonly Dictionary<ulong, int> _frameZeroContentsByTag = new Dictionary<ulong, int>();
        private readonly Dictionary<string, int> _frameZeroContentsByPosition = new Dictionary<string, int>();
        private bool _frameZeroObservationCaptured;

        public TwelvePoolBuild(DefaultSharkyBot defaultBot) : base(defaultBot)
        {
            _buildOptions = defaultBot.BuildOptions;
            _droneMorph = new DroneMorphService(defaultBot) { DesiredDroneCount = 12 };
            _spawningPoolPlacement = new SpawningPoolPlacementService(defaultBot.DebugService);
        }

        // The CSV is named lazily on first write so the NW/SE start-location tag
        // (resolved in OnStart, after this constructor runs) can be included in the name.
        private void EnsureLogFile()
        {
            if (_logFileResolved)
            {
                return;
            }
            _logFileResolved = true;
            try
            {
                var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "mining_tests");
                Directory.CreateDirectory(folder);
                var tag = MiningTestLogNaming.TagForFileName();
                _logFile = Path.Combine(folder, $"mining_test_{TestNumber}{tag}_{DateTime.Now:yyyyMMddHHmmss}.csv");

                if (!File.Exists(_logFile))
                {
                    File.WriteAllText(_logFile, "Loaded,TimestampUTC,GameSeconds,Minerals,Frame,Worker.Count,M[1].content,M[2].content,M[3].content,M[4].content,M[5].content,M[6].content,M[7].content,M[8].content" + Environment.NewLine);
                }
            }
            catch
            {
                _logFile = null;
            }
        }

        public override void OnStart(int frame)
        {
            base.OnStart(frame);
            _poolBuilderTag = 0;
            _poolPlacementResolved = false;
            _poolPlacement = null;
            _poolUnderConstruction = false;
            _poolCompleted = false;
            _lastPoolLogFrame = -1;
            _prevMinerals = -1;
            _initialMinerals = -1;
            _initialFrame = 0;
            _initialRecordWritten = false;
            _frameZeroContentsCaptured = false;
            _frameZeroObservationCaptured = false;
            _frameZeroWorkerCount = 0;
            Array.Clear(_frameZeroMineralContents, 0, _frameZeroMineralContents.Length);
            _frameZeroContentsByTag.Clear();
            _frameZeroContentsByPosition.Clear();
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();
            if (observation?.Observation?.RawData?.Units == null)
            {
                return actions;
            }

            // Phase 1: top up to 12 drones, never beyond — no larva morphing into drones
            // past the 12th; on a 12-drone start this is a no-op and larva stays larva.
            actions.AddRange(_droneMorph.Update(GetFrame(observation), observation));

            var selfUnits = observation.Observation.RawData.Units
                .Where(unit => unit.Alliance == Alliance.Self)
                .ToList();

            // Mineral-rate CSV (same cadence as BuildIne): frame-zero capture, then one
            // record per mineral change. Runs before the pool early-returns so the file
            // keeps recording after the pool is ordered and completed.
            if (!_frameZeroObservationCaptured && MacroData != null && MacroData.Frame >= 0)
            {
                CaptureFrameZeroObservation(observation);
            }
            if (!_frameZeroContentsCaptured)
            {
                TryResolveFrameZeroMineralContents();
            }
            if (MacroData != null)
            {
                var currentMinerals = MacroData.Minerals;
                var currentFrame = MacroData.Frame;

                if (_prevMinerals == -1)
                {
                    _prevMinerals = currentMinerals;
                    _initialMinerals = currentMinerals;
                    _initialFrame = currentFrame;
                }
                else if (currentMinerals != _prevMinerals)
                {
                    AppendMineralRecord(currentMinerals, currentFrame, useFrameZeroContents: false);
                    _prevMinerals = currentMinerals;
                }
            }
            if (!_initialRecordWritten && _frameZeroContentsCaptured && _initialMinerals >= 0)
            {
                AppendMineralRecord(_initialMinerals, _initialFrame, useFrameZeroContents: true);
                _initialRecordWritten = true;
            }

            _poolUnderConstruction = selfUnits.Any(unit =>
                unit.UnitType == (uint)UnitTypes.ZERG_SPAWNINGPOOL && unit.BuildProgress < 1f);
            _poolCompleted = selfUnits.Any(unit =>
                unit.UnitType == (uint)UnitTypes.ZERG_SPAWNINGPOOL && unit.BuildProgress >= 1f);

            // 5x5 rotation (owner spec 2026-10-13): the three temp drones
            // (13-B3 BB, 14-S3 SB, 15-B4 BB) replace the worker B2 took with it
            // into the pool — 11 remaining workers + 3 temps = 14. Desired stays
            // 12 until the pool morph is observed so no larva morphs early.
            // 13-B3 extractor trick (owner spec 2026-10-15 22:20): the trick
            // morph needs the LAST free supply point, so the temp morphing is
            // staged — hold at 13 (11 + 13-B3 + 14-S3) until the trick larva's
            // morph is observed begun (supply 14/14), then queue an overlord
            // (the trick's extra worker needs the supply) and raise to 15 once
            // a second overlord exists so 15-B4 can morph (11 + 3 temps + the
            // trick drone = 15 workers — "one extra worker"). On a failed trick
            // (Done without MorphBegun) fall back to the plain 14 target.
            if (Settings.IsMagannatha5x5Rotation
                && (_poolUnderConstruction || _poolCompleted))
            {
                var stagedTarget = !Settings.B3ExtractorTrickDone
                    ? 13
                    : !Settings.B3ExtractorTrickMorphBegun
                        ? 14
                        : CountUnits(UnitTypes.ZERG_OVERLORD) >= 2 ? 15 : 14;
                if (_droneMorph.DesiredDroneCount < stagedTarget)
                {
                    _droneMorph.DesiredDroneCount = stagedTarget;
                    Console.WriteLine($"[12POOL] temp drone target staged to {stagedTarget} (trick morphBegun={Settings.B3ExtractorTrickMorphBegun} trickDone={Settings.B3ExtractorTrickDone} overlords={CountUnits(UnitTypes.ZERG_OVERLORD)}) frame={GetFrame(observation)}");
                }

                if (Settings.B3ExtractorTrickMorphBegun
                    && !Settings.B3ExtractorTrickOverlordQueued
                    && CountUnits(UnitTypes.ZERG_OVERLORD) < 2)
                {
                    Settings.B3ExtractorTrickOverlordQueued = true;
                    SetDesiredUnitCount(UnitTypes.ZERG_OVERLORD, CountUnits(UnitTypes.ZERG_OVERLORD) + 1);
                    Console.WriteLine($"[12POOL] trick morph begun — overlord queued for the extra worker frame={GetFrame(observation)}");
                }

                // Food-point race (run8 postmortem 2026-10-16): Sharky's MacroManager
                // keeps producing workers (desired count 40) and grabs the single
                // supply point the INSTANT the extractor commit frees it (drone ->
                // building flips used supply 14 -> 13, foodLeft 0 -> 1), so the
                // engine rejects 13-B3's trick morph and the extractor finishes —
                // "do not Finish the extractor". While the trick is unfinished and
                // all three temps exist, take worker production away from Sharky:
                // StrictWorkerCount makes the balancer skip its per-frame
                // DesiredUnitCounts reset and BuildUnits[drone]=false stops
                // UnitBuilder. Restored the frame the trick resolves.
                if (!Settings.B3ExtractorTrickDone
                    && selfUnits.Count(unit => unit.UnitType == (uint)UnitTypes.ZERG_DRONE) >= 13)
                {
                    if (!_buildOptions.StrictWorkerCount)
                    {
                        _buildOptions.StrictWorkerCount = true;
                        Console.WriteLine($"[12POOL] StrictWorkerCount ON — trick window, protecting the last supply point frame={GetFrame(observation)}");
                    }
                    if (MacroData.BuildUnits.ContainsKey(UnitTypes.ZERG_DRONE))
                    {
                        MacroData.BuildUnits[UnitTypes.ZERG_DRONE] = false;
                    }
                }
                else if (Settings.B3ExtractorTrickDone && _buildOptions.StrictWorkerCount)
                {
                    _buildOptions.StrictWorkerCount = false;
                    Console.WriteLine($"[12POOL] StrictWorkerCount OFF — trick resolved frame={GetFrame(observation)}");
                }
            }

            if (_poolCompleted)
            {
                IsComplete = true;
                return actions;
            }

            if (_poolUnderConstruction)
            {
                // The builder is committed; stop re-issuing and let it finish.
                return actions;
            }

            // 5x5 rotation experiment (owner spec 2026-10-13): the worker instruction
            // books own the pool. B2's book returns on the {BB,Pool} preposition row,
            // waits for the bank to hold PoolCost, and issues BUILD_SPAWNINGPOOL
            // itself — a second drone ordered here would race the books, so the pool
            // phase is inert under the rotation flag (the mineral-rate CSV above
            // keeps recording either way).
            if (Settings.IsMagannatha5x5Rotation)
            {
                return actions;
            }

            if (MacroData == null || MacroData.Minerals < PoolCost)
            {
                return actions;
            }

            // Phase 2: minerals banked to 200 — order the pool at the mini-wall placement.
            if (!_poolPlacementResolved)
            {
                var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                _poolPlacement = _spawningPoolPlacement.GetPlacement(Globals.CurrentMapData, startIndex);
                _poolPlacementResolved = _poolPlacement != null;
                if (!_poolPlacementResolved)
                {
                    LogOnce(observation, "[12POOL] pool ordered but placement unresolved; retrying next frame");
                    return actions;
                }

                LogOnce(observation, $"[12POOL] minerals={MacroData.Minerals} >= {PoolCost}; ordering pool at ({_poolPlacement.X:F2},{_poolPlacement.Y:F2})");
            }

            var builder = ResolvePoolBuilder(selfUnits);
            if (builder == null)
            {
                LogOnce(observation, "[12POOL] no worker available to build the pool; retrying next frame");
                return actions;
            }

            actions.Add(CreateBuildCommand(builder.Tag, _poolPlacement));
            return actions;
        }

        private Unit ResolvePoolBuilder(List<Unit> selfUnits)
        {
            // Keep the same builder once chosen so the re-issued command targets the
            // worker that is already walking; fall back to a new drone if it died.
            if (_poolBuilderTag != 0)
            {
                var existing = selfUnits.FirstOrDefault(unit =>
                    unit.Tag == _poolBuilderTag && unit.UnitType == (uint)UnitTypes.ZERG_DRONE);
                if (existing != null)
                {
                    return existing;
                }

                _poolBuilderTag = 0;
            }

            var builder = selfUnits
                .Where(unit => unit.UnitType == (uint)UnitTypes.ZERG_DRONE)
                .OrderBy(unit => unit.Tag)
                .FirstOrDefault();
            if (builder != null)
            {
                _poolBuilderTag = builder.Tag;
                Console.WriteLine($"[12POOL] pool builder selected: drone {builder.Tag}");
            }

            return builder;
        }

        private static SC2APIProtocol.Action CreateBuildCommand(ulong workerTag, Point2D placement)
        {
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.BUILD_SPAWNINGPOOL,
                TargetWorldSpacePos = new Point2D { X = placement.X, Y = placement.Y },
                QueueCommand = false
            };
            command.UnitTags.Add(workerTag);
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        private void LogOnce(ResponseObservation observation, string message)
        {
            var frame = GetFrame(observation);
            if (frame == _lastPoolLogFrame)
            {
                return;
            }

            _lastPoolLogFrame = frame;
            Console.WriteLine($"{message} frame={frame}");
        }

        private static int GetFrame(ResponseObservation observation)
        {
            return observation?.Observation == null ? -1 : (int)observation.Observation.GameLoop;
        }

        private string BuildCurrentMineralContentsCsv()
        {
            var mapData = Globals.CurrentMapData;
            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var orderedMinerals = mapData?.OrderedMainMinerals?.ElementAtOrDefault(startIndex)?
                .Where(mineral => mineral != null && mineral.Index >= 1 && mineral.Index <= 8)
                .OrderBy(mineral => mineral.Index)
                .ToList();
            if (snapshot == null || orderedMinerals == null || orderedMinerals.Count != 8)
            {
                return ",,,,,,,,,";
            }

            var liveMinerals = snapshot.Minerals ?? new Dictionary<ulong, MineralDto>();
            var contents = new int[8];
            foreach (var mineral in orderedMinerals)
            {
                var liveMineral = liveMinerals.TryGetValue(mineral.UnitTag, out var byTag)
                    ? byTag
                    : snapshot.VisibleMinerals?.FirstOrDefault(candidate => candidate != null
                        && candidate.Position != null
                        && mineral.Position != null
                        && Math.Abs(candidate.Position.X - mineral.Position.X) < 0.05f
                        && Math.Abs(candidate.Position.Y - mineral.Position.Y) < 0.05f);
                if (liveMineral == null)
                {
                    return ",,,,,,,,,";
                }

                contents[mineral.Index - 1] = liveMineral.MineralContents;
            }

            var workerCount = snapshot.AvailableWorkers?.Count
                ?? snapshot.SelfUnits?.Values.Count(worker => worker != null && worker.UnitType == (uint)UnitTypes.ZERG_DRONE)
                ?? 0;
            return $",{workerCount},{string.Join(",", contents)}";
        }

        private void CaptureFrameZeroObservation(ResponseObservation observation)
        {
            _frameZeroObservationCaptured = true;
            var rawUnits = observation?.Observation?.RawData?.Units;
            if (rawUnits == null)
            {
                return;
            }

            _frameZeroWorkerCount = rawUnits.Count(unit => unit != null && unit.Alliance == Alliance.Self && unit.UnitType == (uint)UnitTypes.ZERG_DRONE);
            foreach (var unit in rawUnits.Where(unit => unit != null && unit.Alliance == Alliance.Neutral && unit.HasMineralContents && unit.Tag != 0))
            {
                _frameZeroContentsByTag[unit.Tag] = unit.MineralContents;
                _frameZeroContentsByPosition[$"{unit.Pos.X:F2},{unit.Pos.Y:F2}"] = unit.MineralContents;
            }
        }

        private void TryResolveFrameZeroMineralContents()
        {
            var mapData = Globals.CurrentMapData;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (mapData?.OrderedMainMinerals == null || startIndex < 0 || startIndex >= mapData.OrderedMainMinerals.Count)
            {
                return;
            }

            var orderedMinerals = mapData.OrderedMainMinerals[startIndex]
                .Where(mineral => mineral != null && mineral.Index >= 1 && mineral.Index <= 8)
                .OrderBy(mineral => mineral.Index)
                .ToList();
            if (orderedMinerals.Count != 8)
            {
                return;
            }

            var contents = new int[8];
            foreach (var mineral in orderedMinerals)
            {
                if (!_frameZeroContentsByTag.TryGetValue(mineral.UnitTag, out var content)
                    && (mineral.Position == null
                        || !_frameZeroContentsByPosition.TryGetValue($"{mineral.Position.X:F2},{mineral.Position.Y:F2}", out content)))
                {
                    return;
                }

                contents[mineral.Index - 1] = content;
            }

            Array.Copy(contents, _frameZeroMineralContents, contents.Length);
            _frameZeroContentsCaptured = true;
        }

        private void AppendMineralRecord(int minerals, int frame, bool useFrameZeroContents)
        {
            EnsureLogFile();
            if (string.IsNullOrEmpty(_logFile)) return;

            double gameSeconds = frame / 22.4;
            try
            {
                if (DefaultBot?.FrameToTimeConverter != null)
                {
                    gameSeconds = DefaultBot.FrameToTimeConverter.GetTime(frame).TotalSeconds;
                }
            }
            catch { }

            var contents = useFrameZeroContents
                ? $",{_frameZeroWorkerCount},{string.Join(",", _frameZeroMineralContents)}"
                : BuildCurrentMineralContentsCsv();
            var loadedStatus = Settings.BaseDtosLoadedBeforeGameConnection ? "loaded:true" : "loaded:false";
            var line = $"{loadedStatus},{DateTime.UtcNow:o},{gameSeconds:F3},{minerals},{frame}{contents}{Environment.NewLine}";

            try
            {
                lock (_logLock)
                {
                    File.AppendAllText(_logFile, line);
                }
            }
            catch { }
        }
    }
}
