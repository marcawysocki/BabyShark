using System;

namespace BabySharkBot.Manager
{
    /// <summary>
    /// Event arguments for worker label changes.
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
