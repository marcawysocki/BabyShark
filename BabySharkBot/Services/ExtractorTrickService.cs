using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using BabySharkBot.Setup;

namespace BabySharkBot.Services
{
    /// <summary>
    /// Coordinates the extractor-only worker route override.
    /// Permanent-building cancellation and return behavior does not belong here.
    /// </summary>
    public sealed class ExtractorTrickService
    {
        private const int RequiredDrones = 14;
        private const int StartMineralThreshold = 120;
        private const int ExtractorAndDroneMinerals = 125;
        private const float PositionTolerance = 0.75f;
        private const float HatcheryRadius = 2.75f;

        private readonly DefaultSharkyBot _defaultBot;
        private ExtractorRequest _request;

        private enum ExtractorPhase
        {
            Idle,
            TargetInserted,
            ReturnInstructionsLoaded,
            WaitingAtPrePosition,
            ExtractorConfirmed,
            LarvaMorphIssued,
            CancelIssued,
            Completed
        }

        private sealed class ExtractorRequest
        {
            public ulong WorkerTag { get; init; }
            public ulong GeyserTag { get; init; }
            public ulong ExtractorTag { get; set; }
            public int OriginalMti { get; init; }
            public int TemporaryTargetIndex { get; init; }
            public MiningTargetDto DisplacedTarget { get; init; }
            public MiningTargetDto TemporaryTarget { get; init; }
            public ulong LarvaTag { get; set; }
            public ExtractorPhase Phase { get; set; }
        }

        public ExtractorTrickService(DefaultSharkyBot defaultBot)
        {
            _defaultBot = defaultBot ?? throw new ArgumentNullException(nameof(defaultBot));
        }

        public void Reset()
        {
            _request = null;
        }

        public IEnumerable<SC2APIProtocol.Action> Update(
            ResponseObservation observation,
            MawBaseLocationData mapData,
            int startIndex)
        {
            var actions = new List<SC2APIProtocol.Action>();
            if (observation?.Observation?.RawData?.Units == null
                || mapData == null
                || startIndex < 0)
            {
                return actions;
            }

            if (_request == null)
            {
                TryCreateRequest(observation, mapData, startIndex);
            }

            if (_request == null)
            {
                return actions;
            }

            switch (_request.Phase)
            {
                case ExtractorPhase.WaitingAtPrePosition:
                    if (TryIssueExtractorBuild(observation, actions))
                    {
                        _request.Phase = ExtractorPhase.ExtractorConfirmed;
                    }
                    break;

                case ExtractorPhase.ExtractorConfirmed:
                    if (!IsExtractorUnderConstruction(observation, _request.ExtractorTag))
                    {
                        break;
                    }

                    if (TryIssueLarvaMorph(observation, actions))
                    {
                        _request.Phase = ExtractorPhase.LarvaMorphIssued;
                    }
                    break;

                case ExtractorPhase.LarvaMorphIssued:
                    if (HasConfirmedDroneMorph(observation))
                    {
                        if (TryIssueExtractorCancel(actions))
                        {
                            _request.Phase = ExtractorPhase.CancelIssued;
                        }
                    }
                    break;

                case ExtractorPhase.CancelIssued:
                    if (!IsExtractorUnderConstruction(observation, _request.ExtractorTag))
                    {
                        var restoreAction = RestoreMining(observation, mapData, startIndex);
                        if (restoreAction != null)
                        {
                            actions.Add(restoreAction);
                            _request.Phase = ExtractorPhase.Completed;
                        }
                    }
                    break;
            }

            return actions;
        }

