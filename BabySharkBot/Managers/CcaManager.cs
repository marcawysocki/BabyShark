using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.Managers;
using BabySharkBot.Services;
using BabySharkBot.Setup;

namespace BabySharkBot.Managers
{
    public class CcaManager : IManager
    {
        public bool NeverSkip { get; set; } = true;
        public bool SkipFrame { get; set; } = false;
        public double LongestFrame { get; set; } = 0;
        public double TotalFrameTime { get; set; } = 0;

        private readonly chrisCrossAppleSause _ccaService;
        private readonly BabySharkMiningManager _miningManager;

        public chrisCrossAppleSause CcaMiningService => _ccaService;
        public DrawOnlyManager DrawOnlyWrapper => _drawOnlyWrapper;
        private readonly DrawOnlyManager _drawOnlyWrapper;
        private bool _unregistered;
        private int _allMiningConsecutiveFrames = 0;
        private const int AllMiningConfirmationFrames = 2;

        public CcaManager(chrisCrossAppleSause ccaService, BabySharkMiningManager miningManager)
        {
            _ccaService = ccaService ?? throw new ArgumentNullException(nameof(ccaService));
            _miningManager = miningManager ?? throw new ArgumentNullException(nameof(miningManager));
            // subscribe to mining started event so we can unregister ourselves
            _miningManager.OnMiningStarted += HandleMiningStarted;
            _drawOnlyWrapper = new DrawOnlyManager(miningManager);
            _unregistered = false;
        }

        private void HandleMiningStarted()
        {
            try
            {
                if (_unregistered) return;
                _unregistered = true;
                var ai = BabySharkBot.BabySharkAI.Instance;
                if (ai != null)
                {
                    // Keep DrawOnlyManager registered after the CCA handoff so labels and debug
                    // visuals continue to be emitted for the rest of the match.
                    ai.Managers.RemoveAll(m => m == this);
                    Console.WriteLine("CcaManager: unregistered from BabySharkAI.Managers; DrawOnlyManager remains active");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CcaManager.HandleMiningStarted error: {ex.Message}");
            }
            finally
            {
                try
                {
                    _miningManager.OnMiningStarted -= HandleMiningStarted;
                }
                catch { }
            }
        }

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            try
            {
                var frame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
                var relativeFrame = Settings.GetRelativeFrame(frame);
                if (Settings.SimulatedStartActive)
                {
                    return Array.Empty<SC2APIProtocol.Action>();
                }

                // CCA owns the direct 8-worker STOP -> MOVE -> queued SMART opening.
                var mapData = _miningManager?.CurrentMapData;
                if (mapData == null)
                {
                    return Array.Empty<SC2APIProtocol.Action>();
                }

                var startIndex = Globals.CurrentStartIndex >= 0
                    ? Globals.CurrentStartIndex
                    : Settings.CurrentSpawnIndex;
                if (startIndex < 0)
                {
                    Console.WriteLine("CcaManager: current spawn index is unresolved; suppressing CCA commands.");
                    return Array.Empty<SC2APIProtocol.Action>();
                }

                // ObservationManager owns worker classification. Consume its authoritative tag list
                // instead of reclassifying raw units here.
                var liveWorkers = new List<WorkerEntryDto>();
                var snapshot = Globals.CurrentObservation;
                if (snapshot?.AvailableWorkers != null && snapshot.SelfUnits != null)
                {
                    foreach (var tag in snapshot.AvailableWorkers)
                    {
                        if (!snapshot.SelfUnits.TryGetValue(tag, out var worker)
                            || worker == null
                            || worker.Position == null)
                        {
                            continue;
                        }

                        var assignedWorker = mapData.TeamPatchAssignments?
                            .ElementAtOrDefault(startIndex)?
                            .SelectMany(assignment => assignment.Workers ?? new List<WorkerEntryDto>())
                            .FirstOrDefault(assigned => assigned != null && assigned.UnitTag == tag);
                        if (assignedWorker == null)
                        {
                            continue;
                        }

                        worker.Label = assignedWorker.FinalLabel;
                        worker.StartLabel = assignedWorker.StartLabel;
                        worker.FinalLabel = assignedWorker.FinalLabel;
                        liveWorkers.Add(worker);
                    }
                }


                Console.WriteLine($"[CCA INPUT] frame={frame} mapRef={mapData.GetHashCode()} startIndex={startIndex} start1Starting={(mapData.StartingMinerals?.Count > 1 ? mapData.StartingMinerals[1]?.Count ?? 0 : -1)} start1Ordered={(mapData.OrderedMainMinerals?.Count > 1 ? mapData.OrderedMainMinerals[1]?.Count ?? 0 : -1)} currentStarting={(startIndex >= 0 && mapData.StartingMinerals?.Count > startIndex ? mapData.StartingMinerals[startIndex]?.Count ?? 0 : -1)} currentOrdered={(startIndex >= 0 && mapData.OrderedMainMinerals?.Count > startIndex ? mapData.OrderedMainMinerals[startIndex]?.Count ?? 0 : -1)}");
                var actions = _ccaService.BuildBumpOrders(frame, mapData, startIndex, liveWorkers)?.ToList() ?? new List<SC2APIProtocol.Action>();
                if (relativeFrame == 0)
                {
                    Console.WriteLine($"[CCA DELIVERY] frame={frame} relative={relativeFrame} liveWorkers={liveWorkers.Count} availableWorkers={Settings.AvailableWorker.Count} actions={actions.Count}");
                }

                // 8-worker CCA has no bumping phase. Do not run the assignment-based fallback here:
                // when assignments are temporarily empty, assignedTags would also be empty and the
                // allMining check below could incorrectly unregister CCA before frame-0 orders run.
                // Keep CCA authoritative through the documented frame-35 handoff.
                if (liveWorkers.Count == 8 || Settings.WorkerCount == 8)
                {
                    if (relativeFrame >= 35 && !_unregistered)
                    {
                        Console.WriteLine("CcaManager: 8-worker CCA handoff at frame 35.");
                        try
                        {
                            _miningManager.SignalMiningStarted();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"CcaManager: failed 8-worker handoff: {ex.Message}");
                        }

                        HandleMiningStarted();
                    }

                    return actions != null ? actions.ToList() : Array.Empty<SC2APIProtocol.Action>();
                }

                // The canonical 12-worker opening owns its scheduled MOVE/SMART sequence
                // through the fixed frame-35 handoff; do not replace it with the recovery path.
                if (liveWorkers.Count == 12 || Settings.WorkerCount == 12)
                {
                    if (relativeFrame >= 35 && !_unregistered)
                    {
                        Console.WriteLine("CcaManager: 12-worker CCA handoff at frame 35.");
                        try
                        {
                            _miningManager.SignalMiningStarted();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"CcaManager: failed 12-worker handoff: {ex.Message}");
                        }

                        HandleMiningStarted();
                    }

                    return actions != null ? actions.ToList() : Array.Empty<SC2APIProtocol.Action>();
                }

