### REASONING CONTEXT

Based on the codebase analysis, I've identified that the worker mining assignment logic is breaking down due to improper label management and potential enemy base targeting. The issue appears to be in the worker labeling system where workers aren't being properly assigned labels for mining assignments, leading to them targeting incorrect locations including enemy bases.

The core problem lies in:
1. WorkerLabelService not properly initializing or maintaining worker labels
2. Mining assignment logic potentially using uninitialized or incorrect labels
3. Lack of proper validation when determining target locations for mining assignments

### TASK DECOMPOSITION

To fix the worker mining assignment bug, I need to analyze and modify the following components:

1. **WorkerLabelService** - Ensure proper label initialization and maintenance
2. **BabySharkMiningManager** - Fix the mining assignment logic that may be targeting enemy bases
3. **InitialMapData** - Verify proper data structure population for worker assignments
4. **CrosshairService** - Ensure COM visualization doesn't interfere with mining logic

### FILE INDEX

<file_package path="src/BabyShark/Workers/Services/WorkerLabelService.cs">
```csharp
using System;
using System.Collections.Generic;
using Sharky;
using Sharky.MicroTasks;

namespace BabyShark.Workers.Services
{
    public class WorkerLabelService : IService
    {
        private readonly Dictionary<int, string> workerLabels = new();
        private readonly Dictionary<string, int> labelToWorker = new();

        public void SetLabel(int unitTag, string label)
        {
            if (workerLabels.ContainsKey(unitTag))
            {
                var oldLabel = workerLabels[unitTag];
                labelToWorker.Remove(oldLabel);
            }

            workerLabels[unitTag] = label;
            labelToWorker[label] = unitTag;
        }

        public string GetLabel(int unitTag)
        {
            return workerLabels.ContainsKey(unitTag) ? workerLabels[unitTag] : null;
        }

        public int? GetWorker(string label)
        {
            return labelToWorker.ContainsKey(label) ? labelToWorker[label] : (int?)null;
        }

        public void ClearLabel(int unitTag)
        {
            if (workerLabels.TryGetValue(unitTag, out var label))
            {
                workerLabels.Remove(unitTag);
                labelToWorker.Remove(label);
            }
        }

        public bool HasValidLabel(int unitTag)
        {
            return workerLabels.ContainsKey(unitTag) && !string.IsNullOrEmpty(workerLabels[unitTag]);
        }

        public Dictionary<int, string> GetAllLabels()
        {
            return new Dictionary<int, string>(workerLabels);
        }

        public void ClearAllLabels()
        {
            workerLabels.Clear();
            labelToWorker.Clear();
        }
    }
}
```

<file_package path="src/BabyShark/Workers/Managers/BabySharkMiningManager.cs">
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Sharky;
using Sharky.MicroTasks;

namespace BabyShark.Workers.Managers
{
    public class BabySharkMiningManager :IManager
    {
        private readonly WorkerLabelService workerLabelService;
        private readonly CrosshairService crosshairService;
        private readonly IPathingData pathingData;
        private readonly IDebugService debugService;
        
        private List<OrderedMineral> orderedMainMinerals = new();
        private Dictionary<int, string> miningAssignments = new();

        public BabySharkMiningManager(
            WorkerLabelService workerLabelService,
            CrosshairService crosshairService,
            IPathingData pathingData,
            IDebugService debugService)
        {
            this.workerLabelService = workerLabelService;
            this.crosshairService = crosshairService;
            this.pathingData = pathingData;
            this.debugService = debugService;
        }

