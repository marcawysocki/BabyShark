using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.Managers;
using BabySharkBot.Services;
using BabySharkBot.Setup;
using SC2Action = SC2APIProtocol.Action;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Lifecycle manager for the "chrisCrossAppleSause" (CCA) worker initialization phase.
    /// Monitors frame progress and signals the handoff from CCA choreography to steady-state mining.
    /// Unregisters itself once the transition is complete.
    /// </summary>
    public class CcaManager : IManager
    {
        public bool NeverSkip { get; set; } = false;
        public bool SkipFrame { get; set; } = false;
        public double LongestFrame { get; set; } = 0;
        public double TotalFrameTime { get; set; } = 0;

        private readonly chrisCrossAppleSause _ccaService;
        private readonly BabySharkMiningManager _miningManager;
        private readonly BabySharkBuildManager _buildManager;

        public chrisCrossAppleSause CcaMiningService => _ccaService;
        private bool _unregistered;
        private int _allMiningConsecutiveFrames = 0;
        private const int AllMiningConfirmationFrames = 2;

        public CcaManager(chrisCrossAppleSause ccaService, BabySharkMiningManager miningManager, BabySharkBuildManager buildManager)
        {
            _ccaService = ccaService ?? throw new ArgumentNullException(nameof(ccaService));
            _miningManager = miningManager ?? throw new ArgumentNullException(nameof(miningManager));
            _buildManager = buildManager;
            // subscribe to mining started event so we can unregister ourselves
            _miningManager.OnMiningStarted += HandleMiningStarted;
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
                    // Remove this manager
                    ai.Managers.RemoveAll(m => m == this);
                    Console.WriteLine("CcaManager: unregistered from BabySharkAI.Managers");

                    if (_buildManager != null && !ai.Managers.Contains(_buildManager))
                    {
                        ai.Managers.Add(_buildManager);
                        Console.WriteLine("CcaManager: Added BabySharkBuildManager to BabySharkAI.Managers at frame 35 handoff");
                    }
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
                if (frame % 5 == 0)
                {
                    Console.WriteLine($"CcaManager.OnFrame: frame={frame} ccaMining={Settings.ccaMining}");
                }

                // FAILURE BEHAVIOR: If map data is not yet loaded, wait and allow default behavior
                // (Workers moving to center mineral) instead of issuing custom CCA commands.
                if (!Settings.MapDataLoaded)
                {
                    if (frame % 5 == 0) Console.WriteLine("CcaManager: Waiting for MapData to load...");
                    return Array.Empty<SC2APIProtocol.Action>();
                }

                var mapData = _miningManager?.CurrentMapData;
                if (mapData == null)
                {
                    return Array.Empty<SC2APIProtocol.Action>();
                }

                var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                var snapshot = Globals.CurrentObservation;

                if (frame == 0 && snapshot != null)
                {
                    _ccaService.InitializeFrameZero(mapData, startIndex, snapshot, _miningManager.WorkerLabelService, _miningManager.MineralLabelService);
                    // Explicitly enable CCA mining here
                    var state = _ccaService.GetOrCreateCurrentSpawnState(mapData, startIndex);
                    state.CcaMining = true;
                    Settings.ccaMining = true;
                    if (state.Phase == chrisCrossAppleSause.TestPhase.Idle) _ccaService.SetPhase(state, chrisCrossAppleSause.TestPhase.AssigningWorkers);
                }

                var currentAssignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);

                // Build live worker entries from current observation snapshot
                var liveWorkers = new List<WorkerEntryDto>();
                if (snapshot != null)
                {
                    foreach (var tag in snapshot.AvailableWorkers)
                    {
                        if (snapshot.SelfUnits.TryGetValue(tag, out var unit))
                        {
                            var label = _miningManager.WorkerLabelService?.GetLabel(tag) ?? string.Empty;
                            unit.Label = label;
                            unit.FinalLabel = label;
                            liveWorkers.Add(unit);
                        }
                    }
                }

                // After frame 35, ChrisCrossAppleSause unloads and BabySharkMiningManager takes over
                if (frame >= 35 && !_unregistered)
                {
                    Console.WriteLine($"CcaManager: frame {frame} reached, initiating handoff to BabySharkMiningManager.");
                    
                    // Capture final actions from CCA service at the handoff frame
                    var selfUnitsForHandoff = observation?.Observation?.RawData?.Units?.Where(u => u.Alliance == Alliance.Self);
                    var actionsAtHandoff = _ccaService.BuildBumpOrders(frame, mapData, startIndex, liveWorkers, null, selfUnitsForHandoff)?.ToList() ?? new List<SC2Action>();

                    // Signal mining started to unregister this manager
                    _miningManager.SignalMiningStarted();
                    
                    Console.WriteLine("CcaManager: Takeover successful.");
                    
                    return actionsAtHandoff;
                }

                var selfUnits = observation?.Observation?.RawData?.Units?.Where(u => u.Alliance == Alliance.Self);
                var actions = _ccaService.BuildBumpOrders(frame, mapData, startIndex, liveWorkers, null, selfUnits);
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
