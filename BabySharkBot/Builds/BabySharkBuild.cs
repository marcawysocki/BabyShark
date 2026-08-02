using System;
using System.Collections.Generic;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// Base class for all BabyShark builds. Replaces Sharky's ZergSharkyBuild.
    /// Provides direct access to MacroData and ActiveUnitData for build order execution.
    /// </summary>
    public abstract class BabySharkBuild
    {
        protected readonly DefaultSharkyBot DefaultBot;
        protected MacroData MacroData => DefaultBot.MacroData;
        protected ActiveUnitData ActiveUnitData => DefaultBot.ActiveUnitData;
        protected UnitCountService UnitCountService => DefaultBot.UnitCountService;

        public string BuildName { get; protected set; }
        public bool IsComplete { get; protected set; }

        protected BabySharkBuild(DefaultSharkyBot defaultBot)
        {
            DefaultBot = defaultBot;
            BuildName = GetType().Name;
            IsComplete = false;
        }

        public virtual void OnStart(int frame)
        {
            IsComplete = false;
        }

        public abstract IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation);

        public virtual bool ShouldTransition(int frame)
        {
            return false;
        }

        // Convenience helpers for manipulating MacroData desires
        protected void SetDesiredUnitCount(UnitTypes unitType, int count)
        {
            if (MacroData?.DesiredUnitCounts == null)
            {
                Console.WriteLine($"[BabySharkBuild] DesiredUnitCounts is null. Skipping {unitType}={count}");
                return;
            }
            MacroData.DesiredUnitCounts[unitType] = count;
        }

        protected void SetDesiredProductionCount(UnitTypes unitType, int count)
        {
            if (MacroData?.DesiredProductionCounts == null)
            {
                Console.WriteLine($"[BabySharkBuild] DesiredProductionCounts is null. Skipping {unitType}={count}");
                return;
            }
            MacroData.DesiredProductionCounts[unitType] = count;
        }

        protected void SetDesiredTechCount(UnitTypes unitType, int count)
        {
            if (MacroData?.DesiredTechCounts == null)
            {
                Console.WriteLine($"[BabySharkBuild] DesiredTechCounts is null. Skipping {unitType}={count}");
                return;
            }
            MacroData.DesiredTechCounts[unitType] = count;
        }

        protected void SetDesiredGases(int count)
        {
            if (MacroData == null)
            {
                Console.WriteLine($"[BabySharkBuild] MacroData is null. Skipping DesiredGases={count}");
                return;
            }
            MacroData.DesiredGases = count;
        }

        protected int CountUnits(UnitTypes unitType)
        {
            return UnitCountService.Count(unitType);
        }
    }
}
