# Just-In-Time Mining Implementation for BabyShark SC2 AI Bot
C:\Users\marca\source\repos\BabyShark\# Just-In-Time Mining Implementation for BabyShark.MD
C:\Users\marca\source\repos\BabyShark\BabySharkBot\MicroTasks\TeamPatchMiningTask.cs
C:\Users\marca\source\repos\BabyShark\BabySharkBot\Managers\BabySharkMiningManager.cs
C:\Users\marca\source\repos\BabyShark\BabySharkBot\BabySharkBot.cs
C:\Users\marca\source\repos\BabyShark\docs\JIT-Mining-Error-Notes.md


## 📋 Overview

Implement Just-In-Time (JIT) Mining in the BabyShark StarCraft 2 AI bot. The system should automatically switch between Speed Mining and JIT Mining based on worker count per mineral node team.

C:\Users\marca\source\repos\BabyShark\BabySharkBot\Managers\CcaManager.cs should be adding the Mining Managers after Frame 15
  
                // After frame 15, if bump is disabled, issue mining gather orders for any worker that isn't mining
                if (frame >= 15)
             

### Requirements:
- Start with 12 or 8 workers at main base
- Use Speed Mining for teams with ≠ 3 workers
- Use JIT Mining for teams with exactly 3 workers
- JIT: Workers alternate between paired minerals after returning cargo
- Speed Mining: Workers return to same mineral after returning cargo
- Handle both main base and expansion locations
- Order minerals clockwise around town halls

## 🏗️ Implementation Plan

### 1. Data Structures

Add to `Setup/BaseDtos.cs`:

```csharp
public class MineralNode
{
    public Point2D Position { get; set; }
    public Unit MineralUnit { get; set; }
    public float AngleFromCenter { get; set; }
    public bool IsLargeMineral { get; set; }
    public string Identifier { get; set; }
    public bool IsNearMineral { get; set; }
    public float DistanceFromTownHall { get; set; }
}

public class MiningTeam
{
    public string TeamId { get; set; }
    public List<Unit> Workers { get; set; } = new List<Unit>();
    public MineralNode MineralA { get; set; }
    public MineralNode MineralB { get; set; }
    public Dictionary<ulong, int> WorkerLastMineFrame { get; set; } = new Dictionary<ulong, int>();
    public Dictionary<ulong, bool> WorkerLastMinedA { get; set; } = new Dictionary<ulong, bool>();
    public bool IsJITTeam { get; set; } = false;
    public Point2D ExpansionPosition { get; set; }
    public int TeamIndex { get; set; }
}
```

### 2. Core Manager Updates

Add to `Managers/BabySharkMiningManager.cs` fields:

```csharp
private Dictionary<Point2D, List<MineralNode>> _expansionMinerals = new Dictionary<Point2D, List<MineralNode>>();
private Dictionary<Point2D, List<MiningTeam>> _expansionTeams = new Dictionary<Point2D, List<MiningTeam>>();
private Dictionary<ulong, string> _workerTeamAssignment = new Dictionary<ulong, string>();
```

### 3. Core Methods for JIT Mining

Add these methods to `BabySharkMiningManager.cs`:

