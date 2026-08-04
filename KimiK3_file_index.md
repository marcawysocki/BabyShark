### FILE INDEX

<file_package path="src/BabyShark/Workers/Services/WorkerLabelService.cs">
using System;
using System.Collections.Generic;
using System.Threading;

namespace BabyShark.Workers.Services
{
    public class WorkerLabelService
    {
        private readonly Dictionary<int, string> workerLabels = new Dictionary<int, string>();
        private int nextLabelIndex = 1;
        private readonly object _lock = new object();

        public void SetLabel(int unitTag, string label)
        {
            lock (_lock)
            {
                workerLabels[unitTag] = label;
            }
        }

        public string GetLabel(int unitTag)
        {
            lock (_lock)
            {
                workerLabels.TryGetValue(unitTag, out var label);
                return label;
            }
        }

        public int? GetWorker(string label)
        {
            lock (_lock)
            {
                foreach (var kvp in workerLabels)
                {
                    if (kvp.Value == label)
                        return kvp.Key;
                }
                return null;
            }
        }

        public string CreateLabel()
        {
            lock (_lock)
            {
                var label = $"W{nextLabelIndex:D2}";
                workerLabels[nextLabelIndex] = label;
                nextLabelIndex++;
                return label;
            }
        }
    }
}
</file_package>

<file_package path="src/BabyShark/ColorTeams/GreedyChainColorTeamAssignment.cs">
using System.Collections.Generic;
using System.Linq;
using StarCraft2APIProtocol;

namespace BabyShark.ColorTeams
{
    public class GreedyChainColorTeamAssignment
    {
        private readonly Dictionary<int, (int priority, string color, int team)> assignmentOrder = new()
        {
            { 0, (2, "Green", 0) },   // M+M -> Green in 8-worker, Teal in 12-worker
            { 2, (2, "Purple", 2) },  // M+M -> Purple in 8-worker, Salmon in 12-worker  
            { 4, (2, "Red", 4) },     // M+M -> Red in 8-worker, Blue in 12-worker
            { 6, (2, "Orange", 6) }   // M+M -> Orange in 8-worker, Yellow in 12-worker
        };

        public List<(int mineralIndex, string colorTeam)> AssignColorTeams(List<MineralField> minerals)
        {
            var assignments = new List<(int, string)>();
            
            for (int i = 0; i < minerals.Count && i < assignmentOrder.Count; i++)
            {
                var (priority, color, team) = assignmentOrder[i];
                assignments.Add((i, color));
            }

            return assignments;
        }
    }
}
</file_package>

<file_package path="BabySharkBot/Managers/BabySharkMiningManager.cs">
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Sharky;
using Sharky.Managers;
using Sharky.Pathing;
using Sharky.Decisions.Moves;
using Sharky.DefaultBot;

public partial class BabySharkMiningManager : IManager
{
    private readonly InitialMapData initialMapData;
    private readonly SecondaryMapData secondaryMapData;
    private readonly OngoingMapData ongoingMapData;
    private readonly WorkerLabelService workerLabelService;
    private readonly CrosshairService crosshairService;
    private readonly MineralLabelService mineralLabelService;
    private readonly VespeneLabelService vespeneLabelService;
    private readonly ExpansionCOMService expansionCOMService;
    private readonly ExpansionPointService expansionPointService;
    private readonly ExpansionPointDrawService expansionPointDrawService;
    private readonly ProvisionalExpansionService provisionalExpansionService;
    private readonly MapDataService mapDataService;

    public BabySharkMiningManager(
        InitialMapData initialMapData,
        SecondaryMapData secondaryMapData,
        OngoingMapData ongoingMapData,
        WorkerLabelService workerLabelService,
        CrosshairService crosshairService,
        MineralLabelService mineralLabelService,
        VespeneLabelService vespeneLabelService,
        ExpansionCOMService expansionCOMService,
        ExpansionPointService expansionPointService,
        ExpansionPointDrawService expansionPointDrawService,
        ProvisionalExpansionService provisionalExpansionService,
        MapDataService mapDataService)
    {
        this.initialMapData = initialMapData;
        this.secondaryMapData = secondaryMapData;
        this.ongoingMapData = ongoingMapData;
        this.workerLabelService = workerLabelService;
        this.crosshairService = crosshairService;
        this.mineralLabelService = mineralLabelService;
        this.vespeneLabelService = vespeneLabelService;
        this.expansionCOMService = expansionCOMService;
        this.expansionPointService = expansionPointService;
        this.expansionPointDrawService = expansionPointDrawService;
        this.provisionalExpansionService = provisionalExpansionService;
        this.mapDataService = mapDataService;
    }

