using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BabySharkBot.Services;

#nullable enable

namespace BabySharkBot.Setup
{
    public static class TeamLabelRegistrationHelper
    {
        private const string TeamAssignmentsReadyFlag = "TeamAssignmentsReady";

        public static void RegisterTeamLabels(
            MawBaseLocationData mapData,
            List<List<OrderedMineral>> orderedMainMinerals,
            List<List<WorkerEntryDto>> multiStartingUnits,
            List<Vector2Dto> multiMineralCenterOfMass,
            WorkerLabelService? workerLabelService,
            List<List<TeamPatchAssignmentDto>>? targetTeamPatchAssignments = null)
        {
            if (mapData == null || orderedMainMinerals == null || multiStartingUnits == null)
            {
                return;
            }

            targetTeamPatchAssignments ??= mapData.TeamPatchAssignments;
            EnsureAssignmentCapacity(targetTeamPatchAssignments, orderedMainMinerals.Count);

            for (var startIndex = 0; startIndex < orderedMainMinerals.Count; startIndex++)
            {
                var minerals = orderedMainMinerals[startIndex];
                var workers = multiStartingUnits.Count > startIndex ? multiStartingUnits[startIndex] : null;
                var com = multiMineralCenterOfMass != null && multiMineralCenterOfMass.Count > startIndex
                    ? multiMineralCenterOfMass[startIndex]
                    : null;

                if (minerals == null || workers == null || workers.Count == 0 || com == null)
                {
                    continue;
                }

                EnsureTeamLabelsForStart(mapData, startIndex, minerals, workers, com, workerLabelService, targetTeamPatchAssignments);
            }
        }

        public static List<TeamPatchAssignmentDto> EnsureTeamLabelsForStart(
            MawBaseLocationData mapData,
            int startIndex,
            List<OrderedMineral> orderedMinerals,
            List<WorkerEntryDto> workers,
            Vector2Dto mineralCenterOfMass,
            WorkerLabelService? workerLabelService,
            List<List<TeamPatchAssignmentDto>>? targetTeamPatchAssignments = null)
        {
            if (mapData == null || startIndex < 0 || orderedMinerals == null || workers == null || mineralCenterOfMass == null)
            {
                return new List<TeamPatchAssignmentDto>();
            }

            if (!HasInitialWorkerLabels(workers))
            {
                return new List<TeamPatchAssignmentDto>();
            }

            targetTeamPatchAssignments ??= mapData.TeamPatchAssignments;
            EnsureAssignmentCapacity(targetTeamPatchAssignments, startIndex + 1);

            if (IsTeamAssignmentsReady(mapData, startIndex, workers.Count) && targetTeamPatchAssignments[startIndex] != null && targetTeamPatchAssignments[startIndex].Count > 0)
            {
                return targetTeamPatchAssignments[startIndex];
            }

            var assignments = BuildAssignmentsForStart(mapData, startIndex, orderedMinerals, workers, mineralCenterOfMass, workerLabelService);
            targetTeamPatchAssignments[startIndex] = assignments;
            ApplySpawnFlags(mapData, startIndex, assignments);
            MarkTeamAssignmentsReady(mapData, startIndex, assignments, workers.Count);
            return assignments;
        }

        public static List<TeamPatchAssignmentDto> TryGetTeamLabelsForStart(
            MawBaseLocationData mapData,
            int startIndex,
            List<List<TeamPatchAssignmentDto>>? targetTeamPatchAssignments = null)
        {
            if (mapData == null || startIndex < 0 || !IsTeamAssignmentsReady(mapData, startIndex, Settings.WorkerCount))
            {
                return new List<TeamPatchAssignmentDto>();
            }

            targetTeamPatchAssignments ??= mapData.SecondaryTeamPatchAssignments;
            if (targetTeamPatchAssignments.Count > startIndex && targetTeamPatchAssignments[startIndex] != null && targetTeamPatchAssignments[startIndex].Count > 0)
            {
                return targetTeamPatchAssignments[startIndex];
            }

            if (mapData.TeamPatchAssignments.Count > startIndex && mapData.TeamPatchAssignments[startIndex] != null)
            {
                return mapData.TeamPatchAssignments[startIndex];
            }

            return new List<TeamPatchAssignmentDto>();
        }

