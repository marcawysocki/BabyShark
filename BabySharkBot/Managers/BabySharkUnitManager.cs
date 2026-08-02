using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SC2APIProtocol;
using Sharky;
using Sharky.Algorithm;
using Sharky.Extensions;
using Sharky.Pathing;
using Vector2 = System.Numerics.Vector2;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Custom unit manager that extends Sharky's default unit management.
    /// Handles unit life-cycle events (deaths, resources lost) and updates unit calculations.
    /// Manages unit target priorities and nearby ally/enemy data structures.
    /// </summary>
    public class BabySharkUnitManager : Sharky.Managers.SharkyManager
    {
        private readonly SharkyUnitData _sharkyUnitData;
        private readonly SharkyOptions _sharkyOptions;
        private readonly TargetPriorityService _targetPriorityService;
        private readonly CollisionCalculator _collisionCalculator;
        private readonly MapDataService _mapDataService;
        private readonly DebugService _debugService;
        private readonly DamageService _damageService;
        private readonly UnitDataService _unitDataService;
        private readonly BaseData _baseData;
        private readonly EnemyData _enemyData;
        private readonly TargetingData _targetingData;
        private readonly ActiveUnitData _activeUnitData;

        private float _nearbyDistance = 30;
        private float _avoidRange = 1.5f;
        private int _targetPriorityCalculationFrame;
        private readonly HashSet<uint> _loggedUnknownUnitTypes = new HashSet<uint>();

        public BabySharkUnitManager(
            ActiveUnitData activeUnitData,
            SharkyUnitData sharkyUnitData,
            BaseData baseData,
            EnemyData enemyData,
            SharkyOptions sharkyOptions,
            TargetPriorityService targetPriorityService,
            CollisionCalculator collisionCalculator,
            MapDataService mapDataService,
            DebugService debugService,
            DamageService damageService,
            UnitDataService unitDataService,
            TargetingData targetingData)
        {
            _activeUnitData = activeUnitData;
            _sharkyUnitData = sharkyUnitData;
            _baseData = baseData;
            _enemyData = enemyData;
            _sharkyOptions = sharkyOptions;
            _targetPriorityService = targetPriorityService;
            _collisionCalculator = collisionCalculator;
            _mapDataService = mapDataService;
            _debugService = debugService;
            _damageService = damageService;
            _unitDataService = unitDataService;
            _targetingData = targetingData;
            _targetPriorityCalculationFrame = 0;
        }

        public override bool NeverSkip => true;

        public override void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            ProcessObservation(observation);
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            ProcessObservation(observation);
            return null;
        }

        public override void OnEnd(ResponseObservation observation, Result result)
        {
            System.Console.WriteLine($"Enemy Deaths: {_activeUnitData.EnemyDeaths}, {_activeUnitData.EnemyResourcesLost} resources lost");
            System.Console.WriteLine($"Self Deaths: {_activeUnitData.SelfDeaths}, {_activeUnitData.SelfResourcesLost} resources lost");
            System.Console.WriteLine($"Neutral Deaths: {_activeUnitData.NeutralDeaths}");
            base.OnEnd(observation, result);
        }

        private void ProcessObservation(ResponseObservation observation)
        {
            if (observation?.Observation?.RawData?.Units == null)
            {
                return;
            }

            var frame = (int)observation.Observation.GameLoop;

            if (observation.Observation.RawData.Event?.DeadUnits != null)
            {
                _activeUnitData.DeadUnits = observation.Observation.RawData.Event.DeadUnits.ToList();
            }
            else
            {
                _activeUnitData.DeadUnits = new List<ulong>();
            }

            foreach (var unit in _activeUnitData.SelfUnits.Where(u => _sharkyUnitData.UndeadTypes.Contains((UnitTypes)u.Value.Unit.UnitType)))
            {
                if (!observation.Observation.RawData.Units.Any(u => u.Tag == unit.Key))
                {
                    _activeUnitData.DeadUnits.Add(unit.Key);
                    _activeUnitData.SelfDeaths--;
                }
            }

            foreach (var unit in _activeUnitData.EnemyUnits.Where(u => _sharkyUnitData.UndeadTypes.Contains((UnitTypes)u.Value.Unit.UnitType)))
            {
                if (!observation.Observation.RawData.Units.Any(u => u.Tag == unit.Key))
                {
                    _activeUnitData.DeadUnits.Add(unit.Key);
                    _activeUnitData.EnemyDeaths--;
                }
            }

            foreach (var unit in _activeUnitData.NeutralUnits.Where(u => _sharkyUnitData.UndeadTypes.Contains((UnitTypes)u.Value.Unit.UnitType)))
            {
                if (!observation.Observation.RawData.Units.Any(u => u.Tag == unit.Key))
                {
                    _activeUnitData.DeadUnits.Add(unit.Key);
                    _activeUnitData.NeutralDeaths--;
                }
            }

            if (_enemyData.SelfRace == Race.Zerg)
            {
                foreach (var unit in _activeUnitData.Commanders.Where(commander => commander.Value.UnitRole == UnitRole.Build && commander.Value.UnitCalculation.Unit.UnitType == (uint)UnitTypes.ZERG_DRONE))
                {
                    if (!observation.Observation.RawData.Units.Any(u => u.Tag == unit.Key))
                    {
                        _activeUnitData.DeadUnits.Add(unit.Key);
                        _activeUnitData.SelfDeaths--;
                    }
                }
            }
            else if (_enemyData.SelfRace == Race.Protoss)
            {
                foreach (var unit in _activeUnitData.Commanders.Where(commander => (commander.Value.UnitCalculation.Unit.UnitType == (uint)UnitTypes.PROTOSS_HIGHTEMPLAR || commander.Value.UnitCalculation.Unit.UnitType == (uint)UnitTypes.PROTOSS_DARKTEMPLAR) && (commander.Value.UnitRole == UnitRole.Morph || commander.Value.UnitCalculation.Unit.Orders.Any(o => o.AbilityId == (uint)Abilities.MORPH_ARCHON || o.AbilityId == (uint)Abilities.MORPH_ARCHON2))))
                {
                    if (!observation.Observation.RawData.Units.Any(u => u.Tag == unit.Key))
                    {
                        _activeUnitData.DeadUnits.Add(unit.Key);
                        _activeUnitData.SelfDeaths--;
                    }
                }

                foreach (var carrier in _activeUnitData.Commanders.Values.Where(c => c.UnitCalculation.Unit.UnitType == (uint)UnitTypes.PROTOSS_CARRIER))
                {
                    carrier.ChildUnitCalculations.RemoveAll(c => !observation.Observation.RawData.Units.Any(u => u.Tag == c.Unit.Tag));
                }
            }
            else if (_enemyData.SelfRace == Race.Terran)
            {
                foreach (var unit in _activeUnitData.Commanders.Where(commander => commander.Value.UnitCalculation.Attributes.Contains(SC2APIProtocol.Attribute.Structure) && commander.Value.UnitCalculation.FrameLastSeen < frame - 100))
                {
                    if (!observation.Observation.RawData.Units.Any(u => u.Tag == unit.Key))
                    {
                        _activeUnitData.DeadUnits.Add(unit.Key);
                        _activeUnitData.SelfDeaths--;
                    }
                }
            }

            foreach (var tag in _activeUnitData.DeadUnits)
            {
                if (_activeUnitData.EnemyUnits.Remove(tag, out UnitCalculation removedEnemy))
                {
                    if (!removedEnemy.Unit.IsHallucination)
                    {
                        if (removedEnemy.Unit.UnitType != (uint)UnitTypes.PROTOSS_PHOENIX || removedEnemy.NearbyAllies.Any(a => a.UnitClassifications.HasFlag(UnitClassification.ArmyUnit) && Vector2.Distance(a.Position, removedEnemy.Position) < 15))
                        {
                            _activeUnitData.EnemyDeaths++;
                            _activeUnitData.EnemyResourcesLost += (int)removedEnemy.UnitTypeData.MineralCost + (int)removedEnemy.UnitTypeData.VespeneCost;
                        }
                    }
                }

                if (_activeUnitData.SelfUnits.Remove(tag, out UnitCalculation removedAlly))
                {
                    if (!removedAlly.Unit.IsHallucination)
                    {
                        _activeUnitData.SelfDeaths++;
                        _activeUnitData.SelfResourcesLost += (int)removedAlly.UnitTypeData.MineralCost + (int)removedAlly.UnitTypeData.VespeneCost;
                    }
                }
                else if (_activeUnitData.NeutralUnits.Remove(tag, out UnitCalculation removedNeutral))
                {
                    _activeUnitData.NeutralDeaths++;
                }

                _activeUnitData.Commanders.Remove(tag, out UnitCommander removedCommander);
            }

            foreach (var unit in _activeUnitData.NeutralUnits.Where(u => u.Value.Unit.DisplayType == DisplayType.Snapshot))
            {
                _activeUnitData.NeutralUnits.Remove(unit.Key, out UnitCalculation removed);
            }

            var repairers = observation.Observation.RawData.Units.Where(u => u.UnitType == (uint)UnitTypes.TERRAN_SCV || u.UnitType == (uint)UnitTypes.TERRAN_MULE);
            var freshEnemyArchon = false;

            foreach (var unit in observation.Observation.RawData.Units)
            {
                if (unit.Alliance == Alliance.Enemy)
                {
                    var repairingUnits = repairers.Where(u => u.Tag != unit.Tag && u.Alliance == Alliance.Enemy && Vector2.DistanceSquared(new Vector2(u.Pos.X, u.Pos.Y), new Vector2(unit.Pos.X, unit.Pos.Y)) < (1.0 + u.Radius + unit.Radius) * (0.1 + u.Radius + unit.Radius));
                    var attack = TryCreateUnitCalculation(unit, repairingUnits.ToList(), _mapDataService.IsOnCreep(unit.Pos), frame);
                    if (attack == null) continue;

                    if (_activeUnitData.EnemyUnits.TryGetValue(unit.Tag, out UnitCalculation existing))
                    {
                        attack.SetPreviousUnit(existing, existing.FrameLastSeen);
                    }
                    else if (unit.UnitType == (uint)UnitTypes.PROTOSS_ARCHON)
                    {
                        freshEnemyArchon = true;
                    }
                    _activeUnitData.EnemyUnits[unit.Tag] = attack;
                }
                else if (unit.Alliance == Alliance.Self)
                {
                    var repairingUnits = repairers.Where(u => u.Alliance == Alliance.Self && Vector2.DistanceSquared(new Vector2(u.Pos.X, u.Pos.Y), new Vector2(unit.Pos.X, unit.Pos.Y)) < (1.0 + u.Radius + unit.Radius) * (0.1 + u.Radius + unit.Radius));
                    var attack = TryCreateUnitCalculation(unit, repairingUnits.ToList(), _mapDataService.IsOnCreep(unit.Pos), frame);
                    if (attack == null) continue;

                    if (_activeUnitData.SelfUnits.TryGetValue(unit.Tag, out UnitCalculation existing))
                    {
                        attack.SetPreviousUnit(existing, existing.FrameLastSeen);
                    }
                    _activeUnitData.SelfUnits[unit.Tag] = attack;
                }
                else if (unit.Alliance == Alliance.Neutral)
                {
                    var attack = TryCreateUnitCalculation(unit, new List<Unit>(), _mapDataService.IsOnCreep(unit.Pos), frame);
                    if (attack == null) continue;

                    if (_activeUnitData.NeutralUnits.TryGetValue(unit.Tag, out UnitCalculation existing))
                    {
                        attack.SetPreviousUnit(existing, existing.FrameLastSeen);
                    }
                    else if (frame > 0 && _sharkyUnitData.MineralFieldTypes.Contains((UnitTypes)attack.Unit.UnitType))
                    {
                        var existingMatch = _activeUnitData.NeutralUnits.FirstOrDefault(m => m.Value.Unit.Pos.X == attack.Unit.Pos.X && m.Value.Unit.Pos.Y == attack.Unit.Pos.Y);
                        if (existingMatch.Value != null)
                        {
                            if (_activeUnitData.NeutralUnits.Remove(existingMatch.Key, out UnitCalculation foo))
                            {
                                foreach (var baseLocation in _baseData.BaseLocations)
                                {
                                    if (baseLocation.MineralFields.RemoveAll(m => m.Pos.X == attack.Unit.Pos.X && m.Pos.Y == attack.Unit.Pos.Y) > 0)
                                    {
                                        baseLocation.MineralFields.Add(unit);
                                    }
                                }
                                foreach (var baseLocation in _baseData.EnemyBaseLocations)
                                {
                                    if (baseLocation.MineralFields.RemoveAll(m => m.Pos.X == attack.Unit.Pos.X && m.Pos.Y == attack.Unit.Pos.Y) > 0)
                                    {
                                        baseLocation.MineralFields.Add(unit);
                                    }
                                }
                            }
                        }
                    }
                    _activeUnitData.NeutralUnits[unit.Tag] = attack;
                }
            }

            foreach (var unit in _activeUnitData.EnemyUnits.Where(u => u.Value.FrameLastSeen != frame && u.Value.UnitTypeData.Attributes.Contains(SC2APIProtocol.Attribute.Structure)))
            {
                _activeUnitData.EnemyUnits.Remove(unit.Key, out UnitCalculation removed);
            }

            var enemyMain = _targetingData.EnemyMainBasePoint ?? new Point2D { X = 0, Y = 0 };
            var enemyMainVisible = _mapDataService.SelfVisible(enemyMain);
            foreach (var enemy in _activeUnitData.EnemyUnits.Values.ToList())
            {
                if (enemy.FrameLastSeen != frame && _mapDataService.SelfVisible(enemy.Unit.Pos))
                {
                    if (_sharkyUnitData.BurrowedUnits.Contains((UnitTypes)enemy.Unit.UnitType) && !_mapDataService.InSelfDetection(enemy.Unit.Pos))
                    {
                        enemy.Unit.DisplayType = DisplayType.Hidden;
                        continue;
                    }
                    if (enemyMainVisible)
                    {
                        _activeUnitData.EnemyUnits.Remove(enemy.Unit.Tag, out UnitCalculation removed);
                    }
                    else
                    {
                    _activeUnitData.EnemyUnits[enemy.Unit.Tag].Position = new Vector2(enemyMain.X, enemyMain.Y);
                        _activeUnitData.EnemyUnits[enemy.Unit.Tag].Unit.Pos = new Point { X = enemyMain.X, Y = enemyMain.Y, Z = 16 };
                    }
                }
                else if (_mapDataService.OutOfBounds(enemy.Unit.Pos))
                {
                    _activeUnitData.EnemyUnits.Remove(enemy.Unit.Tag, out UnitCalculation removed);
                }
                if (freshEnemyArchon && enemy.Unit.UnitType == (uint)UnitTypes.PROTOSS_HIGHTEMPLAR && enemy.FrameLastSeen != frame)
                {
                    _activeUnitData.EnemyUnits.Remove(enemy.Unit.Tag, out UnitCalculation removed);
                }
            }

            foreach (var unit in _activeUnitData.SelfUnits.Where(u => u.Value.FrameLastSeen != frame && u.Value.Unit.UnitType == (uint)UnitTypes.ZERG_DRONE))
            {
                if (unit.Value.Unit.Orders.Any(o => _sharkyUnitData.BuildingData.Values.Any(b => (uint)b.Ability == o.AbilityId)))
                {
                    _activeUnitData.SelfUnits.Remove(unit.Key, out UnitCalculation removed);
                }
            }

            foreach (var allyAttack in _activeUnitData.SelfUnits)
            {
                ClearUnitCalculations(allyAttack);
            }
            foreach (var enemyAttack in _activeUnitData.EnemyUnits)
            {
                ClearUnitCalculations(enemyAttack);
            }

            var enemyUnits = new KDTree2<UnitCalculation>();
            foreach (var enemyAttack in _activeUnitData.EnemyUnits.Values)
            {
                enemyUnits.Add(enemyAttack, enemyAttack.Position);
            }
            enemyUnits.Build();

            var selfUnits = new KDTree2<UnitCalculation>();
            foreach (var allyAttack in _activeUnitData.SelfUnits.Values)
            {
                selfUnits.Add(allyAttack, allyAttack.Position);
            }
            selfUnits.Build();

            foreach (var allyAttack in _activeUnitData.SelfUnits)
            {
                if (allyAttack.Value.FrameLastSeen != frame)
                {
                    continue;
                }

                enemyUnits.ForRange(allyAttack.Value.Position, _nearbyDistance, enemyAttack =>
                {
                    var range = GetRange(allyAttack.Value, enemyAttack);
                    var distanceSquared = Vector2.DistanceSquared(allyAttack.Value.Position, enemyAttack.Position);
                    if (_damageService.CanDamage(allyAttack.Value, enemyAttack) && distanceSquared <= (range + allyAttack.Value.Unit.Radius + enemyAttack.Unit.Radius) * (range + allyAttack.Value.Unit.Radius + enemyAttack.Unit.Radius))
                    {
                        if (allyAttack.Value.Unit.UnitType == (uint)UnitTypes.TERRAN_SIEGETANKSIEGED && distanceSquared < 4)
                        {
                            return;
                        }

                        if (!enemyAttack.Unit.BuffIds.Contains((uint)Buffs.NEURALPARASITE))
                        {
                            allyAttack.Value.EnemiesInRange.Add(enemyAttack);
                        }

                        enemyAttack.EnemiesInRangeOf.Add(allyAttack.Value);
                    }

                    if (_damageService.CanDamage(enemyAttack, allyAttack.Value))
                    {
                        range = GetRange(enemyAttack, allyAttack.Value);
                        if (distanceSquared <= (_avoidRange + range + allyAttack.Value.Unit.Radius + enemyAttack.Unit.Radius) * (_avoidRange + range + allyAttack.Value.Unit.Radius + enemyAttack.Unit.Radius))
                        {
                            if (enemyAttack.Unit.UnitType == (uint)UnitTypes.TERRAN_SIEGETANKSIEGED && distanceSquared < 4)
                            {
                                return;
                            }

                            allyAttack.Value.EnemiesInRangeOfAvoid.Add(enemyAttack);
                            if (distanceSquared <= (range + allyAttack.Value.Unit.Radius + enemyAttack.Unit.Radius) * (range + allyAttack.Value.Unit.Radius + enemyAttack.Unit.Radius))
                            {
                                enemyAttack.EnemiesInRange.Add(allyAttack.Value);
                                allyAttack.Value.EnemiesInRangeOf.Add(enemyAttack);
                            }
                        }
                    }

                    enemyAttack.NearbyEnemies.Add(allyAttack.Value);
                    if (!enemyAttack.Unit.BuffIds.Contains((uint)Buffs.NEURALPARASITE))
                    {
                        allyAttack.Value.NearbyEnemies.Add(enemyAttack);
                    }
                });

                selfUnits.ForRange(allyAttack.Value.Position, _nearbyDistance, u =>
                {
                    if (allyAttack.Key != u.Unit.Tag)
                    {
                        allyAttack.Value.NearbyAllies.Add(u);
                    }
                });

                allyAttack.Value.Loaded = false;

                if (_activeUnitData.Commanders.TryGetValue(allyAttack.Value.Unit.Tag, out var commander))
                {
                    commander.UnitCalculation = allyAttack.Value;
                }
                else
                {
                    commander = new UnitCommander(allyAttack.Value);
                    _activeUnitData.Commanders[allyAttack.Value.Unit.Tag] = commander;
                }

                var parent = GetParentUnitCalculation(_activeUnitData.Commanders[allyAttack.Value.Unit.Tag]);
                _activeUnitData.Commanders[allyAttack.Value.Unit.Tag].ParentUnitCalculation = parent;
                if (parent != null && _activeUnitData.Commanders.ContainsKey(parent.Unit.Tag))
                {
                    _activeUnitData.Commanders[parent.Unit.Tag].ChildUnitCalculation = _activeUnitData.Commanders[allyAttack.Value.Unit.Tag].UnitCalculation;
                    if (!_activeUnitData.Commanders[parent.Unit.Tag].ChildUnitCalculations.Any(c => c.Unit.Tag == allyAttack.Value.Unit.Tag))
                    {
                        _activeUnitData.Commanders[parent.Unit.Tag].ChildUnitCalculations.Add(_activeUnitData.Commanders[allyAttack.Value.Unit.Tag].UnitCalculation);
                    }
                }

                allyAttack.Value.Attackers = GetTargetedAttacks(allyAttack.Value).ToList();
                allyAttack.Value.Targeters = GetTargeters(allyAttack.Value).ToList();
                allyAttack.Value.EnemiesThreateningDamage = GetEnemiesThreateningDamage(allyAttack.Value);

                if (allyAttack.Value.Unit.Passengers != null)
                {
                    var tags = allyAttack.Value.Unit.Passengers.Select(p => p.Tag);
                    foreach (var tag in tags)
                    {
                        if (_activeUnitData.SelfUnits.ContainsKey(tag))
                        {
                            _activeUnitData.SelfUnits[tag].Loaded = true;
                            _activeUnitData.SelfUnits[tag].NearbyAllies = allyAttack.Value.NearbyAllies;
                            _activeUnitData.SelfUnits[tag].NearbyEnemies = allyAttack.Value.NearbyEnemies;
                            _activeUnitData.SelfUnits[tag].Position = allyAttack.Value.Position;
                            var selfAttack = _activeUnitData.SelfUnits[tag];

                            foreach (var enemyAttack in allyAttack.Value.NearbyEnemies)
                            {
                                var range = GetRange(selfAttack, enemyAttack);
                                if (_damageService.CanDamage(selfAttack, enemyAttack) && Vector2.DistanceSquared(selfAttack.Position, enemyAttack.Position) <= (range + selfAttack.Unit.Radius + enemyAttack.Unit.Radius) * (range + selfAttack.Unit.Radius + enemyAttack.Unit.Radius))
                                {
                                    if (selfAttack.Unit.UnitType == (uint)UnitTypes.TERRAN_SIEGETANKSIEGED && Vector2.DistanceSquared(selfAttack.Position, enemyAttack.Position) < 4)
                                    {
                                        continue;
                                    }
                                    selfAttack.EnemiesInRange.Add(enemyAttack);
                                }
                                if (_damageService.CanDamage(enemyAttack, selfAttack))
                                {
                                    range = GetRange(enemyAttack, selfAttack);
                                    var distanceSquared = Vector2.DistanceSquared(selfAttack.Position, enemyAttack.Position);
                                    if (distanceSquared <= (_avoidRange + range + selfAttack.Unit.Radius + enemyAttack.Unit.Radius) * (_avoidRange + range + selfAttack.Unit.Radius + enemyAttack.Unit.Radius))
                                    {
                                        if (selfAttack.Unit.UnitType == (uint)UnitTypes.TERRAN_SIEGETANKSIEGED && distanceSquared < 4)
                                        {
                                            continue;
                                        }
                                        selfAttack.EnemiesInRangeOfAvoid.Add(enemyAttack);
                                        if (distanceSquared <= (range + selfAttack.Unit.Radius + enemyAttack.Unit.Radius) * (range + selfAttack.Unit.Radius + enemyAttack.Unit.Radius))
                                        {
                                            selfAttack.EnemiesInRangeOf.Add(enemyAttack);
                                        }
                                    }
                                }
                            }

                            _activeUnitData.SelfUnits[tag].EnemiesThreateningDamage = GetEnemiesThreateningDamage(_activeUnitData.SelfUnits[tag]);

                            if (_activeUnitData.SelfUnits[tag].Unit.Shield < _activeUnitData.SelfUnits[tag].Unit.ShieldMax)
                            {
                                var timeLoaded = frame - _activeUnitData.SelfUnits[tag].FrameLastSeen;
                                var regenFrames = timeLoaded - (_sharkyOptions.FramesPerSecond * 7);
                                var shieldRegenerated = regenFrames / (_sharkyOptions.FramesPerSecond / 2f);
                                if (shieldRegenerated > _activeUnitData.SelfUnits[tag].Unit.ShieldMax - _activeUnitData.SelfUnits[tag].Unit.Shield)
                                {
                                    _activeUnitData.SelfUnits[tag].Unit.Shield = _activeUnitData.SelfUnits[tag].Unit.ShieldMax;
                                }
                            }
                        }
                    }
                }
            }

            foreach (var enemyAttack in _activeUnitData.EnemyUnits)
            {
                enemyUnits.ForRange(enemyAttack.Value.Position, _nearbyDistance, u =>
                {
                    if (enemyAttack.Key != u.Unit.Tag)
                    {
                        enemyAttack.Value.NearbyAllies.Add(u);
                    }
                });

                if (enemyAttack.Value.FrameFirstSeen == frame && enemyAttack.Value.PreviousUnitCalculation == null)
                {
                    if (enemyAttack.Value.Unit.UnitType == (uint)UnitTypes.PROTOSS_COLOSSUS)
                    {
                        var sentry = enemyAttack.Value.NearbyAllies.FirstOrDefault(e => e.Unit.UnitType == (uint)UnitTypes.PROTOSS_SENTRY && e.PreviousUnit != null && e.PreviousUnit.Energy - 73 > e.Unit.Energy);
                        if (sentry != null)
                        {
                            enemyAttack.Value.Unit.IsHallucination = true;
                        }
                    }
                    else if (enemyAttack.Value.Unit.UnitType == (uint)UnitTypes.PROTOSS_ARCHON)
                    {
                        var sentry = enemyAttack.Value.NearbyAllies.FirstOrDefault(e => e.Unit.UnitType == (uint)UnitTypes.PROTOSS_SENTRY && e.PreviousUnit != null && e.PreviousUnit.Energy - 73 > e.Unit.Energy);
                        if (sentry != null)
                        {
                            enemyAttack.Value.Unit.IsHallucination = true;
                        }
                    }
                }
            }

            if (_targetPriorityCalculationFrame + 10 < frame)
            {
                foreach (var selfUnit in _activeUnitData.SelfUnits)
                {
                    if (selfUnit.Value.TargetPriorityCalculation == null || selfUnit.Value.TargetPriorityCalculation.FrameCalculated + 10 < frame)
                    {
                        var priorityCalculation = _targetPriorityService.CalculateTargetPriority(selfUnit.Value, frame);
                        selfUnit.Value.TargetPriorityCalculation = priorityCalculation;
                        foreach (var nearbyUnit in selfUnit.Value.NearbyAllies.Where(a => a.NearbyEnemies.Count == selfUnit.Value.NearbyAllies.Count))
                        {
                            nearbyUnit.TargetPriorityCalculation = priorityCalculation;
                        }
                    }
                }
                _targetPriorityCalculationFrame = frame;
            }

            if (_sharkyOptions.Debug)
            {
                foreach (var selfUnit in _activeUnitData.SelfUnits)
                {
                    _debugService.DrawLine(selfUnit.Value.Unit.Pos, new Point { X = selfUnit.Value.End.X, Y = selfUnit.Value.End.Y, Z = selfUnit.Value.Unit.Pos.Z + 1f }, new SC2APIProtocol.Color { R = 0, B = 0, G = 255 });
                }

                foreach (var enemyUnit in _activeUnitData.EnemyUnits)
                {
                    _debugService.DrawLine(enemyUnit.Value.Unit.Pos, new Point { X = enemyUnit.Value.End.X, Y = enemyUnit.Value.End.Y, Z = enemyUnit.Value.Unit.Pos.Z + 1f }, new SC2APIProtocol.Color { R = 255, B = 0, G = 0 });
                }
            }
        }

        private void ClearUnitCalculations(KeyValuePair<ulong, UnitCalculation> attack)
        {
            attack.Value.NearbyAllies.Clear();
            attack.Value.NearbyEnemies.Clear();
            attack.Value.EnemiesInRange.Clear();
            attack.Value.EnemiesInRangeOf.Clear();
            attack.Value.EnemiesInRangeOfAvoid.Clear();
            attack.Value.EnemiesThreateningDamage.Clear();
            attack.Value.Attackers.Clear();
            attack.Value.Targeters.Clear();
        }

        private UnitCalculation? TryCreateUnitCalculation(Unit unit, List<Unit> repairers, bool isOnCreep, int frame)
        {
            try
            {
                return new UnitCalculation(unit, repairers, _sharkyUnitData, _sharkyOptions, _unitDataService, isOnCreep, frame);
            }
            catch (KeyNotFoundException ex)
            {
                if (_loggedUnknownUnitTypes.Add(unit.UnitType))
                {
                    Console.WriteLine($"[BabySharkUnitManager] Unknown unit type {unit.UnitType} (tag {unit.Tag}, alliance {unit.Alliance}). Skipping. {ex.Message}");
                }
                return null;
            }
            catch (NullReferenceException ex)
            {
                if (_loggedUnknownUnitTypes.Add(unit.UnitType))
                {
                    Console.WriteLine($"[BabySharkUnitManager] NRE for unit type {unit.UnitType} (tag {unit.Tag}). Skipping. {ex.Message}");
                }
                return null;
            }
        }

        private float GetRange(UnitCalculation allyAttack, UnitCalculation enemyAttack)
        {
            var range = allyAttack.Range;

            if (allyAttack.Weapons.Any())
            {
                var weapons = allyAttack.Weapons;
                var unit = enemyAttack.Unit;
                Weapon weapon;
                if (unit.IsFlying || unit.UnitType == (uint)UnitTypes.PROTOSS_COLOSSUS || unit.BuffIds.Contains((uint)Buffs.GRAVITONBEAM))
                {
                    weapon = weapons.FirstOrDefault(w => w.Type == Weapon.Types.TargetType.Air || w.Type == Weapon.Types.TargetType.Any);
                }
                else
                {
                    weapon = weapons.FirstOrDefault(w => w.Type == Weapon.Types.TargetType.Ground || w.Type == Weapon.Types.TargetType.Any);
                }

                if (weapon != null)
                {
                    return weapon.Range;
                }
            }

            return range;
        }

        private float GetRange(KeyValuePair<ulong, UnitCalculation> allyAttack, KeyValuePair<ulong, UnitCalculation> enemyAttack)
        {
            return GetRange(allyAttack.Value, enemyAttack.Value);
        }

        private List<UnitCalculation> GetTargetedAttacks(UnitCalculation unitCalculation)
        {
            var attacks = new List<UnitCalculation>();

            foreach (var enemyAttack in unitCalculation.EnemiesInRangeOfAvoid)
            {
                if (_damageService.CanDamage(enemyAttack, unitCalculation)
                    && _collisionCalculator.Collides(unitCalculation.Position, unitCalculation.Unit.Radius, enemyAttack.Start, enemyAttack.End))
                {
                    attacks.Add(enemyAttack);
                }
            }

            return attacks;
        }

        private List<UnitCalculation> GetTargeters(UnitCalculation unitCalculation)
        {
            var attacks = new List<UnitCalculation>();

            foreach (var enemyAttack in unitCalculation.NearbyEnemies)
            {
                if (_damageService.CanDamage(enemyAttack, unitCalculation)
                    && _collisionCalculator.Collides(unitCalculation.Position, unitCalculation.Unit.Radius, enemyAttack.Start, enemyAttack.EndPlusFive))
                {
                    attacks.Add(enemyAttack);
                }
            }

            return attacks;
        }

        private List<UnitCalculation> GetEnemiesThreateningDamage(UnitCalculation unitCalculation)
        {
            var attacks = new List<UnitCalculation>();

            foreach (var enemyAttack in unitCalculation.NearbyEnemies)
            {
                if (_damageService.CanDamage(enemyAttack, unitCalculation))
                {
                    var fireTime = 0.25f;
                    var weapon = unitCalculation.UnitTypeData.Weapons.FirstOrDefault();
                    if (weapon != null && weapon.HasSpeed)
                    {
                        fireTime = weapon.Speed / 10f;
                    }
                    if (unitCalculation.Unit.UnitType == (uint)UnitTypes.PROTOSS_PROBE)
                    {
                        fireTime = .5f;
                    }
                    var distance = Vector2.Distance(unitCalculation.Position, enemyAttack.Position);
                    if (enemyAttack.Unit.UnitType == (uint)UnitTypes.TERRAN_SIEGETANKSIEGED && distance < 2)
                    {
                        continue;
                    }
                    var avoidDistance = _avoidRange + enemyAttack.Range + unitCalculation.Unit.Radius + enemyAttack.Unit.Radius;
                    var distanceToInRange = distance - avoidDistance;
                    var timeToGetInRange = distanceToInRange / unitCalculation.UnitTypeData.MovementSpeed;
                    if (timeToGetInRange < fireTime || (enemyAttack.Unit.UnitType == (uint)UnitTypes.TERRAN_BATTLECRUISER && Vector2.DistanceSquared(enemyAttack.Position, unitCalculation.Position) < 100))
                    {
                        attacks.Add(enemyAttack);
                    }
                }
            }

            return attacks;
        }

        private UnitCalculation GetParentUnitCalculation(UnitCommander commander)
        {
            if (commander.ParentUnitCalculation != null)
            {
                if (_activeUnitData.Commanders.ContainsKey(commander.ParentUnitCalculation.Unit.Tag))
                {
                    return _activeUnitData.Commanders[commander.ParentUnitCalculation.Unit.Tag].UnitCalculation;
                }
            }

            if (commander.UnitCalculation.Unit.UnitType == (uint)UnitTypes.PROTOSS_ADEPTPHASESHIFT)
            {
                var closestAdept = commander.UnitCalculation.NearbyAllies.Where(a => a.Unit.UnitType == (uint)UnitTypes.PROTOSS_ADEPT).OrderBy(a => Vector2.DistanceSquared(a.Position, commander.UnitCalculation.Position)).FirstOrDefault();
                if (closestAdept != null)
                {
                    return closestAdept;
                }
            }

            if (commander.UnitCalculation.Unit.UnitType == (uint)UnitTypes.PROTOSS_DISRUPTORPHASED)
            {
                var closestDisruptor = commander.UnitCalculation.NearbyAllies.Where(a => a.Unit.UnitType == (uint)UnitTypes.PROTOSS_DISRUPTOR).OrderBy(a => Vector2.DistanceSquared(a.Position, commander.UnitCalculation.Position)).FirstOrDefault();
                if (closestDisruptor != null)
                {
                    return closestDisruptor;
                }
            }

            if (commander.UnitCalculation.Unit.UnitType == (uint)UnitTypes.PROTOSS_INTERCEPTOR)
            {
                foreach (var carrier in commander.UnitCalculation.NearbyAllies.Where(a => a.Unit.UnitType == (uint)UnitTypes.PROTOSS_CARRIER))
                {
                    if (_activeUnitData.Commanders.ContainsKey(carrier.Unit.Tag))
                    {
                        if (_activeUnitData.Commanders[carrier.Unit.Tag].ChildUnitCalculations.Any(c => c.Unit.Tag == commander.UnitCalculation.Unit.Tag))
                        {
                            return carrier;
                        }
                    }
                }

                var closestCarrier = commander.UnitCalculation.NearbyAllies.Where(a => a.Unit.UnitType == (uint)UnitTypes.PROTOSS_CARRIER && _activeUnitData.Commanders[a.Unit.Tag].ChildUnitCalculations.Count() < 8).OrderBy(a => Vector2.DistanceSquared(a.Position, commander.UnitCalculation.Position)).FirstOrDefault();
                if (closestCarrier != null)
                {
                    return closestCarrier;
                }
            }

            return null;
        }
    }
}