        public bool TryLoadPrepMr(AssignedWorkerDto assignedWorker, int frame)
        {
            if (_request == null
                || _request.Phase != ExtractorPhase.TargetInserted
                || assignedWorker == null
                || assignedWorker.UnitID != _request.WorkerTag
                || !Settings.RuntimeWorkers.TryGetValue(_request.WorkerTag, out var runtimeWorker)
                || runtimeWorker.PreviousAbilityId != (int)Abilities.HARVEST_GATHER_DRONE
                || runtimeWorker.CurrentAbilityId != (int)Abilities.HARVEST_RETURN_DRONE)
            {
                return false;
            }

            var target = assignedWorker.MiningTargets.ElementAtOrDefault(_request.TemporaryTargetIndex);
            if (target == null || target.ResourceUnitId != _request.GeyserTag)
            {
                return false;
            }

            runtimeWorker.LoadInstructions("prepMR", new[]
            {
                CreateMoveInstruction("prepMR", target.ResourceUnitId, WorkerInstructionPoint.Return, 0),
                CreateMoveInstruction("prepMR", target.ResourceUnitId, WorkerInstructionPoint.Return, 1),
                CreateMoveInstruction("prepMR", target.ResourceUnitId, WorkerInstructionPoint.Return, 14),
                new WorkerInstruction
                {
                    InstructionSet = "prepMR",
                    Command = WorkerInstructionCommand.Return,
                    Point = WorkerInstructionPoint.Return,
                    TargetId = target.ResourceUnitId,
                    RelativeFrame = 15,
                    Queue = true
                }
            }, Settings.GetRelativeFrame(frame));

            TraceBuildInstructionInsertion("prepMR", _request.WorkerTag, frame, runtimeWorker.Instructions.Count);
            _request.Phase = ExtractorPhase.ReturnInstructionsLoaded;
            Console.WriteLine($"[EXTRACTOR PREPMR] worker={_request.WorkerTag} geyser={_request.GeyserTag} frame={frame}");
            return true;
        }

        public bool TryLoadPrepW(AssignedWorkerDto assignedWorker, int frame)
        {
            if (_request == null
                || _request.Phase != ExtractorPhase.ReturnInstructionsLoaded
                || assignedWorker == null
                || assignedWorker.UnitID != _request.WorkerTag
                || !Settings.RuntimeWorkers.TryGetValue(_request.WorkerTag, out var runtimeWorker))
            {
                return false;
            }

            assignedWorker.Mti = _request.TemporaryTargetIndex;
            var target = assignedWorker.MiningTargets.ElementAtOrDefault(_request.TemporaryTargetIndex);
            if (target == null || target.ResourceUnitId != _request.GeyserTag)
            {
                return false;
            }

            runtimeWorker.LoadInstructions("prepW", new[]
            {
                CreateMoveInstruction("prepW", target.ResourceUnitId, WorkerInstructionPoint.Harvest, 0),
                CreateMoveInstruction("prepW", target.ResourceUnitId, WorkerInstructionPoint.Harvest, 1),
                CreateMoveInstruction("prepW", target.ResourceUnitId, WorkerInstructionPoint.Harvest, 14)
            }, Settings.GetRelativeFrame(frame));

            TraceBuildInstructionInsertion("prepW", _request.WorkerTag, frame, runtimeWorker.Instructions.Count);
            _request.Phase = ExtractorPhase.WaitingAtPrePosition;
            Console.WriteLine($"[EXTRACTOR PREPW] worker={_request.WorkerTag} point=({target.HarvestPoint.X:F2},{target.HarvestPoint.Y:F2}) frame={frame}");
            return true;
        }

