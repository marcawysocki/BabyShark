using SC2APIProtocol;
using Sharky;
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

        public void LogAction(SC2APIProtocol.Action action, ResponseObservation observation, string sourceManager)
        {
            if (string.IsNullOrEmpty(_logFile)
                || action?.ActionRaw?.UnitCommand == null
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
                var record = new WorkerCommandRecord
                {
                    Bot = "BabyShark",
                    TimestampUtc = DateTime.UtcNow,
                    GameFrame = frame,
                    GameSeconds = frame / 22.4,
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

        private void Append(WorkerCommandRecord record)
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

        private static bool IsWorker(UnitTypes unitType)
        {
            return unitType == UnitTypes.ZERG_DRONE
                || unitType == UnitTypes.TERRAN_SCV
                || unitType == UnitTypes.PROTOSS_PROBE;
        }

        private sealed class WorkerCommandRecord
        {
            public string Bot { get; set; }
            public DateTime TimestampUtc { get; set; }
            public int GameFrame { get; set; }
            public double GameSeconds { get; set; }
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
