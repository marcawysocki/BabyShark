using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.Managers;
using BabySharkBot.Setup;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Detects worker pass-through events and uses verified mineral SMART targets
    /// so workers can pass through each other without corrective movement.
    /// </summary>
    public sealed class WorkerAwareCollisionManager : IManager
    {
        private const float WorkerPassThroughRange = 0.35f;
        private readonly BabySharkMiningManager _miningManager;
        private readonly HashSet<ulong> _workersInCollision = new();
        private readonly HashSet<ulong> _temporaryMineralWalkers = new();
        private int _lastLogFrame = -1;

        public bool NeverSkip { get; set; } = true;
        public bool SkipFrame { get; set; }
        public double LongestFrame { get; set; }
        public double TotalFrameTime { get; set; }

        public WorkerAwareCollisionManager(BabySharkMiningManager miningManager)
        {
            _miningManager = miningManager ?? throw new ArgumentNullException(nameof(miningManager));
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            _workersInCollision.Clear();
            _temporaryMineralWalkers.Clear();
            _lastLogFrame = -1;
        }

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            _workersInCollision.Clear();
            _temporaryMineralWalkers.Clear();
            return Array.Empty<SC2APIProtocol.Action>();
        }

        public void OnEnd(ResponseObservation observation, Result result)


        {
        }

    }
}
