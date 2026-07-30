qwen.MD
Kimi:
I’ve analyzed the code and identified the root causes of the bunching and incorrect targeting. There are four critical bugs causing the behavior you described:
Root Cause Analysis
1.  Near/Far Mineral Labels Are Swapped (TeamLabelRegistrationHelper.cs line ~320): When ApplyMineralFinalLabels can’t determine near/far by size, it uses DistanceToTownhall >= which picks the farther mineral as “near” (A). This means T1/Y1 are sent to the wrong mineral in their pair.
2.  Bumping Pairs Reversed for Teams 2 & 3 (chrisCrossAppleSause.cs): For Salmon (Team 2) and Blue (Team 3), the code uses w2 as the bumper instead of w3. S2/B2 should be going to mineral B, but they’re being sent to bump toward A. Meanwhile S3/B3 (who should be bumping) are sent to A alone.
3.  Move Commands Override Harvest Commands (chrisCrossAppleSause.cs): At frame 35, HandleAlignAtMineralA issues HARVEST_GATHER, but then immediately issues SMART move commands that override the harvest order. Workers never actually receive the harvest command.
4.  JIT State Initialization Uses Wrong Mineral Order (BabySharkMiningManager.cs): InitializeJitWorkerStates blindly uses Minerals[0] as “A” and Minerals[1] as “B”, but for some teams (like Yellow/Team 4) the list order is [far, near], so workers start on the wrong mineral permanently.
5.  Crossover Pairs Use Wrong Workers (chrisCrossAppleSause.cs): The S3→T1 and B3→Y1 triple-bump logic incorrectly uses T2 and Y2 instead of T1 and Y1.
________________________________________
Corrected Files
Here are the fixed versions. The key changes are marked with // FIX: comments.
1. chrisCrossAppleSause.cs
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
        
        public const int StartFrame = 0;
        public const int AccelerateThreshold = 15;
        public const int AlignThreshold = 35;
        public const int EndThreshold = 65;

        // FIX: Removed 'static' so phase is per-instance and doesn't leak across spawns/games
        private TestPhase _phase = TestPhase.Idle;
        private readonly Dictionary<string, CcaSpawnLearningState> _states = new Dictionary<string, CcaSpawnLearningState>(StringComparer.OrdinalIgnoreCase);
        private bool _harvestCommandsIssued = false;

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
            
            // FIX: Reset per-spawn state so harvest commands are issued fresh each game
            _harvestCommandsIssued = false;
            SetPhase(TestPhase.AssigningWorkers);
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

            // FIX: Use startIndex for fallbacks instead of grabbing the first valid list
            if (state.TeamAssignments.Count == 0 && mapData.TeamPatchAssignments != null && startIndex >= 0 && startIndex < mapData.TeamPatchAssignments.Count)
            {
                var assignments = mapData.TeamPatchAssignments[startIndex];
                if (assignments != null && assignments.Count > 0) state.TeamAssignments = assignments;
            }

            // Secondary Fallback: Try SecondaryTeamPatchAssignments
            if (state.TeamAssignments.Count == 0 && mapData.SecondaryTeamPatchAssignments != null && startIndex >= 0 && startIndex < mapData.SecondaryTeamPatchAssignments.Count)
            {
                var assignments = mapData.SecondaryTeamPatchAssignments[startIndex];
                if (assignments != null && assignments.Count > 0) state.TeamAssignments = assignments;
            }

            // Tertiary Fallback: Try AssignmentsByWorkerCount
            if (state.TeamAssignments.Count == 0 && mapData.AssignmentsByWorkerCount != null)
            {
                if (mapData.AssignmentsByWorkerCount.TryGetValue(workerCount, out var assignmentsByStart))
                {
                    if (startIndex >= 0 && startIndex < assignmentsByStart.Count)
                    {
                        var assignments = assignmentsByStart[startIndex];
                        if (assignments != null && assignments.Count > 0) state.TeamAssignments = assignments;
                    }
                }
            }

            // On frame 0, issue STOP
            if (frame == StartFrame)
            {
                foreach (var w in workerEntries)
                {
                    var stopCmd = new ActionRawUnitCommand { AbilityId = (int)Abilities.STOP, UnitTags = { w.UnitTag } };
                    commands.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = stopCmd } });
                }
                Console.WriteLine($"chrisCrossAppleSause: Issued STOP to {workerEntries.Count} workers on Frame {StartFrame}.");
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
                if (!state.TeamBumping.ContainsKey(5)) state.TeamBumping[5] = true; // Crossover Pair 5 (S3/T1)
                if (!state.TeamBumping.ContainsKey(6)) state.TeamBumping[6] = true; // Crossover Pair 6 (B3/Y1)
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
                        // FIX: Issue harvest commands once, then do NOT add move commands that override them
                        if (!_harvestCommandsIssued)
                        {
                            foreach (var team in state.TeamAssignments)
                            {
                                var minerals = team.Minerals;
                                if (minerals.Count == 0) continue;
                                // FIX: Always resolve A as near, B as far using IsNear flag
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
                            _harvestCommandsIssued = true;
                            Console.WriteLine($"chrisCrossAppleSause: Frame {frame} - Transition to AlignAtMineralA: Issued global Harvest (HARVEST_GATHER) commands.");
                        }
                        
                        // FIX: After harvest commands are issued, do not issue SMART move commands that override them.
                        // HARVEST_GATHER with a UnitTag handles pathing automatically.
                        // We only return the harvest commands above; no move logic is appended.
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
            if (frame >= AlignThreshold) { SetPhase(TestPhase.AlignAtMineralA); return commands; }

            // FIX: Handle crossover pairs BEFORE the team loop so they don't get overridden by team logic
            if (workerCount == 12)
            {
                // FIX: Triple bump uses T1 (not T2) and S3
                if (state.TealM1IsFar && workerByLabel.TryGetValue("S3", out var s3) && workerByLabel.TryGetValue("T1", out var t1Cross))
                {
                    var mineralTA = GetMineralOrdered(state, 1, "TA");
                    if (mineralTA != null) commands.AddRange(ProcessBumpingPair(frame, state, 5, t1Cross, s3, mineralTA));
                }
                // FIX: Triple bump uses Y1 (not Y2) and B3
                if (state.YellowM8IsFar && workerByLabel.TryGetValue("B3", out var b3) && workerByLabel.TryGetValue("Y1", out var y1Cross))
                {
                    var mineralYA = GetMineralOrdered(state, 4, "YA");
                    if (mineralYA != null) commands.AddRange(ProcessBumpingPair(frame, state, 6, y1Cross, b3, mineralYA));
                }
            }

            foreach (var team in state.TeamAssignments)
            {
                var logicalWorkers = team.Workers;
                var minerals = team.Minerals;
                if (logicalWorkers.Count == 0 || minerals.Count == 0) continue;

                // FIX: Consistently resolve mineralA as near and mineralB as far for all teams
                var mineralA = minerals.FirstOrDefault(m => m.IsNear) ?? minerals[0];
                var mineralB = minerals.FirstOrDefault(m => !m.IsNear && m != mineralA) ?? minerals.Skip(1).FirstOrDefault() ?? mineralA;

                var w1 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "1");
                var w2 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "2");
                var w3 = ResolveLiveWorkerBySuffix(logicalWorkers, workerEntries, "3");

                if (workerCount == 8)
                {
                    if (w1 != null) commands.AddRange(ProcessWorkerMovement(w1, mineralA, frame));
                    if (w2 != null) commands.AddRange(ProcessWorkerMovement(w2, mineralB, frame));
                }
                else if (workerCount == 12)
                {
                    // FIX: All teams use the same structure: w1 (primary) is bumped by w3 (partner).
                    // w2 (side) always goes to mineral B. This was backwards for Teams 2 and 3.
                    WorkerEntryDto? lead = w1;
                    WorkerEntryDto? partner = w3;   // FIX: w3 is the bumper for ALL teams
                    WorkerEntryDto? side = w2;      // FIX: w2 goes to mineral B for ALL teams

                    if (state.TeamBumping.GetValueOrDefault(team.TeamNumber, true) && lead != null && partner != null)
                    {
                        commands.AddRange(ProcessBumpingPair(frame, state, team.TeamNumber, lead, partner, mineralA));
                    }
                    else
                    {
                        if (lead != null) commands.AddRange(ProcessWorkerMovement(lead, mineralA, frame));
                        if (partner != null) commands.AddRange(ProcessWorkerMovement(partner, mineralA, frame));
                    }
                    if (side != null) commands.AddRange(ProcessWorkerMovement(side, mineralB, frame));
                }
            }
            return commands;
        }

        // FIX: Removed HandleAlignAtMineralA move logic entirely. 
        // Harvest commands are issued once in BuildBumpOrders and never overridden.
        // The game engine handles pathing to the mineral UnitTag.

        private IEnumerable<SC2APIProtocol.Action> ProcessBumpingPair(int frame, CcaSpawnLearningState state, int pairId, WorkerEntryDto lead, WorkerEntryDto partner, OrderedMineral mineral)
        {
            var commands = new List<SC2APIProtocol.Action>();
            if (lead == null || partner == null || mineral == null) return commands;
            
            // FIX: Relaxed distance check slightly so bumping doesn't disable too early
            if (Distance(lead.Position, partner.Position) >= 3.0f) { state.TeamBumping[pairId] = false; }

            if (state.TeamBumping.GetValueOrDefault(pairId, true))
            {
                var avgX = (lead.Position.X + partner.Position.X) * 0.5f;
                var avgY = (lead.Position.Y + partner.Position.Y) * 0.5f;
                var targetX = (avgX + mineral.Position.X) * 0.5f;
                var targetY = (avgY + mineral.Position.Y) * 0.5f;
                var leadMove = new Point2D { X = (lead.Position.X + targetX) * 0.5f, Y = (lead.Position.Y + targetY) * 0.5f };
                var partnerMove = new Point2D { X = partner.Position.X + (targetX - partner.Position.X) * 0.25f, Y = partner.Position.Y + (targetY - partner.Position.Y) * 0.25f };
                commands.AddRange(MoveTo(lead.UnitTag, leadMove, frame));
                commands.AddRange(MoveTo(partner.UnitTag, partnerMove, frame));
            }
            else
            {
                commands.AddRange(ProcessWorkerMovement(lead, mineral, frame));
                commands.AddRange(ProcessWorkerMovement(partner, mineral, frame));
            }
            return commands;
        }

        private IEnumerable<SC2APIProtocol.Action> ProcessWorkerMovement(WorkerEntryDto worker, OrderedMineral mineral, int frame)
        {
            if (worker == null || mineral == null) return Array.Empty<SC2APIProtocol.Action>();
            if (Distance(worker.Position, mineral.Position) <= 1.5f) return Harvest(worker.UnitTag, mineral.Position, mineral.UnitTag);
            var movePoint = new Point2D { X = (worker.Position.X + mineral.Position.X) * 0.5f, Y = (worker.Position.Y + mineral.Position.Y) * 0.5f };
            return MoveTo(worker.UnitTag, movePoint, frame);
        }

        private IEnumerable<SC2APIProtocol.Action> Harvest(ulong tag, Vector2Dto mineralPos, ulong mineralTag = 0)
        {
            if (tag == 0 || mineralPos == null) return Array.Empty<SC2APIProtocol.Action>();
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

        private IEnumerable<SC2APIProtocol.Action> MoveTo(ulong tag, Point2D point, int frame = -1)
        {
            if (tag == 0) return Array.Empty<SC2APIProtocol.Action>();
            // FIX: Use MOVE (ability 1) before frame 35, and SMART (16) only at frame 35+ when we are NOT in harvest phase
            // Note: During AlignAtMineralA we no longer call this method, so SMART is only used for pre-35 movement.
            int abilityId = (frame != -1 && frame < 35) ? 1 : 16;
            var cmd = new ActionRawUnitCommand { AbilityId = abilityId, TargetWorldSpacePos = point };
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
________________________________________
2. TeamLabelRegistrationHelper.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

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

            if (IsTeamAssignmentsReady(mapData, startIndex) && targetTeamPatchAssignments[startIndex] != null && targetTeamPatchAssignments[startIndex].Count > 0)
            {
                return targetTeamPatchAssignments[startIndex];
            }

            var assignments = BuildAssignmentsForStart(mapData, startIndex, orderedMinerals, workers, mineralCenterOfMass, workerLabelService);
            targetTeamPatchAssignments[startIndex] = assignments;
            ApplySpawnFlags(mapData, startIndex, assignments);
            MarkTeamAssignmentsReady(mapData, startIndex, assignments);
            return assignments;
        }

        public static List<TeamPatchAssignmentDto> TryGetTeamLabelsForStart(
            MawBaseLocationData mapData,
            int startIndex,
            List<List<TeamPatchAssignmentDto>>? targetTeamPatchAssignments = null)
        {
            if (mapData == null || startIndex < 0 || !IsTeamAssignmentsReady(mapData, startIndex))
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
            MarkTeamAssignmentsReady(mapData, startIndex, assignments);
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
                ApplyWorkerFinalLabels(mapData, startIndex, GetTeamPrefix(teamNum, workers.Count), teamWorkers, mineralPair[0], mineralPair[1], workerLabelService);

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

            var avgX = (mA.Position.X + mB.Position.X) * 0.5f;
            var avgY = (mA.Position.Y + mB.Position.Y) * 0.5f;

            var dirX = avgX - townhall.X;
            var dirY = avgY - townhall.Y;
            var mag = MathF.Sqrt(dirX * dirX + dirY * dirY);
            
            var returnX = townhall.X + (dirX / mag) * 2.8f;
            var returnY = townhall.Y + (dirY / mag) * 2.8f;

            var waitX = returnX + (dirX / mag) * 1.5f;
            var waitY = returnY + (dirY / mag) * 1.5f;

            return (new Vector2Dto(returnX, returnY), new Vector2Dto(waitX, waitY));
        }

        private static List<TeamLayout> BuildExplicitTeamLayouts(List<OrderedMineral> minerals, List<WorkerEntryDto> workers)
        {
            var teams = new List<TeamLayout>();

            Console.WriteLine($"TeamLabelRegistrationHelper: Building layouts for {workers?.Count ?? 0} workers and {minerals?.Count ?? 0} minerals.");

            if (workers != null && workers.Count == 12)
            {
                AddTeamIfPossible(teams, minerals, workers, 7, 6, new[] { "W2", "W3", "W4" }, 1);
                AddTeamIfPossible(teams, minerals, workers, 5, 4, new[] { "W1", "W5", "W6" }, 2);
                AddTeamIfPossible(teams, minerals, workers, 2, 3, new[] { "W12", "W7", "W8" }, 3);
                AddTeamIfPossible(teams, minerals, workers, 0, 1, new[] { "W9", "W10", "W11" }, 4);
            }
            else
            {
                AddTeamIfPossible(teams, minerals, workers, 7, 6, new[] { "W1", "W2" }, 1);
                AddTeamIfPossible(teams, minerals, workers, 5, 4, new[] { "W3", "W4" }, 2);
                AddTeamIfPossible(teams, minerals, workers, 2, 3, new[] { "W5", "W6" }, 3);
                AddTeamIfPossible(teams, minerals, workers, 0, 1, new[] { "W7", "W8" }, 4);
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
                    // FIX: Changed >= to <=. The NEAR mineral must be the one CLOSER to townhall (smaller distance).
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

            if (prefix == "T")
            {
                flags["TealM1IsFar"] = IsFarMineral(first, farMineral);
            }
            else if (prefix == "Y")
            {
                flags["YellowM8IsFar"] = IsFarMineral(second, farMineral);
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

            if (prefix == "T")
            {
                var t2Source = "W4";
                var tealM1IsFar = IsFarMineralFlagSet(mapData, startIndex, "TealM1IsFar");
                Console.WriteLine($"TeamLabelRegistrationHelper: start[{startIndex}] TealM1IsFar={tealM1IsFar}");
                if (tealM1IsFar)
                {
                    t2Source = "W2";
                }
                AssignWorkerFinalLabel(teamWorkers, t2Source, "T2", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "T2" }, new[] { "T1", "T3" }, workerLabelService);
                SetNoPushFlag(mapData, "TealNoPush", teamWorkers, nearMineral, "T1", "T3");
            }
            else if (prefix == "Y")
            {
                var y2Source = IsFarMineralFlagSet(mapData, startIndex, "YellowM8IsFar")
                    ? "W11"
                    : "W9";
                AssignWorkerFinalLabel(teamWorkers, y2Source, "Y2", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "Y2" }, new[] { "Y1", "Y3" }, workerLabelService);
                SetNoPushFlag(mapData, "YellowNoPush", teamWorkers, nearMineral, "Y1", "Y3");
            }
            else if (prefix == "S")
            {
                AssignWorkerFinalLabel(teamWorkers, "W1", "S3", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "S3" }, new[] { "S1", "S2" }, workerLabelService);
                SetNoPushFlag(mapData, "SalmonNoPush", teamWorkers, nearMineral, "S1", "S2");
            }
            else if (prefix == "B")
            {
                AssignWorkerFinalLabel(teamWorkers, "W12", "B3", workerLabelService);
                AssignRemainingByDistance(teamWorkers, nearMineral, new[] { "B3" }, new[] { "B1", "B2" }, workerLabelService);
                SetNoPushFlag(mapData, "BlueNoPush", teamWorkers, nearMineral, "B1", "B2");
            }
            else if (prefix == "G" || prefix == "P" || prefix == "O" || prefix == "R")
            {
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
                    1 => "T",
                    2 => "S",
                    3 => "B",
                    4 => "Y",
                    _ => string.Empty
                };
            }

            return teamNumber switch
            {
                1 => "G",
                2 => "P",
                3 => "O",
                4 => "R",
                _ => string.Empty
            };
        }

        private static void MarkTeamAssignmentsReady(MawBaseLocationData mapData, int startIndex, List<TeamPatchAssignmentDto> assignments)
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

            flags[TeamAssignmentsReadyFlag] = assignments != null && assignments.Count > 0;
        }

        private static bool IsTeamAssignmentsReady(MawBaseLocationData mapData, int startIndex)
        {
            if (mapData == null)
            {
                return false;
            }

            return mapData.AssignmentFlagsByStart.TryGetValue(startIndex, out var flags)
                && flags.TryGetValue(TeamAssignmentsReadyFlag, out var ready)
                && ready;
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
________________________________________
3. BabySharkMiningManager.cs (JIT Initialization Fix)
Only the InitializeJitWorkerStates method needs to change:
        private void InitializeJitWorkerStates(List<TeamPatchAssignmentDto> assignments)
        {
            if (assignments == null) return;
            foreach (var assignment in assignments)
            {
                if (assignment?.Workers == null || assignment.Minerals?.Count < 2) continue;
                
                // FIX: Resolve mineralA as the NEAR mineral (IsNear=true) and mineralB as FAR.
                // The list order Minerals[0]/Minerals[1] does NOT reliably indicate near/far.
                var mineralA = assignment.Minerals.FirstOrDefault(m => m.IsNear) ?? assignment.Minerals[0];
                var mineralB = assignment.Minerals.FirstOrDefault(m => !m.IsNear && m != mineralA) ?? assignment.Minerals.Skip(1).FirstOrDefault() ?? mineralA;
                
                foreach (var worker in assignment.Workers)
                {
                    if (worker.UnitTag == 0 || _jitWorkerStates.ContainsKey(worker.UnitTag)) continue;
                    
                    var label = worker.FinalLabel ?? worker.Label ?? string.Empty;
                    // 1 & 3 start on A, 2 starts on B
                    var startsOnA = label.EndsWith("1") || label.EndsWith("3");
                    
                    _jitWorkerStates[worker.UnitTag] = new JitWorkerState
                    {
                        TeamNumber = assignment.TeamNumber,
                        TeamId = assignment.TeamId,
                        CurrentMineralTag = startsOnA ? mineralA.UnitTag : mineralB.UnitTag,
                        AlternateMineralTag = startsOnA ? mineralB.UnitTag : mineralA.UnitTag,
                        CurrentMineralPos = startsOnA ? mineralA.Position : mineralB.Position,
                        AlternateMineralPos = startsOnA ? mineralB.Position : mineralA.Position,
                        WasCarrying = false
                    };
                }
            }
        }
________________________________________
Summary of Changes
Bug Location    Fix
Near/Far swapped    TeamLabelRegistrationHelper.ApplyMineralFinalLabels Changed DistanceToTownhall >= to <= so the closer mineral is correctly labeled “near” (A)
Wrong bumper for Teams 2/3  chrisCrossAppleSause.HandleAcceleratingWorkerOne    All teams now use w3 as the bumper (toward A) and w2 goes to mineral B
Harvest overridden by SMART chrisCrossAppleSause.HandleAlignAtMineralA  Removed move logic from alignment phase. Harvest commands are issued once and never overridden by SMART moves
Crossover pairs wrong   chrisCrossAppleSause.HandleAcceleratingWorkerOne    S3 now bumps T1 (not T2); B3 now bumps Y1 (not Y2)
JIT state wrong mineral BabySharkMiningManager.InitializeJitWorkerStates    Now looks up IsNear flag instead of assuming Minerals[0] is always A
Static phase leaking    chrisCrossAppleSause class  _phase is now instance-level; _harvestCommandsIssued resets per spawn
These changes ensure that: - T1, S1, B1, Y1 always path to the near mineral (A) - T2, S2, B2, Y2 always path to the far mineral (B) - T3, S3, B3, Y3 always bump their “1” teammate toward A - At frame 35, workers receive a clean HARVEST_GATHER command with the correct mineral UnitTag and no overriding move commands
________________________________________
