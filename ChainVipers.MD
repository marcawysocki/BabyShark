ChainVipers.MD

To dynamically pull a Battlecruiser all the way from an enemy base back to your Spore forest, you cannot rely on a fixed 3-Viper array. You need a dynamic array that chains a variable number of Vipers (N) based on the distance.
The Sharky C# architecture requires a flexible loop that maps Vipers sequentially along a calculated vector paths from the enemy frontline straight to your static defense.
## Dynamic Viper Chain Task Implementation

using System;using System.Collections.Generic;using System.Linq;using System.Numerics;using System.Collections.Concurrent;using SC2APIProtocol;using Sharky;using Sharky.MicroTasks;
namespace SharkyExampleBot
{
    public class DynamicViperAbductChainTask : MicroTask
    {
        private readonly ActiveUnitData activeUnitData;
        
        // Dynamic list holding all Vipers currently assigned to this strategic pull
        public List<UnitCommander> ChainVipers { get; private set; } = new List<UnitCommander>();
        
        // Set dynamically by your TargetingManager/BaseData
        public Point2D SporeForestCenter { get; set; }
        public Point2D TargetEnemyBase { get; set; }

        public DynamicViperAbductChainTask(DefaultSharkyBot defaultSharkyBot, float priority, bool enabled = true)
        {
            activeUnitData = defaultSharkyBot.ActiveUnitData;
            Priority = priority;
            Enabled = enabled;
        }

        public override void ClaimUnits(ConcurrentDictionary<ulong, UnitCommander> commanders)
        {
            // Dynamically claim all available Vipers to maximize the length of the chain runway
            var availableVipers = commanders.Values
                .Where(c => !c.Claimed && c.UnitCalculation.Unit.UnitType == (uint)UnitTypes.ZERG_VIPER);

            foreach (var viper in availableVipers)
            {
                viper.Claimed = true;
                ChainVipers.Add(viper);
            }
        }

        public override IEnumerable<SC2APIProtocol.Action> PerformActions(uint frame)
        {
            var actions = new List<SC2APIProtocol.Action>();

            if (ChainVipers.Count == 0 || SporeForestCenter == null || TargetEnemyBase == null)
                return actions;

            // 1. Target locking: Focus on the specific Battlecruiser closest to the enemy base line
            var targetBc = FindTargetBattlecruiser();
            
            // 2. Generate the strategic runway stations dynamically based on active Viper count
            List<Vector2> stations = CalculateRunwayStations(ChainVipers.Count);

            // Sort Vipers by their current proximity to the enemy base to ensure orderly positioning
            var sortedVipers = ChainVipers
                .OrderBy(v => Vector2.Distance(new Vector2(v.UnitCalculation.Unit.Pos.X, v.UnitCalculation.Unit.Pos.Y), new Vector2(TargetEnemyBase.X, TargetEnemyBase.Y)))
                .ToList();

            for (int i = 0; i < sortedVipers.Count; i++)
            {
                var viper = sortedVipers[i];
                var assignedStation = new Point2D { X = stations[i].X, Y = stations[i].Y };

                // Handle Energy Check: If too dry to pull, ignore micro and head home to consume/regenerate
                if (viper.UnitCalculation.Unit.Energy < 75)
                {
                    actions.AddRange(viper.Order(frame, Abilities.MOVE, SporeForestCenter));
                    continue;
                }

                if (targetBc != null)
                {
                    Vector2 bcPos = new Vector2(targetBc.Unit.Pos.X, targetBc.Unit.Pos.Y);
                    Vector2 viperPos = new Vector2(viper.UnitCalculation.Unit.Pos.X, viper.UnitCalculation.Unit.Pos.Y);
                    float distanceToBc = Vector2.Distance(viperPos, bcPos);

                    // If target passes into this specific Viper's 9-range zone, trigger immediate intercept pull
                    if (distanceToBc <= 9.0f)
                    {
                        actions.AddRange(viper.Order(frame, Abilities.EFFECT_ABDUCT, null, targetBc.Unit.Tag));
                        continue;
                    }
                }

                // Default: Hold designated node down the line if no unit is in range
                actions.AddRange(viper.Order(frame, Abilities.MOVE, assignedStation));
            }

            return actions;
        }

