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
    /// SM-harvest-point angular sweep (owner spec 2026-10-29 08:39, --smsweep).
    ///
    /// Two workers per far mineral (SB, BB, BA, YB, YA), STRICTLY ALTERNATING
    /// (only one of the pair approaches the mineral at a time — at 1.87u two
    /// workers on 5 deg-adjacent bearings overlap physically and bump each
    /// other off the stand, which the first SE run demonstrated). The idle
    /// worker holds on a rest orbit 2.75u out on the bearing it last sampled;
    /// the moment its sibling's sample is recorded it walks in.
    ///
    /// Each sample:
    ///   1. move to the approach point 1.87u from the mineral center at the
    ///      test angle (angle 0 = direction mineral center -> hatchery center);
    ///   2. Smart command on the mineral once the approach point is reached —
    ///      the engine walks in and stands at the surface;
    ///   3. after the worker has stood still for 3 consecutive frames (while
    ///      remaining within 1.6u of the mineral center), record the stand
    ///      point and its distance to hatchery circle 1 (radius 2.91u), then
    ///      STOP and move back out to the rest orbit.
    /// Sweep direction +5 deg per sample until the distance to hatchery circle
    /// 1 WORSENS, then 2 deg steps back down until the best, then any missed
    /// 1 deg angles around the best.
    ///
    /// Every sample is written to the worker_commands_*.jsonl telemetry as an
    /// SmSweepSample record; per-mineral best is printed to the console when
    /// the sweep completes.
    /// </summary>
    public sealed class SmSweepService
    {
        private const float ApproachRadius = 1.87f;
        private const float RestRadius = 3.25f;      // idle orbit — clear of the sampler's stand zone
        private const float HatcheryCircle1Radius = 2.91f; // measured natural-test return circle (owner rule 1)
        private const double CoarseStepDeg = 5.0;
        private const double RefineStepDeg = 2.0;
        private const double MaxAngleDeg = 90.0;
        private const float ArriveEpsilon = 0.12f;
        private const float StillEpsilon = 0.012f;   // per-frame displacement (u)
        private const float CommitRadius = 1.6f;     // stillness only counts this close to the mineral
        private const float NeighborBlockRadius = 1.0f; // approach points this close to ANOTHER mineral are unreachable
        private const int RequiredStillFrames = 3;
        private const int SampleTimeoutFrames = 90;
        private const int MaxSamplesPerMineral = 60;
        private const int MineralFrameBudget = 1400; // hard cap — force fill/complete past this

        private static readonly string[] SweepLabels = { "SB", "BB", "BA", "YB", "YA" };

        private enum WorkerPhase { Resting, Moving, Smarting, Stilling }
        private enum SweepPhase { Coarse, Refine, Fill, Done }

        private sealed class Sample
        {
            public double AngleDeg;
            public float StandX, StandY;
            public double DistToHatcheryCircle1;
            public bool Valid;
            public int Frame;
            public string Note = string.Empty;
        }

        private sealed class WorkerSlot
        {
            public ulong Tag;
            public WorkerPhase Phase = WorkerPhase.Resting;
            public double AngleDeg;       // angle being sampled
            public double RestAngleDeg;   // bearing of the rest orbit
            public float TargetX, TargetY;
            public int StillFrames;
            public int PhaseStartFrame;
            public bool HasSampled;       // false until the first recorded sample — idle workers hold position until then
        }

        private sealed class MineralSweep
        {
            public string Label = string.Empty;
            public ulong MineralTag;
            public float CenterX, CenterY;
            public double BaseAngleRad; // mineral center -> hatchery center
            public WorkerSlot WorkerA = new WorkerSlot();
            public WorkerSlot WorkerB = new WorkerSlot();
            public SweepPhase Phase = SweepPhase.Coarse;
            public readonly SortedDictionary<double, Sample> Samples = new SortedDictionary<double, Sample>();
            public double LastCoarseAngle = -CoarseStepDeg; // so the first assigned coarse angle is 0
            public double RefineCursor;
            public readonly Queue<double> FillQueue = new Queue<double>();
            public bool Complete;
            public int StartFrame;
            public int ConsecutiveInvalidRefine;
        }

        private readonly MawBaseLocationData _mapData;
        private bool _initialized;
        private readonly List<MineralSweep> _sweeps = new List<MineralSweep>();
        private readonly List<ulong> _parkedTags = new List<ulong>();
        private readonly List<(float X, float Y, string Label)> _allMineralCenters = new List<(float, float, string)>();
        private bool _summaryPrinted;
        private Vector2Dto _hatchery = new Vector2Dto();
        private string _sideTag = string.Empty;
        private readonly Dictionary<ulong, (float X, float Y)> _lastPos = new Dictionary<ulong, (float, float)>();

        public SmSweepService(MawBaseLocationData mapData)
        {
            _mapData = mapData;
        }

        public List<SC2Action> OnFrame(ResponseObservation observation, int frame)
        {
            var actions = new List<SC2Action>();
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

            foreach (var sweep in _sweeps)
            {
                if (sweep.Complete) continue;
                TickWorker(sweep, sweep.WorkerA, sweep.WorkerB, frame, actions);
                TickWorker(sweep, sweep.WorkerB, sweep.WorkerA, frame, actions);
                CheckComplete(sweep, frame);
            }

            // Track per-frame displacement for stillness detection.
            foreach (var sweep in _sweeps)
            {
                Remember(sweep.WorkerA.Tag);
                Remember(sweep.WorkerB.Tag);
            }

            if (!_summaryPrinted && _sweeps.Count > 0 && _sweeps.All(s => s.Complete))
            {
                _summaryPrinted = true;
                PrintSummary(frame);
            }
            return actions;
        }

        private static bool IsActive(WorkerSlot worker)
        {
            return worker.Phase == WorkerPhase.Moving
                || worker.Phase == WorkerPhase.Smarting
                || worker.Phase == WorkerPhase.Stilling;
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
                    Console.WriteLine($"[SMSWEEP] waiting for workers: {workers.Count}/11 observed at frame {frame}");
                }
                return false;
            }

            _hatchery = hatchery;
            _sideTag = hatchery.X > 100f ? "SE" : "NW";

            // All mineral centers of this start — used to reject approach
            // points that fall inside a neighboring mineral (e.g. YB's
            // negative-angle side is inside YA; sampling there wedges the
            // worker against the neighbor for the full timeout).
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

            // Live mineral tags matched to persisted centers by position (dat
            // UnitTags are from an old game and cannot be trusted).
            var liveMinerals = snapshot.Minerals?.Values?
                .Where(m => m?.Position != null)
                .ToList() ?? new List<MineralDto>();

            foreach (var label in SweepLabels)
            {
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
                    Console.WriteLine($"[SMSWEEP] WARN — persisted mineral {label} not found for start {startIndex}; skipping");
                    continue;
                }

                MineralDto live = null;
                var bestD2 = 2.25f; // 1.5u match radius
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
                    Console.WriteLine($"[SMSWEEP] WARN — no live mineral near persisted center for {label}; skipping");
                    continue;
                }

                _sweeps.Add(new MineralSweep
                {
                    Label = label,
                    MineralTag = live.UnitTag,
                    CenterX = persisted.Position.X,
                    CenterY = persisted.Position.Y,
                    BaseAngleRad = Math.Atan2(hatchery.Y - persisted.Position.Y, hatchery.X - persisted.Position.X),
                    WorkerA = new WorkerSlot { Tag = workers[_sweeps.Count * 2].UnitTag },
                    WorkerB = new WorkerSlot { Tag = workers[_sweeps.Count * 2 + 1].UnitTag },
                    StartFrame = frame,
                });
            }

            // Workers beyond the 10 sweepers park far behind the hatchery with a
            // single move — never touched again, so they cannot bump the samples.
            var com = mapData.MineralCenterOfMass?.ElementAtOrDefault(startIndex);
            for (var i = _sweeps.Count * 2; i < workers.Count; i++)
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
                    Console.WriteLine($"[SMSWEEP] parked {_parkedTags.Count} worker(s) at ({px:F2},{py:F2})");
                }
            }

            _initialized = true;
            // The SC2 engine gives starting drones an AUTO-GATHER order at
            // spawn (workers were already on the ring with 1183 at f0). A
            // STOP at frame 0 clears it and the worker holds at spawn — far
            // from the far-line approach corridor — until its first claim.
            // Hard-won rule: a STOP and a MOVE to the same unit in ONE action
            // batch leaves the unit stopped and DROPS the MOVE, so worker A
            // must NOT be assigned in the init frame; its first Assign MOVE
            // lands on the next frame alone and executes. (Sending everyone
            // to rest orbits at once instead JAMS the hatchery-side orbit
            // cluster — mass moving-timeouts, observed 2026-10-29.)
            foreach (var sweep in _sweeps)
            {
                var stopA = CreateStop(sweep.WorkerA.Tag);
                if (stopA != null) actions.Add(stopA);
                var stopB = CreateStop(sweep.WorkerB.Tag);
                if (stopB != null) actions.Add(stopB);
            }
            Console.WriteLine($"[SMSWEEP] initialized frame={frame} side={_sideTag} startIndex={startIndex} minerals={string.Join(",", _sweeps.Select(s => $"{s.Label}(tag={s.MineralTag},w={s.WorkerA.Tag}/{s.WorkerB.Tag})"))} hatchery=({hatchery.X:F2},{hatchery.Y:F2}) circle1r={HatcheryCircle1Radius} — STRICT ALTERNATION, one sampler per mineral, engine auto-gather STOPped; first assigns next frame");
            return true;
        }

        // ── per-worker state machine ──

        private void TickWorker(MineralSweep sweep, WorkerSlot worker, WorkerSlot sibling, int frame, List<SC2Action> actions)
        {
            var live = LiveWorker(worker.Tag);
            if (live == null) return;

            switch (worker.Phase)
            {
                case WorkerPhase.Resting:
                    // Claim the next angle only while the sibling is not
                    // sampling — strict alternation keeps the two workers from
                    // bumping each other off the stand.
                    if (!IsActive(sibling) && TryNextAngle(sweep, frame, out var angle))
                    {
                        Assign(sweep, worker, angle, frame, actions);
                    }
                    break;

                case WorkerPhase.Moving:
                    if (Dist(live, worker.TargetX, worker.TargetY) < ArriveEpsilon
                        || (frame - worker.PhaseStartFrame > 6
                            && Speed(worker.Tag, live) < 0.02f
                            && Dist(live, worker.TargetX, worker.TargetY) < 0.5f))
                    {
                        var smart = CreateSmart(worker.Tag, sweep.MineralTag);
                        if (smart != null) actions.Add(smart);
                        worker.Phase = WorkerPhase.Smarting;
                        worker.PhaseStartFrame = frame;
                    }
                    else if (frame - worker.PhaseStartFrame > SampleTimeoutFrames)
                    {
                        RecordSample(sweep, worker, live, frame, valid: false, note: "moving-timeout", actions);
                    }
                    break;

                case WorkerPhase.Smarting:
                    if (Dist(live, sweep.CenterX, sweep.CenterY) < CommitRadius
                        && Speed(worker.Tag, live) < StillEpsilon)
                    {
                        worker.Phase = WorkerPhase.Stilling;
                        worker.StillFrames = 1;
                        worker.PhaseStartFrame = frame;
                    }
                    else if (frame - worker.PhaseStartFrame > SampleTimeoutFrames)
                    {
                        RecordSample(sweep, worker, live, frame, valid: false, note: "smarting-timeout", actions);
                    }
                    break;

                case WorkerPhase.Stilling:
                    if (Dist(live, sweep.CenterX, sweep.CenterY) > CommitRadius)
                    {
                        // Bumped off the mineral — go back to waiting for the
                        // gather to settle; never record a far-away stand.
                        worker.Phase = WorkerPhase.Smarting;
                        worker.StillFrames = 0;
                        worker.PhaseStartFrame = frame;
                    }
                    else if (Speed(worker.Tag, live) < StillEpsilon)
                    {
                        worker.StillFrames++;
                        if (worker.StillFrames >= RequiredStillFrames)
                        {
                            RecordSample(sweep, worker, live, frame, valid: true, note: string.Empty, actions);
                        }
                    }
                    else
                    {
                        worker.StillFrames = 0;
                    }
                    if (worker.Phase == WorkerPhase.Stilling && frame - worker.PhaseStartFrame > SampleTimeoutFrames)
                    {
                        RecordSample(sweep, worker, live, frame, valid: false, note: "stilling-timeout", actions);
                    }
                    break;
            }
        }

        private void Assign(MineralSweep sweep, WorkerSlot worker, double angleDeg, int frame, List<SC2Action> actions)
        {
            var rad = sweep.BaseAngleRad + angleDeg * Math.PI / 180.0;
            worker.AngleDeg = angleDeg;
            worker.TargetX = (float)(sweep.CenterX + ApproachRadius * Math.Cos(rad));
            worker.TargetY = (float)(sweep.CenterY + ApproachRadius * Math.Sin(rad));
            worker.StillFrames = 0;
            worker.Phase = WorkerPhase.Moving;
            worker.PhaseStartFrame = frame;
            var move = CreateMove(worker.Tag, worker.TargetX, worker.TargetY);
            if (move != null) actions.Add(move);
        }

        // Send the worker back out to the rest orbit on the bearing it just
        // sampled (radius far enough out to be clear of the next sampler).
        // Workers that have never sampled simply hold where they are — their
        // initial walk to a rest orbit would cross the active sampler's zone.
        private void SendToRest(MineralSweep sweep, WorkerSlot worker, int frame, List<SC2Action> actions)
        {
            worker.Phase = WorkerPhase.Resting;
            worker.StillFrames = 0;
            worker.PhaseStartFrame = frame;
            if (!worker.HasSampled)
            {
                return;
            }
            var rad = sweep.BaseAngleRad + worker.RestAngleDeg * Math.PI / 180.0;
            worker.TargetX = (float)(sweep.CenterX + RestRadius * Math.Cos(rad));
            worker.TargetY = (float)(sweep.CenterY + RestRadius * Math.Sin(rad));
            var move = CreateMove(worker.Tag, worker.TargetX, worker.TargetY);
            if (move != null) actions.Add(move);
        }

        private void RecordSample(MineralSweep sweep, WorkerSlot worker, WorkerEntryDto live, int frame, bool valid, string note, List<SC2Action> actions)
        {
            var standX = live.Position.X;
            var standY = live.Position.Y;
            var dh = Math.Sqrt(
                (standX - _hatchery.X) * (standX - _hatchery.X)
                + (standY - _hatchery.Y) * (standY - _hatchery.Y));
            var dCircle = Math.Abs(dh - HatcheryCircle1Radius);

            var sample = new Sample
            {
                AngleDeg = worker.AngleDeg,
                StandX = standX,
                StandY = standY,
                DistToHatcheryCircle1 = dCircle,
                Valid = valid,
                Frame = frame,
                Note = note ?? string.Empty,
            };
            sweep.Samples[worker.AngleDeg] = sample;
            worker.RestAngleDeg = worker.AngleDeg;
            worker.HasSampled = true;

            // NO STOP here: a STOP and a MOVE to the same unit in one action
            // batch leaves the unit stopped and DROPS the MOVE (observed
            // 2026-10-29). SendToRest's unqueued rest MOVE alone replaces the
            // gather order.

            BabySharkAI.Instance?.CommandTelemetry?.LogSmSweepSample(
                frame, _sideTag, sweep.Label, worker.Tag, worker.AngleDeg,
                worker.TargetX, worker.TargetY, standX, standY, dCircle,
                valid, worker.StillFrames, sweep.Samples.Count);

            OnSampleComplete(sweep, sample);

            if (!sweep.Complete)
            {
                SendToRest(sweep, worker, frame, actions);
            }

            var flag = valid ? "ok" : $"INVALID({note})";
            Console.WriteLine($"[SMSWEEP] {sweep.Label} f{frame} ang={worker.AngleDeg:F1} stand=({standX:F3},{standY:F3}) dC={dCircle:F3} {flag}");
        }

        // ── coarse (5 deg) -> refine (2 deg) -> fill (1 deg) phase logic ──

        private void OnSampleComplete(MineralSweep sweep, Sample sample)
        {
            if (sweep.Phase == SweepPhase.Coarse)
            {
                var a = sample.AngleDeg;
                var worsened = false;
                if (sweep.Samples.TryGetValue(a - CoarseStepDeg, out var prev) && prev.Valid && sample.Valid
                    && sample.DistToHatcheryCircle1 > prev.DistToHatcheryCircle1)
                {
                    worsened = true; // this angle is worse than the previous coarse step
                }
                if (!worsened && sweep.Samples.TryGetValue(a + CoarseStepDeg, out var next) && next.Valid && sample.Valid
                    && next.DistToHatcheryCircle1 > sample.DistToHatcheryCircle1)
                {
                    worsened = true; // the later-assigned angle already proved worse
                }
                if (worsened || a >= MaxAngleDeg)
                {
                    EnterRefine(sweep);
                }
            }
            else if (sweep.Phase == SweepPhase.Refine)
            {
                var a = sample.AngleDeg;
                if (sample.Valid)
                {
                    sweep.ConsecutiveInvalidRefine = 0;
                }
                else
                {
                    sweep.ConsecutiveInvalidRefine++;
                }
                var worsened = sweep.Samples.TryGetValue(a + RefineStepDeg, out var prev)
                    && prev.Valid && sample.Valid
                    && sample.DistToHatcheryCircle1 > prev.DistToHatcheryCircle1;
                if (worsened || a <= -MaxAngleDeg || sweep.ConsecutiveInvalidRefine >= 2)
                {
                    if (sweep.ConsecutiveInvalidRefine >= 2 && !worsened)
                    {
                        Console.WriteLine($"[SMSWEEP] {sweep.Label} refine bailing out — {sweep.ConsecutiveInvalidRefine} consecutive invalid samples (blocked side?)");
                    }
                    EnterFill(sweep);
                }
            }
            // Fill phase: nothing to decide — CheckComplete finishes the mineral.
        }

        private double BestAngle(MineralSweep sweep)
        {
            var valid = sweep.Samples.Where(kv => kv.Value.Valid).ToList();
            var pool = valid.Count > 0 ? valid : sweep.Samples.ToList();
            return pool.OrderBy(kv => kv.Value.DistToHatcheryCircle1).First().Key;
        }

        private void EnterRefine(MineralSweep sweep)
        {
            var bestAngle = BestAngle(sweep);
            sweep.RefineCursor = bestAngle;
            sweep.Phase = SweepPhase.Refine;
            Console.WriteLine($"[SMSWEEP] {sweep.Label} coarse done after {sweep.Samples.Count} samples — best coarse {bestAngle:F1} deg; refining down in {RefineStepDeg:F0} deg steps");
        }

        private void EnterFill(MineralSweep sweep)
        {
            var best = BestAngle(sweep);
            // Any missed 1-degree angle around the best (+-3 deg), nearest first.
            var candidates = Enumerable.Range(-3, 7)
                .Select(d => best + d)
                .Where(ang => Math.Abs(ang) <= MaxAngleDeg && !sweep.Samples.ContainsKey(ang) && !IsApproachBlocked(sweep, ang))
                .OrderBy(ang => Math.Abs(ang - best))
                .ToList();
            foreach (var ang in candidates)
            {
                sweep.FillQueue.Enqueue(ang);
            }
            sweep.Phase = SweepPhase.Fill;
            Console.WriteLine($"[SMSWEEP] {sweep.Label} refine done — best {best:F1} deg; filling {candidates.Count} missed 1-deg angles");
        }

        private bool TryNextAngle(MineralSweep sweep, int frame, out double angle)
        {
            while (true)
            {
                if (frame - sweep.StartFrame > MineralFrameBudget && sweep.Phase != SweepPhase.Fill)
                {
                    Console.WriteLine($"[SMSWEEP] {sweep.Label} frame budget reached — forcing fill");
                    EnterFill(sweep);
                }
                if (sweep.FillQueue.Count > 0)
                {
                    angle = sweep.FillQueue.Dequeue();
                    if (IsApproachBlocked(sweep, angle)) continue; // skip and take the next fill candidate
                    return true;
                }
                if (sweep.Phase == SweepPhase.Coarse)
                {
                    var next = sweep.LastCoarseAngle + CoarseStepDeg;
                    if (next > MaxAngleDeg)
                    {
                        EnterRefine(sweep);
                        continue;
                    }
                    sweep.LastCoarseAngle = next;
                    if (sweep.Samples.ContainsKey(next)) continue;
                    if (IsApproachBlocked(sweep, next))
                    {
                        Console.WriteLine($"[SMSWEEP] {sweep.Label} coarse angle {next:F1} blocked (inside neighbor mineral) — skipping");
                        continue;
                    }
                    angle = next;
                    return true;
                }
                if (sweep.Phase == SweepPhase.Refine)
                {
                    var cursor = sweep.RefineCursor - RefineStepDeg;
                    while (cursor > -MaxAngleDeg
                        && (sweep.Samples.ContainsKey(cursor) || IsApproachBlocked(sweep, cursor)))
                    {
                        if (!sweep.Samples.ContainsKey(cursor))
                        {
                            Console.WriteLine($"[SMSWEEP] {sweep.Label} refine angle {cursor:F1} blocked (inside neighbor mineral) — skipping");
                        }
                        cursor -= RefineStepDeg;
                    }
                    if (cursor < -MaxAngleDeg)
                    {
                        EnterFill(sweep);
                        continue;
                    }
                    sweep.RefineCursor = cursor;
                    angle = cursor;
                    return true;
                }
                // Fill with an empty queue: nothing to assign right now.
                angle = 0;
                return false;
            }
        }

        private void CheckComplete(MineralSweep sweep, int frame)
        {
            if (sweep.Complete || sweep.Phase != SweepPhase.Fill) return;
            if (sweep.FillQueue.Count > 0) return;
            if (IsActive(sweep.WorkerA) || IsActive(sweep.WorkerB)) return;
            if (sweep.Samples.Count == 0) return;

            sweep.Complete = true;
            var bestKey = BestAngle(sweep);
            var best = sweep.Samples[bestKey];
            Console.WriteLine($"[SMSWEEP] {sweep.Label} COMPLETE side={_sideTag} n={sweep.Samples.Count} valid={sweep.Samples.Values.Count(s => s.Valid)} "
                + $"bestAngle={bestKey:F1} stand=({best.StandX:F3},{best.StandY:F3}) dCircle={best.DistToHatcheryCircle1:F3}");
        }

        private void PrintSummary(int frame)
        {
            Console.WriteLine($"[SMSWEEP] ===== SWEEP COMPLETE side={_sideTag} frame={frame} =====");
            foreach (var sweep in _sweeps)
            {
                var bestKey = BestAngle(sweep);
                var best = sweep.Samples[bestKey];
                Console.WriteLine($"[SMSWEEP] SUMMARY {sweep.Label}: bestAngle={bestKey:F1} deg stand=({best.StandX:F3},{best.StandY:F3}) "
                    + $"dCircle={best.DistToHatcheryCircle1:F3} n={sweep.Samples.Count} valid={sweep.Samples.Values.Count(s => s.Valid)}");
            }
        }

        // ── helpers ──

        // An approach point this close to a DIFFERENT mineral's center is
        // unreachable — the engine wedges the worker against the neighbor
        // mineral and the sample burns the whole timeout (seen on YB's
        // negative-angle side, which sits inside YA's footprint NW).
        private bool IsApproachBlocked(MineralSweep sweep, double angleDeg)
        {
            var rad = sweep.BaseAngleRad + angleDeg * Math.PI / 180.0;
            var px = sweep.CenterX + ApproachRadius * (float)Math.Cos(rad);
            var py = sweep.CenterY + ApproachRadius * (float)Math.Sin(rad);
            foreach (var m in _allMineralCenters)
            {
                if (string.Equals(m.Label, sweep.Label, StringComparison.OrdinalIgnoreCase)) continue;
                var dx = px - m.X;
                var dy = py - m.Y;
                if (dx * dx + dy * dy < NeighborBlockRadius * NeighborBlockRadius) return true;
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
            return 1f; // no history yet — treat as moving
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
