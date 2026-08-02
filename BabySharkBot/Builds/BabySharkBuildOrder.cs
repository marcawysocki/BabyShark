using System.Collections.Generic;

namespace BabySharkBot.Builds
{
    public class BabySharkBuildOrder
    {
        public string Name { get; set; } = string.Empty;
        public List<BabySharkBuildStep> Steps { get; set; } = new List<BabySharkBuildStep>();
        public int CurrentStepIndex { get; set; }

        public BabySharkBuildOrder(string name)
        {
            Name = name;
        }

        public BabySharkBuildStep? CurrentStep => CurrentStepIndex < Steps.Count ? Steps[CurrentStepIndex] : null;

        public bool IsComplete => CurrentStepIndex >= Steps.Count;

        public void Advance()
        {
            if (CurrentStepIndex < Steps.Count)
            {
                Steps[CurrentStepIndex].IsComplete = true;
                CurrentStepIndex++;
            }
        }
    }
}