        private List<Vector2> CalculateRunwayStations(int viperCount)
        {
            var stations = new List<Vector2>();
            Vector2 start = new Vector2(TargetEnemyBase.X, TargetEnemyBase.Y);
            Vector2 end = new Vector2(SporeForestCenter.X, SporeForestCenter.Y);
            
            // Linear interpolation spacing across the map
            for (int i = 0; i < viperCount; i++)
            {
                float t = i / (float)(viperCount - 1 == 0 ? 1 : viperCount - 1);
                Vector2 stationPoint = Vector2.Lerp(start, end, t);
                stations.Add(stationPoint);
            }

            return stations;
        }

        private UnitCalculation FindTargetBattlecruiser()
        {
            return activeUnitData.EnemyUnits.Values
                .Where(u => u.Unit.UnitType == (uint)UnitTypes.TERRAN_BATTLECRUISER && u.Unit.BuildProgress == 1.0f)
                .OrderBy(u => Vector2.Distance(new Vector2(u.Unit.Pos.X, u.Unit.Pos.Y), new Vector2(TargetEnemyBase.X, TargetEnemyBase.Y)))
                .FirstOrDefault();
        }
    }
}

## Key Scaling Mechanics

* Vector interpolation: The method Vector2.Lerp automatically takes your N amount of claimed Vipers and divides the space between the enemy base and your Spore forest evenly into precise pathing intervals.
* Auto-spacing limits: If you have 6 Vipers, it maps them perfectly. If you have 12, they bunch closer together, creating an airtight net with zero window for the Battlecruiser to recover momentum mid-air.
* Fallback triage: If a Viper drops below 75 energy, the script safely removes them from the runway loop and orders them back to base so they don't break the chain line by standing idle.

If you want to tighten this bot behavior, should we look into integrating Map Grid Checking to ensure Vipers don't accidentally stand in high enemy ground DPS zones while waiting for the chain?


Integrating a mobile Corruptor squad as the terminating endpoint of your chain completely solves the geographical limitations of stationary Spore Forests.
In this architectural design, your bot calculates the position of the nearest Corruptor cluster and shifts the entire runway vector to anchor directly onto your roaming anti-air army instead of a static base.
## Dynamic Viper to Corruptor Chain Implementation

using System;using System.Collections.Generic;using System.Linq;using System.Numerics;using System.Collections.Concurrent;using SC2APIProtocol;using Sharky;using Sharky.MicroTasks;
namespace SharkyExampleBot
{
    public class MobileViperCorruptorChainTask : MicroTask
    {
        private readonly ActiveUnitData activeUnitData;
        
        // Tracking systems for our active operational units
        public List<UnitCommander> ChainVipers { get; private set; } = new List<UnitCommander>();
        public List<UnitCommander> CorruptorSquad { get; private set; } = new List<UnitCommander>();
        
        public Point2D TargetEnemyBase { get; set; }

        public MobileViperCorruptorChainTask(DefaultSharkyBot defaultSharkyBot, float priority, bool enabled = true)
        {
            activeUnitData = defaultSharkyBot.ActiveUnitData;
            Priority = priority;
            Enabled = enabled;
        }

        public override void ClaimUnits(ConcurrentDictionary<ulong, UnitCommander> commanders)
        {
            // 1. Claim all free Vipers for runway execution
            var availableVipers = commanders.Values
                .Where(c => !c.Claimed && c.UnitCalculation.Unit.UnitType == (uint)UnitTypes.ZERG_VIPER);
            foreach (var viper in availableVipers)
            {
                viper.Claimed = true;
                ChainVipers.Add(viper);
            }

            // 2. Claim Corruptors to act as the mobile termination trap
            var availableCorruptors = commanders.Values
                .Where(c => !c.Claimed && c.UnitCalculation.Unit.UnitType == (uint)UnitTypes.ZERG_CORRUPTOR);
            foreach (var corruptor in availableCorruptors)
            {
                corruptor.Claimed = true;
                CorruptorSquad.Add(corruptor);
            }
        }

