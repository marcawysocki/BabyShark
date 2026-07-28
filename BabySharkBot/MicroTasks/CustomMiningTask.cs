using Sharky.MicroTasks;
using Sharky;
using Sharky.Managers;
using Sharky.DefaultBot;
using Sharky.MicroTasks.Mining;
using BabySharkBot.Setup;

namespace BabySharkBot.MicroTasks
{
    /// <summary>
    /// Custom MiningTask that overrides debug drawing to prevent Sharky's default worker labels.
    /// Worker labels are instead drawn by BabySharkMiningManager using WorkerLabelService.
    /// </summary>
    public class CustomMiningTask : MiningTask
    {
        public CustomMiningTask(DefaultSharkyBot defaultSharkyBot, float priority, MiningDefenseService miningDefenseService, MineralMiner mineralMiner, GasMiner gasMiner)
            : base(defaultSharkyBot, priority, miningDefenseService, mineralMiner, gasMiner)
        {
        }

        public override System.Collections.Generic.IEnumerable<SC2APIProtocol.Action> PerformActions(int frame)
        {
            if (Settings.ccaMining)
            {
                return new System.Collections.Generic.List<SC2APIProtocol.Action>();
            }
            return base.PerformActions(frame);
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
