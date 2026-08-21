using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using BabySharkBot.Setup;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// Runs the normal BuildIne macro setup to twelve drones, then simulates a fresh
    /// twelve-worker game start from the recorded eight-worker opening formation.
    /// </summary>
    public sealed class BuildTest12WorkerStart : BuildIne
    {
        private const int StartingWorkerCount = 8;
        private const int TargetWorkerCount = 12;
        private const float WorkerSpacing = 1.0f;
        private const float FormationTolerance = 0.8f;

        private readonly Dictionary<string, Vector2Dto> _recordedStartPositions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ulong> _recordedStartTagsByLabel = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<ulong> _recordedStartTags = new();
        private bool _initialPositionsRecorded;
        private bool _formationCommandsIssued;
        private bool _formationObservedComplete;
        private int _formationCommandFrame = -1;
        private int _formationCompleteFrame = -1;
        private ulong _hatcheryTag;

        protected override int DesiredDrones => TargetWorkerCount;

        public BuildTest12WorkerStart(DefaultSharkyBot defaultBot) : base(defaultBot)
        {
        }

        public override void OnStart(int frame)
        {
            base.OnStart(frame);
            _recordedStartPositions.Clear();
            _recordedStartTagsByLabel.Clear();
            _recordedStartTags.Clear();
            _initialPositionsRecorded = false;
            _formationCommandsIssued = false;
            _formationObservedComplete = false;
            _formationCommandFrame = -1;
            _formationCompleteFrame = -1;
            _hatcheryTag = 0;
            Settings.SimulatedStartActive = false;
            Settings.BuildOwnsWorkerCommands = false;
            Settings.StartFrame = frame;
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var actions = base.OnFrame(observation)?.ToList() ?? new List<SC2APIProtocol.Action>();
            var currentFrame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            var relativeFrame = Settings.GetRelativeFrame(currentFrame);
            var snapshot = Globals.CurrentObservation;

            if (!_initialPositionsRecorded && relativeFrame == 0 && snapshot?.AvailableWorkers?.Count == StartingWorkerCount)
            {
                RecordInitialEightWorkerPositions(snapshot);
            }

            if (_initialPositionsRecorded && !_formationCommandsIssued
                && snapshot?.AvailableWorkers?.Count >= TargetWorkerCount)
            {
                var formationActions = BuildTwelveWorkerFormationCommands(observation, snapshot).ToList();
                if (formationActions.Count > 0)
                {
                    Settings.SimulatedStartActive = true;
                    Settings.BuildOwnsWorkerCommands = true;
                    _formationCommandFrame = currentFrame;
                    _formationCommandsIssued = true;
                    actions.AddRange(formationActions);
                }
            }
            else if (_formationCommandsIssued && !_formationObservedComplete
                && snapshot?.AvailableWorkers?.Count >= TargetWorkerCount
                && IsFormationComplete(snapshot))
            {
                _formationObservedComplete = true;
                _formationCompleteFrame = currentFrame;
                Settings.SimulatedStartActive = false;
                Settings.BuildOwnsWorkerCommands = false;
                Settings.StartFrame = currentFrame + 1;
                Console.WriteLine($"BuildTest12WorkerStart: formation complete at frame {currentFrame}; BuildManager labels remain authoritative and CCA begins at relative frame 0 on frame {Settings.StartFrame}.");
            }

            return actions;
        }

        private void RecordInitialEightWorkerPositions(ObservationSnapshotDto snapshot)
        {
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var mineralCom = ResolveMineralCenter(startIndex);
            if (mineralCom == null)
            {
                return;
            }

            var workers = snapshot.AvailableWorkers
                .Where(tag => snapshot.SelfUnits.TryGetValue(tag, out var worker)
                    && worker != null
                    && worker.UnitTag != 0
                    && worker.Position != null)
                .Select(tag => snapshot.SelfUnits[tag])
                .ToList();
            if (workers.Count != StartingWorkerCount)
            {
                return;
            }

            var workerTuples = workers.Select(worker =>
                (worker.UnitTag, worker.Position.X, worker.Position.Y, worker.Position.Z, worker.UnitType));
            // BuildManager owns labels; this local chain is geometry-only for formation recording.
            var orderedWorkers = WorkerLabelChainHelper.BuildGreedyWorkerEntries(workerTuples, mineralCom, null);
            if (orderedWorkers.Count != StartingWorkerCount)
            {
                return;
            }

            foreach (var worker in orderedWorkers)
            {
                var label = worker.Label;
                _recordedStartTags.Add(worker.UnitTag);
                _recordedStartTagsByLabel[label] = worker.UnitTag;
                _recordedStartPositions[label] = new Vector2Dto(worker.Position.X, worker.Position.Y, worker.Position.Z);
            }

            _initialPositionsRecorded = _recordedStartPositions.Count == StartingWorkerCount;
            Console.WriteLine($"BuildTest12WorkerStart: recorded {_recordedStartPositions.Count} initial positions W8-W1.");
        }

        private IEnumerable<SC2APIProtocol.Action> BuildTwelveWorkerFormationCommands(
            ResponseObservation observation,
            ObservationSnapshotDto snapshot)
        {
            var actions = new List<SC2APIProtocol.Action>();
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var townhall = ResolveTownhall(startIndex);
            if (townhall == null || _recordedStartPositions.Count != StartingWorkerCount)
            {
                return actions;
            }

            _hatcheryTag = ResolveHatcheryTag(observation, townhall);
            var liveWorkers = snapshot.AvailableWorkers
                .Where(tag => snapshot.SelfUnits.TryGetValue(tag, out var worker)
                    && worker != null
                    && worker.UnitTag != 0
                    && worker.Position != null)
                .Select(tag => snapshot.SelfUnits[tag])
                .ToList();
            if (liveWorkers.Count < TargetWorkerCount)
            {
                return actions;
            }

            var chain = GetRecordedChain();
            var newWorkers = liveWorkers.Where(worker => !_recordedStartTags.Contains(worker.UnitTag))
                .OrderBy(worker => worker.UnitTag)
                .Take(4)
                .ToList();
            if (chain.Count != StartingWorkerCount || newWorkers.Count != 4)
            {
                return actions;
            }

            var targets = BuildExtendedFormationTargets(chain, newWorkers);
            foreach (var worker in liveWorkers)
            {
                if (!targets.TryGetValue(worker.UnitTag, out var target))
                {
                    continue;
                }

                actions.Add(Stop(worker.UnitTag));
                var carriesCargo = worker.IsCarrying && _hatcheryTag != 0;
                if (carriesCargo)
                {
                    actions.Add(SmartToHatchery(worker.UnitTag, _hatcheryTag));
                    actions.Add(QueuedStop(worker.UnitTag));
                }

                actions.Add(RangeMove(worker.UnitTag, target, carriesCargo));
            }

            Console.WriteLine($"BuildTest12WorkerStart: issued formation commands at frame {_formationCommandFrame} for {targets.Count} workers.");
            return actions;
        }

        private bool IsFormationComplete(ObservationSnapshotDto snapshot)
        {
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var townhall = ResolveTownhall(startIndex);
            if (townhall == null) return false;

            var liveWorkers = snapshot.AvailableWorkers
                .Where(tag => snapshot.SelfUnits.TryGetValue(tag, out var worker)
                    && worker != null
                    && worker.UnitTag != 0
                    && worker.Position != null)
                .Select(tag => snapshot.SelfUnits[tag])
                .ToList();
            if (liveWorkers.Count < TargetWorkerCount) return false;

            var chain = GetRecordedChain();
            var newWorkers = liveWorkers.Where(worker => !_recordedStartTags.Contains(worker.UnitTag))
                .OrderBy(worker => worker.UnitTag)
                .Take(4)
                .ToList();
            if (chain.Count != StartingWorkerCount || newWorkers.Count != 4) return false;

            var targets = BuildExtendedFormationTargets(chain, newWorkers);
            return targets.All(pair =>
            {
                var worker = liveWorkers.FirstOrDefault(candidate => candidate.UnitTag == pair.Key);
                return worker?.Position != null && Distance(worker.Position, pair.Value) <= FormationTolerance;
            });
        }

        private List<WorkerEntryDto> GetRecordedChain()
        {
            return Enumerable.Range(1, StartingWorkerCount)
                .Select(number => $"W{number}")
                .Where(label => _recordedStartPositions.ContainsKey(label))
                .Select(label => new WorkerEntryDto
                {
                    UnitTag = _recordedStartTagsByLabel[label],
                    Label = label,
                    StartLabel = label,
                    FinalLabel = label,
                    Position = _recordedStartPositions[label]
                })
                .OrderByDescending(worker => ParseWorkerNumber(worker.Label))
                .ToList();
        }

        private Dictionary<ulong, Point2D> BuildExtendedFormationTargets(
            List<WorkerEntryDto> chain,
            List<WorkerEntryDto> newWorkers)
        {
            var targets = new Dictionary<ulong, Point2D>();
            foreach (var worker in chain)
            {
                if (_recordedStartTagsByLabel.TryGetValue(worker.Label, out var tag))
                {
                    targets[tag] = ToPoint(worker.Position);
                }
            }

            var w8 = chain.First(worker => worker.Label.Equals("W8", StringComparison.OrdinalIgnoreCase));
            var w7 = chain.First(worker => worker.Label.Equals("W7", StringComparison.OrdinalIgnoreCase));
            var w1 = chain.First(worker => worker.Label.Equals("W1", StringComparison.OrdinalIgnoreCase));
            var w2 = chain.First(worker => worker.Label.Equals("W2", StringComparison.OrdinalIgnoreCase));
            var w8Direction = Normalize(w8.Position.X - w7.Position.X, w8.Position.Y - w7.Position.Y, 1f, 0f);
            var w1Direction = Normalize(w1.Position.X - w2.Position.X, w1.Position.Y - w2.Position.Y, -1f, 0f);

            for (var index = 0; index < 2; index++)
            {
                var firstTarget = new Point2D
                {
                    X = w8.Position.X + w8Direction.X * WorkerSpacing * (index + 1),
                    Y = w8.Position.Y + w8Direction.Y * WorkerSpacing * (index + 1)
                };
                var secondTarget = new Point2D
                {
                    X = w1.Position.X + w1Direction.X * WorkerSpacing * (index + 1),
                    Y = w1.Position.Y + w1Direction.Y * WorkerSpacing * (index + 1)
                };
                targets[newWorkers[index].UnitTag] = firstTarget;
                targets[newWorkers[index + 2].UnitTag] = secondTarget;
            }

            return targets;
        }

        private Vector2Dto ResolveMineralCenter(int startIndex)
        {
            return Globals.CurrentMapData?.MineralCenterOfMass != null
                && startIndex >= 0
                && startIndex < Globals.CurrentMapData.MineralCenterOfMass.Count
                ? Globals.CurrentMapData.MineralCenterOfMass[startIndex]
                : null;
        }

        private List<OrderedMineral> ResolveOrderedMinerals(int startIndex)
        {
            return Globals.CurrentMapData?.OrderedMainMinerals != null
                && startIndex >= 0
                && startIndex < Globals.CurrentMapData.OrderedMainMinerals.Count
                ? Globals.CurrentMapData.OrderedMainMinerals[startIndex]
                    .Where(mineral => mineral != null)
                    .OrderBy(mineral => mineral.Index)
                    .ToList()
                : null;
        }

        private Vector2Dto ResolveTownhall(int startIndex)
        {
            return Globals.CurrentMapData?.StartingTownHall != null
                && startIndex >= 0
                && startIndex < Globals.CurrentMapData.StartingTownHall.Length
                ? Globals.CurrentMapData.StartingTownHall[startIndex]
                : null;
        }

        private static ulong ResolveHatcheryTag(ResponseObservation observation, Vector2Dto townhall)
        {
            return observation?.Observation?.RawData?.Units?
                .Where(unit => unit != null
                    && unit.Alliance == Alliance.Self
                    && unit.UnitType == (uint)UnitTypes.ZERG_HATCHERY)
                .OrderBy(unit => DistanceSquared(unit.Pos.X, unit.Pos.Y, townhall.X, townhall.Y))
                .Select(unit => unit.Tag)
                .FirstOrDefault() ?? 0;
        }

        private SC2APIProtocol.Action Stop(ulong tag)
        {
            var workerLabel = WorkerLabelService?.GetLabel(tag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMANDb] phase=BuildTest12 worker={tag} Label={workerLabel} command=STOP queued=false");
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.STOP,
                        UnitTags = { tag }
                    }
                }
            };
        }

        private SC2APIProtocol.Action SmartToHatchery(ulong workerTag, ulong hatcheryTag)
        {
            var workerLabel = WorkerLabelService?.GetLabel(workerTag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMANDc] phase=BuildTest12 worker={workerTag} Label={workerLabel} command=SMART targetTag={hatcheryTag} queued=false");
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.SMART,
                        UnitTags = { workerTag },
                        TargetUnitTag = hatcheryTag
                    }
                }
            };
        }

        private SC2APIProtocol.Action QueuedStop(ulong tag)
        {
            var workerLabel = WorkerLabelService?.GetLabel(tag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMANDd] phase=BuildTest12 worker={tag} Label={workerLabel} command=STOP queued=true");
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.STOP,
                        UnitTags = { tag },
                        QueueCommand = true
                    }
                }
            };
        }

        private SC2APIProtocol.Action RangeMove(ulong tag, Point2D target, bool queued)
        {
            var workerLabel = WorkerLabelService?.GetLabel(tag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMANDe] phase=BuildTest12 worker={tag} Label={workerLabel} command=MOVE pos=({target.X:F2},{target.Y:F2}) queued={queued.ToString().ToLowerInvariant()}");
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw
                {
                    UnitCommand = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.MOVE,
                        UnitTags = { tag },
                        TargetWorldSpacePos = target,
                        QueueCommand = queued
                    }
                }
            };
        }

        private static Point2D ToPoint(Vector2Dto position)
        {
            return new Point2D { X = position.X, Y = position.Y };
        }

        private static Vector2Dto Normalize(float x, float y, float fallbackX, float fallbackY)
        {
            var length = MathF.Sqrt(x * x + y * y);
            return length <= 0.0001f
                ? new Vector2Dto(fallbackX, fallbackY)
                : new Vector2Dto(x / length, y / length);
        }

        private static float Distance(Vector2Dto first, Point2D second)
        {
            return MathF.Sqrt(DistanceSquared(first.X, first.Y, second.X, second.Y));
        }

        private static float DistanceSquared(float firstX, float firstY, float secondX, float secondY)
        {
            var dx = firstX - secondX;
            var dy = firstY - secondY;
            return dx * dx + dy * dy;
        }

        private static int ParseWorkerNumber(string label)
        {
            return int.TryParse(label.AsSpan(1), out var number) ? number : 0;
        }
    }
}
