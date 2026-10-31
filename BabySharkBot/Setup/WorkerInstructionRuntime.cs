using System;
using System.Collections.Generic;

namespace BabySharkBot.Setup
{
    public enum WorkerInstructionCommand
    {
        Move,
        GatherAndMove,
        MoveAndGather,
        Gather,
        Return,
        BuildExtractor,
        // 5x5 rotation (owner spec 2026-10-13, B2 pool): issues
        // BUILD_SPAWNINGPOOL at the persisted mini-wall placement for the
        // current spawn. B2's book reaches this row only after the mineral-
        // gated wait row confirms the bank holds SpawningPoolBuildMinerals.
        BuildSpawningPool,
        // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): issues
        // BUILD_HATCHERY at the Macro Hatchery site (Point =
        // MacroHatcherySite) after the mineral-gated wait row confirms the
        // bank. TERMINAL row — the drone morphs INTO the hatchery and the
        // mining manager's milestone watcher freezes the bot on success or
        // failure.
        BuildHatchery,
        Wait,
        Jump,
        Stop,
        StoreTargetPoint,
        UseTargetPoint,
        LoadInstructionSet
    }

    public enum WorkerInstructionPoint
    {
        None,
        Harvest,
        Return,
        Staging,
        BumpPartner,
        BumpMidpoint,
        BumpHarvestCircle,
        BumpCcaWaitCircle,
        // Alignment gate for the experiment-1 bump walk: resolves to the worker's own
        // position while role 3 and its role-1 partner lie on the hatchery-to-A-mineral
        // line (role 3 closer to the hatchery); null while unaligned, so the row waits.
        BumpAlignedGate,
        // Clearance gate for the 5x5 wait-role mineral walk (T5/Y5/Y3): resolves to the
        // worker's own position once the walker is clear of the partner's line — the
        // partner is meaningfully ahead toward its own mineral (or the walker reached the
        // walk mineral, or the hold timed out); null while the walker must keep walking,
        // so the inverted-gate jump keeps re-issuing the walk gather.
        WaitWalkClearGate,
        // 5x5 T1 first set ("T1 - T4 Bump Set"): the half-way point between the TA
        // speed-mining harvest point and the TA speed-mining return point — the
        // midpoint of the {TA,TA} self-pair row's HarvestA/ReturnPoint (the two
        // ends of the SM line segment). The initial T1 moves park it here, in
        // front of the T4 bumper. Resolved from the row's {TA,TA} pair reference.
        SmHarvestReturnMidpoint,
        // 5x5 T4/Y4 bumper gate ("T1 - T4 Bump Set"): resolves to the partner's (T4:
        // T1, Y4: Y1) live position once the bumper has reached the bumper point — the
        // point where the mineral-walk line (hatchery -> walk mineral) passes the
        // partner's A-mineral speed-mining harvest point, computed as the closest point
        // on that walk segment to the {TA,TA}/{YA,YA} SM harvest point — so the row's
        // Move switches the bumper off the mineral walk and onto a bump move behind its
        // partner. Null while the bumper is still short of the bumper point; fails open
        // to the partner's live position at relative frame 8 (the start of the 8-25
        // partner chase) so a blocked walk can never park the bumper on the walk
        // mineral.
        BumpSmBumper,
        // 5x5 chase-bump follow target (owner spec 2026-10-20 "South New Worker
        // Order", REPLACES the retired distance triggers): resolves to the chased
        // partner's live position (T4/T5: T1, Y4/B2: Y1) — the chase Move rows are
        // purely frame-gated (relative frames 6-18). The Y-side roles (Y4/B2)
        // hold until the walker is CLEAR of every role-1/2/3 worker (0.4u
        // radius): null while any is inside the radius ("Mineral Walk to SB
        // until they are clear of any workers"), so the frame-gated row waits
        // silently and the SB walk gather keeps carrying the worker; deadline
        // fail-open at relative frame 20 so a permanently crowded corridor can
        // never park the walker out of the frame-19 SB walk row.
        BumpChasePartner,
        // 5x5 chase-bump clearance gate (owner spec 2026-10-19): resolves to the
        // worker's own position once NO role-1/2/3 worker (T1/T2/T3/Y1/Y2/Y3) is
        // within 0.4u — the walker has cleared the miners' corridors around the
        // central walk mineral (SA) and can safely move to its own B-mineral
        // staging area. The four chase-bump walkers (T4/T5/Y4/B2) are NOT
        // blockers — they share the central mineral and must never gate each
        // other (B2's label ends in 2, but in this choreography it is the Y-line
        // backstop, not a role-2 miner). Null while any role-1/2/3 worker is
        // inside the radius, so the inverted-gate jump keeps re-issuing the SA
        // mineral walk. Timeout fail-open so a permanently crowded mineral can
        // never park the walker on the gather forever.
        BumpClearOfWorkersGate,
        // 5x5 B2 pool build (owner spec 2026-10-13): the persisted spawning-pool
        // placement (mini-wall spot) for the current spawn — B2's "Move to Pool"
        // rows walk to this point after depositing its cargo, so the mineral
        // wait and the BUILD_SPAWNINGPOOL row fire with B2 standing at the site.
        // Resolved from Globals.CurrentMapData.SpawningPoolPlacements, never from
        // the row TargetId's mining target.
        PoolPlacement,
        // 13-B3 extractor trick (owner spec 2026-10-15): the main-base VA geyser
        // position from the persisted vespene ordering — B3's approach-move rows
        // walk onto the geyser before the mineral-gated build row fires. Resolved
        // from Globals.CurrentMapData.OrderedMainVespene, never from the row
        // TargetId's mining target.
        VaGeyser,
        // 13-B3 extractor trick redirect gate: resolves to the VA geyser position
        // only once the walker is within 0.2u of the walk mineral's harvest
        // standoff distance (just before arrival — never on the mineral itself);
        // null while it must keep walking, so the Move row waits silently and the
        // ONE issued mineral walk keeps carrying the worker.
        VaWalkRedirect,
        // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): the build/test
        // site for the current spawn — the frame-0 latched Macro Hatchery
        // final placement, falling back to the owner-verified placements
        // table. Map data, never resolved from the row TargetId's mining
        // target.
        MacroHatcherySite,
        JitHarvestA,
        JitHarvestB,
        // jitMHFineTune (owner spec 2026-10-22, T1 fine-tuning): the MOVE-THROUGH
        // point — the speed-mining harvest point mirrored across the mineral
        // center (ResourcePosition), so the frame-20 walk passes THROUGH the
        // mineral footprint instead of decelerating at the ~0.2u-short standoff
        // the telemetry showed. Falls back to the plain harvest point when the
        // mineral center is unknown. Resolved from the row TargetId's mining
        // target.
        HarvestThrough,
        JitWaitPointA,
        JitWaitPointB,
        JitReturnPoint,
        // 5x5 A-miner first trip (owner spec 2026-10-09): the first-harvest approach
        // point computed to minimize total travel |worker start -> P| + |P -> SM
        // return point| over the candidate standoff points around the mineral (the
        // harvest/SM harvest points plus samples on the harvest circle). The SM
        // harvest point is NOT forced for the first trip — the objective is to be
        // harvesting/returning as soon as possible; the SECOND trip uses the SM
        // harvest point (self-pair row). Resolved live in the mining manager so the
        // worker's position at row-resolution time (frame 0 = start position) is
        // the origin of the calculation.
        OptimalFirstHarvest,
        StoredTarget,
        // New-logic staging point: the row TargetId mineral's persisted CCA wait
        // point (one unit farther from the mineral center than the JIT harvest
        // point). A staged worker stands here — outside the footprint — until the
        // harvest-release gate (WorkerInstruction.HarvestReleaseGate) says the
        // mineral is free or its active harvester has ACCUMULATED
        // HarvestFrameTracker.StagedReleaseHarvestFrames (32) harvest frames
        // (owner spec 2026-10-20 20:09), so the worker is never the standing 1183
        // waiter the game re-pathes away after 15 frames. Resolved from the team
        // assignments by mineral UnitTag, with the team JIT wait point as fallback.
        CcaWait,
        // T1 arrival test battery (owner spec 2026-10-24 18:16), --t1test=N:
        // SmHarvestForce — the row TargetId mineral's SmHarvestPoint
        // UNCONDITIONALLY (Point.Harvest only resolves the SM point when global
        // speed-mining is active; the test books need it from frame 0). Falls
        // back to HarvestPoint when the SM point is zero.
        // SmMidpoint — the "Green Star": halfway between the mineral's
        // SmHarvestPoint and SmReturnPoint.
        // StarGatedChase — the chased partner's (T4/T5: T1) LIVE position, gated:
        // null until T1 is within 0.2u of ITS OWN TA SmMidpoint, so T4/T5 keep
        // mineral-walking until T1 reaches the star; deadline fail-open at
        // relative frame 22 so a missed star can never park the walker.
        // ChaseBreakOffGate — an InvertGate-jump gate: resolves (non-null) once
        // the FOLLOWER is within 0.5u of the partner's SmHarvestPoint (following
        // closer would shove the partner off the mineral) OR at the relative
        // frame 26 deadline; null while the follow should continue.
        // ClearOfPartnerGate — an InvertGate-jump gate: resolves once the walker
        // is >= 0.4u clear of the partner (T1) OR at the relative frame 19
        // deadline; null while T1 is inside the walker's radius, so the loop
        // keeps mineral-walking without bumping.
        // StarRedirect — the row TargetId mineral's SmHarvestPoint, gated on the
        // WORKER ITSELF being within 0.2u of the mineral's SmMidpoint (the
        // Green Star): null while the worker is still approaching the star, so a
        // Move row carrying this point redirects the worker from the star to the
        // SM harvest only at star proximity; relative-frame-24 deadline fail-open
        // returns the SM harvest unconditionally so the book can never stall.
        // RollingLine — anti-deceleration rolling waypoint (owner spec
        // 2026-10-25 09:24): the worker's CURRENT position plus 0.5u along the
        // unit direction from the worker's frame-0 position (captured on first
        // resolve) through the row TargetId mineral's ResourcePosition — the
        // start->mineral line extended through the mineral. Re-resolved every
        // frame, the waypoint always recedes, so the worker never decelerates
        // into an arrival.
        // HarvestRangeGate — an InvertGate-jump gate for the rolling book:
        // resolves (non-null) once the worker is within 0.35u of the row
        // mineral's ResourcePosition (close enough to harvest), or at the
        // relative-frame-40 deadline; null while still approaching, so the jump
        // keeps re-issuing the rolling move.
        // DecelPlan — variant 9 anti-deceleration approach (owner spec
        // 2026-10-25 16:17, tests 18/19): before the latch the row TargetId
        // mineral's HarvestPoint (the worker's normal destination, same anchor
        // as the rolling tests); once the worker is within 0.7u of it (~1 frame
        // before the game's arrival deceleration onset) the target latches to a
        // FIXED point 2u beyond the HarvestPoint along the frame-0-origin ->
        // HarvestPoint line, so the worker cruises through the footprint at
        // full cruise speed. After the latch the cached point keeps being
        // returned (re-issuing a fixed target does not restart the game's
        // move) so the gate row below it keeps evaluating every frame.
        // T2 is special (owner spec): the latch fires on the FIRST resolve
        // (frame 0) with a 0.5u extension, and there is no gate — the Smart is
        // a plain queued row at the worker's current smart frame.
        // LeadHarvestGate — an InvertGate-jump gate for the DecelPlan book:
        // resolves (non-null) once the worker is within 0.45u of the mineral OR
        // is projected to be within 0.45u in `lead` frames (lead by role:
        // T3/Y1 = 1, Y2/B2 = 2, Y3 = 3, T1 = 4, S2 = 5 — the owner-named
        // projection-lead samples), or at the relative-frame-40 deadline; null
        // while still approaching, so the jump keeps re-issuing the DecelPlan
        // move until the Smart transition frame.
        // RollingReturnPoint — variant 14 return leg (owner question
        // 2026-10-25 "can this technique be applied to the return trip going
        // back to the hatchery"): the homing 2u rolling waypoint toward the
        // worker's StoredTargetPoint — the pair-table ReturnPoint the setup
        // row just stored — the same anti-deceleration receding target as the
        // homing harvest leg, so the drone arrives at the hatchery footprint
        // at full cruise instead of decelerating into the fixed return point.
        // ReturnRangeGate — an InvertGate-jump gate for the rolling return
        // book: resolves once the worker is within 0.45u of the
        // StoredTargetPoint, or 40 frames after the return leg began
        // (per-worker anchor — the leg starts ~100 frames in, so the harvest
        // gates' frame-0 relative anchor cannot be reused).
        SmHarvestForce,
        SmMidpoint,
        StarGatedChase,
        ChaseBreakOffGate,
        ClearOfPartnerGate,
        StarRedirect,
        RollingLine,
        HarvestRangeGate,
        DecelPlan,
        LeadHarvestGate,
        RollingReturnPoint,
        ReturnRangeGate,
        // Bump choreography (owner spec 2026-10-26 18:45, --t1test=34/35,
        // variant 17 — the "green line" T1 push with T4/T5 staging handoffs).
        // All resolve from persisted patch data / live worker positions BEFORE
        // the MiningTargets lookup, keyed by mineral label carried in
        // TargetPointReference:
        // ChoreoGreenLine — T1's frame-0 target (owner Paint 2026-10-26 20:47):
        // the latched start THROUGH the TA mineral (HarvestPoint anchor — the
        // real unit position; ResourcePosition is ~0.9u off), extended 2u past
        // the mineral so T1 rolls through without decelerating.
        // ChoreoSmHarvestLabel — the labeled mineral's SmHarvestPoint (the
        // frame-6 T1 redirect target, T4's TB/TA legs, T5's TA leg).
        // ChoreoSaIntersect — T5's frame-0 move: the ray from T5's latched
        // start toward SA's SmHarvestPoint, extended 2u past its crossing
        // with the TA SM (SmReturn->SmHarvest) line; falls back to 2u past
        // SA's SmHarvestPoint when the ray misses the line.
        // ChoreoWaitCircle2Label — the labeled mineral's OUTER-circle stand
        // point (harvest radius + 0.4u on the hatchery side) — the Circle 2
        // staging point T4/T5 wait at for the T2/S2 32-frame handoff.
        // ChoreoT1ArrivalGate — InvertGate-jump gate: null while T1 is NOT
        // within one frame's travel of the TA SmHarvestPoint (floor relative
        // frame 16, fail-open 30); non-null the frame T1 will arrive next
        // frame, so the SB mineral-walk / T1 Smart fire on that exact frame.
        // ChoreoClearOfGate — InvertGate-jump gate: null while the WORKER is
        // within 1.0u of the live role named in TargetPointReference ("T1"
        // for T4, "T3" for T5), non-null once clear (fail-open frame 45).
        // ChoreoSmLineExtend — T1's frame-10 redirect target (owner spec
        // 2026-10-27 11:16 "switch to the harvest line extended 2u
        // intersecting SMH ... Let T4 bump T1 off that target line ... then
        // target the mineral"): the TA SmHarvestPoint extended 2u along the
        // SmReturn->SmHarvest direction — T1 rolls THROUGH the SM point at
        // cruise speed. The resolver latches T1 on the line and, once T4's
        // bump knocks T1 off it (or T1 reaches the extension end), switches
        // the re-issue target to the SM harvest point; the SMH arrival gate
        // then fires the Smart. (Superseded the 10-26 frame-6 SM-line spec
        // and the 10-27 09:18 move-to-SMH spec — both decelerated T1.)
        // ChoreoConvergence — SUPERSEDED by the 2026-10-27 14:16 fix ("T1 is
        // being told to stay in place"): the closest point on T1's
        // start->TA-mineral line to the TA SM harvest point sits only ~0.45u
        // from spawn, so T1 braked to a standstill by f8. T1's frame-0 target
        // is ChoreoGreenLine again (rolling target 2u PAST the live mineral);
        // this resolver is retained for reference only.
        ChoreoGreenLine,
        ChoreoSmHarvestLabel,
        ChoreoSaIntersect,
        ChoreoWaitCircle2Label,
        ChoreoT1ArrivalGate,
        ChoreoClearOfGate,
        ChoreoSmLineExtend,
        ChoreoConvergence,
        // ChoreoT4AngleStart — owner test suite 2026-10-28 08:14: T4's frame-0
        // MOVE target when --t4angle=D is set — the worker's latched start
        // along its start -> SA-mineral-center heading rotated by D degrees,
        // 4u out (no deceleration before the frame-8 TB switch). The row's
        // TargetPointReference names the heading mineral ("SA").
        ChoreoT4AngleStart
    }