        public static void RegisterTeamLabelsForStart(
            MawBaseLocationData mapData,
            int startIndex,
            List<OrderedMineral> orderedMinerals,
            List<WorkerEntryDto> workers,
            Vector2Dto mineralCenterOfMass,
            WorkerLabelService? workerLabelService,
            List<List<TeamPatchAssignmentDto>>? targetTeamPatchAssignments = null)
        {
            if (mapData == null || startIndex < 0 || orderedMinerals == null || workers == null || mineralCenterOfMass == null)
            {
                return;
            }

            targetTeamPatchAssignments ??= mapData.SecondaryTeamPatchAssignments;
            EnsureAssignmentCapacity(targetTeamPatchAssignments, startIndex + 1);

            var assignments = BuildAssignmentsForStart(mapData, startIndex, orderedMinerals, workers, mineralCenterOfMass, workerLabelService);
            targetTeamPatchAssignments[startIndex] = assignments;
            ApplySpawnFlags(mapData, startIndex, assignments);
            MarkTeamAssignmentsReady(mapData, startIndex, assignments, workers.Count);
        }

        private static List<TeamPatchAssignmentDto> BuildAssignmentsForStart(
            MawBaseLocationData mapData,
            int startIndex,
            List<OrderedMineral> minerals,
            List<WorkerEntryDto> workers,
            Vector2Dto com,
            WorkerLabelService? workerLabelService)
        {
            var result = new List<TeamPatchAssignmentDto>();
            if (minerals == null || workers == null || com == null)
            {
                return result;
            }

            var teamLayouts = BuildExplicitTeamLayouts(minerals, workers);

            foreach (var layout in teamLayouts)
            {
                var mineralPair = layout.MineralPair;
                var teamWorkers = layout.TeamWorkers;
                var teamNum = layout.TeamNumber;

                if (mineralPair.Count != 2 || teamWorkers.Count == 0)
                {
                    continue;
                }

                ApplyMineralFinalLabels(mapData, startIndex, mineralPair, teamNum, workers.Count);
                if (workers.Count == 12)
                {
                    ApplyTwelveWorkerFinalLabels(mapData, startIndex, teamNum, teamWorkers, mineralPair, workerLabelService);
                }
                else
                {
                    // The 8-worker opening keeps the existing direct distance-based role assignment.
                    var actualNearMineral = mineralPair[0].IsNear ? mineralPair[0] : mineralPair[1];
                    var actualFarMineral = mineralPair[0].IsNear ? mineralPair[1] : mineralPair[0];
                    ApplyWorkerFinalLabels(mapData, startIndex, GetTeamPrefix(teamNum, workers.Count), teamWorkers, actualNearMineral, actualFarMineral, workerLabelService);
                }

                var hatcheryPos = mapData.StartingTownHall[startIndex];
                var jitPoints = CalculateJitPoints(mineralPair[0], mineralPair[1], hatcheryPos);
                var teamId = $"{hatcheryPos.X:F1}_{hatcheryPos.Y:F1}_T{teamNum}";

                result.Add(new TeamPatchAssignmentDto
                {
                    TeamNumber = teamNum,
                    NearLabel = $"{GetTeamPrefix(teamNum, workers.Count)}A",
                    FarLabel = $"{GetTeamPrefix(teamNum, workers.Count)}B",
                    TeamId = teamId,
                    Workers = teamWorkers.ToList(),
                    Minerals = mineralPair.ToList(),
                    JitReturnPoint = jitPoints.ReturnPoint,
                    JitWaitPoint = jitPoints.WaitPoint
                });
            }

            return result;
        }

        private static (Vector2Dto ReturnPoint, Vector2Dto WaitPoint) CalculateJitPoints(OrderedMineral mA, OrderedMineral mB, Vector2Dto townhall)
        {
            if (mA?.Position == null || mB?.Position == null || townhall == null)
            {
                return (new Vector2Dto(), new Vector2Dto());
            }

            // Return point: Average of A and B, then projected to townhall radius (approx 2.75u)
            var avgX = (mA.Position.X + mB.Position.X) * 0.5f;
            var avgY = (mA.Position.Y + mB.Position.Y) * 0.5f;

            var dirX = avgX - townhall.X;
            var dirY = avgY - townhall.Y;
            var mag = MathF.Sqrt(dirX * dirX + dirY * dirY);
            
            // Townhall radius is roughly 2.75. We want to return at the edge.
            var returnX = townhall.X + (dirX / mag) * 2.8f;
            var returnY = townhall.Y + (dirY / mag) * 2.8f;

            // Wait point: 1.5u away from the return point towards the minerals
            var waitX = returnX + (dirX / mag) * 1.5f;
            var waitY = returnY + (dirY / mag) * 1.5f;

            return (new Vector2Dto(returnX, returnY), new Vector2Dto(waitX, waitY));
        }

