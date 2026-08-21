using SC2APIProtocol;
using Sharky.DefaultBot;
using System.Linq;
using System;
using System.IO;
using System.Collections.Generic;
using BabySharkBot.Setup;

namespace Sharky.Builds.Zerg
{
    public class BuildTest : ZergSharkyBuild
    {
        private readonly Sharky.Builds.MacroServices.BuildingRequestCancellingService _buildingRequestCancellingService;

        private bool _extractorsRequested;
        private bool _extraDronesQueued;
        private bool _extractorTrickCompleted;

        // minimal additions to enable Step5 call
        private readonly DefaultSharkyBot _defaultSharkyBot;
        private bool _step5Called;

        // remember the Step5 target so we can issue the build later
        private Point2D _step5Target;

        // Stop guard for >275 minerals
        private bool _stopTriggered;

        // --- logging fields for mineral sampling ---
        // TestNumber can be set externally if you want multiple runs labeled
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

        public BuildTest(DefaultSharkyBot defaultSharkyBot)
            : base(defaultSharkyBot)
        {
            _buildingRequestCancellingService = defaultSharkyBot.BuildingRequestCancellingService;

            // store bot reference for Step5 helper
            _defaultSharkyBot = defaultSharkyBot;
            _step5Called = false;

            _stopTriggered = false;
            _step5Target = null;

            // initialize logging (minimal, local CSV)
            try
            {
                var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "mining_tests");
                Directory.CreateDirectory(folder);
                _logFile = Path.Combine(folder, $"mining_test_{TestNumber}_{DateTime.Now:yyyyMMddHHmmss}.csv");

                if (!File.Exists(_logFile))
                {
                    // Header includes frame-zero worker count and canonical M[1]-M[8] contents.
                    File.WriteAllText(_logFile, "Test,TimestampUTC,GameSeconds,Minerals,Frame,Worker.Count,M[1].content,M[2].content,M[3].content,M[4].content,M[5].content,M[6].content,M[7].content,M[8].content" + Environment.NewLine);
                }
            }
            catch
            {
                // do not break runtime on logging failures
                _logFile = null;
            }
        }