    public void OnFrame()
    {
        if (!Settings.Instance.CcaMining)
        {
            return;
        }

        var commanders = Managers.OfType<CommanderManager>().ToList();
        
        foreach (var commander in commanders)
        {
            if (commander.UnitCalculation.Unit.Value.Unit.IsCompletedUnitState() && 
                !commander.UnitCalculation.Unit.Value.Unit.IsFlying && 
                commander.UnitCalculation.Unit.Value.Unit.Health > 0)
            {
                var target = DetermineMiningTarget(commander);
                if (target != null)
                {
                    commander.Order(target);
                }
            }
        }
    }

    private Point2D DetermineMiningTarget(CommanderManager commander)
    {
        var unit = commander.UnitCalculation.Unit.Value;
        
        if (!ongoingMapData.ResolvedAssignments.ContainsKey(unit))
        {
            return null;
        }

        var assignment = ongoingMapData.ResolvedAssignments[unit];
        
        if (assignment.IsNear)
        {
            return new Point2D { X = assignment.X, Y = assignment.Y };
        }

        return FindFurthestAvailableMineral(commander);
    }

    private Point2D FindFurthestAvailableMineral(CommanderManager commander)
    {
        var availableMinerals = initialMapData.MainMinerals
            .Where(m => !ongoingMapData.ResolvedAssignments.Values.Any(a => a.X == m.Pos.X && a.Y == m.Pos.Y))
            .ToList();

        if (!availableMinerals.Any())
        {
            return null;
        }

        var furthest = availableMinerals.OrderByDescending(m => 
            Math.Sqrt(Math.Pow(m.Pos.X - commander.UnitCalculation.Position.X, 2) + 
                     Math.Pow(m.Pos.Y - commander.UnitCalculation.Position.Y, 2))).First();

        return new Point2D { X = furthest.Pos.X, Y = furthest.Pos.Y };
    }
}
</file_package>

<file_package path="BabySharkBot/Setup/OngoingMapData.cs">
using System;
using System.Collections.Generic;
using System.Linq;
using Sharky.Decisions.Moves;
using StarCraft2APIProtocol;

public class OngoingMapData
{
    public Dictionary<ulong, MineralAssignment> ResolvedAssignments { get; set; } = new();
    
    public void ResolveTeamAssignments(List<MineralField> minerals, List<UnitCalculation> workers)
    {
        var sortedWorkers = workers.OrderBy(w => w.Position.X).ThenBy(w => w.Position.Y).ToList();
        
        if (sortedWorkers.Count == 0) return;

        var centerOfMass = CalculateCenterOfMass(minerals);
        var furthestWorker = sortedWorkers.First();
        var anchorWorker = GetAnchorWorker(sortedWorkers, centerOfMass);

        // Assign W1 to furthest worker
        ResolvedAssignments[furthestWorker.Unit.Tag] = new MineralAssignment
        {
            X = centerOfMass.X,
            Y = centerOfMass.Y,
            IsNear = true,
            OriginalIndex = -1
        };

        // Greedy chain assignment for remaining workers
        var remainingMinerals = minerals.OrderBy(m => 
            Math.Sqrt(Math.Pow(m.Pos.X - anchorWorker.Position.X, 2) + 
                     Math.Pow(m.Pos.Y - anchorWorker.Position.Y, 2))).ToList();

        for (int i = 0; i < sortedWorkers.Count - 1 && i < remainingMinerals.Count; i++)
        {
            var worker = sortedWorkers[i + 1];
            var mineral = remainingMinerals[i];
            
            ResolvedAssignments[worker.Unit.Tag] = new MineralAssignment
            {
                X = mineral.Pos.X,
                Y = mineral.Pos.Y,
                IsNear = true,
                OriginalIndex = minerals.IndexOf(mineral)
            };
        }
    }

