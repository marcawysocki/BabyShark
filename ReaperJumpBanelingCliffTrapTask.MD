ReaperJumpBanelingCliffTrapTask.MD

To create a functional Baneling landmine trap in your C# Sharky bot, you need to manage two specific engine mechanics: mid-air invulnerability and unit detection.
A Reaper is completely invulnerable and immune to damage while it is actively in its ReaperJump mid-air animation. Your bot must wait until the Reaper's height coordinate matches the high-ground layer and its status transitions back to standard pathing before triggering the explosion.
## C# Baneling Trap Implementation

using System;using System.Collections.Generic;using System.Linq;using System.Numerics;using SC2APIProtocol;using Sharky;using Sharky.MicroTasks;
namespace SharkyExampleBot
{
    public class BanelingCliffTrapTask : MicroTask
    {
        private readonly ActiveUnitData activeUnitData;
        
        // Tags of the 2 specific Banelings assigned to sit on the cliff landing node
        public HashSet<ulong> TrapBanelings { get; private set; } = new HashSet<ulong>();
        
        // The precise high-ground tile coordinate where the Reaper lands
        public Point2D CliffLandingPoint { get; set; }

        public BanelingCliffTrapTask(DefaultSharkyBot defaultSharkyBot, float priority, bool enabled = true)
        {
            activeUnitData = defaultSharkyBot.ActiveUnitData;
            Priority = priority;
            Enabled = enabled;
        }

        public override void ClaimUnits(ConcurrentDictionary<ulong, UnitCommander> commanders)
        {
            if (TrapBanelings.Count < 2)
            {
                var availableBanelings = commanders.Values
                    .Where(c => !c.Claimed && c.UnitCalculation.Unit.UnitType == (uint)UnitTypes.ZERG_BANELING)
                    .Take(2 - TrapBanelings.Count);

                foreach (var bane in availableBanelings)
                {
                    bane.Claimed = true;
                    TrapBanelings.Add(bane.UnitCalculation.Unit.Tag);
                }
            }
        }

        public override IEnumerable<SC2APIProtocol.Action> PerformActions(uint frame)
        {
            var actions = new List<SC2APIProtocol.Action>();

            if (TrapBanelings.Count == 0 || CliffLandingPoint == null)
                return actions;

            Vector2 trapPos = new Vector2(CliffLandingPoint.X, CliffLandingPoint.Y);

            // Fetch live data for our assigned trap units
            var activeBanes = activeUnitData.SelfUnits.Values
                .Where(u => TrapBanelings.Contains(u.Unit.Tag))
                .ToList();

            // 1. POSITIONING STATE: Ensure banelings are perfectly centered and burrowed
            foreach (var baneCommander in activeUnitData.Commanders.Values.Where(c => TrapBanelings.Contains(c.UnitCalculation.Unit.Tag)))
            {
                var unit = baneCommander.UnitCalculation.Unit;
                Vector2 currentPos = new Vector2(unit.Pos.X, unit.Pos.Y);

                if (Vector2.Distance(currentPos, trapPos) > 0.5f)
                {
                    // Move directly onto the landing center tile
                    actions.AddRange(baneCommander.Order(frame, Abilities.MOVE, CliffLandingPoint));
                }
                else if (unit.Burrowed == IsBurrowed.False && !unit.Orders.Any(o => o.AbilityId == (uint)Abilities.BURROWDOWN))
                {
                    // Secure burrow state immediately upon arrival
                    actions.AddRange(baneCommander.Order(frame, Abilities.BURROWDOWN));
                }
            }

            // 2. DETONATION SCANNER: Look for landmines targets
            var targetReaper = activeUnitData.EnemyUnits.Values
                .FirstOrDefault(e => e.Unit.UnitType == (uint)UnitTypes.TERRAN_REAPER && 
                                     Vector2.Distance(new Vector2(e.Unit.Pos.X, e.Unit.Pos.Y), trapPos) <= 0.8f);

            if (targetReaper != null)
            {
                // CRITICAL SAFETY GUARD: Check if the Reaper is currently in its jumping status loop
                bool isMidAir = targetReaper.Unit.Orders.Any(o => o.AbilityId == (uint)Abilities.EFFECT_REAPERRECOVERY_KD8CHARGE || 
                                                                 o.AbilityId == 169); // 169 is the internal engine ID for Cliff Jump

                // Only explode if the Reaper has physically connected with the high-ground floor
                if (!isMidAir)
                {
                    foreach (var baneCommander in activeUnitData.Commanders.Values.Where(c => TrapBanelings.Contains(c.UnitCalculation.Unit.Tag)))
                    {
                        // Explode from a burrowed state instantly bypassing unburrow latency frames
                        actions.AddRange(baneCommander.Order(frame, Abilities.EFFECT_EXPLODE));
                    }
                }
            }

            return actions;
        }
    }
}