        private static List<TeamLayout> BuildExplicitTeamLayouts(List<OrderedMineral> minerals, List<WorkerEntryDto> workers)
        {
            var teams = new List<TeamLayout>();

            Console.WriteLine($"TeamLabelRegistrationHelper: Building layouts for {workers?.Count ?? 0} workers and {minerals?.Count ?? 0} minerals.");

            // If we have a full 12-worker spawn, use the correct mapping from architecture docs:
            if (workers != null && workers.Count == 12)
            {
                // Team 1: Y (M1, M2) -> W9, W10, W11
                // Team 2: B (M3, M4) -> W7, W8, W12
                // Team 3: S (M5, M6) -> W5, W6, W1
                // Team 4: T (M7, M8) -> W2, W3, W4
                AddTeamIfPossible(teams, minerals, workers, 7, 6, new[] { "W9", "W10", "W11" }, 1);
                AddTeamIfPossible(teams, minerals, workers, 5, 4, new[] { "W7", "W8", "W12" }, 2);
                AddTeamIfPossible(teams, minerals, workers, 2, 3, new[] { "W5", "W6", "W1" }, 3);
                AddTeamIfPossible(teams, minerals, workers, 0, 1, new[] { "W2", "W3", "W4" }, 4);
            }
            else
            {
                // For 8-worker starts, use the corresponding 2-worker-per-team mapping:
                // Team 1: O (M1, M2) -> W7, W8
                // Team 2: R (M3, M4) -> W5, W6
                // Team 3: P (M5, M6) -> W3, W4
                // Team 4: G (M7, M8) -> W1, W2
                AddTeamIfPossible(teams, minerals, workers, 7, 6, new[] { "W7", "W8" }, 1);
                AddTeamIfPossible(teams, minerals, workers, 5, 4, new[] { "W5", "W6" }, 2);
                AddTeamIfPossible(teams, minerals, workers, 2, 3, new[] { "W3", "W4" }, 3);
                AddTeamIfPossible(teams, minerals, workers, 0, 1, new[] { "W1", "W2" }, 4);
            }


            foreach (var team in teams)
            {
                Console.WriteLine($"  - Team {team.TeamNumber}: Minerals=[{string.Join(",", team.MineralPair.Select(m => m.Index))}], Workers=[{string.Join(",", team.TeamWorkers.Select(w => w.Label))}]");
            }

            return teams;
        }

        private static bool HasInitialWorkerLabels(IEnumerable<WorkerEntryDto> workers)
        {
            var labeledWorkers = workers?.Where(w => w != null).ToList() ?? new List<WorkerEntryDto>();
            if (labeledWorkers.Count == 0)
            {
                return false;
            }

            return labeledWorkers.All(w => !string.IsNullOrWhiteSpace(w.Label) && w.Label.StartsWith("W", StringComparison.OrdinalIgnoreCase));
        }

        private static void AddTeamIfPossible(
            List<TeamLayout> teams,
            List<OrderedMineral> minerals,
            List<WorkerEntryDto> workers,
            int m1Index,
            int m2Index,
            string[] workerLabels,
            int teamNumber)
        {
            if (minerals.Count <= m2Index)
            {
                return;
            }

            var teamMinerals = new List<OrderedMineral>();
            if (minerals[m1Index] != null)
            {
                teamMinerals.Add(minerals[m1Index]);
            }

            if (minerals[m2Index] != null)
            {
                teamMinerals.Add(minerals[m2Index]);
            }

            var teamWorkers = new List<WorkerEntryDto>();
            foreach (var workerLabel in workerLabels)
            {
                var worker = workers.FirstOrDefault(w => string.Equals(w?.StartLabel ?? w?.Label, workerLabel, StringComparison.OrdinalIgnoreCase));
                if (worker != null)
                {
                    teamWorkers.Add(worker);
                }
            }

            if (teamMinerals.Count == 2 && teamWorkers.Count > 0)
            {
                teams.Add(new TeamLayout(teamMinerals, teamWorkers, teamNumber));
            }
        }

