using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using BabySharkBot.Managers;
using BabySharkBot.Setup;
using SC2Action = SC2APIProtocol.Action;

namespace BabySharkBot.Services
{
    /// <summary>
    /// SM staging handoff test (owner spec 2026-10-29 16:37, --smhandoff),
    /// built on the --smsweep ideal points and the --smstage staging results.
    ///
    /// Per far mineral (SB, BB, BA, YB, YA), TWO workers:
    ///
    ///  A (harvester): MOVE to the verified 1.87u approach point at the
    ///     sweep's best angle, then SMART the mineral. Its harvest frames are
    ///     counted with the HarvestFrameTracker convention — stationary
    ///     (speed below epsilon) AND gather ability 1183.
    ///  B (stager): MOVE to the staging spot (ideal stand + Settings.
    ///     SmHandoffRadius u toward the hatchery, bearing 0), STOP, and hold.
    ///
    /// When A has ACCUMULATED HandoffHarvestFrames (32) harvest frames, B
    /// receives a SMART on the mineral (owner: "a worker should hold at the
    /// Staging area until the Harvesting worker has harvested for 32 frames
    /// then they receive a smart command").
    ///
    /// Measured per mineral: A's stand and first-harvest frame, the 32-frame
    /// Smart frame, A's last harvest frame and 1183->1184 flip (round end),
    /// B's hold start, Smart frame, stand frame + distance from the ideal
    /// stand, waiter settle, first harvest frame after promotion, and the
    /// SEAM = B first harvest - A last harvest (mineral idle frames;
    /// negative = overlap). Mutual interference: A's max drift from its
    /// recorded stand after B settles nearby, and B's max drift from the
    /// staging spot during the hold.
    ///
    /// Every event lands in the worker_commands_*.jsonl telemetry as an
    /// SmHandoffSample record; per-mineral results print to the console.
    /// </summary>
    public sealed class SmHandoffService
    {
        private const float ApproachRadius = 1.87f;   // legacy sweep corridor (superseded by NearPoint)
        // Owner 2026-10-30 19:39 — workers must mine from the YELLOW point, not
        // the green one: stand at the mineral's near edge ON the hatchery line
        // (~0.85u from center per his annotated screenshot; his circles measured
        // 0.61-0.76u hand-drawn). The 1.4-1.5u sweep-Ideal corridor put miners
        // too far out, rotated off the hatchery line, narrowing every corridor.
        private const float NearPointRadius = 0.85f;
        private const int HandoffHarvestFrames = 32;  // legacy fallback (superseded by arrival-targeted release below)
        private const int ArriveLeadFrames = 14;      // owner answer 1 (2026-10-30): next worker ARRIVES at point 0
                                                      // 13-14 frames before the current harvester's 1183->1184 flip
                                                      // (<15 keeps it safe from the engine's busy-mineral re-target).
        private const float ArriveEpsilon = 0.12f;
        private const float StillEpsilon = 0.012f;
        private const float HarvestStillEpsilon = 0.03f; // stationary threshold for harvest-frame counting
        private const float CommitRadius = 1.6f;
        private const int TipStillFrames = 2;
        private const int BSmartTimeoutFrames = 120;   // Smart -> stand at the mineral
        private const int BWaitTimeoutFrames = 250;    // stand -> first harvest after promotion
        private const int BudgetFrames = 800;          // per mineral, then force-complete
        private const float WanderDist = 0.35f;        // beyond this from the staging spot = interference/bounce

        private static readonly string[] HandoffLabels = { "SB", "BB", "BA", "YB", "YA" };

        // --smsweep results 2026-10-29 (measured stands, both starts).
        // Angle 0 = direction mineral center -> hatchery center.
        // KEEP IN SYNC with SmStageService.SeIdeals / NwIdeals.
        private sealed class IdealEntry
        {
            public double CenterX, CenterY;
            public double BestAngleDeg;
            public double IdealX, IdealY;
        }

        private static readonly Dictionary<string, IdealEntry> SeIdeals = new Dictionary<string, IdealEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["SB"] = new IdealEntry { CenterX = 133.0, CenterY = 19.5, BestAngleDeg = 16.0, IdealX = 131.705, IdealY = 20.231 },
            ["BB"] = new IdealEntry { CenterX = 134.0, CenterY = 20.5, BestAngleDeg = 6.0, IdealX = 132.687, IdealY = 21.204 },
            ["BA"] = new IdealEntry { CenterX = 134.0, CenterY = 22.5, BestAngleDeg = -4.0, IdealX = 132.646, IdealY = 23.084 },
            ["YB"] = new IdealEntry { CenterX = 135.0, CenterY = 23.5, BestAngleDeg = 2.0, IdealX = 133.629, IdealY = 23.590 },
            ["YA"] = new IdealEntry { CenterX = 134.0, CenterY = 24.5, BestAngleDeg = 2.0, IdealX = 132.627, IdealY = 24.488 },
        };

