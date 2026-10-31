using System;
using System.Collections.Generic;
using SC2APIProtocol;
using Sharky.DefaultBot;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// SM staging-handoff build (owner spec 2026-10-29 16:37). No production, no
    /// buildings, no worker commands from the build side — the SmHandoffService
    /// (invoked by the mining manager when Settings.SmHandoffMode) owns every
    /// worker command: per far mineral, worker A harvests (approach at 1.87u +
    /// Smart) while worker B holds at the staging spot (ideal stand + radius
    /// toward the hatchery); when A has accumulated 32 harvest frames, B gets a
    /// Smart on the mineral. Results land in the worker_commands_*.jsonl
    /// telemetry as SmHandoffSample records.
    /// </summary>
    public class SmHandoffBuild : BabySharkBuild
    {
        public SmHandoffBuild(DefaultSharkyBot defaultSharkyBot) : base(defaultSharkyBot)
        {
        }

        public override void OnStart(int frame)
        {
            base.OnStart(frame);
            Console.WriteLine("[SMHANDOFF] build started — no production; handoff service owns all worker commands");
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            // Intentionally empty: nothing may issue commands but the handoff service.
            return Array.Empty<SC2APIProtocol.Action>();
        }
    }
}