```csharp
public void InitializeExpansionMining(Point2D expansionPosition, List<Unit> minerals)
{
    var expansionKey = NormalizePoint(expansionPosition);
    
    // Create mineral nodes with proper ordering
    var mineralNodes = CreateOrderedMineralNodes(expansionPosition, minerals);
    _expansionMinerals[expansionKey] = mineralNodes;

    // Create teams based on mineral pairs
    var teams = new List<MiningTeam>();
    
    // Pair up minerals (M1-M2, M3-M4, etc.)
    for (int i = 0; i < mineralNodes.Count - 1; i += 2)
    {
        var team = new MiningTeam
        {
            TeamId = $"{expansionKey.X:F1}_{expansionKey.Y:F1}_T{i/2 + 1}",
            MineralA = mineralNodes[i],
            MineralB = mineralNodes[i + 1],
            IsJITTeam = false,
            ExpansionPosition = expansionPosition,
            TeamIndex = i/2
        };
        teams.Add(team);
    }
    
    _expansionTeams[expansionKey] = teams;
}

private Point2D NormalizePoint(Point2D point)
{
    return new Point2D { X = (float)System.Math.Round(point.X, 1), Y = (float)System.Math.Round(point.Y, 1) };
}

private List<MineralNode> CreateOrderedMineralNodes(Point2D expansionPosition, List<Unit> minerals)
{
    var mineralNodes = new List<MineralNode>();
    
    foreach (var mineral in minerals)
    {
        var distance = Distance(expansionPosition, mineral.Pos);
        var node = new MineralNode
        {
            Position = mineral.Pos,
            MineralUnit = mineral,
            IsLargeMineral = IsRichMineral(mineral.UnitType),
            AngleFromCenter = CalculateAngleFromCenter(expansionPosition, mineral.Pos),
            DistanceFromTownHall = distance,
            IsNearMineral = distance < 10.0f
        };
        mineralNodes.Add(node);
    }

    // Sort by angle to get clockwise ordering
    mineralNodes.Sort((a, b) => a.AngleFromCenter.CompareTo(b.AngleFromCenter));
    
    // Assign identifiers M1, M2, etc.
    for (int i = 0; i < mineralNodes.Count; i++)
    {
        mineralNodes[i].Identifier = $"M{i + 1}";
    }
    
    return mineralNodes;
}

private bool IsRichMineral(uint unitType)
{
    var unitTypeStr = ((UnitTypes)unitType).ToString();
    return unitTypeStr.Contains("RICH") || 
           unitType == (uint)UnitTypes.PROTOSS_ASSIMILATOR_RICH || 
           unitType == (uint)UnitTypes.TERRAN_REFINERY_RICH || 
           unitType == (uint)UnitTypes.ZERG_EXTRACTOR_RICH;
}

private float CalculateAngleFromCenter(Point2D center, Point2D point)
{
    float deltaX = point.X - center.X;
    float deltaY = point.Y - center.Y;
    return (float)System.Math.Atan2(deltaY, deltaX);
}

private float Distance(Point2D a, Point2D b)
{
    float dx = a.X - b.X;
    float dy = a.Y - b.Y;
    return (float)System.Math.Sqrt(dx * dx + dy * dy);
}

public void AssignWorkerToJITTeam(Unit worker, Point2D expansionPosition)
{
    var expansionKey = NormalizePoint(expansionPosition);
    
    if (!_expansionTeams.ContainsKey(expansionKey))
        return;

    var teams = _expansionTeams[expansionKey];
    if (teams.Count == 0)
        return;

    // Find the team with the fewest workers
    var targetTeam = teams.OrderBy(t => t.Workers.Count).First();
    
    // Check if worker is already assigned to this team
    if (!targetTeam.Workers.Any(w => w.Tag == worker.Tag))
    {
        targetTeam.Workers.Add(worker);
        _workerTeamAssignment[worker.Tag] = targetTeam.TeamId;
    }

    // Check if this team now has 3 workers and should switch to JIT mining
    if (targetTeam.Workers.Count == 3 && !targetTeam.IsJITTeam)
    {
        targetTeam.IsJITTeam = true;
        // Initialize worker tracking
        foreach (var teamWorker in targetTeam.Workers)
        {
            if (!targetTeam.WorkerLastMinedA.ContainsKey(teamWorker.Tag))
            {
                targetTeam.WorkerLastMinedA[teamWorker.Tag] = false;
            }
        }
    }
}

public Point2D GetJITMiningTarget(Unit worker, Point2D expansionPosition, Point2D currentMineralPosition)
{
    var expansionKey = NormalizePoint(expansionPosition);
    
    if (!_expansionTeams.ContainsKey(expansionKey))
        return currentMineralPosition;

    // Find which team this worker belongs to
    var team = FindWorkerTeam(worker, expansionKey);
    if (team == null || !team.IsJITTeam)
        return currentMineralPosition;

    // For JIT teams, alternate between MineralA and MineralB
    bool lastMinedA = team.WorkerLastMinedA.ContainsKey(worker.Tag) ? 
        team.WorkerLastMinedA[worker.Tag] : false;
    
    // Alternate: if last mined A, now mine B, and vice versa
    Point2D targetPosition;
    if (lastMinedA)
    {
        targetPosition = team.MineralB.Position;
        team.WorkerLastMinedA[worker.Tag] = false;
    }
    else
    {
        targetPosition = team.MineralA.Position;
        team.WorkerLastMinedA[worker.Tag] = true;
    }
    
    return targetPosition;
}

private MiningTeam FindWorkerTeam(Unit worker, Point2D expansionKey)
{
    if (!_expansionTeams.ContainsKey(expansionKey))
        return null;

    if (!_workerTeamAssignment.ContainsKey(worker.Tag))
        return null;

    var teamId = _workerTeamAssignment[worker.Tag];
    
    foreach (var team in _expansionTeams[expansionKey])
    {
        if (team.TeamId == teamId)
            return team;
    }
    return null;
}

public bool IsJITTeamWorker(Unit worker)
{
    return _workerTeamAssignment.ContainsKey(worker.Tag);
}

public bool ShouldUseJITMining(Unit worker)
{
    if (!_workerTeamAssignment.ContainsKey(worker.Tag))
        return false;

    // Find the team this worker belongs to
    foreach (var kvp in _expansionTeams)
    {
        foreach (var team in kvp.Value)
        {
            if (team.Workers.Any(w => w.Tag == worker.Tag))
            {
                return team.IsJITTeam;
            }
        }
    }
    return false;
}
```

