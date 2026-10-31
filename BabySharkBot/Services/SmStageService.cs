using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using BabySharkBot.Setup;
using SC2Action = SC2APIProtocol.Action;

namespace BabySharkBot.Services
{
    /// <summary>
    /// SM-harvest-point verify / tipping-point / staging-area test
    /// (owner spec 2026-10-29 12:11, --smstage), built on the --smsweep
    /// results (ideal stand points + best approach angles, hardcoded below).
    ///
    /// Per far mineral (SB, BB, BA, YB, YA), two workers strictly alternate:
    ///
    ///  1. verify + tipping scan ("tip"): approach points at 1.87u from the
    ///     mineral center, at absolute angles best+k deg (best = the sweep's
    ///     winning angle; k = 0, +1, -1, +2, -2, ...). Smart on the mineral,
    ///     wait for the stand, record the stand point. k=0 is the verify
    ///     sample — it must reproduce the ideal stand and ACTUALLY harvest
    ///     (mineral contents drop during a 12-frame harvest watch). A sample
    ///     "misses" when its stand is > TipEpsilon from the ideal stand —
    ///     that direction (+ or -) stops at its first valid miss; the last
    ///     hit before the miss is the greatest angle that still lands the
    ///     ideal spot.
    ///
    ///  2. staging area ("stage"): place the worker at ideal stand + r u
    ///     toward the hatchery (r = 0.30..0.60 step 0.05, plus +/-35 deg
    ///     flanks at r = 0.40), Stop it, and watch for StageHoldFrames
    ///     whether it holds without wandering (displacement > StageWanderDist
    ///     = wandered). From four promising spots a Smart must still hit the
    ///     ideal stand.
    ///
    /// Every sample lands in the worker_commands_*.jsonl telemetry as an
    /// SmStageSample record; per-mineral results print to the console.
    /// </summary>
    public sealed class SmStageService
    {
        private const float ApproachRadius = 1.87f;
        private const double TipEpsilon = 0.15;      // stand farther than this from ideal = miss
        private const int MaxTipK = 12;              // scan best +/- 12 deg
        private const float ArriveEpsilon = 0.12f;
        private const float StillEpsilon = 0.012f;
        private const float CommitRadius = 1.6f;     // stillness only counts this close to the mineral
        private const float NeighborBlockRadius = 1.0f;
        private const int TipStillFrames = 2;
        private const int HarvestWatchFrames = 12;
        private static readonly double[] StageRadii = { 0.30, 0.35, 0.40, 0.45, 0.50, 0.60 };
        private static readonly double[] StageFlankDeg = { -35.0, 35.0 };
        private const double StageFlankRadius = 0.40;
        private const int StageHoldFrames = 30;
        private const float StageHoldEpsilon = 0.12f;  // max drift from the spot while holding
        private const float StageWanderDist = 0.35f;   // beyond this = wandered off
        private const int SampleTimeoutFrames = 90;
        private const int TipBudgetFrames = 900;   // per mineral, then stage phase
        private const int StageBudgetFrames = 700; // per mineral, then force-complete
        private const int TotalBudgetFrames = 1700;

        private static readonly string[] SweepLabels = { "SB", "BB", "BA", "YB", "YA" };

        // --smsweep results 2026-10-29 (measured stands, both starts).
        // Angle 0 = direction mineral center -> hatchery center.
        private sealed class IdealEntry
        {
            public double CenterX, CenterY;
            public double BestAngleDeg;
            public double IdealX, IdealY;
        }

        private static readonly Dictionary<string, IdealEntry> SeIdeals = new Dictionary<string, IdealEntry>(StringComparer.OrdinalIgnoreCase)
        {
            // SE hatchery (127.5, 24.5) — CLEAN re-sweep 2026-10-29 (engine auto-gather STOPped)
            ["SB"] = new IdealEntry { CenterX = 133.0, CenterY = 19.5, BestAngleDeg = 16.0, IdealX = 131.705, IdealY = 20.231 },
            ["BB"] = new IdealEntry { CenterX = 134.0, CenterY = 20.5, BestAngleDeg = 6.0, IdealX = 132.687, IdealY = 21.204 },
            ["BA"] = new IdealEntry { CenterX = 134.0, CenterY = 22.5, BestAngleDeg = -4.0, IdealX = 132.646, IdealY = 23.084 },
            ["YB"] = new IdealEntry { CenterX = 135.0, CenterY = 23.5, BestAngleDeg = 2.0, IdealX = 133.629, IdealY = 23.590 },
            ["YA"] = new IdealEntry { CenterX = 134.0, CenterY = 24.5, BestAngleDeg = 2.0, IdealX = 132.627, IdealY = 24.488 },
        };

