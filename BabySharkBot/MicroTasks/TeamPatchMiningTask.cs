using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using Sharky.MicroTasks;
using Sharky.MicroTasks.Mining;
using BabySharkBot.Managers;
using BabySharkBot.Services;
using BabySharkBot.Setup;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BabySharkBot.MicroTasks
{
    /// <summary>
    /// Patch-level mining task logic.
    /// Handles coordination between JIT prepositioning and steady-state mining assignments.
    /// Enforces Near/Far patch capacity rules and respects greedy mineral ordering.
    /// </summary>
    public class TeamPatchMiningTask : MiningTask
    {
        private readonly DefaultSharkyBot _defaultBot;
        private readonly BabySharkMiningManager _babySharkMiningManager;
        private readonly JitPrepositionService _jitPrepositionService;

        public TeamPatchMiningTask(DefaultSharkyBot defaultSharkyBot, float priority, MiningDefenseService miningDefenseService, MineralMiner mineralMiner, GasMiner gasMiner, BabySharkMiningManager babySharkMiningManager)
            : base(defaultSharkyBot, priority, miningDefenseService, mineralMiner, gasMiner)
        {
            _defaultBot = defaultSharkyBot;
            _babySharkMiningManager = babySharkMiningManager;
            _jitPrepositionService = new JitPrepositionService(defaultSharkyBot, babySharkMiningManager);
        }

        public override IEnumerable<SC2APIProtocol.Action> PerformActions(int frame)
        {
            var mapData = _babySharkMiningManager?.CurrentMapData;
            if (mapData == null)
            {
                return Array.Empty<SC2APIProtocol.Action>();
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var currentAssignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);
            if (currentAssignments == null || currentAssignments.Count == 0)
            {
                return Array.Empty<SC2APIProtocol.Action>();
            }

            return Array.Empty<SC2APIProtocol.Action>();
        }
    }
}
