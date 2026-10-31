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
        private string _logFile;
        private bool _logFileResolved;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        private long _commandTraceSequence;
        private readonly Dictionary<ulong, List<int>> _lastObservedOrderAbilityIds = new Dictionary<ulong, List<int>>();
        private readonly Dictionary<ulong, HarvestTimingState> _harvestTimingStates = new Dictionary<ulong, HarvestTimingState>();
        // Per-worker rolling ability history (most recent first-order abilities, capped
        // at 10, consecutive duplicates collapsed). Shared by the console change lines
        // and the WorkerObservation records so both always show the same up-to-10 list.
        private readonly Dictionary<ulong, List<int>> _workerAbilityHistory = new Dictionary<ulong, List<int>>();

        public WorkerCommandTelemetry()
        {
        }

        // The log file is named lazily on first write so the NW/SE start-location tag
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
                _logFile = Path.Combine(folder, $"worker_commands_BabyShark{tag}_{DateTime.Now:yyyyMMddHHmmss}.jsonl");
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
            EnsureLogFile();
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
                var firstAbilityId = currentAbilityIds.Count > 0 ? currentAbilityIds[0] : 0;
                Append(new WorkerOrderAbilityChangeRecord
                {
                    RecordType = "WorkerOrderAbilityChange",
                    Bot = "BabyShark",
                    TimestampUtc = DateTime.UtcNow,
                    GameFrame = frame,
                    GameSeconds = frame / 22.4,
                    SourceManager = sourceManager ?? string.Empty,
                    WorkerTag = worker.UnitTag,
                    WorkerLabel = ResolveWorkerDisplayLabel(worker.UnitTag, null, worker),
                    CurrentOrderAbilityIds = currentAbilityIds,
                    TargetUnitTag = ResolveCommandTargetDescriptor(worker.UnitTag, firstAbilityId, worker.TargetUnitTag)
                });
            }
        }

        /// <summary>
        /// Console line on every first-order ability change for EVERY worker, carrying
        /// that worker's rolling last-10 ability history. Runs independently of file
        /// logging so the console output is always complete. Call before
        /// LogWorkerObservations so observation records see the updated history.
        /// </summary>
        public void LogWorkerAbilityChanges(
            ResponseObservation observation,
            IEnumerable<WorkerEntryDto> workers,
            string sourceManager)
        {
            if (observation?.Observation?.RawData?.Units == null || workers == null)
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

                var currentAbility = (int)(rawWorker.Orders?.FirstOrDefault()?.AbilityId ?? 0);
                if (!_workerAbilityHistory.TryGetValue(worker.UnitTag, out var history))
                {
                    history = new List<int>();
                    _workerAbilityHistory[worker.UnitTag] = history;
                }

                // --t1trace (owner test suite 2026-10-28 08:51): per-frame T1
                // position/ability/carrying dump over relative frames 5-80 —
                // diagnoses the Smart -> 1183 -> pickup walk-in curve.
                if (Settings.T1PositionTrace)
                {
                    var relativeTraceFrame = Settings.GetRelativeFrame(frame);
                    var isTraceWorker = Settings.T1TraceUnitTag != 0
                        ? worker.UnitTag == Settings.T1TraceUnitTag
                        : string.Equals(worker.FinalLabel ?? worker.Label ?? worker.StartLabel ?? string.Empty, "T1", StringComparison.OrdinalIgnoreCase);
                    if (isTraceWorker
                        && relativeTraceFrame >= 5
                        && relativeTraceFrame <= 160)
                    {
                        var traceCarrying = rawWorker.BuffIds != null
                            && rawWorker.BuffIds.Any(b => b == 271 || b == 272);
                        Console.WriteLine($"[T1TRACE] frame={relativeTraceFrame} pos=({rawWorker.Pos.X:F3},{rawWorker.Pos.Y:F3}) ability={currentAbility} carrying={traceCarrying}");
                    }
                }

                var hasLast = history.Count > 0;
                var last = hasLast ? history[history.Count - 1] : 0;
                if (hasLast && last == currentAbility)
                {
                    continue; // no change
                }

                history.Add(currentAbility);
                if (history.Count > 10)
                {
                    history.RemoveAt(0);
                }

                var workerLabel = worker.FinalLabel ?? worker.Label ?? worker.StartLabel ?? string.Empty;
                var from = !hasLast ? "-" : (last == 0 ? "none" : last.ToString());
                var to = currentAbility == 0 ? "none" : currentAbility.ToString();
                var orders = rawWorker.Orders == null
                    ? string.Empty
                    : string.Join(",", rawWorker.Orders.Select(order => (int)order.AbilityId));
                var last10 = string.Join(",", history.Select(ability => ability == 0 ? "none" : ability.ToString()));
                Console.WriteLine($"[WORKER ABILITY CHANGED] frame={frame} source={sourceManager ?? string.Empty} tag={worker.UnitTag} Label={workerLabel} ability={from}->{to} orders=[{orders}] last10=[{last10}]");
            }
        }

        /// <summary>Snapshot of a worker's rolling ability history (up to 10 entries).</summary>
        public List<int> GetAbilityHistory(ulong workerTag)
        {
            return _workerAbilityHistory.TryGetValue(workerTag, out var history)
                ? new List<int>(history)
                : new List<int>();
        }

        public void LogWorkerObservations(
            ResponseObservation observation,
            IEnumerable<WorkerEntryDto> workers,
            IEnumerable<AssignedWorkerDto> assignedWorkers,
            string sourceManager)
        {
            EnsureLogFile();
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
                var workerLabel = ResolveWorkerDisplayLabel(worker.UnitTag, assignment, worker);
                var roleInfo = ResolveWorkerRoleInfo(worker.UnitTag);
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
                    WorkerRole = roleInfo.Role,
                    RoleNumber = roleInfo.Number,
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
                    // The worker's rolling last-10 ability history (never null, never a
                    // lone current order) — updated by LogWorkerAbilityChanges, which
                    // must run before this method each frame.
                    CurrentOrderAbilityIds = GetAbilityHistory(worker.UnitTag),
                    TargetUnitTag = ResolveObservationTargetDescriptor(worker, rawWorker, assignment, target),
                    // Raw first-order target unit tag straight from the
                    // observation (the mineral being gathered / hatchery being
                    // returned to). Independent of assignments/books — the
                    // natural-observation test relies on this to attribute
                    // engine-chosen stand points to minerals.
                    OrderTargetUnitTag = rawWorker.Orders?.Count > 0
                        ? rawWorker.Orders[0].TargetUnitTag
                        : (ulong?)null
                });
            }
        }

        // --smsweep (owner spec 2026-10-29 08:39): one record per angular sweep
        // sample — the stand-still point a Smart command produced from a 1.87u
        // approach at the test angle, and its distance to hatchery circle 1.
        public void LogSmSweepSample(
            int frame,
            string startSide,
            string mineralLabel,
            ulong workerTag,
            double angleDeg,
            float approachX,
            float approachY,
            float standX,
            float standY,
            double distToHatcheryCircle1,
            bool valid,
            int stillFrames,
            int ordinal)
        {
            EnsureLogFile();
            if (string.IsNullOrEmpty(_logFile))
            {
                return;
            }
            Append(new SmSweepSampleRecord
            {
                RecordType = "SmSweepSample",
                Bot = "BabyShark",
                TimestampUtc = DateTime.UtcNow,
                GameFrame = frame,
                GameSeconds = frame / 22.4,
                StartSide = startSide ?? string.Empty,
                MineralLabel = mineralLabel ?? string.Empty,
                WorkerTag = workerTag,
                AngleDeg = angleDeg,
                ApproachX = approachX,
                ApproachY = approachY,
                StandX = standX,
                StandY = standY,
                DistToHatcheryCircle1 = distToHatcheryCircle1,
                Valid = valid,
                StillFrames = stillFrames,
                Ordinal = ordinal,
            });
        }

        // --smstage (owner spec 2026-10-29 12:11): one record per verify /
        // tipping / staging probe — where the worker was sent, where it stood,
        // whether it hit the ideal stand, held position, or harvested.
        public void LogSmStageSample(
            int frame,
            string startSide,
            string mineralLabel,
            ulong workerTag,
            string phase,
            double angleDeg,
            double radius,
            double bearingDeg,
            double targetX,
            double targetY,
            double standX,
            double standY,
            double distToIdeal,
            bool held,
            bool harvested,
            int mineralContentsBefore,
            int mineralContentsAfter,
            bool valid,
            string note,
            int ordinal)
        {
            EnsureLogFile();
            if (string.IsNullOrEmpty(_logFile))
            {
                return;
            }
            Append(new SmStageSampleRecord
            {
                RecordType = "SmStageSample",
                Bot = "BabyShark",
                TimestampUtc = DateTime.UtcNow,
                GameFrame = frame,
                GameSeconds = frame / 22.4,
                StartSide = startSide ?? string.Empty,
                MineralLabel = mineralLabel ?? string.Empty,
                WorkerTag = workerTag,
                Phase = phase ?? string.Empty,
                AngleDeg = angleDeg,
                Radius = radius,
                BearingDeg = bearingDeg,
                TargetX = targetX,
                TargetY = targetY,
                StandX = standX,
                StandY = standY,
                DistToIdeal = distToIdeal,
                Held = held,
                Harvested = harvested,
                MineralContentsBefore = mineralContentsBefore,
                MineralContentsAfter = mineralContentsAfter,
                Valid = valid,
                Note = note ?? string.Empty,
                Ordinal = ordinal,
            });
        }

        // --smhandoff (owner spec 2026-10-29 16:37): one record per handoff
        // event — A (harvester) and B (stager) state transitions, stands,
        // drift, harvest-frame counts, and the seam between A's round end
        // and B's first harvest frame.
        public void LogSmHandoffSample(
            int frame,
            string startSide,
            string mineralLabel,
            ulong workerTag,
            string role,
            string phase,
            double x,
            double y,
            double distToIdeal,
            int harvestFrames,
            string note,
            int ordinal)
        {
            EnsureLogFile();
            if (string.IsNullOrEmpty(_logFile))
            {
                return;
            }
            Append(new SmHandoffSampleRecord
            {
                RecordType = "SmHandoffSample",
                Bot = "BabyShark",
                TimestampUtc = DateTime.UtcNow,
                GameFrame = frame,
                GameSeconds = frame / 22.4,
                StartSide = startSide ?? string.Empty,
                MineralLabel = mineralLabel ?? string.Empty,
                WorkerTag = workerTag,
                Role = role ?? string.Empty,
                Phase = phase ?? string.Empty,
                X = x,
                Y = y,
                DistToIdeal = distToIdeal,
                HarvestFrames = harvestFrames,
                Note = note ?? string.Empty,
                Ordinal = ordinal,
            });
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
                var roleInfo = ResolveWorkerRoleInfo(worker.Tag);

                // Console logging is intentionally omitted here: the mining manager already
                // prints [MINING COMMAND{n}] with full instruction context for the same
                // actions, so a generic console line would list every command twice. The
                // JSONL record below is still written — that trace has no other source.
                var record = new WorkerCommandRecord
                {
                    Bot = "BabyShark",
                    TimestampUtc = DateTime.UtcNow,
                    GameFrame = frame,
                    GameSeconds = frame / 22.4,
                    CommandTraceId = traceId,
                    SourceManager = sourceManager ?? string.Empty,
                    WorkerTag = worker.Tag,
                    WorkerLabel = workerLabel,
                    WorkerRole = roleInfo.Role,
                    RoleNumber = roleInfo.Number,
                    WorkerType = ((UnitTypes)worker.UnitType).ToString(),
                    WorkerX = worker.Pos?.X,
                    WorkerY = worker.Pos?.Y,
                    CurrentOrderAbilityIds = worker.Orders?.Select(order => (int)order.AbilityId).ToList() ?? new List<int>(),
                    AbilityId = command.AbilityId,
                    Ability = ((Abilities)command.AbilityId).ToString(),
                    QueueCommand = command.QueueCommand,
                    TargetUnitTag = ResolveCommandTargetDescriptor(worker.Tag, (int)command.AbilityId, command.TargetUnitTag),
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

        // Canonical mineral/vespene label order, mirroring
        // BabySharkBuildManager.CanonicalJitResourceOrder so pair labels here are
        // canonicalized exactly the way the pair table canonicalizes them.
        private static readonly string[] CanonicalJitResourceOrder =
        {
            "VA", "VB", "TA", "TB", "SA", "SB", "BA", "BB", "YA", "YB", "Pool"
        };

        private static int CanonicalJitOrder(string label)
        {
            var index = Array.IndexOf(CanonicalJitResourceOrder, label);
            return index < 0 ? CanonicalJitResourceOrder.Length : index;
        }

        /// <summary>
        /// Worker label with real fallback chain: label service first, then the
        /// build-owned assignment role, then the DTO's own label fields. Never
        /// leaves the empty DTO default when any real source has a label.
        /// </summary>
        private string ResolveWorkerDisplayLabel(ulong workerTag, AssignedWorkerDto assignment, WorkerEntryDto worker)
        {
            var label = ResolveWorkerLabel(workerTag);
            if (!string.IsNullOrEmpty(label))
            {
                return label;
            }
            if (assignment != null && !string.IsNullOrEmpty(assignment.Role))
            {
                return assignment.Role;
            }
            return worker?.FinalLabel ?? worker?.Label ?? worker?.StartLabel ?? string.Empty;
        }

        /// <summary>
        /// WorkerRole = the instruction set the worker is ACTUALLY executing. Rows named
        /// "wait"/"last-line" are transition gates, not work: while parked on one the list
        /// has already issued the surrounding block's command (the worker is mid-harvest or
        /// mid-return), so the role resolves to the nearest real command row instead.
        /// The set is qualified with the row's pair-table property when present
        /// ("jitMH.HarvestA"); "ReturnPoint" is reported as "ReturnCargo". During a
        /// collision-avoidance detour the role is "Avoid.{avoided worker label}".
        /// RoleNumber = the resolved row's index inside the set.
        /// </summary>
        private static (string Role, string Number) ResolveWorkerRoleInfo(ulong workerTag)
        {
            if (!Settings.RuntimeWorkers.TryGetValue(workerTag, out var runtimeWorker) || runtimeWorker == null)
            {
                return (string.Empty, string.Empty);
            }

            if (runtimeWorker.CollisionDetourActive)
            {
                var avoidedLabel = BabySharkAI.Instance?.MiningManager?.WorkerLabelService
                    ?.GetLabel(runtimeWorker.CollisionAvoidedUnitTag) ?? string.Empty;
                var avoidRole = string.IsNullOrEmpty(avoidedLabel) ? "Avoid" : $"Avoid.{avoidedLabel}";
                return (avoidRole, runtimeWorker.CurInstrIdx.ToString());
            }

            var index = runtimeWorker.CurInstrIdx;
            WorkerInstruction instruction = null;
            if (index >= 0 && index < runtimeWorker.Instructions.Count)
            {
                instruction = runtimeWorker.Instructions[index];
            }
            instruction ??= runtimeWorker.Instructions.FirstOrDefault();

            // Control rows ("wait", "last-line") only gate an ability transition; the
            // in-flight command belongs to the nearest real command row at or before the
            // pointer (fall forward only when the whole prefix is control rows).
            if (IsControlRow(instruction))
            {
                var realIndex = -1;
                for (var candidate = (index >= 0 ? index : runtimeWorker.Instructions.Count - 1);
                     candidate >= 0;
                     candidate--)
                {
                    if (!IsControlRow(runtimeWorker.Instructions[candidate]))
                    {
                        realIndex = candidate;
                        break;
                    }
                }
                if (realIndex < 0)
                {
                    for (var candidate = index + 1; candidate < runtimeWorker.Instructions.Count; candidate++)
                    {
                        if (!IsControlRow(runtimeWorker.Instructions[candidate]))
                        {
                            realIndex = candidate;
                            break;
                        }
                    }
                }
                if (realIndex >= 0)
                {
                    index = realIndex;
                    instruction = runtimeWorker.Instructions[realIndex];
                }
            }

            var set = instruction?.InstructionSet ?? string.Empty;
            if (instruction != null && !string.IsNullOrWhiteSpace(instruction.TargetPointReference))
            {
                var dot = instruction.TargetPointReference.LastIndexOf('.');
                if (dot > 0 && dot < instruction.TargetPointReference.Length - 1)
                {
                    var property = instruction.TargetPointReference.Substring(dot + 1);
                    if (string.Equals(property, "ReturnPoint", StringComparison.OrdinalIgnoreCase))
                    {
                        property = "ReturnCargo";
                    }
                    if (!string.IsNullOrEmpty(set) && !set.EndsWith("." + property, StringComparison.Ordinal))
                    {
                        set = $"{set}.{property}";
                    }
                }
            }
            return (set, index >= 0 ? index.ToString() : string.Empty);
        }

        private static bool IsControlRow(WorkerInstruction instruction)
        {
            var set = instruction?.InstructionSet ?? string.Empty;
            return string.Equals(set, "wait", StringComparison.OrdinalIgnoreCase)
                || string.Equals(set, "last-line", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Target descriptor for observation records: "{labelA}-{labelB}.{phase}" in
        /// canonical pair order — e.g. "TA-TB.HarvestA" while gathering, "TA-TB.ReturnCargo"
        /// while carrying/returning. Falls back to the raw numeric tag (never null) when
        /// no assignment labels are available.
        /// </summary>
        private static string ResolveObservationTargetDescriptor(
            WorkerEntryDto worker,
            Unit rawWorker,
            AssignedWorkerDto assignment,
            MiningTargetDto target)
        {
            var labels = (assignment?.MiningTargets ?? Enumerable.Empty<MiningTargetDto>())
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.ResourceLabel))
                .Select(candidate => candidate.ResourceLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(CanonicalJitOrder)
                .ToList();

            string pair;
            if (labels.Count >= 2)
            {
                pair = $"{labels[0]}-{labels[1]}";
            }
            else if (labels.Count == 1)
            {
                pair = $"{labels[0]}-{labels[0]}";
            }
            else if (target != null && !string.IsNullOrWhiteSpace(target.ResourceLabel))
            {
                pair = $"{target.ResourceLabel}-{target.ResourceLabel}";
            }
            else
            {
                return worker?.TargetUnitTag != 0 ? worker.TargetUnitTag.ToString() : string.Empty;
            }

            var firstAbility = (int)(rawWorker?.Orders?.FirstOrDefault()?.AbilityId ?? 0);
            var returning = worker.IsCarrying
                || firstAbility == (int)Abilities.HARVEST_RETURN_DRONE
                || firstAbility == (int)Abilities.HARVEST_RETURN;
            if (returning)
            {
                return $"{pair}.ReturnCargo";
            }

            var targetLabel = !string.IsNullOrWhiteSpace(target?.ResourceLabel)
                ? target.ResourceLabel
                : labels.FirstOrDefault() ?? string.Empty;
            var phase = labels.Count >= 2
                && CanonicalJitOrder(targetLabel) > CanonicalJitOrder(labels[0])
                ? "HarvestB"
                : "HarvestA";
            return $"{pair}.{phase}";
        }

        /// <summary>
        /// Target descriptor for command/ability-change records (no assignment context):
        /// prefer the current instruction row's pair reference ("{base}-{A}-{B}.{property}"
        /// → "TA-TB.HarvestA"), then a mineral-tag → label lookup against the jit pair
        /// table. Falls back to the raw numeric tag (never null).
        /// </summary>
        private static string ResolveCommandTargetDescriptor(ulong workerTag, int abilityId, ulong commandTargetTag)
        {
            if (Settings.RuntimeWorkers.TryGetValue(workerTag, out var runtimeWorker) && runtimeWorker != null)
            {
                var index = runtimeWorker.CurInstrIdx;
                var instruction = index >= 0 && index < runtimeWorker.Instructions.Count
                    ? runtimeWorker.Instructions[index]
                    : null;
                if (TryParsePairReference(instruction?.TargetPointReference, out var pair, out var property))
                {
                    var phase = PhaseForAbility(abilityId, property);
                    if (string.IsNullOrEmpty(phase)
                        && IsGatherAbility(abilityId)
                        && !string.IsNullOrEmpty(pair))
                    {
                        // Gather with a non-harvest property: place the target label in the pair.
                        var targetLabel = ResolveResourceLabel(commandTargetTag);
                        if (!string.IsNullOrEmpty(targetLabel))
                        {
                            var pairLabels = pair.Split('-');
                            phase = pairLabels.Length >= 2 && CanonicalJitOrder(targetLabel) > CanonicalJitOrder(pairLabels[0])
                                ? "HarvestB"
                                : "HarvestA";
                        }
                    }
                    if (!string.IsNullOrEmpty(phase))
                    {
                        return $"{pair}.{phase}";
                    }
                }
            }

            if (commandTargetTag != 0)
            {
                var label = ResolveResourceLabel(commandTargetTag);
                if (!string.IsNullOrEmpty(label))
                {
                    var phase = PhaseForAbility(abilityId, null);
                    return string.IsNullOrEmpty(phase) ? label : $"{label}.{phase}";
                }
                return commandTargetTag.ToString();
            }
            return string.Empty;
        }

        // Parses "{base}-{labelA}-{labelB}.{property}" (BuildPairReference form) into
        // "labelA-labelB" + property. The base prefix is numeric; everything after the
        // FIRST dash is the pair (labels themselves never contain dashes).
        private static bool TryParsePairReference(string reference, out string pair, out string property)
        {
            pair = string.Empty;
            property = string.Empty;
            if (string.IsNullOrWhiteSpace(reference))
            {
                return false;
            }
            var dot = reference.LastIndexOf('.');
            if (dot <= 0 || dot == reference.Length - 1)
            {
                return false;
            }
            var key = reference.Substring(0, dot);
            property = reference.Substring(dot + 1);
            var firstDash = key.IndexOf('-');
            if (firstDash <= 0 || firstDash >= key.Length - 1)
            {
                return false;
            }
            pair = key.Substring(firstDash + 1);
            return !string.IsNullOrEmpty(pair);
        }

        // Phase name for a command ability / pair-table property. Return abilities map to
        // "ReturnCargo" (the pair table stores the geometry as "ReturnPoint").
        private static string PhaseForAbility(int abilityId, string property)
        {
            if (abilityId == (int)Abilities.HARVEST_RETURN || abilityId == (int)Abilities.HARVEST_RETURN_DRONE)
            {
                return "ReturnCargo";
            }
            if (string.Equals(property, "ReturnPoint", StringComparison.OrdinalIgnoreCase)) return "ReturnCargo";
            if (string.Equals(property, "HarvestA", StringComparison.OrdinalIgnoreCase)) return "HarvestA";
            if (string.Equals(property, "HarvestB", StringComparison.OrdinalIgnoreCase)) return "HarvestB";
            if (string.Equals(property, "WaitPointA", StringComparison.OrdinalIgnoreCase)) return "WaitPointA";
            if (string.Equals(property, "WaitPointB", StringComparison.OrdinalIgnoreCase)) return "WaitPointB";
            return string.Empty;
        }

        // Mineral unit tag → resource label via the jit pair table (From/To resource rows).
        private static string ResolveResourceLabel(ulong resourceTag)
        {
            if (resourceTag == 0 || Globals.CurrentMapData?.MainJitPairReturnCalculations == null)
            {
                return string.Empty;
            }
            foreach (var pairList in Globals.CurrentMapData.MainJitPairReturnCalculations)
            {
                if (pairList == null)
                {
                    continue;
                }
                foreach (var pair in pairList)
                {
                    if (pair == null)
                    {
                        continue;
                    }
                    if (pair.FromResourceUnitTag == resourceTag && !string.IsNullOrWhiteSpace(pair.FromResourceLabel))
                    {
                        return pair.FromResourceLabel;
                    }
                    if (pair.ToResourceUnitTag == resourceTag && !string.IsNullOrWhiteSpace(pair.ToResourceLabel))
                    {
                        return pair.ToResourceLabel;
                    }
                }
            }
            return string.Empty;
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
            public string TargetUnitTag { get; set; }
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
            public string TargetUnitTag { get; set; }
            public ulong? OrderTargetUnitTag { get; set; }
        }

        private sealed class SmSweepSampleRecord
        {
            public string RecordType { get; set; }
            public string Bot { get; set; }
            public DateTime TimestampUtc { get; set; }
            public int GameFrame { get; set; }
            public double GameSeconds { get; set; }
            public string StartSide { get; set; }
            public string MineralLabel { get; set; }
            public ulong WorkerTag { get; set; }
            public double AngleDeg { get; set; }
            public double ApproachX { get; set; }
            public double ApproachY { get; set; }
            public double StandX { get; set; }
            public double StandY { get; set; }
            public double DistToHatcheryCircle1 { get; set; }
            public bool Valid { get; set; }
            public int StillFrames { get; set; }
            public int Ordinal { get; set; }
        }

        private sealed class SmStageSampleRecord
        {
            public string RecordType { get; set; }
            public string Bot { get; set; }
            public DateTime TimestampUtc { get; set; }
            public int GameFrame { get; set; }
            public double GameSeconds { get; set; }
            public string StartSide { get; set; }
            public string MineralLabel { get; set; }
            public ulong WorkerTag { get; set; }
            public string Phase { get; set; }        // "verify" | "tip" | "stage-hold" | "stage-smart"
            public double AngleDeg { get; set; }     // absolute approach angle (mineral->hatchery = 0)
            public double Radius { get; set; }       // staging radius from ideal stand (0 for tip/verify)
            public double BearingDeg { get; set; }   // staging bearing from stand (0 = toward hatchery)
            public double TargetX { get; set; }
            public double TargetY { get; set; }
            public double StandX { get; set; }
            public double StandY { get; set; }
            public double DistToIdeal { get; set; }
            public bool Held { get; set; }           // stage-hold: stayed put | stage-smart: hit the ideal stand
            public bool Harvested { get; set; }      // mineral contents dropped while standing
            public int MineralContentsBefore { get; set; }
            public int MineralContentsAfter { get; set; }
            public bool Valid { get; set; }
            public string Note { get; set; }
            public int Ordinal { get; set; }
        }

        private sealed class SmHandoffSampleRecord
        {
            public string RecordType { get; set; }
            public string Bot { get; set; }
            public DateTime TimestampUtc { get; set; }
            public int GameFrame { get; set; }
            public double GameSeconds { get; set; }
            public string StartSide { get; set; }
            public string MineralLabel { get; set; }
            public ulong WorkerTag { get; set; }
            public string Role { get; set; }         // "A" (harvester) | "B" (stager)
            public string Phase { get; set; }        // "move" | "stand" | "harvest" | "smart" | "hold" | "flip-return" | "handoff"
            public double X { get; set; }
            public double Y { get; set; }
            public double DistToIdeal { get; set; }  // stands: distance from the stored ideal stand
            public int HarvestFrames { get; set; }   // A: accumulated stationary+1183 frames at this event
            public string Note { get; set; }
            public int Ordinal { get; set; }
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
            public string WorkerLabel { get; set; }
            public string WorkerRole { get; set; }
            public string RoleNumber { get; set; }
            public string WorkerType { get; set; }            public float? WorkerX { get; set; }
            public float? WorkerY { get; set; }
            public List<int> CurrentOrderAbilityIds { get; set; }
            public int AbilityId { get; set; }
            public string Ability { get; set; }
            public bool QueueCommand { get; set; }
            public string TargetUnitTag { get; set; }
            public float? TargetX { get; set; }
            public float? TargetY { get; set; }
        }
    }
}
