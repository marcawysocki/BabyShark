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
        // mineral's active harvester has ReleaseLeadFrames or fewer harvest
        // frames left, so the worker is never the standing 1183 waiter the game
        // re-pathes away after 15 frames. Resolved from the team assignments by
        // mineral UnitTag, with the team JIT wait point as fallback.
        CcaWait
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
        // New-logic harvest-release gate (owner spec 2026-10-11), valid on Wait rows:
        // the row passes when the row TargetId mineral has NO active harvester (free
        // right now) or its active harvester will flip to 1184 within
        // HarvestFrameTracker.ReleaseLeadFrames frames. While neither holds the row
        // blocks with no command, leaving the worker standing at the CcaWait staging
        // point — never on the busy mineral's footprint where the game would re-path
        // it away after 15 frames.
        public bool HarvestReleaseGate { get; set; }
        // Mineral gate (owner spec 2026-10-13, B2 pool build), valid on Wait rows:
        // the row passes only when the mineral bank holds at least this many
        // minerals (-1 = disabled, the default). Rows carrying the gate ignore
        // the cargo-handoff edge, so a handoff wait immediately in front cannot
        // leak the gate through on the deposit frame.
        public int RequireMinerals { get; set; } = -1;
        // Absolute frame gate (owner spec 2026-10-13, Y2 BB trip), valid on Wait
        // rows: the row passes only once the ABSOLUTE game frame reaches this
        // value (-1 = disabled). Same cargo-handoff exclusion as RequireMinerals.
        public int RequireFrame { get; set; } = -1;
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
