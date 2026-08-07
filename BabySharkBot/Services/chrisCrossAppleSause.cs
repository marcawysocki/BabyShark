using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using BabySharkBot.Setup;
using BabySharkBot.MicroTasks;
using System.Diagnostics;

namespace BabySharkBot.Services
{
    public sealed class chrisCrossAppleSause
    {
        public enum TestPhase { Idle, AssigningWorkers, AcceleratingWorkerOne, AlignAtMineralA, CancelAndReturnHome }

        private static volatile TestPhase _phase = TestPhase.Idle;
        private readonly Dictionary<string, CcaSpawnLearningState> _states = new Dictionary<string, CcaSpawnLearningState>(StringComparer.OrdinalIgnoreCase);

        public TestPhase CurrentPhase => _phase;

        public CcaSpawnLearningState GetOrCreateCurrentSpawnState(MawBaseLocationData mapData, int startIndex)
        {
            var key = BuildSpawnKey(mapData, startIndex);
            if (!_states.TryGetValue(key, out var state))
            {
                state = new CcaSpawnLearningState
                {
                    SpawnKey = key,
                    StartIndex = startIndex,
                    TealM1IsFar = startIndex >= 0 && mapData?.M1IsFar != null && startIndex < mapData.M1IsFar.Length && mapData.M1IsFar[startIndex],
                    YellowM8IsFar = startIndex >= 0 && mapData?.M8IsFar != null && startIndex < mapData.M8IsFar.Length && mapData.M8IsFar[startIndex]
                };
                _states[key] = state;
            }
            return state;
        }

        public CcaSpawnLearningState GetCurrentSpawnState(MawBaseLocationData mapData, int startIndex)
        {
            return GetOrCreateCurrentSpawnState(mapData, startIndex);
        }

        public void SetPhase(TestPhase phase)
        {
            if (_phase == phase) return;
            _phase = phase;
            Console.WriteLine($"chrisCrossAppleSause: Phase changed to {_phase}");
        }

        public void EnableCcaMiningForCurrentSpawn(MawBaseLocationData mapData, int startIndex)
        {
            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);
            state.CcaMining = true;
            Settings.ccaMining = true;

            if (_phase == TestPhase.Idle)
            {
                SetPhase(TestPhase.AssigningWorkers);
            }
        }

        public IEnumerable<SC2APIProtocol.Action> BuildBumpOrders(int frame, MawBaseLocationData mapData, int startIndex, IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();

            if (!Settings.ccaMining || mapData == null || workerEntries == null || workerEntries.Count == 0)
            {
                return commands;
            }

            // Logic runs every 5 frames as per prompt
            if (frame % 5 != 0)
            {
                return commands;
            }

            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);

            // Fallback: If current start index has no assignments, try to find ANY valid assignments in mapData
            if (!state.TeamAssignments.Any() && mapData.TeamPatchAssignments != null)
            {
                var firstValid = mapData.TeamPatchAssignments.FirstOrDefault(a => a != null && a.Any());
                if (firstValid != null)
                {
                    state.TeamAssignments = firstValid;
                }
            }

