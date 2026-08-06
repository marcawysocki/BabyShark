# Worker Mining Label System - How Sharky Draws "Mining, Minerals, 0" Labels

## Summary: Label Display Architecture

The labels like **"Mining, Minerals, 0"** are displayed through a hierarchical system of role assignment and debug text rendering.

---

## 1. Label Generation (`MicroTask.cs`)

The base `MicroTask` class has a virtual `DebugUnits()` method that generates labels for all units under its control:

```csharp
public virtual void DebugUnits(DebugService debugService)
{
    foreach (var unit in UnitCommanders)
    {
        var cooldown = "";
        if (unit.UnitCalculation.Unit.HasWeaponCooldown)
        {
            cooldown = ", " + unit.UnitCalculation.Unit.WeaponCooldown.ToString();
        }
        if (unit.ChildUnitCalculations.Any())
        {
            cooldown += ", " + unit.ChildUnitCalculations.Count().ToString();
        }
        debugService.DebugUnitText(unit.UnitCalculation, 
            $"{CommanderDebugText ?? GetType().Name.Replace("Task", "", StringComparison.InvariantCultureIgnoreCase)}, {unit.UnitRole}{cooldown}", 
            CommanderDebugColor ?? debugService.DefaultMicroTaskColor);
    }
}
```

**Label Format:**
- **Task Name:** `GetType().Name.Replace("Task", "")` → "MiningTask" becomes "Mining"
- **Unit Role:** `unit.UnitRole` → "Minerals" or "Gas" (from the `UnitRole` enum)
- **Additional Info:** Optional cooldown or child unit count

The final label becomes: `"Mining" + ", " + "Minerals"` = **"Mining, Minerals"**

---

## 2. Unit Role Enum (`UnitRole.cs`)

The `UnitRole` enum defines all possible roles a unit can have:

```csharp
public enum UnitRole
{
    None,
    Bait,
    Scout,
    PreBuild,
    Build,
    Proxy,
    Minerals,        // ← Assigned to workers mining minerals
    Gas,             // ← Assigned to workers mining vespene gas
    Defend,
    Attack,
    PreventGasSteal,
    PreventBuildingLand,
    Wall,
    Door,
    Harass,
    Support,
    Repair,
    SpawnLarva,
    SpreadCreepWait,
    SpreadCreepWalk,
    SpreadCreepCast,
    Morph,
    Die,
    ChaseReaper,
    WallOff,
    Regenerate,
    Hide,
    Regroup,
    BlockExpansion,
    BlockAddon,
    Leader,
    NextLeader,
    Disband,
    RunAway,
    Chase,
    SaveEnergy,
    Cancel,
    Save,
    DoNotDefend,
    RecallMe
}
```

---

## 3. Role Assignment in MiningTask

### Frame 0 - Initial Split (`SplitWorkers()`)

In `GetSplitAssignments()`:
- All workers are assigned `UnitRole.Minerals` 
- Workers are distributed evenly across mineral patches
- Empty patches are balanced by taking workers from fuller patches

```csharp
List<MiningInfo> GetSplitAssignments()
{
    var miningAssignments = new List<MiningInfo>();
    foreach (var mineralField in BaseData.MainBase.MineralFields)
    {
        miningAssignments.Add(new MiningInfo(mineralField, BaseData.MainBase.ResourceCenter.Pos));
    }

    var workersPerField = UnitCommanders.Count() / (float)miningAssignments.Count();

    foreach (var worker in UnitCommanders.OrderByDescending(...))
    {
        worker.UnitRole = UnitRole.Minerals;  // ← ALL workers start as Minerals
        miningAssignments.Where(m => m.Workers.Count < Math.Ceiling(workersPerField))
            .OrderBy(m => Vector2.DistanceSquared(m.ResourceUnit.Pos.ToVector2(), worker.UnitCalculation.Position))
            .ThenBy(m => Vector2.Distance(m.ResourceUnit.Pos.ToVector2(), BaseData.MainBase.Location.ToVector2()))
            .First()
            .Workers.Add(worker);
    }
    
    // Handle empty mineral patches by balancing workers
    while (miningAssignments.Any(m => m.Workers.Count() == 0))
    {
        var empty = miningAssignments.FirstOrDefault(m => m.Workers.Count() == 0);
        if (empty != null)
        {
            var neighbor = miningAssignments.Where(m => m.Workers.Count() == 2)
                .OrderBy(m => Vector2.Distance(m.ResourceUnit.Pos.ToVector2(), empty.ResourceUnit.Pos.ToVector2()))
                .FirstOrDefault();
            if (neighbor != null)
            {
                var worker = neighbor.Workers.OrderBy(w => Vector2.Distance(w.UnitCalculation.Position, neighbor.ResourceUnit.Pos.ToVector2())).FirstOrDefault();
                neighbor.Workers.Remove(worker);
                empty.Workers.Add(worker);
            }
        }
    }

    return miningAssignments;
}
```

