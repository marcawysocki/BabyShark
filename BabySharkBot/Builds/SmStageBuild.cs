using System;
using System.Collections.Generic;
using SC2APIProtocol;
using Sharky.DefaultBot;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// SM-harvest-point verify/tipping/staging build (owner spec 2026-10-29
    /// 12:11). No production, no buildings, no worker commands from the build
    /// side — the SmStageService (invoked by the mining manager when
    /// Settings.SmStageMode) owns every worker command: per far mineral it
    /// verifies the sweep's ideal stand point (Smart from 1.87u + harvest
    /// check), scans +/- 1 deg approach angles for the tipping point, and
    /// probes the staging area around the stand. Results land in the
    /// worker_commands_*.jsonl telemetry as SmStageSample records.
    /// </summary>
    public class SmStageBuild : BabySharkBuild
    {
        public SmStageBuild(DefaultSharkyBot defaultSharkyBot) : base(defaultSharkyBot)
        {
        }

        public override void OnStart(int frame)
        {
            base.OnStart(frame);
            Console.WriteLine("[SMSTAGE] build started — no production; stage service owns all worker commands");
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            // Intentionally empty: nothing may issue commands but the stage service.
            return Array.Empty<SC2APIProtocol.Action>();
        }
    }
}
