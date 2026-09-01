using System;
using System.Collections.Generic;
using SC2APIProtocol;
using Sharky.Managers;
using BabySharkBot.Services;

namespace BabySharkBot.Managers
{
    public class CcaManager : IManager
    {
        private readonly chrisCrossAppleSause _ccaService;
        private readonly BabySharkMiningManager _miningManager;
        private bool _unregistered;

        public bool NeverSkip { get; set; } = true;
        public bool SkipFrame { get; set; }
        public double LongestFrame { get; set; }
        public double TotalFrameTime { get; set; }
        public chrisCrossAppleSause CcaMiningService => _ccaService;
        public DrawOnlyManager DrawOnlyWrapper { get; }

        public CcaManager(chrisCrossAppleSause ccaService, BabySharkMiningManager miningManager)
        {
            _ccaService = ccaService ?? throw new ArgumentNullException(nameof(ccaService));
            _miningManager = miningManager ?? throw new ArgumentNullException(nameof(miningManager));
            DrawOnlyWrapper = new DrawOnlyManager(miningManager);
            _miningManager.OnMiningStarted += HandleMiningStarted;
        }

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            return Array.Empty<SC2APIProtocol.Action>();
        }

        private void HandleMiningStarted()
        {
            if (_unregistered)
            {
                return;
            }

            _unregistered = true;
            var ai = BabySharkBot.BabySharkAI.Instance;
            ai?.Managers.RemoveAll(manager => manager == this);
            _miningManager.OnMiningStarted -= HandleMiningStarted;
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            _unregistered = false;
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
        }
    }
}