        public override IEnumerable<SC2APIProtocol.Action> PerformActions(uint frame)
        {
            var actions = new List<SC2APIProtocol.Action>();

            if (ChainVipers.Count == 0 || TargetEnemyBase == null)
                return actions;

            // Locate our Corruptor flock centroid to serve as the dynamic vector endpoint
            Vector2 interceptEndpoint = CalculateCorruptorCentroid();

            // Locate the specific target Battlecruiser closest to the vector trajectory
            var targetBc = FindTargetBattlecruiser();

            // Generate Runway nodes interpolation between enemy base and our moving army
            List<Vector2> stations = CalculateMobileRunway(ChainVipers.Count, interceptEndpoint);

            // Sort Vipers by geographic proximity to the enemy launch site
            var sortedVipers = ChainVipers
                .OrderBy(v => Vector2.Distance(new Vector2(v.UnitCalculation.Unit.Pos.X, v.UnitCalculation.Unit.Pos.Y), new Vector2(TargetEnemyBase.X, TargetEnemyBase.Y)))
                .ToList();

            // A. Manage Viper Runway Behaviors
            for (int i = 0; i < sortedVipers.Count; i++)
            {
                var viper = sortedVipers[i];
                var assignedStation = new Point2D { X = stations[i].X, Y = stations[i].Y };

                if (viper.UnitCalculation.Unit.Energy < 75)
                {
                    // Energy Triage: Route back to the Corruptor pocket safely
                    actions.AddRange(viper.Order(frame, Abilities.MOVE, new Point2D { X = interceptEndpoint.X, Y = interceptEndpoint.Y }));
                    continue;
                }

                if (targetBc != null)
                {
                    Vector2 bcPos = new Vector2(targetBc.Unit.Pos.X, targetBc.Unit.Pos.Y);
                    Vector2 viperPos = new Vector2(viper.UnitCalculation.Unit.Pos.X, viper.UnitCalculation.Unit.Pos.Y);

                    if (Vector2.Distance(viperPos, bcPos) <= 9.0f)
                    {
                        actions.AddRange(viper.Order(frame, Abilities.EFFECT_ABDUCT, null, targetBc.Unit.Tag));
                        continue;
                    }
                }

                actions.AddRange(viper.Order(frame, Abilities.MOVE, assignedStation));
            }

            // B. Manage Corruptor Execution Squad Behaviors
            foreach (var corruptor in CorruptorSquad)
            {
                if (targetBc != null)
                {
                    Vector2 bcPos = new Vector2(targetBc.Unit.Pos.X, targetBc.Unit.Pos.Y);
                    Vector2 corruptorPos = new Vector2(corruptor.UnitCalculation.Unit.Pos.X, corruptor.UnitCalculation.Unit.Pos.Y);

                    // Focus target exclusively if the runway successfully drops the BC near the squad
                    if (Vector2.Distance(corruptorPos, bcPos) <= 12.0f)
                    {
                        actions.AddRange(corruptor.Order(frame, Abilities.ATTACK, null, targetBc.Unit.Tag));
                        continue;
                    }
                }

                // Default: Tight escort formation holding the dynamic intercept endpoint
                actions.AddRange(corruptor.Order(frame, Abilities.MOVE, new Point2D { X = interceptEndpoint.X, Y = interceptEndpoint.Y }));
            }

            return actions;
        }

        private Vector2 CalculateCorruptorCentroid()
        {
            if (CorruptorSquad.Count == 0) 
                return new Vector2(TargetEnemyBase.X, TargetEnemyBase.Y); // Fallback to avoid division crashes

            float sumX = CorruptorSquad.Sum(c => c.UnitCalculation.Unit.Pos.X);
            float sumY = CorruptorSquad.Sum(c => c.UnitCalculation.Unit.Pos.Y);
            return new Vector2(sumX / CorruptorSquad.Count, sumY / CorruptorSquad.Count);
        }

        private List<Vector2> CalculateMobileRunway(int viperCount, Vector2 endpoint)
        {
            var stations = new List<Vector2>();
            Vector2 start = new Vector2(TargetEnemyBase.X, TargetEnemyBase.Y);

            for (int i = 0; i < viperCount; i++)
            {
                float t = i / (float)(viperCount - 1 == 0 ? 1 : viperCount - 1);
                stations.Add(Vector2.Lerp(start, endpoint, t));
            }
            return stations;
        }

        private UnitCalculation FindTargetBattlecruiser()
        {
            return activeUnitData.EnemyUnits.Values
                .Where(u => u.Unit.UnitType == (uint)UnitTypes.TERRAN_BATTLECRUISER && u.Unit.BuildProgress == 1.0f)
                .OrderBy(u => Vector2.Distance(new Vector2(u.Unit.Pos.X, u.Unit.Pos.Y), new Vector2(TargetEnemyBase.X, TargetEnemyBase.Y)))
                .FirstOrDefault();
        }
    }
}