        private static void ApplySpawnFlags(MawBaseLocationData mapData, int startIndex, List<TeamPatchAssignmentDto> assignments)
        {
            if (mapData == null)
            {
                return;
            }

            if (!mapData.AssignmentFlagsByStart.TryGetValue(startIndex, out var flags))
            {
                flags = new Dictionary<string, bool>();
                mapData.AssignmentFlagsByStart[startIndex] = flags;
            }

            foreach (var assignment in assignments)
            {
                if (assignment == null || assignment.Minerals.Count < 2)
                {
                    continue;
                }

                var prefix = assignment.TeamNumber switch
                {
                    1 => "Y",
                    2 => "B",
                    3 => "S",
                    4 => "T",
                    _ => string.Empty
                };

                if (string.IsNullOrEmpty(prefix))
                {
                    continue;
                }

                var mA = assignment.Minerals[0];
                var mB = assignment.Minerals[1];
                if (mA == null || mB == null)
                {
                    continue;
                }

                flags[$"{prefix}M{mA.Index}Is{prefix}A"] = true;
                flags[$"{prefix}M{mB.Index}Is{prefix}B"] = true;
            }
        }

        private static void ApplyMineralFinalLabels(MawBaseLocationData mapData, int startIndex, List<OrderedMineral> mineralPair, int teamNumber, int workerCount)
        {
            if (mineralPair == null || mineralPair.Count < 2)
            {
                return;
            }

            var prefix = GetTeamPrefix(teamNumber, workerCount);
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return;
            }

            var first = mineralPair[0];
            var second = mineralPair[1];
            if (first == null || second == null)
            {
                return;
            }

            OrderedMineral nearMineral;
            OrderedMineral farMineral;

            if (first.IsNear && !second.IsNear)
            {
                nearMineral = first;
                farMineral = second;
            }
            else if (second.IsNear && !first.IsNear)
            {
                nearMineral = second;
                farMineral = first;
            }
            else
            {
                if (first.Size != second.Size)
                {
                    nearMineral = first.Size > second.Size ? first : second;
                }
                else if (first.Resources != second.Resources)
                {
                    nearMineral = first.Resources >= second.Resources ? first : second;
                }
                else
                {
                    // FIX: Changed >= to <= so the closer mineral is correctly labeled "near" (A)
                    nearMineral = first.DistanceToTownhall <= second.DistanceToTownhall
                        ? first
                        : second;
                }

                farMineral = nearMineral == first ? second : first;
            }

            nearMineral.IsNear = true;
            nearMineral.IsFar = false;
            farMineral.IsNear = false;
            farMineral.IsFar = true;

            SetFinalLabel(nearMineral, $"{prefix}A");
            SetFinalLabel(farMineral, $"{prefix}B");
            SetMineralIdentityFlags(mapData, startIndex, prefix, first, second, farMineral);

            mapData.MineralFinalLabelsByPosition ??= new Dictionary<string, string>();
            if (nearMineral.Position != null)
            {
                mapData.MineralFinalLabelsByPosition[$"{nearMineral.Position.X:F2},{nearMineral.Position.Y:F2}"] = $"{prefix}A";
            }

            if (farMineral.Position != null)
            {
                mapData.MineralFinalLabelsByPosition[$"{farMineral.Position.X:F2},{farMineral.Position.Y:F2}"] = $"{prefix}B";
            }

        }

        private static void SetMineralIdentityFlags(MawBaseLocationData mapData, int startIndex, string prefix, OrderedMineral first, OrderedMineral second, OrderedMineral farMineral)
        {
            if (mapData == null || string.IsNullOrWhiteSpace(prefix))
            {
                return;
            }

            if (!mapData.AssignmentFlagsByStart.TryGetValue(startIndex, out var flags))
            {
                flags = new Dictionary<string, bool>();
                mapData.AssignmentFlagsByStart[startIndex] = flags;
            }

            if (prefix == "Y")
            {
                flags["M1IsFar"] = IsFarMineral(first, farMineral);
            }
            else if (prefix == "T")
            {
                flags["M8IsFar"] = IsFarMineral(second, farMineral);
            }
        }

