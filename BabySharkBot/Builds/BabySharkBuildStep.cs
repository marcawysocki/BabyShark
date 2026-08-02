namespace BabySharkBot.Builds
{
    public class BabySharkBuildStep
    {
        public enum StepType
        {
            Unit,
            Building,
            Upgrade,
            WaitMinerals,
            WaitGas,
            WaitSupply,
            Custom
        }

        public StepType Type { get; set; }
        public uint UnitType { get; set; }
        public int TargetCount { get; set; }
        public int MineralThreshold { get; set; }
        public int GasThreshold { get; set; }
        public int SupplyThreshold { get; set; }
        public bool IsComplete { get; set; }
        public string Description { get; set; } = string.Empty;
    }
}