        private static readonly Dictionary<string, IdealEntry> NwIdeals = new Dictionary<string, IdealEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["SB"] = new IdealEntry { CenterX = 19.0, CenterY = 132.5, BestAngleDeg = 16.0, IdealX = 20.284, IdealY = 131.766 },
            ["BB"] = new IdealEntry { CenterX = 18.0, CenterY = 131.5, BestAngleDeg = 6.0, IdealX = 19.308, IdealY = 130.793 },
            ["BA"] = new IdealEntry { CenterX = 18.0, CenterY = 129.5, BestAngleDeg = -3.0, IdealX = 19.361, IdealY = 128.921 },
            ["YB"] = new IdealEntry { CenterX = 17.0, CenterY = 128.5, BestAngleDeg = 3.0, IdealX = 18.371, IdealY = 128.347 },
            ["YA"] = new IdealEntry { CenterX = 18.0, CenterY = 127.5, BestAngleDeg = 5.0, IdealX = 19.372, IdealY = 127.563 },
        };

        private enum APhase { ToApproach, Smarting, Stilling, Harvesting, RoundOver }
        private enum BPhase { ToStage, Holding, Smarting, Stilling, Waiting, Harvesting }

        private sealed class HandoffTest
        {
            public string Label = string.Empty;
            public ulong MineralTag;
            public IdealEntry Ideal;
            public double BaseAngleRad;
            public int StartFrame;
            public ulong TagA;            // harvester
            public ulong TagB;            // stager
            public int EventOrdinal;

            public APhase APhase = APhase.ToApproach;
            public int APhaseStart;
            public float ATargetX, ATargetY;
            public float APX, APY;        // sweep approach point (1.87u at best angle)
            public float AStandX, AStandY;
            public bool AStandRecorded;
            public int AStandFrame = -1;
            public int AStillFrames;
            public int AFirstHarvestFrame = -1;
            public int ALastHarvestFrame = -1;
            public int AReturnFrame = -1;
            public int AHarvestFrames;
            public int ACurrentGap;
            public int AMaxGap;
            public double AMaxDrift;
            public bool ASmartIssued;
            public int AHFAtSmart = -1;   // A's harvest-frame count at the moment B's Smart fired

            public BPhase BPhase = BPhase.ToStage;
            public int BPhaseStart;
            public float BSpotX, BSpotY;
            public double BSpotAngleDeg;  // absolute spot angle from the mineral center (0 = hatchery line)
            public bool BHoldStarted;
            public int BHoldStartFrame = -1;
            public int BSmartFrame = -1;
            public int BStandFrame = -1;
            public double BStandDIdeal = -1;
            public int BWaiterFrame = -1;      // first stationary 1183 while A still active (waiter settle)
            public int BFirstHarvestFrame = -1; // first stationary 1183 after A's flip (promoted)
            public int BHarvestFrames;
            public double BMaxDriftHold;
            public bool BWandered;
            public int BStillFrames;

            public bool Complete;
            public int BookReasserts;   // engine re-targets caught by the book
            public ulong TagC;          // shuffle worker three (owner 2026-10-30); 0 = none
            public float P2X, P2Y;      // shuffle point 2 (far slot), corridor 0.4u behind point 1
            public ulong NearTag;       // point-1 slot occupant (0 = free)
            public ulong FarTag;        // point-2 slot occupant (0 = free)
            // Arrival-targeted release (owner answer 1): expected stand-still
            // harvest total measured per mineral from flips (YA = 45 exactly),
            // walk-in measured Smart->standstill each cycle. Stager releases
            // when partner's remaining frames <= ArriveLeadFrames + walkIn.
            public int ExpectedHarvestTotal = 45;
            public double WalkInEstimate = 8.0; // engine walk AP->cell ≈ 1.87u; calibrates from telemetry
            public BookWorker BookA;
            public BookWorker BookB;
            public BookWorker BookC;    // shuffle worker three (null on 2-worker tests)
        }

        private readonly MawBaseLocationData _mapData;
        private bool _initialized;
        private readonly List<HandoffTest> _tests = new List<HandoffTest>();
        private readonly List<ulong> _parkedTags = new List<ulong>();
        private bool _summaryPrinted;
        private Vector2Dto _hatchery = new Vector2Dto();
        private ulong _hatcheryTag;
        private string _sideTag = string.Empty;
        private Dictionary<string, IdealEntry> _ideals;
        private ResponseObservation _lastObservation;
        private readonly Dictionary<ulong, (float X, float Y)> _lastPos = new Dictionary<ulong, (float, float)>();
        // Owner 2026-10-30 17:10 — repurposed idle workers. Commands must NOT
        // land on the init frame (STOP+command same batch drops the command),
        // so they queue here and issue on the first frame after init.
        private readonly List<(ulong WorkerTag, ulong TargetTag)> _pendingSmarts = new List<(ulong, ulong)>();
        private ulong _vespineWorkerTag;      // worker given the one-time Smart on VA
        private ulong _vespineGeyserTag;      // live VA geyser tag (resolved by position)
        private double _vespineGeyserX, _vespineGeyserY;
        private int _vespineSmartFrame = -1;  // frame the vespine Smart was issued
        private bool _vespineArrivedLogged;

        public SmHandoffService(MawBaseLocationData mapData)
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
                // Init frame: STOPs only — no same-frame MOVE to the same units
                // (STOP+MOVE in one batch drops the MOVE). First MOVEs land next frame.
                return actions;
            }

            // First post-init frame: issue the queued one-time Smarts (W3 -> YB,
            // vespine test -> VA geyser). The init STOPs have landed by now.
            if (_pendingSmarts.Count > 0)
            {
                foreach (var (workerTag, targetTag) in _pendingSmarts)
                {
                    // Vespine worker: MOVE to the geyser position. Live-tested
                    // 2026-10-30: BOTH Smart and explicit 3666 on a BARE geyser
                    // are redirected by the engine to mineral gathering (no
                    // extractor -> no vespene gather). The walk to the geyser
                    // is a plain MOVE; 3666 gathers come AFTER the extractor
                    // exists (CreateGatherOnGeyser, Sharky-proven).
                    SC2Action smart;
                    if (workerTag == _vespineWorkerTag)
                    {
                        smart = CreateMove(workerTag, (float)_vespineGeyserX, (float)_vespineGeyserY);
                    }
                    else
                    {
                        smart = CreateSmart(workerTag, targetTag);
                    }
                    if (smart != null) actions.Add(smart);
                    if (workerTag == _vespineWorkerTag) _vespineSmartFrame = frame;
                    Console.WriteLine($"[SMHANDOFF] pending command issued f{frame}: worker {workerTag} -> target {targetTag}{(workerTag == _vespineWorkerTag ? $" (MOVE to VA geyser {_vespineGeyserX:F2},{_vespineGeyserY:F2})" : " (YB W3 smart)")}");
                }
                _pendingSmarts.Clear();
            }

            foreach (var test in _tests)
            {
                if (test.Complete) continue;
                Tick(test, frame, actions);
                CheckComplete(test, frame);
            }

            // Post-completion SPEED-MINING BOOK: each completed test's pair runs
            // the full-speed loop — extended MOVE 2u past each target (no
            // deceleration), Smart/Return timed ~2 frames before arrival, and
            // staging at the spot when the partner is mid-cycle.
            // MUST run BEFORE Remember(): Speed() reads the delta Remember
            // computes, so the book would otherwise always see speed 0.
            foreach (var test in _tests)
            {
                if (!test.Complete) continue;
                TickBook(test, frame, actions);
            }

            foreach (var test in _tests)
            {
                Remember(test.TagA);
                Remember(test.TagB);
            }

            // Vespine-walk observation (owner 2026-10-30 17:10): log the
            // test worker's approach every 20f until it reaches the geyser,
            // so "does a Smart mineral-walk to the geyser?" is answered from
            // telemetry without stepping through.
            if (_vespineWorkerTag != 0 && !_vespineArrivedLogged && frame % 20 == 0)
            {
                var vl = LiveWorker(_vespineWorkerTag);
                if (vl?.Position != null)
                {
                    var dvg = Math.Sqrt(
                        (vl.Position.X - _vespineGeyserX) * (vl.Position.X - _vespineGeyserX)
                        + (vl.Position.Y - _vespineGeyserY) * (vl.Position.Y - _vespineGeyserY));
                    var ab = OrderInfo(_vespineWorkerTag).ability;
                    if (dvg < 2.6)
                    {
                        _vespineArrivedLogged = true;
                        Console.WriteLine($"[SMHANDOFF] vespine f{frame} ARRIVED at VA geyser ({vl.Position.X:F2},{vl.Position.Y:F2}) dVA={dvg:F2} ability={ab} — MOVE walks to the geyser");
                    }
                    else if (_vespineSmartFrame >= 0 && frame - _vespineSmartFrame > 1344)
                    {
                        _vespineArrivedLogged = true;
                        Console.WriteLine($"[SMHANDOFF] vespine f{frame} TIMEOUT — worker at ({vl.Position.X:F2},{vl.Position.Y:F2}) dVA={dvg:F2} ability={ab} after {frame - _vespineSmartFrame}f — MOVE did not reach the geyser");
                    }
                    else
                    {
                        Console.WriteLine($"[SMHANDOFF] vespine f{frame} worker ({vl.Position.X:F2},{vl.Position.Y:F2}) dVA={dvg:F2} ability={ab}");
                    }
                }
            }

            DrawDebugAnnotations(frame);

            if (!_summaryPrinted && _tests.Count > 0 && _tests.All(t => t.Complete))
            {
                _summaryPrinted = true;
                PrintSummary(frame);
            }
            return actions;
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
                    Console.WriteLine($"[SMHANDOFF] waiting for workers: {workers.Count}/11 observed at frame {frame}");
                }
                return false;
            }

            _hatchery = hatchery;
            _sideTag = hatchery.X > 100f ? "SE" : "NW";
            _ideals = _sideTag == "SE" ? SeIdeals : NwIdeals;

            // Live hatchery unit tag (the Smart/return target for the book).
            var hatchUnit = snapshot.SelfUnits.Values
                .Where(u => u != null && (u.UnitType == (uint)UnitTypes.ZERG_HATCHERY
                    || u.UnitType == (uint)UnitTypes.ZERG_LAIR
                    || u.UnitType == (uint)UnitTypes.ZERG_HIVE))
                .OrderBy(u => Math.Abs(u.Position.X - hatchery.X) + Math.Abs(u.Position.Y - hatchery.Y))
                .FirstOrDefault();
            _hatcheryTag = hatchUnit?.UnitTag ?? 0;

            var subset = (Settings.SmHandoffMinerals ?? string.Empty)
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var labels = subset.Count > 0 ? HandoffLabels.Where(l => subset.Contains(l)).ToArray() : HandoffLabels;

            var liveMinerals = snapshot.Minerals?.Values?
                .Where(m => m?.Position != null)
                .ToList() ?? new List<MineralDto>();

            foreach (var label in labels)
            {
                if (!_ideals.TryGetValue(label, out var ideal))
                {
                    Console.WriteLine($"[SMHANDOFF] WARN — no ideal entry for {label} on {_sideTag}; skipping");
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
                    Console.WriteLine($"[SMHANDOFF] WARN — persisted mineral {label} not found for start {startIndex}; skipping");
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
                    Console.WriteLine($"[SMHANDOFF] WARN — no live mineral near persisted center for {label}; skipping");
                    continue;
                }

                if (_tests.Count * 2 + 1 >= workers.Count)
                {
                    Console.WriteLine($"[SMHANDOFF] WARN — not enough workers for another pair; skipping {label}");
                    continue;
                }

                // NEAR-POINT placement (owner 2026-10-30 19:39, annotated
                // screenshot decoded 2026-10-30: workers mine from the GREEN
                // circles instead of the YELLOW ones). Screenshot circles
                // (calibrated against the white mineral pads, ±0.1u):
                //   yellow = 0.61-0.76u from center ON the hatchery line
                //   green  = 0.77-0.87u rotated 20-40° off the hatchery line
                // Miners must stand at the yellow point: the mineral's near
                // edge on the hatchery line — closest to the hatchery, out of
                // every through-lane (mining from green narrows all corridors
                // and lengthens the walk). Measured book stands before this
                // fix sat at the 1.4-1.5u sweep Ideal — even farther out.
                //   NP = center + NearPointRadius @ baseAngleRad (pure hatchery
                //     line, no best-angle offset).
                //   BSpot = staging ring 2.17u out on the hatchery line
                //     (owner: "straight on... a little farther away").
                var baseAngleRad = Math.Atan2(hatchery.Y - ideal.CenterY, hatchery.X - ideal.CenterX);
                var hDx = _hatchery.X - ideal.IdealX;
                var hDy = _hatchery.Y - ideal.IdealY;
                var hLen = Math.Sqrt(hDx * hDx + hDy * hDy);
                var apRad = baseAngleRad;   // near-point: pure hatchery line
                var apX = ideal.CenterX + NearPointRadius * Math.Cos(apRad);
                var apY = ideal.CenterY + NearPointRadius * Math.Sin(apRad);
                var spotRadius = ApproachRadius + 0.30;
                double spotX = ideal.CenterX + spotRadius * Math.Cos(apRad);
                double spotY = ideal.CenterY + spotRadius * Math.Sin(apRad);
                var spotAngleDeg = 0.0;

                // Owner placement rule 2026-10-30 ("BA Staging should not be
                // crowding either YB workers or BB workers"): BA's hatchery-line
                // spot sits inside YB's inbound corridor — measured YB-B pinned
                // ~150f in the BA/YA funnel (run 202610301408+, f12675-12825,
                // wandering a 0.7u box around BA's parked stager). Shift BA's
                // spot 0.5u perpendicular TOWARD BB (sign derived from the
                // BB-Ideal direction so it mirrors correctly NW). Result SE:
                // (132.40,23.15) -> (132.27,22.67); ~1.0u clear of YB's lane,
                // ~1.3u clear of BB's pair, 0.56u from BA's own stand (~4f
                // walk-in, well inside the release budget).
                if (string.Equals(label, "BA", StringComparison.OrdinalIgnoreCase)
                    && _ideals.TryGetValue("BB", out var bbIdeal))
                {
                    var toBbX = bbIdeal.IdealX - ideal.IdealX;
                    var toBbY = bbIdeal.IdealY - ideal.IdealY;
                    var perpX = -hDy / hLen;   // hatchery dir rotated +90°
                    var perpY = hDx / hLen;
                    if (perpX * toBbX + perpY * toBbY < 0) { perpX = -perpX; perpY = -perpY; }
                    spotX += perpX * 0.5;
                    spotY += perpY * 0.5;
                }

                var test = new HandoffTest
                {
                    Label = label,
                    MineralTag = live.UnitTag,
                    Ideal = ideal,
                    BaseAngleRad = baseAngleRad,
                    TagA = workers[_tests.Count * 2].UnitTag,
                    TagB = workers[_tests.Count * 2 + 1].UnitTag,
                    StartFrame = frame,
                    APX = (float)apX,
                    APY = (float)apY,
                    // Shuffle point 2 (owner 17:37 "stage workers on the angle of
                // the points"): the far slot sits ON the approach corridor,
                // 0.4u behind point 1 (which is BSpot at ApproachRadius+0.30).
                P2X = (float)(ideal.CenterX + (ApproachRadius + 0.70) * Math.Cos(apRad)),
                P2Y = (float)(ideal.CenterY + (ApproachRadius + 0.70) * Math.Sin(apRad)),
                BSpotX = (float)spotX,
                    BSpotY = (float)spotY,
                    BSpotAngleDeg = spotAngleDeg,
                };
                _tests.Add(test);
            }

            // Owner 2026-10-30 17:37 — the shuffle test moves to SB FIRST ("Test
            // this on SB First. stage workers on the angle of the points"):
            //   extras[0] -> SB's shuffle worker three (W3), slotted into the
            //                point-1/point-2 conveyor on the approach corridor.
            //   extras[1] -> vespine walk: MOVE to the VA geyser (Smart/3666 on
            //                a BARE geyser were both live-tested and redirect
            //                to minerals; gathers wait for the extractor).
            var extras = workers.Skip(_tests.Count * 2).ToList();
            var sbTest = _tests.FirstOrDefault(t => string.Equals(t.Label, "SB", StringComparison.OrdinalIgnoreCase));
            if (extras.Count > 0 && sbTest != null)
            {
                sbTest.TagC = extras[0].UnitTag;
                Console.WriteLine($"[SMHANDOFF] SB W3 assigned tag={extras[0].UnitTag} — shuffle point-2 conveyor, P1=({sbTest.BSpotX:F2},{sbTest.BSpotY:F2}) P2=({sbTest.P2X:F2},{sbTest.P2Y:F2})");
                extras.RemoveAt(0);
            }
            if (extras.Count > 0)
            {
                var persistedVa = mapData.OrderedMainVespene?.ElementAtOrDefault(startIndex)?
                    .FirstOrDefault(v => v != null
                        && string.Equals(v.Label, "VA", StringComparison.OrdinalIgnoreCase)
                        && v.Position != null);
                ulong vaTag = 0; double vaX = 0, vaY = 0;
                if (persistedVa?.Position != null && snapshot.Vespene != null)
                {
                    foreach (var kv in snapshot.Vespene)
                    {
                        var g = kv.Value;
                        if (g?.Position != null
                            && Math.Abs(g.Position.X - persistedVa.Position.X) < 1.0
                            && Math.Abs(g.Position.Y - persistedVa.Position.Y) < 1.0)
                        {
                            vaTag = kv.Key; vaX = g.Position.X; vaY = g.Position.Y;
                            break;
                        }
                    }
                }
                if (vaTag != 0)
                {
                    _vespineWorkerTag = extras[0].UnitTag;
                    _vespineGeyserTag = vaTag;
                    _vespineGeyserX = vaX;
                    _vespineGeyserY = vaY;
                    _pendingSmarts.Add((extras[0].UnitTag, vaTag));
                    Console.WriteLine($"[SMHANDOFF] vespine-walk test: worker {extras[0].UnitTag} -> MOVE to VA geyser tag={vaTag} at ({vaX:F2},{vaY:F2})");
                    extras.RemoveAt(0);
                }
                else
                {
                    Console.WriteLine($"[SMHANDOFF] WARN — VA geyser not resolvable live; worker {extras[0].UnitTag} falls through to parking");
                }
            }

            var com = mapData.MineralCenterOfMass?.ElementAtOrDefault(startIndex);
            foreach (var w in extras)
            {
                _parkedTags.Add(w.UnitTag);
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
                    Console.WriteLine($"[SMHANDOFF] parked {_parkedTags.Count} worker(s) at ({px:F2},{py:F2})");
                }
            }

            _initialized = true;
            // STOP every test worker at frame 0 to clear the engine's spawn
            // auto-gather (same hard-won rule as SmStageService: the STOPs must
            // land WITHOUT any same-frame MOVE to the same units). First MOVEs
            // (A to its approach point, B to its staging spot) land next frame.
            foreach (var test in _tests)
            {
                var stopA = CreateStop(test.TagA);
                if (stopA != null) actions.Add(stopA);
                var stopB = CreateStop(test.TagB);
                if (stopB != null) actions.Add(stopB);
            }
            Console.WriteLine($"[SMHANDOFF] initialized frame={frame} side={_sideTag} startIndex={startIndex} stageR={Settings.SmHandoffRadius:F2} bearing={(Settings.SmHandoffBearing > 0 ? $"{Settings.SmHandoffBearing:F0}deg-from-corridor" : "0=hatchery-line")} release=arrival-targeted lead={ArriveLeadFrames}f+walkIn minerals={string.Join(",", _tests.Select(t => $"{t.Label}(tag={t.MineralTag},A={t.TagA},B={t.TagB},best={t.Ideal.BestAngleDeg:F1},spot={t.BSpotAngleDeg:F1})"))} hatchery=({hatchery.X:F2},{hatchery.Y:F2}) — engine auto-gather STOPped; first MOVEs next frame");
            return true;
        }

        // ── per-test state machine ──

        private void Tick(HandoffTest test, int frame, List<SC2Action> actions)
        {
            TickA(test, frame, actions);
            TickB(test, frame, actions);
        }

        private void TickA(HandoffTest test, int frame, List<SC2Action> actions)
        {
            var live = LiveWorker(test.TagA);
            if (live == null) return;

            switch (test.APhase)
            {
                case APhase.ToApproach:
                    if (test.APhaseStart == 0)
                    {
                        // First MOVE after the init STOP: A heads to the near
                        // point (0.85u on the hatchery line), then Smart fires
                        // on arrival while stationary (owner 19:39 yellow point).
                        var rad = test.BaseAngleRad;
                        test.ATargetX = (float)(test.Ideal.CenterX + NearPointRadius * Math.Cos(rad));
                        test.ATargetY = (float)(test.Ideal.CenterY + NearPointRadius * Math.Sin(rad));
                        test.APhaseStart = frame;
                        var move = CreateMove(test.TagA, test.ATargetX, test.ATargetY);
                        if (move != null) actions.Add(move);
                        Log(test, test.TagA, "A", "move", frame, live, -1, test.AHarvestFrames, $"approach ang={test.Ideal.BestAngleDeg:F1} -> ({test.ATargetX:F2},{test.ATargetY:F2})", actions);
                        break;
                    }
                    if (Dist(live, test.ATargetX, test.ATargetY) < ArriveEpsilon
                        || (frame - test.APhaseStart > 6
                            && Speed(test.TagA, live) < 0.02f
                            && Dist(live, test.ATargetX, test.ATargetY) < 0.5f))
                    {
                        var smart = CreateSmart(test.TagA, test.MineralTag);
                        if (smart != null) actions.Add(smart);
                        test.APhase = APhase.Smarting;
                        test.APhaseStart = frame;
                    }
                    else if (frame - test.APhaseStart > 30 && Dist(live, test.ATargetX, test.ATargetY) < 0.9f)
                    {
                        // Stalled near the approach point (e.g., B's staging hold
                        // sits on the walk-in corridor) — Smart from here instead
                        // of never arriving. A body parked on the target blocks
                        // at ~0.6-0.75u (two worker radii), hence 0.9. Smart
                        // from <0.9u still lands the stand (production walk-in).
                        var smart = CreateSmart(test.TagA, test.MineralTag);
                        if (smart != null) actions.Add(smart);
                        test.APhase = APhase.Smarting;
                        test.APhaseStart = frame;
                        Log(test, test.TagA, "A", "smart", frame, live, DistToIdeal(test, live), 0,
                            $"APPROACH-STALLED {Dist(live, test.ATargetX, test.ATargetY):F2}u short — Smart from here", actions);
                    }
                    else if (frame > test.APhaseStart + 6 && frame % 25 == 0)
                    {
                        // The engine SILENTLY DROPS a MOVE whose unit stops making
                        // progress (head-on block) — the worker then idles forever
                        // with no order. Re-issue the MOVE to force a re-path.
                        var move = CreateMove(test.TagA, test.ATargetX, test.ATargetY);
                        if (move != null) actions.Add(move);
                        Log(test, test.TagA, "A", "move", frame, live, DistToIdeal(test, live), 0,
                            $"MOVE re-issued ({Dist(live, test.ATargetX, test.ATargetY):F2}u short — earlier MOVE dropped)", actions);
                    }
                    break;

                case APhase.Smarting:
                    if (Dist(live, (float)test.Ideal.CenterX, (float)test.Ideal.CenterY) < CommitRadius
                        && Speed(test.TagA, live) < StillEpsilon)
                    {
                        test.AStillFrames++;
                        if (test.AStillFrames >= TipStillFrames)
                        {
                            test.AStandRecorded = true;
                            test.AStandFrame = frame;
                            test.AStandX = live.Position.X;
                            test.AStandY = live.Position.Y;
                            test.APhase = APhase.Harvesting;
                            test.APhaseStart = frame;
                            var dIdeal = Math.Sqrt(
                                (live.Position.X - test.Ideal.IdealX) * (live.Position.X - test.Ideal.IdealX)
                                + (live.Position.Y - test.Ideal.IdealY) * (live.Position.Y - test.Ideal.IdealY));
                            Log(test, test.TagA, "A", "stand", frame, live, dIdeal, 0, $"stand recorded", actions);
                        }
                    }
                    else
                    {
                        test.AStillFrames = 0;
                    }
                    break;

                case APhase.Harvesting:
                    {
                        var gathering = IsGathering(test.TagA);
                        var stationary = Speed(test.TagA, live) < HarvestStillEpsilon;
                        if (gathering && stationary)
                        {
                            test.AHarvestFrames++;
                            test.ACurrentGap = 0;
                            if (test.AFirstHarvestFrame < 0)
                            {
                                test.AFirstHarvestFrame = frame;
                                Log(test, test.TagA, "A", "harvest", frame, live, -1, test.AHarvestFrames, "first harvest frame", actions);
                            }
                            test.ALastHarvestFrame = frame;
                        }
                        else if (test.AHarvestFrames > 0 && test.AReturnFrame < 0)
                        {
                            test.ACurrentGap++;
                            if (test.ACurrentGap > test.AMaxGap) test.AMaxGap = test.ACurrentGap;
                        }

                        // The 32-harvest-frame release: B gets its Smart exactly here.
                        if (test.AHarvestFrames >= HandoffHarvestFrames && !test.ASmartIssued)
                        {
                            test.ASmartIssued = true;
                            if (test.BPhase == BPhase.Holding)
                            {
                                IssueBSmart(test, frame, actions);
                            }
                            // else: B is not holding yet (slow walk) — issued when its hold starts.
                        }

                        if (IsReturning(test.TagA))
                        {
                            test.AReturnFrame = frame;
                            test.APhase = APhase.RoundOver;
                            Log(test, test.TagA, "A", "flip-return", frame, live, -1, test.AHarvestFrames,
                                $"1183->1184 round over lastHarvest=f{test.ALastHarvestFrame} maxGap={test.AMaxGap} maxDrift={test.AMaxDrift:F3}", actions);
                        }

                        // Interference: A displaced from its recorded stand.
                        if (test.AStandRecorded)
                        {
                            var drift = Dist(live, test.AStandX, test.AStandY);
                            if (drift > test.AMaxDrift) test.AMaxDrift = drift;
                        }
                    }
                    break;

                case APhase.RoundOver:
                    // Keep watching A's drift until B's first harvest — but only
                    // while A is still AT the stand (drift < 0.6); after A departs
                    // for the hatchery the walk home must not count as drift.
                    if (test.AStandRecorded && test.BFirstHarvestFrame < 0)
                    {
                        var drift = Dist(live, test.AStandX, test.AStandY);
                        if (drift < 0.6f && drift > test.AMaxDrift) test.AMaxDrift = drift;
                    }
                    break;
            }
        }

        private void TickB(HandoffTest test, int frame, List<SC2Action> actions)
        {
            var live = LiveWorker(test.TagB);
            if (live == null) return;

            switch (test.BPhase)
            {
                case BPhase.ToStage:
                    if (test.BPhaseStart == 0)
                    {
                        test.BPhaseStart = frame;
                        var move = CreateMove(test.TagB, test.BSpotX, test.BSpotY);
                        if (move != null) actions.Add(move);
                        Log(test, test.TagB, "B", "move", frame, live, -1, 0, $"to staging spot ({test.BSpotX:F2},{test.BSpotY:F2})", actions);
                        break;
                    }
                    if (Dist(live, test.BSpotX, test.BSpotY) < ArriveEpsilon
                        || (frame - test.BPhaseStart > 6
                            && Speed(test.TagB, live) < 0.02f
                            && Dist(live, test.BSpotX, test.BSpotY) < 0.5f)
                        || (frame - test.BPhaseStart > 30 && Dist(live, test.BSpotX, test.BSpotY) < 0.9f))
                    {
                        var stop = CreateStop(test.TagB);
                        if (stop != null) actions.Add(stop);
                        test.BPhase = BPhase.Holding;
                        test.BPhaseStart = frame;
                        test.BHoldStarted = true;
                        test.BHoldStartFrame = frame;
                        Log(test, test.TagB, "B", "hold", frame, live, -1, 0, "staging hold started", actions);
                        // A may already have hit 32 harvest frames while B walked.
                        if (test.ASmartIssued && test.BSmartFrame < 0)
                        {
                            IssueBSmart(test, frame, actions);
                        }
                    }
                    else if (frame > test.BPhaseStart + 6 && frame % 25 == 0)
                    {
                        // MOVE silently dropped by a head-on block (see A's case) —
                        // re-issue to force a re-path instead of idling forever.
                        var move = CreateMove(test.TagB, test.BSpotX, test.BSpotY);
                        if (move != null) actions.Add(move);
                        Log(test, test.TagB, "B", "move", frame, live, DistToIdeal(test, live), 0,
                            $"MOVE re-issued ({Dist(live, test.BSpotX, test.BSpotY):F2}u short — earlier MOVE dropped)", actions);
                    }
                    else if (frame > 25 && frame % 50 == 0)
                    {
                        // Stuck-walker diagnostics: B has not reached its spot.
                        Log(test, test.TagB, "B", "move", frame, live, DistToIdeal(test, live), 0,
                            $"still walking to staging spot ({Dist(live, test.BSpotX, test.BSpotY):F2}u short)", actions);
                    }
                    break;

                case BPhase.Holding:
                    {
                        var drift = Dist(live, test.BSpotX, test.BSpotY);
                        if (drift > test.BMaxDriftHold) test.BMaxDriftHold = drift;
                        if (drift > WanderDist) test.BWandered = true;
                    }
                    break;

                case BPhase.Smarting:
                    if (Dist(live, (float)test.Ideal.CenterX, (float)test.Ideal.CenterY) < CommitRadius
                        && Speed(test.TagB, live) < StillEpsilon)
                    {
                        test.BStillFrames++;
                        if (test.BStillFrames >= TipStillFrames)
                        {
                            test.BStandFrame = frame;
                            var dIdeal = Math.Sqrt(
                                (live.Position.X - test.Ideal.IdealX) * (live.Position.X - test.Ideal.IdealX)
                                + (live.Position.Y - test.Ideal.IdealY) * (live.Position.Y - test.Ideal.IdealY));
                            test.BStandDIdeal = dIdeal;
                            test.BPhase = BPhase.Waiting;
                            test.BPhaseStart = frame;
                            test.BStillFrames = 0;
                            Log(test, test.TagB, "B", "stand", frame, live, dIdeal, 0,
                                $"stand at mineral walkIn=f{frame - test.BSmartFrame}", actions);
                        }
                    }
                    else
                    {
                        test.BStillFrames = 0;
                        if (frame - test.BPhaseStart > BSmartTimeoutFrames)
                        {
                            Log(test, test.TagB, "B", "stand", frame, live, DistToIdeal(test, live), 0, "SMART-TIMEOUT — never stood at the mineral", actions);
                            test.BPhase = BPhase.Waiting; // keep watching for a late harvest anyway
                            test.BPhaseStart = frame;
                            test.BStandFrame = -2; // timeout marker
                        }
                    }
                    break;

                case BPhase.Waiting:
                    {
                        // Bounced away from the mineral? (busy-mineral bounce to another mineral)
                        if (Dist(live, (float)test.Ideal.CenterX, (float)test.Ideal.CenterY) > 2.5f)
                        {
                            Log(test, test.TagB, "B", "stand", frame, live, DistToIdeal(test, live), 0, "BOUNCED — left the mineral footprint", actions);
                            test.BPhase = BPhase.Holding; // stop tracking; metrics will show the failure
                            test.BWandered = true;
                            break;
                        }

                        var gathering = IsGathering(test.TagB);
                        var stationary = Speed(test.TagB, live) < HarvestStillEpsilon;
                        if (gathering && stationary)
                        {
                            if (test.AReturnFrame < 0)
                            {
                                // Waiter settle: holding 1183 while A is still the active harvester.
                                if (test.BWaiterFrame < 0)
                                {
                                    test.BWaiterFrame = frame;
                                    Log(test, test.TagB, "B", "hold", frame, live, DistToIdeal(test, live), 0, "waiter settled with 1183 while A active", actions);
                                }
                            }
                            else if (test.BFirstHarvestFrame < 0)
                            {
                                test.BFirstHarvestFrame = frame;
                                test.BPhase = BPhase.Harvesting;
                                test.BPhaseStart = frame;
                                Log(test, test.TagB, "B", "harvest", frame, live, DistToIdeal(test, live), 0,
                                    $"PROMOTED first harvest after A flip; idleGap=f{frame - test.ALastHarvestFrame}", actions);
                            }
                        }

                        if (test.BFirstHarvestFrame < 0 && frame - test.BPhaseStart > BWaitTimeoutFrames)
                        {
                            Log(test, test.TagB, "B", "harvest", frame, live, DistToIdeal(test, live), 0, "WAIT-TIMEOUT — never harvested after standing", actions);
                            test.Complete = true;
                            LogHandoff(test, frame, actions, "wait-timeout");
                        }
                    }
                    break;

                case BPhase.Harvesting:
                    {
                        var gathering = IsGathering(test.TagB);
                        var stationary = Speed(test.TagB, live) < HarvestStillEpsilon;
                        if (gathering && stationary)
                        {
                            test.BHarvestFrames++;
                        }
                        if (test.BHarvestFrames >= 3)
                        {
                            test.Complete = true;
                            LogHandoff(test, frame, actions, "complete");
                        }
                        else if (frame - test.BPhaseStart > BWaitTimeoutFrames)
                        {
                            test.Complete = true;
                            LogHandoff(test, frame, actions, "harvest-watch-timeout");
                        }
                    }
                    break;
            }
        }

        private void IssueBSmart(HandoffTest test, int frame, List<SC2Action> actions)
        {
            var smart = CreateSmart(test.TagB, test.MineralTag);
            if (smart != null) actions.Add(smart);
            test.BSmartFrame = frame;
            test.AHFAtSmart = test.AHarvestFrames;
            test.BPhase = BPhase.Smarting;
            test.BPhaseStart = frame;
            test.BStillFrames = 0;
            var live = LiveWorker(test.TagB);
            Log(test, test.TagB, "B", "smart", frame, live, -1, 0,
                $"SMART at A harvestFrames={test.AHarvestFrames} (aFirst=f{test.AFirstHarvestFrame})", actions);
        }

        // ── on-screen annotations (step-through aid, re-issued every frame) ──

        private static readonly Dictionary<string, Color> MineralColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["SB"] = new Color { R = 0, G = 255, B = 255 },   // cyan
            ["BB"] = new Color { R = 90, G = 140, B = 255 },  // blue
            ["BA"] = new Color { R = 0, G = 200, B = 100 },   // green
            ["YB"] = new Color { R = 255, G = 220, B = 0 },   // yellow
            ["YA"] = new Color { R = 255, G = 140, B = 0 },   // orange
        };

        private static Color White() => new Color { R = 255, G = 255, B = 255 };

        private static Point P(double x, double y, float z) => new Point { X = (float)x, Y = (float)y, Z = z };

        private void DrawDebugAnnotations(int frame)
        {
            if (!ManagerDebugService.IsDebugEnabled || !_initialized) return;

            foreach (var test in _tests)
            {
                if (!MineralColors.TryGetValue(test.Label, out var color)) color = White();
                var center = test.Ideal;
                var minPt = P(center.CenterX, center.CenterY, 8);

                // B staging spot: sphere + label + line spot -> mineral center.
                var spotPt = P(test.BSpotX, test.BSpotY, 8);
                ManagerDebugService.DrawSphere(spotPt, 0.22f, color);
                ManagerDebugService.DrawText($"{test.Label}-B stage", P(test.BSpotX, test.BSpotY, 12), color, 12);
                ManagerDebugService.DrawLine(spotPt, minPt, color);

                // A approach point (only once the MOVE has been issued): crosshair + line.
                if (test.APhaseStart != 0)
                {
                    var ax = (double)test.ATargetX;
                    var ay = (double)test.ATargetY;
                    ManagerDebugService.DrawLine(P(ax - 0.18, ay, 8), P(ax + 0.18, ay, 8), color);
                    ManagerDebugService.DrawLine(P(ax, ay - 0.18, 8), P(ax, ay + 0.18, 8), color);
                    ManagerDebugService.DrawText($"{test.Label}-A appr best={test.Ideal.BestAngleDeg:F0}",
                        P(ax, ay, 12), color, 12);
                    ManagerDebugService.DrawLine(P(ax, ay, 8), minPt, color);
                }

                // Ideal stand point (the sweep-verified harvest stand).
                ManagerDebugService.DrawSphere(P(center.IdealX, center.IdealY, 8), 0.14f, White());

                // Dynamic per-worker status labels.
                var aLive = LiveWorker(test.TagA);
                if (aLive?.Position != null)
                {
                    var aText = test.APhase switch
                    {
                        APhase.ToApproach => "walk",
                        APhase.Smarting => "smart",
                        APhase.Harvesting => $"harv{test.AHarvestFrames}",
                        APhase.RoundOver => "return",
                        _ => "?",
                    };
                    ManagerDebugService.DrawText($"{test.Label}-A {aText}",
                        P(aLive.Position.X, aLive.Position.Y + 0.55, 12), White(), 11);
                }
                var bLive = LiveWorker(test.TagB);
                if (bLive?.Position != null)
                {
                    string bText;
                    if (test.BPhase == BPhase.Holding)
                    {
                        bText = (test.AReturnFrame >= 0 && test.BSmartFrame < 0) ? "GO!" : "hold";
                    }
                    else
                    {
                        bText = test.BPhase switch
                        {
                            BPhase.ToStage => "walk",
                            BPhase.Smarting => "in",
                            BPhase.Waiting => "wait",
                            BPhase.Harvesting => $"harv{test.BHarvestFrames}",
                            _ => "?",
                        };
                    }
                    ManagerDebugService.DrawText($"{test.Label}-B {bText}",
                        P(bLive.Position.X, bLive.Position.Y + 0.55, 12), color, 11);
                }
            }
        }

        // ── post-completion SPEED-MINING BOOK ──
        // Owner spec 2026-10-29 20:34 — plain engine Smart mining decelerates
        // at both ends and idles the mineral. The book runs the full-speed loop:
        //
        //   Mining ──(1183→1184 flip)──▶ ReturnMove: MOVE 2u extended PAST the
        //     measured hatchery drop point (no deceleration); when ~2 frames
        //     out, Return on the hatchery. Drop detected via buff 271/272 loss.
        //   ReturnMove ──(dropped)──▶ OutboundMove: MOVE 2u extended past the
        //     harvest stand point; when ~2 frames out, SMART the mineral —
        //     unless the partner is mid-cycle (hf<32), then stage at the spot.
        //   Staging ──(partner hf≥32)──▶ SMART the mineral.
        //
        // Safety net: if the engine ever re-targets a Mining worker to another
        // mineral (busy-mineral steal), the book re-asserts the Smart.

        private enum BookPhase { Mining, ReturnMove, OutboundMove, ToStage, Staging, CellWait }

        private sealed class BookWorker
        {
            public ulong Tag;
            public string Role = string.Empty;
            public BookPhase Phase = BookPhase.Mining;
            public int LastFlipFrame = -1;      // last 1184-start frame (cycle start)
            public int LastDropFrame = -1;
            public int LastSmartFrame = -1;
            public int LastHarvestFrame = -1;   // last stationary-1183 frame (for idleGap)
            public float RetX, RetY;            // measured hatchery drop point
            public bool HaveReturnPoint;
            public int HarvestFrames;           // current-cycle harvest-frame count
            public int FirstHarvestFrame = -1;  // first harvest frame of current cycle
            public int PartnerFlipSeen = -1;    // partner LastFlipFrame at last reset (counter integrity)
            public float LegRetX, LegRetY;      // THIS leg's return point (provisional or measured)
            public bool ReturnCmdIssued;        // return-cmd fired this leg (throttle refires)
            public int LastReturnCmdFrame = -100;
            public int OutboundStallFrames;     // consecutive frames my own speed < 0.10 in OutboundMove
            public int HoldSinceFrame = -1;     // hold start frame (-1 = not holding)
            public int Cycles;
            public int ReturnLegSum;            // flip→drop frames, summed
            public int OutboundLegSum;          // drop→smart frames, summed
            public int CycleSum;                // flip→flip frames, summed
            public int IdleGapSum, IdleGapCount;
            public int LastSmartLogFrame = -100; // throttle per-frame logs
            public int StandStillFrame = -1;     // first stationary frame after Smart (walk-in measurement)
            public bool W3Parked;                // approach 3: W3 holds point 1 permanently
            public int LastRefreshFrame = -100;  // approach 3: CellWait MOVE+queued-harvest refresh clock
        }

        private static bool IsGatherAbility(int ability) =>
            ability == (int)Abilities.HARVEST_GATHER
            || ability == (int)Abilities.HARVEST_GATHER_DRONE
            || ability == (int)Abilities.HARVEST_GATHER_PROBE
            || ability == (int)Abilities.HARVEST_GATHER_SCV;

        private static bool IsReturnAbility(int ability) =>
            ability == (int)Abilities.HARVEST_RETURN
            || ability == (int)Abilities.HARVEST_RETURN_DRONE
            || ability == (int)Abilities.HARVEST_RETURN_PROBE
            || ability == (int)Abilities.HARVEST_RETURN_SCV;

        // First-order (ability, targetUnitTag, carrying) of a worker from the raw obs.
        private (int ability, ulong target, bool carrying) OrderInfo(ulong tag)
        {
            var units = _lastObservation?.Observation?.RawData?.Units;
            if (units == null || tag == 0) return (0, 0, false);
            foreach (var u in units)
            {
                if (u == null || u.Tag != tag) continue;
                var order = u.Orders?.FirstOrDefault();
                var carrying = u.BuffIds != null && u.BuffIds.Any(b => b == 271 || b == 272);
                return ((int)(order?.AbilityId ?? 0), order?.TargetUnitTag ?? 0, carrying);
            }
            return (0, 0, false);
        }

        private void TickBook(HandoffTest test, int frame, List<SC2Action> actions)
        {
            if (test.BookA == null)
            {
                test.BookA = new BookWorker { Tag = test.TagA, Role = "A" };
                test.BookB = new BookWorker { Tag = test.TagB, Role = "B" };
                if (test.TagC != 0)
                {
                    test.BookC = new BookWorker { Tag = test.TagC, Role = "C" };
                    BookLog(test, test.BookC, frame, "W3 joined the shuffle");
                }
                Console.WriteLine($"[SMHANDOFF] {test.Label} book started frame={frame}{(test.TagC != 0 ? " — 3-WORKER SHUFFLE" : "")}");
            }

            if (test.TagC != 0)
            {
                TickApproach3(test, frame, actions);
                return;
            }

            TickBookWorker(test, test.BookA, test.BookB, frame, actions);
            TickBookWorker(test, test.BookB, test.BookA, frame, actions);
        }

        // ── Approach 3 (owner 2026-10-30 19:24, "I like 3 best", SB first) ──
        //
        // Point 1 (the corridor ring, BSpot) is W3's permanently: it parks
        // there and never mines in this test. The W1/W2 rotation runs the
        // pair book with one substitution (test.TagC != 0 gates it inside
        // TickBookWorker): after the cargo drop the rotation worker goes
        // STRAIGHT to point 0 with a QUEUED harvest, then re-checks every
        // 14 frames — if the miner still hasn't flipped 1183->1184 it gets
        // the same MOVE + queued harvest again (the refresh beats the
        // engine's 15-frame busy-mineral re-target). On arrival the queued
        // harvest fires into a wait at the cell; the miner's flip promotes
        // it the same frame, so the seam stays ~1.
        private void TickApproach3(HandoffTest test, int frame, List<SC2Action> actions)
        {
            var c = test.BookC;
            if (c != null && !c.W3Parked)
            {
                var cl = LiveWorker(c.Tag);
                if (cl?.Position != null)
                {
                    var d = Dist(cl, test.BSpotX, test.BSpotY);
                    if (d < 0.12f)
                    {
                        var stop = CreateStop(c.Tag);
                        if (stop != null) actions.Add(stop);
                        c.W3Parked = true;
                        BookLog(test, c, frame, $"W3 PARKED at point 1 ({test.BSpotX:F2},{test.BSpotY:F2}) — holding (approach3)");
                    }
                    else if (frame % 10 == 0)
                    {
                        var mv = CreateMove(c.Tag, test.BSpotX, test.BSpotY);
                        if (mv != null) actions.Add(mv);
                    }
                }
            }
            TickBookWorker(test, test.BookA, test.BookB, frame, actions);
            TickBookWorker(test, test.BookB, test.BookA, frame, actions);
        }

        // Queued harvest (approach 3): executed after the worker's current
        // MOVE to point 0 completes — the engine fires it on arrival, which
        // is what puts the waiter into 1183-wait at the cell without us
        // Smarting from a wrong angle.
        private static SC2Action CreateSmartQueued(ulong tag, ulong mineralTag)
        {
            if (tag == 0 || mineralTag == 0) return null;
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.SMART,
                TargetUnitTag = mineralTag,
                QueueCommand = true,
            };
            command.UnitTags.Add(tag);
            return new SC2Action { ActionRaw = new ActionRaw { UnitCommand = command } };
        }

        // ── 3-worker shuffle (owner 2026-10-30 17:37, SB first) ──
        //
        // Conveyor on the approach corridor ("stage workers on the angle of
        // the points"): point 0 = Ideal cell, point 1 = corridor ring 2.17u
        // (BSpot), point 2 = corridor ring 2.57u (P2, 0.4u behind point 1).
        //   miner (hf >= releaseAt) -> near holder Smart (owner step 2)
        //   far holder advances point 2 -> point 1 (step 2)
        //   returner arrives -> takes the first free slot outward-in
        // The W3 push on the returner (steps 4-5) is deliberately left out —
        // owner: "first we see what works and what does not".
        private void TickShuffle(HandoffTest test, int frame, List<SC2Action> actions)
        {
            var ws = new[] { test.BookA, test.BookB, test.BookC };

            // Per-worker bookkeeping identical to the pair book: harvest-frame
            // counting, flip -> passive return, engine re-assert. The "partner"
            // for everyone is the CURRENT MINER = the worker whose harvest
            // count leads (the cell-waiter's <=14 polluted frames never lead).
            var miner = ws.OrderByDescending(w => w.HarvestFrames).First();
            foreach (var w in ws)
            {
                var live = LiveWorker(w.Tag);
                if (live?.Position == null) continue;
                var px = live.Position.X;
                var py = live.Position.Y;
                // Point 0 = the owner's near point (19:39 yellow): 0.85u out on
                // the hatchery line, NOT the 1.4-1.5u sweep Ideal.
                var standX = (float)(test.Ideal.CenterX + NearPointRadius * Math.Cos(test.BaseAngleRad));
                var standY = (float)(test.Ideal.CenterY + NearPointRadius * Math.Sin(test.BaseAngleRad));
                var (ability, target, carrying) = OrderInfo(w.Tag);
                var gathering = IsGatherAbility(ability) && target == test.MineralTag;

                if (miner.LastFlipFrame >= 0 && miner.LastFlipFrame != w.PartnerFlipSeen)
                {
                    w.PartnerFlipSeen = miner.LastFlipFrame;
                    w.HarvestFrames = 0;
                    w.FirstHarvestFrame = -1;
                }

                if (gathering && Speed(w.Tag, live) < HarvestStillEpsilon
                    && Dist(live, (float)test.Ideal.CenterX, (float)test.Ideal.CenterY) < CommitRadius)
                {
                    w.HarvestFrames++;
                    if (w.FirstHarvestFrame < 0)
                    {
                        w.FirstHarvestFrame = frame;
                        if (miner.LastHarvestFrame >= 0 && w.Cycles > 0)
                        {
                            var gap = frame - miner.LastHarvestFrame;
                            w.IdleGapSum += gap;
                            w.IdleGapCount++;
                            BookLog(test, w, frame, $"cycle {w.Cycles + 1} first harvest — idleGap={gap} (miner last harvest f{miner.LastHarvestFrame})");
                        }
                    }
                    w.LastHarvestFrame = frame;
                }

                var releaseAtHf = Math.Max(1, (int)Math.Ceiling(test.ExpectedHarvestTotal - ArriveLeadFrames - test.WalkInEstimate));
                var mySlotX = test.NearTag == w.Tag ? test.BSpotX : test.P2X;
                var mySlotY = test.NearTag == w.Tag ? test.BSpotY : test.P2Y;

                switch (w.Phase)
                {
                    case BookPhase.Mining:
                        // Walk-in measurement (same convention as the pair book).
                        if (w.StandStillFrame < 0 && w.LastSmartFrame >= 0 && frame > w.LastSmartFrame
                            && Speed(w.Tag, live) < 0.02f && Dist(live, standX, standY) < 0.6f)
                        {
                            w.StandStillFrame = frame;
                            var wi = frame - w.LastSmartFrame;
                            if (wi >= 1 && wi <= 40) test.WalkInEstimate = (test.WalkInEstimate * 3 + wi) / 4.0;
                        }
                        if (IsReturnAbility(ability) || (carrying && !gathering))
                        {
                            if (w.LastFlipFrame >= 0 && w.FirstHarvestFrame >= 0)
                            {
                                var cycle = frame - w.LastFlipFrame;
                                w.CycleSum += cycle;
                                BookLog(test, w, frame, $"cycle {w.Cycles} done: cycle={cycle} harvest={w.HarvestFrames} avgCycle={(w.Cycles > 0 ? (w.CycleSum / (double)w.Cycles).ToString("F1") : "?")} (shuffle)");
                            }
                            w.Cycles++;
                            var dh0 = w.HarvestFrames;
                            w.HarvestFrames = 0;
                            w.FirstHarvestFrame = -1;
                            w.LastFlipFrame = frame;
                            w.StandStillFrame = -1;
                            test.ExpectedHarvestTotal = Math.Max(test.ExpectedHarvestTotal, dh0);
                            w.Phase = BookPhase.ReturnMove;
                            w.ReturnCmdIssued = true;
                            BookLog(test, w, frame, "flip — passive return (shuffle)");
                        }
                        else if (!gathering && ability != (int)Abilities.MOVE && ability != 0)
                        {
                            var smart = CreateSmart(w.Tag, test.MineralTag);
                            if (smart != null) actions.Add(smart);
                            test.BookReasserts++;
                            BookLog(test, w, frame, $"BOOK re-assert #{test.BookReasserts} (shuffle, was ability={ability} tgt={target})");
                        }
                        break;

                    case BookPhase.ReturnMove:
                        if (!carrying)
                        {
                            w.RetX = px; w.RetY = py;
                            w.HaveReturnPoint = true;
                            w.LastDropFrame = frame;
                            var ddx = test.APX - px;
                            var ddy = test.APY - py;
                            var dlen = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                            var mv2 = CreateMove(w.Tag, test.APX + ddx / dlen * 2.0f, test.APY + ddy / dlen * 2.0f);
                            if (mv2 != null) actions.Add(mv2);
                            w.Phase = BookPhase.OutboundMove;
                            w.OutboundStallFrames = 0;
                            BookLog(test, w, frame, $"dropped ({px:F2},{py:F2}) after {frame - w.LastFlipFrame}f — outbound to corridor (shuffle)");
                        }
                        else
                        {
                            var dh = (float)Math.Sqrt((px - _hatchery.X) * (px - _hatchery.X) + (py - _hatchery.Y) * (py - _hatchery.Y));
                            var returning = IsReturnAbility(ability) || ability == (int)Abilities.MOVE || ability == 0;
                            if (carrying && !returning && frame - w.LastReturnCmdFrame > 10)
                            {
                                w.LastReturnCmdFrame = frame;
                                var ret = CreateReturn(w.Tag, _hatcheryTag);
                                if (ret != null) actions.Add(ret);
                                BookLog(test, w, frame, $"return re-assert (ability={ability} dHatch={dh:F2})");
                            }
                            else if (carrying && Speed(w.Tag, live) < 0.01f && dh > 4.6f
                                && frame - w.LastFlipFrame > 12 && frame - w.LastReturnCmdFrame > 10)
                            {
                                w.LastReturnCmdFrame = frame;
                                var ret2 = CreateReturn(w.Tag, _hatcheryTag);
                                if (ret2 != null) actions.Add(ret2);
                                BookLog(test, w, frame, $"return STALL recovery (ability={ability} dHatch={dh:F2})");
                            }
                        }
                        break;

                    case BookPhase.OutboundMove:
                        {
                            var dap = (float)Math.Sqrt((px - test.APX) * (px - test.APX) + (py - test.APY) * (py - test.APY));
                            var ds = (float)Math.Sqrt((px - standX) * (px - standX) + (py - standY) * (py - standY));
                            if (dap < 0.30f)
                            {
                                if (miner != w && miner.HarvestFrames > 0 && miner.HarvestFrames < releaseAtHf)
                                {
                                    // Corridor reached mid-cycle: claim a slot.
                                    // Near first if free (shorter next walk), else far.
                                    if (test.NearTag == 0 || test.NearTag == w.Tag)
                                    {
                                        test.NearTag = w.Tag;
                                        var mv3 = CreateMove(w.Tag, test.BSpotX, test.BSpotY);
                                        if (mv3 != null) actions.Add(mv3);
                                        w.Phase = BookPhase.ToStage;
                                        BookLog(test, w, frame, $"claim NEAR slot P1 ({test.BSpotX:F2},{test.BSpotY:F2}) — miner hf={miner.HarvestFrames} (shuffle)");
                                    }
                                    else
                                    {
                                        test.FarTag = w.Tag;
                                        var mv3 = CreateMove(w.Tag, test.P2X, test.P2Y);
                                        if (mv3 != null) actions.Add(mv3);
                                        w.Phase = BookPhase.ToStage;
                                        BookLog(test, w, frame, $"claim FAR slot P2 ({test.P2X:F2},{test.P2Y:F2}) — near held by {test.NearTag} (shuffle)");
                                    }
                                }
                                else
                                {
                                    var smart = CreateSmart(w.Tag, test.MineralTag);
                                    if (smart != null) actions.Add(smart);
                                    w.LastSmartFrame = frame;
                                    w.Phase = BookPhase.Mining;
                                    BookLog(test, w, frame, $"smart at corridor ({dap:F2}u from AP, miner hf={(miner == w ? "me" : miner.HarvestFrames.ToString())}) — shuffle");
                                }
                            }
                            else
                            {
                                var mySpeed = Speed(w.Tag, live);
                                if (mySpeed < 0.10f) w.OutboundStallFrames++;
                                else w.OutboundStallFrames = 0;
                                // Engine-placement recovery (orbit), same as pair book.
                                if (w.OutboundStallFrames > 10 && ds < 1.2f)
                                {
                                    var smart2 = CreateSmart(w.Tag, test.MineralTag);
                                    if (smart2 != null) actions.Add(smart2);
                                    w.LastSmartFrame = frame;
                                    w.Phase = BookPhase.Mining;
                                    BookLog(test, w, frame, $"smart from engine placement ({ds:F2}u from stand, stall {w.OutboundStallFrames}f) — shuffle");
                                    break;
                                }
                                var ddx2 = test.APX - px;
                                var ddy2 = test.APY - py;
                                var dlen2 = (float)Math.Sqrt(ddx2 * ddx2 + ddy2 * ddy2);
                                var mv5 = CreateMove(w.Tag, test.APX + ddx2 / dlen2 * 2.0f, test.APY + ddy2 / dlen2 * 2.0f);
                                if (mv5 != null) actions.Add(mv5);
                            }
                        }
                        break;

                    case BookPhase.ToStage:
                        {
                            var dst = (float)Math.Sqrt((px - mySlotX) * (px - mySlotX) + (py - mySlotY) * (py - mySlotY));
                            if (dst < 0.12f)
                            {
                                var stop = CreateStop(w.Tag);
                                if (stop != null) actions.Add(stop);
                                w.Phase = BookPhase.Staging;
                                BookLog(test, w, frame, $"holding {(test.NearTag == w.Tag ? "NEAR" : "FAR")} slot (shuffle)");
                            }
                            else if (miner.HarvestFrames >= releaseAtHf || miner.Phase != BookPhase.Mining)
                            {
                                // Release for the NEAR holder only; far holder waits
                                // for the advance (or its own release next round).
                                if (test.NearTag == w.Tag)
                                {
                                    ReleaseNear(test, w, frame, actions, dst);
                                }
                            }
                            else if (ability == 0 && frame - w.LastSmartLogFrame > 25)
                            {
                                var mv5 = CreateMove(w.Tag, mySlotX, mySlotY);
                                if (mv5 != null) actions.Add(mv5);
                                w.LastSmartLogFrame = frame;
                                BookLog(test, w, frame, $"to-slot MOVE re-issued ({dst:F2}u short) (shuffle)");
                            }
                        }
                        break;

                    case BookPhase.Staging:
                        if (miner.HarvestFrames >= releaseAtHf || miner.Phase != BookPhase.Mining)
                        {
                            if (test.NearTag == w.Tag)
                            {
                                ReleaseNear(test, w, frame, actions, -1f);
                            }
                        }
                        break;
                }
            }
        }

        // Near-holder release (owner step 2): Smart into the mineral, vacate
        // the near slot, and advance the far holder from point 2 to point 1.
        private void ReleaseNear(HandoffTest test, BookWorker w, int frame, List<SC2Action> actions, float dst)
        {
            var smart = CreateSmart(w.Tag, test.MineralTag);
            if (smart != null) actions.Add(smart);
            w.LastSmartFrame = frame;
            w.Phase = BookPhase.Mining;
            test.NearTag = 0;
            BookLog(test, w, frame, $"GO — near slot released (smart from {(dst >= 0 ? $"{dst:F2}u short of slot" : "hold")}) (shuffle)");
            if (test.FarTag != 0 && test.FarTag != w.Tag)
            {
                var far = test.FarTag == test.BookA?.Tag ? test.BookA
                    : test.FarTag == test.BookB?.Tag ? test.BookB : test.BookC;
                if (far != null)
                {
                    var mv = CreateMove(far.Tag, test.BSpotX, test.BSpotY);
                    if (mv != null) actions.Add(mv);
                    far.Phase = BookPhase.ToStage;
                    test.FarTag = 0;
                    test.NearTag = far.Tag;
                    BookLog(test, far, frame, $"advanced FAR -> NEAR (P2 -> P1) (shuffle)");
                }
            }
        }

        private void TickBookWorker(HandoffTest test, BookWorker w, BookWorker partner, int frame, List<SC2Action> actions)
        {
            var live = LiveWorker(w.Tag);
            if (live?.Position == null) return;
            var px = live.Position.X;
            var py = live.Position.Y;
            // Point 0 = the owner's near point (2026-10-30 19:39 yellow):
            // 0.85u out on the hatchery line, NOT the 1.4-1.5u sweep Ideal.
            var standX = (float)(test.Ideal.CenterX + NearPointRadius * Math.Cos(test.BaseAngleRad));
            var standY = (float)(test.Ideal.CenterY + NearPointRadius * Math.Sin(test.BaseAngleRad));
            var (ability, target, carrying) = OrderInfo(w.Tag);
            var gathering = IsGatherAbility(ability) && target == test.MineralTag;

            // Release with corridor staging (owner 17:21 fix): the stager holds
            // the corridor ring 0.3u behind the approach point and its GO is a
            // Smart from there — the engine then walks ~1.87u (measured
            // walk-in ≈ 8f) onto the Ideal cell. Owner answer-1: the stand
            // arrival should be 13-14 frames before the partner's flip, so:
            //   releaseAt = ExpectedTotal - ArriveLead - walkIn(engine walk).
            // = 45 - 14 - 8 = hf 23. Walk-in calibrates from Smart->standstill
            // telemetry each cycle. (The earlier hf=32 was right for 0.25u
            // staging; the corridor staging needs the earlier release.)
            var releaseAtHf = Math.Max(1, (int)Math.Ceiling(test.ExpectedHarvestTotal - ArriveLeadFrames - test.WalkInEstimate));

            // Counter integrity: true harvest starts when the PARTNER flips (the
            // mineral frees). A worker that Smarted in and stands waiting at a
            // busy mineral also shows stationary 1183 — without this reset those
            // wait frames would count toward its 32-release (owner's standing-
            // still definition: only frames mined as the active harvester count).
            if (partner.LastFlipFrame >= 0 && partner.LastFlipFrame != w.PartnerFlipSeen)
            {
                w.PartnerFlipSeen = partner.LastFlipFrame;
                w.HarvestFrames = 0;
                w.FirstHarvestFrame = -1;
            }

            // Harvest-frame counting (shared convention: stationary + 1183 on own mineral).
            // Proximity guard: without it a post-drop auto-1183 at the hatchery
            // (engine default) would count as "harvest" and corrupt idleGap.
            if (gathering && Speed(w.Tag, live) < HarvestStillEpsilon
                && Dist(live, (float)test.Ideal.CenterX, (float)test.Ideal.CenterY) < CommitRadius)
            {
                w.HarvestFrames++;
                if (w.FirstHarvestFrame < 0)
                {
                    w.FirstHarvestFrame = frame;
                    // Mineral idle measurement: gap since the partner last harvested.
                    if (partner.LastHarvestFrame >= 0 && w.Cycles > 0)
                    {
                        var gap = frame - partner.LastHarvestFrame;
                        w.IdleGapSum += gap;
                        w.IdleGapCount++;
                        BookLog(test, w, frame, $"cycle {w.Cycles + 1} first harvest — idleGap={gap} (partner last harvest f{partner.LastHarvestFrame})");
                    }
                }
                w.LastHarvestFrame = frame;
            }

            switch (w.Phase)
            {
                case BookPhase.Mining:
                    // Walk-in measurement: first stationary frame within 0.6u of
                    // the stand after this cycle's Smart. Feeds releaseAtHf.
                    // Fires before the partner's flip in the normal release flow
                    // (arrival = flip-13.5), so the wait never pollutes it.
                    if (w.StandStillFrame < 0 && w.LastSmartFrame >= 0 && frame > w.LastSmartFrame
                        && Speed(w.Tag, live) < 0.02f
                        && Dist(live, standX, standY) < 0.6f)
                    {
                        w.StandStillFrame = frame;
                        var walkIn = frame - w.LastSmartFrame;
                        if (walkIn >= 1 && walkIn <= 40)
                        {
                            test.WalkInEstimate = (test.WalkInEstimate * 3 + walkIn) / 4.0;
                            BookLog(test, w, frame, $"walk-in measured {walkIn}f (est {test.WalkInEstimate:F1})");
                        }
                    }
                    if (IsReturnAbility(ability) || (carrying && !gathering))
                    {
                        // 1183→1184 flip: cycle ends. Start the speed return leg.
                        if (w.LastFlipFrame >= 0 && w.FirstHarvestFrame >= 0)
                        {
                            var cycle = frame - w.LastFlipFrame;
                            var harvest = w.HarvestFrames;
                            w.CycleSum += cycle;
                            BookLog(test, w, frame,
                                $"cycle {w.Cycles} done: cycle={cycle} harvest={harvest} returnLeg={(w.LastDropFrame >= 0 ? (w.LastDropFrame - w.LastFlipFrame).ToString() : "?")} outboundLeg={(w.LastSmartFrame >= 0 ? (w.LastSmartFrame - w.LastDropFrame).ToString() : "?")} avgCycle={(w.Cycles > 0 ? (w.CycleSum / (double)w.Cycles).ToString("F1") : "?")}");
                        }
                        w.Cycles++;
                        var doneHarvest = w.HarvestFrames;
                        w.HarvestFrames = 0;
                        w.FirstHarvestFrame = -1;
                        w.LastFlipFrame = frame;
                        w.StandStillFrame = -1;
                        // Learn this mineral's true stand-still harvest length
                        // (measured YA = 45 exactly; the release math keys off it).
                        test.ExpectedHarvestTotal = Math.Max(test.ExpectedHarvestTotal, doneHarvest);
                        w.Phase = BookPhase.ReturnMove;
                        // PASSIVE return leg — NO commands. The engine auto-returns
                        // the drone the frame it flips 1183→1184; measured telemetry
                        // (run 4, SB-B f118–125): an extended MOVE toward a point
                        // past the drop ring lands INSIDE the hatchery footprint,
                        // pathing fails, and the worker FREEZES for 7+ frames while
                        // the order churns. The auto-return already paths at full
                        // speed; the only things needed here are the drop-point
                        // measurement (in ReturnMove) and a stall safety net.
                        w.ReturnCmdIssued = true; // no return-cmd will be issued
                        BookLog(test, w, frame,
                            $"flip — passive return (engine auto-return, no commands)");
                    }
                    else if (!gathering && ability != (int)Abilities.MOVE && ability != 0)
                    {
                        // Engine re-targeted to another mineral: re-assert the book.
                        var smart = CreateSmart(w.Tag, test.MineralTag);
                        if (smart != null) actions.Add(smart);
                        test.BookReasserts++;
                        BookLog(test, w, frame,
                            $"BOOK re-assert #{test.BookReasserts} (was ability={ability} tgt={target} want={test.MineralTag})");
                    }
                    break;

                case BookPhase.ReturnMove:
                    if (!carrying)
                    {
                        // Cargo dropped — measure the true return point, start outbound.
                        w.RetX = px;
                        w.RetY = py;
                        w.HaveReturnPoint = true;
                        w.LastDropFrame = frame;
                        // APPROACH 3 DROP FOR EVERY WORKER (owner 2026-10-30
                        // 19:24 "I like 3 best", extended to all minerals when
                        // the near point replaced the corridor 2026-10-30):
                        // after the cargo drop the worker goes STRAIGHT to
                        // point 0 (the near point, 0.85u on the hatchery line)
                        // with a QUEUED harvest, refreshed every 14 frames —
                        // the refresh beats the engine's 15-frame busy-mineral
                        // re-target. The old roll-through corridor died with
                        // the near point: its 2u-past extension now aims INTO
                        // the mineral footprint (measured BA 250-293 / YB
                        // 656-682 cycles on the 20:25 run). The queued harvest
                        // fires on arrival: the worker waits IN 1183 AT the
                        // cell, promoted the frame the partner flips.
                        var mv0 = CreateMove(w.Tag, standX, standY);
                        if (mv0 != null) actions.Add(mv0);
                        var qh = CreateSmartQueued(w.Tag, test.MineralTag);
                        if (qh != null) actions.Add(qh);
                        w.LastSmartFrame = frame;
                        w.LastRefreshFrame = frame;
                        w.Phase = BookPhase.CellWait;
                        BookLog(test, w, frame,
                            $"dropped at ({px:F2},{py:F2}) — approach3: MOVE to point 0 ({standX:F2},{standY:F2}) + queued harvest");
                        break;
                    }
                    else
                    {
                        // Carrying: the engine is auto-returning. Safety nets only —
                        // never a MOVE (unreachable interior targets freeze the worker).
                        var dh = (float)Math.Sqrt((px - _hatchery.X) * (px - _hatchery.X) + (py - _hatchery.Y) * (py - _hatchery.Y));
                        var returning = IsReturnAbility(ability) || ability == (int)Abilities.MOVE || ability == 0;
                        if (carrying && !returning && frame - w.LastReturnCmdFrame > 10)
                        {
                            // Engine re-targeted mid-return (bump stole the order):
                            // re-assert the return.
                            w.LastReturnCmdFrame = frame;
                            var ret = CreateReturn(w.Tag, _hatcheryTag);
                            if (ret != null) actions.Add(ret);
                            BookLog(test, w, frame, $"return re-assert (ability={ability} dHatch={dh:F2})");
                        }
                        else if (carrying && Speed(w.Tag, live) < 0.01f && dh > 4.6f
                            && frame - w.LastFlipFrame > 12 && frame - w.LastReturnCmdFrame > 10)
                        {
                            // Genuine stall far from the hatchery. Threshold is
                            // flip+12 because the post-harvest pickup animation
                            // locks the worker ~8 frames at the mineral (natural,
                            // ability stays 1184, speed 0) — that freeze is NOT a
                            // stall and the recovery Smart would just re-issue the
                            // order the engine already has.
                            w.LastReturnCmdFrame = frame;
                            var ret2 = CreateReturn(w.Tag, _hatcheryTag);
                            if (ret2 != null) actions.Add(ret2);
                            BookLog(test, w, frame, $"return STALL recovery (ability={ability} dHatch={dh:F2})");
                        }
                    }
                    break;

                case BookPhase.OutboundMove:
                    {
                        // Arrival window is on the APPROACH POINT, not the stand:
                        // Smart fired from the 1.87u corridor lands the Ideal
                        // cell; Smart fired close-in from a wrong angle snaps to
                        // the inside edge (owner 17:21 fix).
                        var dap = (float)Math.Sqrt((px - test.APX) * (px - test.APX) + (py - test.APY) * (py - test.APY));
                        var ds = (float)Math.Sqrt((px - standX) * (px - standX) + (py - standY) * (py - standY));
                        if (dap < 0.30f)
                        {
                            if (partner.HarvestFrames > 0 && partner.HarvestFrames < releaseAtHf)
                            {
                                // Partner mid-cycle (not near release yet): stage on the corridor ring.
                                var mv3 = CreateMove(w.Tag, test.BSpotX, test.BSpotY);
                                if (mv3 != null) actions.Add(mv3);
                                w.Phase = BookPhase.ToStage;
                                BookLog(test, w, frame,
                                    $"partner mid-cycle (hf={partner.HarvestFrames}) — staging at ({test.BSpotX:F2},{test.BSpotY:F2})");
                            }
                            else
                            {
                                var smart = CreateSmart(w.Tag, test.MineralTag);
                                if (smart != null) actions.Add(smart);
                                w.LastSmartFrame = frame;
                                w.Phase = BookPhase.Mining;
                                BookLog(test, w, frame,
                                    $"smart at {ds:F2}u from stand — outbound leg {frame - w.LastDropFrame}f");
                            }
                        }
                        else
                        {
                            // PARTNER-PARKED HOLD, surgical: fire only when MY OWN
                            // progress has stalled (8+ consecutive frames under
                            // 0.10 u/frame — rolling never drops below 0.15, the
                            // shove-match bobs at 0-0.08) AND the partner is close
                            // and parked. The naive distance-only version fired on
                            // every normal handoff approach (partner legitimately
                            // 1.0u away while mining) and cost ~40 frames/cycle.
                            var mySpeed = Speed(w.Tag, live);
                            if (mySpeed < 0.10f) w.OutboundStallFrames++;
                            else w.OutboundStallFrames = 0;
                            // ENGINE-PLACEMENT SMART: if we've stalled within
                            // reach of the stand, hand the last stretch to the
                            // engine. For the sandwiched YB stand it places us
                            // ~0.43u short of the Ideal point (YA's adjacent
                            // cell shrinks YB's placement), so the ds<0.28
                            // window never fires and the worker harvest-waits
                            // against MOVE spam forever. Smart from here: free
                            // mineral → harvest starts; busy → the engine parks
                            // us at the wait ring and auto-promotes on the
                            // partner's flip (the owner's staging design).
                            // Radius 0.75 -> 1.2 (run 20261030140827): an
                            // off-axis worker orbit's YB's body at ds 0.78-1.1
                            // (aim-2u-past points across the far face when
                            // approaching from the pinched SW pocket), burning
                            // 400+ frames; the Smart ends the orbit and the
                            // engine walks the last stretch at full speed.
                            // Only fires on stall>10, so healthy minerals that
                            // roll into the 0.28 window never touch it.
                            if (w.OutboundStallFrames > 10 && ds < 1.2f)
                            {
                                var smart2 = CreateSmart(w.Tag, test.MineralTag);
                                if (smart2 != null) actions.Add(smart2);
                                w.LastSmartFrame = frame;
                                w.Phase = BookPhase.Mining;
                                BookLog(test, w, frame,
                                    $"smart from engine placement ({ds:F2}u from Ideal stand, stall {w.OutboundStallFrames}f) — outbound leg {frame - w.LastDropFrame}f");
                                break;
                            }
                            var pLive = LiveWorker(partner.Tag);
                            var pDist = float.MaxValue;
                            if (pLive?.Position != null)
                            {
                                var pdx0 = pLive.Position.X - px;
                                var pdy0 = pLive.Position.Y - py;
                                pDist = (float)Math.Sqrt(pdx0 * pdx0 + pdy0 * pdy0);
                            }
                            // OPPOSITE-DIRECTION YIELD: DISABLED 2026-10-30 after its
                            // first real measurement (runs 20261030135341/140129):
                            // BA avgCycle 114->128 and YB outbound legs exploded to
                            // 217-474f. Each head-on meet costs a 1.2u back-out +
                            // hold + re-approach (~15-20f) and re-fires every
                            // cycle; on YB it stacked with the shove-match hold
                            // into hundred-frame deadlocks. Pre-yield behavior
                            // (press through, natural body slide ~2f) was better.
                            // Corridor capacity stays a GEOMETRY decision (staging
                            // placement per owner's 2026-10-30 rules, chunk 3) —
                            // not solvable by in-flight routing.
                            var holding = false;
                            if (!holding && pLive?.Position != null && w.OutboundStallFrames > 10)
                            {
                                // ASYMMETRY: only the FARTHER worker yields. Without
                                // this both partners see each other parked <1.2u
                                // away and BOTH hold — a mutual standoff with both
                                // bobbing 0.5-1.5u from the stand and nobody mining
                                // (observed f400-444, sep 0.69u, YB idle). The
                                // nearer worker keeps pressing so the handoff
                                // completes.
                                var pds = (float)Math.Sqrt(
                                    (pLive.Position.X - standX) * (pLive.Position.X - standX)
                                    + (pLive.Position.Y - standY) * (pLive.Position.Y - standY));
                                if (pDist < 1.2f && Speed(partner.Tag, pLive) < 0.13f && ds > pds + 0.1f)
                                {
                                    holding = true;
                                    // STOP cancels my standing MOVE order — without
                                    // it the worker keeps pressing into the partner
                                    // even while "holding".
                                    if (w.OutboundStallFrames <= 12 || frame - w.LastSmartLogFrame > 30)
                                    {
                                        var stop2 = CreateStop(w.Tag);
                                        if (stop2 != null) actions.Add(stop2);
                                    }
                                    if (frame - w.LastSmartLogFrame > 30)
                                    {
                                        w.LastSmartLogFrame = frame;
                                        BookLog(test, w, frame,
                                            $"holding — shove match vs parked partner {pDist:F2}u away, stall {w.OutboundStallFrames}f, I am farther (ds={ds:F2} vs {pds:F2})");
                                    }
                                }
                            }
                            if (!holding)
                            {
                                w.HoldSinceFrame = -1;
                            }
                            else if (w.HoldSinceFrame < 0)
                            {
                                w.HoldSinceFrame = frame;
                            }
                            else if (frame - w.HoldSinceFrame > 60 && frame - w.LastSmartLogFrame > 30)
                            {
                                // Held >60f: the presser isn't getting in (engine
                                // harvest-wait deadlock on the sandwiched stand).
                                // Back off 2.5u toward the hatchery to break the
                                // geometry, then resume rolling next leg.
                                w.LastSmartLogFrame = frame;
                                w.HoldSinceFrame = frame; // restart the backoff clock
                                var bdx = px - standX;
                                var bdy = py - standY;
                                var blen = (float)Math.Sqrt(bdx * bdx + bdy * bdy);
                                if (blen > 0.01f)
                                {
                                    var back = CreateMove(w.Tag, standX + bdx / blen * 2.5f, standY + bdy / blen * 2.5f);
                                    if (back != null) actions.Add(back);
                                    BookLog(test, w, frame,
                                        $"backing off {2.5f:F1}u — hold exceeded 60f, breaking standoff");
                                }
                            }
                            if (!holding)
                            {
                            // ROLLING re-target, re-issued EVERY frame: the target is
                            // recomputed from the CURRENT position through the stand
                            // point, extended 2u past it. A single extended MOVE
                            // decelerates early when the target sits only ~0.7u past
                            // the mineral center (engine avoidance curves away and
                            // misses the 0.28u Smart window); constant fresh MOVEs
                            // keep full speed (owner's validated technique).
                            // EXCEPTION: never aim through another mineral's
                            // footprint — YB's line passes through YA (centers 1u
                            // apart), the worker enters YA's harvest cells, and the
                            // engine harvest-lock pins it mid-cell for hundreds of
                            // frames against a MOVE spam it can't obey (observed
                            // f150-568). In that case stop commanding entirely; the
                            // engine keeps its own order and the worker walks free.
                            var ddx2 = test.APX - px;
                            var ddy2 = test.APY - py;
                            var dlen2 = (float)Math.Sqrt(ddx2 * ddx2 + ddy2 * ddy2);
                            var aimX = test.APX + ddx2 / dlen2 * 2.0f;
                            var aimY = test.APY + ddy2 / dlen2 * 2.0f;
                            // Pure rolling — no steering. Two steering designs were
                            // tried (perpendicular push-off, near-side waypoint) and
                            // BOTH made things worse: BA's line clips YB's body
                            // circle at 0.58u and the natural body-slide only costs
                            // ~2 frames, while waypoint commands break the slide
                            // into half-speed hops (BA avgCycle 114→128). Steering
                            // only flickered near the 0.85 threshold anyway. YB's
                            // corridor problem (stand 0.98u from YA's center) is a
                            // geometry decision for the owner, not solvable by
                            // in-flight routing — see report. A "shove-match slide
                            // window" (withhold re-issues while stalled 4-30f) was
                            // also tried and REVERTED: it left YB-B standing in an
                            // EMPTY corridor ~190f of a 300f grind (runs
                            // 202610301408+/142*) — without fresh MOVEs the engine
                            // never completes its avoidance curve. Constant
                            // re-issue stays (owner's validated technique).
                            var mv5 = CreateMove(w.Tag, aimX, aimY);
                            if (mv5 != null) actions.Add(mv5);
                            }
                        }
                    }
                    break;

                case BookPhase.CellWait:
                    {
                        // The engine shows gather-on-our-mineral once the wait
                        // takes hold (or instantly if the mineral was free).
                        if (gathering)
                        {
                            w.Phase = BookPhase.Mining;
                            BookLog(test, w, frame, $"promoted at the cell — mining (approach3)");
                            break;
                        }
                        if (frame - w.LastRefreshFrame >= 14)
                        {
                            w.LastRefreshFrame = frame;
                            if (partner.Phase == BookPhase.Mining || partner.Phase == BookPhase.CellWait)
                            {
                                // Miner still working: re-pin with MOVE + queued
                                // harvest (owner: +14 check, beats the 15-frame
                                // busy-mineral re-target).
                                var mv0 = CreateMove(w.Tag, standX, standY);
                                if (mv0 != null) actions.Add(mv0);
                                var qh = CreateSmartQueued(w.Tag, test.MineralTag);
                                if (qh != null) actions.Add(qh);
                                w.LastSmartFrame = frame;
                                if (frame - w.LastSmartLogFrame > 90)
                                {
                                    w.LastSmartLogFrame = frame;
                                    BookLog(test, w, frame, $"14f refresh — MOVE+queued harvest re-issued, miner hf={partner.HarvestFrames} (approach3)");
                                }
                            }
                            else
                            {
                                // Miner left without promoting us: direct Smart.
                                var s = CreateSmart(w.Tag, test.MineralTag);
                                if (s != null) actions.Add(s);
                                w.Phase = BookPhase.Mining;
                                BookLog(test, w, frame, $"miner left (phase={partner.Phase}) — direct Smart (approach3)");
                            }
                        }
                    }
                    break;

                case BookPhase.ToStage:
                    {
                        var dst = (float)Math.Sqrt((px - test.BSpotX) * (px - test.BSpotX) + (py - test.BSpotY) * (py - test.BSpotY));
                        if (dst < 0.12f)
                        {
                            var stop = CreateStop(w.Tag);
                            if (stop != null) actions.Add(stop);
                            w.Phase = BookPhase.Staging;
                            BookLog(test, w, frame, "staging hold");
                        }
                        else if (partner.HarvestFrames >= releaseAtHf || partner.Phase != BookPhase.Mining)
                        {
                            // Release: partner reached the arrival-targeted
                            // frame — or already left (flip resets its counter;
                            // either way the mineral is free or going).
                            var smart = CreateSmart(w.Tag, test.MineralTag);
                            if (smart != null) actions.Add(smart);
                            w.LastSmartFrame = frame;
                            w.Phase = BookPhase.Mining;
                            BookLog(test, w, frame, $"GO — partner hf={partner.HarvestFrames} phase={partner.Phase}, smart from {dst:F2}u short of spot");
                        }
                        else if (ability == 0 && frame - w.LastSmartLogFrame > 25)
                        {
                            var mv5 = CreateMove(w.Tag, test.BSpotX, test.BSpotY);
                            if (mv5 != null) actions.Add(mv5);
                            w.LastSmartLogFrame = frame;
                            BookLog(test, w, frame, $"to-stage MOVE re-issued ({dst:F2}u short)");
                        }
                    }
                    break;

                case BookPhase.Staging:
                    if (partner.HarvestFrames >= releaseAtHf || partner.Phase != BookPhase.Mining)
                    {
                        var smart = CreateSmart(w.Tag, test.MineralTag);
                        if (smart != null) actions.Add(smart);
                        w.LastSmartFrame = frame;
                        w.Phase = BookPhase.Mining;
                        BookLog(test, w, frame, $"GO — partner hf={partner.HarvestFrames} phase={partner.Phase}, smart from staging");
                    }
                    break;
            }
        }

        private void BookLog(HandoffTest test, BookWorker w, int frame, string note)
        {
            var live = LiveWorker(w.Tag);
            BabySharkAI.Instance?.CommandTelemetry?.LogSmHandoffSample(
                frame, _sideTag, test.Label, w.Tag, w.Role, "book",
                live?.Position?.X ?? -1, live?.Position?.Y ?? -1,
                DistToIdeal(test, live), w.HarvestFrames, note, ++test.EventOrdinal);
            Console.WriteLine($"[SMHANDOFF] {test.Label}-{w.Role} f{frame} book — {note}");
        }

        // ── completion / reporting ──

        private void CheckComplete(HandoffTest test, int frame)
        {
            if (test.Complete) return;
            if (frame - test.StartFrame <= BudgetFrames) return;
            test.Complete = true;
            LogHandoff(test, frame, actions: null, note: "budget-timeout");
        }

        private void LogHandoff(HandoffTest test, int frame, List<SC2Action> actions, string note)
        {
            var liveA = LiveWorker(test.TagA);
            var liveB = LiveWorker(test.TagB);
            var idleGap = (test.BFirstHarvestFrame >= 0 && test.ALastHarvestFrame >= 0)
                ? test.BFirstHarvestFrame - test.ALastHarvestFrame
                : int.MinValue;
            var aCycle = (test.AReturnFrame >= 0 && test.AFirstHarvestFrame >= 0)
                ? test.AReturnFrame - test.AFirstHarvestFrame
                : -1;
            var bWalkIn = (test.BStandFrame >= 0 && test.BSmartFrame >= 0) ? test.BStandFrame - test.BSmartFrame : -1;
            var bWait = (test.BFirstHarvestFrame >= 0 && test.BStandFrame >= 0) ? test.BFirstHarvestFrame - test.BStandFrame : -1;

            var summary =
                $"aStand={(test.AStandRecorded ? $"f{test.AStandFrame}" : "n/a")} "
                + $"aFirstHarvest={(test.AFirstHarvestFrame >= 0 ? $"f{test.AFirstHarvestFrame}" : "n/a")} "
                + $"aSmartB={(test.BSmartFrame >= 0 ? $"f{test.BSmartFrame}" : "n/a")}(hfAtSmart={test.AHFAtSmart}) "
                + $"aLastHarvest={(test.ALastHarvestFrame >= 0 ? $"f{test.ALastHarvestFrame}" : "n/a")} "
                + $"aCycle={aCycle} aMaxGap={test.AMaxGap} aMaxDrift={test.AMaxDrift:F3} "
                + $"bHold={(test.BHoldStartFrame >= 0 ? $"f{test.BHoldStartFrame}" : "n/a")} holdDrift={test.BMaxDriftHold:F3}{(test.BWandered ? " WANDERED" : "")} "
                + $"bSmart={(test.BSmartFrame >= 0 ? $"f{test.BSmartFrame}" : "n/a")} bStand={(test.BStandFrame >= 0 ? $"f{test.BStandFrame}" : "n/a")}(d={(test.BStandDIdeal >= 0 ? test.BStandDIdeal.ToString("F3") : "n/a")}) "
                + $"walkIn={bWalkIn} waiter={(test.BWaiterFrame >= 0 ? $"f{test.BWaiterFrame}" : "n/a")} "
                + $"bFirstHarvest={(test.BFirstHarvestFrame >= 0 ? $"f{test.BFirstHarvestFrame}" : "n/a")} wait={bWait} "
                + $"idleGap={(idleGap != int.MinValue ? $"f{idleGap}" : "n/a")}";

            Console.WriteLine($"[SMHANDOFF] {test.Label} HANDOFF side={_sideTag} {note}: {summary}");

            BabySharkAI.Instance?.CommandTelemetry?.LogSmHandoffSample(
                frame, _sideTag, test.Label, test.TagB, "B", "handoff",
                liveB?.Position?.X ?? -1, liveB?.Position?.Y ?? -1,
                test.BStandDIdeal, test.BHarvestFrames, summary, ++test.EventOrdinal);
        }

        private void PrintSummary(int frame)
        {
            Console.WriteLine($"[SMHANDOFF] ===== HANDOFF TEST COMPLETE side={_sideTag} frame={frame} =====");
            foreach (var test in _tests)
            {
                var idleGap = (test.BFirstHarvestFrame >= 0 && test.ALastHarvestFrame >= 0)
                    ? (test.BFirstHarvestFrame - test.ALastHarvestFrame).ToString()
                    : "n/a";
                Console.WriteLine($"[SMHANDOFF] SUMMARY {test.Label}: idleGap={idleGap} "
                    + $"bStandD={(test.BStandDIdeal >= 0 ? test.BStandDIdeal.ToString("F3") : "n/a")} "
                    + $"aCycle={((test.AReturnFrame >= 0 && test.AFirstHarvestFrame >= 0) ? (test.AReturnFrame - test.AFirstHarvestFrame).ToString() : "n/a")} "
                    + $"aMaxDrift={test.AMaxDrift:F3} bHoldDrift={test.BMaxDriftHold:F3}{(test.BWandered ? " WANDERED" : "")} "
                    + $"walkIn={((test.BStandFrame >= 0 && test.BSmartFrame >= 0) ? (test.BStandFrame - test.BSmartFrame).ToString() : "n/a")} "
                    + $"wait={((test.BFirstHarvestFrame >= 0 && test.BStandFrame >= 0) ? (test.BFirstHarvestFrame - test.BStandFrame).ToString() : "n/a")} "
                    + $"bookReasserts={test.BookReasserts}");
            }
        }

        private void Log(HandoffTest test, ulong tag, string role, string phase, int frame, WorkerEntryDto live,
            double dIdeal, int harvestFrames, string note, List<SC2Action> actions)
        {
            BabySharkAI.Instance?.CommandTelemetry?.LogSmHandoffSample(
                frame, _sideTag, test.Label, tag, role, phase,
                live?.Position?.X ?? -1, live?.Position?.Y ?? -1,
                dIdeal, harvestFrames, note, ++test.EventOrdinal);
            Console.WriteLine($"[SMHANDOFF] {test.Label} f{frame} {role}/{phase} pos=({live?.Position?.X:F3},{live?.Position?.Y:F3})"
                + (dIdeal >= 0 ? $" dIdeal={dIdeal:F3}" : "")
                + (harvestFrames > 0 ? $" hf={harvestFrames}" : "")
                + (string.IsNullOrEmpty(note) ? "" : $" — {note}"));
        }

        // ── helpers ──

        private double DistToIdeal(HandoffTest test, WorkerEntryDto live)
        {
            if (live?.Position == null) return -1;
            var dx = live.Position.X - test.Ideal.IdealX;
            var dy = live.Position.Y - test.Ideal.IdealY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private bool IsGathering(ulong workerTag)
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

        private bool IsReturning(ulong workerTag)
        {
            var units = _lastObservation?.Observation?.RawData?.Units;
            if (units == null || workerTag == 0) return false;
            foreach (var u in units)
            {
                if (u == null || u.Tag != workerTag) continue;
                var ability = (int)(u.Orders?.FirstOrDefault()?.AbilityId ?? 0);
                return ability == (int)Abilities.HARVEST_RETURN
                    || ability == (int)Abilities.HARVEST_RETURN_DRONE
                    || ability == (int)Abilities.HARVEST_RETURN_PROBE
                    || ability == (int)Abilities.HARVEST_RETURN_SCV;
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

        // True if the segment (x1,y1)->(x2,y2) passes within radius of (px,py).
        private static bool SegmentNearPoint(float x1, float y1, float x2, float y2, float px, float py, float radius)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var len2 = dx * dx + dy * dy;
            float t;
            if (len2 < 0.0001f) t = 0f;
            else
            {
                t = ((px - x1) * dx + (py - y1) * dy) / len2;
                t = t < 0f ? 0f : (t > 1f ? 1f : t);
            }
            var cx = x1 + t * dx;
            var cy = y1 + t * dy;
            var ddx = cx - px;
            var ddy = cy - py;
            return ddx * ddx + ddy * ddy < radius * radius;
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

        private static SC2Action CreateGatherOnGeyser(ulong tag, ulong geyserTag)
        {
            // Sharky-proven (worker_commands_Sharky_20261030163905.jsonl, 390
            // gathers on the extractor geyser tag): vespene is commanded with
            // EXPLICIT HARVEST_GATHER (3666) targeted at the geyser unit tag.
            // A SMART on the geyser does NOT work — live-tested 2026-10-30:
            // the engine re-interpreted it as a mineral gather and the worker
            // mineral-walked to the nearest mineral line instead (1183 gather
            // + full carry cycle on SB, never within 3.9u of the geyser).
            if (tag == 0 || geyserTag == 0) return null;
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.HARVEST_GATHER,
                TargetUnitTag = geyserTag,
                QueueCommand = false,
            };
            command.UnitTags.Add(tag);
            return new SC2Action { ActionRaw = new ActionRaw { UnitCommand = command } };
        }

        private static SC2Action CreateReturn(ulong tag, ulong hatcheryTag)
        {
            // HARD-WON (BabySharkMiningManager.CreateInstructionReturnAction):
            // return-cargo = SMART on the town hall. HARVEST_RETURN_DRONE (1184)
            // with a unit target is DROPPED by the game — the worker would stand
            // at the hatchery still carrying.
            if (tag == 0 || hatcheryTag == 0) return null;
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.SMART,
                TargetUnitTag = hatcheryTag,
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