## Tactical code benefits

* Bypassing Unburrow Latency: The code triggers Abilities.EFFECT_EXPLODE directly while the Banelings are still subterranean. Forcing them to unburrow first takes 0.71 seconds, giving human players enough time to react and run away.
* Tight Radius Lock: The distance check is set to an ultra-strict 0.8 units. This ensures the explosion only goes off if the Reaper lands directly on top of the trap, guaranteeing maximum splash damage.

Would you like to analyze how to dynamically acquire the CliffLandingPoint vector coordinate automatically by scanning the map's structural pathing grid?

Using a Hatchery and a Roach Warren at the main ramp leaves the nearby Reaper cliff wide open. To close this backdoor, you can use your Evolution Chambers and Baneling Nest to construct a secondary funnel trap directly behind that cliff landing pad.
## Architectural layout of the double-wall setup

* The Main Ramp Wall: Your Hatchery and Roach Warren form a thick wall blocking the main ramp, defended by your primary production Queen. [1] 
* The Cliff Funnel Wall: Position your two Evolution Chambers and one Baneling Nest in a V-shaped or L-shaped configuration right behind the high-ground edge of the Reaper cliff. Leave a 1-tile wide gap between the buildings.
* The Minefield Placement: Place your 2 Trap Banelings directly on the landing pad tile right in front of that 1-tile gap.
* The Execution Point: Position your Spine Crawler or a second Queen right behind the 1-tile gap, looking through the building bottleneck.

## Pathing engine mechanics
When the Reaper jumps up the cliff, it lands directly on top of the burrowed Banelings.
If the Reaper survives the landmine detonation with a sliver of health, its pathing AI will look for a way into your main base. Because your buildings completely block the left and right sides of the cliff, the engine forces the Reaper to run straight forward through the 1-tile wide gap—delivering it directly to the waiting Spine Crawler or Queen for an immediate cleanup kill.
## C# building position logic
To implement this layout dynamically in a Sharky bot, you need to find the cliff edge and offset your building coordinates. This method generates the building coordinates relative to the cliff landing zone:

using System.Numerics;using SC2APIProtocol;
public class CliffTrapPlacementService
{
    // Computes the exact placement vectors for your secondary funnel wall
    public void GetTrapBuildingPositions(Point2D cliffLandingPoint, Vector2 mapCenter, 
        out Point2D evo1Pos, out Point2D evo2Pos, out Point2D baneNestPos)
    {
        // 1. Calculate the vector pointing away from the cliff toward the inside of your base
        Vector2 landingVector = new Vector2(cliffLandingPoint.X, cliffLandingPoint.Y);
        Vector2 pushDirection = Vector2.Normalize(mapCenter - landingVector); 

        // 2. Derive a perpendicular angle to create the cross-wall funnel flanking the landing point
        Vector2 perpendicular = new Vector2(-pushDirection.Y, pushDirection.X);

        // 3. Offset coordinates to leave a precise 1-tile bottleneck gap in the center
        Vector2 evo1Vector = landingVector + (pushDirection * 2.5f) + (perpendicular * 2.0f);
        Vector2 evo2Vector = landingVector + (pushDirection * 2.5f) - (perpendicular * 2.0f);
        
        // Position the Baneling Nest slightly deeper to close off any wider pathing gaps
        Vector2 nestVector = landingVector + (pushDirection * 4.5f) + (perpendicular * 0.5f);

        evo1Pos = new Point2D { X = (float)Math.Round(evo1Vector.X), Y = (float)Math.Round(evo1Vector.Y) };
        evo2Pos = new Point2D { X = (float)Math.Round(evo2Vector.X), Y = (float)Math.Round(evo2Vector.Y) };
        baneNestPos = new Point2D { X = (float)Math.Round(nestVector.X), Y = (float)Math.Round(nestVector.Y) };
    }
}

Would you like to explore adding Drone production rules to ensure the bot builds the Baneling Nest and Evolution Chambers at the exact same time the Roach Warren begins warping in?

