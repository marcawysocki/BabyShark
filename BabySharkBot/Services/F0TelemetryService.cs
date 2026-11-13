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
    /// --f0test (owner spec 2026-10-31 16:15): first-20-frames telemetry test
    /// for the Shortest-Paths-and-Bumping discussion.
    ///
    /// THE SIX (telemetry):
    ///   T1 — NO ASSIST, rolls to the TA SMH: every frame a MOVE to a point 2u
    ///        past the SMH (homing rolling waypoint, current position + 2u
    ///        toward the anchor), SMART on TA inside 0.45u.
    ///   Y1 — same roll to the YA harvest point (SmHarvestPoint dat point; the
    ///        owner: the whole yellow side has ONE ideal harvest point).
    ///   T4, T5 — Mineral Walk (SMART) to SA at frame 0.
    ///   B2, Y4 — Mineral Walk (SMART) to BA at frame 0.
    ///
    /// SUICIDE (attack own hatchery, ability 3674):
    ///   T2, T3, S2, Y2, Y3, Y5 — frame 0.
    ///   The six — when BOTH T1 and Y1 CARRY their first cargo (buff 271/272;
    ///   2026-11-03 — the 1183 order fires on harvest-order assignment while
    ///   still walking in, so it is not comparable across SMART gates), or at
    ///   the frame-90 fail-open, whichever is first.
    ///
    /// DRAWN EVERY FRAME (both starts get the geometry automatically — all
    /// points resolve from the current start's patch):
    ///   - start-centered circles at the frame 9/11/13 telemetry radii (T1
    ///     teal, Y1 yellow, B2+T4 white, T5+Y4 purple) — radii measured from a
    ///     prior run's telemetry and mirrored across starts;
    ///   - teal line T1 start -> TA SMH, yellow line Y1 start -> YA HP;
    ///   - three red mineral-walk lines from T5's start to the SA, SB, BB
    ///     mineral CENTERS, three red lines from B2's start to BA, BB, SB
    ///     centers (mineral walk targets the X,Y of the mineral, not the
    ///     harvest spot).
    /// </summary>
    public sealed class F0TelemetryService
    {
        private const float RollingStep = 2.0f;        // "2u past the SMH"
        // SMART gate experiment log (SE start, --f0test) — now the --f0gate=X
        // CLI knob (Settings.F0SmartGateRadius, default 1.20):
        //   0.45 (champion gate) -> T1/Y1 ARRIVED f20 (07:12 + 09:55 runs);
        //        both lost ~2 frames sliding around the mineral collision
        //        circle at full speed before the gate let the SMART fire.
        //   0.90 (v3, 10:32 run) -> SMART f16 at d~0.80-0.87, ARRIVED f17.
        //        The engine harvest approach absorbs the slide completely.
        //   1.20 (v4, 06:54 run) -> SMART f13-14 right after the bump,
        //        ARRIVED T1 f15 / Y1 f16.
        private static float SmartRangeRadius => Settings.F0SmartGateRadius;
        private const float ArrivedStandRadius = 0.30f;
        private const int SuicideAttackAbilityId = 3674;
        private const int SmartForceFrame = 19;        // owner spec 2026-11-01 09:03: SMARTs issued to all seven at f19
        private const int DirectMoveFrameA = 13;       // owner spec 2026-11-01 09:03: all seven move to TA/YA SMH
        private const int DirectMoveFrameB = 16;
        private static readonly int[] RadiusFrames = { 10, 13, 16, 19 };
        private const int FailOpenFrame = 26; // owner said 19 expecting arrival <19; no-assist roll arrives ~23-24 (measured SE 2026-10-31)
        private const int PickupFailOpenFrame = 170; // 2026-11-03: covers walk-in + ~48f gather + ~55f return leg

        // Measured frame-10/13/16/19 radii (start-relative distance in game
        // units), SE start, --f0test run 2026-10-31 (worker_commands_BabyShark_
        // SE_20261031164305.jsonl; identical across runs — deterministic).
        // Mirrored across starts (180-degree rotation preserves distances).
        // Keyed by ROLE.
        private static readonly Dictionary<string, float[]> F0CircleRadii =
            new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["T1"] = new float[] { 0.530f, 0.875f, 1.306f, 1.731f },
                ["Y1"] = new float[] { 0.537f, 0.889f, 1.328f, 1.745f },
                ["B2"] = new float[] { 0.527f, 0.872f, 1.304f, 1.815f },
                ["T4"] = new float[] { 0.527f, 0.872f, 1.304f, 1.815f },
                ["T5"] = new float[] { 0.533f, 0.881f, 1.317f, 1.831f },
                ["Y4"] = new float[] { 0.527f, 0.872f, 1.302f, 1.813f },
                ["Y5"] = new float[] { 0.535f, 0.882f, 1.316f, 1.828f }, // SE 2026-10-31 19:19 run (worker_commands_BabyShark_SE_20261031191950)
            };

        private sealed class F0Worker
        {
            public string Role = string.Empty;
            public ulong Tag;
            public float StartX, StartY, StartZ;
            public bool Arrived;
            public int ArriveFrame = -1;
            // 2026-11-03: the honest time-to-mining metric — first frame the
            // carrying buff (271/272) is on. The 1183 order only marks the
            // harvest-order ASSIGNMENT (it appears while the worker is still
            // walking in), so wide SMART gates made ArriveFrame degenerate.
            public bool PickedUp;
            public int PickupFrame = -1;
            // 2026-11-03 return-leg metric: first frame the carrying buff
            // clears after a pickup = cargo delivered at the hatchery.
            public bool WasCarrying;
            public bool Delivered;
            public int DeliverFrame = -1;
            // --f0return=move: MOVE+SMART return-assist state machine.
            public int ReturnAssistFrame = -1;
            public bool SmartIssued;
            public readonly HashSet<int> RadiusLogged = new HashSet<int>();
        }

        // Phase-driven mover choreography (owner spec 2026-10-31 19:13,
        // experiment 2026-11-02 — "levers 1+2" from the 07:12 SE telemetry):
        //   f0  — Y4+Y5 MOVE to BB/BA harvest points, T5 MOVE to TB;
        //         T4 mineral-walks to SA, B2 mineral-walks to BA
        //         (both cross their roller's line ~f13 = the bump).
        //   f13 — T4 FOLLOWS T1, B2 FOLLOWS Y1 (per-frame MOVE onto the
        //         roller's live position = sustained push after the bump,
        //         through f18). T5 NO LONGER moves to TA — its f16 TA phase
        //         knocked T1 off the anchor line at f18 in the 07:12 run
        //         (cost ~1 frame); T5 simply holds its f0 TB move.
        //   f19 — T4 SMART TB, B2 SMART BB, Y4 SMART YB, T5/Y5 SMART SB/BB.
        // MOVE targets are the SM harvest points; mineral walks and SmartTo
        // target the mineral unit (center X,Y per the owner rule).
        private enum ChoreoKind { MoveToAnchor, MoveToPoint, MoveToCenter, MineralWalk, FollowRole, SmartTo }

        private sealed class ChoreoPhase
        {
            public int StartFrame;
            public int EndFrame = -1;    // FollowRole: last frame the phase issues commands (-1 = open-ended)
            public ChoreoKind Kind;
            public string AnchorLabel;   // mineral whose SM harvest point / unit tag is the target
            public string FollowRole;    // FollowRole: role whose live position is the per-frame MOVE target
            public float PointDX, PointDY; // MoveToPoint (2026-11-03): target = own start + delta
        }

        // --f0nobump control books (2026-11-03): T4/B2 hold against the wall
        // (MoveToPoint own-start deltas that avoid both roller corridors);
        // T5/Y4/Y5 unchanged (verified no roller contact in the 07:12 run).
        private static readonly Dictionary<string, ChoreoPhase[]> NoBumpBooks =
            new Dictionary<string, ChoreoPhase[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["T5"] = new[]
                {
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "TB" },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "SB" },
                },
                ["Y4"] = new[]
                {
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "BA" },
                    new ChoreoPhase { StartFrame = 13, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "YA" },
                    new ChoreoPhase { StartFrame = 16, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "YA" },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "YB" },
                },
                ["Y5"] = new[]
                {
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "BB" },
                    new ChoreoPhase { StartFrame = 13, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "YA" },
                    new ChoreoPhase { StartFrame = 16, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "YA" },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "BB" },
                },
                ["T4"] = new[]
                {
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToPoint, PointDX = -0.5f, PointDY = +0.3f },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "TB" },
                },
                ["B2"] = new[]
                {
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToPoint, PointDX = +0.5f, PointDY = +0.5f },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "BB" },
                },
            };

        private static Dictionary<string, ChoreoPhase[]> _switchBooks;
        private static Dictionary<string, ChoreoPhase[]> _switchControlBooks;

        private static Dictionary<string, ChoreoPhase[]> ActiveBooks
        {
            get
            {
                if (Settings.F0NoBump) return NoBumpBooks;
                if (Settings.F0SwitchFrame >= 0 && Settings.F0SwitchControl)
                {
                    // --f0switchctl: T4/T5 hold at the wall (no contact with
                    // T1's bump-point corridor); T1's roller behavior is
                    // identical to the variant run, so variant-minus-control
                    // isolates the bump displacement. B2/Y4/Y5 keep the
                    // default books (Y-side control untouched).
                    return _switchControlBooks ??= new Dictionary<string, ChoreoPhase[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["T4"] = new[]
                        {
                            new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToPoint, PointDX = -0.5f, PointDY = +0.3f },
                            new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "TB" },
                        },
                        ["T5"] = new[]
                        {
                            new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToPoint, PointDX = -0.5f, PointDY = +0.3f },
                            new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "SB" },
                        },
                        ["B2"] = ChoreoBooks["B2"],
                        ["Y4"] = ChoreoBooks["Y4"],
                        ["Y5"] = ChoreoBooks["Y5"],
                    };
                }
                if (Settings.F0SwitchFrame >= 0)
                {
                    // --f0switch=N (2026-11-03): T4 keeps its f0 mineral walk
                    // to SA, but at frame N T4 and T5 get a direct MOVE onto
                    // the TA SMH anchor instead of the follow/hold; f19 SMART
                    // cleanup unchanged. B2/Y4/Y5 keep the default books.
                    var n = Settings.F0SwitchFrame;
                    return _switchBooks ??= new Dictionary<string, ChoreoPhase[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["T4"] = new[]
                        {
                            new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MineralWalk, AnchorLabel = "SA" },
                            new ChoreoPhase { StartFrame = n, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "TA" },
                            new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "TB" },
                        },
                        ["T5"] = new[]
                        {
                            // 2026-11-03 16:03 owner: T5's start angle is SA
                            // via a MOVE to the mineral CENTER (NOT a mineral
                            // walk) — a MOVE keeps collision on, so T5 trails
                            // T4 down the SA line as an active bumper.
                            new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToCenter, AnchorLabel = "SA" },
                            new ChoreoPhase { StartFrame = n, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "TA" },
                            new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "SB" },
                        },
                        ["B2"] = ChoreoBooks["B2"],
                        ["Y4"] = ChoreoBooks["Y4"],
                        ["Y5"] = ChoreoBooks["Y5"],
                    };
                }
                return ChoreoBooks;
            }
        }

        private static readonly Dictionary<string, ChoreoPhase[]> ChoreoBooks =
            new Dictionary<string, ChoreoPhase[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["T5"] = new[]
                {
                    // 2026-11-02: f13/f16 TA phases removed — T5 crossing the
                    // TA corridor knocked T1 off the anchor line at f18 (07:12
                    // run). T5 holds its f0 TB move until the f19 SMART.
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "TB" },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "SB" },
                },
                ["Y4"] = new[]
                {
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "BA" },
                    new ChoreoPhase { StartFrame = 13, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "YA" },
                    new ChoreoPhase { StartFrame = 16, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "YA" },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "YB" },
                },
                ["Y5"] = new[]
                {
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "BB" },
                    new ChoreoPhase { StartFrame = 13, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "YA" },
                    new ChoreoPhase { StartFrame = 16, Kind = ChoreoKind.MoveToAnchor, AnchorLabel = "YA" },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "BB" },
                },
                ["T4"] = new[]
                {
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MineralWalk, AnchorLabel = "SA" },
                    // Sustained push: after the f13 crossing bump, drive into
                    // T1's rear every frame through f18 (lever 2, 2026-11-02).
                    new ChoreoPhase { StartFrame = 13, EndFrame = 18, Kind = ChoreoKind.FollowRole, FollowRole = "T1" },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "TB" },
                },
                ["B2"] = new[]
                {
                    new ChoreoPhase { StartFrame = 0, Kind = ChoreoKind.MineralWalk, AnchorLabel = "BA" },
                    // Sustained push on Y1 after the f13/14 clip (lever 2).
                    new ChoreoPhase { StartFrame = 13, EndFrame = 18, Kind = ChoreoKind.FollowRole, FollowRole = "Y1" },
                    new ChoreoPhase { StartFrame = 19, Kind = ChoreoKind.SmartTo, AnchorLabel = "BB" },
                },
            };

        private readonly MawBaseLocationData _mapData;
        private bool _initialized;
        private int _initFrame;
        private readonly Dictionary<string, F0Worker> _workers = new Dictionary<string, F0Worker>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ulong> _suicideZeroTags = new List<ulong>();
        private readonly string[] _suicideZeroRoles = { "T2", "T3", "S2", "Y2", "Y3" };
        private readonly string[] _telemetryRoles = { "T1", "Y1", "B2", "T4", "T5", "Y4", "Y5" };
        private readonly Dictionary<string, int> _choreoPhaseIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _moveIssued = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private ulong _taTag, _yaTag;
        private float _taAnchorX, _taAnchorY, _yaAnchorX, _yaAnchorY;
        // --f0switch=N: T1's pre-switch roll target = T4's f13 mineral-walk
        // position (the collision point). Computed at init as T4 start +
        // 0.872u toward the SA center so it mirrors to the NW start.
        private float _bumpX, _bumpY;
        // 2026-11-03 12:27 (owner diagram): T1 rolls a FIXED BEARING from its
        // start THROUGH the bump point ("to and thru") — never homes back
        // after overshoot. Unit vector start -> bump point.
        private float _thruX, _thruY;
        private ulong _hatcheryTag;
        private float _hatcheryX, _hatcheryY;
        private bool _sixSuicideTriggered;
        private string _sixSuicideReason = string.Empty;
        private int _sixSuicideFrame = -1;
        private ResponseObservation _lastObservation;

        private static readonly Color Teal = new Color { R = 0, G = 200, B = 200 };
        private static readonly Color Yellow = new Color { R = 255, G = 255, B = 0 };
        private static readonly Color White = new Color { R = 255, G = 255, B = 255 };
        private static readonly Color Purple = new Color { R = 180, G = 60, B = 220 };
        private static readonly Color Red = new Color { R = 255, G = 0, B = 0 };

        public F0TelemetryService(MawBaseLocationData mapData)
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
                if (!TryInitialize(snapshot, frame))
                {
                    return actions; // retry next frame
                }
            }

            var offset = frame - _initFrame;

            // The two rollers: re-issue the 2u homing MOVE every frame until the
            // SMART has been issued; arrival is the engine's 1183 on the mineral.
            TickRoller("T1", _taAnchorX, _taAnchorY, _taTag, offset, actions);
            TickRoller("Y1", _yaAnchorX, _yaAnchorY, _yaTag, offset, actions);

            // The five movers run their phase books (see ChoreoBooks header):
            // f0 moves/walks, f13 T4 follows T1 / B2 follows Y1 (sustained
            // push through f18), f19 SMART locks. T5 holds its f0 TB move.
            TickChoreo("T5", offset, actions);
            TickChoreo("Y4", offset, actions);
            TickChoreo("Y5", offset, actions);
            TickChoreo("T4", offset, actions);
            TickChoreo("B2", offset, actions);

            // Radius snapshots at frames 9/11/13 — the numbers the circles use.
            foreach (var radiusFrame in RadiusFrames)
            {
                if (offset != radiusFrame) continue;
                foreach (var role in _telemetryRoles)
                {
                    LogRadius(role, radiusFrame);
                }
            }

            // Pickup + delivery watch (2026-11-03): first carrying buff = cargo
            // picked up (honest time-to-mining); the buff clearing afterwards =
            // cargo delivered at the hatchery (return-leg metric).
            // --f0return=move assists the return: MOVE 2u toward the hatchery
            // on the pickup frame, SMART on the hatchery one frame later.
            foreach (var role in new[] { "T1", "Y1" })
            {
                if (!_workers.TryGetValue(role, out var pw) || pw.Delivered) continue;
                var (_, _, carrying) = OrderInfo(pw.Tag);
                if (!pw.PickedUp)
                {
                    if (carrying)
                    {
                        pw.PickedUp = true;
                        pw.PickupFrame = offset;
                        Console.WriteLine($"[F0TEST] {role} PICKUP f{offset} (SMART-order f{pw.ArriveFrame})");
                    }
                }
                else if (pw.WasCarrying && !carrying)
                {
                    pw.Delivered = true;
                    pw.DeliverFrame = offset;
                    Console.WriteLine($"[F0TEST] {role} DELIVERED f{offset} (cycle {pw.DeliverFrame - pw.PickupFrame}f after pickup)");
                }
                pw.WasCarrying = carrying;

                if (Settings.F0ReturnAssist && pw.PickedUp && !pw.Delivered)
                {
                    var liveR = LiveWorker(pw.Tag);
                    if (liveR?.Position == null) continue;
                    if (pw.ReturnAssistFrame < 0)
                    {
                        pw.ReturnAssistFrame = offset;
                        var hx = _hatcheryX - liveR.Position.X;
                        var hy = _hatcheryY - liveR.Position.Y;
                        var hd = (float)Math.Sqrt(hx * hx + hy * hy);
                        if (hd > 0.001f)
                        {
                            var assist = CreateMove(pw.Tag,
                                liveR.Position.X + hx / hd * RollingStep,
                                liveR.Position.Y + hy / hd * RollingStep);
                            if (assist != null)
                            {
                                actions.Add(assist);
                                Console.WriteLine($"[F0TEST] {role} RETURN-ASSIST move f{offset} -> hatchery-ward 2u");
                            }
                        }
                    }
                    else if (offset == pw.ReturnAssistFrame + 1)
                    {
                        var smartHome = CreateSmart(pw.Tag, _hatcheryTag);
                        if (smartHome != null)
                        {
                            actions.Add(smartHome);
                            Console.WriteLine($"[F0TEST] {role} RETURN-ASSIST smart f{offset} -> hatchery {_hatcheryTag}");
                        }
                    }
                }
            }

            // Second suicide wave: both rollers DELIVERED their first cargo,
            // or the fail-open. 2026-11-03: switched from the 1183-order
            // arrival to pickup, then to delivery — the return leg is now the
            // measured quantity.
            if (!_sixSuicideTriggered)
            {
                var t1 = _workers.TryGetValue("T1", out var w1) && w1.Delivered;
                var y1 = _workers.TryGetValue("Y1", out var w2) && w2.Delivered;
                if ((t1 && y1) || offset >= PickupFailOpenFrame)
                {
                    _sixSuicideTriggered = true;
                    _sixSuicideFrame = frame;
                    _sixSuicideReason = t1 && y1
                        ? $"both delivered (T1 f{_workers["T1"].DeliverFrame}, Y1 f{_workers["Y1"].DeliverFrame})"
                        : $"frame-{PickupFailOpenFrame} fail-open (T1 delivered={t1}, Y1 delivered={y1})";
                    Console.WriteLine($"[F0TEST] six suicide f{frame} ({offset}) — {_sixSuicideReason}");
                }
            }

            if (offset == 0)
            {
                // First suicide wave: T2/T3/S2/Y2/Y3 attack the hatchery.
                IssueSuicide(actions, _suicideZeroTags, "frame-0 five (T2,T3,S2,Y2,Y3)");
            }
            else if (_sixSuicideTriggered)
            {
                // Everyone (all living drones) joins — re-issued every frame.
                IssueSuicide(actions, null, "all drones");
            }
            else if (offset <= 3)
            {
                // Re-arm the frame-0 wave briefly in case an attack dropped.
                IssueSuicide(actions, _suicideZeroTags, "frame-0 six re-arm");
            }

            DrawAnnotations(offset);
            return actions;
        }

        // ── rollers ──

        private void TickRoller(string role, float anchorX, float anchorY, ulong mineralTag, int offset, List<SC2Action> actions)
        {
            if (!_workers.TryGetValue(role, out var worker) || worker.Arrived)
            {
                return;
            }
            if (_sixSuicideTriggered || offset > FailOpenFrame)
            {
                return;
            }

            var live = LiveWorker(worker.Tag);
            if (live?.Position == null)
            {
                return;
            }

            // Anchor deltas always drive the arrival/SMART gates. The MOVE
            // target may be a via-point (E-series pin experiments): roll at
            // (bearing-to-anchor + delta) for ViaDist units until ViaUntil,
            // then home on the true anchor.
            var adx = anchorX - live.Position.X;
            var ady = anchorY - live.Position.Y;
            var anchorDist = (float)Math.Sqrt(adx * adx + ady * ady);
            var (viaX, viaY) = RollerTarget(role, worker, anchorX, anchorY, offset);
            var switchN = Settings.F0SwitchFrame;
            var isSwitchRoller = switchN >= 0 && role.Equals("T1", StringComparison.OrdinalIgnoreCase);
            if (isSwitchRoller && offset < switchN)
            {
                // --f0switch=N (2026-11-03 12:27 owner diagram): fixed-bearing
                // thru-line — waypoint = position + 2u along the start->bump
                // bearing, so T1 passes THROUGH the collision point and never
                // homes back onto it after overshoot (the f13 ejection bug).
                viaX = live.Position.X + _thruX * RollingStep;
                viaY = live.Position.Y + _thruY * RollingStep;
            }
            var dx = viaX - live.Position.X;
            var dy = viaY - live.Position.Y;
            var dist = (float)Math.Sqrt(dx * dx + dy * dy);

            var (ability, target, _) = OrderInfo(worker.Tag);
            if (ability == 1183 && target == mineralTag)
            {
                worker.Arrived = true;
                worker.ArriveFrame = offset;
                Console.WriteLine($"[F0TEST] {role} ARRIVED f{offset} pos=({live.Position.X:F2},{live.Position.Y:F2}) d={anchorDist:F2} — 1183 on mineral");
                return;
            }

            if (!worker.SmartIssued)
            {
                if (offset >= SmartForceFrame)
                {
                    // Owner spec 2026-11-01 09:03: frame 19 SMART on the
                    // roller's mineral regardless of the gate.
                    var smart = CreateSmart(worker.Tag, mineralTag);
                    if (smart != null)
                    {
                        actions.Add(smart);
                        worker.SmartIssued = true;
                        Console.WriteLine($"[F0TEST] {role} SMART f{offset} (forced f{SmartForceFrame}) pos=({live.Position.X:F2},{live.Position.Y:F2}) d={anchorDist:F2} -> mineral {mineralTag}");
                    }
                }
                else if (anchorDist <= SmartRangeRadius)
                {
                    var smart = CreateSmart(worker.Tag, mineralTag);
                    if (smart != null) actions.Add(smart);
                    worker.SmartIssued = true;
                    Console.WriteLine($"[F0TEST] {role} SMART f{offset} pos=({live.Position.X:F2},{live.Position.Y:F2}) d={anchorDist:F2} -> mineral {mineralTag}");
                }
                else if (dist > 0.001f)
                {
                    if (offset == DirectMoveFrameA || offset == DirectMoveFrameB
                        || (isSwitchRoller && offset == switchN))
                    {
                        // Owner spec 2026-11-01 09:03: at f13 and f16 ALL SEVEN
                        // (rollers included) get a direct MOVE onto the TA/YA
                        // SMH point instead of the rolling 2u waypoint.
                        // --f0switch=N adds the switch-frame direct move for T1.
                        var direct = CreateMove(worker.Tag, anchorX, anchorY);
                        if (direct != null) actions.Add(direct);
                        if (isSwitchRoller && offset == switchN)
                        {
                            Console.WriteLine($"[F0TEST] {role} SWITCH-MOVE f{offset} -> TA SMH ({anchorX:F2},{anchorY:F2}) pos=({live.Position.X:F2},{live.Position.Y:F2}) d={anchorDist:F2}");
                        }
                    }
                    else
                    {
                        // Homing rolling waypoint: current position + 2u toward the
                        // (via-)anchor — permanent transit, no arrival deceleration,
                        // and self-healing after any deflection.
                        var move = CreateMove(worker.Tag,
                            live.Position.X + dx / dist * RollingStep,
                            live.Position.Y + dy / dist * RollingStep);
                        if (move != null) actions.Add(move);
                    }
                }
            }
            else if (anchorDist <= ArrivedStandRadius)
            {
                worker.Arrived = true;
                worker.ArriveFrame = offset;
                Console.WriteLine($"[F0TEST] {role} ARRIVED f{offset} pos=({live.Position.X:F2},{live.Position.Y:F2}) d={anchorDist:F2} — stand radius");
            }

            if (offset <= FailOpenFrame)
            {
                Console.WriteLine($"[F0TEST] {role} f{offset} pos=({live.Position.X:F2},{live.Position.Y:F2}) dAnchor={anchorDist:F2} ab={ability}{(worker.SmartIssued ? " smarted" : "")}");
            }
        }

        // ── mineral-walk lock ──

        // Stay-on-task (owner 2026-10-31 16:40): a worker must stay on its
        // assignment until the roles suicide — never turn around at a
        // midpoint. Mineral-walk phases re-assert the SMART whenever the
        // order leaves the walk mineral (the engine peels busy-mineral
        // waiters off after ~15 frames). Move phases issue the MOVE once and
        // re-issue only if the worker gets peeled onto another mineral.
        private readonly Dictionary<string, int> _walkerReasserts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private void TickChoreo(string role, int offset, List<SC2Action> actions)
        {
            if (!_workers.TryGetValue(role, out var worker)) return;
            if (_sixSuicideTriggered || offset > FailOpenFrame) return;
            if (LiveWorker(worker.Tag) == null) return;
            if (!ActiveBooks.TryGetValue(role, out var book)) return;

            // Latest phase whose start frame has passed.
            var phaseIndex = -1;
            for (var i = 0; i < book.Length; i++)
            {
                if (offset >= book[i].StartFrame) phaseIndex = i;
            }
            if (phaseIndex < 0) return;

            if (!_choreoPhaseIndex.TryGetValue(role, out var active) || active != phaseIndex)
            {
                _choreoPhaseIndex[role] = phaseIndex;
                _moveIssued[role] = false;
                var phase = book[phaseIndex];
                var desc = phase.Kind == ChoreoKind.MineralWalk
                    ? $"mineral walk -> {phase.AnchorLabel}"
                    : phase.Kind == ChoreoKind.FollowRole
                        ? $"FOLLOW {phase.FollowRole} (f{phase.StartFrame}-{(phase.EndFrame >= 0 ? phase.EndFrame.ToString() : "open")})"
                        : phase.Kind == ChoreoKind.SmartTo
                            ? $"SMART -> {phase.AnchorLabel}"
                            : $"MOVE -> {phase.AnchorLabel}";
                Console.WriteLine($"[F0TEST] {role} phase {phaseIndex} f{offset}: {desc}");
            }

            var current = book[phaseIndex];
            if (current.Kind == ChoreoKind.FollowRole)
            {
                // Owner spec 2026-11-01 08:21: frames 13..17 the movers follow
                // the roller — EVERY frame a fresh MOVE to the roller's current
                // position (deliberate collision course for the push). After
                // EndFrame the phase goes quiet until the next phase.
                //
                // 2026-11-02 fix (v3): target the roller's live position + 2u
                // toward the roller's OWN anchor, not the raw position. The
                // 09:55 run showed raw-position follow decelerates the bumper
                // to ~0.11 (short-range arrival braking) and it never
                // re-contacts; the 2u-past waypoint keeps it at full speed
                // plowing through the roller's position — same rolling-
                // waypoint principle as the roller itself.
                if (current.EndFrame >= 0 && offset > current.EndFrame) return;
                if (current.FollowRole == null
                    || !_workers.TryGetValue(current.FollowRole, out var lead))
                {
                    return;
                }
                var leadLive = LiveWorker(lead.Tag);
                if (leadLive?.Position == null) return;
                var tx = leadLive.Position.X;
                var ty = leadLive.Position.Y;
                if (RollerAnchor(current.FollowRole, out var rax, out var ray))
                {
                    var rdx = rax - tx;
                    var rdy = ray - ty;
                    var rd = (float)Math.Sqrt(rdx * rdx + rdy * rdy);
                    if (rd > 0.001f)
                    {
                        tx += rdx / rd * RollingStep;
                        ty += rdy / rd * RollingStep;
                    }
                }
                var followMove = CreateMove(worker.Tag, tx, ty);
                if (followMove != null) actions.Add(followMove);
                return;
            }

            MineralTarget min = null;
            if (current.Kind != ChoreoKind.MoveToPoint
                && !_mineralTargets.TryGetValue(current.AnchorLabel, out min))
            {
                return;
            }

            var (ability, target, _) = OrderInfo(worker.Tag);
            // SmartTo (owner spec 2026-11-01 09:03, frame-19 SMARTs) reuses the
            // walk-lock logic: SMART onto the assigned mineral and keep the
            // worker locked to it if the engine peels it away.
            if (current.Kind == ChoreoKind.MineralWalk || current.Kind == ChoreoKind.SmartTo)
            {
                var onTask = target == min.Tag && (ability == 1183 || ability == (int)Abilities.SMART);
                if (!onTask)
                {
                    var smart = CreateSmart(worker.Tag, min.Tag);
                    if (smart != null)
                    {
                        actions.Add(smart);
                        _walkerReasserts.TryGetValue(role, out var count);
                        _walkerReasserts[role] = count + 1;
                        if (count == 0)
                        {
                            var verb = current.Kind == ChoreoKind.SmartTo ? "SMART lock" : "walk re-assert";
                            Console.WriteLine($"[F0TEST] {role} {verb} f{offset} ab={ability} target={target} -> mineral {current.AnchorLabel} ({min.Tag})");
                        }
                    }
                }
            }
            else
            {
                // MoveToAnchor / MoveToPoint / MoveToCenter: issue once;
                // re-issue only if peeled onto a mineral (the engine
                // re-targeted the MOVE).
                float mx, my;
                ulong mtag = 0;
                if (current.Kind == ChoreoKind.MoveToPoint)
                {
                    mx = worker.StartX + current.PointDX;
                    my = worker.StartY + current.PointDY;
                }
                else if (current.Kind == ChoreoKind.MoveToCenter)
                {
                    mx = min.CenterX; my = min.CenterY;
                }
                else
                {
                    mx = min.AnchorX; my = min.AnchorY; mtag = min.Tag;
                }
                var peeled = mtag != 0 && target != 0 && target != mtag && (ability == 1183 || ability == 1184);
                if (!_moveIssued[role] || peeled)
                {
                    var move = CreateMove(worker.Tag, mx, my);
                    if (move != null)
                    {
                        actions.Add(move);
                        _moveIssued[role] = true;
                        if (!peeled)
                        {
                            Console.WriteLine($"[F0TEST] {role} MOVE f{offset} -> {(current.Kind == ChoreoKind.MoveToPoint ? $"point ({mx:F2},{my:F2})" : current.Kind == ChoreoKind.MoveToCenter ? $"{current.AnchorLabel} center ({mx:F2},{my:F2})" : $"{current.AnchorLabel} HP ({mx:F2},{my:F2})")}");
                        }
                    }
                }
            }
        }

        private void IssueSuicide(List<SC2Action> actions, List<ulong> restrictTags, string why)
        {
            if (_hatcheryTag == 0) return;
            List<ulong> tags;
            if (restrictTags != null)
            {
                tags = restrictTags.Where(t => LiveWorker(t) != null).ToList();
            }
            else
            {
                tags = Globals.CurrentObservation.SelfUnits.Values
                    .Where(u => u != null && u.UnitType == (uint)UnitTypes.ZERG_DRONE && u.UnitTag != 0)
                    .Select(u => u.UnitTag)
                    .ToList();
            }
            if (tags.Count == 0) return;
            var command = new ActionRawUnitCommand
            {
                AbilityId = SuicideAttackAbilityId,
                TargetUnitTag = _hatcheryTag,
                QueueCommand = false
            };
            command.UnitTags.AddRange(tags);
            actions.Add(new SC2Action { ActionRaw = new ActionRaw { UnitCommand = command } });
        }

        private void LogRadius(string role, int radiusFrame)
        {
            if (!_workers.TryGetValue(role, out var worker)) return;
            if (!worker.RadiusLogged.Add(radiusFrame)) return;
            var live = LiveWorker(worker.Tag);
            if (live?.Position == null) return;
            var dx = live.Position.X - worker.StartX;
            var dy = live.Position.Y - worker.StartY;
            var r = Math.Sqrt(dx * dx + dy * dy);
            Console.WriteLine($"[F0TEST] RADIUS {role} f{radiusFrame} pos=({live.Position.X:F2},{live.Position.Y:F2}) r={r:F3}");
        }

        // ── init ──

        private OrderedMineral FindPatchMineral(System.Collections.Generic.IEnumerable<TeamPatchAssignmentDto> assignments, string label)
        {
            if (assignments == null) return null;
            foreach (var assignment in assignments)
            {
                var found = assignment?.Minerals?.FirstOrDefault(m =>
                    m != null
                    && (string.Equals(m.FinalLabel, label, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m.Label, label, StringComparison.OrdinalIgnoreCase)));
                if (found != null) return found;
            }
            return null;
        }

        private sealed class MineralTarget
        {
            public ulong Tag;
            public float AnchorX, AnchorY;   // SM harvest point (MOVE target)
            public float CenterX, CenterY;   // mineral center (mineral-walk target)
        }

        private readonly Dictionary<string, MineralTarget> _mineralTargets =
            new Dictionary<string, MineralTarget>(StringComparer.OrdinalIgnoreCase);

        private bool TryInitialize(ObservationSnapshotDto snapshot, int frame)
        {
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            var mapData = Globals.CurrentMapData ?? _mapData;
            if (startIndex < 0 || mapData == null) return false;
            var assignments = mapData.TeamPatchAssignments?.ElementAtOrDefault(startIndex);
            var hatchery = mapData.StartingTownHall != null && startIndex < mapData.StartingTownHall.Length
                ? mapData.StartingTownHall[startIndex]
                : null;
            if (assignments == null || hatchery == null) return false;

            OrderedMineral FindMineral(string label) => FindPatchMineral(assignments, label);

            // All minerals the choreography touches: roller anchors + movers.
            _mineralTargets.Clear();
            foreach (var label in new[] { "TA", "YA", "SA", "BA", "BB", "TB", "YB" })
            {
                var m = FindMineral(label);
                if (m?.UnitTag == 0 || m?.Position == null)
                {
                    if (frame % 10 == 0) Console.WriteLine($"[F0TEST] waiting for mineral {label} f{frame}");
                    return false;
                }
                var anchor = HasPoint(m.SmHarvestPoint) ? m.SmHarvestPoint : (HasPoint(m.HarvestPoint) ? m.HarvestPoint : m.Position);
                _mineralTargets[label] = new MineralTarget
                {
                    Tag = m.UnitTag,
                    AnchorX = anchor.X,
                    AnchorY = anchor.Y,
                    CenterX = m.Position.X,
                    CenterY = m.Position.Y,
                };
            }

            _taTag = _mineralTargets["TA"].Tag;
            _yaTag = _mineralTargets["YA"].Tag;
            _taAnchorX = _mineralTargets["TA"].AnchorX; _taAnchorY = _mineralTargets["TA"].AnchorY;
            _yaAnchorX = _mineralTargets["YA"].AnchorX; _yaAnchorY = _mineralTargets["YA"].AnchorY;

            // Hatchery tag from live obs (the dat point has no tag).
            var hatchLive = snapshot.SelfUnits.Values.FirstOrDefault(u => u != null && u.UnitType == (uint)UnitTypes.ZERG_HATCHERY);
            _hatcheryTag = hatchLive?.UnitTag ?? 0;
            if (_hatcheryTag == 0)
            {
                if (frame % 10 == 0) Console.WriteLine($"[F0TEST] waiting for hatchery f{frame}");
                return false;
            }
            _hatcheryX = hatchLive.Position.X;
            _hatcheryY = hatchLive.Position.Y;

            var assignedWorkers = mapData.AssignedWorkers?.ElementAtOrDefault(startIndex);
            if (assignedWorkers == null)
            {
                if (frame % 10 == 0) Console.WriteLine($"[F0TEST] waiting for worker assignments f{frame}");
                return false;
            }

            foreach (var role in _telemetryRoles.Concat(_suicideZeroRoles))
            {
                var record = assignedWorkers.FirstOrDefault(candidate =>
                    candidate != null && string.Equals(candidate.Role, role, StringComparison.OrdinalIgnoreCase));
                if (record == null || record.UnitID == 0)
                {
                    if (frame % 10 == 0) Console.WriteLine($"[F0TEST] waiting for role {role} f{frame}");
                    return false;
                }
                var live = snapshot.SelfUnits.TryGetValue(record.UnitID, out var unit) ? unit : null;
                if (live?.Position == null)
                {
                    if (frame % 10 == 0) Console.WriteLine($"[F0TEST] waiting for live unit {role} tag={record.UnitID} f{frame}");
                    return false;
                }
                _workers[role] = new F0Worker
                {
                    Role = role,
                    Tag = record.UnitID,
                    StartX = live.Position.X,
                    StartY = live.Position.Y,
                    StartZ = live.Position.Z,
                };
            }

            _suicideZeroTags.Clear();
            _suicideZeroTags.AddRange(_suicideZeroRoles.Select(r => _workers[r].Tag));

            _initFrame = frame;
            _initialized = true;
            if (Settings.F0SwitchFrame >= 0)
            {
                // Bump point: T4's measured f13 mineral-walk position = its
                // start + 0.872u (the measured f13 radius) along the heading
                // to the SA center. SE seed=3: (127.163, 20.933).
                var t4 = _workers["T4"];
                var sa = _mineralTargets["SA"];
                var bdx = sa.CenterX - t4.StartX;
                var bdy = sa.CenterY - t4.StartY;
                var bd = (float)Math.Sqrt(bdx * bdx + bdy * bdy);
                if (bd > 0.001f)
                {
                    _bumpX = t4.StartX + bdx / bd * 0.872f;
                    _bumpY = t4.StartY + bdy / bd * 0.872f;
                }
                // Fixed thru-bearing: T1 start -> bump point, extended past.
                var t1w = _workers["T1"];
                var tdx = _bumpX - t1w.StartX;
                var tdy = _bumpY - t1w.StartY;
                var td = (float)Math.Sqrt(tdx * tdx + tdy * tdy);
                if (td > 0.001f)
                {
                    _thruX = tdx / td;
                    _thruY = tdy / td;
                }
                Console.WriteLine($"[F0TEST] --f0switch={Settings.F0SwitchFrame}{(Settings.F0SwitchControl ? " CONTROL (T4/T5 hold)" : "")}: T1 rolls THRU bump point ({_bumpX:F2},{_bumpY:F2}) bearing=({_thruX:F3},{_thruY:F3}) until f{Settings.F0SwitchFrame}, then T1/T4/T5 MOVE to TA SMH");
            }
            Console.WriteLine($"[F0TEST] initialized f{frame} start={startIndex} hatchery={_hatcheryTag}");
            foreach (var role in _telemetryRoles)
            {
                var w = _workers[role];
                Console.WriteLine($"[F0TEST] start {role} tag={w.Tag} ({w.StartX:F2},{w.StartY:F2})");
            }
            Console.WriteLine($"[F0TEST] TA anchor ({_taAnchorX:F2},{_taAnchorY:F2}) tag={_taTag}; YA anchor ({_yaAnchorX:F2},{_yaAnchorY:F2}) tag={_yaTag}");
            foreach (var label in _mineralTargets.Keys)
            {
                var mt = _mineralTargets[label];
                Console.WriteLine($"[F0TEST] {label} tag={mt.Tag} center=({mt.CenterX:F2},{mt.CenterY:F2}) anchor=({mt.AnchorX:F2},{mt.AnchorY:F2})");
            }
            return true;
        }

        // ── drawing ──

        // Phase-aware guide lines (owner spec 2026-10-31 19:13): each mover
        // gets a static start -> current-phase-target line — purple T5/Y4,
        // yellow Y5, white T4/B2 for MOVE phases; RED for mineral-walk phases
        // (the walk targets the mineral center). Plus the T1/Y1 SMH lines and
        // the start-centered 10/13/16/19 telemetry circles.
        private void DrawAnnotations(int offset)
        {
            if (!ManagerDebugService.IsDebugEnabled || !_initialized) return;

            var t1 = _workers["T1"];
            var y1 = _workers["Y1"];

            // T1 / Y1 target lines (static, start -> SMH anchor). With
            // --f0switch=N active, T1's line tracks its real roll target:
            // the bump point before frame N, the TA anchor from N on.
            var t1LineX = _taAnchorX;
            var t1LineY = _taAnchorY;
            if (Settings.F0SwitchFrame >= 0 && offset < Settings.F0SwitchFrame)
            {
                t1LineX = _bumpX; t1LineY = _bumpY;
            }
            DrawGroundLine(t1.StartX, t1.StartY, t1.StartZ, t1LineX, t1LineY, Teal);
            DrawGroundLine(y1.StartX, y1.StartY, y1.StartZ, _yaAnchorX, _yaAnchorY, Yellow);

            foreach (var role in new[] { "T5", "Y4", "Y5", "T4", "B2" })
            {
                if (!_workers.TryGetValue(role, out var worker)) continue;
                if (!ActiveBooks.TryGetValue(role, out var book)) continue;
                var phaseIndex = -1;
                for (var i = 0; i < book.Length; i++)
                {
                    if (offset >= book[i].StartFrame) phaseIndex = i;
                }
                if (phaseIndex < 0) continue;
                var phase = book[phaseIndex];
                if (phase.Kind == ChoreoKind.FollowRole)
                {
                    // Follow window (owner 2026-11-01 08:21): white line from
                    // the mover's start to the followed roller's CURRENT
                    // position, redrawn every frame. Nothing after EndFrame.
                    if (phase.EndFrame >= 0 && offset > phase.EndFrame) continue;
                    if (phase.FollowRole == null
                        || !_workers.TryGetValue(phase.FollowRole, out var lead)) continue;
                    var leadLive = LiveWorker(lead.Tag);
                    if (leadLive?.Position == null) continue;
                    DrawGroundLine(worker.StartX, worker.StartY, worker.StartZ,
                        leadLive.Position.X, leadLive.Position.Y, White);
                    continue;
                }
                if (phase.Kind == ChoreoKind.MoveToPoint)
                {
                    // Hold-point phase: white line to own start + delta.
                    DrawGroundLine(worker.StartX, worker.StartY, worker.StartZ,
                        worker.StartX + phase.PointDX, worker.StartY + phase.PointDY, White);
                    continue;
                }
                if (!_mineralTargets.TryGetValue(phase.AnchorLabel, out var min)) continue;

                var isWalk = phase.Kind == ChoreoKind.MineralWalk;
                // Owner convention (2026-10-27 + 2026-11-01 07:30): WHITE line
                // while moving (to the phase's move target = SM harvest point),
                // RED while mineral walking OR on a frame-19 SMART lock (both
                // target the mineral unit center). MoveToCenter (2026-11-03):
                // a MOVE, so WHITE, but aimed at the mineral CENTER.
                var isMineralTarget = isWalk || phase.Kind == ChoreoKind.SmartTo;
                var color = isMineralTarget ? Red : White;
                var toCenter = isMineralTarget || phase.Kind == ChoreoKind.MoveToCenter;
                var tx = toCenter ? min.CenterX : min.AnchorX;
                var ty = toCenter ? min.CenterY : min.AnchorY;
                DrawGroundLine(worker.StartX, worker.StartY, worker.StartZ, tx, ty, color);
            }

            // Start-centered telemetry radii circles (frames 10/13/16/19).
            foreach (var worker in _workers.Values)
            {
                if (!F0CircleRadii.TryGetValue(worker.Role, out var radii)) continue;
                var color = CircleColor(worker.Role);
                foreach (var radius in radii)
                {
                    if (radius <= 0.01f) continue;
                    DrawGroundCircle(worker.StartX, worker.StartY, worker.StartZ, radius, color);
                }
            }
        }

        private static Color CircleColor(string role)
        {
            if (role.Equals("T1", StringComparison.OrdinalIgnoreCase)) return Teal;
            if (role.Equals("Y1", StringComparison.OrdinalIgnoreCase)) return Yellow;
            if (role.Equals("Y5", StringComparison.OrdinalIgnoreCase)) return Yellow;
            if (role.Equals("T5", StringComparison.OrdinalIgnoreCase) || role.Equals("Y4", StringComparison.OrdinalIgnoreCase)) return Purple;
            return White; // B2, T4
        }

        private static void DrawGroundLine(float x1, float y1, float z1, float x2, float y2, Color color)
        {
            ManagerDebugService.DrawLine(
                new Point { X = x1, Y = y1, Z = z1 },
                new Point { X = x2, Y = y2, Z = z1 },
                color);
        }

        private static void DrawGroundCircle(float cx, float cy, float cz, float radius, Color color, int segments = 28)
        {
            var step = Math.PI * 2.0 / segments;
            Point? previous = null;
            for (var i = 0; i <= segments; i++)
            {
                var angle = i * step;
                var point = new Point
                {
                    X = cx + (float)(Math.Cos(angle) * radius),
                    Y = cy + (float)(Math.Sin(angle) * radius),
                    Z = cz
                };
                if (previous != null) ManagerDebugService.DrawLine(previous, point, color);
                previous = point;
            }
        }

        // ── helpers ──

        // The anchor a followed roller is rolling toward (for the 2u-past
        // follow waypoint). Only the two rollers have anchors.
        private bool RollerAnchor(string role, out float ax, out float ay)
        {
            if (role.Equals("T1", StringComparison.OrdinalIgnoreCase))
            {
                ax = _taAnchorX; ay = _taAnchorY; return true;
            }
            if (role.Equals("Y1", StringComparison.OrdinalIgnoreCase))
            {
                ax = _yaAnchorX; ay = _yaAnchorY; return true;
            }
            ax = 0; ay = 0; return false;
        }

        // E-series via-point (2026-11-03): until the until-frame the roller
        // homes on start + ViaDist at (bearing-to-anchor + ViaDeltaDeg);
        // afterwards (or when delta is 0) the true anchor. Angle deltas are
        // relative to the roll bearing so the same value works at both starts.
        private (float, float) RollerTarget(string role, F0Worker worker, float anchorX, float anchorY, int offset)
        {
            double deltaDeg, dist;
            int until;
            if (role.Equals("T1", StringComparison.OrdinalIgnoreCase))
            {
                deltaDeg = Settings.F0T1ViaDeltaDeg; dist = Settings.F0T1ViaDist; until = Settings.F0T1ViaUntilFrame;
            }
            else if (role.Equals("Y1", StringComparison.OrdinalIgnoreCase))
            {
                deltaDeg = Settings.F0Y1ViaDeltaDeg; dist = Settings.F0Y1ViaDist; until = Settings.F0Y1ViaUntilFrame;
            }
            else
            {
                return (anchorX, anchorY);
            }
            if (deltaDeg == 0.0 || offset >= until)
            {
                return (anchorX, anchorY);
            }
            var bearing = Math.Atan2(anchorY - worker.StartY, anchorX - worker.StartX) + deltaDeg * Math.PI / 180.0;
            return (worker.StartX + (float)(Math.Cos(bearing) * dist),
                    worker.StartY + (float)(Math.Sin(bearing) * dist));
        }

        private static bool HasPoint(Vector2Dto point) =>
            point != null && (Math.Abs(point.X) > 0.001f || Math.Abs(point.Y) > 0.001f);

        private WorkerEntryDto LiveWorker(ulong tag)
        {
            if (tag == 0 || Globals.CurrentObservation?.SelfUnits == null) return null;
            return Globals.CurrentObservation.SelfUnits.TryGetValue(tag, out var worker) ? worker : null;
        }

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
    }
}