        private static bool IsFarMineral(OrderedMineral candidate, OrderedMineral farMineral)
        {
            if (candidate == null)
            {
                return false;
            }

            if (candidate.IsFar)
            {
                return true;
            }

            return ReferenceEquals(candidate, farMineral);
        }

        private static void ApplyTwelveWorkerFinalLabels(
            MawBaseLocationData mapData,
            int startIndex,
            int teamNumber,
            List<WorkerEntryDto> teamWorkers,
            List<OrderedMineral> mineralPair,
            WorkerLabelService? workerLabelService)
        {
            if (teamWorkers == null || teamWorkers.Count != 3 || mineralPair == null || mineralPair.Count != 2)
            {
                return;
            }

            var prefix = GetTeamPrefix(teamNumber, 12);
            var nearMineral = mineralPair.FirstOrDefault(m => m.IsNear) ?? mineralPair[0];
            var farMineral = mineralPair.FirstOrDefault(m => !m.IsNear && m != nearMineral) ?? mineralPair[1];
            var remaining = teamWorkers.ToList();

            if (teamNumber == 1 || teamNumber == 4)
            {
                // Teal and Yellow require an adjacent 1/3 pair for CCA push-repell.
                var pair = FindClosestAdjacentPair(remaining);
                if (pair != null)
                {
                    var primary = pair.Value.First;
                    var support = pair.Value.Second;
                    if (DistanceSquared(primary.Position, nearMineral.Position) > DistanceSquared(support.Position, nearMineral.Position))
                    {
                        (primary, support) = (support, primary);
                    }

                    AssignFinalLabel(primary, $"{prefix}1", workerLabelService);
                    AssignFinalLabel(support, $"{prefix}3", workerLabelService);
                    remaining.Remove(primary);
                    remaining.Remove(support);
                }

                foreach (var worker in remaining)
                {
                    AssignFinalLabel(worker, $"{prefix}2", workerLabelService);
                }

                return;
            }

            // Salmon and Blue keep the fixed middle-chain targets. Their suffix is
            // derived from whether that target is the team's A or B mineral.
            foreach (var worker in teamWorkers)
            {
                if (worker.StartLabel == "W1" || worker.StartLabel == "W12")
                {
                    AssignFinalLabel(worker, $"{prefix}3", workerLabelService);
                    continue;
                }

                var pairPosition = teamNumber switch
                {
                    2 when worker.StartLabel == "W7" => 1,
                    2 when worker.StartLabel == "W8" => 0,
                    3 when worker.StartLabel == "W5" => 0,
                    3 when worker.StartLabel == "W6" => 1,
                    _ => -1
                };

                if (pairPosition < 0 || pairPosition >= mineralPair.Count)
                {
                    Console.WriteLine($"TeamLabelRegistrationHelper: [WARN] No fixed middle-chain target for {worker.StartLabel} in team {prefix}");
                    continue;
                }

                var target = mineralPair[pairPosition];
                var suffix = target?.FinalLabel?.EndsWith("A", StringComparison.OrdinalIgnoreCase) == true ? "1" : "2";
                AssignFinalLabel(worker, $"{prefix}{suffix}", workerLabelService);
            }
        }

