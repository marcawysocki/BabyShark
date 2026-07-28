UnitLabelSystem.md

UnitLabel System for SC2 Bot
A centralized architecture for stable debug drawing, roles, and instruction queues

Overview
This document outlines a clean, reliable pattern for managing unit metadata and debug visualization in a StarCraft II bot using Sharky/SC2API. The goal is to create a single source of truth for:

Unit labels

Worker roles

Instruction queues

Movement targets

Debug drawing

This prevents duplication, inconsistent labels, and the common issue where debug text stops rendering after refactoring.

Why a Centralized System Is Needed
Typical problems in SC2 bot development:

Labels get recreated every frame

Debug text disappears after reorganizing code

Attempts to inherit from SC2API Unit fail

GitHub Copilot generates duplicate label logic

Label creation and debug drawing are mixed together

The solution is to use composition and a dictionary keyed by Unit.Tag.

The UnitLabel Class
public class UnitLabel
{
    public Unit Unit { get; set; }              // SC2API unit snapshot
    public string Label { get; set; }           // WorkerLabel or custom label
    public string Role { get; set; }            // Miner, Builder, Scout, etc.

    public Queue<Vector2> InstructionQueue { get; set; } = new();
    public Vector2? CurrentTarget { get; set; } // For drawing movement arrows
}
Why composition?
SC2API Unit is a protobuf struct and cannot be inherited

Wrapping it gives full access without breaking the API

Your metadata persists across frames

UnitLabel Dictionary
public Dictionary<ulong, UnitLabel> UnitLabels = new();
Updating each frame:
foreach (var unit in observation.Observation.RawData.Units)
{
    if (!UnitLabels.TryGetValue(unit.Tag, out var label))
    {
        label = new UnitLabel
        {
            Unit = unit,
            Label = "Worker " + unit.Tag,
            Role = "Miner"
        };

        UnitLabels[unit.Tag] = label;
    }
    else
    {
        label.Unit = unit; // refresh snapshot
    }
}
Benefits
Labels persist

Roles persist

Instruction queues persist

No duplicate labels

No lost debug output

Centralized Debug Drawing
public void DrawUnitLabels(DebugService debug)
{
    foreach (var label in UnitLabels.Values)
    {
        var pos = new Point2D { X = label.Unit.Pos.X, Y = label.Unit.Pos.Y };

        debug.DebugTextOut(
            $"{label.Label}\n{label.Role}",
            pos,
            Color.Yellow
        );

        if (label.CurrentTarget != null)
        {
            debug.DebugLineOut(
                new Point { X = label.Unit.Pos.X, Y = label.Unit.Pos.Y, Z = 0 },
                new Point { X = label.CurrentTarget.Value.X, Y = label.CurrentTarget.Value.Y, Z = 0 },
                Color.Green
            );
        }
    }
}
What this solves
Consistent labels

Clear role visualization

Movement arrows

No duplication

No disappearing debug text

Recommended File Structure
/Data
    UnitLabel.cs
/Managers
    UnitLabelManager.cs
/Debug
    DebugDrawer.cs
This keeps your architecture clean and maintainable.

Why Previous Attempts Broke
❌ Inheriting from Unit
Not allowed — it’s a protobuf struct.

❌ Putting logic in Settings.cs
Settings.cs is static and not meant for logic-heavy classes.

❌ Copilot hallucinating new labels
Happens when label creation is scattered across the codebase.

❌ Mixing label creation with debug drawing
These must be separate systems.

Summary
A stable UnitLabel system requires:

A wrapper class (UnitLabel)

A dictionary keyed by Unit.Tag

A manager that updates labels each frame

A debug drawer that reads only from UnitLabels

Clean separation of concerns

This architecture prevents duplication, preserves metadata, and ensures debug drawing works reliably.

Ask






