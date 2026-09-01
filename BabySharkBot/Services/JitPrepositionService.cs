using System;
using System.Collections.Generic;
using BabySharkBot.Managers;

namespace BabySharkBot.Services
{
    public class JitPrepositionService
    {
        private readonly BabySharkMiningManager _miningManager;

        public JitPrepositionService(object defaultBot, BabySharkMiningManager miningManager)
        {
            _miningManager = miningManager ?? throw new ArgumentNullException(nameof(miningManager));
        }

        public IEnumerable<SC2APIProtocol.Action> Update(int frame, uint currentMinerals)
        {
            return Array.Empty<SC2APIProtocol.Action>();
        }

        public ulong SelectedWorkerTag => 0;
    }
}