                // After frame 15, if bump is disabled, issue mining gather orders for any worker that isn't mining
                if (relativeFrame >= 15)
                {
                    // Determine assigned workers for the current spawn
                    var teamAssignments = mapData.TeamPatchAssignments != null
                        && startIndex >= 0
                        && startIndex < mapData.TeamPatchAssignments.Count
                        ? mapData.TeamPatchAssignments[startIndex]
                        : null;
                    if (teamAssignments != null)
                    {
                        var assignedTags = teamAssignments.SelectMany(t => t.Workers ?? new List<WorkerEntryDto>()).Where(w => w != null).Select(w => w.UnitTag).ToHashSet();

                        // Find workers that are not currently gathering and issue SMART to their assigned mineral harvest point
                        var gatherActions = new List<SC2APIProtocol.Action>();
                        foreach (var w in liveWorkers.Where(lw => assignedTags.Contains(lw.UnitTag)))
                        {
                            var unit = snapshot?.SelfUnits?.TryGetValue(w.UnitTag, out var snapshotWorker) == true ? snapshotWorker : null;
                            var hasMiningOrder = unit?.OrderAbilityIds?.Any(abilityId => abilityId == (int)Abilities.MOVE || abilityId == (int)Abilities.SMART || abilityId == (int)Abilities.HARVEST_GATHER || abilityId == (int)Abilities.HARVEST_GATHER_DRONE || abilityId == (int)Abilities.HARVEST_GATHER_PROBE || abilityId == (int)Abilities.HARVEST_GATHER_SCV || abilityId == (int)Abilities.HARVEST_RETURN) == true;
                            var justReturnedCargo = unit?.WasCarrying == true && unit.IsCarrying == false;
                            if (!hasMiningOrder || justReturnedCargo)
                            {
                                // Try to find assigned mineral for this worker
                                var label = w.Label ?? w.FinalLabel ?? w.StartLabel;
                                var assignedMineral = teamAssignments.SelectMany(t => t.Minerals ?? new List<OrderedMineral>()).FirstOrDefault(m => string.Equals((m?.FinalLabel ?? m?.Label), label, StringComparison.OrdinalIgnoreCase));
                                var targetPoint = assignedMineral?.HarvestPoint;
                                var targetMineralTag = assignedMineral?.UnitTag ?? 0;
                                if (targetPoint != null
                                    && (targetPoint.X != 0f || targetPoint.Y != 0f)
                                    && targetMineralTag != 0)
                                {
                                    gatherActions.AddRange(StopWorker(w.UnitTag));
                                    gatherActions.AddRange(MoveWorker(w.UnitTag, new Point2D { X = targetPoint.X, Y = targetPoint.Y }));
                                    gatherActions.AddRange(GatherMineral(w.UnitTag, targetMineralTag));
                                }
                            }
                        }

                        if (gatherActions.Count > 0)
                        {
                            // Return gather actions so they execute this frame
                            return gatherActions;
                        }

                        // If no gather actions were needed, check if all assigned workers are mining; if so, signal mining started
                        var allMining = true;
                        foreach (var w in liveWorkers.Where(lw => assignedTags.Contains(lw.UnitTag)))
                        {
                            var unit = snapshot?.SelfUnits?.TryGetValue(w.UnitTag, out var snapshotWorker) == true ? snapshotWorker : null;
                            var isMining = unit?.OrderAbilityIds?.Any(abilityId => abilityId == (int)Abilities.HARVEST_GATHER || abilityId == (int)Abilities.HARVEST_RETURN) == true;
                            if (!isMining)
                            {
                                allMining = false;
                                break;
                            }
                        }

                        if (allMining && !_unregistered)
                        {
                            _allMiningConsecutiveFrames++;
                            if (_allMiningConsecutiveFrames >= AllMiningConfirmationFrames)
                            {
                                Console.WriteLine("CcaManager: all assigned workers are mining (confirmed) — signalling handoff and unregistering");
                                try
                                {
                                    _miningManager.SignalMiningStarted();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"CcaManager: failed to signal mining started: {ex.Message}");
                                }

                                HandleMiningStarted();
                            }
                        }
                        else if (!allMining)
                        {
                            _allMiningConsecutiveFrames = 0;
                        }
                    }
                }
                return actions != null ? actions.ToList() : Array.Empty<SC2APIProtocol.Action>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CcaManager.OnFrame error: {ex.Message}");
                return Array.Empty<SC2APIProtocol.Action>();
            }
        }

        private IEnumerable<SC2APIProtocol.Action> StopWorker(ulong workerTag)
        {
            if (workerTag == 0) return Array.Empty<SC2APIProtocol.Action>();
            var workerLabel = _miningManager.WorkerLabelService?.GetLabel(workerTag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMAND8] phase=CCA_RECOVERY worker={workerTag} Label={workerLabel} command=STOP queued=false");
            var command = new ActionRawUnitCommand { AbilityId = (int)Abilities.STOP };
            command.UnitTags.Add(workerTag);
            return new[] { new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } } };
        }

        private IEnumerable<SC2APIProtocol.Action> MoveWorker(ulong workerTag, Point2D target)
        {
            if (workerTag == 0 || target == null) return Array.Empty<SC2APIProtocol.Action>();
            var workerLabel = _miningManager.WorkerLabelService?.GetLabel(workerTag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMAND9] phase=CCA_RECOVERY worker={workerTag} Label={workerLabel} command=MOVE pos=({target.X:F2},{target.Y:F2}) queued=false");
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.MOVE,
                TargetWorldSpacePos = target
            };
            command.UnitTags.Add(workerTag);
            return new[] { new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } } };
        }

        private IEnumerable<SC2APIProtocol.Action> GatherMineral(ulong workerTag, ulong mineralTag)
        {
            if (workerTag == 0 || mineralTag == 0) return Array.Empty<SC2APIProtocol.Action>();
            var workerLabel = _miningManager.WorkerLabelService?.GetLabel(workerTag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMANDa] phase=CCA_RECOVERY worker={workerTag} Label={workerLabel} command=SMART targetTag={mineralTag} queued=true");
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.SMART,
                TargetUnitTag = mineralTag,
                QueueCommand = true
            };
            command.UnitTags.Add(workerTag);
            return new[] { new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } } };
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
        }
    }
}