        private static readonly Dictionary<string, IdealEntry> NwIdeals = new Dictionary<string, IdealEntry>(StringComparer.OrdinalIgnoreCase)
        {
            // NW hatchery (24.5, 127.5) — CLEAN re-sweep 2026-10-29
            ["SB"] = new IdealEntry { CenterX = 19.0, CenterY = 132.5, BestAngleDeg = 16.0, IdealX = 20.284, IdealY = 131.766 },
            ["BB"] = new IdealEntry { CenterX = 18.0, CenterY = 131.5, BestAngleDeg = 6.0, IdealX = 19.308, IdealY = 130.793 },
            ["BA"] = new IdealEntry { CenterX = 18.0, CenterY = 129.5, BestAngleDeg = -3.0, IdealX = 19.361, IdealY = 128.921 },
            ["YB"] = new IdealEntry { CenterX = 17.0, CenterY = 128.5, BestAngleDeg = 3.0, IdealX = 18.371, IdealY = 128.347 },
            ["YA"] = new IdealEntry { CenterX = 18.0, CenterY = 127.5, BestAngleDeg = 5.0, IdealX = 19.372, IdealY = 127.563 },
        };

        private enum WorkerPhase { Resting, Moving, Smarting, Stilling, HarvestWatch, Holding, StageSmarting }
        private enum MineralPhase { Tip, Stage, Done }

        private sealed class StageSpot
        {
            public double Radius;
            public double BearingDeg; // 0 = from ideal stand toward hatchery
            public double X, Y;
            public bool SmartCheck;
        }

        private sealed class TipResult
        {
            public double? LastHitK;    // greatest k in this direction that still hit
            public double? FirstMissK;  // first valid miss in this direction
            public bool DirectionDone;  // stopped at first valid miss
        }

        private sealed class WorkerSlot
        {
            public ulong Tag;
            public WorkerPhase Phase = WorkerPhase.Resting;
            public double AngleDeg;       // current tip angle (absolute; 0 during stage)
            public StageSpot Spot;        // current stage spot (null during tip)
            public float TargetX, TargetY; // move target; during HarvestWatch = the recorded stand
            public int StillFrames;
            public int PhaseStartFrame;
            public int WatchFrames;
            public int ContentsBefore = -1;
            public int ContentsMin = -1;
            public double MaxDrift;       // staging/watch: greatest displacement from anchor
            public bool HasSampled;
        }

        private sealed class MineralTest
        {
            public string Label = string.Empty;
            public ulong MineralTag;
            public IdealEntry Ideal;
            public double BaseAngleRad;
            public WorkerSlot Worker = new WorkerSlot();
            public MineralPhase Phase = MineralPhase.Tip;
            public int KPlus = 1;         // next +k to try (k=0 verify goes first, on the + turn)
            public int KMinus = 1;        // next -k magnitude to try
            public readonly TipResult Plus = new TipResult();
            public readonly TipResult Minus = new TipResult();
            public bool PlusTurn = true;  // interleave + and - directions
            public readonly List<StageSpot> StageSpots = new List<StageSpot>();
            public int StageCursor;
            public int StartFrame;
            public int SampleCount;
            public bool VerifyDone;
            public bool Complete;
            public double? VerifyDist;    // k=0 distToIdeal
            public bool VerifyHarvested;
            public bool WatchHarvested;   // any gather-ability frame during the current harvest watch
        }

        private readonly MawBaseLocationData _mapData;
        private bool _initialized;
        private readonly List<MineralTest> _tests = new List<MineralTest>();
        private readonly List<ulong> _parkedTags = new List<ulong>();
        private readonly List<(float X, float Y, string Label)> _allMineralCenters = new List<(float, float, string)>();
        private bool _summaryPrinted;
        private Vector2Dto _hatchery = new Vector2Dto();
        private string _sideTag = string.Empty;
        private Dictionary<string, IdealEntry> _ideals;
        private ResponseObservation _lastObservation;
        private readonly Dictionary<ulong, (float X, float Y)> _lastPos = new Dictionary<ulong, (float, float)>();

        public SmStageService(MawBaseLocationData mapData)
        {
            _mapData = mapData;
        }

        public List<SC2Action> OnFrame(ResponseObservation observation, int frame)
        {
            var actions = new List<SC2Action>();
            _lastObservation = observation;
            var snapshot = Globals.CurrentObservation;
            if (snapshot?.SelfUnits == null)
            {
                return actions;
            }

            if (!_initialized)
            {
                if (!TryInitialize(snapshot, frame, actions))
                {
                    return actions; // retry next frame
                }
                // Init frame: the STOPs above must land WITHOUT any same-frame
                // MOVE to the same units (STOP+MOVE in one batch drops the
                // MOVE). First claims happen on the next frame.
                return actions;
            }

            // Single worker per mineral (owner test 2026-10-29 12:11): no
            // sibling, no strict alternation, no rest orbits. Between samples
            // the worker simply repositions from its last stand to the next
            // approach point (0.03-0.5u legs) -- which is also the production
            // scenario (returning worker stops near the mineral, then
            // Smart-walks in). Two-worker leapfrog was abandoned after the
            // 2026-10-29 rest-orbit jams: idle workers parked on the mineral
            // stands and blocked the sibling's walk-in.
            foreach (var test in _tests)
            {
                if (test.Complete) continue;
                TickWorker(test, test.Worker, frame, actions);
                CheckComplete(test, frame);
            }

            foreach (var test in _tests)
            {
                Remember(test.Worker.Tag);
            }

            if (!_summaryPrinted && _tests.Count > 0 && _tests.All(t => t.Complete))
            {
                _summaryPrinted = true;
                PrintSummary(frame);
            }
            return actions;
        }

