using SC2APIProtocol;
using Sharky;
using BabySharkBot.Setup;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BabySharkBot.Services
{
    public sealed class WorkerCommandTelemetry
    {
        private readonly object _lock = new object();
        private readonly string _logFile;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        private long _commandTraceSequence;
        private readonly Dictionary<ulong, List<int>> _lastObservedOrderAbilityIds = new Dictionary<ulong, List<int>>();
        private readonly Dictionary<ulong, HarvestTimingState> _harvestTimingStates = new Dictionary<ulong, HarvestTimingState>();

        public WorkerCommandTelemetry()
        {
            try
            {
                var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "mining_tests");
                Directory.CreateDirectory(folder);
                _logFile = Path.Combine(folder, $"worker_commands_BabyShark_{DateTime.Now:yyyyMMddHHmmss}.jsonl");
            }
            catch
            {
                _logFile = null;
            }
        }

        public void LogObservedOrderAbilityChanges(
            ResponseObservation observation,
            IEnumerable<WorkerEntryDto> workers,
            string sourceManager)
        {
            if (string.IsNullOrEmpty(_logFile)
                || observation?.Observation?.RawData?.Units == null
                || workers == null)
            {
                return;
            }

            var rawWorkers = observation.Observation.RawData.Units
                .Where(unit => unit != null && unit.Alliance == Alliance.Self && IsWorker((UnitTypes)unit.UnitType))
                .ToDictionary(unit => unit.Tag);
            var frame = (int)observation.Observation.GameLoop;
            foreach (var worker in workers)
            {
                if (worker == null || !rawWorkers.TryGetValue(worker.UnitTag, out var rawWorker))
                {
                    continue;
                }

                var currentAbilityIds = rawWorker.Orders?
                    .Select(order => (int)order.AbilityId)
                    .ToList() ?? new List<int>();
                if (_lastObservedOrderAbilityIds.TryGetValue(worker.UnitTag, out var previousAbilityIds)
                    && previousAbilityIds.SequenceEqual(currentAbilityIds))
                {
                    continue;
                }

                _lastObservedOrderAbilityIds[worker.UnitTag] = currentAbilityIds;
                Append(new WorkerOrderAbilityChangeRecord
                {
                    RecordType = "WorkerOrderAbilityChange",
                    Bot = "BabyShark",
                    TimestampUtc = DateTime.UtcNow,
                    GameFrame = frame,
                    GameSeconds = frame / 22.4,
                    SourceManager = sourceManager ?? string.Empty,
                    WorkerTag = worker.UnitTag,
                    WorkerLabel = worker.FinalLabel ?? worker.Label ?? worker.StartLabel ?? string.Empty,
                    CurrentOrderAbilityIds = currentAbilityIds,
                    TargetUnitTag = worker.TargetUnitTag == 0 ? null : worker.TargetUnitTag
                });
            }
        }

        public void LogWorkerObservations(
            ResponseObservation observation,
            IEnumerable<WorkerEntryDto> workers,
            IEnumerable<AssignedWorkerDto> assignedWorkers,
            string sourceManager)
        {
            if (string.IsNullOrEmpty(_logFile)
                || observation?.Observation?.RawData?.Units == null
                || workers == null)
            {
                return;
            }

            var rawWorkers = observation.Observation.RawData.Units
                .Where(unit => unit != null && unit.Alliance == Alliance.Self && IsWorker((UnitTypes)unit.UnitType))
                .ToDictionary(unit => unit.Tag);
            var frame = (int)observation.Observation.GameLoop;
            var assignedByTag = (assignedWorkers ?? Enumerable.Empty<AssignedWorkerDto>())
                .Where(worker => worker != null)
                .ToDictionary(worker => worker.UnitID);
            foreach (var worker in workers)
            {
                if (worker == null || !rawWorkers.TryGetValue(worker.UnitTag, out var rawWorker))
                {
                    continue;
                }

                assignedByTag.TryGetValue(worker.UnitTag, out var assignment);
                var target = assignment?.MiningTargets?.ElementAtOrDefault(assignment.Mti);
                var workerLabel = worker.FinalLabel ?? worker.Label ?? worker.StartLabel ?? string.Empty;
                var targetLabel = target?.ToResourceLabel ?? string.Empty;
                UpdateHarvestTiming(worker, rawWorker, frame, workerLabel, targetLabel);
                var waitState = workerLabel.EndsWith("3", StringComparison.OrdinalIgnoreCase)
                    && !worker.IsCarrying
                    && target?.HarvestPoint != null
                    && worker.Position != null
                    && DistanceSquared(worker.Position, target.HarvestPoint) <= 1.5f * 1.5f;

                Append(new WorkerObservationRecord
                {
                    RecordType = "WorkerObservation",
                    Bot = "BabyShark",
                    TimestampUtc = DateTime.UtcNow,
                    GameFrame = frame,
                    GameSeconds = frame / 22.4,
                    SourceManager = sourceManager ?? string.Empty,
                    WorkerTag = worker.UnitTag,
                    WorkerLabel = workerLabel,
                    WorkerRole = workerLabel.Length == 2 ? workerLabel.Substring(0, 1) : string.Empty,
                    RoleNumber = workerLabel.Length == 2 ? workerLabel.Substring(1, 1) : string.Empty,
                    TargetMineralLabel = targetLabel,
                    MineralSide = targetLabel.Length == 2 ? targetLabel.Substring(1, 1) : string.Empty,
                    AssignedTargetUnitTag = target?.ResourceUnitId,
                    IsRoleThreeWaitState = waitState,
                    WaitPointX = target?.HarvestPoint?.X,
                    WaitPointY = target?.HarvestPoint?.Y,
                    WorkerX = worker.Position?.X,
                    WorkerY = worker.Position?.Y,
                    IsCarrying = worker.IsCarrying,
                    WasCarrying = worker.WasCarrying,
                    JustPickedUp = worker.JustPickedUp,
                    TargetUnitTag = worker.TargetUnitTag == 0 ? null : worker.TargetUnitTag
                });
            }
        }

        public void LogAction(SC2APIProtocol.Action action, ResponseObservation observation, string sourceManager)
        {
            if (action?.ActionRaw?.UnitCommand == null
                || observation?.Observation?.RawData?.Units == null)
            {
                return;
            }

            var command = action.ActionRaw.UnitCommand;
            var workers = observation.Observation.RawData.Units
                .Where(unit => unit != null
                    && unit.Alliance == Alliance.Self
                    && IsWorker((UnitTypes)unit.UnitType)
                    && command.UnitTags.Contains(unit.Tag))
                .ToList();

            foreach (var worker in workers)
            {
                var frame = (int)observation.Observation.GameLoop;
                var traceId = System.Threading.Interlocked.Increment(ref _commandTraceSequence);
                var workerLabel = ResolveWorkerLabel(worker.Tag);
                var ability = ((Abilities)command.AbilityId).ToString();
                var targetTag = command.TargetUnitTag == 0 ? string.Empty : $" targetTag={command.TargetUnitTag}";
                var targetPosition = command.TargetWorldSpacePos == null
                    ? string.Empty
                    : $" targetPos=({command.TargetWorldSpacePos.X:F2},{command.TargetWorldSpacePos.Y:F2})";
                Console.WriteLine($"[Mining{traceId}] frame={frame} source={sourceManager ?? string.Empty} worker={worker.Tag} Label={workerLabel} command={ability} queued={command.QueueCommand.ToString().ToLowerInvariant()}{targetTag}{targetPosition}");

                var record = new WorkerCommandRecord
                {
                    Bot = "BabyShark",
                    TimestampUtc = DateTime.UtcNow,
                    GameFrame = frame,
                    GameSeconds = frame / 22.4,
                    CommandTraceId = traceId,
                    SourceManager = sourceManager ?? string.Empty,
                    WorkerTag = worker.Tag,
                    WorkerType = ((UnitTypes)worker.UnitType).ToString(),
                    WorkerX = worker.Pos?.X,
                    WorkerY = worker.Pos?.Y,
                    CurrentOrderAbilityIds = worker.Orders?.Select(order => (int)order.AbilityId).ToList() ?? new List<int>(),
                    AbilityId = command.AbilityId,
                    Ability = ((Abilities)command.AbilityId).ToString(),
                    QueueCommand = command.QueueCommand,
                    TargetUnitTag = command.TargetUnitTag == 0 ? null : command.TargetUnitTag,
                    TargetX = command.TargetWorldSpacePos?.X,
                    TargetY = command.TargetWorldSpacePos?.Y
                };

                Append(record);
            }
        }

        private void UpdateHarvestTiming(
            WorkerEntryDto worker,
            Unit rawWorker,
            int frame,
            string workerLabel,
            string targetLabel)
        {
            var gatherOrder = rawWorker.Orders?.Any(order => IsGatherAbility((int)order.AbilityId)) == true;
            var resourceTag = worker.TargetUnitTag;
            _harvestTimingStates.TryGetValue(worker.UnitTag, out var state);

            if (state == null && gatherOrder && resourceTag != 0 && !worker.IsCarrying)
            {
                _harvestTimingStates[worker.UnitTag] = new HarvestTimingState
                {
                    ResourceTag = resourceTag,
                    StartFrame = frame,
                    WorkerLabel = workerLabel,
                    MineralLabel = targetLabel
                };
                return;
            }

            if (state == null)
            {
                return;
            }

            var harvestEnded = worker.IsCarrying
                || !gatherOrder
                || resourceTag == 0
                || resourceTag != state.ResourceTag;
            if (!harvestEnded)
            {
                return;
            }

            if (frame > state.StartFrame)
            {
                Append(new HarvestTimingRecord
                {
                    RecordType = "HarvestTiming",
                    Bot = "BabyShark",
                    TimestampUtc = DateTime.UtcNow,
                    WorkerTag = worker.UnitTag,
                    WorkerLabel = state.WorkerLabel,
                    MineralLabel = state.MineralLabel,
                    ResourceTag = state.ResourceTag,
                    StartFrame = state.StartFrame,
                    EndFrame = frame,
                    HarvestFrames = frame - state.StartFrame
                });
            }

            _harvestTimingStates.Remove(worker.UnitTag);
            if (!worker.IsCarrying && gatherOrder && resourceTag != 0)
            {
                _harvestTimingStates[worker.UnitTag] = new HarvestTimingState
                {
                    ResourceTag = resourceTag,
                    StartFrame = frame,
                    WorkerLabel = workerLabel,
                    MineralLabel = targetLabel
                };
            }
        }

        private static bool IsGatherAbility(int abilityId)
        {
            return abilityId == (int)Abilities.HARVEST_GATHER
                || abilityId == (int)Abilities.HARVEST_GATHER_DRONE
                || abilityId == (int)Abilities.HARVEST_GATHER_PROBE
                || abilityId == (int)Abilities.HARVEST_GATHER_SCV;
        }

        private void Append(object record)
        {
            try
            {
                var line = JsonSerializer.Serialize(record, _jsonOptions) + Environment.NewLine;
                lock (_lock)
                {
                    File.AppendAllText(_logFile, line);
                }
            }
            catch
            {
            }
        }

        private static float DistanceSquared(Vector2Dto first, Vector2Dto second)
        {
            if (first == null || second == null)
            {
                return float.MaxValue;
            }

            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }

        private string ResolveWorkerLabel(ulong workerTag)
        {
            return BabySharkAI.Instance?.MiningManager?.WorkerLabelService?.GetLabel(workerTag) ?? string.Empty;
        }

        private static bool IsWorker(UnitTypes unitType)
        {
            return unitType == UnitTypes.ZERG_DRONE
                || unitType == UnitTypes.TERRAN_SCV
                || unitType == UnitTypes.PROTOSS_PROBE;
        }

        private sealed class HarvestTimingState
        {
            public ulong ResourceTag { get; set; }
            public int StartFrame { get; set; }
            public string WorkerLabel { get; set; }
            public string MineralLabel { get; set; }
        }

        private sealed class HarvestTimingRecord
        {
            public string RecordType { get; set; }
            public string Bot { get; set; }
            public DateTime TimestampUtc { get; set; }
            public ulong WorkerTag { get; set; }
            public string WorkerLabel { get; set; }
            public string MineralLabel { get; set; }
            public ulong ResourceTag { get; set; }
            public int StartFrame { get; set; }
            public int EndFrame { get; set; }
            public int HarvestFrames { get; set; }
        }

        private sealed class WorkerOrderAbilityChangeRecord
        {
            public string RecordType { get; set; }
            public string Bot { get; set; }
            public DateTime TimestampUtc { get; set; }
            public int GameFrame { get; set; }
            public double GameSeconds { get; set; }
            public string SourceManager { get; set; }
            public ulong WorkerTag { get; set; }
            public string WorkerLabel { get; set; }
            public List<int> CurrentOrderAbilityIds { get; set; }
            public ulong? TargetUnitTag { get; set; }
        }

        private sealed class WorkerObservationRecord
        {
            public string RecordType { get; set; }
            public string Bot { get; set; }
            public DateTime TimestampUtc { get; set; }
            public int GameFrame { get; set; }
            public double GameSeconds { get; set; }
            public string SourceManager { get; set; }
            public ulong WorkerTag { get; set; }
            public string WorkerLabel { get; set; }
            public string WorkerRole { get; set; }
            public string RoleNumber { get; set; }
            public string TargetMineralLabel { get; set; }
            public string MineralSide { get; set; }
            public ulong? AssignedTargetUnitTag { get; set; }
            public bool IsRoleThreeWaitState { get; set; }
            public float? WaitPointX { get; set; }
            public float? WaitPointY { get; set; }
            public float? WorkerX { get; set; }
            public float? WorkerY { get; set; }
            public bool IsCarrying { get; set; }
            public bool WasCarrying { get; set; }
            public bool JustPickedUp { get; set; }
            public List<int> CurrentOrderAbilityIds { get; set; }
            public ulong? TargetUnitTag { get; set; }
        }

        private sealed class WorkerCommandRecord
        {
            public string Bot { get; set; }
            public DateTime TimestampUtc { get; set; }
            public int GameFrame { get; set; }
            public double GameSeconds { get; set; }
            public long CommandTraceId { get; set; }
            public string SourceManager { get; set; }
            public ulong WorkerTag { get; set; }
            public string WorkerType { get; set; }
            public float? WorkerX { get; set; }
            public float? WorkerY { get; set; }
            public List<int> CurrentOrderAbilityIds { get; set; }
            public int AbilityId { get; set; }
            public string Ability { get; set; }
            public bool QueueCommand { get; set; }
            public ulong? TargetUnitTag { get; set; }
            public float? TargetX { get; set; }
            public float? TargetY { get; set; }
        }
    }
}