### 4. Integration with TeamPatchMiningTask

Update `MicroTasks/TeamPatchMiningTask.cs`:

```csharp
public override void OnFrame(BotData botData)
{
    // Existing code...
    
    foreach (var patch in Patches)
    {
        // Handle worker assignments
        foreach (var worker in patch.Workers)
        {
            // Check if this should use JIT mining
            if (_babySharkMiningManager.ShouldUseJITMining(worker))
            {
                // Apply JIT mining logic
                HandleJITWorker(worker, patch, botData);
            }
            else
            {
                // Use existing speed mining logic
                HandleSpeedMiningWorker(worker, patch, botData);
            }
        }
    }
}

private void HandleJITWorker(Unit worker, MiningPatch patch, BotData botData)
{
    // Get the current target mineral
    var currentTarget = GetCurrentTargetMineral(worker);
    
    // Get the next target using JIT logic
    var nextTarget = _babySharkMiningManager.GetJITMiningTarget(
        worker, 
        patch.TownHall.Pos, 
        currentTarget?.Pos ?? patch.MineralFields.FirstOrDefault()?.Pos
    );
    
    // Move to the target mineral
    if (nextTarget != null)
    {
        var action = MiningAction(worker, nextTarget, botData);
        if (action != null)
        {
            botData.Actions.Add(action);
        }
    }
}

private void HandleSpeedMiningWorker(Unit worker, MiningPatch patch, BotData botData)
{
    // Existing speed mining logic
    var targetMineral = GetBestMineralForWorker(worker, patch);
    if (targetMineral != null)
    {
        var action = MiningAction(worker, targetMineral.Pos, botData);
        if (action != null)
        {
            botData.Actions.Add(action);
        }
    }
}
```

## 🔧 Integration Points

### Initialization:
Call `InitializeExpansionMining()` when detecting new expansions

### Worker Assignment:
Call `AssignWorkerToJITTeam()` when assigning workers to expansions

### Mining Target Selection:
Replace mineral target selection with `GetJITMiningTarget()` for JIT teams

## 🎯 Behavior Specification

### Speed Mining (≠ 3 workers):
- Worker mines same mineral continuously
- Returns cargo and goes back to same mineral

### JIT Mining (= 3 workers):
- Workers paired to alternate between MineralA and MineralB
- After returning cargo, worker switches to partner mineral
- Pattern: Mine A → Return → Mine B → Return → Mine A...

## ✅ Validation Criteria

1. Teams with 1-2 or 4+ workers use Speed Mining
2. Teams with exactly 3 workers use JIT Mining
3. Mineral ordering is clockwise around town halls
4. Worker assignments are balanced across teams
5. Existing functionality remains unchanged
6. Performance impact is minimal

## 📈 Expected Benefits

- Reduced congestion at mineral patches
- Improved mining path efficiency
- Better resource collection rates
- Automatic optimization based on worker count
- Seamless transition between mining modes