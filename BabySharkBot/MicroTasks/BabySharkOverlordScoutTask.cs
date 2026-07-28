using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using Sharky.MicroTasks;
using BabySharkBot.Services;
using BabySharkBot.Setup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BabySharkBot.MicroTasks
{
    public class BabySharkOverlordScoutTask : MicroTask
    {
        private readonly DefaultSharkyBot _defaultSharkyBot;
        private readonly ProvisionalExpansionService _provisionalExpansionService;
        private readonly ExpansionCOMService _expansionCOMService;
        private readonly HashSet<ulong> _scoutedTargets = new HashSet<ulong>();
        private readonly HashSet<string> _completedScoutPoints = new HashSet<string>();
        private readonly Dictionary<ulong, Point2D> _assignedTargets = new Dictionary<ulong, Point2D>();

        public BabySharkOverlordScoutTask(DefaultSharkyBot defaultSharkyBot, bool enabled, float priority, ProvisionalExpansionService provisionalExpansionService, ExpansionCOMService expansionCOMService)
        {
            _defaultSharkyBot = defaultSharkyBot;
            _provisionalExpansionService = provisionalExpansionService;
            _expansionCOMService = expansionCOMService;
            Priority = priority;
            Enabled = enabled;
            UnitCommanders = new List<UnitCommander>();
            CommanderDebugText = "BabyShark Overlord Scouting";
            CommanderDebugColor = new Color { R = 255, G = 255, B = 127 };
        }

        public override void ClaimUnits(Dictionary<ulong, UnitCommander> commanders)
        {
            if (_defaultSharkyBot.EnemyData.SelfRace != Race.Zerg)
            {
                return;
            }

            UnitCommanders.RemoveAll(commander => commander.UnitCalculation.Unit.UnitType != (int)UnitTypes.ZERG_OVERLORD);

            if (UnitCommanders.Count > 0)
            {
                return;
            }

            foreach (var commander in commanders.Values)
            {
                if (!commander.Claimed && commander.UnitCalculation.Unit.UnitType == (int)UnitTypes.ZERG_OVERLORD)
                {
                    commander.Claimed = true;
                    commander.UnitRole = UnitRole.Scout;
                    UnitCommanders.Add(commander);
                    return;
                }
            }
        }

        public override IEnumerable<SC2APIProtocol.Action> PerformActions(int frame)
        {
            var commands = new List<SC2APIProtocol.Action>();

            if (_defaultSharkyBot.EnemyData.SelfRace != Race.Zerg || _provisionalExpansionService == null)
            {
                return commands;
            }

            var scoutTargets = GetScoutTargets();
            if (scoutTargets.Count == 0)
            {
                return commands;
            }

            var assignedPointValues = _assignedTargets.Values.ToList();

            foreach (var commander in UnitCommanders)
            {
                if (!_assignedTargets.TryGetValue(commander.UnitCalculation.Unit.Tag, out var assignedPoint) || _scoutedTargets.Contains(commander.UnitCalculation.Unit.Tag))
                {
                    assignedPoint = GetNextPoint(commander, scoutTargets, assignedPointValues);
                    if (assignedPoint == null)
                    {
                        continue;
                    }

                    _assignedTargets[commander.UnitCalculation.Unit.Tag] = assignedPoint;
                    assignedPointValues.Add(assignedPoint);
                }

                if (HasReachedPoint(commander, assignedPoint))
                {
                    _scoutedTargets.Add(commander.UnitCalculation.Unit.Tag);
                    _completedScoutPoints.Add(GetPointKey(assignedPoint));
                    _assignedTargets.Remove(commander.UnitCalculation.Unit.Tag);
                    _provisionalExpansionService.MarkScoutComplete(assignedPoint);
                    continue;
                }

                var action = commander.Order(frame, Abilities.MOVE, assignedPoint);
                if (action != null)
                {
                    commands.AddRange(action);
                }
            }

            return commands;
        }

        private List<Point2D> GetScoutTargets()
        {
            if (_provisionalExpansionService == null && _expansionCOMService == null)
            {
                return new List<Point2D>();
            }

            var targets = new List<Point2D>();

            if (_provisionalExpansionService != null)
            {
                targets.AddRange(_provisionalExpansionService.GetProvisionalScoutPoints().Values);
            }

            if (_expansionCOMService != null)
            {
                targets.AddRange(_expansionCOMService.Get()
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => new Point2D { X = kvp.Value.X, Y = kvp.Value.Y }));
            }

            return targets
                .Where(point => point != null)
                .Where(point => !_completedScoutPoints.Contains(GetPointKey(point)))
                .GroupBy(point => GetPointKey(point))
                .Select(group => group.First())
                .ToList();
        }

        private Point2D GetNextPoint(UnitCommander commander, IEnumerable<Point2D> availablePoints, ICollection<Point2D> alreadyAssigned)
        {
            return availablePoints
                .Where(point => point != null)
                .Where(point => !IsPointUsed(point, alreadyAssigned))
                .FirstOrDefault();
        }

        private bool IsPointUsed(Point2D point, ICollection<Point2D> alreadyAssigned)
        {
            return alreadyAssigned.Any(assigned => assigned != null && Math.Abs(assigned.X - point.X) < 0.01f && Math.Abs(assigned.Y - point.Y) < 0.01f);
        }

        private string GetPointKey(Point2D point)
        {
            return $"{point.X:F2},{point.Y:F2}";
        }

        private bool HasReachedPoint(UnitCommander commander, Point2D point)
        {
            if (point == null)
            {
                return false;
            }

            return Vector2.DistanceSquared(commander.UnitCalculation.Position, new Vector2(point.X, point.Y)) < 0.25f;
        }

    }
}