        private void TryCreateRequest(ResponseObservation observation, MawBaseLocationData mapData, int startIndex)
        {
            var selfUnits = observation.Observation.RawData.Units
                .Where(unit => unit != null && unit.Alliance == Alliance.Self)
                .ToList();
            var droneCount = selfUnits.Count(unit => unit.UnitType == (uint)UnitTypes.ZERG_DRONE);
            var pendingDrones = selfUnits.Count(unit =>
                unit.UnitType == (uint)UnitTypes.ZERG_LARVA
                && unit.Orders.Any(order => order.AbilityId == (uint)Abilities.TRAIN_DRONE));

            if (droneCount + pendingDrones != RequiredDrones
                || _defaultBot.MacroData == null
                || _defaultBot.MacroData.Minerals < StartMineralThreshold)
            {
                return;
            }

            var va = mapData.OrderedMainVespene?.ElementAtOrDefault(startIndex)?
                .FirstOrDefault(candidate => candidate != null
                    && string.Equals(candidate.Label, "VA", StringComparison.OrdinalIgnoreCase));
            if (va?.Position == null || !HasPoint(va.HarvestPoint))
            {
                return;
            }

            var liveVa = observation.Observation.RawData.Units.FirstOrDefault(unit =>
                unit != null
                && unit.Alliance == Alliance.Neutral
                && unit.Tag != 0
                && IsGas(unit.UnitType)
                && DistanceSquared(unit.Pos.X, unit.Pos.Y, va.Position.X, va.Position.Y) <= 0.01f);
            if (liveVa == null)
            {
                return;
            }

            var assignedWorkers = mapData.AssignedWorkers?.ElementAtOrDefault(startIndex);
            var townhall = observation.Observation.RawData.Units.FirstOrDefault(unit =>
                unit != null
                && unit.Alliance == Alliance.Self
                && unit.UnitType == (uint)UnitTypes.ZERG_HATCHERY);
            if (assignedWorkers == null || townhall == null)
            {
                return;
            }

            var candidates = new List<(AssignedWorkerDto Worker, float Cost, Vector2Dto ReturnPoint)>();
            foreach (var assignedWorker in assignedWorkers)
            {
                if (assignedWorker?.UnitID == 0
                    || assignedWorker.MiningTargets == null
                    || assignedWorker.MiningTargets.Count < 2
                    || !observation.Observation.RawData.Units.Any(unit => unit != null && unit.Tag == assignedWorker.UnitID))
                {
                    continue;
                }

                var observedWorker = selfUnits.FirstOrDefault(unit => unit.Tag == assignedWorker.UnitID);
                if (observedWorker == null
                    || observedWorker.Orders?.FirstOrDefault()?.AbilityId != (uint)Abilities.HARVEST_GATHER_DRONE
                    || observedWorker.Pos == null)
                {
                    continue;
                }

                var currentMti = assignedWorker.Mti;
                if (currentMti < 0 || currentMti >= assignedWorker.MiningTargets.Count)
                {
                    continue;
                }

                var nextIndex = (currentMti + 1) % assignedWorker.MiningTargets.Count;
                var displacedTarget = assignedWorker.MiningTargets[nextIndex];
                if (displacedTarget == null || displacedTarget.ResourceUnitId == 0)
                {
                    continue;
                }

                var returnPoint = CalculateReturnPoint(
                    observedWorker.Pos.X,
                    observedWorker.Pos.Y,
                    townhall.Pos.X,
                    townhall.Pos.Y,
                    va.HarvestPoint.X,
                    va.HarvestPoint.Y,
                    townhall.Pos.Z);
                if (!HasPoint(returnPoint))
                {
                    continue;
                }

                var cost = Distance(observedWorker.Pos.X, observedWorker.Pos.Y, returnPoint.X, returnPoint.Y)
                    + Distance(returnPoint.X, returnPoint.Y, va.HarvestPoint.X, va.HarvestPoint.Y);
                candidates.Add((assignedWorker, cost, returnPoint));
            }

            var selected = candidates
                .OrderBy(candidate => candidate.Cost)
                .ThenBy(candidate => candidate.Worker.UnitID)
                .FirstOrDefault();
            if (selected.Worker == null)
            {
                return;
            }

            var selectedWorker = selected.Worker;
            var temporaryTargetIndex = (selectedWorker.Mti + 1) % selectedWorker.MiningTargets.Count;
            var displaced = selectedWorker.MiningTargets[temporaryTargetIndex];
            var temporaryTarget = new MiningTargetDto
            {
                ResourceLabel = "VA",
                FromResourceLabel = selectedWorker.MiningTargets[selectedWorker.Mti].ToResourceLabel,
                ToResourceLabel = "VA",
                ResourceUnitId = liveVa.Tag,
                TownHallUnitId = selectedWorker.TownHallUnitID,
                ResourcePosition = new Vector2Dto(va.Position.X, va.Position.Y, va.Position.Z),
                HarvestPoint = new Vector2Dto(va.HarvestPoint.X, va.HarvestPoint.Y, va.HarvestPoint.Z),
                SmHarvestPoint = new Vector2Dto(va.HarvestPoint.X, va.HarvestPoint.Y, va.HarvestPoint.Z),
                ReturnPoint = selected.ReturnPoint,
                SmReturnPoint = selected.ReturnPoint,
                IsABSwitch = false,
                IsInitialMineralAssignment = false
            };

            selectedWorker.MiningTargets[temporaryTargetIndex] = temporaryTarget;
            _request = new ExtractorRequest
            {
                WorkerTag = selectedWorker.UnitID,
                GeyserTag = liveVa.Tag,
                OriginalMti = selectedWorker.Mti,
                TemporaryTargetIndex = temporaryTargetIndex,
                DisplacedTarget = displaced,
                TemporaryTarget = temporaryTarget,
                Phase = ExtractorPhase.TargetInserted
            };

            Console.WriteLine($"[EXTRACTOR TARGET INSERTED] worker={selectedWorker.UnitID} mti={selectedWorker.Mti} slot={temporaryTargetIndex} displaced={displaced.ToResourceLabel} cost={selected.Cost:F2}");
        }

