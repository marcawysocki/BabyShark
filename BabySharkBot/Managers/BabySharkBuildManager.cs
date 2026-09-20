
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SC2APIProtocol;
using Sharky;
using Sharky.Managers;
using Sharky.DefaultBot;
using BabySharkBot.Services;
using BabySharkBot.Setup;
using System.Linq;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Manager that executes BabyShark builds. Replaces Sharky's BuildManager + ProductionCommander pipeline.
    /// Integrates with DefaultSharkyBot's MacroData to express production desires.
    /// Registered after ObservationManager so build decisions consume the current observation snapshot.
    /// </summary>
    public class BabySharkBuildManager : IManager
    {
        public bool NeverSkip { get; set; } = true;
        public bool SkipFrame { get; set; } = false;
        public double LongestFrame { get; set; } = 0;
        public double TotalFrameTime { get; set; } = 0;

        private readonly DefaultSharkyBot _defaultBot;
        private BabySharkBot.Builds.BabySharkBuild? _activeBuild;
        private WorkerLabelService _workerLabelService;
        private MineralLabelService _mineralLabelService;
        private VespeneLabelService _vespeneLabelService;
        private SpawningPoolPlacementService _spawningPoolPlacementService;
        private bool _started;
        private bool _labelsInitialized;
        private int _greedyChainStartIndex = -1;
        private int _greedyChainWorkerCount = -1;
        private bool _initialAssignmentLatched;
        private bool _workerCountTransitionApplied;
        private int _lastObservedWorkerCount = -1;
        private bool _ccawStartupSummaryLogged;
        private readonly HashSet<ulong> _jitMhStartupLogs = new HashSet<ulong>();
        private readonly HashSet<ulong> _ccawStartupLogs = new HashSet<ulong>();
        private readonly HashSet<ulong> _bumpStartupLogs = new HashSet<ulong>();
        private readonly OngoingMapData _ongoingMapData = new OngoingMapData();
        private bool _frameZeroInstructionListsWritten;

        public BabySharkBuildManager(DefaultSharkyBot defaultBot)
        {
            _defaultBot = defaultBot;
        }

        public void SetBuild(BabySharkBot.Builds.BabySharkBuild build)
        {
            _activeBuild = build;
            _started = false;
            _labelsInitialized = false;
            _greedyChainStartIndex = -1;
            _greedyChainWorkerCount = -1;
            _initialAssignmentLatched = false;
            _workerCountTransitionApplied = false;
            _lastObservedWorkerCount = -1;
            _ccawStartupSummaryLogged = false;
            _frameZeroInstructionListsWritten = false;
            _jitMhStartupLogs.Clear();
            _ccawStartupLogs.Clear();
            _bumpStartupLogs.Clear();
        }

        public void ConfigureLabelServices(
            WorkerLabelService workerLabelService,
            MineralLabelService mineralLabelService,
            VespeneLabelService vespeneLabelService,
            SpawningPoolPlacementService spawningPoolPlacementService)
        {
            _workerLabelService = workerLabelService;
            _activeBuild?.ConfigureWorkerLabelService(workerLabelService);
            _mineralLabelService = mineralLabelService;
            _vespeneLabelService = vespeneLabelService;
            _spawningPoolPlacementService = spawningPoolPlacementService;
            _labelsInitialized = false;
            _greedyChainWorkerCount = -1;
            _initialAssignmentLatched = false;
            _workerCountTransitionApplied = false;
            _lastObservedWorkerCount = -1;
            _ccawStartupSummaryLogged = false;
            _frameZeroInstructionListsWritten = false;
            _jitMhStartupLogs.Clear();
            _ccawStartupLogs.Clear();
            _bumpStartupLogs.Clear();
        }

        public BabySharkBot.Builds.BabySharkBuild? ActiveBuild => _activeBuild;

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var frame = observation?.Observation == null
                ? -1
                : (int)observation.Observation.GameLoop;
            Console.WriteLine($"[BUILD MANAGER ONFRAME ENTER] frame={frame} activeBuild={_activeBuild?.BuildName ?? "<null>"} started={_started} skip={SkipFrame} neverSkip={NeverSkip}");
            if (Debugger.IsAttached)
            {
                //Debugger.Break();
            }

            if (_activeBuild == null)
            {
                Console.WriteLine($"[BUILD MANAGER ONFRAME EXIT] frame={frame} reason=activeBuild-null");
                return Array.Empty<SC2APIProtocol.Action>();
            }
            var chainRebuilt = BuildGreedyMineralChainFromObservation(frame);
            if (chainRebuilt)
            {
                // Wait points must exist before the pair table reads them (C7 ordering fix).
                CalculateCcaWaitPointsForCurrentSpawn();
                var startIndexForPairs = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                var pairTownhall = Globals.CurrentMapData?.StartingTownHall?.ElementAtOrDefault(startIndexForPairs);
                if (pairTownhall != null)
                {
                    PopulateJitPairReturnCalculations(Globals.CurrentMapData, startIndexForPairs, pairTownhall);
                }
            }
            _ongoingMapData.RefreshMiningData(
                null,
                observation,
                Globals.CurrentMapData,
                _workerLabelService,
                null,
                _mineralLabelService,
                _vespeneLabelService);
            UpdateRuntimeLabels();
            SeedRuntimeWorkerInstructions(frame);
            if (frame == 0 && !_frameZeroInstructionListsWritten)
            {
                WriteFrameZeroInstructionLists();
                _frameZeroInstructionListsWritten = true;
            }

            if (!_started)
            {
                _activeBuild.OnStart(frame);
                _started = true;
            }

            Console.WriteLine($"[BUILD MANAGER CALLING BUILD] frame={frame} build={_activeBuild.BuildName} started={_started}");
            if (Debugger.IsAttached)
            {
                //Debugger.Break();
            }

            var actions = _activeBuild.OnFrame(observation) ?? Array.Empty<SC2APIProtocol.Action>();

            Console.WriteLine($"[BUILD MANAGER BUILD RETURNED] frame={frame} build={_activeBuild.BuildName} actions={actions.Count()}");

            if (_activeBuild.ShouldTransition(frame))
            {
                Console.WriteLine($"BabySharkBuildManager: Build {_activeBuild.BuildName} requested transition at frame {frame}");
            }

            return actions;
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            if (_activeBuild == null || _started)
            {
                return;
            }

            var frame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            BuildGreedyMineralChainFromObservation(frame);
            CalculateCcaWaitPointsForCurrentSpawn();
            var pairTownhall = Globals.CurrentMapData?.StartingTownHall?.ElementAtOrDefault(startIndex);
            if (pairTownhall != null)
            {
                PopulateJitPairReturnCalculations(Globals.CurrentMapData, startIndex, pairTownhall);
            }
            _ongoingMapData.RefreshMiningData(
                null,
                observation,
                Globals.CurrentMapData,
                _workerLabelService,
                null,
                _mineralLabelService,
                _vespeneLabelService);
            UpdateRuntimeLabels();
            _activeBuild.OnStart(frame);
            SeedRuntimeWorkerInstructions(frame);
            if (frame == 0 && !_frameZeroInstructionListsWritten)
            {
                WriteFrameZeroInstructionLists();
                _frameZeroInstructionListsWritten = true;
            }
            LogStartupInstructionSummary(startIndex);
            _started = true;
        }

        private void CalculateCcaWaitPointsForCurrentSpawn()
        {
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignments = Globals.CurrentMapData?.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            if (assignments == null || assignments.Count == 0)
            {
                return;
            }

            // Wait circle: 1.5u from the mineral center on the ray toward the hatchery.
            // Role 1 harvests at 1.0u (footprint); role 3 waits at 1.5u so it does not
            // interfere with role 1. Every mineral, A and B, carries a wait point.
            const float waitCircleRadius = 1.5f;

            foreach (var assignment in assignments)
            {
                if (assignment?.Minerals == null || assignment.Minerals.Count == 0)
                {
                    continue;
                }

                OrderedMineral aMineral = null;
                foreach (var mineral in assignment.Minerals)
                {
                    if (mineral == null || string.IsNullOrWhiteSpace(mineral.FinalLabel))
                    {
                        continue;
                    }

                    // A non-zero CcaWaitPoint in BaseDtos is authoritative; reuse it.
                    if (HasNonZeroPoint(mineral.CcaWaitPoint))
                    {
                        if (mineral.FinalLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase))
                        {
                            aMineral = mineral;
                        }
                        continue;
                    }

                    if (!HasNonZeroPoint(mineral.Position) || !HasNonZeroPoint(mineral.HarvestPoint))
                    {
                        continue;
                    }

                    var directionX = mineral.HarvestPoint.X - mineral.Position.X;
                    var directionY = mineral.HarvestPoint.Y - mineral.Position.Y;
                    var harvestDistance = MathF.Sqrt(directionX * directionX + directionY * directionY);
                    if (harvestDistance <= 0.0001f)
                    {
                        continue;
                    }

                    var scale = waitCircleRadius / harvestDistance;
                    mineral.CcaWaitPoint = new Vector2Dto(
                        mineral.Position.X + directionX * scale,
                        mineral.Position.Y + directionY * scale,
                        mineral.HarvestPoint.Z);

                    Console.WriteLine(
                        $"[BUILD START CCAW POINT CALCULATED] start={startIndex} team={assignment.TeamNumber} mineral={mineral.FinalLabel} " +
                        $"point=({mineral.CcaWaitPoint.X:F2},{mineral.CcaWaitPoint.Y:F2}) radius=1.5 stored=BaseDtos");

                    if (mineral.FinalLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase))
                    {
                        aMineral = mineral;
                    }
                }

                // JitWaitPoint remains the team's CCAw storage (Staging / BumpCcaWaitCircle read it).
                if (aMineral != null && HasNonZeroPoint(aMineral.CcaWaitPoint))
                {
                    assignment.JitWaitPoint = aMineral.CcaWaitPoint;
                }
            }
        }

        private void SeedRuntimeWorkerInstructions(int frame)
        {
            var snapshot = Globals.CurrentObservation;
            if (snapshot?.SelfUnits == null || Globals.CurrentMapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignments = Globals.CurrentMapData.TeamPatchAssignments.ElementAtOrDefault(startIndex);
            if (assignments == null)
            {
                return;
            }

            foreach (var assignment in assignments)
            {
                foreach (var assignedWorker in assignment?.Workers ?? new List<WorkerEntryDto>())
                {
                    if (assignedWorker == null || assignedWorker.UnitTag == 0 || !snapshot.SelfUnits.ContainsKey(assignedWorker.UnitTag))
                    {
                        continue;
                    }

                    if (Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitTag, out var runtimeWorker) && runtimeWorker.Instructions.Count > 0)
                    {
                        continue;
                    }

                    var role = assignedWorker.FinalLabel ?? assignedWorker.Label ?? string.Empty;
                    var initialTarget = Globals.CurrentMapData.AssignedWorkers.ElementAtOrDefault(startIndex)?
                        .FirstOrDefault(worker => worker?.UnitID == assignedWorker.UnitTag)?
                        .MiningTargets?.FirstOrDefault();
                    if (initialTarget == null || initialTarget.ResourceUnitId == 0)
                    {
                        continue;
                    }

                    runtimeWorker ??= new RuntimeWorkerState { UnitTag = assignedWorker.UnitTag };
                    // Preserve existing startup behavior: non-Magannatha role 3 workers use CCAw.
                    // Magannatha first trip: T3/Y3 use dedicated bump sets; T1/Y1 use one-time jitMHb; S3/B3 stay CCAw;
                    // all remaining workers stay jitMH. Bump workers hand off to existing jitMR/jitMH after SMART.
                    var roleThree = role.EndsWith("3", StringComparison.OrdinalIgnoreCase);
                    var isMagannathaBumpRole = Settings.IsMagannatha12WorkerOverride
                        && (string.Equals(role, "T3", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(role, "Y3", StringComparison.OrdinalIgnoreCase));
                    var magannathaCcawRole = Settings.IsMagannatha12WorkerOverride
                        && (string.Equals(role, "S3", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(role, "B3", StringComparison.OrdinalIgnoreCase));
                    var useCcaw = roleThree && (!Settings.IsMagannatha12WorkerOverride || magannathaCcawRole);
                    var isMagannathaJitMhBRole = Settings.IsMagannatha12WorkerOverride
                        && (string.Equals(role, "T1", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(role, "Y1", StringComparison.OrdinalIgnoreCase));
                    var instructionSet = isMagannathaBumpRole
                        ? $"bump{role}"
                        : isMagannathaJitMhBRole ? "jitMHb"
                        : useCcaw ? "CCAw" : "jitMH";
                    var movementPoint = isMagannathaBumpRole
                        ? ResolveBumpStartupPoint(role)
                        : useCcaw ? WorkerInstructionPoint.Staging : WorkerInstructionPoint.Harvest;
                    var gatherFrame = useCcaw ? 55 : 15;
                    var startupInstructions = isMagannathaBumpRole
                        ? BuildBumpInstructions(instructionSet, role, initialTarget.ResourceUnitId)
                        : BuildStandardStartupInstructions(instructionSet, movementPoint, initialTarget.ResourceUnitId, gatherFrame);
                    var instructionList = BuildMineralInstructionList(
                        startIndex,
                        assignedWorker,
                        instructionSet,
                        startupInstructions);
                    runtimeWorker.LoadInstructions(instructionSet, instructionList, Settings.GetRelativeFrame(frame));
                    Settings.RuntimeWorkers[assignedWorker.UnitTag] = runtimeWorker;
                    var loaded = Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitTag, out var loadedWorker)
                        && loadedWorker.Instructions.Count > 0
                        && string.Equals(loadedWorker.Instructions[0].InstructionSet, instructionSet, StringComparison.OrdinalIgnoreCase);
                    if (!loaded)
                    {
                        continue;
                    }

                    if (isMagannathaBumpRole)
                    {
                        _bumpStartupLogs.Add(assignedWorker.UnitTag);
                        Console.WriteLine($"[BUILD START INSTRUCTION LOADED] worker={assignedWorker.UnitTag} role={role} set={instructionSet} target={initialTarget.ResourceUnitId}");
                    }
                    else if (useCcaw)
                    {
                        _ccawStartupLogs.Add(assignedWorker.UnitTag);
                    }
                    else
                    {
                        _jitMhStartupLogs.Add(assignedWorker.UnitTag);
                        Console.WriteLine($"[BUILD START INSTRUCTION LOADED] worker={assignedWorker.UnitTag} role={role} set={instructionSet} target={initialTarget.ResourceUnitId}");
                    }
                }
            }
        }

        private static void WriteFrameZeroInstructionLists()
        {
            try
            {
                var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "mining_tests");
                Directory.CreateDirectory(folder);
                var filePath = Path.Combine(folder, $"worker_instruction_lists_frame0_{DateTime.Now:yyyyMMddHHmmss}.json");
                var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                var workers = Globals.CurrentMapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                    ?? new List<AssignedWorkerDto>();
                var output = new
                {
                    RecordType = "WorkerInstructionListsFrameZero",
                    TimestampUtc = DateTime.UtcNow,
                    GameFrame = 0,
                    StartIndex = startIndex,
                    Workers = workers
                        .Where(worker => worker != null && worker.UnitID != 0 && Settings.RuntimeWorkers.TryGetValue(worker.UnitID, out _))
                        .Select(worker =>
                        {
                            var runtimeWorker = Settings.RuntimeWorkers[worker.UnitID];
                            return new
                            {
                                WorkerTag = worker.UnitID,
                                WorkerLabel = worker.Role,
                                CurrentInstructionIndex = runtimeWorker.CurInstrIdx,
                                InstructionStartFrame = runtimeWorker.InstructionStartFrame,
                                CurrentTargetIndex = worker.Mti,
                                Instructions = runtimeWorker.Instructions.Select((instruction, index) => new
                                {
                                    Index = index,
                                    Set = instruction?.InstructionSet ?? string.Empty,
                                    Command = instruction?.Command.ToString() ?? string.Empty,
                                    Point = instruction?.Point.ToString() ?? string.Empty,
                                    TargetId = instruction?.TargetId ?? 0,
                                    RelativeFrame = instruction?.RelativeFrame ?? -1,
                                    PreviousAbilityId = instruction?.PreviousAbilityId ?? -1,
                                    TargetAbilityId = instruction?.TargetAbilityId ?? -1,
                                     NextTargetIndex = instruction?.NextTargetIndex ?? -1,
                                     JumpToInstructionIndex = instruction?.JumpToInstructionIndex ?? -1,
                                     Queue = instruction?.Queue ?? false,
                                     NoCondition = instruction?.NoCondition ?? false,
                                     StoreTargetPoint = instruction?.StoreTargetPoint ?? false,
                                     TargetPointReference = instruction?.TargetPointReference ?? string.Empty,
                                     PositionTolerance = instruction?.PositionTolerance ?? 0f
                                }).ToList()
                            };
                        }).ToList()
                };
                var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                Console.WriteLine($"[BUILD FRAME ZERO INSTRUCTION LISTS] file={filePath} start={startIndex} workers={output.Workers.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BUILD FRAME ZERO INSTRUCTION LISTS] write failed reason={ex.Message}");
            }
        }

        private static IReadOnlyList<WorkerInstruction> BuildStandardStartupInstructions(
            string instructionSet,
            WorkerInstructionPoint movementPoint,
            ulong targetId,
            int gatherFrame)
        {
            var startupMovementPoint = movementPoint == WorkerInstructionPoint.Harvest
                ? WorkerInstructionPoint.StoredTarget
                : movementPoint;
            return new[]
            {
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = startupMovementPoint, TargetId = targetId, RelativeFrame = 0 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = startupMovementPoint, TargetId = targetId, RelativeFrame = 1 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = startupMovementPoint, TargetId = targetId, RelativeFrame = 14 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Gather, Point = WorkerInstructionPoint.Harvest, TargetId = targetId, RelativeFrame = gatherFrame, Queue = true }
            };
        }

        private static IReadOnlyList<WorkerInstruction> BuildBumpInstructions(string instructionSet, string role, ulong targetId)
        {
            var isRoleThree = role.EndsWith("3", StringComparison.OrdinalIgnoreCase);
            var movementPoint = isRoleThree ? WorkerInstructionPoint.BumpPartner : WorkerInstructionPoint.BumpMidpoint;
            var instructions = new List<WorkerInstruction>();
            for (var relativeFrame = 0; relativeFrame <= 14; relativeFrame++)
            {
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Move,
                    Point = movementPoint,
                    TargetId = targetId,
                    RelativeFrame = relativeFrame,
                    Queue = false
                });
            }

            if (!isRoleThree)
            {
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.MoveAndGather,
                    Point = WorkerInstructionPoint.BumpHarvestCircle,
                    TargetId = targetId,
                    RelativeFrame = 15,
                    Queue = false
                });
                return instructions;
            }

            instructions.Add(new WorkerInstruction
            {
                InstructionSet = instructionSet,
                Command = WorkerInstructionCommand.Move,
                Point = WorkerInstructionPoint.BumpCcaWaitCircle,
                TargetId = targetId,
                RelativeFrame = 15,
                Queue = false
            });
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = instructionSet,
                Command = WorkerInstructionCommand.Gather,
                Point = WorkerInstructionPoint.Harvest,
                TargetId = targetId,
                RelativeFrame = 52,
                Queue = true
            });
            return instructions;
        }

        private static List<WorkerInstruction> BuildMineralInstructionList(
            int startIndex,
            WorkerEntryDto worker,
            string startupInstructionSet,
            IReadOnlyList<WorkerInstruction> startupInstructions)
        {
            var assignedWorker = Globals.CurrentMapData?.AssignedWorkers?
                .ElementAtOrDefault(startIndex)?
                .FirstOrDefault(candidate => candidate?.UnitID == worker.UnitTag);
            var targets = assignedWorker?.MiningTargets ?? new List<MiningTargetDto>();
            if (targets.Count < 2)
            {
                return startupInstructions.ToList();
            }

            var firstTarget = targets[0];
            var secondTarget = targets[1];
            var instructions = new List<WorkerInstruction>();
            var workerRole = worker.FinalLabel ?? worker.Label ?? worker.StartLabel ?? string.Empty;
            var firstLinePoint = ResolveFirstLinePoint(startupInstructionSet, workerRole);
            if (firstLinePoint != WorkerInstructionPoint.None)
            {
                AddJitPairSetupInstruction(instructions, startIndex, startupInstructionSet, firstTarget, secondTarget, firstLinePoint, WorkerInstructionCommand.StoreTargetPoint, true);
            }
            instructions.AddRange(startupInstructions);

            var firstGatherAbility = (int)Abilities.HARVEST_GATHER_DRONE;
            var returnAbility = (int)Abilities.HARVEST_RETURN_DRONE;
            var moveAbility = (int)Abilities.MOVE;

            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = firstGatherAbility,
                TargetAbilityId = returnAbility
            });
            AddReturnInstructions(instructions, startIndex, firstTarget, secondTarget);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = moveAbility,
                TargetAbilityId = returnAbility,
                NextTargetIndex = 1
            });

            var secondHarvestIndex = instructions.Count;
            AddHarvestInstructions(instructions, startIndex, "jitMH", secondTarget, firstTarget, WorkerInstructionPoint.JitHarvestB, 15);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = firstGatherAbility,
                TargetAbilityId = returnAbility
            });
            AddReturnInstructions(instructions, startIndex, secondTarget, firstTarget);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = moveAbility,
                TargetAbilityId = returnAbility,
                NextTargetIndex = 0
            });

            var firstHarvestIndex = instructions.Count;
            AddHarvestInstructions(instructions, startIndex, "jitMH", firstTarget, secondTarget, WorkerInstructionPoint.JitHarvestA, 15);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = firstGatherAbility,
                TargetAbilityId = returnAbility
            });
            AddReturnInstructions(instructions, startIndex, firstTarget, secondTarget);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = moveAbility,
                TargetAbilityId = returnAbility,
                NextTargetIndex = 0
            });
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "last-line",
                Command = WorkerInstructionCommand.Jump,
                JumpToInstructionIndex = secondHarvestIndex
            });

            return instructions;
        }

        private static void AddHarvestInstructions(
            List<WorkerInstruction> instructions,
            int startIndex,
            string instructionSet,
            MiningTargetDto target,
            MiningTargetDto pairedTarget,
            WorkerInstructionPoint point,
            int gatherFrame)
        {
            AddJitPairSetupInstruction(instructions, startIndex, instructionSet, target, pairedTarget, point, WorkerInstructionCommand.StoreTargetPoint, true);
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 0 });
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 1 });
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 14 });
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Gather, Point = point, TargetId = target.ResourceUnitId, RelativeFrame = gatherFrame, Queue = true });
        }

        private static void AddReturnInstructions(List<WorkerInstruction> instructions, int startIndex, MiningTargetDto target, MiningTargetDto pairedTarget)
        {
            AddJitPairSetupInstruction(instructions, startIndex, "jitRM", target, pairedTarget, WorkerInstructionPoint.JitReturnPoint, WorkerInstructionCommand.UseTargetPoint, true);
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 0 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 1 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 14 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Return, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 15, Queue = true });
        }

        private static WorkerInstructionPoint ResolveFirstLinePoint(string instructionSet, string role)
        {
            return instructionSet switch
            {
                "CCAw" => WorkerInstructionPoint.JitWaitPointA,
                "jitMHb" => WorkerInstructionPoint.JitHarvestA,
                "jitMH" when role?.EndsWith("2", StringComparison.OrdinalIgnoreCase) == true => WorkerInstructionPoint.JitHarvestB,
                "jitMH" => WorkerInstructionPoint.JitHarvestA,
                _ => WorkerInstructionPoint.None
            };
        }

        // Reference form: "{startIndex}-{first}-{second}.{Property}" with first/second in
        // canonical order, so each pair reference names exactly one stored row (round 7 answer 1).
        private static string BuildPairReference(int startIndex, string labelA, string labelB, string property)
        {
            var first = IsCanonicalFirst(labelA, labelB) ? labelA : labelB;
            var second = string.Equals(first, labelA, StringComparison.Ordinal) ? labelB : labelA;
            return $"{startIndex}-{first}-{second}.{property}";
        }

        private static string TargetHarvestProperty(string targetLabel, string pairedLabel)
        {
            return IsCanonicalFirst(targetLabel, pairedLabel) ? "HarvestA" : "HarvestB";
        }

        private static string TargetWaitProperty(string targetLabel, string pairedLabel)
        {
            return IsCanonicalFirst(targetLabel, pairedLabel) ? "WaitPointA" : "WaitPointB";
        }

        private static void AddJitPairSetupInstruction(
            List<WorkerInstruction> instructions,
            int startIndex,
            string instructionSet,
            MiningTargetDto target,
            MiningTargetDto pairedTarget,
            WorkerInstructionPoint point,
            WorkerInstructionCommand command,
            bool storeTargetPoint)
        {
            var property = point switch
            {
                WorkerInstructionPoint.JitHarvestA or WorkerInstructionPoint.JitHarvestB
                    => TargetHarvestProperty(target.ResourceLabel, pairedTarget.ResourceLabel),
                WorkerInstructionPoint.JitWaitPointA or WorkerInstructionPoint.JitWaitPointB
                    => TargetWaitProperty(target.ResourceLabel, pairedTarget.ResourceLabel),
                WorkerInstructionPoint.JitReturnPoint => "ReturnPoint",
                _ => string.Empty
            };

            instructions.Add(new WorkerInstruction
            {
                InstructionSet = instructionSet,
                Command = command,
                Point = point,
                TargetId = target.ResourceUnitId,
                NoCondition = true,
                StoreTargetPoint = storeTargetPoint,
                TargetPointReference = string.IsNullOrWhiteSpace(property)
                    ? string.Empty
                    : BuildPairReference(startIndex, target.ResourceLabel, pairedTarget.ResourceLabel, property)
            });
        }

        private static WorkerInstructionPoint ResolveBumpStartupPoint(string role)
        {
            return role.EndsWith("3", StringComparison.OrdinalIgnoreCase)
                ? WorkerInstructionPoint.BumpPartner
                : WorkerInstructionPoint.BumpMidpoint;
        }

        private void LogStartupInstructionSummary(int startIndex)
        {
            if (_ccawStartupSummaryLogged)
            {
                return;
            }

            var loadedWorkers = (Globals.CurrentMapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                    ?? new List<AssignedWorkerDto>())
                .Where(worker => worker != null && worker.UnitID != 0)
                .Select(worker => worker.UnitID)
                .Distinct()
                .ToList();
            var roleThreeCount = loadedWorkers.Count(worker => _ccawStartupLogs.Contains(worker));
            var jitMhCount = loadedWorkers.Count(worker => _jitMhStartupLogs.Contains(worker));
            var expectedCcawCount = Settings.IsMagannatha12WorkerOverride ? 2 : 4;
            var expectedBumpCount = Settings.IsMagannatha12WorkerOverride ? 2 : 0;
            var expectedJitMhCount = loadedWorkers.Count - expectedCcawCount - expectedBumpCount;
            var bumpCount = loadedWorkers.Count(worker => _bumpStartupLogs.Contains(worker));
            if (roleThreeCount == expectedCcawCount && bumpCount == expectedBumpCount && jitMhCount == expectedJitMhCount)
            {
                Console.WriteLine($"[BUILD START INSTRUCTIONS] start={startIndex} CCAw={roleThreeCount} bump={bumpCount} jitMH={jitMhCount}.");
                _ccawStartupSummaryLogged = true;
            }
        }

        private static List<OrderedMineral> BuildRuntimeGreedyMinerals(
            List<MineralDto> liveMinerals,
            List<WorkerEntryDto> liveWorkers,
            Vector2Dto mineralCenterOfMass,
            Vector2Dto townhallPosition,
            int startIndex,
            List<HarvestReturnCargoPointDto> cargoPoints)
        {
            if (liveMinerals == null || liveMinerals.Count == 0
                || liveWorkers == null || liveWorkers.Count == 0
                || mineralCenterOfMass == null || townhallPosition == null
                || cargoPoints == null || cargoPoints.Count == 0)
            {
                return new List<OrderedMineral>();
            }

            var workerTuples = liveWorkers
                .Where(worker => worker?.Position != null && worker.UnitTag != 0)
                .Select(worker => (worker.UnitTag, worker.Position.X, worker.Position.Y, worker.Position.Z, worker.UnitType));
            var greedyWorkers = WorkerLabelChainHelper.BuildGreedyWorkerEntries(workerTuples, mineralCenterOfMass, null);
            var w1Position = greedyWorkers.LastOrDefault()?.Position;
            if (w1Position == null)
            {
                return new List<OrderedMineral>();
            }

            var mineralPositions = liveMinerals
                .Where(mineral => mineral?.Position != null && mineral.UnitTag != 0)
                .ToList();
            if (mineralPositions.Count == 0)
            {
                return new List<OrderedMineral>();
            }

            var temporaryTraversal = BuildClosestTraversal(
                mineralPositions.Select(mineral => mineral.Position).ToList(),
                w1Position,
                false);
            if (temporaryTraversal.Count != mineralPositions.Count)
            {
                return new List<OrderedMineral>();
            }

            var highEndPosition = temporaryTraversal[^1];
            var finalTraversal = BuildClosestTraversal(
                mineralPositions.Select(mineral => mineral.Position).ToList(),
                highEndPosition,
                true);
            if (finalTraversal.Count != mineralPositions.Count)
            {
                return new List<OrderedMineral>();
            }

            finalTraversal.Reverse();
            var resourceValues = mineralPositions
                .Select(mineral => Math.Max(0, mineral.MineralContents))
                .Distinct()
                .OrderBy(value => value)
                .ToList();
            if (resourceValues.Count < 2)
            {
                Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] cannot classify greedy minerals without distinct observed resource values.");
                return new List<OrderedMineral>();
            }

            var largeResourceValue = resourceValues[^1];
            var result = new List<OrderedMineral>(finalTraversal.Count);
            for (var orderIndex = 0; orderIndex < finalTraversal.Count; orderIndex++)
            {
                var position = finalTraversal[orderIndex];
                var observed = mineralPositions.First(mineral => DistanceSquared(mineral.Position, position) <= 0.0001f);
                var resources = (uint)Math.Max(0, observed.MineralContents);
                var isLarge = resources == largeResourceValue;
                var cargoPoint = cargoPoints.FirstOrDefault(point =>
                    point?.ResourcePosition != null
                    && DistanceSquared(point.ResourcePosition, observed.Position) <= 0.0001f);
                if (cargoPoint == null
                    || !HasNonZeroPoint(cargoPoint.HarvestPoint)
                    || !HasNonZeroPoint(cargoPoint.ReturnPoint))
                {
                    Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] missing persisted cargo footprint for mineral tag={observed.UnitTag}; suppressing runtime assignments.");
                    return new List<OrderedMineral>();
                }

                result.Add(new OrderedMineral
                {
                    Position = new Vector2Dto(position.X, position.Y, position.Z),
                    HarvestPoint = cargoPoint.HarvestPoint,
                    SmHarvestPoint = cargoPoint.SmHarvestPoint,
                    ReturnPoint = cargoPoint.ReturnPoint,
                    SmReturnPoint = cargoPoint.SmReturnPoint,
                    CcaWaitPoint = cargoPoint.CcaWaitPoint,
                    Index = orderIndex + 1,
                    OriginalIndex = mineralPositions.IndexOf(observed),
                    DistanceFromCOM = Distance(observed.Position, mineralCenterOfMass),
                    DistanceToTownhall = Distance(observed.Position, townhallPosition),
                    Resources = resources,
                    IsNear = isLarge,
                    IsLarge = isLarge,
                    IsFar = !isLarge,
                    Size = isLarge ? MineralSize.Large : MineralSize.Small,
                    UnitTag = observed.UnitTag
                });
            }

            Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] built runtime greedy minerals={result.Count} from live observation.");
            return result;
        }

        private static bool IsSamePosition(Vector2Dto first, Vector2Dto second)
        {
            return first != null && second != null
                && DistanceSquared(first, second) <= 0.0001f;
        }

        private static List<Vector2Dto> BuildClosestTraversal(
            List<Vector2Dto> positions,
            Vector2Dto anchor,
            bool includeAnchor)
        {
            var remaining = positions
                .Select((position, index) => (position, index))
                .ToList();
            var traversal = new List<Vector2Dto>();
            var current = anchor;

            if (includeAnchor)
            {
                var anchorEntry = remaining
                    .OrderBy(entry => DistanceSquared(entry.position, current))
                    .ThenBy(entry => entry.position.X)
                    .ThenBy(entry => entry.position.Y)
                    .FirstOrDefault();
                if (anchorEntry.position == null || DistanceSquared(anchorEntry.position, current) > 0.0001f)
                {
                    return traversal;
                }

                traversal.Add(anchorEntry.position);
                remaining.Remove(anchorEntry);
                current = anchorEntry.position;
            }

            while (remaining.Count > 0)
            {
                var next = remaining
                    .OrderBy(entry => DistanceSquared(entry.position, current))
                    .ThenBy(entry => entry.position.X)
                    .ThenBy(entry => entry.position.Y)
                    .First();
                traversal.Add(next.position);
                remaining.Remove(next);
                current = next.position;
            }

            return traversal;
        }

        private bool BuildGreedyMineralChainFromObservation(int frame)
        {
            var mapData = Globals.CurrentMapData;
            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var workerCount = snapshot.SelfUnits?.Values.Count(worker => worker != null && IsWorkerType(worker.UnitType)) ?? 0;
            var relativeFrame = Settings.GetRelativeFrame(frame);
            if (mapData == null || snapshot == null || startIndex < 0)
            {
                return false;
            }

            if (_initialAssignmentLatched)
            {
                if (_greedyChainStartIndex != startIndex)
                {
                    return false;
                }

                if (_lastObservedWorkerCount == workerCount)
                {
                    return false;
                }

                if (Settings.IsMagannathaMap && workerCount != 12)
                {
                    _lastObservedWorkerCount = workerCount;
                    return false;
                }

                if (relativeFrame > 0 && _workerCountTransitionApplied)
                {
                    _lastObservedWorkerCount = workerCount;
                    return false;
                }
            }

            if (mapData.StartingTownHall == null || startIndex >= mapData.StartingTownHall.Length)
            {
                return false;
            }

            var townhall = mapData.StartingTownHall[startIndex];
            var com = mapData.MineralCenterOfMass != null && mapData.MineralCenterOfMass.Count > startIndex
                ? mapData.MineralCenterOfMass[startIndex]
                : null;
            var visibleMinerals = snapshot.VisibleMinerals
                .Where(mineral => mineral?.IsVisible == true
                    && mineral.MineralContents != 0
                    && mineral.Position != null
                    && mineral.UnitTag != 0)
                .ToList();
            var workers = snapshot.SelfUnits?.Values
                .Where(worker => worker?.Position != null && IsWorkerType(worker.UnitType))
                .ToList() ?? new List<WorkerEntryDto>();

            if (townhall == null || com == null || visibleMinerals.Count != 8 || workers.Count == 0)
            {
                return false;
            }

            var cargoPoints = mapData.MainMineralCargoPoints?.ElementAtOrDefault(startIndex);
            var ordered = BuildRuntimeGreedyMinerals(visibleMinerals, workers, com, townhall, startIndex, cargoPoints);
            if (ordered.Count != visibleMinerals.Count)
            {
                return false;
            }

            mapData.OrderedMainMinerals ??= new List<List<OrderedMineral>>();
            while (mapData.OrderedMainMinerals.Count <= startIndex)
            {
                mapData.OrderedMainMinerals.Add(new List<OrderedMineral>());
            }
            mapData.OrderedMainMinerals[startIndex] = ordered;
            mapData.StartingMinerals ??= new List<List<OrderedMineral>>();
            while (mapData.StartingMinerals.Count <= startIndex)
            {
                mapData.StartingMinerals.Add(new List<OrderedMineral>());
            }
            mapData.StartingMinerals[startIndex] = ordered;
            mapData.TeamPatchAssignments ??= new List<List<TeamPatchAssignmentDto>>();
            var workerTuples = workers
                .Where(worker => worker?.Position != null && worker.UnitTag != 0)
                .Select(worker => (worker.UnitTag, worker.Position.X, worker.Position.Y, worker.Position.Z, worker.UnitType));
            var workersForAssignment = WorkerLabelChainHelper.BuildGreedyWorkerEntries(workerTuples, com, _workerLabelService);
            OrderRuntimeVespeneChain(mapData, startIndex, snapshot.Vespene.Values.ToList(), workersForAssignment, townhall);
            // Rebuild the current game's assignment records from the current worker tags.
            // Preserve MiningManager-owned frame-0 JIT geometry while refreshing worker tags.
            var previousJitPoints = mapData.TeamPatchAssignments.ElementAtOrDefault(startIndex)?
                .Where(assignment => assignment != null)
                .ToDictionary(
                    assignment => assignment.TeamNumber,
                    assignment => (assignment.JitReturnPoint, assignment.JitWaitPoint));
            while (mapData.TeamPatchAssignments.Count <= startIndex)
            {
                mapData.TeamPatchAssignments.Add(new List<TeamPatchAssignmentDto>());
            }
            mapData.TeamPatchAssignments[startIndex] = new List<TeamPatchAssignmentDto>();
            mapData.TeamPatchAssignments[startIndex] = TeamLabelRegistrationHelper.EnsureTeamLabelsForStart(
                mapData,
                startIndex,
                ordered,
                workersForAssignment,
                com,
                _workerLabelService,
                mapData.TeamPatchAssignments);
            if (previousJitPoints != null)
            {
                foreach (var assignment in mapData.TeamPatchAssignments[startIndex])
                {
                    if (assignment != null && previousJitPoints.TryGetValue(assignment.TeamNumber, out var points))
                    {
                        assignment.JitReturnPoint = points.JitReturnPoint;
                        assignment.JitWaitPoint = points.JitWaitPoint;
                    }
                }
            }
            // Pair table population moved to the callers, after CalculateCcaWaitPointsForCurrentSpawn (C7).
            _labelsInitialized = false;
            var currentTownHallUnitId = Globals.CurrentObservation?.CurrentTownHalls?.Values
                .FirstOrDefault(unit => unit?.Position != null
                    && DistanceSquared(unit.Position, townhall) <= 1f)?.UnitTag ?? 0;
            PopulateAssignedWorkersAndCrossTable(mapData, startIndex, mapData.TeamPatchAssignments[startIndex], townhall, currentTownHallUnitId);
            _greedyChainStartIndex = startIndex;
            _greedyChainWorkerCount = workersForAssignment.Count;
            if (!_initialAssignmentLatched)
            {
                _initialAssignmentLatched = true;
                _lastObservedWorkerCount = workersForAssignment.Count;
            }
            else
            {
                _workerCountTransitionApplied = true;
                _lastObservedWorkerCount = workersForAssignment.Count;
            }

            Console.WriteLine($"BabySharkBuildManager: built greedy mineral chain for start[{startIndex}] from {ordered.Count} observed minerals for workerCount={_greedyChainWorkerCount} initialLatched={_initialAssignmentLatched} transitionApplied={_workerCountTransitionApplied}.");
            return true;
        }

        private void OrderRuntimeVespeneChain(
            MawBaseLocationData mapData,
            int startIndex,
            List<OrderedVespene> observedVespenes,
            List<WorkerEntryDto> greedyWorkers,
            Vector2Dto townhall)
        {
            var anchorMineral = mapData.OrderedMainMinerals?
                .ElementAtOrDefault(startIndex)?
                .FirstOrDefault(mineral => mineral?.Index == 8);
            if (anchorMineral?.Position == null || observedVespenes == null || observedVespenes.Count == 0 || townhall == null)
            {
                return;
            }

            var currentSpawnVespenes = mapData.MainVespene?
                .ElementAtOrDefault(startIndex)?
                .Where(position => position != null)
                .ToList();
            if (currentSpawnVespenes == null || currentSpawnVespenes.Count != 2)
            {
                return;
            }

            var ordered = observedVespenes
                .Where(vespene => vespene?.Position != null
                    && vespene.UnitTag != 0
                    && currentSpawnVespenes.Any(position => DistanceSquared(position, vespene.Position) <= 0.01f))
                .Select(vespene => new
                {
                    Vespene = vespene,
                    Distance = Distance(vespene.Position, anchorMineral.Position)
                })
                .OrderBy(item => item.Distance)
                .ToList();
            if (ordered.Count != 2)
            {
                return;
            }

            var result = new List<OrderedVespene>(2);
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                var linePoints = BuildMineralLinePoints(item.Vespene.Position, townhall);
                result.Add(new OrderedVespene
                {
                    Position = item.Vespene.Position,
                    HarvestPoint = linePoints.HarvestPoint,
                    ReturnPoint = linePoints.ReturnPoint,
                    Index = index + 1,
                    DistanceToW4 = item.Distance,
                    Label = index == 0 ? "VA" : "VB",
                    UnitTag = item.Vespene.UnitTag
                });
            }

            mapData.OrderedMainVespene ??= new List<List<OrderedVespene>>();
            while (mapData.OrderedMainVespene.Count <= startIndex)
            {
                mapData.OrderedMainVespene.Add(new List<OrderedVespene>());
            }

            if (result.Count < 2 || result[0].Label != "VA" || result[1].Label != "VB")
            {
                return;
            }

            mapData.OrderedMainVespene[startIndex] = result;
            mapData.VespeneFinalLabelsByPosition ??= new Dictionary<string, string>();
            foreach (var vespene in result)
            {
                mapData.VespeneFinalLabelsByPosition[$"{vespene.Position.X:F2},{vespene.Position.Y:F2}"] = vespene.Label;
                _vespeneLabelService?.SetVespeneLabel(vespene.Label, new Point
                {
                    X = vespene.Position.X,
                    Y = vespene.Position.Y,
                    Z = vespene.Position.Z + 1.0f
                }, ProcessVisableUnits.GetFinalLabelColor(vespene.Label));
            }

            var placement = _spawningPoolPlacementService?.CalculateSpawningPoolPlacement(
                townhall,
                mapData.MineralCenterOfMass.ElementAtOrDefault(startIndex),
                result[1].Position);
            if (placement != null)
            {
                mapData.SpawningPoolPlacements ??= new List<Vector2Dto>();
                while (mapData.SpawningPoolPlacements.Count <= startIndex)
                {
                    mapData.SpawningPoolPlacements.Add(null);
                }

                mapData.SpawningPoolPlacements[startIndex] = new Vector2Dto(placement.X, placement.Y, townhall.Z);
                _spawningPoolPlacementService.DrawPlacement(placement);
            }

            Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] ordered VA/VB from M[8] anchor (VA nearest, VB furthest) and completed spawning-pool placement.");
        }

        private static void PopulateAssignedWorkersAndCrossTable(
            MawBaseLocationData mapData,
            int startIndex,
            List<TeamPatchAssignmentDto> assignments,
            Vector2Dto townhall,
            ulong townHallUnitId)
        {
            while (mapData.AssignedWorkers.Count <= startIndex)
            {
                mapData.AssignedWorkers.Add(new List<AssignedWorkerDto>());
            }

            var allMineralsByLabel = (assignments ?? new List<TeamPatchAssignmentDto>())
                .SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                .Where(mineral => !string.IsNullOrWhiteSpace(mineral.FinalLabel))
                .GroupBy(mineral => mineral.FinalLabel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var workerCount = (assignments ?? new List<TeamPatchAssignmentDto>())
                .SelectMany(assignment => assignment.Workers ?? new List<WorkerEntryDto>())
                .Count();
            var assignedWorkers = new List<AssignedWorkerDto>();
            foreach (var assignment in assignments ?? new List<TeamPatchAssignmentDto>())
            {
                foreach (var worker in assignment.Workers ?? new List<WorkerEntryDto>())
                {
                    var assigned = new AssignedWorkerDto
                    {
                        UnitID = worker.UnitTag,
                        CurrentXY = worker.Position,
                        Role = worker.FinalLabel,
                        TownHallUnitID = townHallUnitId,
                        Mti = 0
                    };

                    foreach (var targetLabel in GetInstructionLabels(worker.FinalLabel, assignment.TeamNumber, workerCount))
                    {
                        if (!allMineralsByLabel.TryGetValue(targetLabel, out var targetMineral))
                        {
                            Console.WriteLine($"[ASSIGNMENT ERROR] role={worker.FinalLabel} target={targetLabel} is not present in BuildManager labels");
                            continue;
                        }

                        var isInitial = assigned.MiningTargets.Count == 0;
                        var miningTarget = CreateMiningTarget(
                            targetMineral,
                            targetMineral,
                            townhall,
                            townHallUnitId,
                            !isInitial,
                            !isInitial);

                        assigned.MiningTargets.Add(miningTarget);
                    }

                    assignedWorkers.Add(assigned);
                }
            }

            mapData.AssignedWorkers[startIndex] = assignedWorkers;

            while (mapData.MiningTargetCrossTables.Count <= startIndex)
            {
                mapData.MiningTargetCrossTables.Add(new MiningTargetCrossTableDto());
            }

            var labels = assignments?
                .SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                .Where(mineral => !string.IsNullOrWhiteSpace(mineral.FinalLabel))
                .Select(mineral => mineral.FinalLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            var crossTable = new MiningTargetCrossTableDto
            {
                StartIndex = startIndex,
                Calculated = true,
                ResourceLabels = labels,
                JitPairs = mapData.MainJitPairReturnCalculations?.ElementAtOrDefault(startIndex)
                    ?.ToList() ?? new List<JitPairReturnCalculationDto>()
            };

            foreach (var fromLabel in labels)
            {
                var fromMineral = assignments.SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                    .First(mineral => string.Equals(mineral.FinalLabel, fromLabel, StringComparison.OrdinalIgnoreCase));
                var routes = new List<MiningTargetDto>();
                foreach (var toLabel in labels)
                {
                    var toMineral = assignments.SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                        .First(mineral => string.Equals(mineral.FinalLabel, toLabel, StringComparison.OrdinalIgnoreCase));
                    routes.Add(CreateMiningTarget(fromMineral, toMineral, townhall, townHallUnitId, string.Equals(fromLabel, toLabel, StringComparison.OrdinalIgnoreCase), !string.Equals(fromLabel, toLabel, StringComparison.OrdinalIgnoreCase)));
                }
                crossTable.Routes.Add(routes);
            }

            mapData.MiningTargetCrossTables[startIndex] = crossTable;
            Console.WriteLine($"BabySharkBuildManager: AssignedWorkers start[{startIndex}]={assignedWorkers.Count}, cross-table labels={labels.Count}, calculated={crossTable.Calculated}");
        }

        // Canonical resource order: VA, VB, then the eight minerals, then Pool.
        // Pair keys are "{startIndex}-{first}-{second}" with first before second in this
        // order. Each unordered pair (including self pairs) is stored exactly once; a self
        // row is the Speed Mining geometry for that resource. HarvestA always belongs to
        // the first-listed label, HarvestB to the second.
        private static readonly string[] CanonicalJitResourceOrder =
        {
            "VA", "VB", "TA", "TB", "SA", "SB", "BA", "BB", "YA", "YB", "Pool"
        };

        private static int CanonicalJitOrder(string label)
        {
            var index = Array.IndexOf(CanonicalJitResourceOrder, label);
            return index < 0 ? CanonicalJitResourceOrder.Length : index;
        }

        private static bool IsCanonicalFirst(string labelA, string labelB)
        {
            return CanonicalJitOrder(labelA) <= CanonicalJitOrder(labelB);
        }

        private static (float X, float Y)? Normalize(float dx, float dy)
        {
            var length = MathF.Sqrt(dx * dx + dy * dy);
            return length <= 0.0001f ? null : (dx / length, dy / length);
        }

        private static Vector2Dto HarvestPointOnCircle(Vector2Dto center, float radius, Vector2Dto toward)
        {
            if (radius <= 0f)
            {
                // Vespene geysers (worker enters) and the pool (never harvested) use their own X,Y.
                return new Vector2Dto(center.X, center.Y, center.Z);
            }

            var direction = Normalize(toward.X - center.X, toward.Y - center.Y);
            return direction == null
                ? new Vector2Dto(center.X, center.Y, center.Z)
                : new Vector2Dto(
                    center.X + direction.Value.X * radius,
                    center.Y + direction.Value.Y * radius,
                    center.Z);
        }

        private static void PopulateJitPairReturnCalculations(
            MawBaseLocationData mapData,
            int startIndex,
            Vector2Dto townhall)
        {
            if (mapData == null || startIndex < 0 || townhall == null)
            {
                return;
            }

            const float hatcheryFootprintRadius = 2.75f;
            const float mineralFootprintRadius = 1.0f;
            const float geyserFootprintRadius = 3f;

            var orderedMinerals = mapData.OrderedMainMinerals?.ElementAtOrDefault(startIndex)
                ?.Where(mineral => mineral != null && !string.IsNullOrWhiteSpace(mineral.FinalLabel))
                .OrderBy(mineral => CanonicalJitOrder(mineral.FinalLabel))
                .ToList() ?? new List<OrderedMineral>();
            var orderedVespene = mapData.OrderedMainVespene?.ElementAtOrDefault(startIndex)
                ?.Where(vespene => vespene != null && !string.IsNullOrWhiteSpace(vespene.Label))
                .OrderBy(vespene => CanonicalJitOrder(vespene.Label))
                .ToList() ?? new List<OrderedVespene>();
            var poolPosition = mapData.SpawningPoolPlacements?.ElementAtOrDefault(startIndex);

            var resources = new List<(string Label, ulong Tag, Vector2Dto Center, float HarvestRadius, Vector2Dto Wait)>();

            foreach (var vespene in orderedVespene)
            {
                // Geyser wait point: on the 3u geyser footprint toward the hatchery — the second
                // worker stands there while the first is inside collecting. The geyser harvest
                // point is the geyser's own X,Y (round 13 semantics; runtime SMART handling
                // for gas is a later adjustment, geometry only here).
                var geyserDirection = Normalize(townhall.X - vespene.Position.X, townhall.Y - vespene.Position.Y);
                var geyserWait = geyserDirection == null
                    ? new Vector2Dto(vespene.Position.X, vespene.Position.Y, vespene.Position.Z)
                    : new Vector2Dto(
                        vespene.Position.X + geyserDirection.Value.X * geyserFootprintRadius,
                        vespene.Position.Y + geyserDirection.Value.Y * geyserFootprintRadius,
                        vespene.Position.Z);
                resources.Add((vespene.Label, vespene.UnitTag, vespene.Position, 0f, geyserWait));
            }

            foreach (var mineral in orderedMinerals)
            {
                resources.Add((mineral.FinalLabel, mineral.UnitTag, mineral.Position, mineralFootprintRadius, mineral.CcaWaitPoint));
            }

            if (poolPosition != null && HasNonZeroPoint(poolPosition))
            {
                // The pool is never harvested; its harvest and wait fields carry its own X,Y so
                // no field is ever stored as (0,0) (round 2 answer 10).
                resources.Add(("Pool", 0, poolPosition, 0f, new Vector2Dto(poolPosition.X, poolPosition.Y, poolPosition.Z)));
            }

            if (resources.Count == 0)
            {
                return;
            }

            mapData.MainJitPairReturnCalculations ??= new List<List<JitPairReturnCalculationDto>>();
            while (mapData.MainJitPairReturnCalculations.Count <= startIndex)
            {
                mapData.MainJitPairReturnCalculations.Add(new List<JitPairReturnCalculationDto>());
            }

            var existing = mapData.MainJitPairReturnCalculations[startIndex] ?? new List<JitPairReturnCalculationDto>();
            var existingByKey = existing
                .Where(pair => pair != null && !string.IsNullOrWhiteSpace(pair.PairKey))
                .ToDictionary(pair => pair.PairKey, StringComparer.OrdinalIgnoreCase);
            var calculated = new List<JitPairReturnCalculationDto>();

            for (var i = 0; i < resources.Count; i++)
            {
                for (var j = i; j < resources.Count; j++)
                {
                    var first = resources[i];
                    var second = resources[j];
                    var key = $"{startIndex}-{first.Label}-{second.Label}";

                    // Reuse stored geometry only when every point list processing reads is present;
                    // rows written before B wait points existed are recalculated, not reused forever.
                    if (existingByKey.TryGetValue(key, out var stored)
                        && HasNonZeroPoint(stored.ReturnPoint)
                        && HasNonZeroPoint(stored.HarvestA)
                        && HasNonZeroPoint(stored.HarvestB)
                        && HasNonZeroPoint(stored.WaitPointA)
                        && HasNonZeroPoint(stored.WaitPointB))
                    {
                        calculated.Add(stored);
                        continue;
                    }

                    // JIT return point: on the hatchery footprint circle (2.75u), toward the
                    // average of the two resource centers. Harvest points: on each resource's
                    // footprint circle (minerals 1.0u), toward that JIT return point. A self
                    // pair degenerates to the speed-mining line (round 9 formula).
                    var averageX = (first.Center.X + second.Center.X) * 0.5f;
                    var averageY = (first.Center.Y + second.Center.Y) * 0.5f;
                    var returnDirection = Normalize(averageX - townhall.X, averageY - townhall.Y)
                        ?? Normalize(first.Center.X - townhall.X, first.Center.Y - townhall.Y);
                    var returnPoint = returnDirection == null
                        ? new Vector2Dto(townhall.X, townhall.Y, townhall.Z)
                        : new Vector2Dto(
                            townhall.X + returnDirection.Value.X * hatcheryFootprintRadius,
                            townhall.Y + returnDirection.Value.Y * hatcheryFootprintRadius,
                            townhall.Z);

                    calculated.Add(new JitPairReturnCalculationDto
                    {
                        PairKey = key,
                        FromResourceLabel = first.Label,
                        ToResourceLabel = second.Label,
                        FromResourceUnitTag = first.Tag,
                        ToResourceUnitTag = second.Tag,
                        WaitPointA = HasNonZeroPoint(first.Wait)
                            ? first.Wait
                            : new Vector2Dto(first.Center.X, first.Center.Y, first.Center.Z),
                        WaitPointB = HasNonZeroPoint(second.Wait)
                            ? second.Wait
                            : new Vector2Dto(second.Center.X, second.Center.Y, second.Center.Z),
                        HarvestA = HarvestPointOnCircle(first.Center, first.HarvestRadius, returnPoint),
                        HarvestB = HarvestPointOnCircle(second.Center, second.HarvestRadius, returnPoint),
                        ReturnPoint = returnPoint
                    });
                }
            }

            mapData.MainJitPairReturnCalculations[startIndex] = calculated;
            Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] jit pair table rows={calculated.Count} (canonical order, self rows included).");
        }


        private static (Vector2Dto Harvest, Vector2Dto Return) BuildLinePoints(Vector2Dto resource, Vector2Dto townhall, float harvestOffset, float returnOffset)
        {
            var dx = resource.X - townhall.X;
            var dy = resource.Y - townhall.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= 0.0001f)
            {
                return (new Vector2Dto(resource.X, resource.Y, resource.Z), new Vector2Dto(townhall.X, townhall.Y, townhall.Z));
            }

            var ux = dx / distance;
            var uy = dy / distance;
            return (
                new Vector2Dto(resource.X - ux * harvestOffset, resource.Y - uy * harvestOffset, resource.Z),
                new Vector2Dto(townhall.X + ux * returnOffset, townhall.Y + uy * returnOffset, townhall.Z));
        }


        private static IReadOnlyList<string> GetInstructionLabels(string role, int teamNumber, int workerCount)
        {
            if (workerCount == 12 || Settings.IsMagannathaMap)
            {
                if (string.IsNullOrWhiteSpace(role) || role.Length != 2)
                {
                    return Array.Empty<string>();
                }

                var teamPrefix = role.Substring(0, 1);
                var roleNumber = role[1];
                var startsOnA = roleNumber == '1' || roleNumber == '3';
                return startsOnA
                    ? new[] { $"{teamPrefix}A", $"{teamPrefix}B" }
                    : new[] { $"{teamPrefix}B", $"{teamPrefix}A" };
            }

            // 8 worker requires a complete rework after 12 worker is satisfactory.
            // The previous per-role label table here sent workers across team patches and is deleted;
            // 8-worker starts now resolve no labels until that rework is designed.
            return Array.Empty<string>();
        }

        private static MiningTargetDto CreateMiningTarget(OrderedMineral from, OrderedMineral to, Vector2Dto townhall, ulong townHallUnitId, bool speedMining, bool abSwitch)
        {
            var returnPoint = speedMining ? from.ReturnPoint : to.ReturnPoint;
            return new MiningTargetDto
            {
                ResourceLabel = to.FinalLabel,
                FromResourceLabel = from.FinalLabel,
                ToResourceLabel = to.FinalLabel,
                ResourceUnitId = to.UnitTag,
                ResourcePosition = to.Position,
                TownHallUnitId = townHallUnitId,
                FromHarvestPoint = from.HarvestPoint,
                ToHarvestPoint = to.HarvestPoint,
                HarvestPoint = to.HarvestPoint,
                SmHarvestPoint = speedMining ? from.SmHarvestPoint : to.SmHarvestPoint,
                ReturnPoint = returnPoint,
                SmReturnPoint = speedMining ? from.SmReturnPoint : to.SmReturnPoint,
                IsSpeedMining = speedMining,
                IsABSwitch = abSwitch,
                IsInitialMineralAssignment = !abSwitch
            };
        }

        private static bool HasNonZeroPoint(Vector2Dto point)
        {
            return point != null && (point.X != 0f || point.Y != 0f);
        }

        private static (Vector2Dto HarvestPoint, Vector2Dto SmHarvestPoint, Vector2Dto ReturnPoint, Vector2Dto SmReturnPoint) BuildMineralLinePoints(Vector2Dto resource, Vector2Dto townhall)
        {
            var dx = resource.X - townhall.X;
            var dy = resource.Y - townhall.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= 0.001f)
            {
                return (new Vector2Dto(resource.X, resource.Y, resource.Z), new Vector2Dto(resource.X, resource.Y, resource.Z), new Vector2Dto(townhall.X, townhall.Y, townhall.Z), new Vector2Dto(townhall.X, townhall.Y, townhall.Z));
            }

            var ux = dx / distance;
            var uy = dy / distance;
            const float mineralHarvestOffset = 1.5f;
            var harvest = new Vector2Dto(resource.X - ux * mineralHarvestOffset, resource.Y - uy * mineralHarvestOffset, resource.Z);
            var smHarvest = new Vector2Dto(resource.X - ux * 2.75f, resource.Y - uy * 2.75f, resource.Z);
            var ret = new Vector2Dto(townhall.X + ux * 2.75f, townhall.Y + uy * 2.75f, townhall.Z);
            var smReturn = new Vector2Dto(townhall.X + ux, townhall.Y + uy, townhall.Z);
            return (harvest, smHarvest, ret, smReturn);
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

        private static float Distance(Vector2Dto first, Vector2Dto second)
        {
            return MathF.Sqrt(DistanceSquared(first, second));
        }

        private void UpdateRuntimeLabels()
        {
            var snapshot = Globals.CurrentObservation;
            if (_labelsInitialized || snapshot == null || Globals.CurrentMapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (startIndex < 0)
            {
                return;
            }

            // BuildGreedyMineralChainFromObservation is the sole worker-label and
            // assignment writer. This method only registers resource visuals and
            // consumes the already-built current-spawn assignments.
            MapLabelRegistrationHelper.RegisterLabels(
                Globals.CurrentMapData,
                startIndex,
                _mineralLabelService,
                _vespeneLabelService);

            RegisterUnitTypeLabels(snapshot);
            var currentAssignments = Globals.CurrentMapData.TeamPatchAssignments.ElementAtOrDefault(startIndex);
            var assignedMinerals = currentAssignments?
                .SelectMany(assignment => assignment.Minerals)
                .ToList();
            _labelsInitialized = currentAssignments != null
                && currentAssignments.Count == 4
                && assignedMinerals != null
                && assignedMinerals.Count == 8
                && assignedMinerals.All(mineral => mineral != null
                    && mineral.Position != null
                    && !string.IsNullOrWhiteSpace(mineral.FinalLabel));
        }

        private void RegisterUnitTypeLabels(ObservationSnapshotDto snapshot)
        {
            if (_workerLabelService == null) return;

            var hatcheryNumber = 0;
            var overlordNumber = 0;
            foreach (var unit in snapshot.CurrentTownHalls.Values.OrderBy(unit => unit.UnitTag))
            {
                hatcheryNumber++;
                _workerLabelService.SetLabel($"H{hatcheryNumber}", unit.UnitTag);
            }

            foreach (var unit in snapshot.SelfUnits.Values
                .Where(unit => unit.UnitType == (uint)UnitTypes.ZERG_OVERLORD || unit.UnitType == (uint)UnitTypes.ZERG_OVERLORDTRANSPORT)
                .OrderBy(unit => unit.UnitTag))
            {
                overlordNumber++;
                _workerLabelService.SetLabel($"OV{overlordNumber}", unit.UnitTag);
            }
        }

        private static bool IsWorkerType(uint unitType)
        {
            return unitType == (uint)UnitTypes.ZERG_DRONE
                || unitType == (uint)UnitTypes.TERRAN_SCV
                || unitType == (uint)UnitTypes.PROTOSS_PROBE;
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
        }
    }
}