### Ongoing - Gas Worker Assignment (`BalanceGasWorkers()`)

```csharp
if (idleWorkers.Any())
{
    var worker = idleWorkers.FirstOrDefault();
    worker.UnitRole = UnitRole.Gas;        // ← Reassign Minerals workers to Gas role
    info.Workers.Add(worker);
    return actions;
}
```

### Cleanup - Remove Lost Workers (`RemoveLostWorkers()`)

```csharp
foreach (var commander in UnitCommanders)
{
    if (commander.UnitRole == UnitRole.Minerals)
    {
        if (!BaseData.SelfBases.Any(selfBase => 
            selfBase.MineralMiningInfo.Any(i => i.Workers.Any(w => w.UnitCalculation.Unit.Tag == commander.UnitCalculation.Unit.Tag))))
        {
            commander.UnitRole = UnitRole.None;  // ← Clear role if worker lost
        }
    }
    else if (commander.UnitRole == UnitRole.Gas)
    {
        if (!BaseData.SelfBases.Any(selfBase => 
            selfBase.GasMiningInfo.Any(i => i.Workers.Any(w => w.UnitCalculation.Unit.Tag == commander.UnitCalculation.Unit.Tag))))
        {
            commander.UnitRole = UnitRole.None;  // ← Clear role if worker lost
        }
    }
}
```

---

## 4. Label Rendering (`DebugService.cs`)

The `DebugService` class manages all debug text rendering:

```csharp
public void DebugUnitText(UnitCalculation unitCalculation, string text, Color color, uint size = 11)
{
    DebugUnitsInfo[unitCalculation.Unit.Tag] = new UnitDebugEntry(
        MacroData.Frame, 
        text,                    // e.g., "Mining, Minerals"
        unitCalculation,         // The unit's position and data
        color,                   // Color (DefaultMicroTaskColor = RGB 255,200,150)
        size);                   // Font size (default 11)
}

public void DrawUnitInfo()
{
    foreach (var unit in DebugUnitsInfo)
    {
        unit.Value.Draw(this);   // Calls UnitDebugEntry.Draw() which renders to 3D space
    }
}

public void ResetDrawRequest()
{
    TextLine = 0;
    DrawRequest = new Request();
    DrawRequest.Debug = new RequestDebug();
    DebugCommand debugCommand = new DebugCommand();
    debugCommand.Draw = new DebugDraw();
    DrawRequest.Debug.Debug.Add(debugCommand);

    // Remove unit debug info older than 5 frames
    DebugUnitsInfo = DebugUnitsInfo
        .Where(x => (MacroData.Frame - x.Value.LastFrameUpdate <= 5))
        .ToDictionary(k => k.Key, v => v.Value);
}
```

The `UnitDebugEntry` class handles the actual 3D world-space rendering of the text above units.

---

## 5. Mining Info Structure (`MiningInfo.cs`)

The `MiningInfo` class stores worker assignments per mineral patch or gas geyser:

```csharp
public class MiningInfo
{
    public MiningInfo(Unit resourceUnit, Point baseLocation)
    {
        ResourceUnit = resourceUnit;
        Workers = new List<UnitCommander>();

        var baseVector = new Vector2(baseLocation.X, baseLocation.Y);
        var mineralVector = new Vector2(ResourceUnit.Pos.X, ResourceUnit.Pos.Y);

        var angle = Math.Atan2(mineralVector.Y - baseVector.Y, baseVector.X - mineralVector.X);
        DropOffPoint = new Point2D 
        { 
            X = baseVector.X + (float)(-2 * Math.Cos(angle)), 
            Y = baseVector.Y - (float)(-2 * Math.Sin(angle)) 
        };

        var mineAngle = Math.Atan2(baseVector.Y - mineralVector.Y, mineralVector.X - baseVector.X);
        HarvestPoint = new Point2D 
        { 
            X = mineralVector.X + (float)(-.5 * Math.Cos(mineAngle)), 
            Y = mineralVector.Y - (float)(-.5 * Math.Sin(mineAngle)) 
        };
    }

    public List<UnitCommander> Workers { get; set; }    // Workers assigned to this patch
    public Unit ResourceUnit { get; set; }              // The mineral/gas unit
    public Point2D DropOffPoint { get; set; }           // Return-to location
    public Point2D HarvestPoint { get; set; }           // Harvest location
}
```

---

## 6. Complete Flow Diagram

