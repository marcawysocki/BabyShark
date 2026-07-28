# Sharky Bot: Dynamic Manager Loading & Priority System  
### A Technical Guide for GitHub Copilot

This document explains how to design a **dynamic Manager system** for a Sharky‑based C# StarCraft II bot.  
It covers:

- How Sharky processes Managers each frame  
- How to load Managers only when needed  
- How dynamic priority works  
- How to activate Managers based on game state  
- How to delay expensive logic until it becomes relevant  

Use this file as reference material for GitHub Copilot when generating or modifying your bot’s architecture.

---

# 1. How Sharky Processes Managers

Sharky maintains a list:

```csharp
public List<IManager> Managers { get; set; }

Every frame, Sharky:

Sorts Managers by Priority (ascending)

Skips Managers where Enabled == false

Calls OnFrame() on each Manager in sorted order

This means:

Lower Priority = runs earlier

Priority is evaluated every frame

Managers can be added or removed at runtime

Managers can change priority dynamically

2. Loading Only Early‑Game Managers

You do not need to load all Managers at game start.

Example early‑game Managers:

WorkerManager

BuildManager

ProductionManager

Map/Intel Managers

Basic Micro (optional)

Example late‑game Managers:

MarineManager

DropManager

HarassManager

TechSwitchManager

EndGameManager

You can add Managers dynamically:

if (!Managers.Any(m => m is MarineManager) &&
    UnitCountService.EquivalentTypeCount(UnitTypes.TERRAN_BARRACKS) > 0)
{
    Managers.Add(new MarineManager(...));
}

Sharky will automatically:

Call MarineManager.OnStart() immediately

Insert it into the sorted Manager list next frame

3. Dynamic Priority System

A Manager’s priority does not need to be a constant.

You can compute it based on:

Game time

Enemy tech

Unit availability

Threat level

Scouting information

Example: WorkerSafetyManager

Only becomes important after rush distance time has passed.

public class WorkerSafetyManager : IManager
{
    const int RushSafetyFrame = 918; // ~41 seconds

    public int Priority
    {
        get
        {
            if (SharkyOptions.Frames < RushSafetyFrame)
            {
                return 900; // very low priority early
            }
            return 30; // high priority once harassment is possible
        }
    }

    public bool Enabled => SharkyOptions.Frames >= RushSafetyFrame;

    public void OnFrame(ResponseObservation obs)
    {
        if (!Enabled) { return; }

        // worker safety logic here
    }
}

Result:

First 41 seconds: Manager is disabled and low priority

After 41 seconds: Manager activates and moves to the top of the list

4. Recommended Priority Bands

To keep ordering predictable, use priority ranges:

Range

Purpose

0–99

Economy, production, build order, worker logic

100–199

Early micro, scouting, worker defense

200–399

Unit‑specific micro (MarineManager, AdeptManager, etc.)

400–599

Midgame strategy, attack/defense, harassment

600–999

Late‑game logic, tech switching, cleanup

This ensures new Managers always slot into a stable, deterministic location.

5. Enabling Managers Only When Needed

You can completely disable a Manager until its logic is relevant:

public bool Enabled => UnitCountService.EquivalentTypeCount(UnitTypes.TERRAN_MARINE) > 0;

Or based on tech:

public bool Enabled => ResearchCompleted(Upgrades.STIMPACK);

Or based on supply:

public bool Enabled => GameState.SupplyUsed >= 40;

Sharky will skip disabled Managers automatically.

6. Dynamic Manager Activation Examples

Activate MarineManager when Barracks starts producing:

if (ProductionService.CanProduce(UnitTypes.TERRAN_MARINE))
{
    Managers.Add(new MarineManager(...));
}

Activate DropManager when Medivacs exist:

if (UnitCountService.Count(UnitTypes.TERRAN_MEDIVAC) > 0)
{
    Managers.Add(new DropManager(...));
}

Activate HarassManager after 3 bases:

if (BaseData.Owned.Count >= 3)
{
    Managers.Add(new HarassManager(...));
}

7. Summary

Sharky sorts Managers by priority every frame

Priority can be dynamic, based on game state

Managers can be added at runtime

Managers can be disabled until needed

This allows a lean early game and scalable mid/late game

Dynamic priority enables precise timing (e.g., rush distance activation)

This architecture keeps your bot fast, modular, and highly adaptive.

8. Suggested Prompt for GitHub Copilot

You can paste this into Copilot Chat:

Use the attached Markdown file as architectural context.  
I am building a Sharky-based SC2 bot with dynamic Manager loading and dynamic priority.  
Managers should activate only when relevant, and priority should change based on game state.  
Follow the patterns described in the document when generating new Managers or modifying existing ones.


InitialMiningData has been renamed InitialMapData.  Both Class and .sc file have been renamed. Currently, we are forcing it to run with a flag.  Ideally it would run only on a new map and data would be loaded from serialized dat files in BaseDtos.

THe Old bot is saved as "C:\Users\marca\source\repos\PupusPistrixVectatorPestium\PupusPistrixVectatorPestiumBot\MicroControllers\Old BabySharkBot .cs .MD"  Currently, I need a new starting Bot running InitialMapData when either the flag was set or the dat files are missing.  The only Manager running will a debug drawing manager that works with WorkerLabelService and draws any debugging information needed to get my complete fork of https://github.com/sharknice/Sharky at this point we do not have a properly working Manager.

This new bot will use IBabySharkBot and BabySharkBot 

We will not be running anything the old bot tried.  The new bot will do OnOpen and see that it needs to run InitialMapData, after it runs as is, OnFrame should run the Debug Drawing Manager to send all the drawing objects to the client. New Managers will be added as we get each new thing working.

Any Manager that has an Identical name as  Sharky will have the prefix "Maw"  for example Sharky BuildManager, Maw Manager would be MawBuildManager.  