        private static bool IsActive(WorkerSlot worker)
        {
            return worker.Phase != WorkerPhase.Resting;
        }

        // ── initialization ──

        private bool TryInitialize(ObservationSnapshotDto snapshot, int frame, List<SC2Action> actions)
        {
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var mapData = Globals.CurrentMapData ?? _mapData;
            if (startIndex < 0 || mapData == null) return false;
            var assignments = mapData.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            var hatchery = mapData.StartingTownHall != null && startIndex < mapData.StartingTownHall.Length
                ? mapData.StartingTownHall[startIndex]
                : null;
            if (assignments == null || hatchery == null) return false;

            var workers = snapshot.SelfUnits.Values
                .Where(u => u != null && u.UnitType == (uint)UnitTypes.ZERG_DRONE && u.UnitTag != 0)
                .OrderBy(u => u.UnitTag)
                .ToList();
            if (workers.Count < 11)
            {
                if (frame % 10 == 0)
                {
                    Console.WriteLine($"[SMSTAGE] waiting for workers: {workers.Count}/11 observed at frame {frame}");
                }
                return false;
            }

            _hatchery = hatchery;
            _sideTag = hatchery.X > 100f ? "SE" : "NW";
            _ideals = _sideTag == "SE" ? SeIdeals : NwIdeals;

            var subset = (Settings.SmStageMinerals ?? string.Empty)
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var labels = subset.Count > 0 ? SweepLabels.Where(l => subset.Contains(l)).ToArray() : SweepLabels;

            _allMineralCenters.Clear();
            foreach (var assignment in assignments)
            {
                foreach (var m in assignment?.Minerals ?? Enumerable.Empty<OrderedMineral>())
                {
                    if (m?.Position == null) continue;
                    var lbl = string.IsNullOrWhiteSpace(m.FinalLabel) ? m.Label : m.FinalLabel;
                    _allMineralCenters.Add((m.Position.X, m.Position.Y, lbl ?? string.Empty));
                }
            }

            var liveMinerals = snapshot.Minerals?.Values?
                .Where(m => m?.Position != null)
                .ToList() ?? new List<MineralDto>();

            foreach (var label in labels)
            {
                if (!_ideals.TryGetValue(label, out var ideal))
                {
                    Console.WriteLine($"[SMSTAGE] WARN — no ideal entry for {label} on {_sideTag}; skipping");
                    continue;
                }

                OrderedMineral persisted = null;
                foreach (var assignment in assignments)
                {
                    persisted = assignment?.Minerals?.FirstOrDefault(m =>
                        m != null
                        && (string.Equals(m.FinalLabel, label, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.Label, label, StringComparison.OrdinalIgnoreCase)));
                    if (persisted != null) break;
                }
                if (persisted?.Position == null)
                {
                    Console.WriteLine($"[SMSTAGE] WARN — persisted mineral {label} not found for start {startIndex}; skipping");
                    continue;
                }

                MineralDto live = null;
                var bestD2 = 2.25f;
                foreach (var m in liveMinerals)
                {
                    var dx = m.Position.X - persisted.Position.X;
                    var dy = m.Position.Y - persisted.Position.Y;
                    var d2 = dx * dx + dy * dy;
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        live = m;
                    }
                }
                if (live == null)
                {
                    Console.WriteLine($"[SMSTAGE] WARN — no live mineral near persisted center for {label}; skipping");
                    continue;
                }

                var test = new MineralTest
                {
                    Label = label,
                    MineralTag = live.UnitTag,
                    Ideal = ideal,
                    BaseAngleRad = Math.Atan2(hatchery.Y - ideal.CenterY, hatchery.X - ideal.CenterX),
                    Worker = new WorkerSlot { Tag = workers[_tests.Count].UnitTag },
                    StartFrame = frame,
                };
                BuildStageSpots(test);
                _tests.Add(test);
            }

            var com = mapData.MineralCenterOfMass?.ElementAtOrDefault(startIndex);
            for (var i = _tests.Count; i < workers.Count; i++)
            {
                _parkedTags.Add(workers[i].UnitTag);
            }
            if (_parkedTags.Count > 0 && com != null)
            {
                var dx = hatchery.X - com.X;
                var dy = hatchery.Y - com.Y;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 0.001)
                {
                    var px = (float)(hatchery.X + dx / len * 7.0);
                    var py = (float)(hatchery.Y + dy / len * 7.0);
                    foreach (var tag in _parkedTags)
                    {
                        var move = CreateMove(tag, px, py);
                        if (move != null) actions.Add(move);
                    }
                    Console.WriteLine($"[SMSTAGE] parked {_parkedTags.Count} worker(s) at ({px:F2},{py:F2})");
                }
            }

