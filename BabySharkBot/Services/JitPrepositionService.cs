using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using BabySharkBot.Setup;
using BabySharkBot.Managers;

namespace BabySharkBot.Services
{
    /// <summary>
    /// Predicts mineral depletion and moves idle workers proactively.
    /// Handles prepositioning for structures (e.g. Spawning Pool) using free workers.
    /// </summary>
    public class JitPrepositionService
    {
        private readonly DefaultSharkyBot _defaultBot;
        private readonly BabySharkMiningManager _miningManager;
        private ulong _selectedWorkerTag = 0;
        private bool _prepositioningStarted = false;
        private bool _buildingStarted = false;

        public JitPrepositionService(DefaultSharkyBot defaultBot, BabySharkMiningManager miningManager)
        {
            _defaultBot = defaultBot;
            _miningManager = miningManager;
        }

        public IEnumerable<SC2APIProtocol.Action> Update(int frame, uint currentMinerals)
        {
            var commands = new List<SC2APIProtocol.Action>();

            // Start prepositioning when we have ~160 minerals (adjust based on distance)
            if (currentMinerals >= 160 && !_prepositioningStarted)
            {
                _selectedWorkerTag = SelectOptimalTeam4Worker();
                if (_selectedWorkerTag != 0)
                {
                    _prepositioningStarted = true;
                    Console.WriteLine($"JitPrepositionService: Selected worker {_selectedWorkerTag} from Team 4 for Spawning Pool prepositioning.");
                }
            }

            if (_prepositioningStarted && !_buildingStarted && _selectedWorkerTag != 0)
            {
                var v2Pos = GetV2Position();
                if (v2Pos != null)
                {
                    // Target location for pool: next to V2
                    var buildPos = new Point2D { X = v2Pos.X + 3.0f, Y = v2Pos.Y }; // Position near V2

                    if (currentMinerals >= 200)
                    {
                        // Issue Build command
                        var buildCmd = new ActionRawUnitCommand
                        {
                            AbilityId = (int)Abilities.BUILD_SPAWNINGPOOL,
                            TargetWorldSpacePos = buildPos,
                            UnitTags = { _selectedWorkerTag }
                        };
                        commands.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = buildCmd } });
                        _buildingStarted = true;
                        Console.WriteLine($"JitPrepositionService: Issued BUILD_SPAWNINGPOOL to worker {_selectedWorkerTag} at ({buildPos.X:F2},{buildPos.Y:F2})");
                    }
                    else
                    {
                        // Just move to the location
                        var moveCmd = new ActionRawUnitCommand
                        {
                            AbilityId = 16, // MOVE
                            TargetWorldSpacePos = buildPos,
                            UnitTags = { _selectedWorkerTag }
                        };
                        commands.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = moveCmd } });
                    }
                }
            }

            return commands;
        }

        public ulong SelectedWorkerTag => _selectedWorkerTag;

        private ulong SelectOptimalTeam4Worker()
        {
            var mapData = _miningManager.CurrentMapData;
            if (mapData == null) return 0;

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);
            if (assignments == null || assignments.Count == 0) return 0;

            var team4 = assignments.FirstOrDefault(t => t.TeamNumber == 4);
            if (team4 == null) return 0;

            // Pick the worker that has just returned cargo (wasCarrying=true, carrying=false in Task)
            // or simply the first one for now.
            // In a full JIT loop, the Task would signal which worker is "free".
            return team4.Workers.FirstOrDefault()?.UnitTag ?? 0;
        }

        private Point2D GetV2Position()
        {
            var service = _miningManager.VespeneLabelService;
            var labels = service.GetAllVespeneLabels();
            if (labels.TryGetValue("V2", out var data))
            {
                return new Point2D { X = data.Position.X, Y = data.Position.Y };
            }
            return null;
        }
    }
}
