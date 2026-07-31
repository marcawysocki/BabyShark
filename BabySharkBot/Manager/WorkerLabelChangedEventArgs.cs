using System;

namespace BabySharkBot.Manager
{
    /// <summary>
    /// Event arguments for worker label changes.
    /// Used by WorkerLabelService to notify other components (like BabySharkMiningManager) 
    /// when a worker's role or identifier has been updated.
    /// </summary>
    public class WorkerLabelChangedEventArgs : EventArgs
    {
        public ulong UnitTag { get; }
        public string Label { get; }

        public WorkerLabelChangedEventArgs(ulong unitTag, string label)
        {
            UnitTag = unitTag;
            Label = label;
        }
    }
}
