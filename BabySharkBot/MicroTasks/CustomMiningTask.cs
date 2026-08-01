using Sharky.MicroTasks;
using Sharky;
using Sharky.Managers;
using Sharky.DefaultBot;
using Sharky.MicroTasks.Mining;
using BabySharkBot.Setup;

namespace BabySharkBot.MicroTasks
{
    /// <summary>
    /// Unit-level mining behavior that overrides debug drawing to prevent Sharky's default worker labels.
    /// Worker labels are instead drawn by BabySharkMiningManager using WorkerLabelService.
    /// Manages the actual unit commands for harvesting and returning cargo when not in CCA phase.
    /// </summary>
    public class CustomMiningTask : MiningTask
    {
        public CustomMiningTask(DefaultSharkyBot defaultSharkyBot, float priority, MiningDefenseService miningDefenseService, MineralMiner mineralMiner, GasMiner gasMiner)
            : base(defaultSharkyBot, priority, miningDefenseService, mineralMiner, gasMiner)
        {
        }

        public override System.Collections.Generic.IEnumerable<SC2APIProtocol.Action> PerformActions(int frame)
        {
            // During CCA and steady-state JIT/Speed mining, BabySharkMiningManager and CcaManager own the commands.
            // CustomMiningTask only exists to override Sharky's default debug drawing.
            return new System.Collections.Generic.List<SC2APIProtocol.Action>();
        }

        /// <summary>
        /// Override DebugUnits to disable Sharky's default mining label drawing.
        /// Custom labels are drawn by BabySharkMiningManager instead.
        /// </summary>
        public override void DebugUnits(DebugService debugService)
        {
            // Intentionally empty - do not call base.DebugUnits()
            // This prevents Sharky from drawing "Mining, Minerals, 0" labels
            // Custom labels are drawn by BabySharkMiningManager.OnFrame()
        }
    }
}
