using SC2APIProtocol;
using Sharky;

namespace BabySharkBot.Setup
{
    public static class MacroDataUpdater
    {
        /// <summary>
        /// Hydrates MacroData from the current observation.
        /// Run this every frame immediately after (or inside) your observation handling
        /// and before any BabySharkBuild.OnFrame calls.
        /// </summary>
        public static void UpdateFromObservation(ResponseObservation observation, MacroData macroData)
        {
            if (observation?.Observation?.PlayerCommon == null || macroData == null)
                return;

            var pc = observation.Observation.PlayerCommon;
            macroData.FoodUsed = (int)pc.FoodUsed;
            macroData.FoodLeft = (int)pc.FoodCap - macroData.FoodUsed;
            macroData.FoodArmy = (int)pc.FoodArmy;
            macroData.FoodWorkers = (int)pc.FoodWorkers;
            macroData.Minerals = (int)pc.Minerals;
            macroData.VespeneGas = (int)pc.Vespene;
            macroData.Frame = (int)observation.Observation.GameLoop;
        }
    }
}