## Strategic and Algorithmic Adjustments

* Dynamic Anchoring: The CalculateCorruptorCentroid function regularly calculates the mean average position of your flying forces. The Vipers dynamically morph their line positions on the fly to match the Corruptors as they patrol or shift across the map.
* Corruptor Aggro Range: Corruptors are configured to break formation and dogpile the Battlecruiser only when it is dragged within 12 units of their cluster. This prevents them from wandering off down the runway ahead of time and breaking your trap line.
* Failsafe Escort: If zero targets are present on the map, the Vipers and Corruptors collapse safely into a singular compressed, mobile pocket.

Should we implement a Corruptor size checker inside the logic to guarantee the script doesn't execute a pull unless you have at least 6–8 Corruptors ready to burst down the Battlecruiser?



Dedicating 3 Evolution Chambers exclusively for Consume targets is the competitive standard. Evolution Chambers offer the highest health-to-mineral cost ratio of any Zerg structure (100 minerals for 750 HP), making them ideal for rapid energy farming without risking your tech nodes. [1, 2, 3, 4] 
Because a Viper requires 14 seconds to extract 50 energy (dealing 200 HP damage), a single dry Viper needs 56 seconds and 800 total HP to hit max capacity (200 energy). An automated Sharky bot behavior must safely cycle low-energy units to these designated coordinates to keep the runway line fully fueled. [5] 
## Automated Viper Energy Management Module

using System.Collections.Generic;using System.Linq;using System.Numerics;using SC2APIProtocol;using Sharky;using Sharky.MicroTasks;
namespace SharkyExampleBot
{
    public class ViperRefuelManager
    {
        private readonly ActiveUnitData activeUnitData;
        
        // Tags of the 3 designated Evo Chambers reserved strictly for refueling
        public HashSet<ulong> DedicatedEvoChamberTags { get; set; } = new HashSet<ulong>();

        public ViperRefuelManager(DefaultSharkyBot defaultSharkyBot)
        {
            activeUnitData = defaultSharkyBot.ActiveUnitData;
        }

        public List<SC2APIProtocol.Action> ManageEnergyFarming(uint frame, List<UnitCommander> vipers)
        {
            var actions = new List<SC2APIProtocol.Action>();

            // Clean up missing or destroyed Evo Chambers from memory
            var validEvos = activeUnitData.SelfUnits.Values
                .Where(u => DedicatedEvoChamberTags.Contains(u.Unit.Tag) && u.Unit.BuildProgress == 1.0f)
                .ToList();

            if (validEvos.Count == 0)
                return actions; // Escape if no dedicated structures are ready

            foreach (var viper in vipers)
            {
                // Trigger refuel triage state if a Viper drops below the single Abduct threshold (75)
                if (viper.UnitCalculation.Unit.Energy < 75)
                {
                    // Locate the optimal Evo Chamber with the healthiest structure padding
                    var bestEvo = validEvos
                        .Where(e => e.Unit.Health > 220) // Keep safety buffer above a single 200 HP consume tick
                        .OrderByDescending(e => e.Unit.Health)
                        .FirstOrDefault();

                    if (bestEvo != null)
                    {
                        // Issue target lock command to feed on the selected structure node
                        actions.AddRange(viper.Order(frame, Abilities.EFFECT_CONSUME, null, bestEvo.Unit.Tag));
                    }
                }
            }

            return actions;
        }

        public void AutoAssignEvoChambers()
        {
            // Fallback scanner to isolate the 3 lowest-priority non-upgrading Evo nodes
            if (DedicatedEvoChamberTags.Count < 3)
            {
                var evos = activeUnitData.SelfUnits.Values
                    .Where(u => u.Unit.UnitType == (uint)UnitTypes.ZERG_EVOLUTIONCHAMBER && u.Unit.BuildProgress == 1.0f)
                    .Take(3 - DedicatedEvoChamberTags.Count);

                foreach (var evo in evos)
                {
                    DedicatedEvoChamberTags.Add(evo.Unit.Tag);
                }
            }
        }
    }
}

## Integration within the Dynamic Runway
To prevent pulling Vipers away in the middle of an active operation, you should call this inside your main DynamicViperAbductChainTask script:

