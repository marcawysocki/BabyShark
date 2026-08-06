SharkyBuildOrderSystem.md

# Sharky Build Order System  
### *Full File-Level Breakdown & End-to-End Process Flow*  
Source: [https://github.com/sharknice/Sharky](https://github.com/sharknice/Sharky) 

---

## 1. Overview

Sharky’s Build Order system is a **modular, commander-driven pipeline**.  
A “Build” in Sharky is not just a list of steps — it is a **full strategy module** containing:

- **Build Choices** (when to switch builds)
- **Build Options** (metadata)
- **Build Order** (the actual sequence of actions)
- **MicroControllers** (unit micro logic)
- **Production & Macro Controllers** (how units/buildings are produced)
- **Managers** (economy, scouting, attack, defense)

The Build Order is executed by **ProductionCommander**, **MacroManager**, and **BuildManager**, which translate build steps into actual SC2 API commands.

---

# 2. Files Involved in Build Orders

Below is the **complete list of files** directly involved in defining, selecting, and executing build orders.

---

## 2.1 Build Definitions (Build Modules)

These files define actual builds (e.g., 3-Gate, 12-Pool, Reaper Expand).

**Directory:**  
`Sharky/Builds/`

**Key files:**

- `Build.cs`  
  Base class for all builds. Defines:
  - `StartBuild()`
  - `OnFrame()`
  - `BuildOptions`
  - `BuildOrder`
  - `CounterTransition()`

- `BuildOrder.cs`  
  Defines the structure of a build order:
  - Supply-based steps
  - Conditional steps
  - Queueing production actions

- Race-specific build folders:  
  - `Sharky/Builds/Protoss/`  
  - `Sharky/Builds/Terran/`  
  - `Sharky/Builds/Zerg/`

Examples:  
- `ProtossBuild.cs`  
- `TerranBuild.cs`  
- `ZergBuild.cs`

These contain concrete build orders like:
- Adept Rush  
- 3-Gate Robo  
- 12-Pool  
- Reaper Expand

---

## 2.2 Build Selection System

These files determine **which build to run** based on scouting, enemy race, map, and strategy detection.

**Directory:**  
`Sharky/Builds/BuildChoosing/`

**Key files:**

- `BuildChoices.cs`  
  Contains the list of available builds for the bot.

- `BuildDecisionService.cs`  
  Chooses the build at game start or mid-game transitions.

- `BuildSelector.cs`  
  Applies logic to pick the best build.

- `BuildMatcher.cs`  
  Matches enemy strategies to counter-builds.

---

## 2.3 Build Execution Pipeline

These files translate build steps into actual SC2 commands.

### Production Commander  
**Directory:**  
`Sharky/Macro/Production/`

**Key files:**

- `ProductionCommander.cs`  
  Central executor of build order steps.  
  Converts build steps → unit production → building construction.

- `ProductionBuilder.cs`  
  Issues SC2 API commands to produce units and buildings.

- `ProductionQueue.cs`  
  Queue of pending production actions.

- `ProductionStep.cs`  
  Represents a single build order step.

---

### Macro Manager  
**Directory:**  
`Sharky/Macro/`

**Key files:**

- `MacroManager.cs`  
  Oversees economy, spending, and production priorities.

- `MacroData.cs`  
  Shared macro state (minerals, gas, supply, production queues).

---

### Build Manager  
**Directory:**  
`Sharky/Macro/Builds/`

**Key files:**

- `BuildManager.cs`  
  Executes building placement and construction logic.

- `BuildingPlacement.cs`  
  Determines where buildings should be placed.

---

## 2.4 Unit Production Logic

These files handle training units according to build order.

**Directory:**  
`Sharky/Macro/Production/Units/`

**Key files:**

- `UnitProduction.cs`  
  Trains units based on build order and macro state.

- `UnitProductionBuilder.cs`  
  Issues SC2 API commands for unit training.

---

## 2.5 Upgrade & Research Logic

**Directory:**  
`Sharky/Macro/Production/Upgrades/`

**Key files:**

- `UpgradeProduction.cs`  
  Executes upgrade steps in build orders.

---

## 2.6 Build Order Conditions & Requirements

**Directory:**  
`Sharky/Builds/BuildOrder/`

**Key files:**

- `BuildOrderStep.cs`  
  A single step (e.g., “Build Gateway at 16 supply”).

- `BuildOrderCondition.cs`  
  Conditional logic (e.g., “If enemy is Zerg, build Stargate”).

- `BuildOrderOptions.cs`  
  Metadata for build orders.

---

## 2.7 Strategy & Enemy Analysis (affects build switching)

**Directory:**  
`Sharky/Strategy/`

**Key files:**

- `EnemyStrategyAnalyzer.cs`  
  Detects enemy build.

- `StrategyManager.cs`  
  Determines if build should switch.

---

## 2.8 Managers That Influence Build Execution

These managers indirectly affect build order execution:

### Economy  
`Sharky/Economy/`

- Worker distribution  
- Gas mining  
- Expansion timing

### Scouting  
`Sharky/Scouting/`

- Provides data for build switching

### Attack/Defense  
`Sharky/Attack/`  
`Sharky/Defense/`

- Can override build steps (e.g., emergency unit production)

---

# 3. Full Build Order Execution Pipeline

This is the **complete end-to-end flow** from game start to build execution.

---

## **Step 1 — Game Start**
- Sharky initializes all managers.
- `BuildDecisionService` selects the initial build.
- `Build.StartBuild()` is called.

---

## **Step 2 — Build Order Initialization**
The selected build loads:

- `BuildOptions`
- `BuildOrder`
- `MicroControllers`
- `TransitionRules`

---

## **Step 3 — Per-Frame Build Execution**
Every frame:

`Build.OnFrame()` runs and may:

- Add steps to `ProductionQueue`
- Trigger transitions
- Modify macro priorities

---

## **Step 4 — ProductionCommander Executes Steps**
`ProductionCommander` processes the queue:

1. Check supply requirements  
2. Check resource availability  
3. Check building prerequisites  
4. Issue SC2 API commands

---

## **Step 5 — MacroManager Adjusts Economy**
MacroManager ensures:

- Enough workers  
- Enough gas  
- Enough supply  
- Correct spending priorities

---

## **Step 6 — BuildManager Places Buildings**
BuildManager determines:

- Building placement  
- Pathing  
- Wall-offs  
- Creep requirements (Zerg)

---

## **Step 7 — UnitProduction Trains Units**
UnitProduction:

- Trains units from production queue  
- Ensures correct larva/warp gate/barracks usage

---

## **Step 8 — UpgradeProduction Executes Upgrades**
UpgradeProduction:

- Starts upgrades  
- Ensures prerequisites  
- Manages research buildings

---

## **Step 9 — StrategyManager May Switch Builds**
If enemy strategy changes:

- StrategyManager triggers build switch  
- BuildDecisionService selects new build  
- New build’s `StartBuild()` is called

---

# 4. Summary Table

| Component | Purpose | Files |
|----------|---------|-------|
| Build Definitions | Define build orders | `Build.cs`, race-specific builds |
| Build Selection | Choose build | `BuildDecisionService.cs`, `BuildChoices.cs` |
| Build Execution | Execute steps | `ProductionCommander.cs`, `ProductionBuilder.cs` |
| Macro System | Economy & production | `MacroManager.cs`, `MacroData.cs` |
| Building Placement | Construct buildings | `BuildManager.cs`, `BuildingPlacement.cs` |
| Unit Production | Train units | `UnitProduction.cs` |
| Upgrades | Research upgrades | `UpgradeProduction.cs` |
| Strategy Detection | Switch builds | `StrategyManager.cs`, `EnemyStrategyAnalyzer.cs` |

---

