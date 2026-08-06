using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using BabySharkBot.Setup;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Manages scouting priorities and unit assignments for scouting tasks.
    /// Loaded on Frame 0 with high priority to claim units from ObservationManager.
    /// </summary>
    public class ScoutingManager : Sharky.Managers.SharkyManager
    {
        private readonly ActiveUnitData _activeUnitData;
        private readonly SharkyUnitData _sharkyUnitData;

        public ScoutingManager(ActiveUnitData activeUnitData, SharkyUnitData sharkyUnitData)
        {
            _activeUnitData = activeUnitData;
            _sharkyUnitData = sharkyUnitData;
        }

        public override bool NeverSkip => true;

        public override void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            // Scouting setup on frame 0
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            if (Globals.CurrentObservation?.ReadyForLabeling == null) return null;

            var ready = Globals.CurrentObservation.ReadyForLabeling;
            
            // 1. Overlord Scouting - Highest Priority on Frame 0
            foreach (var tag in ready.Overlord.ToList())
            {
                // Claim for Scouting task
                ClaimForScouting(tag, "Overlord");
                
                // Remove from ready list so other managers don't attempt to claim it
                ready.Overlord.Remove(tag);
            }

            // 2. Zergling Scouting - Shared with Army
            foreach (var tag in ready.Zergling.ToList())
            {
                // Scouting logic to determine if more scouts are needed (e.g. proxy search)
                bool needsScoutZergling = false; // logic would go here
                
                if (needsScoutZergling)
                {
                    ClaimForScouting(tag, "Zergling");
                    ready.Zergling.Remove(tag);
                }
                // If not claimed by Scouting, it remains in ReadyForLabeling.Zergling for ArmyManager
            }

            return null;
        }

        private void ClaimForScouting(ulong tag, string type)
        {
            // Placeholder for marking the unit as handled by ScoutingManager
            // Console.WriteLine($"[ScoutingManager] Claimed {type} (tag {tag}) for scouting.");
        }
    }
}