* 
* Separation of Concerns: Only pass Vipers that are not currently locked into a live pull into the ManageEnergyFarming system.
* Rotational Logic: As soon as a Viper hits 200 max energy via consumption, the main runway script should instantly flag them as active and re-index them back into the Vector2.Lerp chain configuration. [6, 7] 
* 

Should we adjust the Evo safety threshold to include a Hatchery builder loop so your bot auto-replacements the designated Evo Chambers if the Vipers accidentally chew them down to 0 health? [8] 

[1] [https://www.reddit.com](https://www.reddit.com/r/starcraft/comments/w9va1z/should_vipers_have_a_higher_tech_requirement/)
[2] [https://www.reddit.com](https://www.reddit.com/r/starcraft/comments/1cb9g0/why_use_evo_chambers_for_viper_energy_instead_of/)
[3] [https://www.reddit.com](https://www.reddit.com/r/allthingszerg/comments/j8hhlj/how_to_properly_use_vipers/)
[4] [https://tl.net](https://tl.net/forum/starcraft-2-hots/368971-vipers-flying-and-unnerving-casters)
[5] [https://us.forums.blizzard.com](https://us.forums.blizzard.com/en/sc2/t/nerf-viper-yoinks-aka-abduct-how-to-fix/17188)
[6] [https://www.youtube.com](https://www.youtube.com/watch?v=F0czcOdX1Xo)
[7] [https://liquipedia.net](https://liquipedia.net/starcraft2/Viper_%28Legacy_of_the_Void%29)
[8] [https://www.reddit.com](https://www.reddit.com/r/allthingszerg/comments/at41mz/viper_consume_guide/)

You are completely correct. Vipers regenerate 0.7875 energy per second (or 1.1025 energy per second on Faster game speed) while moving or flying across the map.
If you force them to consume structural health all the way to 200 energy back at base, you waste valuable game time and over-saturate their energy pools. Instead, your bot should calculate a dynamic exit threshold so they finish charging exactly when they arrive back at their destination station.
## The formula
To find the energy target, your bot should measure the straight-line distance to their assigned runway node and apply standard flight speeds:

* Viper Moving Speed: 4.13 units per second.
* Energy Regeneration: 1.1025 energy per second (on Faster speed).
* Energy Gained Per Unit of Distance: $\frac{1.1025}{4.13} \approx \textbf{0.267 energy per unit of distance traveled}$.

## Optimized refuel code logic
Replace your previous static energy check with this dynamic distance-based condition inside your loop:

foreach (var viper in vipers)
{
    float currentEnergy = viper.UnitCalculation.Unit.Energy;

    if (currentEnergy < 75)
    {
        // 1. Calculate how far this Viper has to fly to get back to its assigned runway node
        Vector2 viperPos = new Vector2(viper.UnitCalculation.Unit.Pos.X, viper.UnitCalculation.Unit.Pos.Y);
        float distanceToStation = Vector2.Distance(viperPos, assignedStationVector);

        // 2. Predict how much energy it will freely generate during flight time
        float expectedPassiveRegen = distanceToStation * 0.267f;

        // 3. Set a dynamic ceiling (Cap at 200 max pool limit)
        float targetEnergyCeiling = Math.Min(200f, 200f - expectedPassiveRegen);

        // 4. Stay at the Evo Chamber ONLY until it hits the dynamic target
        if (currentEnergy < targetEnergyCeiling)
        {
            var bestEvo = validEvos.OrderByDescending(e => e.Unit.Health).FirstOrDefault();
            if (bestEvo != null)
            {
                actions.AddRange(viper.Order(frame, Abilities.EFFECT_CONSUME, null, bestEvo.Unit.Tag));
                continue; // Maintain siphon lock state
            }
        }
    }
}

## Script efficiency benefits

* Faster Runway Cycles: If a runway node is sitting 100 units away near the enemy base, the Viper will naturally regenerate 26.7 energy during the return flight. Your bot will stop consuming at 173.3 energy and immediately take off.
* Optimized DPS and Structure Lifespan: Your Evolution Chambers will take significantly less structural damage over time because the Vipers stop chewing them the millisecond they have enough fuel to finish the flight.

Would you like to build an Interception intercept flag so if an enemy Battlecruiser shows up unexpectedly close, the bot forces all refueling Vipers to break their siphon state instantly, regardless of their current energy level?

Here is the updated implementation. This code integrates the Dynamic Refuel Target based on distance and adds the Emergency Break flag to cancel Consume immediately if an enemy Battlecruiser comes within 10 units (the max range of Yamato Cannon).

using System;using System.Collections.Generic;using System.Linq;using System.Numerics;using SC2APIProtocol;using Sharky;using Sharky.MicroTasks;
namespace SharkyExampleBot
{
    public class ProtectedRefuelManager
    {
        private readonly ActiveUnitData activeUnitData;
        
        // Tags of the 3 designated Evo Chambers inside the Spore Forest
        public HashSet<ulong> DedicatedEvoChamberTags { get; set; } = new HashSet<ulong>();

        public ProtectedRefuelManager(DefaultSharkyBot defaultSharkyBot)
        {
            activeUnitData = defaultSharkyBot.ActiveUnitData;
        }

        public List<SC2APIProtocol.Action> ManageEnergyFarming(uint frame, List<UnitCommander> vipers, List<Vector2> runwayStations)
        {
            var actions = new List<SC2APIProtocol.Action>();

            var validEvos = activeUnitData.SelfUnits.Values
                .Where(u => DedicatedEvoChamberTags.Contains(u.Unit.Tag) && u.Unit.BuildProgress == 1.0f)
                .ToList();

            if (validEvos.Count == 0 || vipers.Count == 0)
                return actions;

            // 1. EMERGENCY BREAK SCAN: Check if any enemy Battlecruisers are encroaching the Spore Forest perimeter
            var nearbyThreats = activeUnitData.EnemyUnits.Values
                .Where(e => e.Unit.UnitType == (uint)UnitTypes.TERRAN_BATTLECRUISER);

            foreach (var viper in vipers)
            {
                Vector2 viperPos = new Vector2(viper.UnitCalculation.Unit.Pos.X, viper.UnitCalculation.Unit.Pos.Y);
                float currentEnergy = viper.UnitCalculation.Unit.Energy;

                // Scan for threats within 10.0 range to preemptively avoid getting sniped by Yamato Cannon
                bool isThreatNearby = nearbyThreats.Any(t => Vector2.Distance(viperPos, new Vector2(t.Unit.Pos.X, t.Unit.Pos.Y)) <= 10.0f);

                if (isThreatNearby)
                {
                    // EMERGENCY BREAK: If consuming, issue a Stop command to immediately break the animation channel
                    if (viper.UnitCalculation.Unit.Orders.Any(o => o.AbilityId == (uint)Abilities.EFFECT_CONSUME))
                    {
                        actions.AddRange(viper.Order(frame, Abilities.STOP));
                    }
                    continue; // Skip the refuel logic loop for this frame to allow defensive micro/fleeing
                }

                // 2. DYNAMIC REFUEL LOGIC: Only consume if under full energy capacity minus transit regen
                if (currentEnergy < 75) 
                {
                    // Find the furthest station this Viper might be assigned to in the runway vector
                    Vector2 targetStation = runwayStations.LastOrDefault(); 
                    float distanceToStation = Vector2.Distance(viperPos, targetStation);

                    // 0.267 energy generated per unit of travel distance
                    float expectedPassiveRegen = distanceToStation * 0.267f;
                    float dynamicEnergyCeiling = Math.Min(200f, 200f - expectedPassiveRegen);

                    if (currentEnergy < dynamicEnergyCeiling)
                    {
                        var bestEvo = validEvos.Where(e => e.Unit.Health > 220).OrderByDescending(e => e.Unit.Health).FirstOrDefault();
                        if (bestEvo != null)
                        {
                            actions.AddRange(viper.Order(frame, Abilities.EFFECT_CONSUME, null, bestEvo.Unit.Tag));
                        }
                    }
                }
            }

            return actions;
        }
    }
}

## Operational safeguards

* Preemptive Interception: By monitoring the 10.0f vector radius, Vipers will drop their connection before a Battlecruiser completes its 2-second Yamato lock-on animation.
* Instant Stop Frame Overwrite: The Abilities.STOP action clears the active task queue instantly. This frees up the Viper to be immediate available for an emergency Abduct if a Battlecruiser steps inside the Spore Forest boundary.

Would you like to write a routine for your Queens to automatically step in and Transfuse the Evolution Chambers if they drop below half health?



