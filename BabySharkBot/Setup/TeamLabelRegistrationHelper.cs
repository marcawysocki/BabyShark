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
            var minerals = orderedMainMinerals[startIndex]?
                .Where(mineral => mineral != null)
                .OrderBy(mineral => mineral.Index)
                .ToList();
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

            // Team membership is defined only from the canonical greedy mineral chain.
            // Persisted Index 1..8 maps directly to M[1]..M[8]; caller/list enumeration order is not semantic.
            var canonicalMinerals = minerals
                .Where(mineral => mineral != null)
                .OrderBy(mineral => mineral.Index)
                .ToList();
            var teamLayouts = BuildExplicitTeamLayouts(canonicalMinerals, workers);

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
                    // The 8-worker opening keeps the greedy worker-to-mineral order.
                    // Worker list order maps directly to mineral pair order; suffixes are
                    // then derived from each target mineral's final A/B label.
                    ApplyWorkerFinalLabels(mapData, startIndex, GetTeamPrefix(teamNum, workers.Count), teamWorkers, mineralPair[0], mineralPair[1], workerLabelService);
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

            // Full 12-worker mapping follows the shared color order:
            // Teal: M[1], M[2] -> W2, W3, W4
            // Salmon: M[3], M[4] -> W5, W6, W1
            // Blue: M[5], M[6] -> W7, W8, W12
            // Yellow: M[7], M[8] -> W9, W10, W11
            if (workers != null && workers.Count == 12)
            {
                AddTeamIfPossible(teams, minerals, workers, 0, 1, new[] { "W2", "W3", "W4" }, 1);
                AddTeamIfPossible(teams, minerals, workers, 2, 3, new[] { "W5", "W6", "W1" }, 2);
                AddTeamIfPossible(teams, minerals, workers, 4, 5, new[] { "W7", "W8", "W12" }, 3);
                AddTeamIfPossible(teams, minerals, workers, 6, 7, new[] { "W9", "W10", "W11" }, 4);
            }
            else
            {
                // For 8-worker starts, use a strict linear 1-to-1 mapping based on greedy chain order:
                // Team 1: O (M0, M1) -> W1, W2
                // Team 2: R (M2, M3) -> W3, W4
                // Team 3: P (M4, M5) -> W5, W6
                // Team 4: G (M6, M7) -> W7, W8
                AddTeamIfPossible(teams, minerals, workers, 0, 1, new[] { "W1", "W2" }, 1);
                AddTeamIfPossible(teams, minerals, workers, 2, 3, new[] { "W3", "W4" }, 2);
                AddTeamIfPossible(teams, minerals, workers, 4, 5, new[] { "W5", "W6" }, 3);
                AddTeamIfPossible(teams, minerals, workers, 6, 7, new[] { "W7", "W8" }, 4);
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
                    1 => "T",
                    2 => "S",
                    3 => "B",
                    4 => "Y",
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

            // A/B are identical for every team and independent of the numeric greedy
            // prefix and Near/Far classification: Large is always A, Small is always B.
            var aMineral = first.Size == MineralSize.Large
                ? first
                : second.Size == MineralSize.Large
                    ? second
                    : null;
            var bMineral = first.Size == MineralSize.Small
                ? first
                : second.Size == MineralSize.Small
                    ? second
                    : null;
            if (aMineral == null || bMineral == null || aMineral == bMineral)
            {
                Console.WriteLine($"TeamLabelRegistrationHelper: [WARN] {prefix} pair must contain one Large and one Small mineral; M[{first.Index}]={first.Size}, M[{second.Index}]={second.Size}");
                return;
            }

            var farMineral = ResolveFarMineralForFlags(first, second);
            SetFinalLabel(aMineral, $"{prefix}A");
            SetFinalLabel(bMineral, $"{prefix}B");
            Console.WriteLine($"Assignment: {FormatMineralAssignmentLabel(aMineral)} Unit ID={aMineral.UnitTag}");
            Console.WriteLine($"Assignment: {FormatMineralAssignmentLabel(bMineral)} Unit ID={bMineral.UnitTag}");
            SetMineralIdentityFlags(mapData, startIndex, prefix, first, second, farMineral);

            mapData.MineralFinalLabelsByPosition ??= new Dictionary<string, string>();
            if (aMineral.Position != null)
            {
                mapData.MineralFinalLabelsByPosition[$"{aMineral.Position.X:F2},{aMineral.Position.Y:F2}"] = $"{prefix}A";
            }

            if (bMineral.Position != null)
            {
                mapData.MineralFinalLabelsByPosition[$"{bMineral.Position.X:F2},{bMineral.Position.Y:F2}"] = $"{prefix}B";
            }

        }

        private static string FormatMineralAssignmentLabel(OrderedMineral mineral)
        {
            if (mineral == null || string.IsNullOrWhiteSpace(mineral.FinalLabel))
            {
                return string.Empty;
            }

            return mineral.Index > 0 ? $"{mineral.Index}-{mineral.FinalLabel}" : mineral.FinalLabel;
        }

        private static OrderedMineral ResolveFarMineralForFlags(OrderedMineral first, OrderedMineral second)
        {
            if (first?.IsFar == true && second?.IsFar != true) return first;
            if (second?.IsFar == true && first?.IsFar != true) return second;
            return first?.DistanceToTownhall >= second?.DistanceToTownhall ? first : second;
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
            var aMineral = mineralPair.FirstOrDefault(mineral =>
                string.Equals(mineral?.FinalLabel, $"{prefix}A", StringComparison.OrdinalIgnoreCase));
            var bMineral = mineralPair.FirstOrDefault(mineral =>
                string.Equals(mineral?.FinalLabel, $"{prefix}B", StringComparison.OrdinalIgnoreCase));
            if (aMineral?.Position == null || bMineral?.Position == null)
            {
                Console.WriteLine($"TeamLabelRegistrationHelper: [WARN] Missing {prefix}A/{prefix}B mineral labels for 12-worker assignment.");
                return;
            }

            if (teamNumber == 1)
            {
                // M[1] is the Teal edge. The B/A identity of M[1] determines the
                // fixed T2 worker and which two candidates compete for T1.
                var m1 = mineralPair.FirstOrDefault(mineral => mineral.Index == 1);
                var m1IsB = string.Equals(m1?.FinalLabel, "TB", StringComparison.OrdinalIgnoreCase);
                var t2Source = m1IsB ? "W2" : "W4";
                var t1Candidates = m1IsB ? new[] { "W3", "W4" } : new[] { "W2", "W3" };
                var t1Target = m1IsB ? aMineral : m1;
                var t2 = FindWorkerByStartLabel(teamWorkers, t2Source);
                var t1 = FindClosestWorker(teamWorkers, t1Candidates, t1Target);
                if (t2 == null || t1 == null || t1 == t2)
                {
                    Console.WriteLine($"TeamLabelRegistrationHelper: [WARN] Incomplete Teal conditional assignment for start[{startIndex}].");
                    return;
                }

                AssignFinalLabel(t2, "T2", workerLabelService);
                AssignFinalLabel(t1, "T1", workerLabelService);
                var t3 = teamWorkers.FirstOrDefault(worker => worker != t2 && worker != t1);
                if (t3 != null)
                {
                    AssignFinalLabel(t3, "T3", workerLabelService);
                }

                SetNoPushFlag(mapData, startIndex, "TealNoPush");
                return;
            }

            if (teamNumber == 2)
            {
                // Salmon keeps the fixed M[3]/M[4] targets; W1 is the S3 A-mineral
                // worker defined by the current 12-worker opening contract.
                var s3 = FindWorkerByStartLabel(teamWorkers, "W1");
                var w5 = FindWorkerByStartLabel(teamWorkers, "W5");
                var w6 = FindWorkerByStartLabel(teamWorkers, "W6");
                if (s3 == null || w5 == null || w6 == null)
                {
                    Console.WriteLine($"TeamLabelRegistrationHelper: [WARN] Incomplete Salmon fixed assignment for start[{startIndex}].");
                    return;
                }

                AssignFinalLabel(s3, "S3", workerLabelService);
                AssignFinalLabel(w5, MineralRoleSuffix(mineralPair.FirstOrDefault(mineral => mineral.Index == 3), "S"), workerLabelService);
                AssignFinalLabel(w6, MineralRoleSuffix(mineralPair.FirstOrDefault(mineral => mineral.Index == 4), "S"), workerLabelService);
                SetNoPushFlag(mapData, startIndex, "SalmonNoPush");
                return;
            }

            if (teamNumber == 3)
            {
                // Blue keeps the fixed M[5]/M[6] targets; W12 is B3.
                var b3 = FindWorkerByStartLabel(teamWorkers, "W12");
                var w7 = FindWorkerByStartLabel(teamWorkers, "W7");
                var w8 = FindWorkerByStartLabel(teamWorkers, "W8");
                if (b3 == null || w7 == null || w8 == null)
                {
                    Console.WriteLine($"TeamLabelRegistrationHelper: [WARN] Incomplete Blue fixed assignment for start[{startIndex}].");
                    return;
                }

                AssignFinalLabel(b3, "B3", workerLabelService);
                AssignFinalLabel(w7, MineralRoleSuffix(mineralPair.FirstOrDefault(mineral => mineral.Index == 5), "B"), workerLabelService);
                AssignFinalLabel(w8, MineralRoleSuffix(mineralPair.FirstOrDefault(mineral => mineral.Index == 6), "B"), workerLabelService);
                SetNoPushFlag(mapData, startIndex, "BlueNoPush");
                return;
            }

            if (teamNumber == 4)
            {
                // M[8] is the Yellow edge. If it is Yellow B, W11 is Y2;
                // otherwise W9 is Y2. The nearest remaining worker to YA is Y1.
                var m8 = mineralPair.FirstOrDefault(mineral => mineral.Index == 8);
                var m8IsB = string.Equals(m8?.FinalLabel, "YB", StringComparison.OrdinalIgnoreCase);
                var y2Source = m8IsB ? "W11" : "W9";
                var y2 = FindWorkerByStartLabel(teamWorkers, y2Source);
                var remaining = teamWorkers.Where(worker => worker != y2).ToList();
                var y1 = FindClosestWorker(remaining, remaining.Select(worker => worker.StartLabel).ToArray(), aMineral);
                if (y2 == null || y1 == null || y1 == y2)
                {
                    Console.WriteLine($"TeamLabelRegistrationHelper: [WARN] Incomplete Yellow conditional assignment for start[{startIndex}].");
                    return;
                }

                AssignFinalLabel(y2, "Y2", workerLabelService);
                AssignFinalLabel(y1, "Y1", workerLabelService);
                var y3 = teamWorkers.FirstOrDefault(worker => worker != y2 && worker != y1);
                if (y3 != null)
                {
                    AssignFinalLabel(y3, "Y3", workerLabelService);
                }

                SetNoPushFlag(mapData, startIndex, "YellowNoPush");
            }
        }

        private static WorkerEntryDto? FindWorkerByStartLabel(IEnumerable<WorkerEntryDto> workers, string startLabel)
        {
            return workers?.FirstOrDefault(worker =>
                string.Equals(worker?.StartLabel, startLabel, StringComparison.OrdinalIgnoreCase));
        }

        private static WorkerEntryDto? FindClosestWorker(
            IEnumerable<WorkerEntryDto> workers,
            IEnumerable<string> candidateLabels,
            OrderedMineral target)
        {
            if (target?.Position == null)
            {
                return null;
            }

            var candidates = new HashSet<string>(candidateLabels ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return workers?
                .Where(worker => worker?.Position != null && candidates.Contains(worker.StartLabel))
                .OrderBy(worker => DistanceSquared(worker.Position, target.Position))
                .FirstOrDefault();
        }

        private static string MineralRoleSuffix(OrderedMineral mineral, string teamPrefix)
        {
            if (mineral == null || string.IsNullOrWhiteSpace(teamPrefix))
            {
                return string.Empty;
            }

            return string.Equals(mineral.FinalLabel, $"{teamPrefix}A", StringComparison.OrdinalIgnoreCase)
                ? $"{teamPrefix}1"
                : string.Equals(mineral.FinalLabel, $"{teamPrefix}B", StringComparison.OrdinalIgnoreCase)
                    ? $"{teamPrefix}2"
                    : string.Empty;
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

            if (teamWorkers.Count == 2)
            {
                for (var i = 0; i < teamWorkers.Count && i < 2; i++)
                {
                    var target = i == 0 ? nearMineral : farMineral;
                    var suffix = string.Equals(target?.FinalLabel, $"{prefix}A", StringComparison.OrdinalIgnoreCase)
                        ? "1"
                        : string.Equals(target?.FinalLabel, $"{prefix}B", StringComparison.OrdinalIgnoreCase)
                            ? "2"
                            : string.Empty;

                    if (string.IsNullOrEmpty(suffix))
                    {
                        Console.WriteLine($"TeamLabelRegistrationHelper: [WARN] Cannot derive {prefix} suffix for {teamWorkers[i].StartLabel}; target mineral label={target?.FinalLabel}");
                        continue;
                    }

                    AssignFinalLabel(teamWorkers[i], $"{prefix}{suffix}", workerLabelService);
                }

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
                SetLegacyNoPushFlag(mapData, "YellowNoPush", teamWorkers, nearMineral, "Y1", "Y3");
            }
            else if (prefix == "T")
            {
                var t2Source = IsFarMineralFlagSet(mapData, startIndex, "M8IsFar")
                    ? "W11"
                    : "W9";
                AssignWorkerFinalLabel(teamWorkers, t2Source, "T2", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "T2" }, new[] { "T1", "T3" }, workerLabelService);
                SetLegacyNoPushFlag(mapData, "TealNoPush", teamWorkers, nearMineral, "T1", "T3");
            }
            else if (prefix == "B")
            {
                AssignWorkerFinalLabel(teamWorkers, "W1", "B3", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "B3" }, new[] { "B1", "B2" }, workerLabelService);
                SetLegacyNoPushFlag(mapData, "BlueNoPush", teamWorkers, nearMineral, "B1", "B2");
            }
            else if (prefix == "S")
            {
                AssignWorkerFinalLabel(teamWorkers, "W12", "S3", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "S3" }, new[] { "S1", "S2" }, workerLabelService);
                SetLegacyNoPushFlag(mapData, "SalmonNoPush", teamWorkers, nearMineral, "S1", "S2");
            }
            else
            {
                // Every mineral team uses the same four prefixes, including 8-worker starts.
                // The team layout already preserves the documented worker-to-mineral pairing.
                for (var i = 0; i < teamWorkers.Count && i < 2; i++)
                {
                    AssignFinalLabel(teamWorkers[i], $"{prefix}{i + 1}", workerLabelService);
                }
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

            var workerIndex = ParseWorkerIndex(worker.StartLabel ?? worker.Label);
            var displayLabel = workerIndex > 0 ? $"{workerIndex}-{worker.FinalLabel}" : worker.FinalLabel;
            Console.WriteLine($"Assignment: {displayLabel} Unit ID={worker.UnitTag}");
        }

        private static int ParseWorkerIndex(string label)
        {
            if (string.IsNullOrWhiteSpace(label) || label.Length < 2 || label[0] != 'W')
            {
                return 0;
            }

            return int.TryParse(label.Substring(1), out var index) ? index : 0;
        }

        private static void SetLegacyNoPushFlag(
            MawBaseLocationData mapData,
            string flagName,
            List<WorkerEntryDto> teamWorkers,
            OrderedMineral largeMineral,
            string worker1Label,
            string worker3Label)
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

        private static void SetNoPushFlag(MawBaseLocationData mapData, int startIndex, string flagName)
        {
            if (mapData == null || string.IsNullOrWhiteSpace(flagName) || startIndex < 0)
            {
                return;
            }

            if (!mapData.AssignmentFlagsByStart.TryGetValue(startIndex, out var flags))
            {
                flags = new Dictionary<string, bool>();
                mapData.AssignmentFlagsByStart[startIndex] = flags;
            }

            flags[flagName] = true;
        }

        private static void SetFinalLabel(OrderedMineral mineral, string label)
        {
            if (mineral == null || string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            mineral.FinalLabel = label;
            mineral.Label = $"{mineral.Index}-{label}";
            mineral.TeamLabel = label;
        }

        private static string GetTeamPrefix(int teamNumber, int workerCount)
        {
            // Mineral teams always use the 12-worker color vocabulary. The mineral manager
            // owns behavior after frame 35, so labels must remain stable across worker counts.
            return teamNumber switch
            {
                1 => "T",
                2 => "S",
                3 => "B",
                4 => "Y",
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