```
FRAME 0 (Game Start)
  ↓
MiningTask.PerformActions()
  ↓
SplitWorkers()
  ↓
  GetSplitAssignments()
    └─→ For each worker: worker.UnitRole = UnitRole.Minerals
    └─→ Assign workers to MineralMiningInfo.Workers lists
    └─→ Return assignments; set BaseData.SelfBases[0].MineralMiningInfo
  
EACH FRAME (PerformActions)
  ↓
  ├─ RemoveLostWorkers()
  │   └─→ Clear UnitRole if worker not in mining lists
  │
  ├─ BalanceGasWorkers()
  │   └─→ If gas geyser undersaturated:
  │       └─→ worker.UnitRole = UnitRole.Gas
  │       └─→ Add to GasMiningInfo.Workers
  │
  ├─ MineralMiner.MineMinerals()
  │   └─→ Execute harvest orders for UnitRole.Minerals workers
  │
  ├─ GasMiner.MineGas()
  │   └─→ Execute harvest orders for UnitRole.Gas workers
  │
  └─ (Debug Manager calls DebugUnits() if SharkyOptions.DebugMicroTaskUnits)
     ↓
     MicroTask.DebugUnits()
       ↓
       For each UnitCommander:
         └─→ label = "Mining" + ", " + unit.UnitRole
         └─→ debugService.DebugUnitText(unit, label, color)
       ↓
     DebugService.DrawUnitInfo()
       ↓
       For each UnitDebugEntry:
         └─→ Draw 3D text at unit position
```

---

## 7. Key Files Summary

| File | Purpose | Key Methods/Properties |
|------|---------|------------------------|
| `Sharky/MicroTasks/Mining/MiningTask.cs` | Orchestrates worker mining; assigns roles | `PerformActions()`, `SplitWorkers()`, `BalanceGasWorkers()`, `RemoveLostWorkers()` |
| `Sharky/MicroTasks/Mining/MineralMiner.cs` | Handles mineral gathering commands | `MineMinerals()` |
| `Sharky/MicroTasks/Mining/GasMiner.cs` | Handles gas gathering commands | `MineGas()` |
| `Sharky/MiningInfo.cs` | Data structure for tracking workers per patch | `Workers`, `ResourceUnit`, `DropOffPoint`, `HarvestPoint` |
| `Sharky/Unit/UnitRole.cs` | Enum defining all unit roles | `Minerals`, `Gas`, `None`, etc. |
| `Sharky/MicroTasks/MicroTask.cs` | Base class for all micro tasks | `DebugUnits()`, `CommanderDebugText` property |
| `Sharky/DebugService.cs` | Handles text rendering to screen | `DebugUnitText()`, `DrawUnitInfo()`, `DrawText()` |
| `Sharky/Unit/UnitDebugEntry.cs` | Renders individual unit labels in 3D space | `Draw()` method |
| `Sharky/Managers/DebugManager.cs` | Calls DebugUnits() when `SharkyOptions.DebugMicroTaskUnits` is true | `OnFrame()` |

---

## 8. Label Example Walkthrough

**Step 1:** Worker is created and claimed by MiningTask  
→ `worker.UnitRole = UnitRole.Minerals` (assigned in `GetSplitAssignments()`)

**Step 2:** Each frame, if debug is enabled  
→ `MicroTask.DebugUnits()` is called  
→ For this worker: `label = "Mining" + ", " + "Minerals"`

**Step 3:** Label is registered  
→ `debugService.DebugUnitText(worker.UnitCalculation, "Mining, Minerals", orangeColor)`

**Step 4:** Label is rendered  
→ `DebugService.DrawUnitInfo()` iterates all registered labels  
→ `UnitDebugEntry.Draw(debugService)` renders "Mining, Minerals" at worker's 3D position

**Result:** "Mining, Minerals" appears above the worker unit in the game world

---

## 9. How to Customize Labels

### Option A: Change the Task Name Display

Override `CommanderDebugText` property in any `MicroTask` subclass:

```csharp
public class CustomMiningTask : MiningTask
{
    public CustomMiningTask(DefaultSharkyBot bot) : base(bot)
    {
        CommanderDebugText = "GatherWorker";  // Instead of "Mining"
    }
}
```

Now labels will show: "GatherWorker, Minerals"

### Option B: Change the Color

```csharp
public class CustomMiningTask : MiningTask
{
    public CustomMiningTask(DefaultSharkyBot bot) : base(bot)
    {
        CommanderDebugColor = new Color() { R = 0, G = 255, B = 0 }; // Green instead of orange
    }
}
```

### Option C: Add Custom Role Information

Extend the `DebugUnits()` method:

```csharp
public override void DebugUnits(DebugService debugService)
{
    base.DebugUnits(debugService);
    
    foreach (var unit in UnitCommanders)
    {
        var extraInfo = $" [{unit.LastTargetTag}]";
        // Custom logic here
    }
}
```

---

## 10. Debug Drawing System Integration

The debug drawing system works in this order:

1. **Request Creation** - `DebugService.ResetDrawRequest()` prepares a new debug request
2. **Label Registration** - `MicroTask.DebugUnits()` calls `DebugService.DebugUnitText()`
3. **Rendering** - `DebugService.DrawUnitInfo()` iterates all registered labels
4. **Transmission** - `DebugManager.OnFrame()` sends the request to the game
5. **Display** - SC2 API renders the text at the specified 3D coordinates

All debug drawing is controlled by `SharkyOptions.Debug` flag and specific options like `DebugMicroTaskUnits`.