        private bool TryIssueExtractorBuild(ResponseObservation observation, List<SC2APIProtocol.Action> actions)
        {
            if (_request == null || _defaultBot.MacroData.Minerals < ExtractorAndDroneMinerals)
            {
                return false;
            }

            var worker = observation.Observation.RawData.Units.FirstOrDefault(unit => unit.Tag == _request.WorkerTag);
            var larvaAvailable = observation.Observation.RawData.Units.Any(unit =>
                unit != null
                && unit.Alliance == Alliance.Self
                && unit.UnitType == (uint)UnitTypes.ZERG_LARVA
                && unit.Orders.Count == 0);
            if (worker == null
                || !larvaAvailable
                || !HasPoint(_request.TemporaryTarget.HarvestPoint)
                || DistanceSquared(worker.Pos.X, worker.Pos.Y, _request.TemporaryTarget.HarvestPoint.X, _request.TemporaryTarget.HarvestPoint.Y) > PositionTolerance * PositionTolerance)
            {
                return false;
            }

            actions.Add(CreateUnitCommand(
                Abilities.BUILD_EXTRACTOR,
                _request.WorkerTag,
                _request.GeyserTag,
                null));
            Console.WriteLine($"[EXTRACTOR BUILD] worker={_request.WorkerTag} geyser={_request.GeyserTag} minerals={_defaultBot.MacroData.Minerals}");
            return true;
        }

        private bool TryIssueLarvaMorph(ResponseObservation observation, List<SC2APIProtocol.Action> actions)
        {
            if (_request == null
                || _defaultBot.MacroData.Minerals < 50
                || _defaultBot.MacroData.FoodLeft < 1)
            {
                return false;
            }

            var larva = observation.Observation.RawData.Units.FirstOrDefault(unit =>
                unit != null
                && unit.Alliance == Alliance.Self
                && unit.UnitType == (uint)UnitTypes.ZERG_LARVA
                && unit.Orders.Count == 0);
            if (larva == null)
            {
                return false;
            }

            _request.LarvaTag = larva.Tag;
            actions.Add(CreateUnitCommand(Abilities.TRAIN_DRONE, larva.Tag, null, null));
            Console.WriteLine($"[EXTRACTOR DRONE MORPH] larva={larva.Tag} worker={_request.WorkerTag}");
            return true;
        }

        private bool HasConfirmedDroneMorph(ResponseObservation observation)
        {
            return _request != null
                && _request.LarvaTag != 0
                && observation.Observation.RawData.Units.Any(unit =>
                    unit != null
                    && unit.Tag == _request.LarvaTag
                    && unit.Orders.Any(order => order.AbilityId == (uint)Abilities.TRAIN_DRONE));
        }

        private bool TryIssueExtractorCancel(List<SC2APIProtocol.Action> actions)
        {
            if (_request == null)
            {
                return false;
            }

            if (_request.ExtractorTag == 0)
            {
                return false;
            }

            actions.Add(CreateUnitCommand(Abilities.CANCEL_BUILDINPROGRESS, _request.ExtractorTag, null, null));
            Console.WriteLine($"[EXTRACTOR CANCEL] extractor={_request.ExtractorTag} worker={_request.WorkerTag}");
            return true;
        }

        private SC2APIProtocol.Action RestoreMining(
            ResponseObservation observation,
            MawBaseLocationData mapData,
            int startIndex)
        {
            var assignedWorker = mapData.AssignedWorkers?.ElementAtOrDefault(startIndex)?
                .FirstOrDefault(worker => worker?.UnitID == _request.WorkerTag);
            if (assignedWorker == null
                || assignedWorker.MiningTargets == null
                || _request.TemporaryTargetIndex < 0
                || _request.TemporaryTargetIndex >= assignedWorker.MiningTargets.Count)
            {
                return null;
            }

            if (!Settings.RuntimeWorkers.TryGetValue(_request.WorkerTag, out var runtimeWorker)
                || _request.DisplacedTarget == null
                || !HasPoint(_request.DisplacedTarget.HarvestPoint))
            {
                return null;
            }

            var target = _request.DisplacedTarget;
            assignedWorker.MiningTargets[_request.TemporaryTargetIndex] = target;
            assignedWorker.Mti = _request.TemporaryTargetIndex;
            runtimeWorker.LoadInstructions("jitMH", new[]
            {
                CreateMoveInstruction("jitMH", target.ResourceUnitId, WorkerInstructionPoint.Harvest, 0),
                CreateMoveInstruction("jitMH", target.ResourceUnitId, WorkerInstructionPoint.Harvest, 1),
                CreateMoveInstruction("jitMH", target.ResourceUnitId, WorkerInstructionPoint.Harvest, 14),
                new WorkerInstruction
                {
                    InstructionSet = "jitMH",
                    Command = WorkerInstructionCommand.Gather,
                    Point = WorkerInstructionPoint.Harvest,
                    TargetId = target.ResourceUnitId,
                    RelativeFrame = 15,
                    Queue = true
                }
            }, Settings.GetRelativeFrame((int)observation.Observation.GameLoop));

            runtimeWorker.CurInstrIdx = 1;
            TraceBuildInstructionInsertion("jitMH", _request.WorkerTag, (int)observation.Observation.GameLoop, runtimeWorker.Instructions.Count);
            Console.WriteLine($"[EXTRACTOR RESTORE MINING] worker={_request.WorkerTag} target={target.ToResourceLabel} jitMH+0");
            return null;
        }

