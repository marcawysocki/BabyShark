using SC2APIProtocol;
using Sharky;
using Sharky.Builds.Zerg;
using Sharky.DefaultBot;

namespace BabySharkBot.Builds
{
    public class MutaliskRush : ZergSharkyBuild
    {
        public MutaliskRush(DefaultSharkyBot defaultSharkyBot) : base(defaultSharkyBot)
        {
        }

        public override void StartBuild(int frame)
        {
            base.StartBuild(frame);

            BuildOptions.StrictGasCount = true;

            MacroData.DesiredProductionCounts[UnitTypes.ZERG_HATCHERY] = 2;
        }

        public override void OnFrame(ResponseObservation observation)
        {
            if (MacroData.Minerals >= 20000)
            {
                if (MacroData.DesiredTechCounts[UnitTypes.ZERG_SPAWNINGPOOL] < 1)
                {
                    MacroData.DesiredTechCounts[UnitTypes.ZERG_SPAWNINGPOOL] = 1;
                }
            }
        }

        public override bool Transition(int frame)
        {
            return false;
        }

        public new string Name()
        {
            return "MiningSpawningPoolTest";
        }
    }
}