            // Execute state machine transitions immediately until we hit a commanded phase
            bool stateChanged = true;
            while (stateChanged)
            {
                stateChanged = false;
                switch (_phase)
                {
                    case TestPhase.Idle:
                        if (state.TeamAssignments.Any())
                        {
                            SetPhase(TestPhase.AssigningWorkers);
                            stateChanged = true;
                        }
                        else
                        {
                            return commands;
                        }
                        break;

                    case TestPhase.AssigningWorkers:
                        if (state.TeamAssignments.Any())
                        {
                            SetPhase(TestPhase.AcceleratingWorkerOne);
                            stateChanged = true;
                        }
                        break;

                    case TestPhase.AcceleratingWorkerOne:
                        commands.AddRange(HandleAcceleratingWorkerOne(frame, mapData, state, workerEntries));
                        return commands;

                    case TestPhase.AlignAtMineralA:
                        commands.AddRange(HandleAlignAtMineralA(frame, mapData, state, workerEntries));
                        return commands;

                    case TestPhase.CancelAndReturnHome:
                        return commands;
                }
            }

            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> HandleAcceleratingWorkerOne(int frame, MawBaseLocationData mapData, CcaSpawnLearningState state, IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();
            bool allAtA = true;

            foreach (var team in state.TeamAssignments)
            {
                var logicalWorkers = team.Workers;
                var minerals = team.Minerals;
                if (logicalWorkers.Count == 0 || minerals.Count == 0) continue;

                var aMineral = minerals.FirstOrDefault(m => m.IsNear) ?? minerals[0];
                var bMineral = minerals.FirstOrDefault(m => !m.IsNear && m != aMineral) ?? minerals.Skip(1).FirstOrDefault() ?? aMineral;

                var w1 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "1");
                var w2 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "2");
                var w3 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "3");

                if (w1 == null)
                {
                    continue;
                }

                var distToA = Distance(w1.Position, aMineral.Position);
                if (distToA > 1.5f)
                {
                    allAtA = false;

                    WorkerEntryDto bumper = null;
                    if (team.TeamNumber == 1) bumper = w3;
                    else if (team.TeamNumber == 2) bumper = w2;
                    else if (team.TeamNumber == 3) bumper = w2;
                    else if (team.TeamNumber == 4) bumper = w3;

                    if (bumper != null && Distance(w1.Position, bumper.Position) < 2.0f)
                    {
                        var targetLinePoint = new Point2D
                        {
                            X = (w1.Position.X + bumper.Position.X + aMineral.Position.X) / 3f,
                            Y = (w1.Position.Y + bumper.Position.Y + aMineral.Position.Y) / 3f
                        };

                        var movePoint = new Point2D
                        {
                            X = (w1.Position.X + targetLinePoint.X) * 0.5f,
                            Y = (w1.Position.Y + targetLinePoint.Y) * 0.5f
                        };
                        commands.AddRange(MoveTo(w1.UnitTag, movePoint));

                        var bumpMovePoint = new Point2D
                        {
                            X = (bumper.Position.X + w1.Position.X) * 0.25f + bumper.Position.X * 0.75f,
                            Y = (bumper.Position.Y + w1.Position.Y) * 0.25f + bumper.Position.Y * 0.75f
                        };
                        commands.AddRange(MoveTo(bumper.UnitTag, bumpMovePoint));
                    }
                    else
                    {
                        commands.AddRange(MoveTo(w1.UnitTag, new Point2D { X = aMineral.Position.X, Y = aMineral.Position.Y }));
                        if (w2 != null) commands.AddRange(MoveTo(w2.UnitTag, new Point2D { X = bMineral.Position.X, Y = bMineral.Position.Y }));
                        if (w3 != null) commands.AddRange(MoveTo(w3.UnitTag, new Point2D { X = aMineral.Position.X, Y = aMineral.Position.Y }));
                    }
                }
            }

            if (allAtA && state.TeamAssignments.Any())
            {
                SetPhase(TestPhase.AlignAtMineralA);
            }

            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> HandleAlignAtMineralA(int frame, MawBaseLocationData mapData, CcaSpawnLearningState state, IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();

            foreach (var team in state.TeamAssignments)
            {
                var logicalWorkers = team.Workers;
                var minerals = team.Minerals;
                if (logicalWorkers.Count == 0 || minerals.Count == 0) continue;

                var aMineral = minerals.FirstOrDefault(m => m.IsNear) ?? minerals[0];
                var hatcheryPos = mapData.StartingTownHall[state.StartIndex];

                var w1 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "1");
                var w3 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "3");

                if (w1 == null || w3 == null || aMineral == null || hatcheryPos == null) continue;

                var dirX = aMineral.Position.X - hatcheryPos.X;
                var dirY = aMineral.Position.Y - hatcheryPos.Y;
                var mag = MathF.Sqrt(dirX * dirX + dirY * dirY);
                dirX /= mag;
                dirY /= mag;

                var w3Target = new Point2D
                {
                    X = aMineral.Position.X - dirX * 1.0f,
                    Y = aMineral.Position.Y - dirY * 1.0f
                };
                commands.AddRange(MoveTo(w3.UnitTag, w3Target));
            }

            return commands;
        }

        private WorkerEntryDto ResolveLiveWorkerBySuffix(List<WorkerEntryDto> logicalWorkers, IReadOnlyList<WorkerEntryDto> liveWorkers, string suffix)
        {
            var logical = logicalWorkers.FirstOrDefault(w => w.Label != null && w.Label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (logical == null) return null;

            var labelToMatch = logical.FinalLabel ?? logical.Label ?? logical.StartLabel;
            if (string.IsNullOrWhiteSpace(labelToMatch)) return null;

            return liveWorkers.FirstOrDefault(w =>
                string.Equals(w.Label, labelToMatch, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w.FinalLabel, labelToMatch, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w.StartLabel, labelToMatch, StringComparison.OrdinalIgnoreCase));
        }

        private IEnumerable<SC2APIProtocol.Action> MoveTo(ulong tag, Point2D point)
        {
            if (tag == 0) return Array.Empty<SC2APIProtocol.Action>();

            var command = new ActionRawUnitCommand
            {
                AbilityId = 16, // MOVE
                TargetWorldSpacePos = point
            };
            command.UnitTags.Add(tag);
            return new List<SC2APIProtocol.Action> { new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } } };
        }

        private float Distance(Vector2Dto a, Vector2Dto b)
        {
            if (a == null || b == null) return float.MaxValue;
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private static string BuildSpawnKey(MawBaseLocationData mapData, int startIndex)
        {
            var location = (mapData?.StartingTownHall != null && startIndex >= 0 && startIndex < mapData.StartingTownHall.Length)
                ? mapData.StartingTownHall[startIndex] : null;
            return location == null ? $"spawn-{startIndex}" : $"spawn-{startIndex}-{location.X:F2}-{location.Y:F2}";
        }

        public void RecordSpawnObservation(MawBaseLocationData mapData, int startIndex, List<List<TeamPatchAssignmentDto>> teamAssignmentsByStart, WorkerLabelService workerLabelService = null, int frame = -1, IReadOnlyList<WorkerEntryDto> workerEntries = null)
        {
            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);
            state.TeamAssignments = (teamAssignmentsByStart != null && startIndex >= 0 && startIndex < teamAssignmentsByStart.Count)
                ? teamAssignmentsByStart[startIndex] : new List<TeamPatchAssignmentDto>();

            // Sync tags from live workers using labels
            if (workerEntries != null)
            {
                foreach (var team in state.TeamAssignments)
                {
                    foreach (var logicalWorker in team.Workers)
                    {
                        var label = logicalWorker.FinalLabel ?? logicalWorker.Label ?? logicalWorker.StartLabel;
                        if (string.IsNullOrWhiteSpace(label)) continue;

                        var liveWorker = workerEntries.FirstOrDefault(w =>
                            string.Equals(w.Label, label, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(w.FinalLabel, label, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(w.StartLabel, label, StringComparison.OrdinalIgnoreCase));

                        if (liveWorker != null)
                        {
                            logicalWorker.UnitTag = liveWorker.UnitTag;
                        }
                    }
                }
            }

            if (workerLabelService != null)
            {
                foreach (var team in state.TeamAssignments)
                {
                    foreach (var worker in team.Workers)
                    {
                        var label = worker.FinalLabel ?? worker.Label ?? worker.StartLabel;
                        if (!string.IsNullOrWhiteSpace(label) && worker.UnitTag != 0)
                        {
                            workerLabelService.SetLabel(label, worker.UnitTag);
                        }
                    }
                }
            }
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
