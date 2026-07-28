using SC2APIProtocol;
using Sharky.Managers;
using System;
using System.Collections.Generic;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Simple manager that only invokes BabySharkMiningManager.DrawDebugVisuals each frame.
    /// Added to DefaultSharkyBot.Managers so drawing runs in the main loop without modifying Sharky code.
    /// </summary>
    public class DrawOnlyManager : IManager
    {
        private readonly BabySharkMiningManager _miningManager;

        public DrawOnlyManager(BabySharkMiningManager miningManager)
        {
            _miningManager = miningManager ?? throw new ArgumentNullException(nameof(miningManager));
            NeverSkip = true;
        }

        public bool NeverSkip { get; set; }

        public bool SkipFrame { get; set; }

        public double LongestFrame { get; set; }

        public double TotalFrameTime { get; set; }

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            try
            {
                if (observation.Observation.GameLoop % 50 == 0)
                {
                    Console.WriteLine($"DrawOnlyManager.OnFrame: frame={observation.Observation.GameLoop}");
                }
                _miningManager?.DrawDebugVisuals(observation);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DrawOnlyManager: error drawing debug visuals: {ex.Message}");
            }

            return Array.Empty<SC2APIProtocol.Action>();
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
        }
    }
}