    public sealed class WorkerInstruction
    {
        public string InstructionSet { get; set; } = string.Empty;
        public WorkerInstructionCommand Command { get; set; }
        public WorkerInstructionPoint Point { get; set; }
        public ulong TargetId { get; set; }
        public int RelativeFrame { get; set; } = -1;
        public int PreviousAbilityId { get; set; } = -1;
        public int TargetAbilityId { get; set; } = -1;
        // Optional second ability id accepted by the same row gate: the row passes when
        // CurrentAbilityId equals TargetAbilityId OR TargetAbilityId2 (when >= 0). Lets one
        // row match either drone ability (1183 gather / 1184 return) — sequential rows
        // cannot express an OR because an unsatisfied gate blocks every row behind it.
        public int TargetAbilityId2 { get; set; } = -1;
        public bool RequireResources { get; set; }
        public float PositionTolerance { get; set; } = 0.25f;
        public bool Queue { get; set; }
        public bool NoCondition { get; set; }
        public bool StoreTargetPoint { get; set; }
        public string TargetPointReference { get; set; } = string.Empty;
        public int NextTargetIndex { get; set; } = -1;
        public int JumpToInstructionIndex { get; set; } = -1;
        public string NextInstructionSet { get; set; } = string.Empty;
        // Inverts a Jump row's position gate: the jump fires while the gate point is
        // UNresolved (gate row still waiting) and passes through when it resolves.
        // Used by the bump walk loop to re-issue the 1183 harvest every frame until
        // the BumpAlignedGate resolves, then fall through to the stop row.
        public bool InvertGate { get; set; }
        // Relative jump target (owner fix 2026-10-19): when true, the jump targets
        // the row DIRECTLY ABOVE itself (CurInstrIdx - 1) instead of the absolute
        // JumpToInstructionIndex. BuildMineralInstructionList PREPENDS rows for
        // single-target temp roles (B2's {BB,BB} self-pair setup), which shifts
        // every absolute index in the startup set — the chase-bump clearance jump
        // sits immediately below its walk-gather row, so "previous row" is
        // prepend-proof. JumpToInstructionIndex is ignored when this is set.
        public bool JumpToPreviousRow { get; set; }
        // New-logic harvest-release gate (owner specs 2026-10-11 / 2026-10-20 20:09),
        // valid on Wait rows: the row passes when the row TargetId mineral has NO
        // active harvester (free right now) or its active harvester has ACCUMULATED
        // HarvestFrameTracker.StagedReleaseHarvestFrames (32) harvest frames —
        // "All Workers needs to stage until the active mineral has 32 Harvest
        // frames". While neither holds the row blocks with no command, leaving the
        // worker standing at the CcaWait staging point — never on the busy
        // mineral's footprint where the game would re-path it away after 15 frames.
        public bool HarvestReleaseGate { get; set; }
        // Free-only release gate (owner report 2026-10-25 21:36, B2 stops mining):
        // valid on Wait rows carrying HarvestReleaseGate. When true the 32-frame
        // accumulated-harvest clause is skipped — the row passes ONLY when the
        // target mineral has no active harvester. B2's second BB approach needs
        // this: Y5's swing book parks on BB for 120+ frames, so the 32-frame
        // clause released B2 onto an occupied footprint, its one-shot Smart
        // bounced, and the book deadlocked into wait(1183->1184) with no cargo.
        public bool HarvestReleaseFreeOnly { get; set; }

