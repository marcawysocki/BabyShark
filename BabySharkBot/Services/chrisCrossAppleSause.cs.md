using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using BabySharkBot.Setup;

namespace BabySharkBot.Services
{
    public sealed class chrisCrossAppleSause
    {
        private readonly Dictionary<string, CcaSpawnLearningState> _states = new Dictionary<string, CcaSpawnLearningState>(StringComparer.OrdinalIgnoreCase);

        public CcaSpawnLearningState GetOrCreateCurrentSpawnState(MawBaseLocationData mapData, int startIndex)
        {
            var key = BuildSpawnKey(mapData, startIndex);
            if (!_states.TryGetValue(key, out var state))
            {
                state = new CcaSpawnLearningState
                {
                    SpawnKey = key,
                    StartIndex = startIndex,
                    TealM1IsFar = startIndex >= 0 && mapData?.TealM1IsFar != null && startIndex < mapData.TealM1IsFar.Length && mapData.TealM1IsFar[startIndex],
                    YellowM8IsFar = startIndex >= 0 && mapData?.YellowM8IsFar != null && startIndex < mapData.YellowM8IsFar.Length && mapData.YellowM8IsFar[startIndex]
                };

                _states[key] = state;
            }

            return state;
        }

        public void RecordSpawnObservation(MawBaseLocationData mapData, int startIndex, List<List<TeamPatchAssignmentDto>> teamAssignmentsByStart)
        {
            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);
            state.TeamAssignments = teamAssignmentsByStart != null && startIndex >= 0 && teamAssignmentsByStart.Count > startIndex && teamAssignmentsByStart[startIndex] != null
                ? teamAssignmentsByStart[startIndex].Select(CloneTeamAssignment).ToList()
                : new List<TeamPatchAssignmentDto>();
        }

