using System;
using System.Collections.Generic;
using BabySharkBot.Setup;

namespace BabySharkBot.Services
{
    public sealed class chrisCrossAppleSause
    {
        public void EnableCcaMiningForCurrentSpawn(MawBaseLocationData mapData, int startIndex)
        {
            Settings.ccaMining = true;
        }

        public IEnumerable<SC2APIProtocol.Action> BuildBumpOrders(
            int frame,
            MawBaseLocationData mapData,
            int startIndex,
            IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            return Array.Empty<SC2APIProtocol.Action>();
        }

        public void RecordSpawnObservation(
            MawBaseLocationData mapData,
            int startIndex,
            List<List<TeamPatchAssignmentDto>> teamAssignmentsByStart,
            WorkerLabelService workerLabelService = null,
            int frame = -1,
            IReadOnlyList<WorkerEntryDto> workerEntries = null)
        {
        }
    }
}
