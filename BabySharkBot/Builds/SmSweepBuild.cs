using System;
using System.Collections.Generic;
using SC2APIProtocol;
using Sharky.DefaultBot;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// SM-harvest-point angular sweep build (owner spec 2026-10-29 08:39).
    ///
    /// No production, no buildings, no worker commands from the build side —
    /// the SmSweepService (invoked by the mining manager when
    /// Settings.SmSweepMode) owns every worker command: two workers per far
    /// mineral (SB, BB, BA, YB, YA) leapfrog Smart commands from approach
    /// points 1.87u out, sweeping the approach angle +5 deg until the stand
    /// point's distance to hatchery circle 1 worsens, back down in 2 deg steps,
    /// then 1 deg fill. Results land in the worker_commands_*.jsonl telemetry
    /// as SmSweepSample records.
    /// </summary>
    public class SmSweepBuild : BabySharkBuild
    {
        public SmSweepBuild(DefaultSharkyBot defaultSharkyBot) : base(defaultSharkyBot)
        {
        }

        public override void OnStart(int frame)
        {
            base.OnStart(frame);
            Console.WriteLine("[SMSWEEP] build started — no production; sweep service owns all worker commands");
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            // Intentionally empty: nothing may issue commands but the sweep service.
            return Array.Empty<SC2APIProtocol.Action>();
        }
    }
}
