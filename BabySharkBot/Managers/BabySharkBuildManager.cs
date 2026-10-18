
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SC2APIProtocol;
using Sharky;
using Sharky.Builds.BuildingPlacement;
using Sharky.Managers;
using Sharky.DefaultBot;
using BabySharkBot.Services;
using BabySharkBot.Setup;
using System.Linq;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Manager that executes BabyShark builds. Replaces Sharky's BuildManager + ProductionCommander pipeline.
    /// Integrates with DefaultSharkyBot's MacroData to express production desires.
    /// Registered after ObservationManager so build decisions consume the current observation snapshot.
    /// </summary>
    public class BabySharkBuildManager : IManager
    {
        public bool NeverSkip { get; set; } = true;
        public bool SkipFrame { get; set; } = false;
        public double LongestFrame { get; set; } = 0;
        public double TotalFrameTime { get; set; } = 0;

        private readonly DefaultSharkyBot _defaultBot;
        private BabySharkBot.Builds.BabySharkBuild? _activeBuild;
        private WorkerLabelService _workerLabelService;
        private MineralLabelService _mineralLabelService;
        private VespeneLabelService _vespeneLabelService;
        private SpawningPoolPlacementService _spawningPoolPlacementService;
        private MacroHatcheryPlacementService _macroHatcheryPlacementService;
        private bool _started;
        private bool _labelsInitialized;
        private int _greedyChainStartIndex = -1;
        private int _greedyChainWorkerCount = -1;
        private bool _initialAssignmentLatched;
        private bool _workerCountTransitionApplied;
        private int _lastObservedWorkerCount = -1;
        private bool _ccawStartupSummaryLogged;
        private readonly HashSet<ulong> _jitMhStartupLogs = new HashSet<ulong>();
        private readonly HashSet<ulong> _ccawStartupLogs = new HashSet<ulong>();
        private readonly HashSet<ulong> _bumpStartupLogs = new HashSet<ulong>();
        private int _macroHatcheryNextAttemptFrame;
        private string _macroHatcheryLastError = string.Empty;
        private bool _macroHatcheryReferenceLogged;
        private readonly OngoingMapData _ongoingMapData = new OngoingMapData();
        private bool _frameZeroInstructionListsWritten;
        // Temp-drone onboarding (owner spec 2026-10-13): the copied team.Workers
        // entries of onboarded temps (13-B3, 14-S3, 15-B4), re-synced from the live
        // snapshot every frame, plus one [TEMP DRONE SKIP] line per extra drone.
        private readonly Dictionary<ulong, WorkerEntryDto> _onboardedTempEntries = new Dictionary<ulong, WorkerEntryDto>();
        private readonly HashSet<ulong> _extraDroneReported = new HashSet<ulong>();

        // 13-B3 extractor trick driver (owner spec): the book loaded at onboarding
        public BabySharkBuildManager(DefaultSharkyBot defaultBot)
        {
            _defaultBot = defaultBot;
        }

        public void SetBuild(BabySharkBot.Builds.BabySharkBuild build)
        {
            _activeBuild = build;
            _started = false;
            _labelsInitialized = false;
            _greedyChainStartIndex = -1;
            _greedyChainWorkerCount = -1;
            _initialAssignmentLatched = false;
            _workerCountTransitionApplied = false;
            _lastObservedWorkerCount = -1;
            _ccawStartupSummaryLogged = false;
            _frameZeroInstructionListsWritten = false;
            _jitMhStartupLogs.Clear();
            _ccawStartupLogs.Clear();
            _bumpStartupLogs.Clear();
        }

        public void ConfigureLabelServices(
            WorkerLabelService workerLabelService,
            MineralLabelService mineralLabelService,
            VespeneLabelService vespeneLabelService,
            SpawningPoolPlacementService spawningPoolPlacementService)
        {
            _workerLabelService = workerLabelService;
            _activeBuild?.ConfigureWorkerLabelService(workerLabelService);
            _mineralLabelService = mineralLabelService;
            _vespeneLabelService = vespeneLabelService;
            _spawningPoolPlacementService = spawningPoolPlacementService;
            _labelsInitialized = false;
            _greedyChainWorkerCount = -1;
            _initialAssignmentLatched = false;
            _workerCountTransitionApplied = false;
            _lastObservedWorkerCount = -1;
            _ccawStartupSummaryLogged = false;
            _frameZeroInstructionListsWritten = false;
            _jitMhStartupLogs.Clear();
            _ccawStartupLogs.Clear();
            _bumpStartupLogs.Clear();
        }

        public BabySharkBot.Builds.BabySharkBuild? ActiveBuild => _activeBuild;

        public IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var frame = observation?.Observation == null
                ? -1
                : (int)observation.Observation.GameLoop;
            Console.WriteLine($"[BUILD MANAGER ONFRAME ENTER] frame={frame} activeBuild={_activeBuild?.BuildName ?? "<null>"} started={_started} skip={SkipFrame} neverSkip={NeverSkip}");
            if (Debugger.IsAttached)
            {
                //Debugger.Break();
            }

            if (_activeBuild == null)
            {
                Console.WriteLine($"[BUILD MANAGER ONFRAME EXIT] frame={frame} reason=activeBuild-null");
                return Array.Empty<SC2APIProtocol.Action>();
            }
            var chainRebuilt = BuildGreedyMineralChainFromObservation(frame);
            if (chainRebuilt)
            {
                // Wait points must exist before the pair table reads them (C7 ordering fix).
                CalculateCcaWaitPointsForCurrentSpawn();
                var startIndexForPairs = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                var pairTownhall = Globals.CurrentMapData?.StartingTownHall?.ElementAtOrDefault(startIndexForPairs);
                if (pairTownhall != null)
                {
                    PopulateJitPairReturnCalculations(Globals.CurrentMapData, startIndexForPairs, pairTownhall);
                }
            }
            _ongoingMapData.RefreshMiningData(
                null,
                observation,
                Globals.CurrentMapData,
                _workerLabelService,
                null,
                _mineralLabelService,
                _vespeneLabelService);
            UpdateRuntimeLabels();
            OnboardNewTempDrones(frame);
            SeedRuntimeWorkerInstructions(frame);
            UpdateMacroHatcheryPlacement(frame, observation);
            if (frame == 0 && !_frameZeroInstructionListsWritten)
            {
                WriteFrameZeroInstructionLists();
                _frameZeroInstructionListsWritten = true;
            }

            if (!_started)
            {
                _activeBuild.OnStart(frame);
                _started = true;
            }

            Console.WriteLine($"[BUILD MANAGER CALLING BUILD] frame={frame} build={_activeBuild.BuildName} started={_started}");
            if (Debugger.IsAttached)
            {
                //Debugger.Break();
            }

            var actions = _activeBuild.OnFrame(observation) ?? Array.Empty<SC2APIProtocol.Action>();

            Console.WriteLine($"[BUILD MANAGER BUILD RETURNED] frame={frame} build={_activeBuild.BuildName} actions={actions.Count()}");

            if (_activeBuild.ShouldTransition(frame))
            {
                Console.WriteLine($"BabySharkBuildManager: Build {_activeBuild.BuildName} requested transition at frame {frame}");
            }

            return actions;
        }

        public void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            if (_activeBuild == null || _started)
            {
                return;
            }

            var frame = observation?.Observation == null ? 0 : (int)observation.Observation.GameLoop;
            _macroHatcheryPlacementService ??= new MacroHatcheryPlacementService(_defaultBot);
            _macroHatcheryPlacementService.EnsureMapDataInitialized(gameInfo);
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            BuildGreedyMineralChainFromObservation(frame);
            CalculateCcaWaitPointsForCurrentSpawn();
            var pairTownhall = Globals.CurrentMapData?.StartingTownHall?.ElementAtOrDefault(startIndex);
            if (pairTownhall != null)
            {
                PopulateJitPairReturnCalculations(Globals.CurrentMapData, startIndex, pairTownhall);
            }
            _ongoingMapData.RefreshMiningData(
                null,
                observation,
                Globals.CurrentMapData,
                _workerLabelService,
                null,
                _mineralLabelService,
                _vespeneLabelService);
            UpdateRuntimeLabels();
            _activeBuild.OnStart(frame);
            SeedRuntimeWorkerInstructions(frame);
            if (frame == 0 && !_frameZeroInstructionListsWritten)
            {
                WriteFrameZeroInstructionLists();
                _frameZeroInstructionListsWritten = true;
            }
            LogStartupInstructionSummary(startIndex);
            _started = true;
        }

        // Macro Hatchery placement (owner spec 2026-10-16): corner wall at the
        // top of the ramp, center 2.5 from the wall, touching the ramp edge on
        // one side. Independent of the vespene chain: Sharky's WallDataService
        // generation needs BaseData fully populated, which is not guaranteed at
        // label-init time (run12: GetWallData threw ArgumentNullException on the
        // first frame and the vespene chain never re-ran the block). So this
        // retries on a cooldown until it latches, then draws the footprint
        // circle every frame (green = spec buildable, yellow = spiral-adjusted,
        // red = failed) so the spot stays visible in-game for the first-attempt
        // timing/validity check.
        private MacroHatcheryPlacement _macroHatcheryPlacement;

        private void UpdateMacroHatcheryPlacement(int frame, ResponseObservation observation)
        {
            var mapData = Globals.CurrentMapData;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var townhall = mapData?.StartingTownHall?.ElementAtOrDefault(startIndex);
            if (mapData == null || townhall == null || frame < 0)
            {
                return;
            }

            // Reference capture (owner alignment tool): if the owner places a
            // real hatchery away from the main base, log its exact center once
            // — that is the ground-truth intended Macro Hatchery spot, no
            // pixel-guessing from screenshots.
            if (!_macroHatcheryReferenceLogged && observation?.Observation?.RawData?.Units != null)
            {
                var reference = observation.Observation.RawData.Units.FirstOrDefault(unit => unit != null
                    && unit.Alliance == Alliance.Self
                    && unit.UnitType == (uint)UnitTypes.ZERG_HATCHERY
                    && unit.Pos != null
                    && Math.Abs(unit.Pos.X - townhall.X) + Math.Abs(unit.Pos.Y - townhall.Y) > 10f);
                if (reference != null)
                {
                    _macroHatcheryReferenceLogged = true;
                    Console.WriteLine($"[MACRO HATCHERY] REFERENCE HATCHERY tag={reference.Tag} center=({reference.Pos.X:F2},{reference.Pos.Y:F2}) start={startIndex} frame={frame}");
                }
            }

            var latched = mapData.MacroHatcheryPlacements != null
                && mapData.MacroHatcheryPlacements.Count > startIndex
                && mapData.MacroHatcheryPlacements[startIndex] != null;

            // Always (re)compute the in-memory placement object once per run —
            // even when a point is latched from serialized map data — so the
            // Wall geometry is available for diagnostics/drawing (run21: the
            // reconstructed-from-disk object had no Wall and the buildable-grid
            // dump silently skipped).
            if (_macroHatcheryPlacement == null)
            {
                if (!latched && frame < _macroHatcheryNextAttemptFrame)
                {
                    return;
                }

                if (!latched)
                {
                    _macroHatcheryNextAttemptFrame = frame + 30;
                }

                try
                {
                    _macroHatcheryPlacementService ??= new MacroHatcheryPlacementService(_defaultBot);
                    var enemyTownhall = mapData.StartingTownHall?
                        .Select((candidate, index) => (candidate, index))
                        .FirstOrDefault(entry => entry.candidate != null && entry.index != startIndex)
                        .candidate;
                    var macro = _macroHatcheryPlacementService.GetPlacement(Settings.CurrentMapName, townhall, enemyTownhall);
                    if (macro?.Final == null)
                    {
                        return;
                    }

                    while (mapData.MacroHatcheryPlacements.Count <= startIndex)
                    {
                        mapData.MacroHatcheryPlacements.Add(null);
                    }

                    _macroHatcheryPlacement = macro;
                    var final = macro.Final;
                    mapData.MacroHatcheryPlacements[startIndex] = new Vector2Dto(final.X, final.Y, townhall.Z);
                    var spec = macro.Spec;
                    var adjusted = macro.Adjusted;
                    Console.WriteLine($"[MACRO HATCHERY] spec=({spec.X:F2},{spec.Y:F2}) valid={macro.SpecValid}"
                        + (adjusted != null ? $" adjusted=({adjusted.X:F2},{adjusted.Y:F2})" : " adjusted=none")
                        + $" final=({final.X:F2},{final.Y:F2}) latchedFromDisk={latched} start={startIndex} map={Settings.CurrentMapName} frame={frame}");
                    Console.WriteLine($"[MACRO HATCHERY] candidates: {string.Join(" ", macro.Candidates.Take(8).Select(c => $"({c.X:F1},{c.Y:F1})"))}");
                    // Owner correction 2026-10-16: the offset target gives the
                    // DIRECTION; Final is snapped to the nearest LEGAL flat
                    // .5-grid placement. Log the arrow spot, the snapped
                    // point, its grid validity, and the legal options.
                    if (macro.OwnerTarget != null)
                    {
                        var target = macro.OwnerTarget;
                        var dx = final.X - target.X;
                        var dy = final.Y - target.Y;
                        Console.WriteLine($"[MACRO HATCHERY] ownerTarget=({target.X:F2},{target.Y:F2}) final=({final.X:F2},{final.Y:F2}) finalValid={macro.FinalValid} snapDist={MathF.Sqrt(dx * dx + dy * dy):F2}"
                            + $" legalNear=[{string.Join(" ", macro.LegalNearOwnerTarget.Select(p => $"({p.X:F1},{p.Y:F1})"))}]");
                    }
                    // Owner pass 2026-10-16: dump the buildable grid around the
                    // ramp every game (one-shot) until the Macro Hatchery
                    // location is settled — the wall segments themselves are
                    // "virtual" and sit on buildable cells, so 'H' centers
                    // can still overlap the future 3x3 wall buildings.
                    RunBuildableDiagnostics(mapData, startIndex, townhall, frame);
                }
                catch (Exception ex)
                {
                    var message = ex.Message ?? ex.ToString();
                    if (message != _macroHatcheryLastError)
                    {
                        _macroHatcheryLastError = message;
                        Console.WriteLine($"[MACRO HATCHERY] placement calc failed frame={frame}: {ex}");
                    }
                }
            }

            if (_macroHatcheryPlacement != null)
            {
                _macroHatcheryPlacementService?.DrawPlacement(_macroHatcheryPlacement);
            }

            // Owner directive 2026-10-16: draw BOTH owner-table locations
            // (North + South) every frame regardless of start, so he can
            // compare both circles at once on frame zero.
            _macroHatcheryPlacementService?.DrawAllOwnerPlacements();
        }

        // One-shot diagnostic: when the spec point is invalid and the spiral
        // found nothing, probe AreaBuildable at known reference points to tell
        // "the pocket is genuinely unbuildable" apart from "AreaBuildable is
        // broken" (run18: nothing legal within 3u of the spec point).
        private bool _macroHatcheryDiagnosticsLogged;

        private void RunBuildableDiagnostics(MawBaseLocationData mapData, int startIndex, Vector2Dto townhall, int frame)
        {
            if (_macroHatcheryDiagnosticsLogged || _defaultBot.BuildingService == null)
            {
                return;
            }
            _macroHatcheryDiagnosticsLogged = true;

            var probes = new List<string>();
            void Probe(string name, float x, float y)
            {
                var buildable = _defaultBot.BuildingService.AreaBuildable(x, y, MacroHatcheryPlacementService.HatcheryRadius);
                var cell = _defaultBot.MapData != null && x >= 0 && y >= 0 && x < _defaultBot.MapData.MapWidth && y < _defaultBot.MapData.MapHeight
                    ? _defaultBot.MapData.Map[(int)x, (int)y]
                    : null;
                probes.Add($"{name}=({x:F1},{y:F1}):{(buildable ? "Y" : "N")}/cell{(cell?.Buildable == true ? "Y" : "N")}");
            }

            Probe("north", townhall.X, townhall.Y + 8);
            Probe("south", townhall.X, townhall.Y - 8);
            Probe("east", townhall.X + 8, townhall.Y);
            Probe("west", townhall.X - 8, townhall.Y);
            var pool = mapData.SpawningPoolPlacements?.ElementAtOrDefault(startIndex);
            if (pool != null)
            {
                Probe("pool", pool.X, pool.Y);
            }

            Console.WriteLine($"[MACRO HATCHERY] diagnostics start={startIndex} frame={frame}: {string.Join(" ", probes)}");
            DumpBuildableAroundRamp(mapData, startIndex, townhall);
        }

        // ASCII dump of the buildable grid around the ramp entrance (one shot).
        // 'B' buildable cell, '.' not, 'S' spec point, 'w'/'W' VIRTUAL wall
        // segment cell ('w' = buildable underneath — the segments are computed
        // terran wall-off points, not real map structures, so the underlying
        // cell may be legal), 'R' ramp center, 'H' a 5x5-all-buildable hatchery
        // center found by brute-force scan. Ground truth for where a hatchery
        // actually fits near the ramp.
        private void DumpBuildableAroundRamp(MawBaseLocationData mapData, int startIndex, Vector2Dto townhall)
        {
            var map = _defaultBot.MapData?.Map;
            var buildingService = _defaultBot.BuildingService;
            if (map == null || buildingService == null || _macroHatcheryPlacement?.Wall?.RampCenter == null)
            {
                return;
            }

            var ramp = _macroHatcheryPlacement.Wall.RampCenter;
            var spec = _macroHatcheryPlacement.Spec;
            var walls = new Dictionary<(int, int), bool>();
            foreach (var segment in _macroHatcheryPlacement.Wall.WallSegments ?? new List<WallSegment>())
            {
                if (segment?.Position == null)
                {
                    continue;
                }
                var half = segment.Size / 2f;
                for (var x = (int)Math.Floor(segment.Position.X - half); x <= (int)Math.Ceiling(segment.Position.X + half); x++)
                {
                    for (var y = (int)Math.Floor(segment.Position.Y - half); y <= (int)Math.Ceiling(segment.Position.Y + half); y++)
                    {
                        walls[(x, y)] = x >= 0 && y >= 0 && x < _defaultBot.MapData.MapWidth && y < _defaultBot.MapData.MapHeight
                            && map[x, y].Buildable;
                    }
                }
            }

            // Brute-force legal hatchery centers on the .5 grid in the window.
            var hatcheryCenters = new HashSet<(int, int)>();
            var minX = (int)ramp.X - 12;
            var maxX = (int)ramp.X + 12;
            var minY = (int)ramp.Y - 12;
            var maxY = (int)ramp.Y + 12;
            for (var halfX = minX * 2 + 1; halfX <= maxX * 2 + 1; halfX += 2)
            {
                for (var halfY = minY * 2 + 1; halfY <= maxY * 2 + 1; halfY += 2)
                {
                    var cx = halfX / 2f;
                    var cy = halfY / 2f;
                    if (buildingService.AreaBuildable(cx, cy, MacroHatcheryPlacementService.HatcheryRadius))
                    {
                        hatcheryCenters.Add(((int)cx, (int)cy));
                    }
                }
            }

            var centerList = hatcheryCenters
                .OrderBy(c => (c.Item1 - ramp.X) * (c.Item1 - ramp.X) + (c.Item2 - ramp.Y) * (c.Item2 - ramp.Y))
                .Take(12)
                .ToList();
            Console.WriteLine($"[MACRO HATCHERY] legal hatchery centers near ramp (nearest 12): {string.Join(" ", centerList.Select(c => $"({c.Item1}.5,{c.Item2}.5)"))}");

            Console.WriteLine("[MACRO HATCHERY] buildable grid around ramp (x increases right, y increases down):");
            for (var y = minY; y <= maxY; y++)
            {
                var row = new System.Text.StringBuilder();
                for (var x = minX; x <= maxX; x++)
                {
                    if (x < 0 || y < 0 || x >= _defaultBot.MapData.MapWidth || y >= _defaultBot.MapData.MapHeight)
                    {
                        row.Append(' ');
                        continue;
                    }
                    if (spec != null && (int)spec.X == x && (int)spec.Y == y)
                    {
                        row.Append('S');
                    }
                    else if ((int)ramp.X == x && (int)ramp.Y == y)
                    {
                        row.Append('R');
                    }
                    else if (walls.TryGetValue((x, y), out var wallBuildable))
                    {
                        row.Append(wallBuildable ? 'w' : 'W');
                    }
                    else if ((int)townhall.X == x && (int)townhall.Y == y)
                    {
                        row.Append('T');
                    }
                    else if (hatcheryCenters.Contains((x, y)))
                    {
                        row.Append('H');
                    }
                    else
                    {
                        row.Append(map[x, y].Buildable ? 'B' : '.');
                    }
                }
                Console.WriteLine($"[MACRO HATCHERY] y={y,3} {row}");
            }
        }

        private void CalculateCcaWaitPointsForCurrentSpawn()
        {
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignments = Globals.CurrentMapData?.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            if (assignments == null || assignments.Count == 0)
            {
                return;
            }

            // Wait circle: 1.5u from the mineral center on the ray toward the hatchery.
            // Role 1 harvests at 1.0u (footprint); role 3 waits at 1.5u so it does not
            // interfere with role 1. Every mineral, A and B, carries a wait point.
            const float waitCircleRadius = 1.5f;

            foreach (var assignment in assignments)
            {
                if (assignment?.Minerals == null || assignment.Minerals.Count == 0)
                {
                    continue;
                }

                OrderedMineral aMineral = null;
                foreach (var mineral in assignment.Minerals)
                {
                    if (mineral == null || string.IsNullOrWhiteSpace(mineral.FinalLabel))
                    {
                        continue;
                    }

                    // A non-zero CcaWaitPoint in BaseDtos is authoritative; reuse it.
                    if (HasNonZeroPoint(mineral.CcaWaitPoint))
                    {
                        if (mineral.FinalLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase))
                        {
                            aMineral = mineral;
                        }
                        continue;
                    }

                    if (!HasNonZeroPoint(mineral.Position) || !HasNonZeroPoint(mineral.HarvestPoint))
                    {
                        continue;
                    }

                    var directionX = mineral.HarvestPoint.X - mineral.Position.X;
                    var directionY = mineral.HarvestPoint.Y - mineral.Position.Y;
                    var harvestDistance = MathF.Sqrt(directionX * directionX + directionY * directionY);
                    if (harvestDistance <= 0.0001f)
                    {
                        continue;
                    }

                    var scale = waitCircleRadius / harvestDistance;
                    mineral.CcaWaitPoint = new Vector2Dto(
                        mineral.Position.X + directionX * scale,
                        mineral.Position.Y + directionY * scale,
                        mineral.HarvestPoint.Z);

                    Console.WriteLine(
                        $"[BUILD START CCAW POINT CALCULATED] start={startIndex} team={assignment.TeamNumber} mineral={mineral.FinalLabel} " +
                        $"point=({mineral.CcaWaitPoint.X:F2},{mineral.CcaWaitPoint.Y:F2}) radius=1.5 stored=BaseDtos");

                    if (mineral.FinalLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase))
                    {
                        aMineral = mineral;
                    }
                }

                // JitWaitPoint remains the team's CCAw storage (Staging / BumpCcaWaitCircle read it).
                if (aMineral != null && HasNonZeroPoint(aMineral.CcaWaitPoint))
                {
                    assignment.JitWaitPoint = aMineral.CcaWaitPoint;
                }
            }
        }

        private void SeedRuntimeWorkerInstructions(int frame)
        {
            var snapshot = Globals.CurrentObservation;
            if (snapshot?.SelfUnits == null || Globals.CurrentMapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assignments = Globals.CurrentMapData.TeamPatchAssignments.ElementAtOrDefault(startIndex);
            if (assignments == null)
            {
                return;
            }

            foreach (var assignment in assignments)
            {
                foreach (var assignedWorker in assignment?.Workers ?? new List<WorkerEntryDto>())
                {
                    if (assignedWorker == null || assignedWorker.UnitTag == 0 || !snapshot.SelfUnits.ContainsKey(assignedWorker.UnitTag))
                    {
                        continue;
                    }

                    if (Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitTag, out var runtimeWorker) && runtimeWorker.Instructions.Count > 0)
                    {
                        continue;
                    }

                    var role = assignedWorker.FinalLabel ?? assignedWorker.Label ?? string.Empty;
                    var initialTarget = Globals.CurrentMapData.AssignedWorkers.ElementAtOrDefault(startIndex)?
                        .FirstOrDefault(worker => worker?.UnitID == assignedWorker.UnitTag)?
                        .MiningTargets?.FirstOrDefault();
                    if (initialTarget == null || initialTarget.ResourceUnitId == 0)
                    {
                        continue;
                    }

                    runtimeWorker ??= new RuntimeWorkerState { UnitTag = assignedWorker.UnitTag };

                    // 5x5 rotation, Version 2: role-specific startup sets on top of the
                    // rotation book. Bump roles are ONLY T4 (bumps T1 toward TA) and
                    // Y4 (bumps Y1 toward YA) — T3 no longer bumps, it is a first miner
                    // on SA. After the alignment gate the bumper parks on its B mineral
                    // wait point (TB/YB) and takes the frame-45 handoff gather as that
                    // mineral's 2nd miner (Build5x5BumpInstructions). There are no wait
                    // roles in Version 2: T5/Y5 are swing miners that walk straight to
                    // SB/BB (one trip, SA-SB/BA-BB return),
                    // S2/B2 speed-mine SB/BB
                    // directly, and the A-miners T1/Y1/Y3 walk straight to their A
                    // mineral (the double-A prologue is appended by Build5x5Book).
                    // T2/Y2 walk straight to their B mineral and mine it twice before
                    // the rotation (owner spec 2026-10-13 — the prologue is appended by
                    // Build5x5Book like the A-miners').
                    // The appended book (BuildMineralInstructionList 5x5 branch) runs
                    // the perpetual 3-mineral rotation.
                    if (Settings.IsMagannatha5x5Rotation && Is5x5RotationRole(role))
                    {
                        var is5x5BumpRole = Is5x5BumpRole(role);
                        // 5x5 A-miners (owner spec 2026-10-09): every worker whose first
                        // mining target is an A mineral (TA/SA/BA/YA — in the full
                        // rotation T1/B1/S1/Y1, in the 12-worker test T1/Y1/T3/Y3) opens
                        // with the same set shape: store a first-trip approach point,
                        // walk to it (frames 0/+1), then a position-gated Smart Harvest
                        // (queued gather) on the mineral. The stored point is the
                        // computed optimal first-harvest point for every A-miner EXCEPT
                        // T1/Y1, which walk straight to the (speed-mining) Harvest point
                        // on the hatchery->mineral line — owner request 2026-10-13: the
                        // optimal circle sample angled the T1/Y1 walk toward T5/Y5's
                        // side instead of the direct straight line.
                        var is5x5AMiner = !is5x5BumpRole
                            && IsAMineralLabel(initialTarget.ResourceLabel);
                        var instructionSet5x5 = is5x5BumpRole ? $"bump5x5{role}"
                            : is5x5AMiner ? "jitSM5x5A"
                            : "jitMH5x5";
                        var startup5x5 = is5x5BumpRole
                            ? Build5x5BumpInstructions(instructionSet5x5, role, initialTarget.ResourceUnitId, initialTarget.ResourceLabel, startIndex, assignments)
                            : is5x5AMiner
                                ? Build5x5AMinerStartupInstructions(instructionSet5x5, initialTarget.ResourceUnitId, role)
                                : NormalizeRole(role) == "T5"
                                    // Harvest-frame test case: the new T5 (W1) stages at
                                    // SB's CCA wait point and waits on the harvest-release
                                    // gate before its one-time SB trip — "going to a wait
                                    // position until S2 finishes SB". The gate passes
                                    // immediately when SB is free, so an uncontested start
                                    // still mines at once; the standard startup rows sit
                                    // behind the gate and their relative frames count from
                                    // the gate pass.
                                    ? Build5x5SwingStartupWithStaging(instructionSet5x5, initialTarget.ResourceUnitId)
                                    : BuildStandardStartupInstructions(instructionSet5x5, WorkerInstructionPoint.Harvest, initialTarget.ResourceUnitId, -1);

                        var list5x5 = BuildMineralInstructionList(startIndex, assignedWorker, instructionSet5x5, startup5x5);
                        runtimeWorker.LoadInstructions(instructionSet5x5, list5x5, Settings.GetRelativeFrame(frame));
                        Settings.RuntimeWorkers[assignedWorker.UnitTag] = runtimeWorker;
                        if (is5x5BumpRole)
                        {
                            _bumpStartupLogs.Add(assignedWorker.UnitTag);
                        }
                        else
                        {
                            _jitMhStartupLogs.Add(assignedWorker.UnitTag);
                        }

                        Console.WriteLine($"[BUILD START 5X5 LOADED] worker={assignedWorker.UnitTag} role={role} set={instructionSet5x5} target={initialTarget.ResourceUnitId}");
                        continue;
                    }

                    // Preserve existing startup behavior: non-Magannatha role 3 workers use CCAw.
                    // Magannatha experiment 1: T3/Y3 use dedicated bump sets (1183 mineral walk,
                    // alignment-gated stop, wait-point moves); T1 uses one-time jitMHbT1 and Y1
                    // uses one-time jitMHb; S3/B3 use
                    // the harvest-first CCAw shape (1183 first, wait-point moves at frames 14/15);
                    // all remaining workers stay jitMH. Bump workers hand off to existing
                    // jitMR/jitMH after the A-mineral gather.
                    var roleThree = role.EndsWith("3", StringComparison.OrdinalIgnoreCase);
                    var isMagannathaBumpRole = Settings.IsMagannatha12WorkerOverride
                        && (string.Equals(role, "T3", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(role, "Y3", StringComparison.OrdinalIgnoreCase));
                    var magannathaCcawRole = Settings.IsMagannatha12WorkerOverride
                        && (string.Equals(role, "S3", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(role, "B3", StringComparison.OrdinalIgnoreCase));
                    var useCcaw = roleThree && (!Settings.IsMagannatha12WorkerOverride || magannathaCcawRole);
                    var isMagannathaJitMhBT1Role = Settings.IsMagannatha12WorkerOverride
                        && string.Equals(role, "T1", StringComparison.OrdinalIgnoreCase);
                    var isMagannathaJitMhBRole = Settings.IsMagannatha12WorkerOverride
                        && string.Equals(role, "Y1", StringComparison.OrdinalIgnoreCase);
                    var instructionSet = isMagannathaBumpRole
                        ? $"bump{role}"
                        : isMagannathaJitMhBT1Role ? "jitMHbT1"
                        : isMagannathaJitMhBRole ? "jitMHb"
                        : useCcaw ? "CCAw" : "jitMH";
                    var movementPoint = useCcaw ? WorkerInstructionPoint.Staging : WorkerInstructionPoint.Harvest;
                    // gatherFrame >= 0 keeps the first gather frame-based; only Role 3's initial
                    // CCAw wait set uses that (frame 55). A negative gatherFrame makes the gather
                    // position-gated: it fires when the worker is within 0.1u of the mineral
                    // footprint point, however long the walk takes.
                    var gatherFrame = useCcaw ? 55 : -1;
                    // Magannatha experiment 1: the role-1 workers' first-trip gather keeps
                    // the stored-point position gate but also fires at relative frame 20 —
                    // without the fallback Y1 stalled on row 4 forever in the first run.
                    // Same stall risk for S1/B1, so they get the identical unqueued
                    // frame-20 harvest. (T1 no longer comes through here — it uses the
                    // jitMHbT1 set built by BuildJitMhBT1StartupInstructions.)
                    var isFrameTwentyRole = string.Equals(role, "Y1", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(role, "S1", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(role, "B1", StringComparison.OrdinalIgnoreCase);
                    var gatherFallbackFrame = Settings.IsMagannatha12WorkerOverride && isFrameTwentyRole
                        ? 20
                        : -1;
                    var startupInstructions = isMagannathaBumpRole
                        ? BuildBumpInstructions(instructionSet, role, assignment, assignments, initialTarget.ResourceUnitId)
                        : isMagannathaJitMhBT1Role
                            ? BuildJitMhBT1StartupInstructions(instructionSet, initialTarget.ResourceUnitId)
                        : magannathaCcawRole
                            ? BuildHarvestFirstWaitInstructions(instructionSet, initialTarget.ResourceUnitId)
                            : BuildStandardStartupInstructions(instructionSet, movementPoint, initialTarget.ResourceUnitId, gatherFrame, gatherFallbackFrame);
                    var instructionList = BuildMineralInstructionList(
                        startIndex,
                        assignedWorker,
                        instructionSet,
                        startupInstructions);
                    runtimeWorker.LoadInstructions(instructionSet, instructionList, Settings.GetRelativeFrame(frame));
                    Settings.RuntimeWorkers[assignedWorker.UnitTag] = runtimeWorker;
                    var loaded = Settings.RuntimeWorkers.TryGetValue(assignedWorker.UnitTag, out var loadedWorker)
                        && loadedWorker.Instructions.Count > 0
                        && string.Equals(loadedWorker.Instructions[0].InstructionSet, instructionSet, StringComparison.OrdinalIgnoreCase);
                    if (!loaded)
                    {
                        continue;
                    }

                    if (isMagannathaBumpRole)
                    {
                        _bumpStartupLogs.Add(assignedWorker.UnitTag);
                        Console.WriteLine($"[BUILD START INSTRUCTION LOADED] worker={assignedWorker.UnitTag} role={role} set={instructionSet} target={initialTarget.ResourceUnitId}");
                    }
                    else if (useCcaw)
                    {
                        _ccawStartupLogs.Add(assignedWorker.UnitTag);
                    }
                    else
                    {
                        _jitMhStartupLogs.Add(assignedWorker.UnitTag);
                        Console.WriteLine($"[BUILD START INSTRUCTION LOADED] worker={assignedWorker.UnitTag} role={role} set={instructionSet} target={initialTarget.ResourceUnitId}");
                    }
                }
            }
        }

        // Owner spec 2026-10-13: after B2 morphs into the spawning pool, the
        // larva-born replacement drones are onboarded as temp speed-miners —
        // 1st new drone = "13-B3" (BB), 2nd = "14-S3" (SB), 3rd = "15-B4"
        // (TB on Start 0 / YB on Start 1 since 2026-10-16 22:11) —
        // each receiving the S2/B2-style single-mineral loop. The frame-0 greedy
        // chain is never rebuilt (BuildGreedyMineralChainFromObservation's
        // rotation guard); instead the new drone's tag is added to the existing
        // team assignment and assigned-worker records and its book is loaded
        // here. Runs every frame; already-onboarded temps are skipped by the
        // known-tag check, and their entry positions are re-synced from the
        // live snapshot (the snapshot's WorkerEntryDto objects are recreated
        // every frame, so the team.Workers copy must be refreshed manually).
        private void OnboardNewTempDrones(int frame)
        {
            if (!Settings.IsMagannatha5x5Rotation)
            {
                return;
            }

            var snapshot = Globals.CurrentObservation;
            var mapData = Globals.CurrentMapData;
            if (snapshot?.SelfUnits == null || mapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var assigned = mapData.AssignedWorkers?.ElementAtOrDefault(startIndex);
            var teams = mapData.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            if (assigned == null || teams == null || teams.Count == 0)
            {
                return;
            }

            foreach (var entry in _onboardedTempEntries.Values)
            {
                if (snapshot.SelfUnits.TryGetValue(entry.UnitTag, out var live))
                {
                    entry.Position = live.Position;
                    entry.IsCarrying = live.IsCarrying;
                    entry.OrderAbilityIds = live.OrderAbilityIds;
                }
            }

            // 1st new drone -> 13-B3 (BB), 2nd -> 14-S3 (SB), 3rd -> 15-B4.
            // 15-B4 mines the Roach-Warren-side B mineral (owner spec 2026-10-16
            // 22:11): TEAL TB on Start 0, YELLOW YB on Start 1 — placing it next
            // to the side that builds the Roach Warren.
            var b4MineralLabel = startIndex == 0 ? "TB" : "YB";
            var tempSpecs = new[]
            {
                (Role: "B3", MineralLabel: "BB"),
                (Role: "S3", MineralLabel: "SB"),
                (Role: "B4", MineralLabel: b4MineralLabel),
            };
            var onboardedCount = assigned.Count(worker =>
                worker != null && IsOnboardedTempRole(worker.Role));

            var knownTags = new HashSet<ulong>(assigned.Where(worker => worker != null).Select(worker => worker.UnitID));
            var newDrones = snapshot.SelfUnits.Values
                .Where(unit => unit != null
                    && unit.UnitType == (uint)UnitTypes.ZERG_DRONE
                    && unit.UnitTag != 0
                    && !knownTags.Contains(unit.UnitTag))
                .OrderBy(unit => unit.UnitTag)
                .ToList();

            foreach (var drone in newDrones)
            {
                if (onboardedCount >= tempSpecs.Length)
                {
                    if (_extraDroneReported.Add(drone.UnitTag))
                    {
                        Console.WriteLine($"[TEMP DRONE SKIP] frame={frame} tag={drone.UnitTag} — beyond the three temp specs (13-B3, 14-S3, 15-B4); left unseeded");
                    }
                    continue;
                }

                var spec = tempSpecs[onboardedCount];
                var templateTarget = assigned
                    .SelectMany(worker => worker?.MiningTargets ?? new List<MiningTargetDto>())
                    .FirstOrDefault(candidate => string.Equals(candidate?.ResourceLabel, spec.MineralLabel, StringComparison.OrdinalIgnoreCase));
                if (templateTarget == null)
                {
                    Console.WriteLine($"[TEMP DRONE SKIP] frame={frame} tag={drone.UnitTag} role={spec.Role} — no {spec.MineralLabel} mining target template in assignments");
                    continue;
                }

                var team = teams.FirstOrDefault(candidate => candidate?.Minerals?.Any(mineral =>
                    string.Equals(mineral?.FinalLabel, spec.MineralLabel, StringComparison.OrdinalIgnoreCase)) == true);
                if (team == null)
                {
                    Console.WriteLine($"[TEMP DRONE SKIP] frame={frame} tag={drone.UnitTag} role={spec.Role} — no team assignment holds {spec.MineralLabel}");
                    continue;
                }

                OnboardTempDrone(startIndex, assigned, team, drone, 12 + onboardedCount + 1, spec.Role, templateTarget, frame);
                onboardedCount++;
            }
        }

        private static bool IsOnboardedTempRole(string role)
        {
            var normalized = NormalizeRole(role);
            return normalized == "B3" || normalized == "S3" || normalized == "B4";
        }

        private void OnboardTempDrone(
            int startIndex,
            List<AssignedWorkerDto> assigned,
            TeamPatchAssignmentDto team,
            WorkerEntryDto drone,
            int tempNumber,
            string role,
            MiningTargetDto templateTarget,
            int frame)
        {
            var tag = drone.UnitTag;
            var label = $"{tempNumber}-{role}";

            // Team assignment entry (a COPY — the snapshot's WorkerEntryDto objects
            // are recreated every frame): makes the tag resolvable for team-based
            // lookups and gives SeedRuntimeWorkerInstructions a labeled worker.
            var entry = new WorkerEntryDto
            {
                UnitTag = tag,
                UnitType = drone.UnitType,
                Position = drone.Position,
                Label = role,
                StartLabel = role,
                FinalLabel = role,
                FirstSeenFrame = frame,
                FirstSeenPosition = drone.Position,
                IsCompleted = true,
                OrderAbilityIds = drone.OrderAbilityIds?.ToList() ?? new List<int>()
            };
            team.Workers.Add(entry);
            _onboardedTempEntries[tag] = entry;

            // Assigned-worker record: the mining manager's per-worker loop,
            // observation sync (CurrentXY/Mti), and collision system all iterate
            // this list.
            var target = CloneMiningTarget(templateTarget);
            var assignedWorker = new AssignedWorkerDto
            {
                UnitID = tag,
                Role = role,
                TownHallUnitID = target.TownHallUnitId,
                CurrentXY = drone.Position,
                Mti = 0
            };
            assignedWorker.MiningTargets.Add(target);
            assigned.Add(assignedWorker);

            // Display label above the unit (e.g. "13-B3").
            _workerLabelService?.SetLabel(label, tag);

            // Book: the plain S2/B2 single-mineral speed-mining loop — the temp
            // branch of BuildMineralInstructionList / Build5x5Book (the pool
            // sequence is gated on the exact role "B2", so B3/S3/B4 skip it).
            // 13-B3 is the one exception (owner spec 2026-10-15 22:20): its book
            // carries the extractor-trick chapter after the first BB harvest +
            // return — see Build5x5Book's B3 gate. S3/B4 get the plain loop.
            var startup = BuildStandardStartupInstructions("jitMH5x5", WorkerInstructionPoint.Harvest, target.ResourceUnitId, -1);
            var book = BuildMineralInstructionList(startIndex, entry, "jitMH5x5", startup);

            var runtimeWorker = new RuntimeWorkerState { UnitTag = tag };
            runtimeWorker.LoadInstructions("jitMH5x5", book, Settings.GetRelativeFrame(frame));
            Settings.RuntimeWorkers[tag] = runtimeWorker;

            Console.WriteLine($"[TEMP DRONE ONBOARDED] frame={frame} tag={tag} label={label} mineral={target.ResourceLabel} bookRows={book.Count}");
        }


        private static MiningTargetDto CloneMiningTarget(MiningTargetDto source)
        {
            return new MiningTargetDto
            {
                ResourceLabel = source.ResourceLabel,
                FromResourceLabel = source.FromResourceLabel,
                ToResourceLabel = source.ToResourceLabel,
                ResourceUnitId = source.ResourceUnitId,
                TownHallUnitId = source.TownHallUnitId,
                ResourcePosition = source.ResourcePosition,
                FromHarvestPoint = source.FromHarvestPoint,
                ToHarvestPoint = source.ToHarvestPoint,
                HarvestPoint = source.HarvestPoint,
                SmHarvestPoint = source.SmHarvestPoint,
                ReturnPoint = source.ReturnPoint,
                SmReturnPoint = source.SmReturnPoint,
                IsSpeedMining = source.IsSpeedMining,
                IsABSwitch = source.IsABSwitch,
                IsInitialMineralAssignment = source.IsInitialMineralAssignment
            };
        }

        private static void WriteFrameZeroInstructionLists()
        {
            try
            {
                var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "mining_tests");
                Directory.CreateDirectory(folder);
                var filePath = Path.Combine(folder, $"worker_instruction_lists_frame0_{DateTime.Now:yyyyMMddHHmmss}.json");
                var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
                var workers = Globals.CurrentMapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                    ?? new List<AssignedWorkerDto>();
                var output = new
                {
                    RecordType = "WorkerInstructionListsFrameZero",
                    TimestampUtc = DateTime.UtcNow,
                    GameFrame = 0,
                    StartIndex = startIndex,
                    Workers = workers
                        .Where(worker => worker != null && worker.UnitID != 0 && Settings.RuntimeWorkers.TryGetValue(worker.UnitID, out _))
                        .Select(worker =>
                        {
                            var runtimeWorker = Settings.RuntimeWorkers[worker.UnitID];
                            return new
                            {
                                WorkerTag = worker.UnitID,
                                WorkerLabel = worker.Role,
                                CurrentInstructionIndex = runtimeWorker.CurInstrIdx,
                                InstructionStartFrame = runtimeWorker.InstructionStartFrame,
                                CurrentTargetIndex = worker.Mti,
                                Instructions = runtimeWorker.Instructions.Select((instruction, index) => new
                                {
                                    Index = index,
                                    Set = instruction?.InstructionSet ?? string.Empty,
                                    Command = instruction?.Command.ToString() ?? string.Empty,
                                    Point = instruction?.Point.ToString() ?? string.Empty,
                                    TargetId = instruction?.TargetId ?? 0,
                                    RelativeFrame = instruction?.RelativeFrame ?? -1,
                                    PreviousAbilityId = instruction?.PreviousAbilityId ?? -1,
                                    TargetAbilityId = instruction?.TargetAbilityId ?? -1,
                                    TargetAbilityId2 = instruction?.TargetAbilityId2 ?? -1,
                                     NextTargetIndex = instruction?.NextTargetIndex ?? -1,
                                     JumpToInstructionIndex = instruction?.JumpToInstructionIndex ?? -1,
                                     Queue = instruction?.Queue ?? false,
                                     NoCondition = instruction?.NoCondition ?? false,
                                     StoreTargetPoint = instruction?.StoreTargetPoint ?? false,
                                     TargetPointReference = instruction?.TargetPointReference ?? string.Empty,
                                     PositionTolerance = instruction?.PositionTolerance ?? 0f
                                }).ToList()
                            };
                        }).ToList()
                };
                var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                Console.WriteLine($"[BUILD FRAME ZERO INSTRUCTION LISTS] file={filePath} start={startIndex} workers={output.Workers.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BUILD FRAME ZERO INSTRUCTION LISTS] write failed reason={ex.Message}");
            }
        }

        private static IReadOnlyList<WorkerInstruction> BuildStandardStartupInstructions(
            string instructionSet,
            WorkerInstructionPoint movementPoint,
            ulong targetId,
            int gatherFrame,
            int gatherFallbackFrame = -1)
        {
            var startupMovementPoint = movementPoint == WorkerInstructionPoint.Harvest
                ? WorkerInstructionPoint.StoredTarget
                : movementPoint;
            // Role 3's initial CCAw set is the one exception to position-gated gathering: the
            // worker stands on the 1.5u wait circle, outside the 0.1u footprint gate, so that
            // gather keeps its frame condition. Every other gather fires when the worker is
            // within 0.1u of the mineral footprint point.
            var gather = new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Gather, Point = WorkerInstructionPoint.Harvest, TargetId = targetId, Queue = true };
            if (gatherFrame >= 0)
            {
                gather.RelativeFrame = gatherFrame;
            }
            else
            {
                // Version 2 experiment (owner request): queued at +16 behind the walk —
                // rides the order queue and begins harvesting the moment the walk
                // completes (Sharky-style zero-latency walk->mine) instead of waiting
                // for the 0.5u position poll to notice the arrival. The tolerance is
                // kept as a belt-and-braces gate: it can fire the row a frame or two
                // early once the worker is in range, and the lost-tread self-heal in
                // the mining manager still walks a stranded worker in.
                gather.RelativeFrame = 16;
                // 0.1u is unreachable: mineral pathing radius + drone radius (~0.375)
                // keep worker centers well outside the footprint circle. 0.5 covers it.
                gather.PositionTolerance = 0.5f;
                // Gate on the same stored pair point the startup moves walk to. Gating on the
                // generic Harvest point strands the worker: the pair harvest point and the
                // mineral-to-hatchery harvest point differ by more than 0.1u on most minerals.
                gather.Point = WorkerInstructionPoint.StoredTarget;
            }

            // Frame fallback on top of the position gate (Magannatha Y1): fires at the
            // frame even when the walk has not reached the stored point yet. Unqueued so
            // the harvest interrupts the still-running frame-14 move instead of waiting
            // behind it in the order queue — that queue wait was the visible gap between
            // the frame-20 command and Y1 actually starting on YA. Safe from the
            // same-batch move+gather discard because this row fires at frame 20, six
            // frames after the last move row.
            if (gatherFallbackFrame >= 0)
            {
                gather.RelativeFrame = gatherFallbackFrame;
                gather.Queue = false;
            }

            return new[]
            {
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = startupMovementPoint, TargetId = targetId, RelativeFrame = 0 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = startupMovementPoint, TargetId = targetId, RelativeFrame = 1 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = startupMovementPoint, TargetId = targetId, RelativeFrame = 14 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = startupMovementPoint, TargetId = targetId, RelativeFrame = 15 },
                gather
            };
        }

        // T1-only first-trip set (Magannatha). Same walk as jitMHb (moves at frames
        // 0/1/14/15 to the stored pair point), but the position-gated startup gather is
        // removed. The ONLY 3666 (HARVEST_GATHER) in this set fires at relative frame
        // +16, queued, and it is the only command at that frame — no return-point move.
        // Queued so it appends to the order queue behind the frame-15 move instead of
        // colliding with it in one batch; harvesting begins the moment the walk ends.
        private static IReadOnlyList<WorkerInstruction> BuildJitMhBT1StartupInstructions(string instructionSet, ulong targetId)
        {
            return new[]
            {
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = targetId, RelativeFrame = 0 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = targetId, RelativeFrame = 1 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = targetId, RelativeFrame = 14 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = targetId, RelativeFrame = 15 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Gather, Point = WorkerInstructionPoint.Harvest, TargetId = targetId, RelativeFrame = 16, Queue = true }
            };
        }

        // Experiment 1 (Magannatha) bump set for T3/Y3. Opens with the 1183 Smart Harvest
        // on the walk mineral (T3: ordered [3] = 3-SA, Y3: ordered [5] = 5-BB). The
        // inverted-gate jump re-issues that harvest every frame until both workers sit on
        // the hatchery-to-A-mineral line with role 3 closer to the hatchery
        // (BumpAlignedGate), then moves onto the team's role-3 wait
        // point, then a frame-gated gather on the A mineral hands off to the jit cycle
        // appended by BuildMineralInstructionList.
        private static IReadOnlyList<WorkerInstruction> BuildBumpInstructions(
            string instructionSet,
            string role,
            TeamPatchAssignmentDto teamAssignment,
            List<TeamPatchAssignmentDto> allAssignments,
            ulong targetId)
        {
            var walkMineralIndex = string.Equals(role, "T3", StringComparison.OrdinalIgnoreCase) ? 3 : 5;
            var walkMineral = (allAssignments ?? new List<TeamPatchAssignmentDto>())
                .SelectMany(assignment => assignment?.Minerals ?? new List<OrderedMineral>())
                .FirstOrDefault(mineral => mineral != null && mineral.Index == walkMineralIndex);
            var aMineral = teamAssignment?.Minerals?.FirstOrDefault(mineral =>
                mineral != null
                && !string.IsNullOrWhiteSpace(mineral.FinalLabel)
                && mineral.FinalLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase));
            var harvestTargetId = aMineral?.UnitTag != 0 ? aMineral.UnitTag : targetId;
            if (walkMineral?.UnitTag == 0 || harvestTargetId == 0)
            {
                // Missing walk/A mineral data: fall back to the plain jitMH walk rather
                // than seeding a bump list that can never resolve its gate.
                Console.WriteLine($"[BUILD START BUMP SET FALLBACK] role={role} set={instructionSet} walkIndex={walkMineralIndex} walkTag={walkMineral?.UnitTag ?? 0} aTag={harvestTargetId}");
                return BuildStandardStartupInstructions(instructionSet, WorkerInstructionPoint.Harvest, targetId, -1);
            }

            Console.WriteLine($"[BUILD START BUMP SET] role={role} set={instructionSet} walkMineral=[{walkMineral.Index}]{walkMineral.FinalLabel} tag={walkMineral.UnitTag} aMineral={aMineral?.FinalLabel} aTag={harvestTargetId}");

            return new List<WorkerInstruction>
            {
                // Frame 0 and every loop pass: 1183 Smart Harvest on the walk mineral.
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Gather,
                    Point = WorkerInstructionPoint.None,
                    TargetId = walkMineral.UnitTag,
                    NoCondition = true,
                    Queue = false
                },
                // While unaligned, jump back to the harvest; when the gate resolves,
                // pass through to the move rows below.
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Jump,
                    Point = WorkerInstructionPoint.BumpAlignedGate,
                    TargetId = harvestTargetId,
                    JumpToInstructionIndex = 0,
                    InvertGate = true
                },
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Move,
                    Point = WorkerInstructionPoint.BumpCcaWaitCircle,
                    TargetId = harvestTargetId,
                    RelativeFrame = 1,
                    Queue = false
                },
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Move,
                    Point = WorkerInstructionPoint.BumpCcaWaitCircle,
                    TargetId = harvestTargetId,
                    RelativeFrame = 2,
                    Queue = false
                },
                // Role 3 then takes the A mineral itself and the appended jit cycle
                // (wait [1183->1184] -> jitRM -> jitMH ...) takes over.
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Gather,
                    Point = WorkerInstructionPoint.Harvest,
                    TargetId = harvestTargetId,
                    RelativeFrame = 45,
                    Queue = true
                }
            };
        }

        // ----- Experiment 2 (5x5 rotation) role helpers and startup sets -----

        private static string NormalizeRole(string role)
        {
            return string.IsNullOrWhiteSpace(role) ? string.Empty : role.Trim().ToUpperInvariant();
        }

        // 5x5 roles are two-char labels T1-T5, Y1-Y5 plus the temps S2 and B2. Under the
        // 5x5 flag the label table (TeamLabelRegistrationHelper) only produces these.
        private static bool Is5x5RotationRole(string role)
        {
            var normalized = NormalizeRole(role);
            return normalized.Length == 2
                && (normalized[0] == 'T' || normalized[0] == 'Y' || normalized[0] == 'S' || normalized[0] == 'B')
                && normalized[1] >= '1' && normalized[1] <= '5';
        }

        // Bump roles, Version 2: ONLY T4 (W2) bumps T1 toward TA and Y4 (W11) bumps
        // Y1 toward YA. T3 is a first miner on SA in Version 2 — its Version 1 bump
        // of S2 is gone. T4/Y4 reuse the walk minerals of the experiment-1 T3/Y3
        // sets (ordered [3] / ordered [5]).
        private static bool Is5x5BumpRole(string role)
        {
            var normalized = NormalizeRole(role);
            return normalized == "T4" || normalized == "Y4";
        }

        // Swing roles, Version 2: T5 (W6) mines SB once (returning via the SA-SB pair
        // row) then rotates SA -> TB -> TA; Y5 (W7) mines BB once (BA-BB return) then
        // rotates BA -> YA -> YB. Version 1's wait roles (gated mineral walk, CCA wait
        // point, frame-50 gather) no longer exist. Y2 left the swing group with the
        // 2026-10-13 rework — it now mines YB twice like T2 mines TB twice (see
        // Is5x5DoubleFirstMineralRole) and runs the standard rotation entering at
        // BA: BA -> YA -> YB (owner spec 2026-10-15, BB detour retired).
        private static bool Is5x5SwingRole(string role)
        {
            var normalized = NormalizeRole(role);
            return normalized == "T5" || normalized == "Y5";
        }

        // First-mineral-twice roles (owner spec 2026-10-13): T2 and Y2 mine their first
        // mineral TWICE before joining the rotation — T2: TB, TB, then TA, SA, TB; Y2:
        // YB, YB, then BA, YA, YB — the same prologue shape the A-miners run: the FIRST
        // return rides the {B,B} self-pair ReturnPoint (the speed-mining return point)
        // and the SECOND harvest walks out on the {B,B} self-pair HarvestA (the SM
        // harvest point). Role-gated (unlike the label-gated A-miner predicate) because
        // the owner named the roles; no other B-miner (T4/Y4, temps) double-mines.
        private static bool Is5x5DoubleFirstMineralRole(string role)
        {
            var normalized = NormalizeRole(role);
            return normalized == "T2" || normalized == "Y2";
        }

        // A-miner predicate, owner spec 2026-10-09: EVERY worker whose first mining
        // target is an A mineral (TA/SA/BA/YA) — the full rotation's T1/B1/S1/Y1 and
        // the 12-worker subset's T1/Y1/T3/Y3 alike — mines its first mineral twice
        // before joining the rotation. Label-gated, not role-gated, so a rotation
        // reassignment (S1 vs T3 on SA) changes nothing here. The FIRST return goes
        // out on the speed-mining return point (the {X,X} self-pair row stores the SM
        // geometry); the SECOND harvest rides the SM harvest point; after the second
        // harvest the worker is on the normal 3-mineral cycle.
        private static bool Is5x5AMinerRole(string role, List<MiningTargetDto> targets)
        {
            return !string.IsNullOrWhiteSpace(role)
                && Is5x5RotationRole(role)
                && targets != null
                && targets.Count > 0
                && IsAMineralLabel(targets[0].ResourceLabel);
        }

        // Temp roles, Version 2: speed-mine their B mineral directly on the {SB,SB} /
        // {BB,BB} self-pair points forever — Version 1's first trip on the A mineral
        // (S2: SA, B2: BA) is removed. The 2026-10-13 pool rework adds the larva-born
        // temps 13-B3 (BB), 14-S3 (SB), and 15-B4 (BB): S3/B4 run this same plain
        // loop — 13-B3's book instead carries the extractor-trick chapter after the
        // first BB harvest + return (owner spec 2026-10-15 22:20, Build5x5Book's B3
        // gate). The pool sequence exists only in the exact-role "B2" branch of
        // Build5x5Book.
        private static bool Is5x5TempRole(string role)
        {
            var normalized = NormalizeRole(role);
            return normalized == "S2" || normalized == "B2"
                || normalized == "B3" || normalized == "S3" || normalized == "B4";
        }

        // Bump set for the 5x5 rotation, Version 2. Owner-spec bumper-gate shape
        // ("T1 - T4 Bump Set", extended 2026-10-13): frame-0 smart harvest on the walk
        // mineral, bumper-gated bump move onto the partner, a per-frame live-position
        // chase of the partner from relative frames 8-25 ("keep pushing until frame
        // 25"), wait-point moves at 26/27, and the frame-45 handoff gather:
        //   - T4 and Y4 both come through here; the partner mapping is evaluated in
        //     BabySharkMiningManager.ResolveBumpSmBumperPoint / ResolveObservedBumpPartner
        //     (T4->T1, Y4->Y1).
        //   - The bumper gate's A mineral is resolved BY LABEL across all assignments
        //     (T4: TA, Y4: YA) because Y4 (W11) sits in the BA/BB team whose own A
        //     mineral is BA — a teamAssignment lookup would gate Y4 on the wrong mineral.
        //   - After the chase the bumper parks on its B mineral's self-pair wait point
        //     ({TB,TB}/{YB,YB} WaitPointA) — "move to a wait point on a B mineral" —
        //     and the frame-45 gather hands off on that B mineral (MiningTargets[0],
        //     TB for T4 / YB for Y4) as its 2nd miner.
        private static IReadOnlyList<WorkerInstruction> Build5x5BumpInstructions(
            string instructionSet,
            string role,
            ulong targetId,
            string targetLabel,
            int startIndex,
            List<TeamPatchAssignmentDto> allAssignments)
        {
            var normalizedRole = NormalizeRole(role);
            var allMinerals = (allAssignments ?? new List<TeamPatchAssignmentDto>())
                .SelectMany(assignment => assignment?.Minerals ?? new List<OrderedMineral>())
                .ToList();

            // T4 mirrors the old T3 walk (ordered [3]); Y4 mirrors the old Y3 walk
            // (ordered [5]). The gate mineral is the partner's A mineral by label.
            OrderedMineral walkMineral;
            string aLabel;
            if (normalizedRole == "T4")
            {
                walkMineral = allMinerals.FirstOrDefault(mineral => mineral != null && mineral.Index == 3);
                aLabel = "TA";
            }
            else if (normalizedRole == "Y4")
            {
                walkMineral = allMinerals.FirstOrDefault(mineral => mineral != null && mineral.Index == 5);
                aLabel = "YA";
            }
            else
            {
                Console.WriteLine($"[BUILD START 5X5 BUMP FALLBACK] role={normalizedRole} set={instructionSet} reason=unknown-bump-role");
                return BuildStandardStartupInstructions(instructionSet, WorkerInstructionPoint.Harvest, targetId, -1);
            }

            var aMineral = allMinerals.FirstOrDefault(mineral =>
                mineral != null
                && string.Equals(mineral.FinalLabel, aLabel, StringComparison.OrdinalIgnoreCase));
            var harvestTargetId = aMineral?.UnitTag != 0 ? aMineral.UnitTag : targetId;
            if (walkMineral?.UnitTag == 0 || harvestTargetId == 0)
            {
                Console.WriteLine($"[BUILD START 5X5 BUMP FALLBACK] role={normalizedRole} set={instructionSet} walkTag={walkMineral?.UnitTag ?? 0} aLabel={aLabel} aTag={harvestTargetId}");
                return BuildStandardStartupInstructions(instructionSet, WorkerInstructionPoint.Harvest, targetId, -1);
            }

            Console.WriteLine($"[BUILD START 5X5 BUMP SET] role={normalizedRole} set={instructionSet} walkMineral=[{walkMineral.Index}]{walkMineral.FinalLabel} tag={walkMineral.UnitTag} aMineral={aLabel} aTag={harvestTargetId} handoff={targetLabel} tag={targetId}");

            // "T1 - T4 Bump Set" (owner spec, extended 2026-10-13): BOTH bump roles run
            // this set — T4 (bumps T1 toward TA) and Y4 (bumps Y1 toward YA). The bump is
            // gated on the bumper point — where the mineral-walk line intersects the
            // partner's A-mineral speed-mining line — and from relative frame 8 through
            // relative frame 25 ("keep pushing until frame 25", owner request) the
            // bumper chases the partner's live X,Y with one unqueued move per frame. All
            // frames are absolute from frame 0 (no InstructionStartFrame reset anywhere
            // in this set): the bumper gate fails open at frame 8 so the chase window
            // always runs.
            return new List<WorkerInstruction>
            {
                // Frame 0: 1183 Smart Harvest on the walk mineral — the mineral walk
                // that carries the bumper to the bumper point behind its partner.
                // Issued once; the walk runs until the bumper move below interrupts it.
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Gather,
                    Point = WorkerInstructionPoint.None,
                    TargetId = walkMineral.UnitTag,
                    NoCondition = true,
                    Queue = false
                },
                // The bumper move: BumpSmBumper resolves to the partner's live position
                // once the bumper reaches the bumper point (and fails open at relative
                // frame 8), so this row fires the frame the gate resolves and switches
                // the bumper off the mineral walk onto a bump move behind its partner,
                // accelerating it toward the A-mineral SM harvest point. The 100u
                // tolerance makes the row's own position gate a formality — the bumper
                // gate lives in the resolver; while the resolver is null the row waits
                // silently. The {TA,TA}/{YA,YA} reference carries the A-mineral SM
                // harvest point the bumper point is computed against.
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Move,
                    Point = WorkerInstructionPoint.BumpSmBumper,
                    TargetId = harvestTargetId,
                    NoCondition = false,
                    PositionTolerance = 100f,
                    Queue = false,
                    TargetPointReference = BuildPairReference(startIndex, aLabel, aLabel, "HarvestA")
                },
                // Then the partner chase (owner request 2026-10-13): from relative frame
                // 8 through relative frame 25 the bumper moves to its partner's current
                // X,Y — one unqueued move row per frame, each resolving the partner's
                // live position at the frame it fires, so the bumper is always moving to
                // where the partner actually is and shoves it forward toward the
                // A-mineral speed-mining harvest point. PositionTolerance 0 makes the
                // rows purely frame-gated (a position gate could fire them early the
                // moment the bumper touches its partner). Frames are absolute from
                // frame 0 — no gate reset anywhere in this set.
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 8, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 9, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 10, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 11, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 12, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 13, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 14, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 15, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 16, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 17, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 18, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 19, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 20, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 21, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 22, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 23, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 24, PositionTolerance = 0f, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.BumpPartner, TargetId = targetId, RelativeFrame = 25, PositionTolerance = 0f, Queue = false },
                // The chase window is over: switch to the B-mineral wait point (existing
                // Version 2 handoff geometry) at frames 26/27, and the frame-45 gather
                // hands off on that B mineral (TB for T4 / YB for Y4) as before.
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.UseTargetPoint,
                    Point = WorkerInstructionPoint.JitWaitPointA,
                    TargetId = targetId,
                    NoCondition = true,
                    StoreTargetPoint = true,
                    TargetPointReference = BuildPairReference(startIndex, targetLabel, targetLabel, "WaitPointA")
                },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = targetId, RelativeFrame = 26, Queue = false },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = targetId, RelativeFrame = 27, Queue = false },
                // The bumper then takes the B mineral itself (2nd miner on TB/YB) and
                // the appended rotation book (whose MiningTargets[0] is that same
                // mineral) takes over.
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Gather,
                    Point = WorkerInstructionPoint.Harvest,
                    TargetId = targetId,
                    RelativeFrame = 45,
                    Queue = true
                }
            };
        }

        // True when the label names an "A" mineral (TA/SA/BA/YA) — the 5x5 A-miner
        // predicate. Gated on the mining target label rather than the worker role so
        // the same code serves the full rotation (T1/B1/S1/Y1) and the 12-worker
        // subset (T1/Y1/T3/Y3 hold the SA/BA slots today) without role-name churn.
        private static bool IsAMineralLabel(string resourceLabel)
        {
            return !string.IsNullOrWhiteSpace(resourceLabel)
                && resourceLabel.EndsWith("A", StringComparison.OrdinalIgnoreCase);
        }

        // 5x5 A-miner first set (owner spec 2026-10-09): the worker mines its A
        // mineral TWICE before joining the 3-mineral rotation.
        //   row 0: store the first-trip approach point, then
        //   rows 1-2 (0/+1): walk to that point (anti-coalesce double move).
        //   row 3: Smart Harvest on the mineral — POSITION-gated (no frame floor:
        //          the fixed frame-19 gather measured slower, owner report 2026-10-09),
        //          QUEUED so it rides behind the walk and begins harvesting the frame
        //          the worker arrives at the stored point. The lost-tread self-heal
        //          in the mining manager re-fires it if a blocked destination idles
        //          the worker. No re-walk rows: an unqueued re-move would cancel an
        //          already-running early harvest.
        // The stored first-trip point is role-dependent (owner request 2026-10-13):
        //   - T1/Y1: the (speed-mining) Harvest point on the hatchery->mineral line —
        //     the direct straight-line walk. The computed optimal first-harvest point
        //     angled their walk toward T5/Y5's side, so it is not used for them.
        //   - every other A-miner: the OPTIMAL first-harvest approach point (resolved
        //     live: the point minimizing |worker start -> P| + |P -> SM return point|
        //     over the candidate standoff points around the mineral — the SM harvest
        //     point is NOT forced on the first trip).
        // The first return (SM return point) and the second harvest (SM harvest
        // point) are appended by Build5x5Book's A-miner prologue.
        private static IReadOnlyList<WorkerInstruction> Build5x5AMinerStartupInstructions(
            string instructionSet,
            ulong targetId,
            string role)
        {
            var normalizedRole = NormalizeRole(role);
            var firstTripPoint = normalizedRole == "T1" || normalizedRole == "Y1"
                ? WorkerInstructionPoint.Harvest
                : WorkerInstructionPoint.OptimalFirstHarvest;
            return new List<WorkerInstruction>
            {
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.StoreTargetPoint,
                    Point = firstTripPoint,
                    TargetId = targetId,
                    NoCondition = true,
                    StoreTargetPoint = true
                },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = targetId, RelativeFrame = 0 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = targetId, RelativeFrame = 1 },
                new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Gather, Point = WorkerInstructionPoint.StoredTarget, TargetId = targetId, RelativeFrame = -1, PositionTolerance = 0.5f, Queue = true }
            };
        }

        // Experiment 1 (Magannatha) S3/B3 first trip: open with the 1183 Smart Harvest on
        // the A mineral (S3: SA, B3: BA), then at relative frames 14 and 15 switch to
        // moves onto the role-3 wait point, then the frame-55 A-mineral gather the old
        // CCAw set used. Same CCAw set name — only the first-trip shape changes; the
        // appended jit cycle is untouched.
        private static IReadOnlyList<WorkerInstruction> BuildHarvestFirstWaitInstructions(string instructionSet, ulong targetId)
        {
            return new List<WorkerInstruction>
            {
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Gather,
                    Point = WorkerInstructionPoint.None,
                    TargetId = targetId,
                    NoCondition = true,
                    Queue = false
                },
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Move,
                    Point = WorkerInstructionPoint.Staging,
                    TargetId = targetId,
                    RelativeFrame = 14,
                    Queue = false
                },
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Move,
                    Point = WorkerInstructionPoint.Staging,
                    TargetId = targetId,
                    RelativeFrame = 15,
                    Queue = false
                },
                // The A-mineral gather itself, same frame-gated shape the old CCAw wait
                // set used (frame 55). The frame-0 harvest above is interrupted by the
                // frame 14/15 moves, so without this row the worker never mines, never
                // returns cargo, and the appended wait [1183->1184] row never passes —
                // the worker stands at the wait point for the rest of the game.
                new WorkerInstruction
                {
                    InstructionSet = instructionSet,
                    Command = WorkerInstructionCommand.Gather,
                    Point = WorkerInstructionPoint.Harvest,
                    TargetId = targetId,
                    RelativeFrame = 55,
                    Queue = true
                }
            };
        }

        // T5 (swing) startup with new-logic staging (owner spec 2026-10-11): the
        // harvest-release staging pair prepended to the standard startup rows. The
        // frame-0 staging move starts the walk to the mineral's CCA wait point; the
        // gate then blocks until SB is free or within the release window; the
        // standard rows behind it (stored-point moves at relative 0/1/14/15, queued
        // gather at +16) count from the gate pass, not from game start.
        private static IReadOnlyList<WorkerInstruction> Build5x5SwingStartupWithStaging(
            string instructionSet,
            ulong targetId)
        {
            var instructions = new List<WorkerInstruction>();
            AddHarvestReleaseStagingInstructions(instructions, targetId);
            instructions.AddRange(BuildStandardStartupInstructions(instructionSet, WorkerInstructionPoint.Harvest, targetId, -1));
            return instructions;
        }

        private static List<WorkerInstruction> BuildMineralInstructionList(
            int startIndex,
            WorkerEntryDto worker,
            string startupInstructionSet,
            IReadOnlyList<WorkerInstruction> startupInstructions)
        {
            var assignedWorker = Globals.CurrentMapData?.AssignedWorkers?
                .ElementAtOrDefault(startIndex)?
                .FirstOrDefault(candidate => candidate?.UnitID == worker.UnitTag);
            var targets = assignedWorker?.MiningTargets ?? new List<MiningTargetDto>();
            var workerRole = worker.FinalLabel ?? worker.Label ?? worker.StartLabel ?? string.Empty;
            if (targets.Count < 2)
            {
                // Version 2 temp roles (S2/B2) have a single-target book: the {SB,SB} /
                // {BB,BB} self-pair speed-mining loop. The setup row stores the self-pair
                // harvest point and Build5x5Book appends the loop.
                if (Settings.IsMagannatha5x5Rotation && Is5x5TempRole(workerRole) && targets.Count == 1)
                {
                    var tempList = new List<WorkerInstruction>();
                    AddSelfPairSetupInstruction(tempList, startIndex, startupInstructionSet, targets[0], WorkerInstructionCommand.StoreTargetPoint, WorkerInstructionPoint.JitHarvestA, "HarvestA");
                    tempList.AddRange(startupInstructions);
                    return Build5x5Book(tempList, startIndex, workerRole, targets);
                }
                return startupInstructions.ToList();
            }

            var firstTarget = targets[0];
            var secondTarget = targets[1];
            var instructions = new List<WorkerInstruction>();
            var firstLinePoint = ResolveFirstLinePoint(startupInstructionSet, workerRole);
            if (firstLinePoint != WorkerInstructionPoint.None)
            {
                AddJitPairSetupInstruction(instructions, startIndex, startupInstructionSet, firstTarget, secondTarget, firstLinePoint, WorkerInstructionCommand.StoreTargetPoint, true);
            }
            instructions.AddRange(startupInstructions);

            // 5x5 rotation (Version 2): the worker's MiningTargets list is its personal
            // chapter order (GetInstructionLabels rotated the book to the worker's start
            // chapter; T5/Y5 carry a four-entry swing list and S2/B2 a single-entry
            // speed-mining list). The book below runs harvest/return per chapter and the
            // last-line Jump loops the rotation forever.
            if (Settings.IsMagannatha5x5Rotation && Is5x5RotationRole(workerRole))
            {
                return Build5x5Book(instructions, startIndex, workerRole, targets);
            }

            var firstGatherAbility = (int)Abilities.HARVEST_GATHER_DRONE;
            var returnAbility = (int)Abilities.HARVEST_RETURN_DRONE;

            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = firstGatherAbility,
                TargetAbilityId = returnAbility
            });
            if (IsSingleMineralRole(workerRole))
            {
                // Role-1 workers mine their A mineral only (S1: SA, Y1: YA, T1: TA,
                // B1: BA, ...), and S2 mines SB only: a single-mineral loop on the
                // {X}-{X} self-pair line. Both teammates return simultaneously, so every
                // return uses the self-pair return point (S1: SA-SA, S2: SB-SB, Y1:
                // YA-YA, ...) and the worker walks back out on the self-pair harvest
                // point — never the shared pair points, so their paths never cross.
                // Mti stays on MiningTargets[0] forever.
                AddSelfPairReturnInstructions(instructions, startIndex, firstTarget);
                instructions.Add(CreateHandoffWait());

                // Harvest-release staging (owner spec 2026-10-12, all workers): stage at
                // the A mineral's CCA wait point and gate on the harvest-release window
                // before EVERY repeat approach; the jump target lands on the staging row
                // so each cycle re-stages.
                var selfHarvestIndex = instructions.Count;
                AddHarvestReleaseStagingInstructions(instructions, firstTarget.ResourceUnitId);
                AddSelfPairHarvestInstructions(instructions, startIndex, "jitMH", firstTarget);
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddSelfPairReturnInstructions(instructions, startIndex, firstTarget);
                instructions.Add(CreateHandoffWait());

                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "last-line",
                    Command = WorkerInstructionCommand.Jump,
                    JumpToInstructionIndex = selfHarvestIndex
                });
                return instructions;
            }
            AddReturnInstructions(instructions, startIndex, firstTarget, secondTarget);
            // Passes when the cargo hand-off is observed after the return command —
            // see AdvanceSatisfiedControlInstructions. Previous/TargetAbilityId stay at
            // the default -1 because (MOVE, RETURN_DRONE) never occurs once the return lands.
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                NextTargetIndex = 1
            });

            // Harvest-release staging (owner spec 2026-10-12, all workers): stage at the
            // B mineral's CCA wait point and gate on the harvest-release window before
            // walking in. The ABBA cycle's trailing jump lands here too, so those roles
            // re-stage on every repeat approach.
            var secondHarvestIndex = instructions.Count;
            AddHarvestReleaseStagingInstructions(instructions, secondTarget.ResourceUnitId);
            AddHarvestInstructions(instructions, startIndex, "jitMH", secondTarget, firstTarget, WorkerInstructionPoint.JitHarvestB);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = firstGatherAbility,
                TargetAbilityId = returnAbility
            });

            if (IsAbbaStaggerRole(workerRole))
            {
                AddAbbaStaggerCycle(instructions, startIndex, firstTarget, secondTarget);
            }
            else
            {
                AddReturnInstructions(instructions, startIndex, secondTarget, firstTarget);
                // Passes when the cargo hand-off is observed — see AdvanceSatisfiedControlInstructions.
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    NextTargetIndex = 0
                });

                var firstHarvestIndex = instructions.Count;
                AddHarvestReleaseStagingInstructions(instructions, firstTarget.ResourceUnitId);
                AddHarvestInstructions(instructions, startIndex, "jitMH", firstTarget, secondTarget, WorkerInstructionPoint.JitHarvestA);
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddReturnInstructions(instructions, startIndex, firstTarget, secondTarget);
                // Passes when the cargo hand-off is observed — see AdvanceSatisfiedControlInstructions.
                // The jump below loops back to the second harvest (MiningTargets[1]), so the
                // target pointer must advance to 1 here; leaving it at 0 made the worker's
                // CurrentTargetIndex disagree with the set it was about to run.
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    NextTargetIndex = 1
                });
            }

            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "last-line",
                Command = WorkerInstructionCommand.Jump,
                JumpToInstructionIndex = secondHarvestIndex
            });

            return instructions;
        }

        // 5x5 rotation book, Version 2, appended after the startup set (which already
        // gathered targets[0]). Layout per role class:
        //
        // Temp (S2/B2, single target): the startup gathered SB/BB; the book is the
        //   {SB,SB}/{BB,BB} self-pair speed-mining loop forever:
        //   wait(1183->1184)
        //   ret {X,X} self row                    (speed-mining geometry)
        //   wait handoff
        //   [harvest X self row]  <- selfHarvestIndex, the last-line Jump target
        //   wait(1183->1184)
        //   ret {X,X} self row
        //   wait handoff
        //   Jump -> selfHarvestIndex
        //
        // A-miner (every worker whose first target is an A mineral — T1/B1/S1/Y1 in
        // the full rotation, T1/Y1/T3/Y3 in the 12-worker subset): the startup set
        // (Build5x5AMinerStartupInstructions) walked to the computed optimal first-
        // harvest point and Smart-Harvested the A mineral at relative frame 19; the
        // prologue below mines the A mineral a SECOND time before the rotation:
        //   wait(1183->1184)
        //   ret {A,A} self row                    (the FIRST return goes out on the
        //                                          speed-mining return point — self
        //                                          rows store the SM geometry)
        //   wait handoff
        //   [harvest A again on the {A,A} self-row HarvestA — the SM harvest point]
        //   ...then the standard 3-chapter book below; after the second harvest the
        //   worker is on the normal 3-mineral cycle. EXCEPTIONS (owner spec
        //   2026-10-13, T1/T3 return-path crossing fix): T3 mines SA a THIRD time
        //   (one more self-pair cycle), and T1 inserts a TB chapter and a TA chapter
        //   (TA, TA, TB, TA) before the standard book — see the detour blocks after
        //   the prologue.
        //
        // Swing (T5/Y5 four targets): the startup gathered
        //   targets[0] (SB/BB); one return via the {SA,SB}/{BA,BB} pair
        //   row, then the rotation runs on the remaining entries — T5/Y5 rotate
        //   targets[1..3], wrapping to targets[1]. (Y2 left the swing group with the
        //   2026-10-13 rework; it now double-mines YB via the first-mineral-twice
        //   prologue and runs the plain rotation {YB, BA, YA}.)
        //   wait(1183->1184)
        //   ret(targets[0] -> targets[1])         (the SA-SB / BA-BB "return harvest")
        //   wait handoff; wait mti -> 1
        //   [harvest targets[1]]  <- chapter1Harvest, the last-line Jump target
        //   wait(1183->1184)
        //   ret(targets[1] -> targets[2])
        //   wait handoff; wait mti -> 2
        //   [harvest targets[2]]
        //   wait(1183->1184)
        //   ret(targets[2] -> targets[3])
        //   wait handoff; wait mti -> 3
        //   [harvest targets[3]]
        //   wait(1183->1184)
        //   ret(targets[3] -> targets[1])         (wrap: TA pairs back to SA)
        //   wait handoff; wait mti -> 1
        //   Jump -> chapter1Harvest
        //
        // Everyone else (T2/T3/Y2 and the A-miners after their prologue): the
        // standard 3-chapter rotation:
        //   [startup harvest targets[0]]            (caller)
        //   wait(1183->1184)
        //   ret(targets[0] -> targets[1])
        //   wait handoff; wait mti -> 1
        //   [harvest targets[1]]  <- chapter1Harvest, the last-line Jump target
        //   wait(1183->1184)
        //   ret(targets[1] -> targets[2])
        //   wait handoff; wait mti -> 2
        //   [harvest targets[2]]
        //   wait(1183->1184)
        //   ret(targets[2] -> targets[0])
        //   wait handoff; wait mti -> 0
        //   [harvest targets[0]]
        //   wait(1183->1184)
        //   ret(targets[0] -> targets[1])
        //   wait handoff; wait mti -> 1
        //   Jump -> chapter1Harvest (mti already 1, matching the set about to run)
        private static List<WorkerInstruction> Build5x5Book(
            List<WorkerInstruction> instructions,
            int startIndex,
            string workerRole,
            List<MiningTargetDto> targets)
        {
            var firstGatherAbility = (int)Abilities.HARVEST_GATHER_DRONE;
            var returnAbility = (int)Abilities.HARVEST_RETURN_DRONE;

            if (Is5x5TempRole(workerRole))
            {
                var onlyTarget = targets[0];
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddSelfPairReturnInstructions(instructions, startIndex, onlyTarget);
                instructions.Add(CreateHandoffWait());

                // 13-B3 cover chapter (owner spec 2026-10-17 11:27): the extractor
                // trick moved OFF B3 — the larva-born drone arrived too late for
                // the 125-bank window — onto an opening worker (Y5 on Start 0, T1
                // on Start 1). B3's book is now: the ONE standard BB harvest +
                // return above, then ONE cover assignment on the trick side's B
                // mineral (YB on Start 0 / TB on Start 1 — "replacing the drone
                // doing the extractor trick for one assignment"), and on that
                // trip's pickup (1183 -> 1184) ONE mineral walk to the mineral on
                // the opposite side of the hatchery to get around it, returning
                // cargo 25 frames in — once past the other workers' return
                // points. Then the plain BB loop below. When the cover template
                // or the opposite-side mineral is unavailable the chapter is
                // skipped (logged) and B3 just loops BB.
                var isB3CoverRole = string.Equals(NormalizeRole(workerRole), "B3", StringComparison.OrdinalIgnoreCase);
                if (isB3CoverRole)
                {
                    AddB3CoverChapter(instructions, startIndex);
                }

                // Harvest-frame test case (owner spec 2026-10-11): the new S2 (W6)
                // stages at SB's CCA wait point and waits on the harvest-release gate
                // before EVERY repeat approach — "testing the new logic the second
                // time while W1 is mining". The staging rows sit inside the jump loop
                // so every cycle re-stages; the self-pair harvest behind the gate
                // still mines the {SB,SB} speed-mining point.
                var loopStagingIndex = instructions.Count;
                AddHarvestReleaseStagingInstructions(instructions, onlyTarget.ResourceUnitId);
                var selfHarvestIndex = instructions.Count;
                AddSelfPairHarvestInstructions(instructions, startIndex, "jitMH", onlyTarget);
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });

                if (!string.Equals(NormalizeRole(workerRole), "B2", StringComparison.OrdinalIgnoreCase))
                {
                    // S2/B3/S3/B4: the infinite self-pair speed-mining loop. B3 jumps
                    // back to the STAGING rows (loopStagingIndex), not the harvest —
                    // it shares BB with B4 after the cover chapter, so every cycle
                    // must re-stage at BB's CCA wait point and re-clear the
                    // harvest-release gate before re-approaching (same fix the
                    // retired "b3ret" book used; jumping past staging walked B3
                    // onto a busy BB and got it re-pathed to random minerals).
                    AddSelfPairReturnInstructions(instructions, startIndex, onlyTarget);
                    instructions.Add(CreateHandoffWait());
                    instructions.Add(new WorkerInstruction
                    {
                        InstructionSet = "last-line",
                        Command = WorkerInstructionCommand.Jump,
                        JumpToInstructionIndex = isB3CoverRole ? loopStagingIndex : selfHarvestIndex
                    });
                    return instructions;
                }

                // Owner spec 2026-10-13 ("Back to B2", explicit row order): B2 mines
                // BB, BB (the startup trip plus the loop harvest above), Moves to the
                // {BB,Pool} preposition point, Returns its cargo, Moves to the pool
                // site, waits for the bank to hold the pool's 200 minerals, and
                // issues BUILD_SPAWNINGPOOL there. THE BUILD ROW IS TERMINAL: a Zerg
                // drone ordered to build morphs INTO the building — B2's unit id
                // becomes the spawning pool and stops being a worker — so the book
                // ends here. Rows behind the build could fire in the same frame and
                // their unqueued moves would cancel the morph before it commits (the
                // "B2 refuses to build" symptom). The first return above keeps the
                // 165-minerals redirect, so an early first return prepositions too.
                AddPoolPairReturnInstructions(instructions, startIndex, onlyTarget);
                instructions.Add(CreateHandoffWait());
                // Move to Pool: walk from the hatchery deposit point to the pool
                // site (same 0/1/14/15 re-issue pattern as the other move sets),
                // so the mineral wait and the build fire with B2 at the placement.
                instructions.Add(new WorkerInstruction { InstructionSet = "pool", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.PoolPlacement, TargetId = onlyTarget.ResourceUnitId, RelativeFrame = 0 });
                instructions.Add(new WorkerInstruction { InstructionSet = "pool", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.PoolPlacement, TargetId = onlyTarget.ResourceUnitId, RelativeFrame = 1 });
                instructions.Add(new WorkerInstruction { InstructionSet = "pool", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.PoolPlacement, TargetId = onlyTarget.ResourceUnitId, RelativeFrame = 14 });
                instructions.Add(new WorkerInstruction { InstructionSet = "pool", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.PoolPlacement, TargetId = onlyTarget.ResourceUnitId, RelativeFrame = 15 });
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "pool",
                    Command = WorkerInstructionCommand.Wait,
                    RequireMinerals = BabySharkMiningManager.SpawningPoolBuildMinerals
                });
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "pool",
                    Command = WorkerInstructionCommand.BuildSpawningPool,
                    TargetId = onlyTarget.ResourceUnitId,
                    NoCondition = true
                });
                return instructions;
            }

            // First-mineral-twice prologue (owner specs 2026-10-09 and 2026-10-13): mine
            // the first mineral a SECOND time before the rotation. Runs for the A-miners
            // (T1/B1/S1/Y1, T3 — their first mineral is an A mineral) AND for T2/Y2
            // (TB/YB double-miners per owner spec 2026-10-13). The FIRST return rides
            // the {X,X} self-pair row — the speed-mining return point; the SECOND
            // harvest walks out on the {X,X} self-pair HarvestA — the speed-mining
            // harvest point (A-miner first trips used the computed optimal point
            // instead, see Build5x5AMinerStartupInstructions).
            if ((Is5x5AMinerRole(workerRole, targets) || Is5x5DoubleFirstMineralRole(workerRole)) && targets.Count >= 2)
            {
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddSelfPairReturnInstructions(instructions, startIndex, targets[0]);
                instructions.Add(CreateHandoffWait());
                // Extractor-trick owner splice (owner spec 2026-10-17 11:27): the
                // trick moved OFF the larva-born 13-B3 onto an opening worker —
                // T1 on Start 1 (VA sits next to TA) — right after its FIRST TA
                // harvest + return, so it reaches VA near frame ~120 instead of
                // B3's ~700 and only WAITS for the 125 bank. The trick chapter
                // ends in the TERMINAL BuildExtractor row; the mining manager's
                // driver cancels the extractor once the larva morph begins and
                // RESUMES THIS BOOK at the row after the build — the staging row
                // of the prologue's second TA harvest below — so T1 "mines TA
                // after the Extractor Trick" and then runs its normal TB/TA
                // rotation.
                if (string.Equals(NormalizeRole(workerRole), GetExtractorTrickRole(startIndex), StringComparison.OrdinalIgnoreCase)
                    && TryGetB3TrickGeometry(startIndex, out var trickWalkMineral, out var trickVaGeyser))
                {
                    AddB3ExtractorTrickChapter(instructions, trickWalkMineral, trickVaGeyser);
                }
                // Harvest-release staging (owner spec 2026-10-12, all workers): stage at
                // the mineral's CCA wait point and gate on the harvest-release window
                // before the prologue's second harvest.
                AddHarvestReleaseStagingInstructions(instructions, targets[0].ResourceUnitId);
                AddSelfPairHarvestInstructions(instructions, startIndex, "jitMH", targets[0]);
            }

            // Owner spec 2026-10-15: Y2 mines YB twice (the prologue above), then
            // joins the standard rotation — its first chapter is BA (targets[1]),
            // giving the repeating unit BA -> YA -> YB. No BB detour.
            if (NormalizeRole(workerRole) == "T3" && targets.Count >= 2)
            //     so the standard book below resumes with its usual {TA,SA} return into
            //     the SA chapter.
            if (NormalizeRole(workerRole) == "T3" && targets.Count >= 2)
            {
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddSelfPairReturnInstructions(instructions, startIndex, targets[0]);
                instructions.Add(CreateHandoffWait());
                AddHarvestReleaseStagingInstructions(instructions, targets[0].ResourceUnitId);
                AddSelfPairHarvestInstructions(instructions, startIndex, "jitMH", targets[0]);
            }

            if (NormalizeRole(workerRole) == "T1" && targets.Count >= 3)
            {
                // TB chapter: return on the {TA,TB} pair row, harvest TB on the
                // {TB,TA} pair HarvestA.
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddReturnInstructions(instructions, startIndex, targets[0], targets[2]);
                instructions.Add(CreateHandoffWait());
                instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 2 });
                AddHarvestReleaseStagingInstructions(instructions, targets[2].ResourceUnitId);
                AddHarvestInstructions(instructions, startIndex, "jitMH", targets[2], targets[0], WorkerInstructionPoint.JitHarvestA);
                // TA chapter: return on the {TB,TA} pair row, harvest TA on the
                // {TA,SA} pair HarvestA (the rotation's normal TA chapter geometry).
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddReturnInstructions(instructions, startIndex, targets[2], targets[0]);
                instructions.Add(CreateHandoffWait());
                instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 0 });
                AddHarvestReleaseStagingInstructions(instructions, targets[0].ResourceUnitId);
                AddHarvestInstructions(instructions, startIndex, "jitMH", targets[0], targets[1], WorkerInstructionPoint.JitHarvestA);
            }

            if (Is5x5SwingRole(workerRole) && targets.Count >= 4)
            {
                // Swing prologue: the startup harvested targets[0] (SB/BB); return
                // via the {targets[1],targets[0]} pair row (SA-SB / BA-BB),
                // hand off, then run the repeating unit:
                //   T5/Y5 (4 targets): rotate targets[1..3], wrapping to targets[1].
                //   The old 5-target Y2 branch ("too many workers on BA" rework) is
                //   retired — Y2 left the swing group in the 2026-10-13 rework and no
                //   swing role carries five targets anymore; the targets.Count == 5
                //   block below is unreachable and kept only as history.
                // Owner spec 2026-10-14 ("add Y5 back with the new code"): Y5 rejoins
                // the swing rotation and enters at its LAST target (YB) instead of BA
                // — with the old BA entry Y2/Y4/Y5 all piled onto one mineral every
                // cycle. With Y4's cycle de-synced (one-time BA chapter) and Y2's
                // double-YB prologue (2026-10-15: its BB detour is retired, it joins
                // the rotation at BA), the yellow entries are Y1=YB, Y2=BA, Y3=YA,
                // Y4=YA(+desync), Y5=YB — a 2/2/1 spread. T5 keeps the original
                // targets[1] (SA) entry.
                var swingStart = NormalizeRole(workerRole) == "Y5" && targets.Count == 4
                    ? targets.Count - 1
                    : 1;
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddReturnInstructions(instructions, startIndex, targets[0], targets[swingStart]);
                instructions.Add(CreateHandoffWait());
                // Extractor-trick owner splice (owner spec 2026-10-17 11:27): the
                // trick moved OFF the larva-born 13-B3 onto an opening worker —
                // Y5 on Start 0 (VA sits next to YA) — right after the startup
                // harvest + return. The trick chapter ends in the TERMINAL
                // BuildExtractor row; the mining manager's driver cancels the
                // extractor once the larva morph begins and RESUMES THIS BOOK at
                // the row after the build. The one-time YA chapter below sits at
                // exactly that spot, so Y5 "mines YA after the Extractor Trick",
                // then hands off into the normal YB -> BA -> YA swing rotation
                // (the mti -> swingStart row that already follows).
                if (string.Equals(NormalizeRole(workerRole), GetExtractorTrickRole(startIndex), StringComparison.OrdinalIgnoreCase)
                    && targets.Count >= 4
                    && TryGetB3TrickGeometry(startIndex, out var trickWalkMineral, out var trickVaGeyser))
                {
                    AddB3ExtractorTrickChapter(instructions, trickWalkMineral, trickVaGeyser);
                    var yaTarget = targets[2];
                    var ybTarget = targets[3];
                    AddHarvestReleaseStagingInstructions(instructions, yaTarget.ResourceUnitId);
                    AddHarvestInstructions(instructions, startIndex, "jitMH", yaTarget, ybTarget, WorkerInstructionPoint.JitHarvestA);
                    instructions.Add(new WorkerInstruction
                    {
                        InstructionSet = "wait",
                        Command = WorkerInstructionCommand.Wait,
                        PreviousAbilityId = firstGatherAbility,
                        TargetAbilityId = returnAbility
                    });
                    AddReturnInstructions(instructions, startIndex, yaTarget, ybTarget);
                    instructions.Add(CreateHandoffWait());
                }
                instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = targets.Count == 5 ? 1 : swingStart });

                var loopStart = 1;
                if (targets.Count == 5)
                {
                    // Y2 one-time YA harvest, then the repeating YB -> BA -> YA unit.
                    // Harvest-release staging (owner spec 2026-10-12, all workers).
                    AddHarvestReleaseStagingInstructions(instructions, targets[1].ResourceUnitId);
                    AddHarvestInstructions(instructions, startIndex, "jitMH", targets[1], targets[2], WorkerInstructionPoint.JitHarvestA);
                    instructions.Add(new WorkerInstruction
                    {
                        InstructionSet = "wait",
                        Command = WorkerInstructionCommand.Wait,
                        PreviousAbilityId = firstGatherAbility,
                        TargetAbilityId = returnAbility
                    });
                    AddReturnInstructions(instructions, startIndex, targets[1], targets[2]);
                    instructions.Add(CreateHandoffWait());
                    instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 2 });
                    loopStart = 2;
                }

                var swingChapter1Harvest = instructions.Count;
                // Harvest-release staging (owner spec 2026-10-12): EVERY swing role
                // (T5/Y5) stages at each rotation mineral's CCA wait point and waits
                // on the harvest-release gate before walking in. The last-line Jump
                // lands on swingChapter1Harvest, i.e. the first chapter's staging row,
                // so every loop pass re-stages.
                const bool useChapterStaging = true;
                // Chapter order: start at swingStart (Y5: YB; T5: SA) and cycle
                // through the remaining swing targets in index order, wrapping.
                var rotationStart = targets.Count == 5 ? loopStart : swingStart;
                var chapterOrder = new List<int>();
                for (var i = rotationStart; i < targets.Count; i++)
                {
                    chapterOrder.Add(i);
                }
                for (var i = loopStart; i < rotationStart; i++)
                {
                    chapterOrder.Add(i);
                }
                for (var pos = 0; pos < chapterOrder.Count; pos++)
                {
                    var i = chapterOrder[pos];
                    var nextIndex = chapterOrder[(pos + 1) % chapterOrder.Count];
                    if (useChapterStaging)
                    {
                        AddHarvestReleaseStagingInstructions(instructions, targets[i].ResourceUnitId);
                    }
                    AddHarvestInstructions(instructions, startIndex, "jitMH", targets[i], targets[nextIndex], WorkerInstructionPoint.JitHarvestA);
                    instructions.Add(new WorkerInstruction
                    {
                        InstructionSet = "wait",
                        Command = WorkerInstructionCommand.Wait,
                        PreviousAbilityId = firstGatherAbility,
                        TargetAbilityId = returnAbility
                    });
                    AddReturnInstructions(instructions, startIndex, targets[i], targets[nextIndex]);
                    instructions.Add(CreateHandoffWait());
                    instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = nextIndex });
                }

                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "last-line",
                    Command = WorkerInstructionCommand.Jump,
                    JumpToInstructionIndex = swingChapter1Harvest
                });
                return instructions;
            }

            // Owner spec 2026-10-13 ("starting on different minerals"): Y2, Y4 and
            // Y5 all entered the rotation at BA and marched in lockstep — the yellow
            // bunching (teal spreads 2/2/1 because its roles enter at SA/TA/TB/SA).
            // Y4 enters at YA: its three chapters run t2 -> t0 -> t1
            // (YA -> YB -> BA — the canonical direction), looping forever, after a
            // ONE-TIME BA chapter that de-synchronizes its cycle from the others
            // (see below). Yellow rotation entries are Y1=YB, Y2=BA, Y3=YA,
            // Y4=YA(desynced), Y5=YB (swing enters at YB) — a 2/2/1 spread.
            if (NormalizeRole(workerRole) == "Y4" && targets.Count >= 3)
            {
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                // Prologue just mined YB (t0): return on the {YB,BA} pair row, hand
                // off, then run a ONE-TIME BA chapter (t1) BEFORE joining the loop.
                // The extra trip de-synchronizes Y4's cycle from Y1/Y2/Y3 — with
                // all four yellow rotation workers sharing ~equal 3-chapter cycle
                // lengths, equal-length cycles preserved the entry offsets forever
                // and the same three workers re-formed their pile at YB every
                // cycle ("we keep getting 3 workers trying to mine YB").
                AddReturnInstructions(instructions, startIndex, targets[0], targets[1]);
                instructions.Add(CreateHandoffWait());
                instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 1 });
                AddHarvestReleaseStagingInstructions(instructions, targets[1].ResourceUnitId);
                AddHarvestInstructions(instructions, startIndex, "jitMH", targets[1], targets[2], WorkerInstructionPoint.JitHarvestA);
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddReturnInstructions(instructions, startIndex, targets[1], targets[2]);
                instructions.Add(CreateHandoffWait());
                instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 2 });

                var y4Chapter1Harvest = instructions.Count;
                // Chapter YA (t2), next YB (t0).
                AddHarvestReleaseStagingInstructions(instructions, targets[2].ResourceUnitId);
                AddHarvestInstructions(instructions, startIndex, "jitMH", targets[2], targets[0], WorkerInstructionPoint.JitHarvestA);
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddReturnInstructions(instructions, startIndex, targets[2], targets[0]);
                instructions.Add(CreateHandoffWait());
                instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 0 });

                // Chapter YB (t0), next BA (t1).
                AddHarvestReleaseStagingInstructions(instructions, targets[0].ResourceUnitId);
                AddHarvestInstructions(instructions, startIndex, "jitMH", targets[0], targets[1], WorkerInstructionPoint.JitHarvestA);
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddReturnInstructions(instructions, startIndex, targets[0], targets[1]);
                instructions.Add(CreateHandoffWait());
                instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 1 });

                // Chapter BA (t1), next YA (t2).
                AddHarvestReleaseStagingInstructions(instructions, targets[1].ResourceUnitId);
                AddHarvestInstructions(instructions, startIndex, "jitMH", targets[1], targets[2], WorkerInstructionPoint.JitHarvestA);
                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "wait",
                    Command = WorkerInstructionCommand.Wait,
                    PreviousAbilityId = firstGatherAbility,
                    TargetAbilityId = returnAbility
                });
                AddReturnInstructions(instructions, startIndex, targets[1], targets[2]);
                instructions.Add(CreateHandoffWait());
                instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 2 });

                instructions.Add(new WorkerInstruction
                {
                    InstructionSet = "last-line",
                    Command = WorkerInstructionCommand.Jump,
                    JumpToInstructionIndex = y4Chapter1Harvest
                });
                return instructions;
            }

            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = firstGatherAbility,
                TargetAbilityId = returnAbility
            });
            AddReturnInstructions(instructions, startIndex, targets[0], targets[1]);
            instructions.Add(CreateHandoffWait());
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 1 });

            // Harvest-release staging (owner spec 2026-10-12, all workers): every chapter
            // approach stages at the chapter mineral's CCA wait point and gates on the
            // harvest-release window; the last-line Jump lands on chapter1Harvest, the
            // first chapter's staging row, so every loop pass re-stages.
            var chapter1Harvest = instructions.Count;
            AddHarvestReleaseStagingInstructions(instructions, targets[1].ResourceUnitId);
            AddHarvestInstructions(instructions, startIndex, "jitMH", targets[1], targets[2], WorkerInstructionPoint.JitHarvestA);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = firstGatherAbility,
                TargetAbilityId = returnAbility
            });
            AddReturnInstructions(instructions, startIndex, targets[1], targets[2]);
            instructions.Add(CreateHandoffWait());
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 2 });

            AddHarvestReleaseStagingInstructions(instructions, targets[2].ResourceUnitId);
            AddHarvestInstructions(instructions, startIndex, "jitMH", targets[2], targets[0], WorkerInstructionPoint.JitHarvestA);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = firstGatherAbility,
                TargetAbilityId = returnAbility
            });
            AddReturnInstructions(instructions, startIndex, targets[2], targets[0]);
            instructions.Add(CreateHandoffWait());
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 0 });

            AddHarvestReleaseStagingInstructions(instructions, targets[0].ResourceUnitId);
            AddHarvestInstructions(instructions, startIndex, "jitMH", targets[0], targets[1], WorkerInstructionPoint.JitHarvestA);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = firstGatherAbility,
                TargetAbilityId = returnAbility
            });
            AddReturnInstructions(instructions, startIndex, targets[0], targets[1]);
            instructions.Add(CreateHandoffWait());
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 1 });

            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "last-line",
                Command = WorkerInstructionCommand.Jump,
                JumpToInstructionIndex = chapter1Harvest
            });
            return instructions;
        }

        // ABBA stagger cycle for Y1/Y2/T1/T2. Loop = ABBAB for role 1, BAABA for role 2:
        // worker 1 and worker 2 of a team never mine the same mineral at the same time
        // and never converge on the shared pair return point (one returning from A and
        // the other from B today both walk to the SAME {YA,YB}.ReturnPoint and cross
        // paths). The first trip to the second mineral returns via the {X}-{X} self-pair
        // point — straight back to the hatchery on the mineral line — the repeat trip
        // walks back out on the same self-pair harvest point, and every later return is
        // the normal pair (green) geometry.
        //
        // Appended after the startup trip, the startup return, and the wait(gather->
        // return) row of the second harvest (which the caller adds and which is the jump
        // target). With X = MiningTargets[1] and Y = MiningTargets[0], the layout is:
        //   [harvest X]                (caller; = secondHarvestIndex, jump target)
        //   retX  DIRECT {X}-{X}       <- set 2 return (red line)
        //   wait handoff; wait mti stays X
        //   [harvest X2 self-pair]     <- set 3
        //   retX normal (green)
        //   wait handoff; wait mti -> Y
        //   [harvest Y]                <- set 4
        //   retY normal
        //   wait handoff; wait mti -> X
        //   [harvest X']               <- set 5
        //   retX normal
        //   wait handoff; wait mti -> Y
        //   [harvest Y']               <- set 6
        //   retY normal
        //   wait handoff; wait mti -> X  (jump lands on harvest X, Mti already correct)
        private static void AddAbbaStaggerCycle(
            List<WorkerInstruction> instructions,
            int startIndex,
            MiningTargetDto firstTarget,
            MiningTargetDto secondTarget)
        {
            var firstGatherAbility = (int)Abilities.HARVEST_GATHER_DRONE;
            var returnAbility = (int)Abilities.HARVEST_RETURN_DRONE;

            // Set 2 return: direct self-pair point, straight back to the hatchery.
            AddSelfPairReturnInstructions(instructions, startIndex, secondTarget);
            instructions.Add(CreateHandoffWait());
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 1 });

            // Set 3: repeat harvest of X, walking back out on the self-pair harvest point.
            // Harvest-release staging (owner spec 2026-10-12, all workers): gate every
            // repeat approach behind the mineral's CCA wait point + release window.
            AddHarvestReleaseStagingInstructions(instructions, secondTarget.ResourceUnitId);
            AddSelfPairHarvestInstructions(instructions, startIndex, "jitMH", secondTarget);
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, PreviousAbilityId = firstGatherAbility, TargetAbilityId = returnAbility });
            AddReturnInstructions(instructions, startIndex, secondTarget, firstTarget);
            instructions.Add(CreateHandoffWait());
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 0 });

            // Set 4: first mineral again, normal geometry.
            AddHarvestReleaseStagingInstructions(instructions, firstTarget.ResourceUnitId);
            AddHarvestInstructions(instructions, startIndex, "jitMH", firstTarget, secondTarget, WorkerInstructionPoint.JitHarvestA);
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, PreviousAbilityId = firstGatherAbility, TargetAbilityId = returnAbility });
            AddReturnInstructions(instructions, startIndex, firstTarget, secondTarget);
            instructions.Add(CreateHandoffWait());
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 1 });

            // Set 5: second mineral, normal pair geometry.
            AddHarvestReleaseStagingInstructions(instructions, secondTarget.ResourceUnitId);
            AddHarvestInstructions(instructions, startIndex, "jitMH", secondTarget, firstTarget, WorkerInstructionPoint.JitHarvestB);
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, PreviousAbilityId = firstGatherAbility, TargetAbilityId = returnAbility });
            AddReturnInstructions(instructions, startIndex, secondTarget, firstTarget);
            instructions.Add(CreateHandoffWait());
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 0 });

            // Set 6: first mineral; the trailing wait points Mti back at X because the
            // jump lands on the second harvest (MiningTargets[1]).
            AddHarvestReleaseStagingInstructions(instructions, firstTarget.ResourceUnitId);
            AddHarvestInstructions(instructions, startIndex, "jitMH", firstTarget, secondTarget, WorkerInstructionPoint.JitHarvestA);
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, PreviousAbilityId = firstGatherAbility, TargetAbilityId = returnAbility });
            AddReturnInstructions(instructions, startIndex, firstTarget, secondTarget);
            instructions.Add(CreateHandoffWait());
            instructions.Add(new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait, NextTargetIndex = 1 });

            Console.WriteLine($"[BUILD ABBA] start={startIndex} X={secondTarget.ResourceLabel} (direct {secondTarget.ResourceLabel}-{secondTarget.ResourceLabel} return) Y={firstTarget.ResourceLabel} loop=X,X,Y,X,Y");
        }

        // Passes when the cargo hand-off is observed after the return command — see
        // AdvanceSatisfiedControlInstructions. Previous/TargetAbilityId stay at the
        // default -1 because (MOVE, RETURN_DRONE) never occurs once the return lands.
        private static WorkerInstruction CreateHandoffWait()
        {
            return new WorkerInstruction { InstructionSet = "wait", Command = WorkerInstructionCommand.Wait };
        }

        // 13-B3 extractor trick: the name of the return-to-BB loop the mining
        // manager loads on the worker once the cancelled extractor restores the
        // drone. Built here at onboarding (build manager owns book construction);
        // the mining manager only executes/loads it.
        private const string B3ReturnInstructionSet = "b3ret";

        // Extractor-trick owner (owner spec 2026-10-17 11:27): the trick moved
        // OFF 13-B3 — the larva-born drone arrived too late for the 125-bank
        // window ("B3 is not arriving soon enough"). An opening worker does it
        // instead: Y5 on Start 0 (yellow side — VA sits next to YA), T1 on
        // Start 1 (teal — VA next to TA). After the trick the owner mines that
        // side's A mineral. B3 (first temp drone) instead covers ONE assignment
        // on the trick side's B mineral (YB / TB) with a far-side walk return.
        private static string GetExtractorTrickRole(int startIndex) => startIndex == 0 ? "Y5" : "T1";
        private static string GetTrickSideBLabel(int startIndex) => startIndex == 0 ? "YB" : "TB";
        private static string GetTrickOppositeALabel(int startIndex) => startIndex == 0 ? "TA" : "YA";

        // Geometry for the 13-B3 trick chapter: the main-base VA geyser and the
        // mineral nearest it (the walk mineral — YA or TA depending on start, by
        // distance). False when either is unavailable, in which case the caller
        // falls back to the plain temp loop.
        private static bool TryGetB3TrickGeometry(int startIndex, out OrderedMineral walkMineral, out OrderedVespene vaGeyser)
        {
            walkMineral = null;
            vaGeyser = null;

            var mapData = Globals.CurrentMapData;
            var va = mapData?.OrderedMainVespene?.ElementAtOrDefault(startIndex)?
                .FirstOrDefault(candidate => candidate != null
                    && string.Equals(candidate.Label, "VA", StringComparison.OrdinalIgnoreCase)
                    && candidate.UnitTag != 0
                    && HasNonZeroPoint(candidate.Position));
            if (va == null)
            {
                return false;
            }

            var nearest = mapData?.OrderedMainMinerals?.ElementAtOrDefault(startIndex)?
                .Where(candidate => candidate != null
                    && candidate.UnitTag != 0
                    && HasNonZeroPoint(candidate.Position)
                    && HasNonZeroPoint(candidate.HarvestPoint))
                .OrderBy(candidate => DistanceSquared(candidate.Position, va.Position))
                .FirstOrDefault();
            if (nearest == null)
            {
                return false;
            }

            walkMineral = nearest;
            vaGeyser = va;
            return true;
        }

        // 13-B3 extractor trick chapter (owner spec 2026-10-15, settled spec 22:50):
        // five sets, running right after B3's ONE standard BB harvest + return.
        //   set 1 (jitMH5x5, caller's startup): Mine BB.
        //   set 2 (wait/jitRM/wait, caller): the return set.
        //   set 3 (xwalk): a mineral walk whose last row is the move to the end at
        //            VA — xwalk 0 is ONE unqueued Smart Harvest on the mineral
        //            nearest VA (no re-issue rows), xwalk 1 is the ONE redirect move:
        //            the VaWalkRedirect point resolves to the VA geyser position
        //            only once the walker is within 0.2u of the walk mineral's
        //            harvest standoff (just before arrival); until then the row
        //            waits silently.
        //   set 4 (va): Build Extractor at 125 — the mineral-gated wait (75 for the
        //            extractor + 50 for the larva morph the mining manager issues in
        //            the same frame as the build, then cancels the frame the bank
        //            drops below 50 — the morph's spend), then BUILD_EXTRACTOR —
        //            TERMINAL row; the drone morphs into the extractor and the
        //            mining manager's trick driver owns the rest.
        //   set 5 (b3ret): after the cancel restores the drone, the driver loads
        //            the BB loop — "then loop BB".
        private static void AddB3ExtractorTrickChapter(
            List<WorkerInstruction> instructions,
            OrderedMineral walkMineral,
            OrderedVespene vaGeyser)
        {
            // Set 3 — ONE mineral walk whose last row is the move that ENDS at VA.
            // xwalk 0: ONE mineral walk — a single unqueued Smart Harvest on the mineral
            //            nearest VA. No re-issue rows: the walk runs until the redirect
            //            below interrupts it.
            // xwalk 1: the move to the end at VA — the VaWalkRedirect point resolves
            //            to the VA geyser position; the row's RelativeFrame is the
            //            ONLY gate (owner spec 2026-10-16 00:35, simplified: "Mineral
            //            walk for 20 frames then move to VA"). The move fires 20
            //            frames after the walk starts — long before the worker can
            //            reach the mineral (~74 frames), so the walk mineral is never
            //            mined. PositionTolerance 0 so the row cannot pass on
            //            position before its frame gate opens.
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "xwalk",
                Command = WorkerInstructionCommand.Gather,
                Point = WorkerInstructionPoint.None,
                TargetId = walkMineral.UnitTag,
                NoCondition = true,
                Queue = false
            });
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "xwalk",
                Command = WorkerInstructionCommand.Move,
                Point = WorkerInstructionPoint.VaWalkRedirect,
                TargetId = walkMineral.UnitTag,
                RelativeFrame = 20,
                PositionTolerance = 0f,
                Queue = false
            });
            // Set 4 — Build Extractor at 125: the mineral-gated wait (75 for the
            // extractor + 50 for the larva morph the mining manager issues in the
            // same frame as the build, then cancels the frame the bank drops below
            // 50 — the morph's spend), then BUILD_EXTRACTOR — TERMINAL row; the
            // drone morphs into the extractor and the mining manager's trick driver
            // owns the rest. No approach rows: set 3's move already ended at VA.
            // The wait ALSO carries the supply-full gate (owner spec 2026-10-17
            // 14:08: "The extractor trick is done at 14 supply to get to 15") —
            // the row passes only when FoodUsed has reached FoodCap, because the
            // trick only nets the extra worker at a full supply: the extractor
            // build frees the supply point the larva morph needs, and the cancel
            // lands at 15/14. Fired early (supply free) it would be an ordinary
            // drone morph with extra steps, so the trick owner waits at VA until
            // the 14th drone exists AND the bank holds 125.
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "va",
                Command = WorkerInstructionCommand.Wait,
                RequireMinerals = BabySharkMiningManager.ExtractorTrickBuildMinerals,
                RequireSupplyFull = true
            });
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "va",
                Command = WorkerInstructionCommand.BuildExtractor,
                TargetId = vaGeyser.UnitTag,
                NoCondition = true
            });
        }

        // 13-B3 cover chapter (owner spec 2026-10-17 11:27): B3 no longer does
        // the extractor trick — it covers ONE assignment on the trick side's B
        // mineral while the trick owner (Y5/T1) is away at VA:
        //   coverwalk 0 (stage/wait/jitMH/wait): stage at the cover mineral's
        //            CCA wait point, release gate, self-pair harvest rows, then
        //            the ability gate (1183 -> 1184) that passes the pickup
        //            frame, so the mineral walk below is issued CARRYING.
        //   coverwalk 1 (coverwalk Gather): ONE mineral walk — a single
        //            unqueued Smart Harvest on the OPPOSITE side's A mineral
        //            (TA on Start 0 / YA on Start 1) — to get around the
        //            hatchery ("issue ONE Mineral Walk to the opposite side to
        //            get around the Hatchery"). No re-issue rows.
        //   coverwalk 2 (coverwalk Return): return cargo at RelativeFrame 25 —
        //            25 frames in the walker is past the other workers' return
        //            points ("return Cargo when it is past any other return
        //            points"). The frame count starts at the pickup gate above
        //            (same pattern as the extractor-trick xwalk set and the
        //            retired b3mh "mhwalk" set). Return resolves the town hall
        //            through the cover mining target.
        //   handoff wait: passes when the deposit lands; the caller's BB loop
        //            staging then takes over.
        // When the cover mining-target template or the opposite-side mineral is
        // unavailable the chapter is SKIPPED (logged) and B3 falls straight
        // into the plain BB loop.
        private static void AddB3CoverChapter(List<WorkerInstruction> instructions, int startIndex)
        {
            var coverLabel = GetTrickSideBLabel(startIndex);
            var oppositeALabel = GetTrickOppositeALabel(startIndex);

            var mapData = Globals.CurrentMapData;
            var assigned = mapData?.AssignedWorkers?.ElementAtOrDefault(startIndex);
            var coverTarget = assigned?
                .SelectMany(worker => worker?.MiningTargets ?? new List<MiningTargetDto>())
                .FirstOrDefault(candidate => candidate != null
                    && string.Equals(candidate.ResourceLabel, coverLabel, StringComparison.OrdinalIgnoreCase));
            var oppositeMineral = mapData?.OrderedMainMinerals?.ElementAtOrDefault(startIndex)?
                .FirstOrDefault(candidate => candidate != null
                    && candidate.UnitTag != 0
                    && string.Equals(candidate.Label, oppositeALabel, StringComparison.OrdinalIgnoreCase));

            if (coverTarget == null || oppositeMineral == null)
            {
                Console.WriteLine($"[B3 COVER SKIP] start={startIndex} cover={coverLabel} template={(coverTarget == null ? "missing" : "ok")} opposite={oppositeALabel} mineral={(oppositeMineral == null ? "missing" : "ok")} — B3 falls into the plain BB loop");
                return;
            }

            AddHarvestReleaseStagingInstructions(instructions, coverTarget.ResourceUnitId);
            AddSelfPairHarvestInstructions(instructions, startIndex, "jitMH", coverTarget);
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = (int)Abilities.HARVEST_GATHER_DRONE,
                TargetAbilityId = (int)Abilities.HARVEST_RETURN_DRONE
            });

            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "coverwalk",
                Command = WorkerInstructionCommand.Gather,
                TargetId = oppositeMineral.UnitTag,
                NoCondition = true,
                Queue = false
            });
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "coverwalk",
                Command = WorkerInstructionCommand.Return,
                TargetId = coverTarget.ResourceUnitId,
                RelativeFrame = 25,
                Queue = false
            });

            instructions.Add(CreateHandoffWait());
        }

        // B3 Macro Hatchery test (owner spec 2026-10-16 18:35): the book the
        // trick driver loads once the cancelled extractor restores the drone
        // (replaces the plain "b3ret" BB loop while the test runs).
        private const string B3MacroHatcheryInstructionSet = "b3mh";
        // Owner spec 2026-10-16 18:35: "wait for 275 minerals". AGENT NOTE:
        // Sharky's building data prices the hatchery morph at 300 — if the
        // build never engages at a 275 bank, this constant is the first
        // suspect (one-line change; the freeze banner will say FAILURE).
        private const int MacroHatcheryBuildMinerals = 275;

        // The mineral at the SIDE OF THE HATCHERY for B3's post-trick 25-frame
        // mineral walk (owner correction 2026-10-16 19:42: "Mineral Walk to
        // side of the Hatchery for 25 frames") — the start's OWN mineral
        // nearest the townhall (TA on Magannatha), so the walk from BB passes
        // the hatchery and the return-cargo lands at the hatchery side. Falls
        // back to the plain BB loop when unavailable.
        private static bool TryGetB3MacroHatcherySideMineral(int startIndex, out OrderedMineral sideMineral)
        {
            sideMineral = null;
            var townhall = Globals.CurrentMapData?.StartingTownHall?.ElementAtOrDefault(startIndex);
            var minerals = Globals.CurrentMapData?.OrderedMainMinerals?.ElementAtOrDefault(startIndex);
            if (townhall == null || minerals == null)
            {
                return false;
            }

            sideMineral = minerals
                .Where(mineral => mineral != null
                    && mineral.UnitTag != 0
                    && HasNonZeroPoint(mineral.Position))
                .OrderBy(mineral => DistanceSquared(mineral.Position, townhall))
                .FirstOrDefault();
            return sideMineral != null;
        }

        // B3 Macro Hatchery test book (owner spec 2026-10-16 18:35, walk target
        // corrected 19:42 — "the set after the harvest needs to Mineral Walk to
        // side of the Hatchery for 25 frames. At 25 Frames issue a return
        // cargo, when Cargo has returned issue a move to the Macro Hatch
        // Location"):
        //   set 1 (stage/jitMH/wait): the ONE standard BB harvest — stage at BB's
        //            CCA wait point, release gate, self-pair harvest rows, then the
        //            ability gate (1183 -> 1184) that passes the pickup frame, so
        //            the mineral walk below is issued CARRYING.
        //   set 2 (mhwalk): ONE mineral walk — a single unqueued Smart Harvest on
        //            the start's own mineral nearest the townhall (the
        //            hatchery-side mineral, TA on Magannatha) — then a Return row
        //            at RelativeFrame 25 ("at 25 frames have it return cargo").
        //            The RelativeFrame counts from the pickup gate, i.e. from the
        //            walk start (same pattern as the extractor-trick xwalk set).
        //            Return resolves the town hall through the BB mining target.
        //   set 3 (wait/mhsite): a plain handoff wait (passes when the deposit
        //            lands — "when Cargo has returned"), THEN the move to the
        //            Macro Hatchery site — the move must not overwrite a return
        //            that is still walking.
        //   set 4 (mhsite): the mineral-gated wait (275) and the TERMINAL
        //            BuildHatchery row — the drone morphs into the hatchery and
        //            the mining manager's milestone watcher owns the rest.
        private static void RegisterB3MacroHatcheryBook(int startIndex, MiningTargetDto bbTarget, OrderedMineral sideMineral)
        {
            var book = new List<WorkerInstruction>();

            AddHarvestReleaseStagingInstructions(book, bbTarget.ResourceUnitId);
            AddSelfPairHarvestInstructions(book, startIndex, "jitMH", bbTarget);
            book.Add(new WorkerInstruction
            {
                InstructionSet = "wait",
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = (int)Abilities.HARVEST_GATHER_DRONE,
                TargetAbilityId = (int)Abilities.HARVEST_RETURN_DRONE
            });

            book.Add(new WorkerInstruction
            {
                InstructionSet = "mhwalk",
                Command = WorkerInstructionCommand.Gather,
                TargetId = sideMineral.UnitTag,
                NoCondition = true,
                Queue = false
            });
            book.Add(new WorkerInstruction
            {
                InstructionSet = "mhwalk",
                Command = WorkerInstructionCommand.Return,
                TargetId = bbTarget.ResourceUnitId,
                RelativeFrame = 25,
                Queue = false
            });

            book.Add(CreateHandoffWait());
            book.Add(new WorkerInstruction
            {
                InstructionSet = "mhsite",
                Command = WorkerInstructionCommand.Move,
                Point = WorkerInstructionPoint.MacroHatcherySite,
                NoCondition = true,
                Queue = false
            });

            book.Add(new WorkerInstruction
            {
                InstructionSet = "mhsite",
                Command = WorkerInstructionCommand.Wait,
                RequireMinerals = MacroHatcheryBuildMinerals
            });
            book.Add(new WorkerInstruction
            {
                InstructionSet = "mhsite",
                Command = WorkerInstructionCommand.BuildHatchery,
                Point = WorkerInstructionPoint.MacroHatcherySite,
                NoCondition = true
            });

            Settings.RuntimeInstructionSets[B3MacroHatcheryInstructionSet] = book;
        }

        private static bool IsAbbaStaggerRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role) || role.Length != 2)
            {
                return false;
            }

            var prefix = char.ToUpperInvariant(role[0]);
            var suffix = char.ToUpperInvariant(role[1]);
            return (prefix == 'Y' || prefix == 'T') && (suffix == '1' || suffix == '2');
        }

        // Single-mineral roles: every role-1 worker (S1, Y1, T1, B1, ...) mines only its
        // A mineral (MiningTargets[0]) on the {A}-{A} self-pair line, plus S2 which mines
        // only SB. Their instruction list is the self-pair loop in
        // BuildMineralInstructionList, not the two-mineral cycle the role-2 workers run.
        private static bool IsSingleMineralRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role) || role.Length != 2)
            {
                return false;
            }

            var prefix = char.ToUpperInvariant(role[0]);
            var suffix = char.ToUpperInvariant(role[1]);
            if (suffix == '1')
            {
                return true;
            }

            return prefix == 'S' && suffix == '2';
        }

        // New-logic staging pair (owner spec 2026-10-11): a frame-0 move to the target
        // mineral's CCA wait point followed by the harvest-release gate. The gate
        // blocks with no command until the mineral is free or its active harvester has
        // HarvestFrameTracker.ReleaseLeadFrames or fewer harvest frames left, so the
        // staged worker never stands on a busy mineral's footprint as the 1183 waiter
        // the game re-pathes away after 15 frames. When the gate passes, the Wait row
        // resets InstructionStartFrame, so the harvest rows behind it count their
        // relative frames from the release frame.
        private static void AddHarvestReleaseStagingInstructions(List<WorkerInstruction> instructions, ulong mineralTag)
        {
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "stage",
                Command = WorkerInstructionCommand.Move,
                Point = WorkerInstructionPoint.CcaWait,
                TargetId = mineralTag,
                RelativeFrame = 0,
                Queue = false
            });
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "stage",
                Command = WorkerInstructionCommand.Wait,
                TargetId = mineralTag,
                HarvestReleaseGate = true
            });
        }

        // Same row shape as AddReturnInstructions, but the stored point is the {X}-{X}
        // self-pair ReturnPoint — on the hatchery footprint circle straight toward X —
        // so a worker returning to the mineral it just mined takes the direct line
        // instead of converging on the shared pair return point with its teammate.
        private static void AddSelfPairReturnInstructions(List<WorkerInstruction> instructions, int startIndex, MiningTargetDto target)
        {
            AddSelfPairSetupInstruction(instructions, startIndex, "jitRM", target, WorkerInstructionCommand.UseTargetPoint, WorkerInstructionPoint.JitReturnPoint, "ReturnPoint");
            AddJitRmStopAndReturnMoveInstructions(instructions, target.ResourceUnitId);
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 0 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 1 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 14 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 15 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Return, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = -1, PositionTolerance = 0.5f, Queue = true });
        }

        // Owner spec 2026-10-13 (B2 pool): same row shape as AddSelfPairReturnInstructions,
        // but the stored point is the {BB,Pool} pair row's ReturnPoint — the preposition
        // on the shortest BB -> spawning-pool path — instead of the {BB,BB} self-pair
        // return point, so B2's deposit lands it on the way to the pool site.
        private static void AddPoolPairReturnInstructions(List<WorkerInstruction> instructions, int startIndex, MiningTargetDto target)
        {
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "jitRM",
                Command = WorkerInstructionCommand.UseTargetPoint,
                Point = WorkerInstructionPoint.JitReturnPoint,
                TargetId = target.ResourceUnitId,
                NoCondition = true,
                StoreTargetPoint = true,
                TargetPointReference = BuildPairReference(startIndex, target.ResourceLabel, "Pool", "ReturnPoint")
            });
            AddJitRmStopAndReturnMoveInstructions(instructions, target.ResourceUnitId);
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 0 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 1 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 14 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 15 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Return, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = -1, PositionTolerance = 0.5f, Queue = true });
        }

        // Repeat harvest of the same mineral: same row shape as AddHarvestInstructions,
        // but the stored point is the {X}-{X} self-pair HarvestA — the direct walk back
        // out to X on the mineral line.
        private static void AddSelfPairHarvestInstructions(List<WorkerInstruction> instructions, int startIndex, string instructionSet, MiningTargetDto target)
        {
            AddSelfPairSetupInstruction(instructions, startIndex, instructionSet, target, WorkerInstructionCommand.StoreTargetPoint, WorkerInstructionPoint.JitHarvestA, "HarvestA");
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 0 });
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 1 });
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 14 });
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 15 });
            // Version 2 experiment (owner request): the harvest command is QUEUED at
            // +16 behind the walk — it rides the order queue and begins the moment the
            // walk completes, instead of waiting for the 0.5u poll to see the arrival.
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Gather, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 16, PositionTolerance = 0.5f, Queue = true });
        }

        // Reference form matches BuildPairReference with identical labels; the pair
        // table stores self rows ({startIndex}-YB-YB etc., straight-line geometry),
        // which ResolvePairTablePoint resolves like any other pair reference.
        private static void AddSelfPairSetupInstruction(
            List<WorkerInstruction> instructions,
            int startIndex,
            string instructionSet,
            MiningTargetDto target,
            WorkerInstructionCommand command,
            WorkerInstructionPoint point,
            string property)
        {
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = instructionSet,
                Command = command,
                Point = point,
                TargetId = target.ResourceUnitId,
                NoCondition = true,
                StoreTargetPoint = true,
                TargetPointReference = BuildPairReference(startIndex, target.ResourceLabel, target.ResourceLabel, property)
            });
        }

        private static void AddHarvestInstructions(
            List<WorkerInstruction> instructions,
            int startIndex,
            string instructionSet,
            MiningTargetDto target,
            MiningTargetDto pairedTarget,
            WorkerInstructionPoint point)
        {
            AddJitPairSetupInstruction(instructions, startIndex, instructionSet, target, pairedTarget, point, WorkerInstructionCommand.StoreTargetPoint, true);
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 0 });
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 1 });
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 14 });
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 15 });
            // Version 2 experiment (owner request): the harvest command is QUEUED at
            // +16 behind the walk — the same anti-coalescing shape as before (an
            // unqueued gather fired in one batch with a move got discarded by the
            // engine, and the following wait row froze), but instead of waiting for
            // the 0.5u position poll to NOTICE the arrival, the queued gather rides
            // the order queue and begins harvesting the moment the walk completes
            // (Sharky-style zero-latency walk->mine). The 0.5u tolerance is kept as
            // a belt-and-braces gate for the frame the row fires; if a lost tread
            // idles the worker the queued gather still walks it in.
            instructions.Add(new WorkerInstruction { InstructionSet = instructionSet, Command = WorkerInstructionCommand.Gather, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 16, PositionTolerance = 0.5f, Queue = true });
        }

        private static void AddReturnInstructions(List<WorkerInstruction> instructions, int startIndex, MiningTargetDto target, MiningTargetDto pairedTarget)
        {
            AddJitPairSetupInstruction(instructions, startIndex, "jitRM", target, pairedTarget, WorkerInstructionPoint.JitReturnPoint, WorkerInstructionCommand.UseTargetPoint, true);
            AddJitRmStopAndReturnMoveInstructions(instructions, target.ResourceUnitId);
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 0 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 1 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 14 });
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Move, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = 15 });
            // Position-gated return: fires when the worker is within 0.5u of the pair return
            // point (0.1u is unreachable — unit radii), so cargo is handed off as soon as
            // the worker arrives at the hatchery.
            instructions.Add(new WorkerInstruction { InstructionSet = "jitRM", Command = WorkerInstructionCommand.Return, Point = WorkerInstructionPoint.StoredTarget, TargetId = target.ResourceUnitId, RelativeFrame = -1, PositionTolerance = 0.5f, Queue = true });
        }

        // EXPERIMENT (owner request, 2026-10-03): the STOP row is REMOVED from every
        // jitRM set. Previous shape (kept below for reference) opened with an
        // ability-gated STOP on 1184/1183 followed by this MOVE, because the old
        // evidence said an unqueued MOVE lands QUEUED behind an active 1184
        // return-walk, leaving the worker frozen ~8 frames — the STOP cleared the
        // order first, costing 1 frame of churn at every pickup edge.
        //   STOP — REMOVED.
        //   MOVE — frame-gated at RelativeFrame=1, i.e. the frame AFTER jitRM entry
        //        (the set is entered on the 1183->1184 pickup flip), to the
        //        StoredTarget return point the setup row just loaded. Deliberately
        //        NOT NoCondition and NOT ability-gated: NoCondition would fire on
        //        the entry frame and coalesce, and an ability gate would deadlock.
        // The timed re-moves (0/1/14/15) and the queued position-gated Return behind
        // these rows are unchanged. If the frozen-worker signature reappears (return
        // leg stretching ~8+ frames, worker walking to the hatchery and back out),
        // restore the STOP row.
        private static void AddJitRmStopAndReturnMoveInstructions(List<WorkerInstruction> instructions, ulong targetId)
        {
            instructions.Add(new WorkerInstruction
            {
                InstructionSet = "jitRM",
                Command = WorkerInstructionCommand.Move,
                Point = WorkerInstructionPoint.StoredTarget,
                TargetId = targetId,
                RelativeFrame = 1
            });
        }

        private static WorkerInstructionPoint ResolveFirstLinePoint(string instructionSet, string role)
        {
            return instructionSet switch
            {
                "CCAw" => WorkerInstructionPoint.JitWaitPointA,
                "jitMHbT1" => WorkerInstructionPoint.JitHarvestA,
                "jitMHb" => WorkerInstructionPoint.JitHarvestA,
                "jitMH5x5" => WorkerInstructionPoint.JitHarvestA,
                "jitMH" when role?.EndsWith("2", StringComparison.OrdinalIgnoreCase) == true => WorkerInstructionPoint.JitHarvestB,
                "jitMH" => WorkerInstructionPoint.JitHarvestA,
                _ => WorkerInstructionPoint.None
            };
        }

        // Reference form: "{startIndex}-{first}-{second}.{Property}" with first/second in
        // canonical order, so each pair reference names exactly one stored row (round 7 answer 1).
        private static string BuildPairReference(int startIndex, string labelA, string labelB, string property)
        {
            var first = IsCanonicalFirst(labelA, labelB) ? labelA : labelB;
            var second = string.Equals(first, labelA, StringComparison.Ordinal) ? labelB : labelA;
            return $"{startIndex}-{first}-{second}.{property}";
        }

        private static string TargetHarvestProperty(string targetLabel, string pairedLabel)
        {
            return IsCanonicalFirst(targetLabel, pairedLabel) ? "HarvestA" : "HarvestB";
        }

        private static string TargetWaitProperty(string targetLabel, string pairedLabel)
        {
            return IsCanonicalFirst(targetLabel, pairedLabel) ? "WaitPointA" : "WaitPointB";
        }

        private static void AddJitPairSetupInstruction(
            List<WorkerInstruction> instructions,
            int startIndex,
            string instructionSet,
            MiningTargetDto target,
            MiningTargetDto pairedTarget,
            WorkerInstructionPoint point,
            WorkerInstructionCommand command,
            bool storeTargetPoint)
        {
            var property = point switch
            {
                WorkerInstructionPoint.JitHarvestA or WorkerInstructionPoint.JitHarvestB
                    => TargetHarvestProperty(target.ResourceLabel, pairedTarget.ResourceLabel),
                WorkerInstructionPoint.JitWaitPointA or WorkerInstructionPoint.JitWaitPointB
                    => TargetWaitProperty(target.ResourceLabel, pairedTarget.ResourceLabel),
                WorkerInstructionPoint.JitReturnPoint => "ReturnPoint",
                _ => string.Empty
            };

            instructions.Add(new WorkerInstruction
            {
                InstructionSet = instructionSet,
                Command = command,
                Point = point,
                TargetId = target.ResourceUnitId,
                NoCondition = true,
                StoreTargetPoint = storeTargetPoint,
                TargetPointReference = string.IsNullOrWhiteSpace(property)
                    ? string.Empty
                    : BuildPairReference(startIndex, target.ResourceLabel, pairedTarget.ResourceLabel, property)
            });
        }

        private void LogStartupInstructionSummary(int startIndex)
        {
            if (_ccawStartupSummaryLogged)
            {
                return;
            }

            var loadedWorkers = (Globals.CurrentMapData?.AssignedWorkers?.ElementAtOrDefault(startIndex)
                    ?? new List<AssignedWorkerDto>())
                .Where(worker => worker != null && worker.UnitID != 0)
                .Select(worker => worker.UnitID)
                .Distinct()
                .ToList();
            var roleThreeCount = loadedWorkers.Count(worker => _ccawStartupLogs.Contains(worker));
            var jitMhCount = loadedWorkers.Count(worker => _jitMhStartupLogs.Contains(worker));
            // 5x5 rotation Version 2: no wait sets (the wait roles are gone), 2 bump
            // sets (T4, Y4), the rest walk straight to their first mineral.
            var expectedCcawCount = Settings.IsMagannatha5x5Rotation
                ? 0
                : Settings.IsMagannatha12WorkerOverride ? 2 : 4;
            var expectedBumpCount = Settings.IsMagannatha5x5Rotation
                ? 2
                : Settings.IsMagannatha12WorkerOverride ? 2 : 0;
            var expectedJitMhCount = loadedWorkers.Count - expectedCcawCount - expectedBumpCount;
            var bumpCount = loadedWorkers.Count(worker => _bumpStartupLogs.Contains(worker));
            if (roleThreeCount == expectedCcawCount && bumpCount == expectedBumpCount && jitMhCount == expectedJitMhCount)
            {
                Console.WriteLine($"[BUILD START INSTRUCTIONS] start={startIndex} CCAw={roleThreeCount} bump={bumpCount} jitMH={jitMhCount}.");
                _ccawStartupSummaryLogged = true;
            }
        }

        private static List<OrderedMineral> BuildRuntimeGreedyMinerals(
            List<MineralDto> liveMinerals,
            List<WorkerEntryDto> liveWorkers,
            Vector2Dto mineralCenterOfMass,
            Vector2Dto townhallPosition,
            int startIndex,
            List<HarvestReturnCargoPointDto> cargoPoints)
        {
            if (liveMinerals == null || liveMinerals.Count == 0
                || liveWorkers == null || liveWorkers.Count == 0
                || mineralCenterOfMass == null || townhallPosition == null
                || cargoPoints == null || cargoPoints.Count == 0)
            {
                return new List<OrderedMineral>();
            }

            var workerTuples = liveWorkers
                .Where(worker => worker?.Position != null && worker.UnitTag != 0)
                .Select(worker => (worker.UnitTag, worker.Position.X, worker.Position.Y, worker.Position.Z, worker.UnitType));
            var greedyWorkers = WorkerLabelChainHelper.BuildGreedyWorkerEntries(workerTuples, mineralCenterOfMass, null);
            var w1Position = greedyWorkers.LastOrDefault()?.Position;
            if (w1Position == null)
            {
                return new List<OrderedMineral>();
            }

            var mineralPositions = liveMinerals
                .Where(mineral => mineral?.Position != null && mineral.UnitTag != 0)
                .ToList();
            if (mineralPositions.Count == 0)
            {
                return new List<OrderedMineral>();
            }

            var temporaryTraversal = BuildClosestTraversal(
                mineralPositions.Select(mineral => mineral.Position).ToList(),
                w1Position,
                false);
            if (temporaryTraversal.Count != mineralPositions.Count)
            {
                return new List<OrderedMineral>();
            }

            var highEndPosition = temporaryTraversal[^1];
            var finalTraversal = BuildClosestTraversal(
                mineralPositions.Select(mineral => mineral.Position).ToList(),
                highEndPosition,
                true);
            if (finalTraversal.Count != mineralPositions.Count)
            {
                return new List<OrderedMineral>();
            }

            finalTraversal.Reverse();
            var resourceValues = mineralPositions
                .Select(mineral => Math.Max(0, mineral.MineralContents))
                .Distinct()
                .OrderBy(value => value)
                .ToList();
            if (resourceValues.Count < 2)
            {
                Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] cannot classify greedy minerals without distinct observed resource values.");
                return new List<OrderedMineral>();
            }

            var largeResourceValue = resourceValues[^1];
            var result = new List<OrderedMineral>(finalTraversal.Count);
            for (var orderIndex = 0; orderIndex < finalTraversal.Count; orderIndex++)
            {
                var position = finalTraversal[orderIndex];
                var observed = mineralPositions.First(mineral => DistanceSquared(mineral.Position, position) <= 0.0001f);
                var resources = (uint)Math.Max(0, observed.MineralContents);
                var isLarge = resources == largeResourceValue;
                var cargoPoint = cargoPoints.FirstOrDefault(point =>
                    point?.ResourcePosition != null
                    && DistanceSquared(point.ResourcePosition, observed.Position) <= 0.0001f);
                if (cargoPoint == null
                    || !HasNonZeroPoint(cargoPoint.HarvestPoint)
                    || !HasNonZeroPoint(cargoPoint.ReturnPoint))
                {
                    Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] missing persisted cargo footprint for mineral tag={observed.UnitTag}; suppressing runtime assignments.");
                    return new List<OrderedMineral>();
                }

                result.Add(new OrderedMineral
                {
                    Position = new Vector2Dto(position.X, position.Y, position.Z),
                    HarvestPoint = cargoPoint.HarvestPoint,
                    SmHarvestPoint = cargoPoint.SmHarvestPoint,
                    ReturnPoint = cargoPoint.ReturnPoint,
                    SmReturnPoint = cargoPoint.SmReturnPoint,
                    CcaWaitPoint = cargoPoint.CcaWaitPoint,
                    Index = orderIndex + 1,
                    OriginalIndex = mineralPositions.IndexOf(observed),
                    DistanceFromCOM = Distance(observed.Position, mineralCenterOfMass),
                    DistanceToTownhall = Distance(observed.Position, townhallPosition),
                    Resources = resources,
                    IsNear = isLarge,
                    IsLarge = isLarge,
                    IsFar = !isLarge,
                    Size = isLarge ? MineralSize.Large : MineralSize.Small,
                    UnitTag = observed.UnitTag
                });
            }

            Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] built runtime greedy minerals={result.Count} from live observation.");
            return result;
        }

        private static bool IsSamePosition(Vector2Dto first, Vector2Dto second)
        {
            return first != null && second != null
                && DistanceSquared(first, second) <= 0.0001f;
        }

        private static List<Vector2Dto> BuildClosestTraversal(
            List<Vector2Dto> positions,
            Vector2Dto anchor,
            bool includeAnchor)
        {
            var remaining = positions
                .Select((position, index) => (position, index))
                .ToList();
            var traversal = new List<Vector2Dto>();
            var current = anchor;

            if (includeAnchor)
            {
                var anchorEntry = remaining
                    .OrderBy(entry => DistanceSquared(entry.position, current))
                    .ThenBy(entry => entry.position.X)
                    .ThenBy(entry => entry.position.Y)
                    .FirstOrDefault();
                if (anchorEntry.position == null || DistanceSquared(anchorEntry.position, current) > 0.0001f)
                {
                    return traversal;
                }

                traversal.Add(anchorEntry.position);
                remaining.Remove(anchorEntry);
                current = anchorEntry.position;
            }

            while (remaining.Count > 0)
            {
                var next = remaining
                    .OrderBy(entry => DistanceSquared(entry.position, current))
                    .ThenBy(entry => entry.position.X)
                    .ThenBy(entry => entry.position.Y)
                    .First();
                traversal.Add(next.position);
                remaining.Remove(next);
                current = next.position;
            }

            return traversal;
        }

        private bool BuildGreedyMineralChainFromObservation(int frame)
        {
            var mapData = Globals.CurrentMapData;
            var snapshot = Globals.CurrentObservation;
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var workerCount = snapshot.SelfUnits?.Values.Count(worker => worker != null && IsWorkerType(worker.UnitType)) ?? 0;
            var relativeFrame = Settings.GetRelativeFrame(frame);
            if (mapData == null || snapshot == null || startIndex < 0)
            {
                return false;
            }

            if (_initialAssignmentLatched)
            {
                if (_greedyChainStartIndex != startIndex)
                {
                    return false;
                }

                if (Settings.IsMagannatha5x5Rotation)
                {
                    // Rotation experiment: the frame-0 assignment is permanent.
                    // B2's morph into the spawning pool and the 13/14/15 temp
                    // drones change the observed worker count — rebuilding here
                    // re-labels every worker (W1..W12), unregisters the mineral
                    // labels, and orphans the running books (observed at frame
                    // 731 of the 2026-10-13 17:53 run). New drones are onboarded
                    // as temps by OnboardNewTempDrones instead.
                    return false;
                }

                if (_lastObservedWorkerCount == workerCount)
                {
                    return false;
                }

                if (Settings.IsMagannathaMap && workerCount != 12)
                {
                    _lastObservedWorkerCount = workerCount;
                    return false;
                }

                if (relativeFrame > 0 && _workerCountTransitionApplied)
                {
                    _lastObservedWorkerCount = workerCount;
                    return false;
                }
            }

            if (mapData.StartingTownHall == null || startIndex >= mapData.StartingTownHall.Length)
            {
                return false;
            }

            var townhall = mapData.StartingTownHall[startIndex];
            var com = mapData.MineralCenterOfMass != null && mapData.MineralCenterOfMass.Count > startIndex
                ? mapData.MineralCenterOfMass[startIndex]
                : null;
            var visibleMinerals = snapshot.VisibleMinerals
                .Where(mineral => mineral?.IsVisible == true
                    && mineral.MineralContents != 0
                    && mineral.Position != null
                    && mineral.UnitTag != 0)
                .ToList();
            var workers = snapshot.SelfUnits?.Values
                .Where(worker => worker?.Position != null && IsWorkerType(worker.UnitType))
                .ToList() ?? new List<WorkerEntryDto>();

            if (townhall == null || com == null || visibleMinerals.Count != 8 || workers.Count == 0)
            {
                return false;
            }

            var cargoPoints = mapData.MainMineralCargoPoints?.ElementAtOrDefault(startIndex);
            var ordered = BuildRuntimeGreedyMinerals(visibleMinerals, workers, com, townhall, startIndex, cargoPoints);
            if (ordered.Count != visibleMinerals.Count)
            {
                return false;
            }

            mapData.OrderedMainMinerals ??= new List<List<OrderedMineral>>();
            while (mapData.OrderedMainMinerals.Count <= startIndex)
            {
                mapData.OrderedMainMinerals.Add(new List<OrderedMineral>());
            }
            mapData.OrderedMainMinerals[startIndex] = ordered;
            mapData.StartingMinerals ??= new List<List<OrderedMineral>>();
            while (mapData.StartingMinerals.Count <= startIndex)
            {
                mapData.StartingMinerals.Add(new List<OrderedMineral>());
            }
            mapData.StartingMinerals[startIndex] = ordered;
            mapData.TeamPatchAssignments ??= new List<List<TeamPatchAssignmentDto>>();
            var workerTuples = workers
                .Where(worker => worker?.Position != null && worker.UnitTag != 0)
                .Select(worker => (worker.UnitTag, worker.Position.X, worker.Position.Y, worker.Position.Z, worker.UnitType));
            var workersForAssignment = WorkerLabelChainHelper.BuildGreedyWorkerEntries(workerTuples, com, _workerLabelService);
            OrderRuntimeVespeneChain(mapData, startIndex, snapshot.Vespene.Values.ToList(), workersForAssignment, townhall);
            // Rebuild the current game's assignment records from the current worker tags.
            // Preserve MiningManager-owned frame-0 JIT geometry while refreshing worker tags.
            var previousJitPoints = mapData.TeamPatchAssignments.ElementAtOrDefault(startIndex)?
                .Where(assignment => assignment != null)
                .ToDictionary(
                    assignment => assignment.TeamNumber,
                    assignment => (assignment.JitReturnPoint, assignment.JitWaitPoint));
            while (mapData.TeamPatchAssignments.Count <= startIndex)
            {
                mapData.TeamPatchAssignments.Add(new List<TeamPatchAssignmentDto>());
            }
            mapData.TeamPatchAssignments[startIndex] = new List<TeamPatchAssignmentDto>();
            mapData.TeamPatchAssignments[startIndex] = TeamLabelRegistrationHelper.EnsureTeamLabelsForStart(
                mapData,
                startIndex,
                ordered,
                workersForAssignment,
                com,
                _workerLabelService,
                mapData.TeamPatchAssignments);
            if (previousJitPoints != null)
            {
                foreach (var assignment in mapData.TeamPatchAssignments[startIndex])
                {
                    if (assignment != null && previousJitPoints.TryGetValue(assignment.TeamNumber, out var points))
                    {
                        assignment.JitReturnPoint = points.JitReturnPoint;
                        assignment.JitWaitPoint = points.JitWaitPoint;
                    }
                }
            }
            // Pair table population moved to the callers, after CalculateCcaWaitPointsForCurrentSpawn (C7).
            _labelsInitialized = false;
            var currentTownHallUnitId = Globals.CurrentObservation?.CurrentTownHalls?.Values
                .FirstOrDefault(unit => unit?.Position != null
                    && DistanceSquared(unit.Position, townhall) <= 1f)?.UnitTag ?? 0;
            PopulateAssignedWorkersAndCrossTable(mapData, startIndex, mapData.TeamPatchAssignments[startIndex], townhall, currentTownHallUnitId);
            _greedyChainStartIndex = startIndex;
            _greedyChainWorkerCount = workersForAssignment.Count;
            if (!_initialAssignmentLatched)
            {
                _initialAssignmentLatched = true;
                _lastObservedWorkerCount = workersForAssignment.Count;
            }
            else
            {
                _workerCountTransitionApplied = true;
                _lastObservedWorkerCount = workersForAssignment.Count;
            }

            Console.WriteLine($"BabySharkBuildManager: built greedy mineral chain for start[{startIndex}] from {ordered.Count} observed minerals for workerCount={_greedyChainWorkerCount} initialLatched={_initialAssignmentLatched} transitionApplied={_workerCountTransitionApplied}.");
            return true;
        }

        private void OrderRuntimeVespeneChain(
            MawBaseLocationData mapData,
            int startIndex,
            List<OrderedVespene> observedVespenes,
            List<WorkerEntryDto> greedyWorkers,
            Vector2Dto townhall)
        {
            var anchorMineral = mapData.OrderedMainMinerals?
                .ElementAtOrDefault(startIndex)?
                .FirstOrDefault(mineral => mineral?.Index == 8);
            if (anchorMineral?.Position == null || observedVespenes == null || observedVespenes.Count == 0 || townhall == null)
            {
                return;
            }

            var currentSpawnVespenes = mapData.MainVespene?
                .ElementAtOrDefault(startIndex)?
                .Where(position => position != null)
                .ToList();
            if (currentSpawnVespenes == null || currentSpawnVespenes.Count != 2)
            {
                return;
            }

            var ordered = observedVespenes
                .Where(vespene => vespene?.Position != null
                    && vespene.UnitTag != 0
                    && currentSpawnVespenes.Any(position => DistanceSquared(position, vespene.Position) <= 0.01f))
                .Select(vespene => new
                {
                    Vespene = vespene,
                    Distance = Distance(vespene.Position, anchorMineral.Position)
                })
                .OrderBy(item => item.Distance)
                .ToList();
            if (ordered.Count != 2)
            {
                return;
            }

            var result = new List<OrderedVespene>(2);
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                var linePoints = BuildMineralLinePoints(item.Vespene.Position, townhall);
                result.Add(new OrderedVespene
                {
                    Position = item.Vespene.Position,
                    HarvestPoint = linePoints.HarvestPoint,
                    ReturnPoint = linePoints.ReturnPoint,
                    Index = index + 1,
                    DistanceToW4 = item.Distance,
                    Label = index == 0 ? "VA" : "VB",
                    UnitTag = item.Vespene.UnitTag
                });
            }

            mapData.OrderedMainVespene ??= new List<List<OrderedVespene>>();
            while (mapData.OrderedMainVespene.Count <= startIndex)
            {
                mapData.OrderedMainVespene.Add(new List<OrderedVespene>());
            }

            if (result.Count < 2 || result[0].Label != "VA" || result[1].Label != "VB")
            {
                return;
            }

            mapData.OrderedMainVespene[startIndex] = result;
            mapData.VespeneFinalLabelsByPosition ??= new Dictionary<string, string>();
            foreach (var vespene in result)
            {
                mapData.VespeneFinalLabelsByPosition[$"{vespene.Position.X:F2},{vespene.Position.Y:F2}"] = vespene.Label;
                _vespeneLabelService?.SetVespeneLabel(vespene.Label, new Point
                {
                    X = vespene.Position.X,
                    Y = vespene.Position.Y,
                    Z = vespene.Position.Z + 1.0f
                }, ProcessVisableUnits.GetFinalLabelColor(vespene.Label));
            }

            var placement = _spawningPoolPlacementService?.CalculateSpawningPoolPlacement(
                townhall,
                mapData.MineralCenterOfMass.ElementAtOrDefault(startIndex),
                result[1].Position);
            if (placement != null)
            {
                mapData.SpawningPoolPlacements ??= new List<Vector2Dto>();
                while (mapData.SpawningPoolPlacements.Count <= startIndex)
                {
                    mapData.SpawningPoolPlacements.Add(null);
                }

                mapData.SpawningPoolPlacements[startIndex] = new Vector2Dto(placement.X, placement.Y, townhall.Z);
                _spawningPoolPlacementService.DrawPlacement(placement);
            }

            Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] ordered VA/VB from M[8] anchor (VA nearest, VB furthest) and completed spawning-pool placement.");
        }

        private static void PopulateAssignedWorkersAndCrossTable(
            MawBaseLocationData mapData,
            int startIndex,
            List<TeamPatchAssignmentDto> assignments,
            Vector2Dto townhall,
            ulong townHallUnitId)
        {
            while (mapData.AssignedWorkers.Count <= startIndex)
            {
                mapData.AssignedWorkers.Add(new List<AssignedWorkerDto>());
            }

            var allMineralsByLabel = (assignments ?? new List<TeamPatchAssignmentDto>())
                .SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                .Where(mineral => !string.IsNullOrWhiteSpace(mineral.FinalLabel))
                .GroupBy(mineral => mineral.FinalLabel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var workerCount = (assignments ?? new List<TeamPatchAssignmentDto>())
                .SelectMany(assignment => assignment.Workers ?? new List<WorkerEntryDto>())
                .Count();
            var assignedWorkers = new List<AssignedWorkerDto>();
            foreach (var assignment in assignments ?? new List<TeamPatchAssignmentDto>())
            {
                foreach (var worker in assignment.Workers ?? new List<WorkerEntryDto>())
                {
                    var assigned = new AssignedWorkerDto
                    {
                        UnitID = worker.UnitTag,
                        CurrentXY = worker.Position,
                        Role = worker.FinalLabel,
                        TownHallUnitID = townHallUnitId,
                        Mti = 0
                    };

                    foreach (var targetLabel in GetInstructionLabels(worker.FinalLabel, assignment.TeamNumber, workerCount))
                    {
                        if (!allMineralsByLabel.TryGetValue(targetLabel, out var targetMineral))
                        {
                            Console.WriteLine($"[ASSIGNMENT ERROR] role={worker.FinalLabel} target={targetLabel} is not present in BuildManager labels");
                            continue;
                        }

                        var isInitial = assigned.MiningTargets.Count == 0;
                        var miningTarget = CreateMiningTarget(
                            targetMineral,
                            targetMineral,
                            townhall,
                            townHallUnitId,
                            !isInitial,
                            !isInitial);

                        assigned.MiningTargets.Add(miningTarget);
                    }

                    assignedWorkers.Add(assigned);
                }
            }

            mapData.AssignedWorkers[startIndex] = assignedWorkers;

            while (mapData.MiningTargetCrossTables.Count <= startIndex)
            {
                mapData.MiningTargetCrossTables.Add(new MiningTargetCrossTableDto());
            }

            var labels = assignments?
                .SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                .Where(mineral => !string.IsNullOrWhiteSpace(mineral.FinalLabel))
                .Select(mineral => mineral.FinalLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            var crossTable = new MiningTargetCrossTableDto
            {
                StartIndex = startIndex,
                Calculated = true,
                ResourceLabels = labels,
                JitPairs = mapData.MainJitPairReturnCalculations?.ElementAtOrDefault(startIndex)
                    ?.ToList() ?? new List<JitPairReturnCalculationDto>()
            };

            foreach (var fromLabel in labels)
            {
                var fromMineral = assignments.SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                    .First(mineral => string.Equals(mineral.FinalLabel, fromLabel, StringComparison.OrdinalIgnoreCase));
                var routes = new List<MiningTargetDto>();
                foreach (var toLabel in labels)
                {
                    var toMineral = assignments.SelectMany(assignment => assignment.Minerals ?? new List<OrderedMineral>())
                        .First(mineral => string.Equals(mineral.FinalLabel, toLabel, StringComparison.OrdinalIgnoreCase));
                    routes.Add(CreateMiningTarget(fromMineral, toMineral, townhall, townHallUnitId, string.Equals(fromLabel, toLabel, StringComparison.OrdinalIgnoreCase), !string.Equals(fromLabel, toLabel, StringComparison.OrdinalIgnoreCase)));
                }
                crossTable.Routes.Add(routes);
            }

            mapData.MiningTargetCrossTables[startIndex] = crossTable;
            Console.WriteLine($"BabySharkBuildManager: AssignedWorkers start[{startIndex}]={assignedWorkers.Count}, cross-table labels={labels.Count}, calculated={crossTable.Calculated}");
        }

        // Canonical resource order: VA, VB, then the eight minerals, then Pool.
        // Pair keys are "{startIndex}-{first}-{second}" with first before second in this
        // order. Each unordered pair (including self pairs) is stored exactly once; a self
        // row is the Speed Mining geometry for that resource. HarvestA always belongs to
        // the first-listed label, HarvestB to the second.
        private static readonly string[] CanonicalJitResourceOrder =
        {
            "VA", "VB", "TA", "TB", "SA", "SB", "BA", "BB", "YA", "YB", "Pool"
        };

        private static int CanonicalJitOrder(string label)
        {
            var index = Array.IndexOf(CanonicalJitResourceOrder, label);
            return index < 0 ? CanonicalJitResourceOrder.Length : index;
        }

        private static bool IsCanonicalFirst(string labelA, string labelB)
        {
            return CanonicalJitOrder(labelA) <= CanonicalJitOrder(labelB);
        }

        private static (float X, float Y)? Normalize(float dx, float dy)
        {
            var length = MathF.Sqrt(dx * dx + dy * dy);
            return length <= 0.0001f ? null : (dx / length, dy / length);
        }

        private static Vector2Dto HarvestPointOnCircle(Vector2Dto center, float radius, Vector2Dto toward)
        {
            if (radius <= 0f)
            {
                // Vespene geysers (worker enters) and the pool (never harvested) use their own X,Y.
                return new Vector2Dto(center.X, center.Y, center.Z);
            }

            var direction = Normalize(toward.X - center.X, toward.Y - center.Y);
            return direction == null
                ? new Vector2Dto(center.X, center.Y, center.Z)
                : new Vector2Dto(
                    center.X + direction.Value.X * radius,
                    center.Y + direction.Value.Y * radius,
                    center.Z);
        }

        private static void PopulateJitPairReturnCalculations(
            MawBaseLocationData mapData,
            int startIndex,
            Vector2Dto townhall)
        {
            if (mapData == null || startIndex < 0 || townhall == null)
            {
                return;
            }

            // Owner request: return points sit 0.1u OUTSIDE the hatchery footprint circle
            // (2.75u + 0.1u = 2.85u from the hatchery center) so the carrying worker's
            // arrival turn starts earlier on the walk back.
            const float hatcheryReturnRadius = 2.85f;
            const float mineralFootprintRadius = 1.0f;
            const float geyserFootprintRadius = 3f;

            var orderedMinerals = mapData.OrderedMainMinerals?.ElementAtOrDefault(startIndex)
                ?.Where(mineral => mineral != null && !string.IsNullOrWhiteSpace(mineral.FinalLabel))
                .OrderBy(mineral => CanonicalJitOrder(mineral.FinalLabel))
                .ToList() ?? new List<OrderedMineral>();
            var orderedVespene = mapData.OrderedMainVespene?.ElementAtOrDefault(startIndex)
                ?.Where(vespene => vespene != null && !string.IsNullOrWhiteSpace(vespene.Label))
                .OrderBy(vespene => CanonicalJitOrder(vespene.Label))
                .ToList() ?? new List<OrderedVespene>();
            var poolPosition = mapData.SpawningPoolPlacements?.ElementAtOrDefault(startIndex);

            var resources = new List<(string Label, ulong Tag, Vector2Dto Center, float HarvestRadius, Vector2Dto Wait)>();

            foreach (var vespene in orderedVespene)
            {
                // Geyser wait point: on the 3u geyser footprint toward the hatchery — the second
                // worker stands there while the first is inside collecting. The geyser harvest
                // point is the geyser's own X,Y (round 13 semantics; runtime SMART handling
                // for gas is a later adjustment, geometry only here).
                var geyserDirection = Normalize(townhall.X - vespene.Position.X, townhall.Y - vespene.Position.Y);
                var geyserWait = geyserDirection == null
                    ? new Vector2Dto(vespene.Position.X, vespene.Position.Y, vespene.Position.Z)
                    : new Vector2Dto(
                        vespene.Position.X + geyserDirection.Value.X * geyserFootprintRadius,
                        vespene.Position.Y + geyserDirection.Value.Y * geyserFootprintRadius,
                        vespene.Position.Z);
                resources.Add((vespene.Label, vespene.UnitTag, vespene.Position, 0f, geyserWait));
            }

            foreach (var mineral in orderedMinerals)
            {
                resources.Add((mineral.FinalLabel, mineral.UnitTag, mineral.Position, mineralFootprintRadius, mineral.CcaWaitPoint));
            }

            if (poolPosition != null && HasNonZeroPoint(poolPosition))
            {
                // The pool is never harvested; its harvest and wait fields carry its own X,Y so
                // no field is ever stored as (0,0) (round 2 answer 10).
                resources.Add(("Pool", 0, poolPosition, 0f, new Vector2Dto(poolPosition.X, poolPosition.Y, poolPosition.Z)));
            }

            if (resources.Count == 0)
            {
                return;
            }

            mapData.MainJitPairReturnCalculations ??= new List<List<JitPairReturnCalculationDto>>();
            while (mapData.MainJitPairReturnCalculations.Count <= startIndex)
            {
                mapData.MainJitPairReturnCalculations.Add(new List<JitPairReturnCalculationDto>());
            }

            var existing = mapData.MainJitPairReturnCalculations[startIndex] ?? new List<JitPairReturnCalculationDto>();
            var existingByKey = existing
                .Where(pair => pair != null && !string.IsNullOrWhiteSpace(pair.PairKey))
                .ToDictionary(pair => pair.PairKey, StringComparer.OrdinalIgnoreCase);
            var calculated = new List<JitPairReturnCalculationDto>();

            for (var i = 0; i < resources.Count; i++)
            {
                for (var j = i; j < resources.Count; j++)
                {
                    var first = resources[i];
                    var second = resources[j];
                    var key = $"{startIndex}-{first.Label}-{second.Label}";

                    // Reuse stored geometry only when every point list processing reads is present;
                    // rows written before B wait points existed are recalculated, not reused forever.
                    if (existingByKey.TryGetValue(key, out var stored)
                        && HasNonZeroPoint(stored.ReturnPoint)
                        && HasNonZeroPoint(stored.HarvestA)
                        && HasNonZeroPoint(stored.HarvestB)
                        && HasNonZeroPoint(stored.WaitPointA)
                        && HasNonZeroPoint(stored.WaitPointB))
                    {
                        calculated.Add(stored);
                        continue;
                    }

                    // JIT return point: 0.1u outside the hatchery footprint circle (2.85u
                    // from the hatchery center, owner request), toward the average of the
                    // two resource centers. Harvest points: on each resource's footprint
                    // circle (minerals 1.0u), toward that JIT return point. A self pair
                    // degenerates to the speed-mining line (round 9 formula).
                    var averageX = (first.Center.X + second.Center.X) * 0.5f;
                    var averageY = (first.Center.Y + second.Center.Y) * 0.5f;
                    var returnDirection = Normalize(averageX - townhall.X, averageY - townhall.Y)
                        ?? Normalize(first.Center.X - townhall.X, first.Center.Y - townhall.Y);
                    var returnPoint = returnDirection == null
                        ? new Vector2Dto(townhall.X, townhall.Y, townhall.Z)
                        : new Vector2Dto(
                            townhall.X + returnDirection.Value.X * hatcheryReturnRadius,
                            townhall.Y + returnDirection.Value.Y * hatcheryReturnRadius,
                            townhall.Z);

                    calculated.Add(new JitPairReturnCalculationDto
                    {
                        PairKey = key,
                        FromResourceLabel = first.Label,
                        ToResourceLabel = second.Label,
                        FromResourceUnitTag = first.Tag,
                        ToResourceUnitTag = second.Tag,
                        WaitPointA = HasNonZeroPoint(first.Wait)
                            ? first.Wait
                            : new Vector2Dto(first.Center.X, first.Center.Y, first.Center.Z),
                        WaitPointB = HasNonZeroPoint(second.Wait)
                            ? second.Wait
                            : new Vector2Dto(second.Center.X, second.Center.Y, second.Center.Z),
                        HarvestA = HarvestPointOnCircle(first.Center, first.HarvestRadius, returnPoint),
                        HarvestB = HarvestPointOnCircle(second.Center, second.HarvestRadius, returnPoint),
                        ReturnPoint = returnPoint
                    });
                }
            }

            mapData.MainJitPairReturnCalculations[startIndex] = calculated;
            Console.WriteLine($"BabySharkBuildManager: Start[{startIndex}] jit pair table rows={calculated.Count} (canonical order, self rows included).");
        }


        private static (Vector2Dto Harvest, Vector2Dto Return) BuildLinePoints(Vector2Dto resource, Vector2Dto townhall, float harvestOffset, float returnOffset)
        {
            var dx = resource.X - townhall.X;
            var dy = resource.Y - townhall.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= 0.0001f)
            {
                return (new Vector2Dto(resource.X, resource.Y, resource.Z), new Vector2Dto(townhall.X, townhall.Y, townhall.Z));
            }

            var ux = dx / distance;
            var uy = dy / distance;
            return (
                new Vector2Dto(resource.X - ux * harvestOffset, resource.Y - uy * harvestOffset, resource.Z),
                new Vector2Dto(townhall.X + ux * returnOffset, townhall.Y + uy * returnOffset, townhall.Z));
        }


        private static IReadOnlyList<string> GetInstructionLabels(string role, int teamNumber, int workerCount)
        {
            // 5x5 rotation books, Version 2: each worker's label list IS its personal
            // chapter order, starting at its own chapter; the last-line Jump in
            // Build5x5Book loops the rotation forever. Changes from Version 1:
            //   T4/Y4 (bumpers) now hand off on their B mineral (TB/YB) — 2nd miner —
            //   instead of the team A mineral, so their books start at chapter 2:
            //   T4 {TB,TA,SA}, Y4 {YB,BA,YA}.
            //   T5/Y5 are swing miners: one trip on the far B mineral (SB/BB) returning
            //   via the {SA,SB}/{BA,BB} pair row, then the rotation {SA,TB,TA} /
            //   {BA,YA,YB} — four entries, with targets[0] consumed by the startup set.
            //   S2/B2 speed-mine their B mineral directly (SB/BB single entry — no
            //   first trip on the A mineral).
            //   T1/Y1/Y3 and T3 mine their first mineral twice (the double harvest is
            //   appended by Build5x5Book, not encoded here). T3: SA THREE times
            //   (owner spec 2026-10-13 — one more self-pair cycle), then repeat
            //   {TB, TA, SA}. T1: TA, TA, TB, TA (owner spec 2026-10-13 — a TB
            //   chapter and a TA chapter inserted after the prologue), then the
            //   rotation SA -> TB -> TA.
            //   T2/Y2 mine their first mineral twice too (owner spec 2026-10-13):
            //   T2 {TB,TA,SA} with TB, TB first; Y2 {YB,BA,YA} with YB, YB first.
            //   Y2's old five-entry swing list (YB,YA,YB,BA,YA, "too many workers on
            //   BA" rework) is retired — Y2 left the swing group.
            // Rotation direction is unchanged: teal TA->SA->TB, yellow YA->YB->BA.
            // The return point of every chapter is the pair row of the mineral just
            // mined and the next one (T1: TA-SA return after TA, TB-SA return after
            // SA, TA-TB return after TB).
            // No workerCount gate: a rotation role must never fall through to the legacy
            // two-mineral {X}A/{X}B branch below — that stale fallback is the "old list
            // commands" that kept T1 on TA-TB and Y1 on YA-YB without the third mineral.
            if (Settings.IsMagannatha5x5Rotation
                && !string.IsNullOrWhiteSpace(role) && role.Length == 2)
            {
                var prefix = role.Substring(0, 1);
                var suffix = role[1];
                switch (prefix)
                {
                    case "T":
                        return suffix switch
                        {
                            '1' => new[] { "TA", "SA", "TB" },
                            '2' or '4' => new[] { "TB", "TA", "SA" },
                            '3' => new[] { "SA", "TB", "TA" },
                            '5' => new[] { "SB", "SA", "TB", "TA" },
                            _ => Array.Empty<string>()
                        };
                    case "Y":
                        return suffix switch
                        {
                            '1' => new[] { "YA", "YB", "BA" },
                            // Y2 (owner spec 2026-10-15): mine YB twice (the prologue in
                            // Build5x5Book appends the second harvest), then rotate
                            // BA -> YA -> YB — the standard book's first chapter is
                            // BA (targets[1]) after the prologue.
                            '2' => new[] { "YB", "BA", "YA" },
                            '3' => new[] { "BA", "YA", "YB" },
                            '4' => new[] { "YB", "BA", "YA" },
                            '5' => new[] { "BB", "BA", "YA", "YB" },
                            _ => Array.Empty<string>()
                        };
                    case "S":
                        return suffix == '2' ? new[] { "SB" } : Array.Empty<string>();
                    case "B":
                        return suffix == '2' ? new[] { "BB" } : Array.Empty<string>();
                    default:
                        return Array.Empty<string>();
                }
            }

            if (workerCount == 12 || Settings.IsMagannathaMap)
            {
                if (string.IsNullOrWhiteSpace(role) || role.Length != 2)
                {
                    return Array.Empty<string>();
                }

                var teamPrefix = role.Substring(0, 1);
                var roleNumber = role[1];
                var startsOnA = roleNumber == '1' || roleNumber == '3';
                return startsOnA
                    ? new[] { $"{teamPrefix}A", $"{teamPrefix}B" }
                    : new[] { $"{teamPrefix}B", $"{teamPrefix}A" };
            }

            // 8 worker requires a complete rework after 12 worker is satisfactory.
            // The previous per-role label table here sent workers across team patches and is deleted;
            // 8-worker starts now resolve no labels until that rework is designed.
            return Array.Empty<string>();
        }

        private static MiningTargetDto CreateMiningTarget(OrderedMineral from, OrderedMineral to, Vector2Dto townhall, ulong townHallUnitId, bool speedMining, bool abSwitch)
        {
            var returnPoint = speedMining ? from.ReturnPoint : to.ReturnPoint;
            return new MiningTargetDto
            {
                ResourceLabel = to.FinalLabel,
                FromResourceLabel = from.FinalLabel,
                ToResourceLabel = to.FinalLabel,
                ResourceUnitId = to.UnitTag,
                ResourcePosition = to.Position,
                TownHallUnitId = townHallUnitId,
                FromHarvestPoint = from.HarvestPoint,
                ToHarvestPoint = to.HarvestPoint,
                HarvestPoint = to.HarvestPoint,
                SmHarvestPoint = speedMining ? from.SmHarvestPoint : to.SmHarvestPoint,
                ReturnPoint = returnPoint,
                SmReturnPoint = speedMining ? from.SmReturnPoint : to.SmReturnPoint,
                IsSpeedMining = speedMining,
                IsABSwitch = abSwitch,
                IsInitialMineralAssignment = !abSwitch
            };
        }

        private static bool HasNonZeroPoint(Vector2Dto point)
        {
            return point != null && (point.X != 0f || point.Y != 0f);
        }

        private static (Vector2Dto HarvestPoint, Vector2Dto SmHarvestPoint, Vector2Dto ReturnPoint, Vector2Dto SmReturnPoint) BuildMineralLinePoints(Vector2Dto resource, Vector2Dto townhall)
        {
            var dx = resource.X - townhall.X;
            var dy = resource.Y - townhall.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= 0.001f)
            {
                return (new Vector2Dto(resource.X, resource.Y, resource.Z), new Vector2Dto(resource.X, resource.Y, resource.Z), new Vector2Dto(townhall.X, townhall.Y, townhall.Z), new Vector2Dto(townhall.X, townhall.Y, townhall.Z));
            }

            var ux = dx / distance;
            var uy = dy / distance;
            const float mineralHarvestOffset = 1.5f;
            var harvest = new Vector2Dto(resource.X - ux * mineralHarvestOffset, resource.Y - uy * mineralHarvestOffset, resource.Z);
            var smHarvest = new Vector2Dto(resource.X - ux * 2.75f, resource.Y - uy * 2.75f, resource.Z);
            var ret = new Vector2Dto(townhall.X + ux * 2.75f, townhall.Y + uy * 2.75f, townhall.Z);
            var smReturn = new Vector2Dto(townhall.X + ux, townhall.Y + uy, townhall.Z);
            return (harvest, smHarvest, ret, smReturn);
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

        private static float Distance(Vector2Dto first, Vector2Dto second)
        {
            return MathF.Sqrt(DistanceSquared(first, second));
        }

        private void UpdateRuntimeLabels()
        {
            var snapshot = Globals.CurrentObservation;
            if (_labelsInitialized || snapshot == null || Globals.CurrentMapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (startIndex < 0)
            {
                return;
            }

            // BuildGreedyMineralChainFromObservation is the sole worker-label and
            // assignment writer. This method only registers resource visuals and
            // consumes the already-built current-spawn assignments.
            MapLabelRegistrationHelper.RegisterLabels(
                Globals.CurrentMapData,
                startIndex,
                _mineralLabelService,
                _vespeneLabelService);

            RegisterUnitTypeLabels(snapshot);
            var currentAssignments = Globals.CurrentMapData.TeamPatchAssignments.ElementAtOrDefault(startIndex);
            var assignedMinerals = currentAssignments?
                .SelectMany(assignment => assignment.Minerals)
                .ToList();
            _labelsInitialized = currentAssignments != null
                && currentAssignments.Count == 4
                && assignedMinerals != null
                && assignedMinerals.Count == 8
                && assignedMinerals.All(mineral => mineral != null
                    && mineral.Position != null
                    && !string.IsNullOrWhiteSpace(mineral.FinalLabel));
        }

        private void RegisterUnitTypeLabels(ObservationSnapshotDto snapshot)
        {
            if (_workerLabelService == null) return;

            var hatcheryNumber = 0;
            var overlordNumber = 0;
            foreach (var unit in snapshot.CurrentTownHalls.Values.OrderBy(unit => unit.UnitTag))
            {
                hatcheryNumber++;
                _workerLabelService.SetLabel($"H{hatcheryNumber}", unit.UnitTag);
            }

            foreach (var unit in snapshot.SelfUnits.Values
                .Where(unit => unit.UnitType == (uint)UnitTypes.ZERG_OVERLORD || unit.UnitType == (uint)UnitTypes.ZERG_OVERLORDTRANSPORT)
                .OrderBy(unit => unit.UnitTag))
            {
                overlordNumber++;
                _workerLabelService.SetLabel($"OV{overlordNumber}", unit.UnitTag);
            }
        }

        private static bool IsWorkerType(uint unitType)
        {
            return unitType == (uint)UnitTypes.ZERG_DRONE
                || unitType == (uint)UnitTypes.TERRAN_SCV
                || unitType == (uint)UnitTypes.PROTOSS_PROBE;
        }

        public void OnEnd(ResponseObservation observation, Result result)
        {
        }
    }
}