        // Frames-only release gate (owner spec 2026-10-26 18:45, the T4->TB /
        // T5->SB staging handoffs): valid on Wait rows carrying
        // HarvestReleaseGate. When true the free-mineral clause is skipped —
        // the row passes ONLY when the target mineral HAS an active harvester
        // that has accumulated StagedReleaseHarvestFrames (32) harvest frames
        // ("wait for T2 to reach 32 Harvest frames then issue a Smart on TB").
        // The free-clause would pass the moment T4/T5 arrive at the stage
        // point, long before T2 starts mining.
        public bool HarvestReleaseFramesOnly { get; set; }
        // Mineral gate (owner spec 2026-10-13, B2 pool build), valid on Wait rows:
        // the row passes only when the mineral bank holds at least this many
        // minerals (-1 = disabled, the default). Rows carrying the gate ignore
        // the cargo-handoff edge, so a handoff wait immediately in front cannot
        // leak the gate through on the deposit frame.
        public int RequireMinerals { get; set; } = -1;
        // Absolute frame gate (owner spec 2026-10-13, Y2 BB trip), valid on Wait
        // rows: the row passes only once the ABSOLUTE game frame reaches this
        // value (-1 = disabled). Same cargo-handoff exclusion as RequireMinerals.
        // Command rows (owner spec 2026-10-22, jitRMFineTune): the value acts as an
        // absolute EARLIEST-EXECUTION floor — the row may not execute before the
        // game reaches the frame, and once past it the row's normal gates govern.
        public int RequireFrame { get; set; } = -1;
        // Ability override (owner spec 2026-10-22, jitMHFineTune): when >= 0, a
        // Gather row issues this ability id instead of HARVEST_GATHER — the
        // fine-tune Smart rows use it to send SMART (right-click) on the mineral,
        // matching the Smart the game itself reports for harvest orders.
        public int AbilityIdOverride { get; set; } = -1;
    }