        public void InitializeMiningAssignments(List<MineralField> minerals, List<VespeneGeyser> vespeneGeysers, Point2D mineralCenterOfMass)
        {
            // Clear existing assignments
            miningAssignments.Clear();
            
            // Validate inputs
            if (minerals == null || minerals.Count == 0)
            {
                DebugLogger.Error("BabySharkMiningManager: No minerals provided for assignment");
                return;
            }

            // Calculate anchor worker position (middle worker)
            var workers = WorkerCache.Instance.Units.Values
                .Where(u => u.UnitClassifications.Contains(UnitClassification.Worker) && 
                           !u.IsFlying && !u.IsHallucination)
                .ToList();

            if (workers.Count == 0)
            {
                DebugLogger.Error("BabySharkMiningManager: No workers found");
                return;
            }

            // Sort workers by distance from COM
            var sortedWorkers = workers.OrderBy(w => DistanceSquared(w.Pos, mineralCenterOfMass)).ToList();
            
            string anchorLabel;
            if (workers.Count == 8)
            {
                anchorLabel = "W3"; // Anchor for 8-worker start
            }
            else if (workers.Count == 12)
            {
                anchorLabel = "W6"; // Anchor for 12-worker start
            }
            else
            {
                anchorLabel = $"WA{workers.Count / 2 - 1}"; // Dynamic anchor
            }

            var anchorWorker = sortedWorkers.FirstOrDefault(w => GetLabel(w) == anchorLabel);
            if (anchorWorker == null)
            {
                DebugLogger.Error($"BabySharkMiningManager: Anchor worker {anchorLabel} not found");
                return;
            }

            // Phase 1: Find furthest mineral from anchor = M
            var furthestMineral = minerals.OrderBy(m => DistanceSquared(m.Pos, anchorWorker.Pos)).Last();
            
            // Phase 2: Greedy chain assignment
            var remainingMinerals = minerals.Except(new[] { furthestMineral }).ToList();
            var greedyOrder = new List<MineralField> { furthestMineral };

            while (remainingMinerals.Count > 0)
            {
                var closest = remainingMinerals.OrderBy(m => DistanceSquared(m.Pos, greedyOrder.Last().Pos)).First();
                greedyOrder.Add(closest);
                remainingMinerals.Remove(closest);
            }

            // Phase 3: Classify Near/Far and assign labels
            for (int i = 0; i < greedyOrder.Count && i < workers.Count - 1; i++)
            {
                var mineral = greedyOrder[i];
                var worker = sortedWorkers[i + 1]; // Skip anchor
                
                var distanceToCom = Distance(mineral.Pos, mineralCenterOfMass);
                var avgDistance = minerals.Average(m => Distance(m.Pos, mineralCenterOfMass));
                var isNear = distanceToCom < avgDistance;

                var label = $"M{i}"; // M0, M1, etc.
                
                workerLabelService.SetLabel(worker.Tag, label);
                miningAssignments[worker.Tag] = mineral.Tag.ToString();
                
                // Visual debugging
                crosshairService.SetCOM(mineral.Pos, label, isNear ? Colors.Green : Colors.Red);
            }

            // Ensure all workers have labels
            foreach (var worker in workers)
            {
                if (!workerLabelService.HasValidLabel(worker.Tag))
                {
                    workerLabelService.SetLabel(worker.Tag, $"W{Array.IndexOf(workers.ToArray(), worker)}");
                }
            }

            DebugLogger.Info($"BabySharkMiningManager: Assigned {greedyOrder.Count} minerals to {workers.Count - 1} workers");
        }

        public List<OrderedMineral> GetOrderedMainMinerals()
        {
            return orderedMainMinerals;
        }

        private double DistanceSquared(Point2D p1, Point2D p2)
        {
            var dx = p1.X - p2.X;
            var dy = p1.Y - p2.Y;
            return dx * dx + dy * dy;
        }

        private double Distance(Point2D p1, Point2D p2)
        {
            return Math.Sqrt(DistanceSquared(p1, p2));
        }

        private string GetLabel(Unit unit)
        {
            return workerLabelService.GetLabel(unit.Tag) ?? $"U{unit.Tag}";
        }
    }
}
```

<file_package path="src/BabyShark/Setup/InitialMapData.cs">
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Sharky;

namespace BabyShark.Setup
{
    public class InitialMapData : IInitialMapData
    {
        public List<MineralField> MainMinerals { get; set; }
        public List<VespeneGeyser> MainVespene { get; set; }
        public Point2D MineralCenterOfMS { get; set; }
        public Dictionary<int, string> StartingUnits { get; set; }
        public List<OrderedMineral> OrderedMainMinerals { get; set; }
        public List<Point2D> ExpansionTownhalls { get; set; }
        public bool BaseHasBeenPlayed { get; set; }

        public InitialMapData()
        {
            StartingUnits = new Dictionary<int, string>();
            ExpansionTownhalls = new List<Point2D>();
            OrderedMainMinerals = new List<OrderedMineral>();
        }

        public void ValidateData()
        {
            if (MainMinerals == null || MainMinerals.Count == 0)
            {
                throw new InvalidOperationException("InitialMapData: No main minerals loaded");
            }

            if (MineralCenterOfMS == default(Point2D))
            {
                throw new InvalidOperationException("InitialMapData: Mineral center of mass not calculated");
            }
        }
    }
}
```

