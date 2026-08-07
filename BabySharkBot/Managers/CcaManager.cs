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
        public bool NeverSkip { get; set; } = false;
        public bool SkipFrame { get; set; } = false;
        public double LongestFrame { get; set; } = 0;
        public double TotalFrameTime { get; set; } = 0;

        private readonly chrisCrossAppleSause _ccaService;
        private readonly BabySharkMiningManager _miningManager;

        public chrisCrossAppleSause CcaMiningService => _ccaService;
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
                    // Remove DrawOnly wrapper if present
                    ai.Managers.RemoveAll(m => m == _drawOnlyWrapper);
                    // Remove this manager
                    ai.Managers.RemoveAll(m => m == this);
                    Console.WriteLine("CcaManager: unregistered from BabySharkAI.Managers");
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
                var mapData = _miningManager?.CurrentMapData;
                if (mapData == null)
                {
                    return Array.Empty<SC2APIProtocol.Action>();
                }

                var startIndex = mapData?.StartingTownHall != null ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;

                // Build live worker entries from current observation
                var liveWorkers = new List<WorkerEntryDto>();
                var rawUnits = observation?.Observation?.RawData?.Units;
                if (rawUnits != null)
                {
                    foreach (var u in rawUnits.Where(u => u != null && u.Alliance == Alliance.Self && (u.UnitType == (uint)Sharky.UnitTypes.ZERG_DRONE || u.UnitType == (uint)Sharky.UnitTypes.TERRAN_SCV || u.UnitType == (uint)Sharky.UnitTypes.PROTOSS_PROBE)))
                    {
                        var label = _miningManager.WorkerLabelService?.GetLabel(u.Tag) ?? string.Empty;
                        liveWorkers.Add(new WorkerEntryDto
                        {
                            UnitTag = u.Tag,
                            UnitType = u.UnitType,
                            Position = new Vector2Dto(u.Pos.X, u.Pos.Y, u.Pos.Z),
                            Label = label,
                            StartLabel = label,
                            FinalLabel = label
                        });
                    }
                }

                var actions = _ccaService.BuildBumpOrders(frame, mapData, startIndex, liveWorkers);

                // After frame 15, if bump is disabled, issue mining gather orders for any worker that isn't mining
                if (frame >= 15)
                {
                    // Determine assigned workers for the current spawn
                    var state = _miningManager.CcaMiningService.GetCurrentSpawnState(mapData, startIndex);
                    if (state?.TeamAssignments != null)
                    {
                        var assignedTags = state.TeamAssignments.SelectMany(t => t.Workers ?? new List<WorkerEntryDto>()).Where(w => w != null).Select(w => w.UnitTag).ToHashSet();

                        // Find workers that are not currently gathering and issue SMART to their assigned mineral harvest point
                        var gatherActions = new List<SC2APIProtocol.Action>();
                        foreach (var w in liveWorkers.Where(lw => assignedTags.Contains(lw.UnitTag)))
                        {
                            var unit = observation?.Observation?.RawData?.Units?.FirstOrDefault(u => u != null && u.Tag == w.UnitTag);
                            var isMining = unit?.Orders != null && unit.Orders.Any(o => Convert.ToInt32(o.AbilityId) == (int)Abilities.HARVEST_GATHER || Convert.ToInt32(o.AbilityId) == (int)Abilities.HARVEST_GATHER_DRONE || Convert.ToInt32(o.AbilityId) == (int)Abilities.HARVEST_GATHER_PROBE || Convert.ToInt32(o.AbilityId) == (int)Abilities.HARVEST_GATHER_SCV || Convert.ToInt32(o.AbilityId) == (int)Abilities.HARVEST_RETURN);
                            if (!isMining)
                            {
                                // Try to find assigned mineral for this worker
                                var label = w.Label ?? w.FinalLabel ?? w.StartLabel;
                                var assignedMineral = state.TeamAssignments.SelectMany(t => t.Minerals ?? new List<OrderedMineral>()).FirstOrDefault(m => string.Equals((m?.FinalLabel ?? m?.Label), label, StringComparison.OrdinalIgnoreCase));
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
                            var unit = observation?.Observation?.RawData?.Units?.FirstOrDefault(u => u != null && u.Tag == w.UnitTag);
                            var isMining = unit?.Orders != null && unit.Orders.Any(o => Convert.ToInt32(o.AbilityId) == (int)Abilities.HARVEST_GATHER || Convert.ToInt32(o.AbilityId) == (int)Abilities.HARVEST_RETURN);
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
