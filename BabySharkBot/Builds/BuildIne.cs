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
            _teamBuildSelected = false;

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
            _step5Called = false;
            _stopTriggered = false;
            _step5Target = null;
            _teamBuildSelected = false;
            _prevMinerals = -1;

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

        private List<SC2APIProtocol.Action> BuildEightWorkerOpening(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();
            if (Settings.AvailableWorker.Count != 8 || observation?.Observation?.RawData?.Units == null)
            {
                return actions;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var mapData = Globals.CurrentMapData;
            if (mapData == null || startIndex < 0)
            {
                return actions;
            }

            var mineralLists = mapData.SecondaryOrderedMainMinerals != null
                && mapData.SecondaryOrderedMainMinerals.Count > startIndex
                && mapData.SecondaryOrderedMainMinerals[startIndex] != null
                && mapData.SecondaryOrderedMainMinerals[startIndex].Count >= 8
                ? mapData.SecondaryOrderedMainMinerals
                : mapData.OrderedMainMinerals;

            if (mineralLists == null || mineralLists.Count <= startIndex)
            {
                return actions;
            }

            var minerals = mineralLists[startIndex]
                ?.Where(m => m != null && m.Position != null)
                .OrderBy(m => m.Index)
                .ToList();
            if (minerals == null || minerals.Count < 8)
            {
                return actions;
            }

            var workers = observation.Observation.RawData.Units
                .Where(u => u != null && u.Alliance == Alliance.Self && u.Tag != 0 && u.UnitType == (uint)UnitTypes.ZERG_DRONE)
                .Select(u => new { Unit = u, Position = new Vector2Dto(u.Pos.X, u.Pos.Y, u.Pos.Z) })
                .ToList();
            if (workers.Count != 8)
            {
                return actions;
            }

            var mineralCom = mapData.MineralCenterOfMass != null && startIndex < mapData.MineralCenterOfMass.Count
                ? mapData.MineralCenterOfMass[startIndex]
                : null;
            if (mineralCom == null)
            {
                return actions;
            }

            var remaining = new List<dynamic>(workers);
            var greedyOrder = new List<dynamic>();
            while (remaining.Count > 0)
            {
                var next = greedyOrder.Count == 0
                    ? remaining.OrderByDescending(w => DistanceSquared(w.Position, mineralCom)).First()
                    : remaining.OrderBy(w => DistanceSquared(w.Position, greedyOrder[^1].Position)).First();
                greedyOrder.Add(next);
                remaining.Remove(next);
            }

            for (var chainIndex = 0; chainIndex < 8; chainIndex++)
            {
                var workerNumber = 8 - chainIndex;
                var mineral = minerals[7 - chainIndex];
                var worker = greedyOrder[chainIndex];
                var mineralTag = ResolveLiveMineralTag(mineral, observation);
                if (mineralTag == 0) continue;

                var returnPoint = mineral.ReturnPoint != null && (mineral.ReturnPoint.X != 0f || mineral.ReturnPoint.Y != 0f)
                    ? mineral.ReturnPoint
                    : CalculateReturnPoint(mapData.StartingTownHall[startIndex], mineral.Position);
                var harvestPoint = CalculateTemporaryHarvestPoint(worker.Position, returnPoint, mineral.Position, 1.5f);

                actions.Add(Stop(worker.Unit.Tag));
                actions.Add(Move(worker.Unit.Tag, harvestPoint));
                actions.Add(Smart(worker.Unit.Tag, mineralTag));
                Console.WriteLine($"BuildIne: W{workerNumber} -> M[{7 - chainIndex}] at frame 0");
            }

            return actions;
        }

        private static float DistanceSquared(Vector2Dto first, Vector2Dto second)
        {
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }

        private static Vector2Dto CalculateReturnPoint(Vector2Dto townhall, Vector2Dto mineral)
        {
            var dx = mineral.X - townhall.X;
            var dy = mineral.Y - townhall.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            return length <= 0.0001f
                ? new Vector2Dto(townhall.X + 2.75f, townhall.Y)
                : new Vector2Dto(townhall.X + dx / length * 2.75f, townhall.Y + dy / length * 2.75f);
        }

        private static Point2D CalculateTemporaryHarvestPoint(Vector2Dto worker, Vector2Dto returnPoint, Vector2Dto mineral, float offset)
        {
            var midX = (worker.X + returnPoint.X) * 0.5f;
            var midY = (worker.Y + returnPoint.Y) * 0.5f;
            var dirX = mineral.X - midX;
            var dirY = mineral.Y - midY;
            var length = MathF.Sqrt(dirX * dirX + dirY * dirY);
            if (length <= 0.0001f) return new Point2D { X = mineral.X + offset, Y = mineral.Y };
            return new Point2D { X = mineral.X - dirX / length * offset, Y = mineral.Y - dirY / length * offset };
        }

        private static ulong ResolveLiveMineralTag(OrderedMineral mineral, ResponseObservation observation)
        {
            var match = observation.Observation.RawData.Units
                .Where(u => u != null && u.Alliance == Alliance.Neutral)
                .OrderBy(u => DistanceSquared(new Vector2Dto(u.Pos.X, u.Pos.Y), mineral.Position))
                .FirstOrDefault();
            return match != null && DistanceSquared(new Vector2Dto(match.Pos.X, match.Pos.Y), mineral.Position) < 4f ? match.Tag : mineral.UnitTag;
        }

        private static SC2APIProtocol.Action Stop(ulong tag) => new SC2APIProtocol.Action
        {
            ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.STOP, UnitTags = { tag } } }
        };

        private static SC2APIProtocol.Action Move(ulong tag, Point2D point) => new SC2APIProtocol.Action
        {
            ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.MOVE, UnitTags = { tag }, TargetWorldSpacePos = point } }
        };

        private static SC2APIProtocol.Action Smart(ulong tag, ulong mineralTag) => new SC2APIProtocol.Action
        {
            ActionRaw = new ActionRaw { UnitCommand = new ActionRawUnitCommand { AbilityId = (int)Abilities.SMART, UnitTags = { tag }, TargetUnitTag = mineralTag, QueueCommand = true } }
        };

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();
            int frame = (int)observation.Observation.GameLoop;

            SelectTeamBuild();

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