    public sealed class RuntimeWorkerState
    {
        public ulong UnitTag { get; init; }
        public List<WorkerInstruction> Instructions { get; set; } = new();
        public int CurInstrIdx { get; set; }
        public int InstructionStartFrame { get; set; }
        public int CurrentAbilityId { get; set; } = -1;
        public int PreviousAbilityId { get; set; } = -1;
        public int TargetAbilityId { get; set; } = -1;
        public Vector2Dto Position { get; set; } = new();
        public Vector2Dto StoredTargetPoint { get; set; } = new();
        public bool IsCarrying { get; set; }
        public bool WasCarrying { get; set; }
        public bool HasObservedSmart { get; set; }
        public bool CargoReturned => WasCarrying && !IsCarrying;

        // Collision-avoidance detour state, owned by WorkerCollisionAvoidanceService.
        // While CollisionDetourActive is true the mining manager suspends list execution
        // for this worker and the mineral walk owns its commands. CurInstrIdx is NOT
        // advanced during the detour, so the LIST instructions resume at the suspended
        // row once the detour ends.
        public bool CollisionDetourActive { get; set; }
        public int CollisionDetourStartFrame { get; set; } = -1;
        // The carrying worker being passed through, and the self-minus-other offset at
        // detour start. The dot product of the current offset with the start offset
        // going negative is the "x,y passed the teammate" signal that ends the detour.
        public ulong CollisionAvoidedUnitTag { get; set; }
        public float CollisionDetourStartOffsetX { get; set; }
        public float CollisionDetourStartOffsetY { get; set; }
        // True once the detouring walker and the avoided worker have actually come
        // within contact range (WorkerPassThroughRange) during THIS detour. The
        // "pair-clear" end test (no carrying worker within contact range) may only
        // end an ENGAGED detour — a detour that starts at the 2.0u oncoming range
        // is by definition not within 0.35u of the pair, so without this flag the
        // next frame's pair-clear ended it immediately and the walk flip-flopped
        // on/off every frame (owner report 2026-10-16 22:35: "Y3 turned off
        // mineral walking"; the cancel/reissue churn shoved the returning worker
        // onto a long avoidance line).
        public bool CollisionDetourEngaged { get; set; }

        // Label color-cue state (owner spec 2026-10-17 08:11): on pickup the
        // number part of the worker label takes the just-collected mineral's
        // color and the letter part the NEXT mineral's color; at cargo return
        // the full label switches to the next mineral's color. Red for the
        // entire mineral walk is handled at draw time (CollisionDetourActive).
        public string VisualCollectedLabel { get; set; }
        public string VisualNextLabel { get; set; }
        public bool VisualFullNextLabel { get; set; }

        public void LoadInstructions(string instructionSet, IReadOnlyList<WorkerInstruction> instructions, int relativeFrame)
        {
            if (instructions == null)
            {
                throw new ArgumentNullException(nameof(instructions));
            }

            Instructions = new List<WorkerInstruction>(instructions);
            CurInstrIdx = 0;
            InstructionStartFrame = relativeFrame;
            TargetAbilityId = Instructions.Count == 0 ? -1 : Instructions[0].TargetAbilityId;
        }
    }
}
