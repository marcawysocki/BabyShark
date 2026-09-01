using System;
using System.Collections.Generic;

namespace BabySharkBot.Setup
{
    public enum WorkerInstructionCommand
    {
        Move,
        Gather,
        Return,
        LoadInstructionSet
    }

    public enum WorkerInstructionPoint
    {
        None,
        Harvest,
        Return,
        Staging
    }

    public sealed class WorkerInstruction
    {
        public string InstructionSet { get; set; } = string.Empty;
        public WorkerInstructionCommand Command { get; set; }
        public WorkerInstructionPoint Point { get; set; }
        public ulong TargetId { get; set; }
        public int RelativeFrame { get; set; } = -1;
        public int TargetAbilityId { get; set; } = -1;
        public float PositionTolerance { get; set; } = 0.25f;
        public bool Queue { get; set; }
        public string NextInstructionSet { get; set; } = string.Empty;
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
        public bool IsCarrying { get; set; }
        public bool WasCarrying { get; set; }
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