            _initialized = true;
            // The SC2 engine gives starting drones an AUTO-GATHER order at
            // spawn (workers were already standing on the ring with 1183 at
            // f0). A STOP at frame 0 clears it and the worker holds at spawn —
            // far from the far-line approach corridor — until its first claim.
            // Hard-won rule: a STOP and a MOVE to the same unit in ONE action
            // batch leaves the unit stopped and DROPS the MOVE, so worker A
            // must NOT be assigned in the init frame; its first AssignTip
            // MOVE lands on the next frame alone and executes.
            foreach (var test in _tests)
            {
                var stop = CreateStop(test.Worker.Tag);
                if (stop != null) actions.Add(stop);
            }
            Console.WriteLine($"[SMSTAGE] initialized frame={frame} side={_sideTag} startIndex={startIndex} minerals={string.Join(",", _tests.Select(t => $"{t.Label}(tag={t.MineralTag},w={t.Worker.Tag},best={t.Ideal.BestAngleDeg:F1})"))} hatchery=({hatchery.X:F2},{hatchery.Y:F2}) — STRICT ALTERNATION, engine auto-gather STOPped; first assigns next frame");
            return true;
        }

        private void BuildStageSpots(MineralTest test)
        {
            var hDx = _hatchery.X - test.Ideal.IdealX;
            var hDy = _hatchery.Y - test.Ideal.IdealY;
            var hLen = Math.Sqrt(hDx * hDx + hDy * hDy);
            var hUx = hDx / hLen;
            var hUy = hDy / hLen;
            foreach (var r in StageRadii)
            {
                test.StageSpots.Add(new StageSpot
                {
                    Radius = r,
                    BearingDeg = 0,
                    X = test.Ideal.IdealX + hUx * r,
                    Y = test.Ideal.IdealY + hUy * r,
                    SmartCheck = Math.Abs(r - 0.35) < 0.001 || Math.Abs(r - 0.45) < 0.001,
                });
            }
            foreach (var flank in StageFlankDeg)
            {
                var rad = flank * Math.PI / 180.0;
                var ux = hUx * Math.Cos(rad) - hUy * Math.Sin(rad);
                var uy = hUx * Math.Sin(rad) + hUy * Math.Cos(rad);
                test.StageSpots.Add(new StageSpot
                {
                    Radius = StageFlankRadius,
                    BearingDeg = flank,
                    X = test.Ideal.IdealX + ux * StageFlankRadius,
                    Y = test.Ideal.IdealY + uy * StageFlankRadius,
                    SmartCheck = true,
                });
            }
        }

        // ── tip angle generator ──

        private bool TryNextTipAngle(MineralTest test, out double angle)
        {
            while (true)
            {
                var plusOpen = !test.Plus.DirectionDone && test.KPlus <= MaxTipK;
                var minusOpen = !test.Minus.DirectionDone && test.KMinus <= MaxTipK;
                if (!plusOpen && !minusOpen)
                {
                    EnterStage(test);
                    angle = 0;
                    return false;
                }

                // k=0 verify sample goes first (on the opening + turn).
                int k;
                if (test.PlusTurn && plusOpen)
                {
                    k = test.KPlus == 1 && !test.VerifyDone ? 0 : test.KPlus;
                    if (k == 0) test.VerifyDone = true; else test.KPlus++;
                }
                else if (!test.PlusTurn && minusOpen)
                {
                    k = -test.KMinus;
                    test.KMinus++;
                }
                else if (plusOpen)
                {
                    k = test.KPlus == 1 && !test.VerifyDone ? 0 : test.KPlus;
                    if (k == 0) test.VerifyDone = true; else test.KPlus++;
                }
                else
                {
                    k = -test.KMinus;
                    test.KMinus++;
                }
                test.PlusTurn = !test.PlusTurn;

                var abs = test.Ideal.BestAngleDeg + k;
                if (IsApproachBlocked(test, abs))
                {
                    Console.WriteLine($"[SMSTAGE] {test.Label} tip angle {abs:F1} blocked (inside neighbor mineral) — skipping");
                    continue;
                }
                angle = abs;
                return true;
            }
        }

        private bool IsApproachBlocked(MineralTest test, double angleDeg)
        {
            var rad = test.BaseAngleRad + angleDeg * Math.PI / 180.0;
            var px = test.Ideal.CenterX + ApproachRadius * Math.Cos(rad);
            var py = test.Ideal.CenterY + ApproachRadius * Math.Sin(rad);
            return IsPointNearOtherMineral(test.Label, px, py);
        }

        private bool IsStageSpotBlocked(MineralTest test, StageSpot spot)
        {
            return IsPointNearOtherMineral(test.Label, spot.X, spot.Y);
        }

        private bool IsPointNearOtherMineral(string ownLabel, double x, double y)
        {
            foreach (var m in _allMineralCenters)
            {
                if (string.Equals(m.Label, ownLabel, StringComparison.OrdinalIgnoreCase)) continue;
                var dx = x - m.X;
                var dy = y - m.Y;
                if (dx * dx + dy * dy < NeighborBlockRadius * NeighborBlockRadius) return true;
            }
            return false;
        }

        private void EnterStage(MineralTest test)
        {
            if (test.Phase != MineralPhase.Tip) return;
            test.Phase = MineralPhase.Stage;
            Console.WriteLine($"[SMSTAGE] {test.Label} tip scan done (n={test.SampleCount}) — verify dist={(test.VerifyDist.HasValue ? test.VerifyDist.Value.ToString("F3") : "n/a")} harvested={test.VerifyHarvested}; entering staging probe");
        }

        // ── per-worker state machine ──

        private void TickWorker(MineralTest test, WorkerSlot worker, int frame, List<SC2Action> actions)
        {
            var live = LiveWorker(worker.Tag);
            if (live == null) return;

            switch (worker.Phase)
            {
                case WorkerPhase.Resting:
                    if (test.Phase == MineralPhase.Tip)
                    {
                        if (TryNextTipAngle(test, out var angle))
                        {
                            AssignTip(test, worker, angle, frame, actions);
                        }
                    }
                    else if (test.Phase == MineralPhase.Stage)
                    {
                        while (test.StageCursor < test.StageSpots.Count)
                        {
                            var spot = test.StageSpots[test.StageCursor++];
                            if (IsStageSpotBlocked(test, spot))
                            {
                                Console.WriteLine($"[SMSTAGE] {test.Label} stage spot r={spot.Radius:F2} b={spot.BearingDeg:F0} blocked (inside neighbor mineral) — skipping");
                                continue;
                            }
                            AssignStage(test, worker, spot, frame, actions);
                            return;
                        }
                    }
                    break;

                case WorkerPhase.Moving:
                    if (Dist(live, worker.TargetX, worker.TargetY) < ArriveEpsilon
                        || (frame - worker.PhaseStartFrame > 6
                            && Speed(worker.Tag, live) < 0.02f
                            && Dist(live, worker.TargetX, worker.TargetY) < 0.5f))
                    {
                        if (worker.Spot != null)
                        {
                            // Staging spot reached — Stop and hold.
                            var stop = CreateStop(worker.Tag);
                            if (stop != null) actions.Add(stop);
                            worker.Phase = WorkerPhase.Holding;
                            worker.PhaseStartFrame = frame;
                            worker.MaxDrift = 0;
                        }
                        else
                        {
                            var smart = CreateSmart(worker.Tag, test.MineralTag);
                            if (smart != null) actions.Add(smart);
                            worker.Phase = WorkerPhase.Smarting;
                            worker.PhaseStartFrame = frame;
                        }
                    }
                    else if (frame - worker.PhaseStartFrame > SampleTimeoutFrames)
                    {
                        if (worker.Spot != null)
                        {
                            Log(test, worker, frame, "stage-hold", 0, worker.Spot.Radius, worker.Spot.BearingDeg,
                                live.Position.X, live.Position.Y, DistToIdeal(test, live), held: false, harvested: false,
                                -1, -1, valid: false, note: "moving-timeout", actions);
                            FinishSample(test, worker, frame, actions);
                        }
                        else
                        {
                            RecordTip(test, worker, live, frame, valid: false, note: "moving-timeout", actions);
                        }
                    }
                    break;

                case WorkerPhase.Smarting:
                case WorkerPhase.StageSmarting:
                    if (Dist(live, (float)test.Ideal.CenterX, (float)test.Ideal.CenterY) < CommitRadius
                        && Speed(worker.Tag, live) < StillEpsilon)
                    {
                        worker.Phase = WorkerPhase.Stilling;
                        worker.StillFrames = 1;
                        worker.PhaseStartFrame = frame;
                    }
                    else if (frame - worker.PhaseStartFrame > SampleTimeoutFrames)
                    {
                        if (worker.Spot != null)
                        {
                            RecordStageSmart(test, worker, live, frame, held: false, note: "smarting-timeout", actions);
                        }
                        else
                        {
                            RecordTip(test, worker, live, frame, valid: false, note: "smarting-timeout", actions);
                        }
                    }
                    break;

                case WorkerPhase.Stilling:
                    if (Dist(live, (float)test.Ideal.CenterX, (float)test.Ideal.CenterY) > CommitRadius)
                    {
                        worker.Phase = worker.Spot != null ? WorkerPhase.StageSmarting : WorkerPhase.Smarting;
                        worker.StillFrames = 0;
                        worker.PhaseStartFrame = frame;
                    }
                    else if (Speed(worker.Tag, live) < StillEpsilon)
                    {
                        worker.StillFrames++;
                        if (worker.StillFrames >= TipStillFrames)
                        {
                            if (worker.Spot != null)
                            {
                                RecordStageSmart(test, worker, live, frame, held: true, note: string.Empty, actions);
                            }
                            else
                            {
                                RecordTip(test, worker, live, frame, valid: true, note: string.Empty, actions);
                            }
                        }
                    }
                    else
                    {
                        worker.StillFrames = 0;
                    }
                    if (worker.Phase == WorkerPhase.Stilling && frame - worker.PhaseStartFrame > SampleTimeoutFrames)
                    {
                        if (worker.Spot != null)
                        {
                            RecordStageSmart(test, worker, live, frame, held: false, note: "stilling-timeout", actions);
                        }
                        else
                        {
                            RecordTip(test, worker, live, frame, valid: false, note: "stilling-timeout", actions);
                        }
                    }
                    break;

                case WorkerPhase.HarvestWatch:
                    worker.WatchFrames++;
                    var contents = ReadMineralContents(test.MineralTag);
                    if (contents >= 0 && (worker.ContentsMin < 0 || contents < worker.ContentsMin)) worker.ContentsMin = contents;
                    if (!test.WatchHarvested && IsWorkerGathering(worker.Tag)) test.WatchHarvested = true;
                    var driftNow = Dist(live, worker.TargetX, worker.TargetY);
                    if (driftNow > worker.MaxDrift) worker.MaxDrift = driftNow;
                    if (worker.WatchFrames >= HarvestWatchFrames)
                    {
                        var harvested = test.WatchHarvested;
                        if (Math.Abs(worker.AngleDeg - test.Ideal.BestAngleDeg) < 0.01)
                        {
                            test.VerifyHarvested = harvested;
                        }
                        Log(test, worker, frame, "verify-watch", worker.AngleDeg, 0, 0,
                            live.Position.X, live.Position.Y, DistToIdeal(test, live), held: true, harvested,
                            worker.ContentsBefore, worker.ContentsMin, valid: true,
                            note: $"maxDrift={worker.MaxDrift:F3}", actions);
                        test.WatchHarvested = false;
                        FinishSample(test, worker, frame, actions);
                    }
                    break;

                case WorkerPhase.Holding:
                    var drift = Dist(live, worker.TargetX, worker.TargetY);
                    if (drift > worker.MaxDrift) worker.MaxDrift = drift;
                    if (drift > StageWanderDist)
                    {
                        Log(test, worker, frame, "stage-hold", 0, worker.Spot.Radius, worker.Spot.BearingDeg,
                            live.Position.X, live.Position.Y, DistToIdeal(test, live), held: false, harvested: false,
                            -1, -1, valid: true, note: $"wandered drift={drift:F3}", actions);
                        FinishSample(test, worker, frame, actions);
                    }
                    else if (frame - worker.PhaseStartFrame >= StageHoldFrames)
                    {
                        var held = worker.MaxDrift <= StageHoldEpsilon;
                        Log(test, worker, frame, "stage-hold", 0, worker.Spot.Radius, worker.Spot.BearingDeg,
                            live.Position.X, live.Position.Y, DistToIdeal(test, live), held, harvested: false,
                            -1, -1, valid: true, note: $"maxDrift={worker.MaxDrift:F3}", actions);
                        if (held && worker.Spot.SmartCheck)
                        {
                            // Hold confirmed — Smart from the staging spot must still hit the ideal stand.
                            var smart = CreateSmart(worker.Tag, test.MineralTag);
                            if (smart != null) actions.Add(smart);
                            worker.Phase = WorkerPhase.StageSmarting;
                            worker.PhaseStartFrame = frame;
                            worker.StillFrames = 0;
                        }
                        else
                        {
                            FinishSample(test, worker, frame, actions);
                        }
                    }
                    break;
            }
        }

        // ── assign / record ──

        private void AssignTip(MineralTest test, WorkerSlot worker, double angleDeg, int frame, List<SC2Action> actions)
        {
            var rad = test.BaseAngleRad + angleDeg * Math.PI / 180.0;
            worker.AngleDeg = angleDeg;
            worker.Spot = null;
            worker.TargetX = (float)(test.Ideal.CenterX + ApproachRadius * Math.Cos(rad));
            worker.TargetY = (float)(test.Ideal.CenterY + ApproachRadius * Math.Sin(rad));
            worker.StillFrames = 0;
            worker.Phase = WorkerPhase.Moving;
            worker.PhaseStartFrame = frame;
            var move = CreateMove(worker.Tag, worker.TargetX, worker.TargetY);
            if (move != null) actions.Add(move);
        }

        private void AssignStage(MineralTest test, WorkerSlot worker, StageSpot spot, int frame, List<SC2Action> actions)
        {
            worker.AngleDeg = 0;
            worker.Spot = spot;
            worker.TargetX = (float)spot.X;
            worker.TargetY = (float)spot.Y;
            worker.StillFrames = 0;
            worker.MaxDrift = 0;
            worker.Phase = WorkerPhase.Moving;
            worker.PhaseStartFrame = frame;
            var move = CreateMove(worker.Tag, worker.TargetX, worker.TargetY);
            if (move != null) actions.Add(move);
        }

        private double DistToIdeal(MineralTest test, WorkerEntryDto live)
        {
            var dx = live.Position.X - test.Ideal.IdealX;
            var dy = live.Position.Y - test.Ideal.IdealY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void RecordTip(MineralTest test, WorkerSlot worker, WorkerEntryDto live, int frame, bool valid, string note, List<SC2Action> actions)
        {
            var dIdeal = DistToIdeal(test, live);
            var k = worker.AngleDeg - test.Ideal.BestAngleDeg;
            var contents = ReadMineralContents(test.MineralTag);
            var isVerify = Math.Abs(k) < 0.01;

            if (valid)
            {
                test.SampleCount++;
                var result = k >= 0 ? test.Plus : test.Minus;
                if (dIdeal <= TipEpsilon)
                {
                    result.LastHitK = k;
                }
                else
                {
                    result.FirstMissK = k;
                    result.DirectionDone = true;
                    Console.WriteLine($"[SMSTAGE] {test.Label} tip TIPPED at k={k:F0} (abs {worker.AngleDeg:F1}) stand=({live.Position.X:F3},{live.Position.Y:F3}) dIdeal={dIdeal:F3}");
                }
                if (isVerify) test.VerifyDist = dIdeal;

                Log(test, worker, frame, isVerify ? "verify" : "tip", worker.AngleDeg, 0, 0,
                    live.Position.X, live.Position.Y, dIdeal, held: true, harvested: false,
                    contents, contents, valid, note, actions);

                if (isVerify)
                {
                    // Verify sample: keep the Smart alive and watch for a contents drop.
                    worker.Phase = WorkerPhase.HarvestWatch;
                    worker.PhaseStartFrame = frame;
                    worker.WatchFrames = 0;
                    worker.ContentsBefore = contents;
                    worker.ContentsMin = contents;
                    worker.TargetX = live.Position.X; // anchor drift at the recorded stand
                    worker.TargetY = live.Position.Y;
                    worker.MaxDrift = 0;
                    return;
                }
            }
            else
            {
                Log(test, worker, frame, isVerify ? "verify" : "tip", worker.AngleDeg, 0, 0,
                    live.Position.X, live.Position.Y, dIdeal, held: false, harvested: false,
                    contents, contents, valid, note, actions);
            }

            FinishSample(test, worker, frame, actions);
        }

        private void RecordStageSmart(MineralTest test, WorkerSlot worker, WorkerEntryDto live, int frame, bool held, string note, List<SC2Action> actions)
        {
            var dIdeal = DistToIdeal(test, live);
            var hit = held && dIdeal <= TipEpsilon;
            Log(test, worker, frame, "stage-smart", 0, worker.Spot.Radius, worker.Spot.BearingDeg,
                live.Position.X, live.Position.Y, dIdeal, held: hit, harvested: false,
                -1, -1, valid: held, note: (held ? $"dIdeal={dIdeal:F3}" : note), actions);
            FinishSample(test, worker, frame, actions);
        }

        private void FinishSample(MineralTest test, WorkerSlot worker, int frame, List<SC2Action> actions)
        {
            // The worker STAYS at its stand between samples. No STOP (a
            // same-frame STOP+MOVE to one unit drops the MOVE) and no
            // rest-orbit walk (the orbit arc is a cramped pocket where the
            // MOVE completes short and walkers block each other). The next
            // AssignTip/AssignStage repositions it 0.03-0.5u to the next
            // approach/staging spot -- the production scenario.
            worker.Phase = WorkerPhase.Resting;
            worker.StillFrames = 0;
            worker.HasSampled = true;
            worker.Spot = null;
        }

        private void Log(MineralTest test, WorkerSlot worker, int frame, string phase, double angle, double radius, double bearing,
            double standX, double standY, double dIdeal, bool held, bool harvested,
            int contentsBefore, int contentsAfter, bool valid, string note, List<SC2Action> actions)
        {
            BabySharkAI.Instance?.CommandTelemetry?.LogSmStageSample(
                frame, _sideTag, test.Label, worker.Tag, phase, angle, radius, bearing,
                worker.TargetX, worker.TargetY, standX, standY, dIdeal,
                held, harvested, contentsBefore, contentsAfter, valid, note, test.SampleCount);
            var flag = valid ? (held ? "ok" : "MISS") : $"INVALID({note})";
            Console.WriteLine($"[SMSTAGE] {test.Label} f{frame} {phase} ang={angle:F1} r={radius:F2} b={bearing:F0} stand=({standX:F3},{standY:F3}) dIdeal={dIdeal:F3} {flag} {note}");
        }

        // ── completion ──

        private void CheckComplete(MineralTest test, int frame)
        {
            if (test.Complete) return;
            var elapsed = frame - test.StartFrame;
            if (test.Phase == MineralPhase.Tip && elapsed > TipBudgetFrames)
            {
                Console.WriteLine($"[SMSTAGE] {test.Label} tip budget reached at f{frame} — entering staging");
                EnterStage(test);
            }
            if (test.Phase == MineralPhase.Stage
                && (test.StageCursor >= test.StageSpots.Count || elapsed > TotalBudgetFrames)
                && !IsActive(test.Worker))
            {
                test.Complete = true;
                var plus = test.Plus.LastHitK;
                var minus = test.Minus.LastHitK;
                Console.WriteLine($"[SMSTAGE] {test.Label} COMPLETE side={_sideTag} n={test.SampleCount} "
                    + $"verify dist={(test.VerifyDist.HasValue ? test.VerifyDist.Value.ToString("F3") : "n/a")} harvested={test.VerifyHarvested} "
                    + $"tipPlus={(plus.HasValue ? "+" + plus.Value.ToString("F0") : "none")} tipMinus={(minus.HasValue ? minus.Value.ToString("F0") : "none")}");
            }
        }

        private void PrintSummary(int frame)
        {
            Console.WriteLine($"[SMSTAGE] ===== STAGE TEST COMPLETE side={_sideTag} frame={frame} =====");
            foreach (var test in _tests)
            {
                var plus = test.Plus.LastHitK;
                var minus = test.Minus.LastHitK;
                Console.WriteLine($"[SMSTAGE] SUMMARY {test.Label}: verify dist={(test.VerifyDist.HasValue ? test.VerifyDist.Value.ToString("F3") : "n/a")} "
                    + $"harvested={test.VerifyHarvested} tipPlus={(plus.HasValue ? "+" + plus.Value.ToString("F0") : "none")} "
                    + $"tipMinus={(minus.HasValue ? minus.Value.ToString("F0") : "none")} n={test.SampleCount}");
            }
        }

        // ── helpers ──

        private int ReadMineralContents(ulong mineralTag)
        {
            var units = _lastObservation?.Observation?.RawData?.Units;
            if (units == null || mineralTag == 0) return -1;
            foreach (var u in units)
            {
                if (u != null && u.Tag == mineralTag) return u.MineralContents;
            }
            return -1;
        }

        // True when the worker's current first-order ability is a gather —
        // the direct "is it actually harvesting" signal (mineral contents
        // only tick ~55+ frames into a mining round, far past the watch).
        private bool IsWorkerGathering(ulong workerTag)
        {
            var units = _lastObservation?.Observation?.RawData?.Units;
            if (units == null || workerTag == 0) return false;
            foreach (var u in units)
            {
                if (u == null || u.Tag != workerTag) continue;
                var ability = (int)(u.Orders?.FirstOrDefault()?.AbilityId ?? 0);
                return ability == (int)Abilities.HARVEST_GATHER
                    || ability == (int)Abilities.HARVEST_GATHER_DRONE
                    || ability == (int)Abilities.HARVEST_GATHER_PROBE
                    || ability == (int)Abilities.HARVEST_GATHER_SCV;
            }
            return false;
        }

        private WorkerEntryDto LiveWorker(ulong tag)
        {
            if (tag == 0 || Globals.CurrentObservation?.SelfUnits == null) return null;
            return Globals.CurrentObservation.SelfUnits.TryGetValue(tag, out var worker) ? worker : null;
        }

        private void Remember(ulong tag)
        {
            var live = LiveWorker(tag);
            if (live?.Position != null)
            {
                _lastPos[tag] = (live.Position.X, live.Position.Y);
            }
        }

        private float Speed(ulong tag, WorkerEntryDto live)
        {
            if (live?.Position == null) return 0f;
            if (_lastPos.TryGetValue(tag, out var last))
            {
                var dx = live.Position.X - last.X;
                var dy = live.Position.Y - last.Y;
                return (float)Math.Sqrt(dx * dx + dy * dy);
            }
            return 1f;
        }

        private static float Dist(WorkerEntryDto worker, float x, float y)
        {
            if (worker?.Position == null) return float.MaxValue;
            var dx = worker.Position.X - x;
            var dy = worker.Position.Y - y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static SC2Action CreateMove(ulong tag, float x, float y)
        {
            if (tag == 0) return null;
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.MOVE,
                TargetWorldSpacePos = new Point2D { X = x, Y = y },
                QueueCommand = false,
            };
            command.UnitTags.Add(tag);
            return new SC2Action { ActionRaw = new ActionRaw { UnitCommand = command } };
        }

        private static SC2Action CreateSmart(ulong tag, ulong mineralTag)
        {
            if (tag == 0 || mineralTag == 0) return null;
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.SMART,
                TargetUnitTag = mineralTag,
                QueueCommand = false,
            };
            command.UnitTags.Add(tag);
            return new SC2Action { ActionRaw = new ActionRaw { UnitCommand = command } };
        }

        private static SC2Action CreateStop(ulong tag)
        {
            if (tag == 0) return null;
            var command = new ActionRawUnitCommand { AbilityId = (int)Abilities.STOP };
            command.UnitTags.Add(tag);
            return new SC2Action { ActionRaw = new ActionRaw { UnitCommand = command } };
        }
    }
}