[1] [https://starcraft.fandom.com](https://starcraft.fandom.com/wiki/Forum:2018_Season_3_Protoss_Wall-off_Guide)

The Terran base jump-down spot (the cliff edge of their main base) is typically used for a one-way exit early in the game. A Reaper will jump down this cliff to cross the map and harass you, but it rarely tries to jump back up unless it is fleeing a counter-attack.
However, you can place a proxy Zergling or Baneling trap at the bottom of this specific jump-down coordinate to catch the Reaper the exact frame it lands on the low ground.
## Low-ground landing mechanics
Unlike your high-ground base trap, a low-ground proxy trap reverses the vision and pathing rules:

* The Landing Lock: When a Reaper jumps down, it undergoes the exact same mid-air landing frames. It cannot change its trajectory once the jump animation begins.
* No Funnel Required: You do not need to build Evolution Chambers or a Baneling Nest at the bottom of the Terran cliff. The map's natural terrain blocks the Reaper's backward movement because it cannot turn around and jump back up immediately without an active cooldown reset.
* The Proxy Placement: You position your burrowed Banelings directly on the low-ground tiles where the Reaper must land.

## C# Terran Jump-Down Trap Module
To implement a proxy landmine at the enemy's jump-down vector, your bot must locate the enemy main base cliff coordinates and park two early Zerglings/Banelings right at the base of the wall:

using System;using System.Collections.Generic;using System.Linq;using System.Numerics;using SC2APIProtocol;using Sharky;using Sharky.MicroTasks;
namespace SharkyExampleBot
{
    public class EnemyJumpDownTrapTask : MicroTask
    {
        private readonly ActiveUnitData activeUnitData;
        public HashSet<ulong> ProxyBanelings { get; private set; } = new HashSet<ulong>();
        
        // This vector must be calculated based on the enemy main base cliff coordinate
        public Point2D EnemyLowGroundLandingPoint { get; set; }

        public EnemyJumpDownTrapTask(DefaultSharkyBot defaultSharkyBot, float priority, bool enabled = true)
        {
            activeUnitData = defaultSharkyBot.ActiveUnitData;
            Priority = priority;
            Enabled = enabled;
        }

        public override void ClaimUnits(ConcurrentDictionary<ulong, UnitCommander> commanders)
        {
            // Claim 2 early Banelings for the proxy trap
            if (ProxyBanelings.Count < 2)
            {
                var availableBanelings = commanders.Values
                    .Where(c => !c.Claimed && c.UnitCalculation.Unit.UnitType == (uint)UnitTypes.ZERG_BANELING)
                    .Take(2 - ProxyBanelings.Count);

                foreach (var bane in availableBanelings)
                {
                    bane.Claimed = true;
                    ProxyBanelings.Add(bane.UnitCalculation.Unit.Tag);
                }
            }
        }

        public override IEnumerable<SC2APIProtocol.Action> PerformActions(uint frame)
        {
            var actions = new List<SC2APIProtocol.Action>();

            if (ProxyBanelings.Count == 0 || EnemyLowGroundLandingPoint == null)
                return actions;

            Vector2 landingVector = new Vector2(EnemyLowGroundLandingPoint.X, EnemyLowGroundLandingPoint.Y);

            // 1. Send proxy units to the bottom of the enemy cliff and burrow
            foreach (var baneCommander in activeUnitData.Commanders.Values.Where(c => ProxyBanelings.Contains(c.UnitCalculation.Unit.Tag)))
            {
                var unit = baneCommander.UnitCalculation.Unit;
                Vector2 currentPos = new Vector2(unit.Pos.X, unit.Pos.Y);

                if (Vector2.Distance(currentPos, landingVector) > 0.5f)
                {
                    actions.AddRange(baneCommander.Order(frame, Abilities.MOVE, EnemyLowGroundLandingPoint));
                }
                else if (unit.Burrowed == IsBurrowed.False && !unit.Orders.Any(o => o.AbilityId == (uint)Abilities.BURROWDOWN))
                {
                    actions.AddRange(baneCommander.Order(frame, Abilities.BURROWDOWN));
                }
            }

            // 2. Scan for the Reaper descending the cliff
            var targetReaper = activeUnitData.EnemyUnits.Values
                .FirstOrDefault(e => e.Unit.UnitType == (uint)UnitTypes.TERRAN_REAPER && 
                                     Vector2.Distance(new Vector2(e.Unit.Pos.X, e.Unit.Pos.Y), landingVector) <= 0.8f);

            if (targetReaper != null)
            {
                // Check if the Reaper has successfully finished the jump and touched the low-ground layer
                bool isMidAir = targetReaper.Unit.Orders.Any(o => o.AbilityId == (uint)Abilities.EFFECT_REAPERRECOVERY_KD8CHARGE || o.AbilityId == 169);

                if (!isMidAir)
                {
                    foreach (var baneCommander in activeUnitData.Commanders.Values.Where(c => ProxyBanelings.Contains(c.UnitCalculation.Unit.Tag)))
                    {
                        actions.AddRange(baneCommander.Order(frame, Abilities.EFFECT_EXPLODE));
                    }
                }
            }

            return actions;
        }
    }
}

## Strategic timing risk
While highly effective if it connects, a proxy trap at their base is riskier than a defensive one. If the Terran player rallies their very first Marine or Hellion down the main ramp instead of the cliff, they might run directly into your unburrowed Banelings while they are traveling across the map to get into position.
If you'd like, we can write a Base detection rule that uses your initial Overlord scout to verify if the Terran opened with a Reactor or a Tech Lab before sending units out to set up this proxy trap. [1] 

[1] [https://liquipedia.net](https://liquipedia.net/starcraft/Bionic_Terran_vs._Zerg_Timing)

Yes, proxying multiple Barracks and putting Add-ons on them is exactly how the aggressive "Proxy Reaper Rush" functions. [1] 
Understanding how the add-on mechanics interact with Reapers is key to programming your bot's detection rules:
## 1. The Add-on Mechanics

* 
* Reactors (Double Production): In Legacy of the Void, Reapers do not require a Tech Lab to be built—they can be produced directly from a Reactor. If a Terran proxies 3 Barracks and manages to slap Reactors on them, they can flood 6 Reapers at a time into your main base. [2, 3, 4] 
* Tech Labs (Upgrades): If you see them put a Tech Lab on their proxied Barracks, they aren't trying to unlock the unit; they are researching Nitro Packs (Speed) or Combat Drugs (Health Regen) to make their existing flock incredibly difficult to kill with basic Zerglings. [5] 
* 

## 2. Updating your Bot's Detection Logic
Because a proxy build can scale up massively with add-ons, your Sharky bot needs a robust scanner. Use your Initial Overlord Scout to look into the Terran's main base at roughly 1:30 to 1:45 in game time: [6, 7, 8] 

public bool IsEnemyProxyingReapers()
{
    // 1. Scan the enemy's main base coordinates for a Barracks
    bool hasBarracksInMain = activeUnitData.EnemyUnits.Values
        .Any(u => u.Unit.UnitType == (uint)UnitTypes.TERRAN_BARRACKS && !u.Unit.IsFlying);

    // 2. Check if they are actively mining Gas (Reapers cost 50 Gas each)
    bool isMiningGas = activeUnitData.EnemyUnits.Values
        .Any(u => u.Unit.UnitType == (uint)UnitTypes.TERRAN_REFINERY);

    // PROXY ALARM: If they have Gas but ZERO Barracks in their main base, it's a proxy rush
    if (!hasBarracksInMain && isMiningGas)
    {
        return true;
    }
    return false;
}

## 3. Executing the Defensive Bot Pivot
If the condition above returns true, your bot must instantly bypass standard macro commands and prioritize surviving the flood: [9] 

* 
* Accelerate the Funnel Wall: Instantly pull 3 Drones to place your Roach Warren, Hatchery, and the secondary Evolution Chamber cliff-trap funnel before the first 3 Reapers arrive at your base. [9] 
* Spike Queen Production: Queue up continuous Queens at your main Hatchery. Since Queens have a 5.5 ground attack range and heavy health pools, they are your best active defense against mass Reactor-Reapers.
* Keep Banelings Primed: Do not unburrow your cliff-pad Banelings at the 4:30 minute mark if you detected a proxy. Keep the trap armed until you can confirm via your army that the Terran has completely stopped producing from their forward proxy buildings. [6] 
* 


[1] [https://terrancraft.com](https://terrancraft.com/2018/12/11/tvt-defending-against-proxy-reaper/)
[2] [https://www.youtube.com](https://www.youtube.com/watch?v=IiF-nTuwWn4&t=618)
[3] [https://liquipedia.net](https://liquipedia.net/starcraft2/Reaper_%28Legacy_of_the_Void%29)
[4] [https://liquipedia.net](https://liquipedia.net/starcraft2/Reactor_%28Legacy_of_the_Void%29)
[5] [https://starcraft.fandom.com](https://starcraft.fandom.com/wiki/Tech_lab/Development)
[6] [https://terrancraft.com](https://terrancraft.com/2017/06/28/tvz-triple-barracks-reaper/)
[7] [https://terrancraft.com](https://terrancraft.com/2015/06/06/reaper-expand-for-all-match-ups/)
[8] [https://www.reddit.com](https://www.reddit.com/r/allthingszerg/comments/ramqdh/dealing_with_a_massive_proxy_barracks_rush/)
[9] [https://www.youtube.com](https://www.youtube.com/watch?v=3xN-xLt_gVU&t=397)
