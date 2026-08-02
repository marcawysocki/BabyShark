using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using Sharky.Builds.Zerg;
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
        private readonly Sharky.Builds.MacroServices.BuildingRequestCancellingService _buildingRequestCancellingService;

        private bool _extractorsRequested;
        private bool _extraDronesQueued;
        private bool _extractorTrickCompleted;

        private bool _step5Called;
        private Point2D _step5Target;
        private bool _stopTriggered;

        // --- logging fields for mineral sampling ---
        public int TestNumber { get; set; } = 6;
        int _prevMinerals = -1;
        string _logFile;
        readonly object _logLock = new object();

        public BuildIne(DefaultSharkyBot defaultBot) : base(defaultBot)
        {
            _buildingRequestCancellingService = defaultBot.BuildingRequestCancellingService;
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
                    File.WriteAllText(_logFile, "Test,TimestampUTC,GameSeconds,Minerals,Frame" + Environment.NewLine);
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

            SetDesiredGases(0);
            SetDesiredProductionCount(UnitTypes.ZERG_HATCHERY, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_DRONE, 14);
            SetDesiredUnitCount(UnitTypes.ZERG_QUEEN, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_OVERLORD, 1);

            _extractorsRequested = false;
            _extraDronesQueued = false;
            _extractorTrickCompleted = false;
            _prevMinerals = -1;
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            // record mineral changes for spreadsheet:
            if (MacroData != null)
            {
                var currentMinerals = MacroData.Minerals;
                var currentFrame = MacroData.Frame;

                if (_prevMinerals == -1)
                {
                    _prevMinerals = currentMinerals;
                    AppendMineralRecord(currentMinerals, currentFrame);
                }
                else if (currentMinerals != _prevMinerals)
                {
                    AppendMineralRecord(currentMinerals, currentFrame);
                    _prevMinerals = currentMinerals;
                }
            }

            // Early stop: when minerals exceed 275, trigger the build at the stored Step5 location (once)
            if (!_stopTriggered && MacroData != null && MacroData.Minerals > 275)
            {
                _stopTriggered = true;
                if (_step5Target != null)
                {
                    Console.WriteLine($"BuildIne: Prepositioning threshold reached for {_step5Target.X}, {_step5Target.Y}");
                    // Maw calls removed as they are missing in this environment
                }
                return Array.Empty<SC2APIProtocol.Action>();
            }

            if (_extractorTrickCompleted)
            {
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
                return Array.Empty<SC2APIProtocol.Action>();
            }

            var supply = MacroData.FoodUsed;
            var larvaCount = ActiveUnitData.SelfUnits.Values.Count(u => u.Unit.UnitType == (uint)UnitTypes.ZERG_LARVA);
            var mineralsValue = MacroData.Minerals;

            // Step 1: At 14 supply, 1 larva, 120 minerals, request extractor
            if (!_extractorsRequested && supply >= 14 && larvaCount >= 1 && mineralsValue >= 120)
            {
                SetDesiredGases(1);
                _extractorsRequested = true;
            }

            // Step 2: Once extractor in progress, queue extra drone
            var inProgressExtractors = ActiveUnitData.SelfUnits.Values.Count(u =>
                u.Unit.UnitType == (uint)UnitTypes.ZERG_EXTRACTOR &&
                u.Unit.BuildProgress < 1.0f);

            if (_extractorsRequested && !_extraDronesQueued && inProgressExtractors >= 1)
            {
                var currentDroneCount = CountUnits(UnitTypes.ZERG_DRONE);
                SetDesiredUnitCount(UnitTypes.ZERG_DRONE, currentDroneCount + 1);
                _extraDronesQueued = true;
                _extractorsRequested = false;
            }

            // Step 3: When extra drone is queued, cancel extractor
            if (_extraDronesQueued)
            {
                if (inProgressExtractors > 0)
                {
                    SetDesiredGases(0);
                    _buildingRequestCancellingService.RequestCancel(UnitTypes.ZERG_EXTRACTOR, 0);
                }
                else
                {
                    _extractorTrickCompleted = true;
                    SetDesiredGases(0);
                    SetDesiredProductionCount(UnitTypes.ZERG_HATCHERY, 2);
                    SetDesiredUnitCount(UnitTypes.ZERG_OVERLORD, CountUnits(UnitTypes.ZERG_OVERLORD) + 1);
                }
            }

            return Array.Empty<SC2APIProtocol.Action>();
        }

        private void AppendMineralRecord(int minerals, int frame)
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

            var line = $"{TestNumber},{DateTime.UtcNow:o},{gameSeconds:F3},{minerals},{frame}{Environment.NewLine}";

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
