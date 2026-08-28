using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using Sharky.Builds.Zerg;
using BabySharkBot.Services;
using BabySharkBot.Setup;
using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// Ported version of BuildTest.cs using the BabySharkBuild architecture.
    /// Handles logging, extractor trick, and ramp calculation.
    /// </summary>
    public class BuildIne : BabySharkBuild
    {
        private readonly DroneMorphService _droneMorph;
        private readonly ExtractorTrickService _extractorTrick;
        protected virtual int DesiredDrones => 14;
        
        private readonly Sharky.Builds.MacroServices.BuildingRequestCancellingService _buildingRequestCancellingService;

        private bool _step5Called;
        private Point2D _step5Target;
        private bool _stopTriggered;
        private bool _teamBuildSelected;

        // --- logging fields for mineral sampling ---
        public int TestNumber { get; set; } = 6;
        int _prevMinerals = -1;
        private int _initialMinerals = -1;
        private int _initialFrame;
        private bool _initialRecordWritten;
        string _logFile;
        readonly object _logLock = new object();
        private bool _frameZeroContentsCaptured;
        private int _frameZeroWorkerCount;
        private readonly int[] _frameZeroMineralContents = new int[8];
        private readonly Dictionary<ulong, int> _frameZeroContentsByTag = new Dictionary<ulong, int>();
        private readonly Dictionary<string, int> _frameZeroContentsByPosition = new Dictionary<string, int>();
        private bool _frameZeroObservationCaptured;

        public BuildIne(DefaultSharkyBot defaultBot) : base(defaultBot)
        {
            _buildingRequestCancellingService = defaultBot.BuildingRequestCancellingService;
            _droneMorph = new DroneMorphService(defaultBot) { DesiredDroneCount = DesiredDrones };
            _extractorTrick = new ExtractorTrickService(defaultBot);
            
            _step5Called = false;
            _stopTriggered = false;
            _step5Target = null;
            _teamBuildSelected = false;

            // initialize logging (minimal, local CSV)
            try
            {
                var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "mining_tests");
                Directory.CreateDirectory(folder);
                _logFile = Path.Combine(folder, $"mining_test_{TestNumber}_{DateTime.Now:yyyyMMddHHmmss}.csv");

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
            _extractorTrick.Reset();
            _step5Called = false;
            _stopTriggered = false;
            _step5Target = null;
            _teamBuildSelected = false;
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

            if (Settings.GetRelativeFrame(frame) == 0)
            {
                SelectTeamBuild();
            }
        }

        private void SelectTeamBuild()
        {
            if (_teamBuildSelected)
            {
                return;
            }

            var isEightWorkerBuild = Settings.AvailableWorker.Count == 8;
            if (isEightWorkerBuild)
            {
                BuildTeam8();
            }
            else
            {
                BuildTeam12();
            }

            _teamBuildSelected = true;
            Console.WriteLine($"BuildIne: selected {(isEightWorkerBuild ? "BuildTeam8" : "BuildTeam12")} from {Settings.AvailableWorker.Count} available workers.");
        }

        private void BuildTeam8()
        {
            SetDesiredGases(0);
            SetDesiredProductionCount(UnitTypes.ZERG_HATCHERY, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_DRONE, DesiredDrones);
            SetDesiredUnitCount(UnitTypes.ZERG_QUEEN, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_OVERLORD, 1);
        }

        private void BuildTeam12()
        {
            SetDesiredGases(0);
            SetDesiredProductionCount(UnitTypes.ZERG_HATCHERY, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_DRONE, DesiredDrones);
            SetDesiredUnitCount(UnitTypes.ZERG_QUEEN, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_OVERLORD, 1);
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();
            int frame = (int)observation.Observation.GameLoop;

            SelectTeamBuild();

            if (!_frameZeroObservationCaptured && MacroData != null && MacroData.Frame >= 0)
            {
                CaptureFrameZeroObservation(observation);
            }
            if (!_frameZeroContentsCaptured)
            {
                TryResolveFrameZeroMineralContents();
            }

            // CCA owns the 8-worker STOP -> MOVE -> queued SMART opening.
            // BuildIne only selects the build and manages macro production targets.

            // record mineral changes for spreadsheet:
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

            // --- Phase 1: Continuous drone morphing until desired count ---
            actions.AddRange(_droneMorph.Update(frame, observation));

            // --- Phase 2: Extractor trick at 14 drones (for 15th worker supply) ---
            actions.AddRange(_extractorTrick.Update(observation));

            // Early stop: when minerals exceed 275, trigger the build at the stored Step5 location (once)
            if (!_stopTriggered && MacroData != null && MacroData.Minerals > 275)
            {
                _stopTriggered = true;
                if (_step5Target != null)
                {
                    Console.WriteLine($"BuildIne: Prepositioning threshold reached for {_step5Target.X}, {_step5Target.Y}");
                }
                return actions;
            }

            // Call Step5 exactly once after minerals exceed 175
            if (!_step5Called && MacroData != null && MacroData.Minerals > 175)
            {
                try
                {
                    var target = Step5.CalculateTopOfRamp(DefaultBot, DefaultBot.BaseData, ActiveUnitData);
                    _step5Target = target;
                    Console.WriteLine($"BuildIne: Step5 target calculated at X={target.X}, Y={target.Y}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"BuildIne: Step5 call failed: {ex.Message}");
                }
                _step5Called = true;
            }

            return actions;
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
            if (string.IsNullOrEmpty(_logFile)) return;

            double gameSeconds = frame / 22.4; 
            try
            {
                if (DefaultBot?.FrameToTimeConverter != null)
                {
                    gameSeconds = DefaultBot.FrameToTimeConverter.GetTime(frame).TotalSeconds;
                }
            }
            catch {}

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
            catch {}
        }
    }
}
