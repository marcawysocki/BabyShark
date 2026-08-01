using System.Linq;
using System.Collections.Generic;
using SC2APIProtocol;
using Sharky;

namespace PupusPistrixVectatorPestiumBot.Builds
{
    public static class DroneProductionSnippet
    {
        // Call from a macro task or every-frame production routine.
        // Returns SC2 actions to send to the client.
        public static IEnumerable<SC2APIProtocol.Action> MorphDrone(int frame, ActiveUnitData activeUnitData, MacroData macroData)
        {
            var actions = new List<SC2APIProtocol.Action>();

            // Basic resource/supply checks (Drone cost = 50 minerals)
            if (macroData.Minerals < 50) return actions;
            if (macroData.FoodLeft <= 0) return actions;

            // Find an idle larva (unit wrapper used by ActiveUnitData)
            var larva = activeUnitData.SelfUnits.Values
                .Where(u => u.Unit.UnitType == (uint)UnitTypes.ZERG_LARVA && u.Unit.Orders.Count == 0)
                .OrderBy(u => u.FrameLastSeen)
                .FirstOrDefault();

            if (larva == null) return actions;

            // Find the UnitCommander for that larva and issue the training order
            if (activeUnitData.Commanders.TryGetValue(larva.Unit.Tag, out var commander))
            {
                // commander.Order(...) should return IEnumerable<SC2APIProtocol.Action>
                var cmd = commander.Order(frame, Abilities.TRAIN_DRONE);
                if (cmd != null)
                {
                    actions.AddRange(cmd);
                }
            }

            return actions;
        }
    }
}