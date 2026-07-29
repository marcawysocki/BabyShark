using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using BabySharkBot.Setup;
using BabySharkBot.MicroTasks;
using BabySharkBot.Managers;
using BabySharkBot.Manager;
using System.Diagnostics;

#nullable enable

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
                    TealM1IsFar = startIndex >= 0 && mapData?.TealM1IsFar != null && startIndex < mapData.TealM1IsFar.Length && mapData.TealM1IsFar[startIndex],
                    YellowM8IsFar = startIndex >= 0 && mapData?.YellowM8IsFar != null && startIndex < mapData.YellowM8IsFar.Length && mapData.YellowM8IsFar[startIndex]
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
            if (_phase == TestPhase.Idle) SetPhase(TestPhase.AssigningWorkers);
        }

        public IEnumerable<SC2APIProtocol.Action> BuildBumpOrders(int frame, MawBaseLocationData mapData, int startIndex, IReadOnlyList<WorkerEntryDto> workerEntries, IEnumerable<UnitCommander>? commanders = null)
        {
            var commands = new List<SC2APIProtocol.Action>();
            if (!Settings.ccaMining || mapData == null || workerEntries == null || workerEntries.Count == 0) return commands;

            // Frequency Logic: Idle/Assigning run every 5 frames. Accelerating/Aligning run EVERY frame.
            bool isHighFrequencyPhase = _phase == TestPhase.AcceleratingWorkerOne || _phase == TestPhase.AlignAtMineralA;
            if (frame % 5 != 0 && !isHighFrequencyPhase)
            {
                return commands;
            }

            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);
            var workerCount = Settings.WorkerCount > 0 ? Settings.WorkerCount : 12;

            // Fallback for missing assignments
            if (state.TeamAssignments.Count == 0 && mapData.TeamPatchAssignments != null)
            {
                var firstValid = mapData.TeamPatchAssignments.FirstOrDefault(a => a != null && a.Count > 0);
                if (firstValid != null) state.TeamAssignments = firstValid;
            }

            // Secondary Fallback: Try SecondaryTeamPatchAssignments
            if (state.TeamAssignments.Count == 0 && mapData.SecondaryTeamPatchAssignments != null)
            {
                var firstValid = mapData.SecondaryTeamPatchAssignments.FirstOrDefault(a => a != null && a.Count > 0);
                if (firstValid != null) state.TeamAssignments = firstValid;
            }

            // Tertiary Fallback: Try AssignmentsByWorkerCount
            if (state.TeamAssignments.Count == 0 && mapData.AssignmentsByWorkerCount != null)
            {
                if (mapData.AssignmentsByWorkerCount.TryGetValue(workerCount, out var assignmentsByStart))
                {
                    var firstValid = assignmentsByStart.FirstOrDefault(a => a != null && a.Count > 0);
                    if (firstValid != null) state.TeamAssignments = firstValid;
                }
            }

            // On frame 0, issue STOP
            if (frame == 0)
            {
                foreach (var w in workerEntries)
                {
                    var stopCmd = new ActionRawUnitCommand { AbilityId = 4, UnitTags = { w.UnitTag } };
                    commands.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = stopCmd } });
                }
                Console.WriteLine($"chrisCrossAppleSause: Issued STOP to {workerEntries.Count} workers on Frame 0.");
            }

            if (state.TeamAssignments.Count == 0) return commands;

            var workerByLabel = new Dictionary<string, WorkerEntryDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in workerEntries)
            {
                var label = w.FinalLabel ?? w.Label ?? w.StartLabel;
                if (!string.IsNullOrWhiteSpace(label)) workerByLabel[label] = w;
            }

            // Initialize TeamBumping for all teams if 12 workers
            if (workerCount == 12)
            {
                if (!state.TeamBumping.ContainsKey(1)) state.TeamBumping[1] = true;
                if (!state.TeamBumping.ContainsKey(2)) state.TeamBumping[2] = true;
                if (!state.TeamBumping.ContainsKey(3)) state.TeamBumping[3] = true;
                if (!state.TeamBumping.ContainsKey(4)) state.TeamBumping[4] = true;
                if (!state.TeamBumping.ContainsKey(5)) state.TeamBumping[5] = true; // Crossover Pair 5 (S3/T2)
                if (!state.TeamBumping.ContainsKey(6)) state.TeamBumping[6] = true; // Crossover Pair 6 (B3/Y2)
            }

            bool stateChanged = true;
            while (stateChanged)
            {
                stateChanged = false;
                switch (_phase)
                {
                    case TestPhase.Idle:
                        if (state.TeamAssignments.Any()) { SetPhase(TestPhase.AssigningWorkers); stateChanged = true; }
                        break;
                    case TestPhase.AssigningWorkers:
                        if (state.TeamAssignments.Any()) { SetPhase(TestPhase.AcceleratingWorkerOne); stateChanged = true; }
                        break;
                    case TestPhase.AcceleratingWorkerOne:
                        var accCommands = HandleAcceleratingWorkerOne(frame, mapData, state, workerEntries, workerCount, workerByLabel);
                        if (_phase == TestPhase.AlignAtMineralA && !accCommands.Any()) { stateChanged = true; continue; }
                        commands.AddRange(accCommands);
                        return commands;
                    case TestPhase.AlignAtMineralA:
                        // After frame 35, once we transition to alignment, immediately issue harvest commands to all workers
                        foreach (var team in state.TeamAssignments)
                        {
                            var minerals = team.Minerals;
                            if (minerals.Count == 0) continue;
                            var mineralA = minerals.FirstOrDefault(m => m.IsNear) ?? minerals[0];
                            var mineralB = minerals.FirstOrDefault(m => !m.IsNear && m != mineralA) ?? minerals.Skip(1).FirstOrDefault() ?? mineralA;

                            var w1 = ResolveLiveWorkerBySuffix(team.Workers, workerEntries, "1");
                            var w2 = ResolveLiveWorkerBySuffix(team.Workers, workerEntries, "2");
                            var w3 = ResolveLiveWorkerBySuffix(team.Workers, workerEntries, "3");

                            if (commanders != null)
                            {
                                if (w1 != null) { var c1 = commanders.FirstOrDefault(c => c.UnitCalculation.Unit.Tag == w1.UnitTag); if (c1 != null) commands.AddRange(c1.Order(frame, Abilities.HARVEST_GATHER, null, mineralA.UnitTag)); }
                                if (w2 != null) { var c2 = commanders.FirstOrDefault(c => c.UnitCalculation.Unit.Tag == w2.UnitTag); if (c2 != null) commands.AddRange(c2.Order(frame, Abilities.HARVEST_GATHER, null, mineralB.UnitTag)); }
                                if (w3 != null) { var c3 = commanders.FirstOrDefault(c => c.UnitCalculation.Unit.Tag == w3.UnitTag); if (c3 != null) commands.AddRange(c3.Order(frame, Abilities.HARVEST_GATHER, null, mineralA.UnitTag)); }
                            }
                            else
                            {
                                if (w1 != null) commands.AddRange(Harvest(w1.UnitTag, mineralA.Position, mineralA.UnitTag));
                                if (w2 != null) commands.AddRange(Harvest(w2.UnitTag, mineralB.Position, mineralB.UnitTag));
                                if (w3 != null) commands.AddRange(Harvest(w3.UnitTag, mineralA.Position, mineralA.UnitTag));
                            }
                        }
                        Console.WriteLine($"chrisCrossAppleSause: Frame {frame} - Transition to AlignAtMineralA: Issued global Harvest (HARVEST_GATHER) commands.");
                        
                        commands.AddRange(HandleAlignAtMineralA(frame, mapData, state, workerEntries));
                        return commands;
                    case TestPhase.CancelAndReturnHome:
                        return commands;
                }
            }
            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> HandleAcceleratingWorkerOne(int frame, MawBaseLocationData mapData, CcaSpawnLearningState state, IReadOnlyList<WorkerEntryDto> workerEntries, int workerCount, Dictionary<string, WorkerEntryDto> workerByLabel)
        {
            var commands = new List<SC2APIProtocol.Action>();
            if (frame >= 35) { SetPhase(TestPhase.AlignAtMineralA); return commands; }

            // Handle Crossover Bumping Pairs (Higher priority)
            if (workerCount == 12)
            {
                if (state.TealM1IsFar && workerByLabel.TryGetValue("S3", out var s3) && workerByLabel.TryGetValue("T2", out var t2))
                {
                    var mineralTA = GetMineralOrdered(state, 1, "TA");
                    if (mineralTA != null) commands.AddRange(ProcessBumpingPair(frame, state, 5, t2, s3, mineralTA));
                }
                if (state.YellowM8IsFar && workerByLabel.TryGetValue("B3", out var b3) && workerByLabel.TryGetValue("Y2", out var y2))
                {
                    var mineralYA = GetMineralOrdered(state, 4, "YA");
                    if (mineralYA != null) commands.AddRange(ProcessBumpingPair(frame, state, 6, y2, b3, mineralYA));
                }
            }

            foreach (var team in state.TeamAssignments)
            {
                var logicalWorkers = team.Workers;
                var minerals = team.Minerals;
                if (logicalWorkers.Count == 0 || minerals.Count == 0) continue;

                var mineralA = minerals.FirstOrDefault(m => m.IsNear) ?? minerals[0];
                var mineralB = minerals.FirstOrDefault(m => !m.IsNear && m != mineralA) ?? minerals.Skip(1).FirstOrDefault() ?? mineralA;

                var w1 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "1");
                var w2 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "2");
                var w3 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "3");

                if (workerCount == 8)
                {
                    if (w1 != null) commands.AddRange(ProcessWorkerMovement(w1, mineralA));
                    if (w2 != null) commands.AddRange(ProcessWorkerMovement(w2, mineralB));
                }
                else if (workerCount == 12)
                {
                    bool isCrossover = (team.TeamNumber == 1 && state.TealM1IsFar) || (team.TeamNumber == 2 && state.TealM1IsFar) || (team.TeamNumber == 3 && state.YellowM8IsFar) || (team.TeamNumber == 4 && state.YellowM8IsFar);
                    if (isCrossover)
                    {
                        if (w1 != null) commands.AddRange(ProcessWorkerMovement(w1, mineralA));
                        if (w2 != null) commands.AddRange(ProcessWorkerMovement(w2, (team.TeamNumber == 1 || team.TeamNumber == 4) ? mineralA : mineralB));
                        if (w3 != null) commands.AddRange(ProcessWorkerMovement(w3, (team.TeamNumber == 1 || team.TeamNumber == 4) ? mineralA : mineralB));
                        continue;
                    }

                    WorkerEntryDto? lead = w1;
                    WorkerEntryDto? partner = (team.TeamNumber == 1 || team.TeamNumber == 4) ? w3 : w2;
                    WorkerEntryDto? side = (partner == w3) ? w2 : w3;

                    if (state.TeamBumping.GetValueOrDefault(team.TeamNumber, true) && lead != null && partner != null)
                    {
                        commands.AddRange(ProcessBumpingPair(frame, state, team.TeamNumber, lead, partner, mineralA));
                    }
                    else
                    {
                        if (lead != null) commands.AddRange(ProcessWorkerMovement(lead, mineralA));
                        if (partner != null) commands.AddRange(ProcessWorkerMovement(partner, (team.TeamNumber == 1 || team.TeamNumber == 4) ? mineralA : mineralB));
                    }
                    if (side != null) commands.AddRange(ProcessWorkerMovement(side, (team.TeamNumber == 1 || team.TeamNumber == 4) ? mineralB : mineralA));
                }
            }
            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> HandleAlignAtMineralA(int frame, MawBaseLocationData mapData, CcaSpawnLearningState state, IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();
            if (frame > 65) { SetPhase(TestPhase.CancelAndReturnHome); return commands; }

            foreach (var team in state.TeamAssignments)
            {
                var logicalWorkers = team.Workers;
                var minerals = team.Minerals;
                if (logicalWorkers.Count == 0 || minerals.Count == 0) continue;

                var mineralA = minerals.FirstOrDefault(m => m.IsNear) ?? minerals[0];
                var mineralB = minerals.FirstOrDefault(m => !m.IsNear && m != mineralA) ?? minerals.Skip(1).FirstOrDefault() ?? mineralA;
                var hatcheryPos = mapData.StartingTownHall[state.StartIndex];

                var w1 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "1");
                var w2 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "2");
                var w3 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "3");

                if (mineralA == null || hatcheryPos == null) continue;

                var dirX = mineralA.Position.X - hatcheryPos.X;
                var dirY = mineralA.Position.Y - hatcheryPos.Y;
                var mag = MathF.Sqrt(dirX * dirX + dirY * dirY);
                dirX /= mag; dirY /= mag;

                if (w1 != null) commands.AddRange(ProcessWorkerMovement(w1, mineralA));
                if (w3 != null)
                {
                    var w3Target = new Point2D { X = mineralA.Position.X - dirX * 1.0f, Y = mineralA.Position.Y - dirY * 1.0f };
                    // For W3 micro-positioning, we use MOVE. Once it's close enough, ProcessWorkerMovement triggers Harvest.
                    commands.AddRange(ProcessWorkerMovement(w3, mineralA));
                }
                if (w2 != null && mineralB != null) commands.AddRange(ProcessWorkerMovement(w2, mineralB));
            }
            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> ProcessBumpingPair(int frame, CcaSpawnLearningState state, int pairId, WorkerEntryDto lead, WorkerEntryDto partner, OrderedMineral mineral)
        {
            var commands = new List<SC2APIProtocol.Action>();
            if (lead == null || partner == null || mineral == null) return commands;
            if (Distance(lead.Position, partner.Position) >= 2.0f) { state.TeamBumping[pairId] = false; }

            if (state.TeamBumping.GetValueOrDefault(pairId, true))
            {
                var avgX = (lead.Position.X + partner.Position.X) * 0.5f;
                var avgY = (lead.Position.Y + partner.Position.Y) * 0.5f;
                var targetX = (avgX + mineral.Position.X) * 0.5f;
                var targetY = (avgY + mineral.Position.Y) * 0.5f;
                var leadMove = new Point2D { X = (lead.Position.X + targetX) * 0.5f, Y = (lead.Position.Y + targetY) * 0.5f };
                var partnerMove = new Point2D { X = partner.Position.X + (targetX - partner.Position.X) * 0.25f, Y = partner.Position.Y + (targetY - partner.Position.Y) * 0.25f };
                commands.AddRange(MoveTo(lead.UnitTag, leadMove));
                commands.AddRange(MoveTo(partner.UnitTag, partnerMove));
            }
            else
            {
                commands.AddRange(ProcessWorkerMovement(lead, mineral));
                commands.AddRange(ProcessWorkerMovement(partner, mineral));
            }
            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> ProcessWorkerMovement(WorkerEntryDto worker, OrderedMineral mineral)
        {
            if (worker == null || mineral == null) return Array.Empty<SC2APIProtocol.Action>();
            if (Distance(worker.Position, mineral.Position) <= 1.5f) return Harvest(worker.UnitTag, mineral.Position, mineral.UnitTag);
            var movePoint = new Point2D { X = (worker.Position.X + mineral.Position.X) * 0.5f, Y = (worker.Position.Y + mineral.Position.Y) * 0.5f };
            return MoveTo(worker.UnitTag, movePoint);
        }

        private IEnumerable<SC2APIProtocol.Action> Harvest(ulong tag, Vector2Dto mineralPos, ulong mineralTag = 0)
        {
            if (tag == 0 || mineralPos == null) return Array.Empty<SC2APIProtocol.Action>();
            // Use AbilityId 3666 (Generic Gather) or AbilityId 16 (SMART) with tag.
            // User requested HARVEST_GATHER which is preferred for explicit harvesting.
            var cmd = new ActionRawUnitCommand { AbilityId = (int)Abilities.HARVEST_GATHER };
            if (mineralTag != 0)
            {
                cmd.TargetUnitTag = mineralTag;
            }
            else
            {
                cmd.TargetWorldSpacePos = new Point2D { X = mineralPos.X, Y = mineralPos.Y };
            }
            
            cmd.UnitTags.Add(tag);
            return new List<SC2APIProtocol.Action> { new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = cmd } } };
        }

        private WorkerEntryDto? ResolveLiveWorkerBySuffix(List<WorkerEntryDto> logicalWorkers, IReadOnlyList<WorkerEntryDto> liveWorkers, string suffix)
        {
            var targetWLabel = $"W{suffix}";
            var logical = logicalWorkers.FirstOrDefault(w => w.Label != null && w.Label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (logical == null) return null;
            if (logical.UnitTag != 0) { var byTag = liveWorkers.FirstOrDefault(w => w.UnitTag == logical.UnitTag); if (byTag != null) return byTag; }
            return liveWorkers.FirstOrDefault(w => string.Equals(w.Label, logical.FinalLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(w.Label, logical.StartLabel, StringComparison.OrdinalIgnoreCase) || (w.Label != null && w.Label.Equals(targetWLabel, StringComparison.OrdinalIgnoreCase)));
        }

        private IEnumerable<SC2APIProtocol.Action> MoveTo(ulong tag, Point2D point)
        {
            if (tag == 0) return Array.Empty<SC2APIProtocol.Action>();
            var cmd = new ActionRawUnitCommand { AbilityId = 16, TargetWorldSpacePos = point };
            cmd.UnitTags.Add(tag);
            return new List<SC2APIProtocol.Action> { new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = cmd } } };
        }

        private float Distance(Vector2Dto a, Vector2Dto b)
        {
            if (a == null || b == null) return float.MaxValue;
            return MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2));
        }

        private OrderedMineral? GetMineralOrdered(CcaSpawnLearningState state, int teamNum, string label)
        {
            return state.TeamAssignments.FirstOrDefault(t => t.TeamNumber == teamNum)?.Minerals.FirstOrDefault(m => string.Equals(m.FinalLabel ?? m.Label, label, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildSpawnKey(MawBaseLocationData mapData, int startIndex)
        {
            var loc = (mapData?.StartingTownHall != null && startIndex >= 0 && startIndex < mapData.StartingTownHall.Length) ? mapData.StartingTownHall[startIndex] : null;
            return loc == null ? $"spawn-{startIndex}" : $"spawn-{startIndex}-{loc.X:F2}-{loc.Y:F2}";
        }

        public void RecordSpawnObservation(MawBaseLocationData mapData, int startIndex, List<TeamPatchAssignmentDto> teamAssignments, WorkerLabelService? workerLabelService = null, int frame = -1, IReadOnlyList<WorkerEntryDto>? workerEntries = null, MineralLabelService? mineralLabelService = null)
        {
            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);
            state.TeamAssignments = teamAssignments ?? new List<TeamPatchAssignmentDto>();
            
            if (workerEntries != null && state.InitialPositions.Count == 0) 
            { 
                foreach (var w in workerEntries) state.InitialPositions[w.UnitTag] = new Vector2Dto(w.Position.X, w.Position.Y, w.Position.Z); 
            }

            if (workerEntries != null)
            {
                foreach (var team in state.TeamAssignments)
                {
                    foreach (var logical in team.Workers)
                    {
                        if (logical.UnitTag != 0) 
                        { 
                            var byTag = workerEntries.FirstOrDefault(w => w.UnitTag == logical.UnitTag); 
                            if (byTag != null) { SyncWorkerLabels(logical, byTag); continue; } 
                        }
                        var preciseLabel = logical.FinalLabel ?? logical.Label; 
                        var startLabel = logical.StartLabel ?? logical.Label;
                        var live = workerEntries.FirstOrDefault(w => string.Equals(w.Label, preciseLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(w.FinalLabel, preciseLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(w.Label, startLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(w.StartLabel, startLabel, StringComparison.OrdinalIgnoreCase));
                        if (live != null) { logical.UnitTag = live.UnitTag; SyncWorkerLabels(logical, live); }
                    }
                }
            }

            // Synchronize Mineral tags using MineralLabelService
            if (mineralLabelService != null)
            {
                var labels = mineralLabelService.GetAllMineralLabels();
                foreach (var team in state.TeamAssignments)
                {
                    foreach (var mineral in team.Minerals)
                    {
                        var lbl = mineral.FinalLabel ?? mineral.Label;
                        if (!string.IsNullOrWhiteSpace(lbl) && labels.TryGetValue(lbl, out var data))
                        {
                            mineral.UnitTag = data.Tag;
                        }
                    }
                }
            }

            if (workerLabelService != null)
            {
                foreach (var team in state.TeamAssignments)
                {
                    foreach (var w in team.Workers)
                    {
                        var lbl = w.FinalLabel ?? w.Label ?? w.StartLabel;
                        if (!string.IsNullOrWhiteSpace(lbl) && w.UnitTag != 0) 
                        { 
                            var current = workerLabelService.GetLabel(w.UnitTag); 
                            if (string.IsNullOrWhiteSpace(current) || current.StartsWith("W", StringComparison.OrdinalIgnoreCase)) 
                            { 
                                workerLabelService.SetLabel(lbl, w.UnitTag); 
                            } 
                        }
                    }
                }
            }
        }

        private void SyncWorkerLabels(WorkerEntryDto logical, WorkerEntryDto live)
        {
            if (string.IsNullOrWhiteSpace(live.FinalLabel)) live.FinalLabel = logical.FinalLabel;
            if (string.IsNullOrWhiteSpace(live.StartLabel)) live.StartLabel = logical.StartLabel;
            if (string.IsNullOrWhiteSpace(live.Label)) live.Label = logical.Label;
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
        public Dictionary<int, bool> TeamBumping { get; set; } = new Dictionary<int, bool>();
        public Dictionary<ulong, Vector2Dto> InitialPositions { get; set; } = new Dictionary<ulong, Vector2Dto>();
    }
}