        public void EnableCcaMiningForCurrentSpawn(MawBaseLocationData mapData, int startIndex)
        {
            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);
            state.CcaMining = true;
            Settings.ccaMining = true;
        }

        public IEnumerable<SC2APIProtocol.Action> BuildBumpOrders(int frame, MawBaseLocationData mapData, int startIndex, IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            Debugger.Break();
            var commands = new List<SC2APIProtocol.Action>();
            if (!Settings.ccaMining || mapData == null || workerEntries == null || workerEntries.Count == 0)
            {
                return commands;
            }

            if (frame % 5 != 0)
            {
                return commands;
            }

            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);

            foreach (var team in state.TeamAssignments ?? new List<TeamPatchAssignmentDto>())
            {
                if (team?.Workers == null || team.Workers.Count == 0 || team.Minerals == null || team.Minerals.Count < 2)
                {
                    continue;
                }

                var aMineral = team.Minerals.FirstOrDefault(m => m != null && m.IsNear) ?? team.Minerals[0];
                var bMineral = team.Minerals.FirstOrDefault(m => m != null && !m.IsNear && m != aMineral) ?? team.Minerals.Skip(1).FirstOrDefault() ?? aMineral;
                if (aMineral?.Position == null || bMineral?.Position == null)
                {
                    continue;
                }

                var bumpEnabled = IsBumpEnabled(team.TeamNumber);
                var queuedMoves = false;
                if (bumpEnabled && frame < 15)
                {
                    Debugger.Break();

                    var primaryWorker = ResolveWorker(team.Workers, workerEntries, 1);
                    var secondaryWorker = ResolveWorker(team.Workers, workerEntries, 2);
                    if (primaryWorker == null || secondaryWorker == null)
                    {
                        continue;
                    }

                    var pairDistance = Distance(primaryWorker.Position, secondaryWorker.Position);
                    if (pairDistance > 0.75f)
                    {
                        bumpEnabled = false;
                    }
                    else
                    {
                        var bumpBeginPoint = new Point2D
                        {
                            X = (primaryWorker.Position.X + secondaryWorker.Position.X) * 0.5f,
                            Y = (primaryWorker.Position.Y + secondaryWorker.Position.Y) * 0.5f
                        };

                        var primaryTargetPoint = new Point2D
                        {
                            X = (bumpBeginPoint.X + aMineral.Position.X) * 0.5f,
                            Y = (bumpBeginPoint.Y + aMineral.Position.Y) * 0.5f
                        };

                        var secondaryTargetPoint = new Point2D
                        {
                            X = (bumpBeginPoint.X + primaryTargetPoint.X) * 0.5f,
                            Y = (bumpBeginPoint.Y + primaryTargetPoint.Y) * 0.5f
                        };

                        commands.AddRange(MoveTo(frame, primaryWorker, primaryTargetPoint.X, primaryTargetPoint.Y));
                        commands.AddRange(MoveTo(frame, secondaryWorker, secondaryTargetPoint.X, secondaryTargetPoint.Y));
                        queuedMoves = true;
                        continue;
                    }
                }

                if (frame >= 15)
                {
                    bumpEnabled = false;
                }

                foreach (var worker in team.Workers)
                {
                    var workerLabel = worker?.FinalLabel ?? worker?.Label ?? worker?.StartLabel;
                    var liveWorker = ResolveWorkerByLabel(workerLabel, workerEntries);
                    if (liveWorker?.Position == null)
                    {
                        continue;
                    }

                    var targetPoint = GetFallbackTargetPoint(team, workerLabel, liveWorker.Position, aMineral.Position, bMineral.Position);
                    commands.AddRange(MoveTo(frame, liveWorker, targetPoint.X, targetPoint.Y));
                    queuedMoves = true;
                }

                if (queuedMoves)
                {
                    Debugger.Break();
                }
            }

            if (frame % 5 == 0)
            {
                Debugger.Break();
            }

            return commands;
        }

        public CcaSpawnLearningState GetCurrentSpawnState(MawBaseLocationData mapData, int startIndex)
        {
            return GetOrCreateCurrentSpawnState(mapData, startIndex);
        }

        private static WorkerEntryDto ResolveWorkerByLabel(string label, IReadOnlyList<WorkerEntryDto> allWorkers)
        {
            if (string.IsNullOrWhiteSpace(label) || allWorkers == null)
            {
                return null;
            }

            return allWorkers.FirstOrDefault(w => string.Equals(w?.FinalLabel ?? w?.Label ?? w?.StartLabel, label, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsBumpEnabled(int teamNumber)
        {
            return teamNumber switch
            {
                1 => Settings.T1Bump,
                2 => Settings.S1Bump,
                3 => Settings.B1Bump,
                4 => Settings.Y1Bump,
                _ => false
            };
        }

        private static WorkerEntryDto ResolveWorker(IEnumerable<WorkerEntryDto> teamWorkers, IReadOnlyList<WorkerEntryDto> allWorkers, int workerSuffix)
        {
            var label = $"W{workerSuffix}";
            var worker = allWorkers?.FirstOrDefault(w => string.Equals(w?.FinalLabel ?? w?.Label ?? w?.StartLabel, label, StringComparison.OrdinalIgnoreCase));
            if (worker != null)
            {
                return worker;
            }

            return teamWorkers?.FirstOrDefault(w => string.Equals(w?.FinalLabel ?? w?.Label ?? w?.StartLabel, label, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<SC2APIProtocol.Action> MoveTo(int frame, WorkerEntryDto worker, float x, float y)
        {
            if (worker?.UnitTag == 0)
            {
                return Array.Empty<SC2APIProtocol.Action>();
            }

            var command = new ActionRawUnitCommand
            {
                AbilityId = 16,
                TargetWorldSpacePos = new Point2D { X = x, Y = y }
            };
            command.UnitTags.Add(worker.UnitTag);

            return new List<SC2APIProtocol.Action>
            {
                new SC2APIProtocol.Action
                {
                    ActionRaw = new ActionRaw
                    {
                        UnitCommand = command
                    }
                }
            };
        }

        private static Point2D GetFallbackTargetPoint(TeamPatchAssignmentDto team, string workerLabel, Vector2Dto liveWorkerPosition, Vector2Dto aMineralPosition, Vector2Dto bMineralPosition)
        {
            var workerSuffix = GetWorkerSuffix(workerLabel);
            if (workerSuffix == 2 && bMineralPosition != null)
            {
                return new Point2D
                {
                    X = bMineralPosition.X,
                    Y = bMineralPosition.Y
                };
            }

            if (aMineralPosition != null)
            {
                return new Point2D
                {
                    X = aMineralPosition.X,
                    Y = aMineralPosition.Y
                };
            }

            return new Point2D
            {
                X = liveWorkerPosition?.X ?? 0,
                Y = liveWorkerPosition?.Y ?? 0
            };
        }

        private static int GetWorkerSuffix(string workerLabel)
        {
            if (string.IsNullOrWhiteSpace(workerLabel) || workerLabel.Length < 2)
            {
                return 0;
            }

            return int.TryParse(workerLabel.Substring(1), out var suffix) ? suffix : 0;
        }

        private static float Distance(Vector2Dto first, Vector2Dto second)
        {
            if (first == null || second == null)
            {
                return float.MaxValue;
            }

            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private static string BuildSpawnKey(MawBaseLocationData mapData, int startIndex)
        {
            var location = mapData?.StartingTownHall != null && startIndex >= 0 && startIndex < mapData.StartingTownHall.Length
                ? mapData.StartingTownHall[startIndex]
                : null;

            if (location == null)
            {
                return $"spawn-{startIndex}";
            }

            return $"spawn-{startIndex}-{location.X:F2}-{location.Y:F2}";
        }

        private static TeamPatchAssignmentDto CloneTeamAssignment(TeamPatchAssignmentDto assignment)
        {
            if (assignment == null)
            {
                return null;
            }

            return new TeamPatchAssignmentDto
            {
                TeamNumber = assignment.TeamNumber,
                NearLabel = assignment.NearLabel,
                FarLabel = assignment.FarLabel,
                Workers = assignment.Workers?.Select(CloneWorker).ToList() ?? new List<WorkerEntryDto>(),
                Minerals = assignment.Minerals?.Select(CloneMineral).ToList() ?? new List<OrderedMineral>()
            };
        }

        private static WorkerEntryDto CloneWorker(WorkerEntryDto worker)
        {
            if (worker == null)
            {
                return null;
            }

            return new WorkerEntryDto
            {
                UnitTag = worker.UnitTag,
                UnitType = worker.UnitType,
                Label = worker.Label,
                StartLabel = worker.StartLabel,
                FinalLabel = worker.FinalLabel,
                Position = worker.Position == null ? new Vector2Dto() : new Vector2Dto(worker.Position.X, worker.Position.Y, worker.Position.Z)
            };
        }

        private static OrderedMineral CloneMineral(OrderedMineral mineral)
        {
            if (mineral == null)
            {
                return null;
            }

            return new OrderedMineral
            {
                Index = mineral.Index,
                Label = mineral.Label,
                FinalLabel = mineral.FinalLabel,
                TeamLabel = mineral.TeamLabel,
                IsNear = mineral.IsNear,
                IsFar = mineral.IsFar,
                Resources = mineral.Resources,
                Size = mineral.Size,
                DistanceToTownhall = mineral.DistanceToTownhall,
                DistanceFromCOM = mineral.DistanceFromCOM,
                OriginalIndex = mineral.OriginalIndex,
                Position = mineral.Position == null ? new Vector2Dto() : new Vector2Dto(mineral.Position.X, mineral.Position.Y, mineral.Position.Z),
                HarvestPoint = mineral.HarvestPoint == null ? new Vector2Dto() : new Vector2Dto(mineral.HarvestPoint.X, mineral.HarvestPoint.Y, mineral.HarvestPoint.Z),
                SmHarvestPoint = mineral.SmHarvestPoint == null ? new Vector2Dto() : new Vector2Dto(mineral.SmHarvestPoint.X, mineral.SmHarvestPoint.Y, mineral.SmHarvestPoint.Z),
                ReturnPoint = mineral.ReturnPoint == null ? new Vector2Dto() : new Vector2Dto(mineral.ReturnPoint.X, mineral.ReturnPoint.Y, mineral.ReturnPoint.Z),
                SmReturnPoint = mineral.SmReturnPoint == null ? new Vector2Dto() : new Vector2Dto(mineral.SmReturnPoint.X, mineral.SmReturnPoint.Y, mineral.SmReturnPoint.Z)
            };
        }
    }

    public sealed class CcaSpawnLearningState
    {
        public string SpawnKey { get; set; } = string.Empty;
        public int StartIndex { get; set; } = -1;
        public bool CcaMining { get; set; }
        public bool TealM1IsFar { get; set; }
        public bool YellowM8IsFar { get; set; }
        public List<TeamPatchAssignmentDto> TeamAssignments { get; set; } = new List<TeamPatchAssignmentDto>();
    }
}