<file_package path="src/BabyShark/Workers/MicroTasks/TeamPatchMiningTask.cs">
```csharp
using System;
using System.Linq;
using Sharky;
using Sharky.MicroTasks;

namespace BabyShark.Workers.MicroTasks
{
    public class TeamPatchMiningTask : MicroTask
    {
        private readonly WorkerLabelService workerLabelService;
        private readonly BabySharkMiningManager miningManager;
        private readonly IUnitData unitData;

        public TeamPatchMiningTask(
            WorkerLabelService workerLabelService,
            BabySharkMiningManager miningManager,
            IUnitData unitData)
        {
            this.workerLabelService = workerLabelService;
            this.miningManager = miningManager;
            this.unitData = unitData;
        }

        public override bool Execute(UnitCommander uc, Point2D target, bool queue = true)
        {
            var unit = uc.Unit;
            
            // Validate target is a valid mineral patch
            if (unitData.MineralFields.ContainsKey(target))
            {
                var mineral = unitData.MineralFields[target];
                
                // Check if worker has valid mining label
                var label = workerLabelService.GetLabel(unit.Tag);
                if (!string.IsNullOrEmpty(label) && !label.StartsWith("M"))
                {
                    DebugLogger.Warning($"TeamPatchMiningTask: Worker {unit.Tag} has invalid label '{label}' for mineral assignment");
                    return false;
                }

                // Execute mining command
                uc.Order(Abilities.EFFECT_MINERALFIELD, target);
                return true;
            }
            
            // If no valid mineral, return false to trigger re-evaluation
            return false;
        }

        public override bool IsNeeded(UnitCommander uc)
        {
            var unit = uc.Unit;
            
            // Only workers need mining tasks
            if (!unit.UnitClassifications.Contains(UnitClassification.Worker))
                return false;

            // Check if worker has valid label
            var label = workerLabelService.GetLabel(unit.Tag);
            return !string.IsNullOrEmpty(label) && label.StartsWith("M");
        }
    }
}
```

<file_package path="src/BabyShark/Setup/BaseDtos.cs">
```csharp
using System;
using System.Collections.Generic;

namespace BabyShark.Setup
{
    public class BaseDto
    {
        public List<MineralField> MainMinerals { get; set; }
        public List<VespeneGeyser> MainVespene { get; set; }
        public Point2D MineralCenterOfMS { get; set; }
        public Dictionary<int, string> StartingUnits { get; set; }
        public List<OrderedMineral> OrderedMainMinerals { get; set; }
        public List<Point2D> ExpansionTownhalls { get; set; }
        public bool BaseHasBeenPlayed { get; set; }

        public BaseDto()
        {
            MainMinerals = new List<MineralField>();
            MainVespene = new List<VespeneGeyser>();
            StartingUnits = new Dictionary<int, string>();
            OrderedMainMinerals = new List<OrderedMineral>();
            ExpansionTownhalls = new List<Point2D>();
        }
    }

    public class OrderedMineral
    {
        public int Index { get; set; }
        public bool IsNear { get; set; }
        public Point2D Position { get; set; }
        public double DistanceFromCOM { get; set; }
        public int OriginalIndex { get; set; }

        public OrderedMineral()
        {
            Position = new Point2D();
        }
    }
}
```

<file_package path="src/BabyShark/ColorTeams/GreedyChainColorTeamAssignment.cs">
```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace BabyShark.ColorTeams
{
    public static class GreedyChainColorTeamAssignment
    {
        private static readonly Dictionary<int, (int workerCount, string color8, string color12)> ColorAssignments = new()
        {
            { 0, (2, "Green", 0) },   // M+M -> Green in 8-worker, Teal in 12-worker
            { 2, (2, "Purple", 2) },  // M+M -> Purple in 8-worker, Salmon in 12-worker
            { 4, (2, "Red", 4) },     // M+M -> Red in 8-worker, Blue in 12-worker
            { 6, (2, "Orange", 6) }   // M+M -> Orange in 8-worker, Yellow in 12-worker
        };

        public static void AssignColorTeams(List<OrderedMineral> orderedMinerals, int workerCount)
        {
            var colorIndex = 0;
            
            for (int i = 0; i < orderedMinerals.Count && colorIndex < ColorAssignments.Count; i++)
            {
                var mineral = orderedMinerals[i];
                var assignment = ColorAssignments[colorIndex];
                
                // Determine color based on worker count
                string assignedColor = workerCount == 8 ? assignment.color8 : assignment.color12;
                
                // Apply color to mineral (this would be used by the drawing service)
                mineral.ColorTeam = assignedColor;
                
                colorIndex++;
            }
        }

        public static string GetColorForIndex(int index, int workerCount)
        {
            if (!ColorAssignments.ContainsKey(index))
                return "White"; // Default color

            var assignment = ColorAssignments[index];
            return workerCount == 8 ? assignment.color8 : assignment.color12;
        }
    }
}
```

### CLARIFYING QUESTIONS

1. **Worker Count Determination**: How is the exact number of workers at the start of the game determined? Should we count all workers or only those assigned to mining?

2. **Mineral Validation**: Should we add validation to ensure that mineral fields being targeted are actually accessible and not blocked by terrain or enemy structures?

3. **Error Handling**: In the `TeamPatchMiningTask`, should we implement a retry mechanism if the initial mining command fails due to pathing issues?

4. **Thread Safety**: Are the worker label service operations thread-safe, or do we need to add synchronization for multi-threaded access?