    private Point2D CalculateCenterOfMass(List<MineralField> minerals)
    {
        var centerX = minerals.Average(m => m.Pos.X);
        var centerY = minerals.Average(m => m.Pos.Y);
        return new Point2D { X = (float)centerX, Y = (float)centerY };
    }

    private UnitCalculation GetAnchorWorker(List<UnitCalculation> workers, Point2D com)
    {
        return workers.OrderBy(w => 
            Math.Sqrt(Math.Pow(w.Position.X - com.X, 2) + 
                     Math.Pow(w.Position.Y - com.Y, 2))).First();
    }
}

public class MineralAssignment
{
    public float X { get; set; }
    public float Y { get; set; }
    public bool IsNear { get; set; }
    public int OriginalIndex { get; set; }
}
</file_package>

<file_package path="BabySharkBot/Setup/InitialMapData.cs">
using System;
using System.Collections.Generic;
using System.Linq;
using Sharky.Decisions.Moves;
using StarCraft2APIProtocol;

public static class InitialMapData
{
    public static BaseDto GetNewMiningData(ResponseGameInfo gameInfo, ResponseData data, 
        ResponseObservation observation, MapState mapState)
    {
        var baseDto = new BaseDto();
        
        // Single pass unit scan
        var minerals = new List<MineralField>();
        var vespeneGeysers = new List<VespeneGeyser>();
        var workers = new List<UnitCalculation>();

        foreach (var unit in observation.Observation.RawData.Units)
        {
            if (unit.Value.Unit.IsMineralField())
            {
                minerals.Add(new MineralField
                {
                    Pos = new Point2D { X = unit.Value.Pos.X, Y = unit.Value.Pos.Y },
                    Owner = unit.Value.Owner,
                    IsNeutral = unit.Value.Unit.IsNeutral()
                });
            }
            else if (unit.Value.Unit.IsVespeneGeyser())
            {
                vespeneGeysers.Add(new VespeneGeyser
                {
                    Pos = new Point2D { X = unit.Value.Pos.X, Y = unit.Value.Pos.Y },
                    Owner = unit.Value.Owner
                });
            }
            else if (unit.Value.Unit.IsWorker() && unit.Value.Owner == gameInfo.PlayerId)
            {
                workers.Add(new UnitCalculation
                {
                    Unit = unit.Value,
                    Position = new Point2D { X = unit.Value.Pos.X, Y = unit.Value.Pos.Y }
                });
            }
        }

        // Assign worker labels
        var labelService = ServiceLocator.Get<WorkerLabelService>();
        foreach (var worker in workers)
        {
            if (!labelService.GetLabel(worker.Unit.Tag).HasValue)
            {
                labelService.SetLabel(worker.Unit.Tag, labelService.CreateLabel());
            }
        }

        // Calculate center of mass and identify W1
        var com = CalculateCenterOfMass(minerals);
        var furthestWorker = workers.OrderByDescending(w => 
            Math.Sqrt(Math.Pow(w.Position.X - com.X, 2) + 
                     Math.Pow(w.Position.Y - com.Y, 2))).First();

        // Greedy chain assignment
        var orderedMinerals = OrderMineralsForGreedyChain(minerals, furthestWorker.Position);
        
        baseDto.MainMinerals = minerals;
        baseDto.MainVespene = vespeneGeysers;
        baseDto.MineralCenterOfMass = com;
        baseDto.StartingUnits = workers;
        baseDto.OrderedMainMinerals = orderedMinerals;
        baseDto.BaseHasBeenPlayed = true;

        return baseDto;
    }

    private static List<MineralField> OrderMineralsForGreedyChain(List<MineralField> minerals, Point2D w1Pos)
    {
        var sorted = minerals.OrderBy(m => 
            Math.Sqrt(Math.Pow(m.Pos.X - w1Pos.X, 2) + 
                     Math.Pow(m.Pos.Y - w1Pos.Y, 2))).ToList();

        return sorted.Take(8).ToList();
    }

    private static Point2D CalculateCenterOfMass(List<MineralField> minerals)
    {
        var centerX = minerals.Average(m => m.Pos.X);
        var centerY = minerals.Average(m => m.Pos.Y);
        return new Point2D { X = (float)centerX, Y = (float)centerY };
    }
}
</file_package>
