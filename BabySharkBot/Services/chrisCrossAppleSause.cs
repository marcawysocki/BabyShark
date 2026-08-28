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
        private WorkerLabelService _workerLabelService;
        private int _frame55HandoffStartIndex = -1;
        private int _frame15GatherFrame = -1;
        private readonly HashSet<ulong> _frame15GatheredWorkers = new();
        private readonly HashSet<string> _roleOneCompletionTeams = new(StringComparer.OrdinalIgnoreCase);

        public void EnableCcaMiningForCurrentSpawn(MawBaseLocationData mapData, int startIndex)
        {
            Settings.ccaMining = true;
            _roleOneCompletionTeams.Clear();
            _frame55HandoffStartIndex = -1;
            _frame15GatherFrame = -1;
            _frame15GatheredWorkers.Clear();
        }

        public IEnumerable<SC2APIProtocol.Action> BuildBumpOrders(int frame, MawBaseLocationData mapData, int startIndex, IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();
            var relativeFrame = Settings.GetRelativeFrame(frame);

            if (!Settings.ccaMining || mapData == null || workerEntries == null || workerEntries.Count == 0)
            {
                return commands;
            }

            if (relativeFrame >= 15 && relativeFrame < 35 && !Settings.IsMagannathaMap)
            {
                var roleOneCompletion = BuildRoleOneCompletionHandoff(mapData, startIndex, workerEntries);
                if (roleOneCompletion.Count > 0)
                {
                    return roleOneCompletion;
                }
            }

            if (relativeFrame == 15 && (workerEntries.Count == 8 || workerEntries.Count == 12 || Settings.WorkerCount == 8 || Settings.WorkerCount == 12))
            {
                if (_frame15GatherFrame != frame)
                {
                    _frame15GatherFrame = frame;
                    _frame15GatheredWorkers.Clear();
                }

                return BuildRoleOneHarvestOrders(mapData, startIndex, workerEntries).ToList();
            }

            if (workerEntries.Count == 8 || Settings.WorkerCount == 8)
            {
                 return IsSharkySequenceFrame(relativeFrame)
                     ? BuildFrame0EightWorkerSpeedMine(mapData, startIndex, workerEntries, relativeFrame).ToList()
                     : commands;
            }


            if (workerEntries.Count == 12 || Settings.WorkerCount == 12)
            {
                if (Settings.IsMagannatha12WorkerOverride)
                {
                    if (relativeFrame == 55 && _frame55HandoffStartIndex != startIndex)
                    {
                        _frame55HandoffStartIndex = startIndex;
                        return BuildMagannathaFrame55Handoff(mapData, startIndex, workerEntries).ToList();
                    }

                    return relativeFrame == 0
                        ? BuildTwelveWorkerOrders(relativeFrame, mapData, startIndex, workerEntries, true).ToList()
                        : BuildMagannathaOpeningOrders(relativeFrame, mapData, startIndex, workerEntries).ToList();
                }

                return relativeFrame == 0
                    ? BuildTwelveWorkerOrders(relativeFrame, mapData, startIndex, workerEntries, true).ToList()
                    : BuildTwelveWorkerOrders(relativeFrame, mapData, startIndex, workerEntries, true).ToList();
            }

            if (relativeFrame < 0 || relativeFrame % 5 != 0 || Settings.AvailableWorker.Count == 0)
            {
                return commands;
            }

            var teamAssignments = mapData.TeamPatchAssignments != null
                && startIndex >= 0
                && startIndex < mapData.TeamPatchAssignments.Count
                ? mapData.TeamPatchAssignments[startIndex]
                : null;
            if (teamAssignments == null || teamAssignments.Count == 0)
            {
                return commands;
            }

            commands.AddRange(HandleAcceleratingWorkerOne(frame, mapData, teamAssignments, workerEntries));
            commands.AddRange(HandleAlignAtMineralA(frame, mapData, startIndex, teamAssignments, workerEntries));
            return commands;
        }

        private List<SC2APIProtocol.Action> BuildRoleOneCompletionHandoff(
            MawBaseLocationData mapData,
            int startIndex,
            IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();
            var assignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);
            var snapshot = Globals.CurrentObservation;
            var hatcheryTag = snapshot?.CurrentTownHalls?.Values
                .Where(unit => unit?.UnitTag != 0 && unit.Position != null)
                .OrderBy(unit => DistanceSquared(unit.Position, mapData.StartingTownHall.ElementAtOrDefault(startIndex)))
                .Select(unit => unit.UnitTag)
                .FirstOrDefault() ?? 0;
            if (assignments.Count == 0 || snapshot == null || hatcheryTag == 0)
            {
                return commands;
            }

            foreach (var assignment in assignments)
            {
                var teamPrefix = GetTeamPrefix(assignment.TeamNumber);
                if (string.IsNullOrEmpty(teamPrefix) || _roleOneCompletionTeams.Contains(teamPrefix))
                {
                    continue;
                }

                var roleOne = assignment.Workers?.FirstOrDefault(worker =>
                    (worker?.FinalLabel ?? worker?.Label)?.Equals($"{teamPrefix}1", StringComparison.OrdinalIgnoreCase) == true);
                var liveRoleOne = workerEntries.FirstOrDefault(worker => worker.UnitTag == roleOne?.UnitTag);
                if (liveRoleOne == null || !liveRoleOne.IsCarrying)
                {
                    continue;
                }

                commands.AddRange(SmartTo(liveRoleOne.UnitTag, hatcheryTag));
                _roleOneCompletionTeams.Add(teamPrefix);
                Console.WriteLine($"[CCA ROLE1 COMPLETE] team={teamPrefix} role1={liveRoleOne.UnitTag} command=SMART hatchery queued=true");
            }

            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> BuildRoleOneHarvestOrders(
            MawBaseLocationData mapData,
            int startIndex,
            IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();
            var assignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);
            var snapshot = Globals.CurrentObservation;
            if (assignments.Count == 0 || snapshot == null)
            {
                return commands;
            }

            foreach (var assignment in assignments)
            {
                var teamPrefix = GetTeamPrefix(assignment.TeamNumber);
                var aMineral = assignment.Minerals?.FirstOrDefault(mineral =>
                    string.Equals(mineral?.FinalLabel, $"{teamPrefix}A", StringComparison.OrdinalIgnoreCase));
                var bMineral = assignment.Minerals?.FirstOrDefault(mineral =>
                    string.Equals(mineral?.FinalLabel, $"{teamPrefix}B", StringComparison.OrdinalIgnoreCase));

                foreach (var worker in assignment.Workers ?? new List<WorkerEntryDto>())
                {
                    var role = worker?.FinalLabel ?? worker?.Label;
                    if (worker?.UnitTag == 0
                        || string.IsNullOrWhiteSpace(role)
                        || (!role.EndsWith("1", StringComparison.OrdinalIgnoreCase)
                            && !role.EndsWith("2", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var mineral = role.EndsWith("1", StringComparison.OrdinalIgnoreCase) ? aMineral : bMineral;
                    var mineralTag = ResolveLiveMineralTag(mineral, snapshot);
                    var liveWorker = workerEntries.FirstOrDefault(candidate => candidate.UnitTag == worker.UnitTag);
                    if (liveWorker != null && mineralTag != 0 && _frame15GatheredWorkers.Add(liveWorker.UnitTag))
                    {
                        commands.AddRange(HarvestGatherTo(liveWorker.UnitTag, mineralTag, true));
                    }
                }
            }


            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> BuildTwelveWorkerFrameZeroOrders(
            MawBaseLocationData mapData,
            int startIndex,
            IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();
            var assignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);
            if (assignments.Count == 0)
            {
                return commands;
            }

            foreach (var assignment in assignments)
            {
                if (assignment.Workers == null || assignment.Workers.Count == 0 || assignment.Minerals == null || assignment.Minerals.Count < 2)
                {
                    continue;
                }

                var teamPrefix = GetTeamPrefix(assignment.TeamNumber);
                var aMineral = assignment.Minerals.FirstOrDefault(mineral =>
                    string.Equals(mineral?.FinalLabel, $"{teamPrefix}A", StringComparison.OrdinalIgnoreCase));
                var bMineral = assignment.Minerals.FirstOrDefault(mineral =>
                    string.Equals(mineral?.FinalLabel, $"{teamPrefix}B", StringComparison.OrdinalIgnoreCase));
                var roleOne = assignment.Workers.FirstOrDefault(worker => (worker.FinalLabel ?? worker.Label)?.EndsWith("1", StringComparison.OrdinalIgnoreCase) == true);
                var roleTwo = assignment.Workers.FirstOrDefault(worker => (worker.FinalLabel ?? worker.Label)?.EndsWith("2", StringComparison.OrdinalIgnoreCase) == true);
                var roleThree = assignment.Workers.FirstOrDefault(worker => (worker.FinalLabel ?? worker.Label)?.EndsWith("3", StringComparison.OrdinalIgnoreCase) == true);
                if (!IsTeamBumpingDisabled(mapData, startIndex, assignment.TeamNumber)
                    || aMineral?.HarvestPoint == null
                    || bMineral?.HarvestPoint == null)
                {
                    continue;
                }

                if (roleOne != null && workerEntries.Any(worker => worker.UnitTag == roleOne.UnitTag))
                {
                    commands.AddRange(MoveTo(roleOne.UnitTag, ToPoint2D(aMineral.HarvestPoint)));
                }

                if (roleTwo != null && workerEntries.Any(worker => worker.UnitTag == roleTwo.UnitTag))
                {
                    commands.AddRange(MoveTo(roleTwo.UnitTag, ToPoint2D(bMineral.HarvestPoint)));
                }

                if (roleThree != null
                    && workerEntries.Any(worker => worker.UnitTag == roleThree.UnitTag))
                {
                    var waitPoint = CalculateRoleThreeWaitPoint(
                        roleThree.FinalLabel ?? roleThree.Label,
                        assignment,
                        mapData.StartingTownHall.ElementAtOrDefault(startIndex),
                        aMineral);
                    if (waitPoint != null)
                    {
                        commands.AddRange(MoveTo(roleThree.UnitTag, ToPoint2D(waitPoint)));
                    }
                }
            }

            return commands;
        }

        private static bool IsTeamBumpingDisabled(MawBaseLocationData mapData, int startIndex, int teamNumber)
        {
            if (Settings.IsMagannatha12WorkerOverride)
            {
                return true;
            }

            var prefix = GetTeamPrefix(teamNumber);
            return mapData?.AssignmentFlagsByStart?.TryGetValue(startIndex, out var flags) == true
                && flags.TryGetValue($"{prefix}NoPush", out var noPush)
                && noPush;
        }

        private static Vector2Dto CalculateRoleThreeWaitPoint(
            string role,
            TeamPatchAssignmentDto assignment,
            Vector2Dto townhall,
            OrderedMineral aMineral)
        {
            if (aMineral?.Position == null || townhall == null)
            {
                return null;
            }

            if (string.Equals(role, "S3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "B3", StringComparison.OrdinalIgnoreCase))
            {
                var dx = townhall.X - aMineral.Position.X;
                var dy = townhall.Y - aMineral.Position.Y;
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                return distance <= 0.001f
                    ? null
                    : new Vector2Dto(
                        aMineral.Position.X + dx / distance,
                        aMineral.Position.Y + dy / distance,
                        aMineral.Position.Z);
            }

            return assignment?.JitWaitPoint != null
                && (assignment.JitWaitPoint.X != 0f || assignment.JitWaitPoint.Y != 0f)
                ? assignment.JitWaitPoint
                : CalculateWaitPoint(townhall, aMineral.Position);
        }

        private static Vector2Dto CalculateWaitPoint(Vector2Dto townhall, Vector2Dto mineral)
        {
            if (townhall == null || mineral == null)
            {
                return null;
            }

            var dx = mineral.X - townhall.X;
            var dy = mineral.Y - townhall.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            return distance <= 0.001f
                ? null
                : new Vector2Dto(mineral.X - dx / distance, mineral.Y - dy / distance, mineral.Z);
        }

        private static Point2D ToPoint2D(Vector2Dto point)
        {
            return point == null ? null : new Point2D { X = point.X, Y = point.Y };
        }

        private IEnumerable<SC2APIProtocol.Action> BuildMagannathaOpeningOrders(
            int relativeFrame,
            MawBaseLocationData mapData,
            int startIndex,
            IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            return relativeFrame == 0 || relativeFrame == 1 || relativeFrame == 14
                ? BuildTwelveWorkerOrders(relativeFrame, mapData, startIndex, workerEntries, true)
                : Array.Empty<SC2APIProtocol.Action>();
        }

        private IEnumerable<SC2APIProtocol.Action> BuildMagannathaFrame55Handoff(
            MawBaseLocationData mapData,
            int startIndex,
            IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();
            var assignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);
            var snapshot = Globals.CurrentObservation;

            if (assignments.Count == 0 || snapshot == null)
            {
                return commands;
            }

            foreach (var assignment in assignments)
            {
                var aMineral = assignment.Minerals?.FirstOrDefault(mineral =>
                    string.Equals(mineral?.FinalLabel, $"{GetTeamPrefix(assignment.TeamNumber)}A", StringComparison.OrdinalIgnoreCase));
                if (aMineral == null)
                {
                    continue;
                }

                foreach (var worker in assignment.Workers ?? new List<WorkerEntryDto>())
                {
                    var liveWorker = workerEntries.FirstOrDefault(candidate => candidate.UnitTag == worker.UnitTag);
                    var role = worker.FinalLabel ?? worker.Label;
                    if (liveWorker == null || string.IsNullOrWhiteSpace(role))
                    {
                        continue;
                    }

                    if (role.EndsWith("3", StringComparison.OrdinalIgnoreCase))
                    {
                        var mineralTag = ResolveLiveMineralTag(aMineral, snapshot);
                        if (mineralTag != 0)
                        {
                            commands.AddRange(SmartToImmediate(liveWorker.UnitTag, mineralTag));
                        }
                    }
                }
            }

            return commands;
        }

        private static string GetTeamPrefix(int teamNumber)
        {
            return teamNumber switch
            {
                1 => "T",
                2 => "S",
                3 => "B",
                4 => "Y",
                _ => string.Empty
            };
        }

        private static float DistanceSquared(Vector2Dto first, Vector2Dto second)
        {
            if (first == null || second == null)
            {
                return float.MaxValue;
            }

            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }

        private IEnumerable<SC2APIProtocol.Action> BuildTwelveWorkerOrders(
            int relativeFrame,
            MawBaseLocationData mapData,
            int startIndex,
            IReadOnlyList<WorkerEntryDto> workerEntries,
            bool waitRoleThree = false)
        {
            var commands = new List<SC2APIProtocol.Action>();
            if (relativeFrame != 0 && relativeFrame != 1 && relativeFrame != 14)
            {
                return commands;
            }

            var assignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);
            if (assignments.Count == 0)
            {
                return commands;
            }

            foreach (var assignment in assignments)
            {
                foreach (var worker in assignment.Workers ?? new List<WorkerEntryDto>())
                {
                    var liveWorker = workerEntries.FirstOrDefault(candidate => candidate.UnitTag == worker.UnitTag);
                    var target = ResolveInitialTarget(worker, assignment);
                    if (liveWorker == null || target?.Position == null)
                    {
                        continue;
                    }

                    if (waitRoleThree && IsRoleThree(worker.FinalLabel ?? worker.Label))
                    {
                        var aMineral = assignment.Minerals?.FirstOrDefault(mineral =>
                            string.Equals(mineral?.FinalLabel, $"{GetTeamPrefix(assignment.TeamNumber)}A", StringComparison.OrdinalIgnoreCase));
                        var waitPoint = CalculateRoleThreeWaitPoint(
                            worker.FinalLabel ?? worker.Label,
                            assignment,
                            mapData.StartingTownHall.ElementAtOrDefault(startIndex),
                            aMineral);
                        if (waitPoint != null)
                        {
                            commands.AddRange(MoveTo(liveWorker.UnitTag, ToPoint2D(waitPoint), false));
                        }

                        continue;
                    }

                    var movePoint = target.HarvestPoint != null
                        && (target.HarvestPoint.X != 0f || target.HarvestPoint.Y != 0f)
                        ? new Point2D { X = target.HarvestPoint.X, Y = target.HarvestPoint.Y }
                        : new Point2D { X = target.Position.X, Y = target.Position.Y };
                    var mineralTag = ResolveLiveMineralTag(target, Globals.CurrentObservation);
                    if (mineralTag == 0)
                    {
                        continue;
                    }

                    commands.AddRange(BuildSharkyGatherSequence(
                        liveWorker.UnitTag,
                        mineralTag,
                        target,
                        movePoint,
                        relativeFrame));
                }
            }

            return commands;
        }

        private static bool IsRoleThree(string label)
        {
            return !string.IsNullOrWhiteSpace(label)
                && label.Length == 2
                && label[1] == '3'
                && (label[0] == 'T' || label[0] == 'S' || label[0] == 'B' || label[0] == 'Y');
        }

        private static OrderedMineral ResolveInitialTarget(WorkerEntryDto worker, TeamPatchAssignmentDto assignment)
        {
            if (worker == null || assignment?.Minerals == null)
            {
                return null;
            }

            var role = worker.FinalLabel ?? worker.Label;
            if (string.Equals(role, "S3", StringComparison.OrdinalIgnoreCase))
            {
                return assignment.Minerals.FirstOrDefault(mineral => string.Equals(mineral.FinalLabel, "SA", StringComparison.OrdinalIgnoreCase));
            }

            if (string.Equals(role, "B3", StringComparison.OrdinalIgnoreCase))
            {
                return assignment.Minerals.FirstOrDefault(mineral => string.Equals(mineral.FinalLabel, "BA", StringComparison.OrdinalIgnoreCase));
            }

            if (role?.Length == 2 && int.TryParse(role.Substring(1), out var roleNumber))
            {
                var suffix = roleNumber == 1 ? "A" : roleNumber == 2 ? "B" : "A";
                return assignment.Minerals.FirstOrDefault(mineral =>
                    string.Equals(mineral.FinalLabel, $"{role[0]}{suffix}", StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private IEnumerable<SC2APIProtocol.Action> HandleAcceleratingWorkerOne(int frame, MawBaseLocationData mapData, IReadOnlyList<TeamPatchAssignmentDto> teamAssignments, IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();

            foreach (var team in teamAssignments)
            {
                var logicalWorkers = team.Workers;
                var minerals = team.Minerals;
                if (logicalWorkers.Count == 0 || minerals.Count == 0) continue;

                var teamPrefix = GetTeamPrefix(team.TeamNumber);
                var aMineral = minerals.FirstOrDefault(mineral => string.Equals(mineral?.FinalLabel, $"{teamPrefix}A", StringComparison.OrdinalIgnoreCase));
                var bMineral = minerals.FirstOrDefault(mineral => string.Equals(mineral?.FinalLabel, $"{teamPrefix}B", StringComparison.OrdinalIgnoreCase));
                if (aMineral == null || bMineral == null)
                {
                    continue;
                }

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

            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> HandleAlignAtMineralA(int frame, MawBaseLocationData mapData, int startIndex, IReadOnlyList<TeamPatchAssignmentDto> teamAssignments, IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();

            foreach (var team in teamAssignments)
            {
                var logicalWorkers = team.Workers;
                var minerals = team.Minerals;
                if (logicalWorkers.Count == 0 || minerals.Count == 0) continue;

                var teamPrefix = GetTeamPrefix(team.TeamNumber);
                var aMineral = minerals.FirstOrDefault(mineral => string.Equals(mineral?.FinalLabel, $"{teamPrefix}A", StringComparison.OrdinalIgnoreCase));
                if (aMineral == null)
                {
                    continue;
                }

                var hatcheryPos = mapData.StartingTownHall[startIndex];

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
            if (tag == 0 || point == null) return Array.Empty<SC2APIProtocol.Action>();

            var workerLabel = _workerLabelService?.GetLabel(tag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMAND2] phase=CCA worker={tag} Label={workerLabel} command=MOVE pos=({point.X:F2},{point.Y:F2}) queued=false");
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

        public void RecordSpawnObservation(MawBaseLocationData mapData, int startIndex, List<List<TeamPatchAssignmentDto>> teamAssignmentsByStart, WorkerLabelService workerLabelService = null, int frame = -1, IReadOnlyList<WorkerEntryDto> workerEntries = null)
        {
            _workerLabelService = workerLabelService;
            // Team assignments are read directly from mapData during BuildBumpOrders.
            // This compatibility hook retains the shared label service for command diagnostics.
        }

        /// <summary>
        /// Frame-0 speed-mining setup for 8-worker starts.
        ///
        /// Each worker is given one MOVE (harvest point) followed by a SMART (mineral tag) cue.
        /// The MOVE shapes the worker's approach angle so the SMART cue fires the gather from the optimal
        /// position instead of letting SC2's default behavior pile all workers onto the closest mineral.
        ///
        /// Live data comes from the Observation snapshot (Globals.CurrentObservation) populated by
        /// ObservationManager. Workers and minerals are paired by closest unassigned mineral, so this
        /// runs without depending on TeamPatchAssignments being populated.
        /// </summary>
        private IEnumerable<SC2APIProtocol.Action> BuildFrame0EightWorkerSpeedMine(
            MawBaseLocationData mapData,
            int startIndex,
            IReadOnlyList<WorkerEntryDto> workerEntries,
            int relativeFrame)
        {
            var commands = new List<SC2APIProtocol.Action>();

            Console.WriteLine($"[CCA Frame0] Entered. mapData!=null:{mapData!=null} startIndex={startIndex} Settings.WorkerCount={Settings.WorkerCount}");

            if (mapData == null || startIndex < 0) return commands;

            var townhall = ResolveTownhall(mapData, startIndex);
            Console.WriteLine($"[CCA Frame0] townhall={(townhall!=null ? $"({townhall.X:F2},{townhall.Y:F2})" : "null")}");
            if (townhall == null) return commands;

            var snapshot = Globals.CurrentObservation;
            Console.WriteLine($"[CCA Frame0] snapshot={(snapshot!=null ? "ok" : "null")} SelfUnits={snapshot?.SelfUnits?.Count ?? 0} AvailableWorkers={snapshot?.AvailableWorkers?.Count ?? 0} Minerals={snapshot?.Minerals?.Count ?? 0}");
            if (snapshot == null) return commands;

            // AvailableWorkers is the single source of truth for live, controllable workers.
            // SelfUnits supplies only the current position for each authoritative tag.
            var liveWorkers = (workerEntries ?? Array.Empty<WorkerEntryDto>())
                .Where(entry => entry != null && entry.UnitTag != 0 && entry.Position != null)
                .Select(entry => (Tag: entry.UnitTag, Position: entry.Position))
                .ToList();

            Console.WriteLine($"[CCA Frame0] liveWorkers.Count={liveWorkers.Count}");
            if (liveWorkers.Count == 0) return commands;

            // Use only the mineral set stored for this start index. Do not select the
            // first non-empty start: that can send workers to another spawn's mineral line.
            var persistedMinerals = mapData.StartingMinerals != null
                && startIndex >= 0
                && startIndex < mapData.StartingMinerals.Count
                ? mapData.StartingMinerals[startIndex]
                : null;
            if (persistedMinerals == null || persistedMinerals.Count == 0)
            {
                // Backward-compatible reads of older map data are safe only when the
                // corresponding ordered list has the same valid start index.
                persistedMinerals = mapData.OrderedMainMinerals != null
                    && startIndex >= 0
                    && startIndex < mapData.OrderedMainMinerals.Count
                    ? mapData.OrderedMainMinerals[startIndex]
                    : null;
            }
            // Index is the persisted one-based greedy-chain position: Index 1 = M[1]
            // (W1/Teal side), Index 8 = M[8] (W12/Yellow side). Observation order is
            // never used to resolve a mineral.
            var mineralsByIndex = persistedMinerals?
                .Where(m => m != null && m.Position != null)
                .OrderBy(m => m.Index)
                .ToList() ?? new List<OrderedMineral>();

            Console.WriteLine($"[CCA Frame0] Persisted greedy minerals={mineralsByIndex.Count}; expected 8 for 8-worker start.");
            for (int mi = 0; mi < mineralsByIndex.Count; mi++)
            {
                var mineral = mineralsByIndex[mi];
                Console.WriteLine($"  M[{mineral.Index}] (stored Index={mineral.Index}) position=({mineral.Position.X:F2},{mineral.Position.Y:F2}) cachedTag={mineral.UnitTag}");
            }
            if (mineralsByIndex.Count < liveWorkers.Count) return commands;

            // Match the persisted harvest geometry: the worker center remains outside the
            // mineral footprint with the established 1.5u center offset.
            const float mineralOffset = 1.5f;

            // Greedy worker chain: anchor at mineral COM, then chain to the closest
            // unvisited worker. The traversal begins at W8/W12 and descends to W1;
            // CCA pairs that traversal with the high-to-low mineral chain. Observation
            // order is never semantic.
            var mineralCom = mapData.MineralCenterOfMass != null
                && startIndex >= 0
                && startIndex < mapData.MineralCenterOfMass.Count
                ? mapData.MineralCenterOfMass[startIndex]
                : null;
            if (mineralCom == null) return commands;

            var workerTuples = liveWorkers
                .Select(worker => (worker.Tag, worker.Position.X, worker.Position.Y, worker.Position.Z, (uint)UnitTypes.ZERG_DRONE));
            var greedyWorkers = WorkerLabelChainHelper.BuildGreedyWorkerEntries(workerTuples, mineralCom, null);

            Console.WriteLine("[CCA Frame0] Greedy worker chain and required targets:");
            for (int wi = 0; wi < greedyWorkers.Count && wi < mineralsByIndex.Count; wi++)
            {
                var workerNumber = greedyWorkers.Count - wi;
                var targetIndex = mineralsByIndex.Count - 1 - wi;
                var targetMineral = mineralsByIndex[targetIndex];
                var worker = greedyWorkers[wi];
                var mineralTag = ResolveLiveMineralTag(targetMineral, snapshot);
                var returnPoint = targetMineral.ReturnPoint != null
                    && (targetMineral.ReturnPoint.X != 0f || targetMineral.ReturnPoint.Y != 0f)
                    ? targetMineral.ReturnPoint
                    : ComputeReturnCargoPoint(townhall, targetMineral.Position);
                var harvestPoint = targetMineral.HarvestPoint != null
                    && (targetMineral.HarvestPoint.X != 0f || targetMineral.HarvestPoint.Y != 0f)
                    ? new Point2D { X = targetMineral.HarvestPoint.X, Y = targetMineral.HarvestPoint.Y }
                    : CalculateFrame0HarvestPoint(
                        worker.Position,
                        returnPoint,
                        targetMineral.Position,
                        mineralOffset);

                var targetFinalLabel = targetMineral.FinalLabel ?? string.Empty;
                var workerFinalLabel = targetFinalLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase)
                    ? $"{targetFinalLabel.Substring(0, targetFinalLabel.Length - 1)}1"
                    : targetFinalLabel.EndsWith("B", StringComparison.OrdinalIgnoreCase)
                        ? $"{targetFinalLabel.Substring(0, targetFinalLabel.Length - 1)}2"
                        : $"W{workerNumber}";
                var workerDisplayLabel = $"{workerNumber}-{workerFinalLabel}";
                var mineralDisplayLabel = $"{targetMineral.Index}-{targetFinalLabel}";
                Console.WriteLine($"  {workerDisplayLabel} tag={worker.UnitTag} -> {mineralDisplayLabel} storedIndex={targetMineral.Index} mineralTag={mineralTag} position=({targetMineral.Position.X:F2},{targetMineral.Position.Y:F2})");
                if (mineralTag == 0) continue;

                    commands.AddRange(BuildSharkyGatherSequence(
                        worker.UnitTag,
                        mineralTag,
                        targetMineral,
                        harvestPoint,
                        relativeFrame));
            }

            Console.WriteLine($"[CCA Frame0] Issued {commands.Count} actions total.");
            return commands;
        }
        private IEnumerable<SC2APIProtocol.Action> Stop(ulong tag)
        {
            if (tag == 0) return Array.Empty<SC2APIProtocol.Action>();

            var workerLabel = _workerLabelService?.GetLabel(tag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMAND3] phase=CCA worker={tag} Label={workerLabel} command=STOP queued=false");
            var command = new ActionRawUnitCommand
            {
                //AbilityId = (int)Abilities.STOP
            };
            command.UnitTags.Add(tag);

            return new List<SC2APIProtocol.Action>
    {
        new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } }
    };
        }

        private static Vector2Dto ResolveTownhall(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData?.StartingTownHall == null) return null;
            if (startIndex < 0 || startIndex >= mapData.StartingTownHall.Length) return null;
            return mapData.StartingTownHall[startIndex];
        }

        private static Vector2Dto ComputeReturnCargoPoint(Vector2Dto townhall, Vector2Dto mineral)
        {
            if (mineral == null) return townhall;
            var dx = mineral.X - townhall.X;
            var dy = mineral.Y - townhall.Y;
            var dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist <= 0.0001f) return new Vector2Dto(townhall.X + 2.75f, townhall.Y);
            return new Vector2Dto(
                townhall.X + (dx / dist) * 2.75f,
                townhall.Y + (dy / dist) * 2.75f);
        }

        private static ulong ResolveLiveMineralTag(OrderedMineral mineral, ObservationSnapshotDto snapshot)
        {
            if (mineral == null || mineral.Position == null) return 0;

            if (snapshot?.Minerals != null)
            {
                ulong bestTag = 0;
                var bestDistanceSquared = float.MaxValue;
                foreach (var liveMineral in snapshot.Minerals.Values)
                {
                    if (liveMineral == null || liveMineral.UnitTag == 0 || liveMineral.Position == null) continue;
                    var dx = liveMineral.Position.X - mineral.Position.X;
                    var dy = liveMineral.Position.Y - mineral.Position.Y;
                    var distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared < bestDistanceSquared)
                    {
                        bestDistanceSquared = distanceSquared;
                        bestTag = liveMineral.UnitTag;
                    }
                }

                if (bestTag != 0 && bestDistanceSquared < 4f) return bestTag;
            }

            return mineral.UnitTag;
        }

        /// <summary>
        /// Compute the frame-0 harvest point on the mineral circle using the user's geometric construction:
        /// the worker-facing intersection of the line from midpoint(worker, returnCargoPoint) to mineralCenter.
        /// Reusable for RL Matrix mineral-target optimization.
        /// </summary>
        /// <param name="workerPosition">Worker's current X/Y (typically its starting position).</param>
        /// <param name="returnCargoPoint">Mineral's ReturnPoint (already mapped by InitialMapData).</param>
        /// <param name="mineralPosition">Mineral's center X/Y.</param>
        /// <param name="mineralOffset">Distance from mineral center at which the worker stands
        /// (1.0 mineral radius + 0.5 worker standoff = 1.5 for normal minerals).</param>
        internal static Point2D CalculateFrame0HarvestPoint(
            Vector2Dto workerPosition,
            Vector2Dto returnCargoPoint,
            Vector2Dto mineralPosition,
            float mineralOffset)
        {
            // Midpoint of (worker, return cargo point).
            var midX = (workerPosition.X + returnCargoPoint.X) * 0.5f;
            var midY = (workerPosition.Y + returnCargoPoint.Y) * 0.5f;

            // Direction from midpoint toward mineral center.
            var dirX = mineralPosition.X - midX;
            var dirY = mineralPosition.Y - midY;
            var dirLen = MathF.Sqrt(dirX * dirX + dirY * dirY);

            if (dirLen <= 0.0001f)
            {
                // Degenerate: midpoint coincides with mineral center.
                // Fall back to a position on +X so the worker has somewhere to walk.
                return new Point2D
                {
                    X = mineralPosition.X + mineralOffset,
                    Y = mineralPosition.Y
                };
            }

            var unitX = dirX / dirLen;
            var unitY = dirY / dirLen;

            // Worker-facing intersection of the line (midpoint -> mineral) with the mineral circle.
            return new Point2D
            {
                X = mineralPosition.X - unitX * mineralOffset,
                Y = mineralPosition.Y - unitY * mineralOffset
            };
        }

        private static bool IsSharkySequenceFrame(int relativeFrame)
        {
            return relativeFrame == 0 || relativeFrame == 1 || relativeFrame == 14;
        }

        private IEnumerable<SC2APIProtocol.Action> BuildSharkyGatherSequence(
            ulong workerTag,
            ulong mineralTag,
            OrderedMineral mineral,
            Point2D harvestPoint,
            int relativeFrame)
        {
            if (workerTag == 0 || mineralTag == 0 || mineral == null || harvestPoint == null)
            {
                return Array.Empty<SC2APIProtocol.Action>();
            }

            var initialPoint = mineral.SmHarvestPoint != null
                && (mineral.SmHarvestPoint.X != 0f || mineral.SmHarvestPoint.Y != 0f)
                ? new Point2D { X = mineral.SmHarvestPoint.X, Y = mineral.SmHarvestPoint.Y }
                : harvestPoint;
            var midPoint = new Point2D
            {
                X = (initialPoint.X + harvestPoint.X) * 0.5f,
                Y = (initialPoint.Y + harvestPoint.Y) * 0.5f
            };
            var returnPoint = mineral.SmReturnPoint != null
                && (mineral.SmReturnPoint.X != 0f || mineral.SmReturnPoint.Y != 0f)
                ? new Point2D { X = mineral.SmReturnPoint.X, Y = mineral.SmReturnPoint.Y }
                : mineral.ReturnPoint == null
                    ? null
                    : new Point2D { X = mineral.ReturnPoint.X, Y = mineral.ReturnPoint.Y };

            var actions = new List<SC2APIProtocol.Action>();
            switch (relativeFrame)
            {
                case 0:
                    actions.AddRange(MoveTo(workerTag, harvestPoint, false));
                    break;
                case 1:
                    actions.AddRange(MoveTo(workerTag, midPoint, false));
                    break;
            }
            return actions;
        }

        private IEnumerable<SC2APIProtocol.Action> HarvestGatherTo(ulong tag, ulong targetTag, bool queued)
        {
            if (tag == 0 || targetTag == 0) return Array.Empty<SC2APIProtocol.Action>();

            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.HARVEST_GATHER,
                TargetUnitTag = targetTag,
                QueueCommand = queued
            };
            command.UnitTags.Add(tag);
            return new[]
            {
                new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } }
            };
        }

        private IEnumerable<SC2APIProtocol.Action> MoveTo(ulong tag, Point2D point, bool queued)
        {
            if (tag == 0 || point == null) return Array.Empty<SC2APIProtocol.Action>();

            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.MOVE,
                TargetWorldSpacePos = point,
                QueueCommand = queued
            };
            command.UnitTags.Add(tag);
            return new[]
            {
                new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } }
            };
        }

        private IEnumerable<SC2APIProtocol.Action> SmartToImmediate(ulong tag, ulong targetTag)
        {
            if (tag == 0 || targetTag == 0) return Array.Empty<SC2APIProtocol.Action>();

            var workerLabel = _workerLabelService?.GetLabel(tag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMAND5] phase=CCA frame=55 worker={tag} Label={workerLabel} command=SMART targetTag={targetTag} queued=false");
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.SMART,
                TargetUnitTag = targetTag,
                QueueCommand = false
            };
            command.UnitTags.Add(tag);
            return new List<SC2APIProtocol.Action>
            {
                new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } }
            };
        }

        private IEnumerable<SC2APIProtocol.Action> SmartTo(ulong tag, ulong targetTag)
        {
            if (tag == 0 || targetTag == 0) return Array.Empty<SC2APIProtocol.Action>();

            // Sharky speed-mining pattern: GATHER/SMART is QUEUED so MOVE can run first and shape
            // the worker's approach angle. See Sharky/MicroTasks/Mining/MineralMiner.cs:73-77 and
            // UnitCommander.Order at Sharky/Unit/UnitCommander.cs:78-147 (QueueCommand=true).
            var workerLabel = _workerLabelService?.GetLabel(tag) ?? string.Empty;
            Console.WriteLine($"[MINING COMMAND4] phase=CCA worker={tag} Label={workerLabel} command=SMART targetTag={targetTag} queued=true");
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.SMART,
                TargetUnitTag = targetTag,
                QueueCommand = true
            };
            command.UnitTags.Add(tag);
            return new List<SC2APIProtocol.Action>
            {
                new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } }
            };
        }
    }

}
