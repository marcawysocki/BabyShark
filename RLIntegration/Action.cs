namespace RLIntegration;

public enum MacroAction
{
    Idle = 0,
    Build = 1,
    Expand = 2,
    AttackMove = 3,
    Defend = 4,
    Tech = 5
}

public sealed class Action
{
    public MacroAction MacroAction { get; set; } = MacroAction.Idle;
    public float MacroTargetX { get; set; }
    public float MacroTargetY { get; set; }
    public int MacroTargetUnitTag { get; set; }
    public float MicroMoveX { get; set; }
    public float MicroMoveY { get; set; }
    public bool Attack { get; set; }
    public bool HoldPosition { get; set; }
    public int TargetUnitTag { get; set; }
}
