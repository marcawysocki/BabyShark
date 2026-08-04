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
using System.Numerics;

#nullable enable

namespace BabySharkBot.Services
{
    /// <summary>
    /// Domain Metaphor: "chrisCrossAppleSause" — Criss-cross applesauce (sitting cross-legged).
    /// Purpose: Handles worker initialization and setup. Arranges units per team
    /// and broadcasts initial briefing/orders.
    /// </summary>
    public sealed class chrisCrossAppleSause
    {
        public enum TestPhase { Idle, AssigningWorkers, AcceleratingWorkerOne, AlignAtMineralA, CancelAndReturnHome }
        
        public const int StartFrame = 0;
        public const int AccelerateThreshold = 15;
        public const int AlignThreshold = 35;
        public const int EndThreshold = 65;

        private readonly Dictionary<string, CcaSpawnLearningState> _states = new Dictionary<string, CcaSpawnLearningState>(StringComparer.OrdinalIgnoreCase);

        public TestPhase CurrentPhase(MawBaseLocationData mapData, int startIndex)
        {
            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);
            return state.Phase;
        }

        public CcaSpawnLearningState GetOrCreateCurrentSpawnState(MawBaseLocationData mapData, int startIndex)
        {
            var key = BuildSpawnKey(mapData, startIndex);
            if (!_states.TryGetValue(key, out var state))
            {
                state = new CcaSpawnLearningState
                {
                    SpawnKey = key,
                    StartIndex = startIndex,
                    M1IsFar = startIndex >= 0 && mapData?.M1IsFar != null && startIndex < mapData.M1IsFar.Length && mapData.M1IsFar[startIndex],
                    M8IsFar = startIndex >= 0 && mapData?.M8IsFar != null && startIndex < mapData.M8IsFar.Length && mapData.M8IsFar[startIndex],
                    Phase = TestPhase.Idle,
                    HarvestCommandsIssued = false
                };
                _states[key] = state;
            }
            return state;
        }

        public CcaSpawnLearningState GetCurrentSpawnState(MawBaseLocationData mapData, int startIndex)
        {
            return GetOrCreateCurrentSpawnState(mapData, startIndex);
        }

        public void SetPhase(CcaSpawnLearningState state, TestPhase phase)
        {
            if (state.Phase == phase) return;
            state.Phase = phase;
            Console.WriteLine($"chrisCrossAppleSause [{state.SpawnKey}]: Phase changed to {state.Phase}");
        }