        private bool IsExtractorUnderConstruction(ResponseObservation observation, ulong extractorTag)
        {
            var extractor = observation.Observation.RawData.Units.FirstOrDefault(unit =>
                unit != null
                && unit.Alliance == Alliance.Self
                && unit.UnitType == (uint)UnitTypes.ZERG_EXTRACTOR
                && unit.BuildProgress < 1.0f
                && (extractorTag == 0 || unit.Tag == extractorTag || DistanceSquared(unit.Pos.X, unit.Pos.Y, _request.TemporaryTarget.ResourcePosition.X, _request.TemporaryTarget.ResourcePosition.Y) <= 1.0f));
            if (extractor != null && _request.ExtractorTag == 0)
            {
                _request.ExtractorTag = extractor.Tag;
            }

            return extractor != null;
        }

        private static void TraceBuildInstructionInsertion(string instructionSet, ulong workerTag, int frame, int instructionCount)
        {
            Console.WriteLine($"[BUILD INSTRUCTION INSERTED] set={instructionSet} worker={workerTag} frame={frame} count={instructionCount}");
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
        }

        private static WorkerInstruction CreateMoveInstruction(string set, ulong targetId, WorkerInstructionPoint point, int frame)
        {
            return new WorkerInstruction
            {
                InstructionSet = set,
                Command = WorkerInstructionCommand.Move,
                Point = point,
                TargetId = targetId,
                RelativeFrame = frame
            };
        }

        private static SC2APIProtocol.Action CreateUnitCommand(Abilities ability, ulong unitTag, ulong? targetUnitTag, Point2D targetPoint)
        {
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)ability,
                QueueCommand = false
            };
            command.UnitTags.Add(unitTag);
            if (targetUnitTag.HasValue)
            {
                command.TargetUnitTag = targetUnitTag.Value;
            }
            if (targetPoint != null)
            {
                command.TargetWorldSpacePos = targetPoint;
            }

            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        private static bool IsGas(uint unitType)
        {
            return unitType == (uint)UnitTypes.NEUTRAL_VESPENEGEYSER
                || unitType == (uint)UnitTypes.NEUTRAL_RICHVESPENEGEYSER
                || unitType == (uint)UnitTypes.NEUTRAL_SPACEPLATFORMGEYSER;
        }

        private static bool HasPoint(Vector2Dto point)
        {
            return point != null && (point.X != 0f || point.Y != 0f);
        }

        private static float Distance(float x0, float y0, float x1, float y1)
        {
            return MathF.Sqrt(DistanceSquared(x0, y0, x1, y1));
        }

        private static float DistanceSquared(float x0, float y0, float x1, float y1)
        {
            var dx = x0 - x1;
            var dy = y0 - y1;
            return dx * dx + dy * dy;
        }

        private static Vector2Dto CalculateReturnPoint(
            float workerX,
            float workerY,
            float hatcheryX,
            float hatcheryY,
            float targetX,
            float targetY,
            float z)
        {
            var bestPoint = new Vector2Dto();
            var bestCost = float.MaxValue;
            const int samples = 72;
            for (var index = 0; index < samples; index++)
            {
                var angle = MathF.PI * 2f * index / samples;
                var candidateX = hatcheryX + MathF.Cos(angle) * HatcheryRadius;
                var candidateY = hatcheryY + MathF.Sin(angle) * HatcheryRadius;
                var cost = Distance(workerX, workerY, candidateX, candidateY)
                    + Distance(candidateX, candidateY, targetX, targetY);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestPoint = new Vector2Dto(candidateX, candidateY, z);
                }
            }

            return bestCost < float.MaxValue ? bestPoint : null;
        }
    }
}