        private static (WorkerEntryDto First, WorkerEntryDto Second)? FindClosestAdjacentPair(List<WorkerEntryDto> workers)
        {
            if (workers == null || workers.Count < 2)
            {
                return null;
            }

            var pair = workers
                .SelectMany((first, firstIndex) => workers.Skip(firstIndex + 1).Select(second => (first, second)))
                .OrderBy(candidate => DistanceSquared(candidate.first.Position, candidate.second.Position))
                .FirstOrDefault();

            return pair.first == null || pair.second == null ? null : (pair.first, pair.second);
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

        private static void ApplyWorkerFinalLabels(
            MawBaseLocationData mapData,
            int startIndex,
            string prefix,
            List<WorkerEntryDto> teamWorkers,
            OrderedMineral nearMineral,
            OrderedMineral farMineral,
            WorkerLabelService? workerLabelService)
        {
            if (teamWorkers == null || teamWorkers.Count == 0 || nearMineral?.Position == null)
            {
                return;
            }

            if (prefix == "Y")
            {
                var y2Source = "W4";
                var m1IsFar = IsFarMineralFlagSet(mapData, startIndex, "M1IsFar");
                Console.WriteLine($"TeamLabelRegistrationHelper: start[{startIndex}] M1IsFar={m1IsFar}");
                if (m1IsFar)
                {
                    y2Source = "W2";
                }
                AssignWorkerFinalLabel(teamWorkers, y2Source, "Y2", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "Y2" }, new[] { "Y1", "Y3" }, workerLabelService);
                SetNoPushFlag(mapData, "YellowNoPush", teamWorkers, nearMineral, "Y1", "Y3");
            }
            else if (prefix == "T")
            {
                var t2Source = IsFarMineralFlagSet(mapData, startIndex, "M8IsFar")
                    ? "W11"
                    : "W9";
                AssignWorkerFinalLabel(teamWorkers, t2Source, "T2", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "T2" }, new[] { "T1", "T3" }, workerLabelService);
                SetNoPushFlag(mapData, "TealNoPush", teamWorkers, nearMineral, "T1", "T3");
            }
            else if (prefix == "B")
            {
                AssignWorkerFinalLabel(teamWorkers, "W1", "B3", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "B3" }, new[] { "B1", "B2" }, workerLabelService);
                SetNoPushFlag(mapData, "BlueNoPush", teamWorkers, nearMineral, "B1", "B2");
            }
            else if (prefix == "S")
            {
                AssignWorkerFinalLabel(teamWorkers, "W12", "S3", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "S3" }, new[] { "S1", "S2" }, workerLabelService);
                SetNoPushFlag(mapData, "SalmonNoPush", teamWorkers, nearMineral, "S1", "S2");
            }
            else if (prefix == "G" || prefix == "P" || prefix == "O" || prefix == "R")
            {
                // 8 worker start logic: 1 is closest to near mineral, 2 is closest to far mineral
                var sortedNear = teamWorkers.OrderBy(w => Vector2.Distance(new Vector2(w.Position.X, w.Position.Y), new Vector2(nearMineral.Position.X, nearMineral.Position.Y))).ToList();
                var w1 = sortedNear[0];
                var w2 = teamWorkers.FirstOrDefault(w => w != w1);

                AssignFinalLabel(w1, $"{prefix}1", workerLabelService);
                if (w2 != null) AssignFinalLabel(w2, $"{prefix}2", workerLabelService);
            }
        }

        private static bool IsFarMineralFlagSet(MawBaseLocationData mapData, int startIndex, string flagName)
        {
            return mapData != null
                && mapData.AssignmentFlagsByStart.TryGetValue(startIndex, out var flags)
                && flags.TryGetValue(flagName, out var value)
                && value;
        }

        private static void AssignWorkerFinalLabel(List<WorkerEntryDto> teamWorkers, string originalLabel, string finalLabel, WorkerLabelService? workerLabelService)
        {
            var worker = teamWorkers.FirstOrDefault(w => string.Equals(w?.StartLabel ?? w?.Label, originalLabel, StringComparison.OrdinalIgnoreCase));
            if (worker == null)
            {
                Console.WriteLine($"TeamLabelRegistrationHelper: [WARN] Could not find worker with StartLabel '{originalLabel}' to assign {finalLabel}");
                return;
            }

            worker.FinalLabel = finalLabel;
            worker.Label = finalLabel;

            if (worker.UnitTag != 0)
            {
                workerLabelService?.SetLabel(finalLabel, worker.UnitTag);
                Console.WriteLine($"TeamLabelRegistrationHelper: Assigned {finalLabel} to Tag={worker.UnitTag} (was {originalLabel})");
            }
        }

        private static void AssignRemainingByDistance(List<WorkerEntryDto> teamWorkers, OrderedMineral largeMineral, IEnumerable<string> reservedFinalLabels, string[] finalLabels, WorkerLabelService? workerLabelService)
        {
            if (teamWorkers == null || teamWorkers.Count == 0 || largeMineral == null || largeMineral.Position == null)
            {
                return;
            }

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in reservedFinalLabels ?? Enumerable.Empty<string>())
            {
                reserved.Add(label);
            }

            var candidates = teamWorkers.Where(w => !reserved.Contains(w.FinalLabel)).ToList();
            if (candidates.Count < 2)
            {
                return;
            }

            AssignFinalLabel(candidates[0], finalLabels[0], workerLabelService);
            AssignFinalLabel(candidates[1], finalLabels[1], workerLabelService);
        }

        private static void AssignFinalLabel(WorkerEntryDto worker, string finalLabel, WorkerLabelService? workerLabelService)
        {
            if (worker == null || string.IsNullOrWhiteSpace(finalLabel))
            {
                return;
            }

            worker.FinalLabel = finalLabel;
            worker.Label = finalLabel;
            if (worker.UnitTag != 0)
            {
                workerLabelService?.SetLabel(finalLabel, worker.UnitTag);
            }
        }

        private static void SetNoPushFlag(MawBaseLocationData mapData, string flagName, List<WorkerEntryDto> teamWorkers, OrderedMineral largeMineral, string worker1Label, string worker3Label)
        {
            if (mapData == null || largeMineral?.Position == null)
            {
                return;
            }

            if (!mapData.AssignmentFlagsByStart.TryGetValue(0, out var flags))
            {
                flags = new Dictionary<string, bool>();
                mapData.AssignmentFlagsByStart[0] = flags;
            }

            var w1 = teamWorkers.FirstOrDefault(w => string.Equals(w.FinalLabel, worker1Label, StringComparison.OrdinalIgnoreCase));
            var w3 = teamWorkers.FirstOrDefault(w => string.Equals(w.FinalLabel, worker3Label, StringComparison.OrdinalIgnoreCase));
            if (w1?.Position == null || w3?.Position == null)
            {
                flags[flagName] = false;
                return;
            }

            var d1 = Vector2.Distance(new Vector2(w1.Position.X, w1.Position.Y), new Vector2(largeMineral.Position.X, largeMineral.Position.Y));
            var d3 = Vector2.Distance(new Vector2(w3.Position.X, w3.Position.Y), new Vector2(largeMineral.Position.X, largeMineral.Position.Y));
            flags[flagName] = (d3 - d1) > 0.5f;
        }

        private static void SetFinalLabel(OrderedMineral mineral, string label)
        {
            if (mineral == null || string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            mineral.FinalLabel = label;
            mineral.Label = label;
            mineral.TeamLabel = label;
        }

        private static string GetTeamPrefix(int teamNumber, int workerCount)
        {
            if (workerCount == 12)
            {
                return teamNumber switch
                {
                    1 => "Y",
                    2 => "B",
                    3 => "S",
                    4 => "T",
                    _ => string.Empty
                };
            }

            // 8 Workers or other non-standard starts
            return teamNumber switch
            {
                1 => "O", // Orange
                2 => "R", // Red
                3 => "P", // Purple
                4 => "G", // Green
                _ => string.Empty
            };
        }

        private static void MarkTeamAssignmentsReady(MawBaseLocationData mapData, int startIndex, List<TeamPatchAssignmentDto> assignments, int workerCount)
        {
            if (mapData == null)
            {
                return;
            }

            if (!mapData.AssignmentFlagsByStart.TryGetValue(startIndex, out var flags))
            {
                flags = new Dictionary<string, bool>();
                mapData.AssignmentFlagsByStart[startIndex] = flags;
            }

            flags[$"{TeamAssignmentsReadyFlag}:{workerCount}"] = assignments != null && assignments.Count > 0;
        }

        private static bool IsTeamAssignmentsReady(MawBaseLocationData mapData, int startIndex, int workerCount)
        {
            if (mapData == null)
            {
                return false;
            }

            return mapData.AssignmentFlagsByStart.TryGetValue(startIndex, out var flags)
                && flags.TryGetValue($"{TeamAssignmentsReadyFlag}:{workerCount}", out var ready)
                && ready;
        }

        /// <summary>
        /// Assigns team labels to a newly morphed drone based on current worker count.
        /// Call this from ProcessVisibleUnits when an unlabeled drone is detected.
        /// </summary>
        public static WorkerEntryDto AssignDynamicWorker(
            ulong unitTag,
            Vector2Dto position,
            int currentWorkerCount,
            WorkerLabelService workerLabelService,
            MawBaseLocationData mapData,
            int startIndex)
        {
            var assignment = TeamColorService.GetAssignmentForNewWorker(currentWorkerCount);
            var label = $"{assignment.prefix}{assignment.workerNum}";
            workerLabelService.SetLabel(label, unitTag);

            // If this is a 3rd worker joining an 8-worker team, transition existing workers
            if (assignment.workerNum == 3 && currentWorkerCount >= 8 && currentWorkerCount <= 11)
            {
                TransitionExistingTeamTo12WorkerColor(
                    assignment.pairIndex, 
                    currentWorkerCount + 1, 
                    workerLabelService, 
                    mapData, 
                    startIndex);
            }

            var entry = new WorkerEntryDto
            {
                UnitTag = unitTag,
                Position = position,
                Label = label,
                StartLabel = label,
                FinalLabel = label
            };

            Console.WriteLine($"AssignDynamicWorker: Worker {label} (tag {unitTag}) " +
                $"assigned at count {currentWorkerCount + 1}. Pink={assignment.isPink}");

            return entry;
        }

        /// <summary>
        /// When a 3rd worker joins, existing workers 1-2 on that team change from 
        /// 8-worker color to 12-worker color (e.g., G1→T1, G2→T2).
        /// </summary>
        private static void TransitionExistingTeamTo12WorkerColor(
            int pairIndex,
            int newTotalCount,
            WorkerLabelService workerLabelService,
            MawBaseLocationData mapData,
            int startIndex)
        {
            var meta = TeamColorService.GetTeamMeta(pairIndex);
            var oldPrefix = meta.prefix8;
            var newPrefix = meta.prefix12;

            // Find workers with old prefix and update their labels
            var allLabels = workerLabelService.GetAllLabels();
            foreach (var kvp in allLabels)
            {
                if (kvp.Key.StartsWith(oldPrefix))
                {
                    var suffix = kvp.Key.Substring(oldPrefix.Length);
                    var newLabel = $"{newPrefix}{suffix}";
                    workerLabelService.RemoveLabel(kvp.Key);
                    workerLabelService.SetLabel(newLabel, kvp.Value);
                    Console.WriteLine($"TransitionExistingTeamTo12WorkerColor: Re-labeled {kvp.Key} to {newLabel} (tag {kvp.Value})");
                }
            }
        }

        private static WorkerEntryDto? FindClosestWorkerToMineral(List<WorkerEntryDto> workers, OrderedMineral mineral)
        {
            if (workers == null || workers.Count == 0 || mineral == null)
            {
                return null;
            }

            WorkerEntryDto? closest = null;
            var closestDistance = float.MaxValue;

            foreach (var worker in workers)
            {
                var distance = Vector2.Distance(new Vector2(worker.Position.X, worker.Position.Y), new Vector2(mineral.Position.X, mineral.Position.Y));
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = worker;
                }
            }

            return closest;
        }

        private static (OrderedMineral Mineral, WorkerEntryDto Worker) FindClosestWorkerMineralPair(List<WorkerEntryDto> workers, OrderedMineral m1, OrderedMineral m2)
        {
            if (workers == null || workers.Count == 0 || m1 == null || m2 == null)
            {
                return (null, null);
            }

            OrderedMineral? closestMineral = null;
            WorkerEntryDto? closestWorker = null;
            var closestDistance = float.MaxValue;

            foreach (var worker in workers)
            {
                var workerPos = new Vector2(worker.Position.X, worker.Position.Y);

                var distToM1 = Vector2.Distance(workerPos, new Vector2(m1.Position.X, m1.Position.Y));
                if (distToM1 < closestDistance)
                {
                    closestDistance = distToM1;
                    closestMineral = m1;
                    closestWorker = worker;
                }

                var distToM2 = Vector2.Distance(workerPos, new Vector2(m2.Position.X, m2.Position.Y));
                if (distToM2 < closestDistance)
                {
                    closestDistance = distToM2;
                    closestMineral = m2;
                    closestWorker = worker;
                }
            }

            return (closestMineral, closestWorker);
        }

        private static void EnsureAssignmentCapacity(List<List<TeamPatchAssignmentDto>> assignments, int count)
        {
            if (assignments == null)
            {
                return;
            }

            while (assignments.Count < count)
            {
                assignments.Add(new List<TeamPatchAssignmentDto>());
            }
        }

        private sealed record TeamLayout(List<OrderedMineral> MineralPair, List<WorkerEntryDto> TeamWorkers, int TeamNumber);
    }
}