        public override void StartBuild(int frame)
        {
            base.StartBuild(frame);

            BuildOptions.StrictWorkerCount = true;
            BuildOptions.StrictGasCount = true;
            BuildOptions.StrictSupplyCount = true;

            MacroData.DesiredGases = 0;
            MacroData.DesiredProductionCounts[UnitTypes.ZERG_HATCHERY] = 1;
            MacroData.DesiredUnitCounts[UnitTypes.ZERG_DRONE] = 15;
            MacroData.DesiredUnitCounts[UnitTypes.ZERG_QUEEN] = 1;
            MacroData.DesiredUnitCounts[UnitTypes.ZERG_OVERLORD ] = 1;

            _extractorsRequested = false;
            _extraDronesQueued = false;
            _extractorTrickCompleted = false;

            // reset prev minerals at start of the build
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

        public override void OnFrame(ResponseObservation observation)
        {
            base.OnFrame(observation);

            if (!_frameZeroObservationCaptured && MacroData != null && MacroData.Frame >= 0)
            {
                CaptureFrameZeroObservation(observation);
            }
            if (!_frameZeroContentsCaptured)
            {
                TryResolveFrameZeroMineralContents();
            }

            // record mineral changes for spreadsheet:
            if (MacroData != null)
            {
                var currentMinerals = MacroData.Minerals;
                var currentFrame = MacroData.Frame;

                if (_prevMinerals == -1)
                {
                    // Buffer the initial row until frame-zero M[1]-M[8] contents are available.
                    _prevMinerals = currentMinerals;
                    _initialMinerals = currentMinerals;
                    _initialFrame = currentFrame;
                }
                else if (currentMinerals != _prevMinerals)
                {
                    // minerals changed this frame -> log
                    AppendMineralRecord(currentMinerals, currentFrame);
                    _prevMinerals = currentMinerals;
                }
            }

            if (!_initialRecordWritten && _frameZeroContentsCaptured && _initialMinerals >= 0)
            {
                AppendMineralRecord(_initialMinerals, _initialFrame);
                _initialRecordWritten = true;
            }

            // Early stop: when minerals exceed 275, trigger the build at the stored Step5 location (once)
            if (!_stopTriggered && MacroData != null && MacroData.Minerals > 275)
            {
                _stopTriggered = true;

                if (_step5Target != null)
                {
                    try
                    {
                        // Always use the Maw helper directly (do not delegate to PrePositionBuilderTask)
                        // Maw.MicroControllers.MineralWalkerMaw.PrepositionAt(_defaultSharkyBot, _step5Target, MacroData.Frame);
                        System.Console.WriteLine($"BuildTest: PrepositionAt called for {_step5Target.X}, {_step5Target.Y}");

                        // Log the best commander selected by Maw (if any)
                        // var best = Maw.MicroControllers.MineralWalkerMaw.BestUnitCommander;
                        // if (best != null && best.UnitCalculation?.Unit != null)
                        // {
                        //     System.Console.WriteLine($"BuildTest: Maw.BestUnitCommander tag={best.UnitCalculation.Unit.Tag}");
                        // }
                        // else
                        // {
                        //     System.Console.WriteLine("BuildTest: Maw.BestUnitCommander is null");
                        // }
                    }
                    catch (System.Exception ex)
                    {
                        System.Console.WriteLine($"BuildTest: error requesting build at step5 target: {ex.Message}");
                    }
                }

                // stop further Step5 activity
                return;
            }

            if (_extractorTrickCompleted)
            {
                // Call Step5 exactly once after minerals exceed 175
                if (!_step5Called && MacroData != null && MacroData.Minerals > 175)
                {
                    try
                    {
                        var target = Step5.CalculateTopOfRamp(_defaultSharkyBot, BaseData, ActiveUnitData);
                        _step5Target = target; // store for later build at 275
                        System.Console.WriteLine($"BuildTest: Step5 target X={target.X}, Y={target.Y}");
                        // Delegate to Maw helper to perform prepositioning; Step5 no longer issues orders
                        // Maw.MicroControllers.MineralWalkerMaw.PrepositionAt(_defaultSharkyBot, target, MacroData.Frame);
                    }
                    catch (System.Exception ex)
                    {
                        System.Console.WriteLine($"BuildTest: Step5 call failed: {ex.Message}");
                    }
                    _step5Called = true;
                }

                return;
            }

            var supply = MacroData.FoodUsed;
            var larvaCount = ActiveUnitData.SelfUnits.Values.Count(u => u.Unit.UnitType == (uint)UnitTypes.ZERG_LARVA);
            var mineralsValue = MacroData.Minerals;

            // Step 1: At 14 supply, 2 larva, 250 minerals, request double extractor
            //if (!_extractorsRequested && supply >= 14 && larvaCount >= 2 && minerals >= 250)
            if (!_extractorsRequested && supply >= 14 && larvaCount >= 1 && mineralsValue >= 120)
            {
                MacroData.DesiredGases = 1; // Sharky will assign two drones to build
                _extractorsRequested = true;
            }

            // Step 2: Once 2 extractors in progress, queue 2 extra drones
            var inProgressExtractors = ActiveUnitData.SelfUnits.Values.Count(u =>
                u.Unit.UnitType == (uint)UnitTypes.ZERG_EXTRACTOR &&
                u.Unit.BuildProgress < 1.0f);

            if (_extractorsRequested && !_extraDronesQueued && inProgressExtractors >= 1)
            {
                var currentDroneCount = UnitCountService.Count(UnitTypes.ZERG_DRONE);
                MacroData.DesiredUnitCounts[UnitTypes.ZERG_DRONE] = currentDroneCount + 1;
                _extraDronesQueued = true;
                _extractorsRequested = false;

            }

            // Step 3: When extra drones are queued, cancel extractors
            if (_extraDronesQueued)
            {
                if (inProgressExtractors > 0)
                {
                    // Sharky uses BuildingRequestCancellingService for cancels
                    MacroData.DesiredGases = 0;

                    _buildingRequestCancellingService.RequestCancel(UnitTypes.ZERG_EXTRACTOR, 0);
                }
                else
                {
                    // Trick complete, reset macro intent
                    _extractorTrickCompleted = true;
                    MacroData.DesiredGases = 0;
                    BuildOptions.StrictGasCount = true;
                    MacroData.DesiredProductionCounts[UnitTypes.ZERG_HATCHERY] = 2;

                    // Step 4: Queue up next Overlord
                    MacroData.DesiredUnitCounts[UnitTypes.ZERG_OVERLORD] =
                        UnitCountService.Count(UnitTypes.ZERG_OVERLORD) + 1;
                }
            }
        }

        private void AppendMineralRecord(int minerals, int frame)
        {
            if (string.IsNullOrEmpty(_logFile)) return;

            // Compute game-time seconds with millisecond precision
            double gameSeconds = frame / 60.0; // fallback
            try
            {
                if (_defaultSharkyBot?.FrameToTimeConverter != null)
                {
                    gameSeconds = _defaultSharkyBot.FrameToTimeConverter.GetTime(frame).TotalSeconds;
                }
            }
            catch
            {
                // ignore and use fallback
            }

            var contents = _frameZeroContentsCaptured
                ? $",{_frameZeroWorkerCount},{string.Join(",", _frameZeroMineralContents)}"
                : ",,,,,,,,,";
            var line = $"{TestNumber},{DateTime.UtcNow:o},{gameSeconds:F3},{minerals},{frame}{contents}{Environment.NewLine}";

            try
            {
                lock (_logLock)
                {
                    File.AppendAllText(_logFile, line);
                }
            }
            catch
            {
                // swallow logging exceptions to avoid interfering with runtime
            }
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

        public override bool Transition(int frame)
        {
            return false;
        }
    }
}