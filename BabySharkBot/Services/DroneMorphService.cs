using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;

namespace BabySharkBot.Services
{
    /// <summary>
    /// Reusable drone morphing service. Any build can instantiate this,
    /// set DesiredDroneCount, and call Update() every frame.
    /// </summary>
    public class DroneMorphService
    {
        private readonly DefaultSharkyBot _defaultBot;
        
        public int DesiredDroneCount { get; set; } = 12;
        public bool Enabled { get; set; } = true;

        public DroneMorphService(DefaultSharkyBot defaultBot)
        {
            _defaultBot = defaultBot;
        }

        public IEnumerable<SC2APIProtocol.Action> Update(int frame, ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();

            if (!Enabled || observation?.Observation?.RawData?.Units == null) 
                return actions;

            var selfUnits = observation.Observation.RawData.Units
                .Where(u => u.Alliance == Alliance.Self).ToList();

            int currentDrones = selfUnits.Count(u => 
                u.UnitType == (uint)UnitTypes.ZERG_DRONE);
            
            // Count drones currently morphing from larva
            int pendingDrones = selfUnits.Count(u => 
                u.UnitType == (uint)UnitTypes.ZERG_LARVA && 
                u.Orders.Any(o => o.AbilityId == (uint)Abilities.TRAIN_DRONE));

            if (currentDrones + pendingDrones >= DesiredDroneCount) 
                return actions;

            int minerals = _defaultBot.MacroData.Minerals;
            int foodLeft = _defaultBot.MacroData.FoodLeft;

            if (minerals < 50 || foodLeft < 1) 
                return actions;

            // Find idle larva (not already morphing)
            var larva = selfUnits.FirstOrDefault(u => 
                u.UnitType == (uint)UnitTypes.ZERG_LARVA && 
                u.Orders.Count == 0);

            if (larva == null) 
                return actions;

            var morphCmd = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.TRAIN_DRONE,
                UnitTags = { larva.Tag }
            };
            
            actions.Add(new SC2APIProtocol.Action 
            { 
                ActionRaw = new ActionRaw { UnitCommand = morphCmd } 
            });

            Console.WriteLine($"DroneMorphService: Morphing larva {larva.Tag} into drone (Target: {DesiredDroneCount}, Current: {currentDrones}, Pending: {pendingDrones})");

            return actions;
        }
    }
}
