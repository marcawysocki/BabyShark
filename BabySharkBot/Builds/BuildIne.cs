using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using Sharky.Builds.Zerg;
using BabySharkBot.Services;
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
        private const int DesiredDrones = 14;
        
        private readonly Sharky.Builds.MacroServices.BuildingRequestCancellingService _buildingRequestCancellingService;

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
            _droneMorph = new DroneMorphService(defaultBot) { DesiredDroneCount = DesiredDrones };
            _extractorTrick = new ExtractorTrickService(defaultBot);
            
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
            _extractorTrick.Reset();

            SetDesiredGases(0);
            SetDesiredProductionCount(UnitTypes.ZERG_HATCHERY, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_DRONE, DesiredDrones);
            SetDesiredUnitCount(UnitTypes.ZERG_QUEEN, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_OVERLORD, 1);

            _prevMinerals = -1;
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();
            int frame = (int)observation.Observation.GameLoop;

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
