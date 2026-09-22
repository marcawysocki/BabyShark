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
        JitHarvestA,
        JitHarvestB,
        JitWaitPointA,
        JitWaitPointB,
        JitReturnPoint,
        StoredTarget
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
