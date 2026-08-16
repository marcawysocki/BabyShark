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

                        var label = _miningManager.WorkerLabelService?.GetLabel(tag) ?? string.Empty;
                        worker.Label = label;
                        worker.StartLabel = label;
                        worker.FinalLabel = label;
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
                            var isMining = unit?.OrderAbilityIds?.Any(abilityId => abilityId == (int)Abilities.HARVEST_GATHER || abilityId == (int)Abilities.HARVEST_GATHER_DRONE || abilityId == (int)Abilities.HARVEST_GATHER_PROBE || abilityId == (int)Abilities.HARVEST_GATHER_SCV || abilityId == (int)Abilities.HARVEST_RETURN) == true;
                            if (!isMining)
                            {
                                // Try to find assigned mineral for this worker
                                var label = w.Label ?? w.FinalLabel ?? w.StartLabel;
                                var assignedMineral = teamAssignments.SelectMany(t => t.Minerals ?? new List<OrderedMineral>()).FirstOrDefault(m => string.Equals((m?.FinalLabel ?? m?.Label), label, StringComparison.OrdinalIgnoreCase));
                                var targetPoint = assignedMineral?.HarvestPoint ?? assignedMineral?.Position;
                                if (targetPoint != null)
                                {
                                    var cmd = new ActionRawUnitCommand
                                    {
                                        AbilityId = (int)Abilities.SMART,
                                        TargetWorldSpacePos = new Point2D { X = targetPoint.X, Y = targetPoint.Y }
                                    };
                                    cmd.UnitTags.Add(w.UnitTag);
                                    gatherActions.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = cmd } });
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

        public void OnEnd(ResponseObservation observation, Result result)
        {
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
        }
    }
}