        public void EnableCcaMiningForCurrentSpawn(MawBaseLocationData mapData, int startIndex)
        {
            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);
            state.CcaMining = true;
            Settings.ccaMining = true;
            if (state.Phase == TestPhase.Idle) SetPhase(state, TestPhase.AssigningWorkers);
        }

        public IEnumerable<SC2APIProtocol.Action> BuildBumpOrders(int frame, MawBaseLocationData mapData, int startIndex, IReadOnlyList<WorkerEntryDto> workerEntries, IEnumerable<UnitCommander>? commanders = null, IEnumerable<Unit>? selfUnits = null)
        {
            var commands = new List<SC2APIProtocol.Action>();
            if (!Settings.ccaMining || mapData == null || workerEntries == null || workerEntries.Count == 0) return commands;

            var state = GetOrCreateCurrentSpawnState(mapData, startIndex);

            // Frequency Logic: Idle/Assigning run every 5 frames. Accelerating/Aligning run EVERY frame.
            bool isHighFrequencyPhase = state.Phase == TestPhase.AcceleratingWorkerOne || state.Phase == TestPhase.AlignAtMineralA;
            if (frame % 5 != 0 && !isHighFrequencyPhase)
            {
                return commands;
            }

            var workerCount = Settings.WorkerCount > 0 ? Settings.WorkerCount : 12;
            var currentAssignments = OngoingMapData.ResolveTeamAssignments(mapData, startIndex);
            if (!HasValidCurrentSpawnAssignments(mapData, startIndex, currentAssignments, workerEntries, selfUnits))
            {
                // Never substitute another spawn's assignments. Failing closed is safer than
                // issuing a valid command to the wrong mineral line.
                state.TeamAssignments = new List<TeamPatchAssignmentDto>();
                return commands;
            }

            state.TeamAssignments = currentAssignments;

            // On frame 0, issue STOP
            if (frame == StartFrame)
            {
                foreach (var w in workerEntries)
                {
                    var stopCmd = new ActionRawUnitCommand { AbilityId = (int)Abilities.STOP, UnitTags = { w.UnitTag } };
                    commands.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = stopCmd } });
                }
                Console.WriteLine($"chrisCrossAppleSause [{state.SpawnKey}]: Issued STOP to {workerEntries.Count} workers on Frame {StartFrame}.");
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
                // Team numbers are Y=1, B=2, S=3, T=4 in TeamLabelRegistrationHelper.
                // Only Teal and Yellow use CCA push-repell pairs; Salmon and Blue remain direct.
                state.TeamBumping[1] = true;
                state.TeamBumping[2] = false;
                state.TeamBumping[3] = false;
                state.TeamBumping[4] = true;
                state.TeamBumping[5] = false;
                state.TeamBumping[6] = false;
            }

            bool stateChanged = true;
            while (stateChanged)
            {
                stateChanged = false;
                switch (state.Phase)
                {
                    case TestPhase.Idle:
                        if (state.TeamAssignments.Any()) { SetPhase(state, TestPhase.AssigningWorkers); stateChanged = true; }
                        break;
                    case TestPhase.AssigningWorkers:
                        if (state.TeamAssignments.Any()) { SetPhase(state, TestPhase.AcceleratingWorkerOne); stateChanged = true; }
                        break;
                    case TestPhase.AcceleratingWorkerOne:
                        var accCommands = HandleAcceleratingWorkerOne(frame, mapData, state, workerEntries, workerCount, workerByLabel);
                        if (state.Phase == TestPhase.AlignAtMineralA && !accCommands.Any()) { stateChanged = true; continue; }
                        commands.AddRange(accCommands);
                        return commands;
                    case TestPhase.AlignAtMineralA:
                        // After frame 35, once we transition to alignment, immediately issue harvest commands to all workers
                        if (!state.HarvestCommandsIssued)
                        {
                            var harvestedTags = new HashSet<ulong>();
                            foreach (var team in state.TeamAssignments)
                            {
                                var minerals = team.Minerals;
                                if (minerals.Count == 0) continue;
                                var mineralA = minerals.FirstOrDefault(m => m.IsNear) ?? minerals[0];
                                var mineralB = minerals.FirstOrDefault(m => !m.IsNear && m != mineralA) ?? minerals.Skip(1).FirstOrDefault() ?? mineralA;

                                var w1 = ResolveLiveWorkerBySuffix(team.Workers, workerEntries, "1");
                                var w2 = ResolveLiveWorkerBySuffix(team.Workers, workerEntries, "2");
                                var w3 = ResolveLiveWorkerBySuffix(team.Workers, workerEntries, "3");

                                if (mineralA.UnitTag == 0) Console.WriteLine($"[WARN] {state.SpawnKey} Team {team.TeamNumber} mineralA ({mineralA.Label}) has UnitTag=0");
                                if (mineralB.UnitTag == 0) Console.WriteLine($"[WARN] {state.SpawnKey} Team {team.TeamNumber} mineralB ({mineralB.Label}) has UnitTag=0");

                                if (commanders != null)
                                {
                                    if (w1 != null && mineralA.UnitTag != 0) { var c1 = commanders.FirstOrDefault(c => c.UnitCalculation.Unit.Tag == w1.UnitTag); if (c1 != null) { commands.AddRange(c1.Order(frame, Abilities.HARVEST_GATHER, null, mineralA.UnitTag)); harvestedTags.Add(w1.UnitTag); } }
                                    if (w2 != null && mineralB.UnitTag != 0) { var c2 = commanders.FirstOrDefault(c => c.UnitCalculation.Unit.Tag == w2.UnitTag); if (c2 != null) { commands.AddRange(c2.Order(frame, Abilities.HARVEST_GATHER, null, mineralB.UnitTag)); harvestedTags.Add(w2.UnitTag); } }
                                    if (w3 != null && mineralA.UnitTag != 0) { var c3 = commanders.FirstOrDefault(c => c.UnitCalculation.Unit.Tag == w3.UnitTag); if (c3 != null) { commands.AddRange(c3.Order(frame, Abilities.HARVEST_GATHER, null, mineralA.UnitTag)); harvestedTags.Add(w3.UnitTag); } }
                                }
                                else
                                {
                                    if (w1 != null && mineralA.UnitTag != 0) { commands.AddRange(Harvest(w1.UnitTag, mineralA.Position, mineralA.UnitTag)); harvestedTags.Add(w1.UnitTag); }
                                    if (w2 != null && mineralB.UnitTag != 0) { commands.AddRange(Harvest(w2.UnitTag, mineralB.Position, mineralB.UnitTag)); harvestedTags.Add(w2.UnitTag); }
                                    if (w3 != null && mineralA.UnitTag != 0) { commands.AddRange(Harvest(w3.UnitTag, mineralA.Position, mineralA.UnitTag)); harvestedTags.Add(w3.UnitTag); }
                                }
                            }

                            // Safety-net: Harvest for any worker not explicitly assigned
                            foreach (var w in workerEntries)
                            {
                                if (!harvestedTags.Contains(w.UnitTag))
                                {
                                    var closestMineral = state.TeamAssignments.SelectMany(t => t.Minerals).Where(m => m.UnitTag != 0).OrderBy(m => Distance(w.Position, m.Position)).FirstOrDefault();
                                    if (closestMineral != null)
                                    {
                                        commands.AddRange(Harvest(w.UnitTag, closestMineral.Position, closestMineral.UnitTag));
                                        harvestedTags.Add(w.UnitTag);
                                        Console.WriteLine($"[SAFETY] {state.SpawnKey}: Safety-net harvest for worker {w.UnitTag} ({w.FinalLabel}) to mineral {closestMineral.Label}");
                                    }
                                }
                            }

                            state.HarvestCommandsIssued = true;
                            Console.WriteLine($"chrisCrossAppleSause [{state.SpawnKey}]: Frame {frame} - Transition to AlignAtMineralA: Issued global Harvest commands.");
                        }
                        
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
            if (frame >= AlignThreshold) { SetPhase(state, TestPhase.AlignAtMineralA); return commands; }

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
                    if (w1 != null) commands.AddRange(ProcessWorkerMovement(w1, mineralA, frame));
                    if (w2 != null) commands.AddRange(ProcessWorkerMovement(w2, mineralB, frame));
                }
                else if (workerCount == 12)
                {
                    if (team.TeamNumber == 2 || team.TeamNumber == 3)
                    {
                        if (w1 != null) commands.AddRange(ProcessWorkerMovement(w1, mineralA, frame));
                        if (w2 != null) commands.AddRange(ProcessWorkerMovement(w2, mineralB, frame));
                        if (w3 != null)
                        {
                            var helperMineral = team.TeamNumber == 2
                                ? GetMineralOrdered(state, 3, "SA")
                                : GetMineralOrdered(state, 4, "BA");
                            commands.AddRange(ProcessWorkerMovement(w3, helperMineral ?? mineralA, frame));
                        }
                        continue;
                    }

                    // Teal and Yellow use w1/w3 as the side-by-side push pair.
                    // Salmon and Blue use direct A/B movement; their w3 worker is a
                    // normal target mover and must not enter the bump state machine.
                    WorkerEntryDto? lead = w1;
                    WorkerEntryDto? partner = w3;
                    WorkerEntryDto? side = w2;

                    if (state.TeamBumping.GetValueOrDefault(team.TeamNumber, false) && lead != null && partner != null)
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

        private IEnumerable<SC2APIProtocol.Action> HandleAlignAtMineralA(int frame, MawBaseLocationData mapData, CcaSpawnLearningState state, IReadOnlyList<WorkerEntryDto> workerEntries)
        {
            var commands = new List<SC2APIProtocol.Action>();
            if (frame > EndThreshold) { SetPhase(state, TestPhase.CancelAndReturnHome); return commands; }

            // FIX: Removed move logic from alignment phase. 
            // Harvest commands are issued once (in the state machine above) and never overridden by SMART moves here.
            return commands;
        }

        /// <summary>
        /// Mineral-walk aware movement. 
        /// If far: MOVE toward mineral. 
        /// If close (<= 2.5f): SMART on mineral tag (collision-free phase-through).
        /// If at mineral (<= 1.5f): HARVEST_GATHER.
        /// </summary>
        private IEnumerable<SC2APIProtocol.Action> ProcessWorkerMovement(
            WorkerEntryDto worker,
            OrderedMineral mineral,
            int frame)
        {
            if (worker == null || mineral == null)
                return Array.Empty<SC2APIProtocol.Action>();

            float dist = Distance(worker.Position, mineral.Position);

            // Already there — lock into mining
            if (dist <= 1.5f)
            {
                if (mineral.UnitTag != 0)
                    return Harvest(worker.UnitTag, mineral.Position, mineral.UnitTag);
                else
                    return Harvest(worker.UnitTag, mineral.Position, 0);  // position-only gather
            }

            // Close enough to mineral-walk through other workers
            if (dist <= 2.5f && mineral.UnitTag != 0)
                return SmartToMineral(worker.UnitTag, mineral.UnitTag);

            // Still approaching — normal move toward mineral
            var movePoint = new Point2D
            {
                X = (worker.Position.X + mineral.Position.X) * 0.5f,
                Y = (worker.Position.Y + mineral.Position.Y) * 0.5f
            };

            return MoveTo(worker.UnitTag, movePoint, frame);
        }

        /// <summary>
        /// The mineral-walk command. SMART on mineral tag = collision disabled.
        /// </summary>
        private IEnumerable<SC2APIProtocol.Action> SmartToMineral(ulong workerTag, ulong mineralTag)
        {
            if (workerTag == 0 || mineralTag == 0) return Array.Empty<SC2APIProtocol.Action>();
            var cmd = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.SMART,
                TargetUnitTag = mineralTag
            };
            cmd.UnitTags.Add(workerTag);
            return new List<SC2APIProtocol.Action>
            {
                new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = cmd } }
            };
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
                var partnerMove = new Point2D { X = (partner.Position.X + targetX) * 0.5f, Y = (partner.Position.Y + targetY) * 0.5f };
                
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

        private IEnumerable<SC2APIProtocol.Action> MoveTo(ulong tag, Point2D point, int frame = -1)
        {
            if (tag == 0) return Array.Empty<SC2APIProtocol.Action>();
            // Use AbilityId 1 (MOVE) before frame 35, and AbilityId 16 (SMART) at/after frame 35 for handoff.
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

        private static bool HasValidCurrentSpawnAssignments(
            MawBaseLocationData mapData,
            int startIndex,
            IReadOnlyCollection<TeamPatchAssignmentDto> assignments,
            IReadOnlyCollection<WorkerEntryDto> workerEntries,
            IEnumerable<Unit>? selfUnits)
        {
            if (mapData == null || startIndex < 0 || assignments == null || assignments.Count == 0
                || mapData.StartingTownHall == null || startIndex >= mapData.StartingTownHall.Length
                || mapData.StartingTownHall[startIndex] == null)
            {
                return false;
            }

            var currentMinerals = startIndex < mapData.OrderedMainMinerals.Count
                ? mapData.OrderedMainMinerals[startIndex]
                : new List<OrderedMineral>();
            if (currentMinerals.Count == 0)
            {
                return false;
            }

            var currentWorkerTags = (selfUnits ?? Enumerable.Empty<Unit>())
                .Where(unit => unit != null && IsWorkerType(unit.UnitType))
                .Select(unit => unit.Tag)
                .ToHashSet();
            if (currentWorkerTags.Count == 0)
            {
                currentWorkerTags = (workerEntries ?? Array.Empty<WorkerEntryDto>())
                    .Where(worker => worker != null && worker.UnitTag != 0)
                    .Select(worker => worker.UnitTag)
                    .ToHashSet();
            }

            var currentMineralTags = currentMinerals
                .Where(m => m != null && m.UnitTag != 0)
                .Select(m => m.UnitTag)
                .ToHashSet();
            var currentPositions = currentMinerals
                .Where(m => m?.Position != null)
                .Select(m => $"{m.Position.X:F2},{m.Position.Y:F2}")
                .ToHashSet(StringComparer.Ordinal);

            return assignments.All(team => team != null
                && team.Workers != null
                && team.Workers.Count > 0
                && team.Workers.All(worker => worker != null && worker.UnitTag != 0 && currentWorkerTags.Contains(worker.UnitTag))
                && team.Minerals != null
                && team.Minerals.Count > 0
                && team.Minerals.All(mineral => mineral?.Position != null
                    && ((mineral.UnitTag != 0 && currentMineralTags.Contains(mineral.UnitTag))
                        || currentPositions.Contains($"{mineral.Position.X:F2},{mineral.Position.Y:F2}"))));
        }

        private static bool IsWorkerType(uint unitType)
        {
            return unitType == (uint)UnitTypes.ZERG_DRONE
                || unitType == (uint)UnitTypes.TERRAN_SCV
                || unitType == (uint)UnitTypes.PROTOSS_PROBE;
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

            if (mineralLabelService == null || state.TeamAssignments.Any(t => t.Minerals.Any(m => m.UnitTag == 0)))
            {
                Console.WriteLine($"[WARN] {state.SpawnKey} Some minerals lack UnitTag. Ensure RecordSpawnObservation receives MineralLabelService.");
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
        public bool M1IsFar { get; set; }
        public bool M8IsFar { get; set; }
        public List<TeamPatchAssignmentDto> TeamAssignments { get; set; } = new List<TeamPatchAssignmentDto>();
        public Dictionary<int, bool> TeamBumping { get; set; } = new Dictionary<int, bool>();
        public Dictionary<ulong, Vector2Dto> InitialPositions { get; set; } = new Dictionary<ulong, Vector2Dto>();

        // NEW: Per-spawn phase machine
        public chrisCrossAppleSause.TestPhase Phase { get; set; } = chrisCrossAppleSause.TestPhase.Idle;
        public bool HarvestCommandsIssued { get; set; } = false;
    }
}
