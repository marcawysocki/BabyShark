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

            // Ensure debug visualizations are rendered while this manager is active
            var ai = BabySharkBot.BabySharkAI.Instance;
            if (ai != null)
            {
                if (!ai.Managers.Contains(_drawOnlyWrapper))
                {
                    ai.Managers.Add(_drawOnlyWrapper);
                    Console.WriteLine("CcaManager: added DrawOnlyManager to BabySharkAI.Managers");
                }
                else
                {
                    Console.WriteLine("CcaManager: DrawOnlyManager already present in Managers");
                }
            }
            else
            {
                Console.WriteLine("CcaManager: BabySharkAI.Instance is null - cannot add DrawOnlyManager");
            }
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
                if (frame % 5 == 0)
                {
                    Console.WriteLine($"CcaManager.OnFrame: frame={frame} ccaMining={Settings.ccaMining}");
                }
                var mapData = _miningManager?.CurrentMapData;
                if (mapData == null)
                {
                    return Array.Empty<SC2APIProtocol.Action>();
                }

                var startIndex = mapData?.StartingTownHall != null ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                var currentAssignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);

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

                // After frame 35, ChrisCrossAppleSause unloads and BabySharkMiningManager takes over
                if (frame >= 35 && !_unregistered)
                {
                    Console.WriteLine($"CcaManager: frame {frame} reached, initiating handoff to BabySharkMiningManager.");
                    
                    // Signal mining started to unregister this manager and DrawOnlyManager
                    _miningManager.SignalMiningStarted();
                    
                    // Ensure BabySharkMiningManager is registered in BabySharkAI.Managers
                    var ai = BabySharkBot.BabySharkAI.Instance;
                    if (ai != null)
                    {
                        ai.EnsureManagersRegistered();
                    }
                    
                    // System break after takeover to verify BabySharkMiningManager is running as requested
                    try
                    {
                        Console.WriteLine("CcaManager: Takeover successful, triggering system break.");
                        System.Diagnostics.Debugger.Break();
                    }
                    catch { }
                    
                    return Array.Empty<SC2Action>();
                }

                var actions = _ccaService.BuildBumpOrders(frame, mapData, startIndex, liveWorkers, null);
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
