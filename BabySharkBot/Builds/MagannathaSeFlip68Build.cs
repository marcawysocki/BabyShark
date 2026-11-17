using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using BabySharkBot.Setup;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// "Magannatha SE flip68" (owner spec 2026-11-16 19:06 + 19:11): the build
    /// writes every worker's instruction book; the mining manager ONLY executes
    /// the books (all PatternJIT side-drivers and the old EarlySuicide harness
    /// are gated off by Settings.Flip68BuildMode). No production, no buildings,
    /// no helpers — the book IS the choreography, and the suicide is a plain
    /// book row ("at some condition do something", condition = T2's TB flip).
    ///
    /// Canon choreography (owner-verified telemetry 2026-11-16, seed 3 SE):
    ///   W2 (1-T3 label in the assignment layer — canon T1): f0 SMART walk->BB,
    ///       f7 MOVE (127.5,19.1), f13 SMART->TA, flips TA f69.
    ///   W3 (canon T2): f0 MOVE (124.5,21.5), f8 MOVE (128.5,18.1),
    ///       f24 SMART->TB, flips TB f79 — the suicide trigger.
    ///   W1 (canon T3): f9 MOVE to the TA wait point (TA SMH + 0.4u toward the
    ///       hatchery), stands on a gate-wait until W2 flips (f69), one takeover
    ///       SMART onto TA, then the TA loop.
    ///   W4 (canon S3): f15 MOVE to the owner's EXACT point (spec 18:19) —
    ///       SA mineral (130.00,18.50) at 0.85W / 1.5N = (129.15,20.00),
    ///       measured from the MINERAL center — parked behind the SA
    ///       harvester, ready to mine — then SMART->SA at relative frame 32,
    ///       then the bank-200 suicide.
    ///   W11/W12 (owner spec 17:20 + 18:19 parks): W11 f0 park at the owner's
    ///       EXACT point — BA mineral (134.00,22.50) at 1.75W / 0.1N =
    ///       (132.25,22.60), measured from the MINERAL — f32 SMART->BA;
    ///       W12 f0 park at the owner's EXACT point — YA mineral (134.00,24.50)
    ///       at 2W / 0.1N = (132.00,24.60), measured from the MINERAL —
    ///       f32 SMART->YA.
    ///   W5 (canon 5-S1, assignment T3, assigned mineral SA): f0 MOVE to the
    ///       2u-W/0.5u-S starting line, f7 MOVE-thru to SA's SMH stand, f13
    ///       SMART->SA (SA flip f69, tied with TA). Engine owns the SA cycle,
    ///       joins the suicide.
    ///   W7 (canon 7-Y3, assigned BA): f0 MOVE 0.5u E / 2u N of its seat,
    ///       f7 MOVE-thru to BA's SMH stand, f13 SMART->BA. Objective: BA
    ///       flip earlier (3 pts).
    ///   W8 (canon 8-Y2): f0 SMART mineral-walk -> SA (crosses W7's path);
    ///       on the contact frame a Move 2u west / 2u south of its contact
    ///       position (90-degree rotation of 4-S3's 2E/2S); then a move-thru
    ///       to BB's SMH stand and SMART->BB (owner spec 2026-11-17 14:42 —
    ///       BB flip = 1 pt, idle until a worker walks back).
    ///   W9/W10 (canon 9-Y1 / 10-B2, owner spec 16:49 yellow pair): W9 f0
    ///       MOVE 0.25u E / 2u N, f9 thru to YB SMH, f15 SMART->YB (YB f83);
    ///       W10 f0 SMART walk->BB, f9 reposition 2W/2S of contact, f15 thru
    ///       to YA SMH, f21 SMART->YA (YA f69).
    ///   The other 1 worker (W6/S2): plain loop on its assigned mineral,
    ///       joins the suicide.
    ///   Suicide: every worker ATTACKs the own hatchery once W3 shows the
    ///       1184 return ability (its TB flip). Book ends after the attack row.
    /// </summary>
    public class MagannathaSeFlip68Build : BabySharkBuild
    {
        private const string Set = "flip68";
        private const int GatherDrone = (int)Abilities.HARVEST_GATHER_DRONE;
        private const int ReturnDrone = (int)Abilities.HARVEST_RETURN_DRONE;

        // Drone cruise speed in units per game frame (p90 measured, champion
        // telemetry 2026-11-14) — used to predict 4-S3's walk position on its
        // contact frame.
        private const float DroneCruisePerFrame = 0.176f;

        // owner spec 2026-11-17 11:54: the frame 4-S3's walk to BA contacts
        // 5-S1 — S3's reposition Move (2u E / 2u S of the contact position)
        // issues on this frame. Swept 3-12 (owner 11:54 re-run): f7/f8 tie
        // at SA flip f70; f7 kept.
        private const int S3ContactFrame = 7;

        // owner spec 2026-11-17 12:19: 5-S1's f0 starting-line offset (the
        // start ANGLE toward SA SMH): this many units west and south of the
        // live f0 seat. Swept 2026-11-17: 3.0/0.5 -> f70, 2.0/0.5 -> f69
        // (confirmed x2, ties TA's f69 flip) — 2.0/0.5 kept.
        private const float S1StartWest = 2f;
        private const float S1StartSouth = 0.5f;

        // owner spec 2026-11-17 16:49: the yellow pair (9-Y1 -> YB, 10-B2
        // walk->BB then -> YA) gets its OWN contact frame so the sweep for
        // best YA/YB flip does not move the proven salmon/blue books.
        // Swept f4-f10: f9 wins big on YA (f70 vs f75+ elsewhere).
        private const int YellowContactFrame = 9;

        // 9-Y1's f0 starting-line offset (north / slightly east of its
        // seat). Swept at f9: E0.25/N2.0 -> YA flip f69 (all four A
        // minerals flip f69; confirmed x2) with YB f83 — kept.
        private const float W9StartEast = 0.25f;
        private const float W9StartNorth = 2f;

        // owner spec 2026-11-17 17:20: the parked stand-ins (11-Y4 on BA,
        // 12-Y5 on YA) SMART their mineral at this relative frame.
        private const int ParkSmartFrame = 32;

        private bool _seeded;
        private bool _seedPendingLogged;

        public MagannathaSeFlip68Build(DefaultSharkyBot defaultSharkyBot) : base(defaultSharkyBot)
        {
        }

        public override void OnStart(int frame)
        {
            base.OnStart(frame);
            _seeded = false;
            _seedPendingLogged = false;
            Console.WriteLine("[FLIP68] build started — waiting for map data + observation to seed all 12 worker books");
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            // Once the books are seeded this build never issues another command:
            // the mining manager's book executor is the only command source.
            if (_seeded)
            {
                return Array.Empty<SC2APIProtocol.Action>();
            }
            if (observation?.Observation?.RawData?.Units == null)
            {
                return Array.Empty<SC2APIProtocol.Action>();
            }

            TrySeed((int)observation.Observation.GameLoop);
            return Array.Empty<SC2APIProtocol.Action>();
        }

        private void TrySeed(int frame)
        {
            var snapshot = Globals.CurrentObservation;
            var mapData = Globals.CurrentMapData;
            if (snapshot?.SelfUnits == null || snapshot.SelfUnits.Count == 0 || mapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (startIndex < 0)
            {
                return;
            }

            var teams = mapData.TeamPatchAssignments.ElementAtOrDefault(startIndex);
            var assigned = mapData.AssignedWorkers.ElementAtOrDefault(startIndex);
            var minerals = mapData.OrderedMainMinerals.ElementAtOrDefault(startIndex);
            if (teams == null || assigned == null || minerals == null)
            {
                return;
            }

            var workersByLabel = new Dictionary<string, WorkerEntryDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var team in teams)
            {
                foreach (var worker in team?.Workers ?? new List<WorkerEntryDto>())
                {
                    if (worker != null && worker.UnitTag != 0 && !string.IsNullOrWhiteSpace(worker.StartLabel))
                    {
                        workersByLabel[worker.StartLabel] = worker;
                    }
                }
            }

            var ta = minerals.FirstOrDefault(m => m != null && string.Equals(m.FinalLabel, "TA", StringComparison.OrdinalIgnoreCase) && m.UnitTag != 0);
            var tb = minerals.FirstOrDefault(m => m != null && string.Equals(m.FinalLabel, "TB", StringComparison.OrdinalIgnoreCase) && m.UnitTag != 0);
            var bb = minerals.FirstOrDefault(m => m != null && string.Equals(m.FinalLabel, "BB", StringComparison.OrdinalIgnoreCase) && m.UnitTag != 0);
            var ba = minerals.FirstOrDefault(m => m != null && string.Equals(m.FinalLabel, "BA", StringComparison.OrdinalIgnoreCase) && m.UnitTag != 0);
            var sa = minerals.FirstOrDefault(m => m != null && string.Equals(m.FinalLabel, "SA", StringComparison.OrdinalIgnoreCase) && m.UnitTag != 0);
            var ya = minerals.FirstOrDefault(m => m != null && string.Equals(m.FinalLabel, "YA", StringComparison.OrdinalIgnoreCase) && m.UnitTag != 0);
            var yb = minerals.FirstOrDefault(m => m != null && string.Equals(m.FinalLabel, "YB", StringComparison.OrdinalIgnoreCase) && m.UnitTag != 0);
            if (ta == null || tb == null || bb == null || ba == null || sa == null || ya == null || yb == null)
            {
                LogPending(frame, $"minerals not resolved yet (TA={(ta == null ? "missing" : "ok")} TB={(tb == null ? "missing" : "ok")} BB={(bb == null ? "missing" : "ok")} BA={(ba == null ? "missing" : "ok")} SA={(sa == null ? "missing" : "ok")} YA={(ya == null ? "missing" : "ok")} YB={(yb == null ? "missing" : "ok")})");
                return;
            }

            if (!workersByLabel.TryGetValue("W1", out var w1)
                || !workersByLabel.TryGetValue("W2", out var w2)
                || !workersByLabel.TryGetValue("W3", out var w3)
                || !workersByLabel.TryGetValue("W4", out var w4)
                || !workersByLabel.TryGetValue("W5", out var w5)
                || !workersByLabel.TryGetValue("W7", out var w7)
                || !workersByLabel.TryGetValue("W8", out var w8)
                || !workersByLabel.TryGetValue("W9", out var w9)
                || !workersByLabel.TryGetValue("W10", out var w10)
                || !workersByLabel.TryGetValue("W11", out var w11)
                || !workersByLabel.TryGetValue("W12", out var w12))
            {
                LogPending(frame, "worker labels W1-W5/W7-W12 not all resolved yet");
                return;
            }

            var hatchery = snapshot.SelfUnits.Values
                .Where(u => u != null && u.UnitType == (uint)UnitTypes.ZERG_HATCHERY)
                .OrderBy(u => DistanceSquared(u.Position, ta.Position))
                .FirstOrDefault();
            if (hatchery == null || hatchery.UnitTag == 0 || hatchery.Position == null)
            {
                LogPending(frame, "own hatchery not observed yet");
                return;
            }

            var hatchTag = hatchery.UnitTag;
            var taTag = ta.UnitTag;
            var tbTag = tb.UnitTag;
            var bbTag = bb.UnitTag;
            var baTag = ba.UnitTag;
            var saTag = sa.UnitTag;
            var yaTag = ya.UnitTag;
            var ybTag = yb.UnitTag;

            // W1's takeover stand point: TA SM harvest point + 0.4u toward the
            // hatchery (owner spec — T3 waits in the red box until T1 finishes).
            var dx = hatchery.Position.X - ta.SmHarvestPoint.X;
            var dy = hatchery.Position.Y - ta.SmHarvestPoint.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0.001)
            {
                LogPending(frame, "TA SM harvest point degenerate — cannot compute wait point");
                return;
            }
            var waitX = ta.SmHarvestPoint.X + (float)(dx / len * 0.4);
            var waitY = ta.SmHarvestPoint.Y + (float)(dy / len * 0.4);

            // W5 (canon 5-S1, assignment T3) SA approach geometry (owner spec
            // 2026-11-17 10:09 + 11:54): starting line 0.5u south / 3u west of
            // its live f0 position; the move-thru target is SA's SMH stand
            // itself ("it should have a move thru to SA SMH").
            if (!snapshot.SelfUnits.TryGetValue(w5.UnitTag, out var w5Unit) || w5Unit.Position == null)
            {
                LogPending(frame, "W5 (5-S1) live position not observed yet");
                return;
            }
            var s1StartX = w5Unit.Position.X - S1StartWest;
            var s1StartY = w5Unit.Position.Y - S1StartSouth;
            var s1ThruX = sa.SmHarvestPoint.X;
            var s1ThruY = sa.SmHarvestPoint.Y;

            // owner spec 2026-11-17 17:27: 4-S3 no longer walks to BA. At f15
            // it Moves to 1u north / 0.7u west of SA's SMH stand — parked
            // and ready — and SMARTs SA at relative frame 32 (the W11/W12
            // parked stand-in pattern).
            // owner spec 2026-11-17 18:19: EXACT point from the owner —
            // SA mineral center (130.00,18.50) at 0.85W / 1.5N = (129.15,20.00).
            // Offsets measured from the MINERAL, not SMH. Do not re-derive.
            var s3ParkX = sa.Position.X - 0.85f;
            var s3ParkY = sa.Position.Y + 1.5f;

            // Blue-team 90-degree rotation of the 5-S1/4-S3 pattern (owner
            // spec 2026-11-17 14:36): W8 (canon 8-Y2) opens with a mineral
            // walk to SA — its transparent walk crosses W7's path in the
            // south corridor; W7 (canon 7-Y3, assigned BA) starts NORTH and
            // slightly east of its seat, move-thru to BA's SMH stand on the
            // contact frame, SMART->BA. Objectives: BA flip earlier (3 pts),
            // BB flip (1 pt) — "the faster we are on A minerals".
            if (!snapshot.SelfUnits.TryGetValue(w7.UnitTag, out var w7Unit) || w7Unit.Position == null)
            {
                LogPending(frame, "W7 (7-Y3) live position not observed yet");
                return;
            }
            if (!snapshot.SelfUnits.TryGetValue(w8.UnitTag, out var w8Unit) || w8Unit.Position == null)
            {
                LogPending(frame, "W8 (8-Y2) live position not observed yet");
                return;
            }
            var w7StartX = w7Unit.Position.X + 0.5f; // slightly east
            var w7StartY = w7Unit.Position.Y + 2f;   // 2u north
            var w7ThruX = ba.SmHarvestPoint.X;
            var w7ThruY = ba.SmHarvestPoint.Y;
            var w8Wx = sa.HarvestPoint.X - w8Unit.Position.X;
            var w8Wy = sa.HarvestPoint.Y - w8Unit.Position.Y;
            var w8Wlen = Math.Sqrt(w8Wx * w8Wx + w8Wy * w8Wy);
            if (w8Wlen <= 0.001)
            {
                LogPending(frame, "SA harvest point degenerate — cannot compute 8-Y2 contact point");
                return;
            }
            var w8ContactX = w8Unit.Position.X + (float)(w8Wx / w8Wlen * DroneCruisePerFrame * S3ContactFrame);
            var w8ContactY = w8Unit.Position.Y + (float)(w8Wy / w8Wlen * DroneCruisePerFrame * S3ContactFrame);
            // Rotated 90° from S3's (+2E/-2S): pull the walker back south-
            // west, clear of W7's north-east BA lane.
            var w8MoveX = w8ContactX - 2f; // 2u west of the contact position
            var w8MoveY = w8ContactY - 2f; // 2u south of the contact position
            // owner spec 2026-11-17 14:42: after the bump frame W8 moves
            // THRU to BB's SMH stand and mines BB (the 1-point objective —
            // BB is idle until a worker walks back).
            var w8ThruX = bb.SmHarvestPoint.X;
            var w8ThruY = bb.SmHarvestPoint.Y;

            // Yellow-team rotation, one seat south (owner spec 2026-11-17
            // 16:49): W9 (canon 9-Y1) mirrors W7/3-T2 — f0 N/slightly-E
            // start, move-thru to YB's SMH on the contact frame, SMART->YB
            // as soon as it can. W10 (canon 10-B2) mirrors W8 — f0 mineral
            // walk to BB (crosses W9's path), reposition 2u W / 2u S of
            // contact on the bump frame, then move-thru to YA's SMH and
            // SMART->YA as soon as possible. W11 (11-Y4) and W12 (12-Y5)
            // open on mineral walks to BB — sandwich pushers, telemetry
            // first (owner wants to watch what their walks do).
            if (!snapshot.SelfUnits.TryGetValue(w9.UnitTag, out var w9Unit) || w9Unit.Position == null)
            {
                LogPending(frame, "W9 (9-Y1) live position not observed yet");
                return;
            }
            if (!snapshot.SelfUnits.TryGetValue(w10.UnitTag, out var w10Unit) || w10Unit.Position == null)
            {
                LogPending(frame, "W10 (10-B2) live position not observed yet");
                return;
            }
            if (!snapshot.SelfUnits.TryGetValue(w11.UnitTag, out var w11Unit) || w11Unit.Position == null)
            {
                LogPending(frame, "W11 (11-Y4) live position not observed yet");
                return;
            }
            if (!snapshot.SelfUnits.TryGetValue(w12.UnitTag, out var w12Unit) || w12Unit.Position == null)
            {
                LogPending(frame, "W12 (12-Y5) live position not observed yet");
                return;
            }
            // owner spec 2026-11-17 17:20: W11 parks 2u W / 0.1u N of BA's
            // SMH stand, W12 parks 2u W / 0.1u N of YA's SMH stand; each
            // SMARTs its mineral at relative frame 32 (supersedes the
            // 17:08 3S/1E start and the BB walk telemetry).
            // owner spec 2026-11-17 18:19: EXACT point from the owner — BA
            // mineral center (134.00,22.50) at 1.75W / 0.1N = (132.25,22.60).
            // Offsets measured from the MINERAL, not SMH. Do not re-derive.
            var w11StartX = ba.Position.X - 1.75f;
            var w11StartY = ba.Position.Y + 0.1f;
            // owner spec 2026-11-17 18:26: EXACT point from the owner — YA
            // mineral center (134.00,24.50) at 2W / 0.1N = (132.00,24.60).
            // Offsets measured from the MINERAL, not SMH (same convention as
            // the W11 / 4-S3 parks). Do not re-derive.
            var w12StartX = ya.Position.X - 2f;
            var w12StartY = ya.Position.Y + 0.1f;
            var w9StartX = w9Unit.Position.X + W9StartEast; // slightly east
            var w9StartY = w9Unit.Position.Y + W9StartNorth; // north
            var w9ThruX = yb.SmHarvestPoint.X;
            var w9ThruY = yb.SmHarvestPoint.Y;
            var w10Wx = bb.HarvestPoint.X - w10Unit.Position.X;
            var w10Wy = bb.HarvestPoint.Y - w10Unit.Position.Y;
            var w10Wlen = Math.Sqrt(w10Wx * w10Wx + w10Wy * w10Wy);
            if (w10Wlen <= 0.001)
            {
                LogPending(frame, "BB harvest point degenerate — cannot compute 10-B2 contact point");
                return;
            }
            var w10ContactX = w10Unit.Position.X + (float)(w10Wx / w10Wlen * DroneCruisePerFrame * YellowContactFrame);
            var w10ContactY = w10Unit.Position.Y + (float)(w10Wy / w10Wlen * DroneCruisePerFrame * YellowContactFrame);
            var w10MoveX = w10ContactX - 2f; // 2u west of the contact position
            var w10MoveY = w10ContactY - 2f; // 2u south of the contact position
            var w10ThruX = ya.SmHarvestPoint.X;
            var w10ThruY = ya.SmHarvestPoint.Y;

            var relativeFrame = Settings.GetRelativeFrame(frame);

            // The four teal books. Gate worker for the suicide = W3's TB flip.
            var w2Book = new List<WorkerInstruction>
            {
                GatherRow(bbTag, 0, noCondition: false),   // 0: f0 SMART walk->BB
                MoveRow(127.5f, 19.1f, 7),                 // 1
                GatherRow(taTag, 13, noCondition: false),  // 2: f13 SMART->TA
                FlipWait(),                                // 3
                ReturnRow(taTag),                          // 4
                HandoffWait(),                             // 5
                GateJump(8),                   // 6: suicide gate (bank 200) -> attack
                LoopJump(2),                               // 7: normal cycle
                AttackRow(hatchTag),           // 8
            };
            var w3Book = new List<WorkerInstruction>
            {
                MoveRow(124.5f, 21.5f, 0),                 // 0
                MoveRow(128.5f, 18.1f, 8),                 // 1
                GatherRow(tbTag, 24, noCondition: false),  // 2: f24 SMART->TB
                FlipWait(),                                // 3
                ReturnRow(tbTag),                          // 4
                HandoffWait(),                             // 5
                GateJump(8),                   // 6: bank-200 suicide gate
                LoopJump(2),                               // 7
                AttackRow(hatchTag),           // 8
            };
            var w1Book = new List<WorkerInstruction>
            {
                MoveRow(waitX, waitY, 9),                  // 0: f9 move to the TA wait point
                GateWait(w2.UnitTag),                      // 1: stand until W2 flips TA
                GatherRow(taTag, -1, noCondition: true),   // 2: takeover SMART (one shot)
                // After the takeover SMART the ENGINE owns the steady
                // gather->return->resume cycle on TA — no flip waits, no
                // return rows, no handoff waits (runs 20261117062849 /
                // 20261117070620 / 20261117072134: every book-driven cycle
                // re-issue fizzled eventually and parked the worker). W2/W3's
                // rf13 choreography cycles stay book-driven — they are the
                // proven heart of this test.
                BankGateWait(),                            // 3: hold until bank 200
                AttackRow(hatchTag),                       // 4
            };
            var w4Book = new List<WorkerInstruction>
            {
                // owner spec 2026-11-17 18:19: f15 Move to the owner's EXACT
                // point — SA mineral (130.00,18.50) at 0.85W / 1.5N =
                // (129.15,20.00), measured from the MINERAL — parked behind
                // the SA harvester, ready — then SMART->SA at rel frame 32.
                MoveRow(s3ParkX, s3ParkY, 15),             // 0: f15 park at the SA stand point
                GatherRow(saTag, ParkSmartFrame, noCondition: false),  // 1: f32 SMART->SA
                BankGateWait(),                            // 2: hold until bank 200
                AttackRow(hatchTag),                       // 3
            };
            // owner spec 2026-11-17 10:09 "test frame 8 to 5": the 5-S1
            // move-thru frame under test. The SMART keeps W2's +6
            // move->smart gap (f7->f13 on the TA book).
            var w5Book = new List<WorkerInstruction>
            {
                MoveRow(s1StartX, s1StartY, 0),            // 0: f0 starting line 0.5S/3W
                MoveRow(s1ThruX, s1ThruY, S3ContactFrame), // 1: move-thru to SA SMH, on the contact frame
                GatherRow(saTag, S3ContactFrame + 6, noCondition: false),  // 2: SMART->SA (W2's +6 move->smart gap)
                BankGateWait(),                            // 3: hold until bank 200
                AttackRow(hatchTag),                       // 4
            };
            // owner spec 2026-11-17 14:36: blue rotation — same frames and
            // angles as the 5-S1/4-S3 pattern. W8 walks to SA and repositions
            // 2u W / 2u S of contact; W7 starts N/slightly-E, move-thru to
            // BA SMH on the contact frame, SMART->BA.
            var w7Book = new List<WorkerInstruction>
            {
                MoveRow(w7StartX, w7StartY, 0),            // 0: f0 N/slightly-E starting line
                MoveRow(w7ThruX, w7ThruY, S3ContactFrame), // 1: move-thru to BA SMH
                GatherRow(baTag, S3ContactFrame + 6, noCondition: false),  // 2: SMART->BA
                BankGateWait(),                            // 3: hold until bank 200
                AttackRow(hatchTag),                       // 4
            };
            var w8Book = new List<WorkerInstruction>
            {
                GatherRow(saTag, 0, noCondition: false),               // 0: f0 SMART walk->SA
                MoveRow(w8MoveX, w8MoveY, S3ContactFrame),             // 1: contact-frame move 2W/2S
                MoveRow(w8ThruX, w8ThruY, S3ContactFrame + 6),         // 2: move-thru to BB SMH
                GatherRow(bbTag, S3ContactFrame + 12, noCondition: false),  // 3: SMART->BB
                BankGateWait(),                                        // 4: hold until bank 200
                AttackRow(hatchTag),                                   // 5
            };
            // owner spec 2026-11-17 16:49: W9 mines YB (N/slightly-E start
            // like 3-T2, then after the bump mine YB as soon as we can); W10
            // walks to BB, and after the bump mines YA as soon as possible.
            var w9Book = new List<WorkerInstruction>
            {
                MoveRow(w9StartX, w9StartY, 0),                            // 0: f0 N/slightly-E starting line
                MoveRow(w9ThruX, w9ThruY, YellowContactFrame),             // 1: move-thru to YB SMH
                GatherRow(ybTag, YellowContactFrame + 6, noCondition: false),  // 2: SMART->YB
                BankGateWait(),                                            // 3: hold until bank 200
                AttackRow(hatchTag),                                       // 4
            };
            var w10Book = new List<WorkerInstruction>
            {
                GatherRow(bbTag, 0, noCondition: false),                   // 0: f0 SMART walk->BB
                MoveRow(w10MoveX, w10MoveY, YellowContactFrame),           // 1: contact-frame move 2W/2S
                MoveRow(w10ThruX, w10ThruY, YellowContactFrame + 6),       // 2: move-thru to YA SMH
                GatherRow(yaTag, YellowContactFrame + 12, noCondition: false),  // 3: SMART->YA
                BankGateWait(),                                            // 4: hold until bank 200
                AttackRow(hatchTag),                                       // 5
            };
            // owner spec 2026-11-17 17:20: W11 parks 2u W / 0.1u N of BA's
            // SMH stand and SMARTs BA at relative frame 32; W12 parks on YA
            // the same way (supersedes the 17:08 3S/1E start and the BB
            // walk telemetry).
            var w11Book = new List<WorkerInstruction>
            {
                MoveRow(w11StartX, w11StartY, 0),          // 0: f0 park at the BA stand point
                GatherRow(baTag, ParkSmartFrame, noCondition: false),  // 1: f32 SMART->BA
                BankGateWait(),                            // 2: hold until bank 200
                AttackRow(hatchTag),                       // 3
            };
            var w12Book = new List<WorkerInstruction>
            {
                MoveRow(w12StartX, w12StartY, 0),          // 0: f0 park at the YA stand point
                GatherRow(yaTag, ParkSmartFrame, noCondition: false),  // 1: f32 SMART->YA
                BankGateWait(),                            // 2: hold until bank 200
                AttackRow(hatchTag),                       // 3
            };

            SeedBook(w2.UnitTag, w2Book, relativeFrame, "W2/canon-T1");
            SeedBook(w3.UnitTag, w3Book, relativeFrame, "W3/canon-T2");
            SeedBook(w1.UnitTag, w1Book, relativeFrame, "W1/canon-T3");
            SeedBook(w4.UnitTag, w4Book, relativeFrame, "W4/canon-S3");
            SeedBook(w5.UnitTag, w5Book, relativeFrame, "W5/5-S1");
            SeedBook(w7.UnitTag, w7Book, relativeFrame, "W7/7-Y3");
            SeedBook(w8.UnitTag, w8Book, relativeFrame, "W8/8-Y2");
            SeedBook(w9.UnitTag, w9Book, relativeFrame, "W9/9-Y1");
            SeedBook(w10.UnitTag, w10Book, relativeFrame, "W10/10-B2");
            SeedBook(w11.UnitTag, w11Book, relativeFrame, "W11/11-Y4");
            SeedBook(w12.UnitTag, w12Book, relativeFrame, "W12/12-Y5");

            // The mining manager resolves Gather points and Return town halls
            // from AssignedWorkers[startIndex].MiningTargets, so the
            // choreographed workers get exact routes for the minerals their
            // books touch. 5-S1's assignment already targets SA and 7-Y3's
            // already targets BA, so only the books whose minerals differ
            // from the assignment (or add a second mineral) need rewrites.
            RewriteTargets(assigned, w1.UnitTag, hatchTag, new[] { ta });
            RewriteTargets(assigned, w2.UnitTag, hatchTag, new[] { bb, ta });
            RewriteTargets(assigned, w3.UnitTag, hatchTag, new[] { tb });
            RewriteTargets(assigned, w4.UnitTag, hatchTag, new[] { sa });
            RewriteTargets(assigned, w8.UnitTag, hatchTag, new[] { sa, bb });
            RewriteTargets(assigned, w9.UnitTag, hatchTag, new[] { yb });
            RewriteTargets(assigned, w10.UnitTag, hatchTag, new[] { bb, ya });
            RewriteTargets(assigned, w11.UnitTag, hatchTag, new[] { ba });
            RewriteTargets(assigned, w12.UnitTag, hatchTag, new[] { ya });

            // The other 1 worker (W6/S2): a plain loop on its already-assigned
            // first mineral plus the same suicide tail.
            var tealTags = new HashSet<ulong> { w1.UnitTag, w2.UnitTag, w3.UnitTag, w4.UnitTag, w5.UnitTag, w7.UnitTag, w8.UnitTag, w9.UnitTag, w10.UnitTag, w11.UnitTag, w12.UnitTag };
            foreach (var assignedWorker in assigned)
            {
                if (assignedWorker == null || assignedWorker.UnitID == 0 || tealTags.Contains(assignedWorker.UnitID))
                {
                    continue;
                }
                var firstTarget = assignedWorker.MiningTargets?.FirstOrDefault(t => t != null && t.ResourceUnitId != 0);
                if (firstTarget == null)
                {
                    Console.WriteLine($"[FLIP68] WARN frame={frame} worker={assignedWorker.UnitID} role={assignedWorker.Role} has no mining target — no book seeded (no invention: assignment data missing)");
                    continue;
                }
                var mineralTag = firstTarget.ResourceUnitId;
                var book = new List<WorkerInstruction>
                {
                    GatherRow(mineralTag, 0, noCondition: false),  // 0: f0 SMART onto the assigned mineral
                    // The ENGINE owns the steady gather->return->resume cycle
                    // from here — no flip waits, no return rows, no loop
                    // jumps, no handoff waits (runs 20261117062849 /
                    // 20261117070620 / 20261117072134: every book-driven
                    // cycle re-issue fizzled eventually — zombie queued
                    // returns, missed deposit edges, heal cooldowns — and
                    // parked the worker). Vanilla auto-resume keeps the
                    // worker on this mineral; the only book job left is the
                    // bank-200 suicide.
                    BankGateWait(),                               // 1: hold until bank 200
                    AttackRow(hatchTag),                          // 2
                };
                SeedBook(assignedWorker.UnitID, book, relativeFrame, $"{assignedWorker.Role}");
            }

            _seeded = true;
            Console.WriteLine($"[FLIP68] seeded 12 worker books at frame={frame} relative={relativeFrame} startIndex={startIndex} hatch={hatchTag} TA={taTag} TB={tbTag} BB={bbTag} BA={baTag} SA={saTag} YA={yaTag} YB={ybTag} W1wait=({waitX:F4},{waitY:F4}) S1start=({s1StartX:F2},{s1StartY:F2}) S3park=({s3ParkX:F2},{s3ParkY:F2}) W7start=({w7StartX:F2},{w7StartY:F2}) W8move=({w8MoveX:F2},{w8MoveY:F2}) W9start=({w9StartX:F2},{w9StartY:F2}) W9thru=YB-SMH({w9ThruX:F2},{w9ThruY:F2}) W10move=({w10MoveX:F2},{w10MoveY:F2}) W10thru=YA-SMH({w10ThruX:F2},{w10ThruY:F2})");
        }

        private void SeedBook(ulong unitTag, List<WorkerInstruction> book, int relativeFrame, string label)
        {
            var state = new RuntimeWorkerState { UnitTag = unitTag };
            state.LoadInstructions(Set, book, relativeFrame);
            Settings.RuntimeWorkers[unitTag] = state;
            Console.WriteLine($"[FLIP68] book {label}: worker={unitTag} rows={book.Count}");
        }

        private static void RewriteTargets(
            List<AssignedWorkerDto> assigned,
            ulong unitTag,
            ulong hatchTag,
            IEnumerable<OrderedMineral> minerals)
        {
            var entry = assigned.FirstOrDefault(w => w != null && w.UnitID == unitTag);
            if (entry == null)
            {
                return;
            }
            entry.TownHallUnitID = hatchTag;
            entry.Mti = 0;
            entry.MiningTargets = minerals
                .Where(m => m != null && m.UnitTag != 0)
                .Select(m => new MiningTargetDto
                {
                    ResourceLabel = m.FinalLabel ?? string.Empty,
                    ResourceUnitId = m.UnitTag,
                    TownHallUnitId = hatchTag,
                    ResourcePosition = m.Position,
                    HarvestPoint = m.HarvestPoint,
                    SmHarvestPoint = m.SmHarvestPoint,
                    ReturnPoint = m.ReturnPoint,
                    SmReturnPoint = m.SmReturnPoint,
                    IsInitialMineralAssignment = true,
                })
                .ToList();
        }

        private void LogPending(int frame, string why)
        {
            if (!_seedPendingLogged)
            {
                _seedPendingLogged = true;
                Console.WriteLine($"[FLIP68] seed pending frame={frame}: {why}");
            }
        }

        private static WorkerInstruction GatherRow(ulong mineralTag, int relativeFrame, bool noCondition)
        {
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Gather,
                Point = WorkerInstructionPoint.Harvest,
                TargetId = mineralTag,
                RelativeFrame = relativeFrame,
                NoCondition = noCondition,
                AbilityIdOverride = (int)Abilities.SMART,
            };
        }

        private static WorkerInstruction MoveRow(float x, float y, int relativeFrame)
        {
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Move,
                Point = WorkerInstructionPoint.None,
                LiteralX = x,
                LiteralY = y,
                RelativeFrame = relativeFrame,
            };
        }

        private static WorkerInstruction FlipWait()
        {
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Wait,
                PreviousAbilityId = GatherDrone,
                TargetAbilityId = ReturnDrone,
            };
        }

        private static WorkerInstruction HandoffWait()
        {
            // No gates: passes on the cargo-handed-off edge after the Return.
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Wait,
            };
        }

        private static WorkerInstruction GateWait(ulong gateWorkerTag)
        {
            // Holds the book until the gate worker shows the 1184 return ability.
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Wait,
                GateWorkerTag = gateWorkerTag,
                GateWorkerAbilityId = ReturnDrone,
            };
        }

        private static WorkerInstruction ReturnRow(ulong mineralTag)
        {
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Return,
                Point = WorkerInstructionPoint.None,
                TargetId = mineralTag,
                NoCondition = true,
                Queue = true,
            };
        }

        // owner spec 2026-11-17 06:20 "change the suicide to Bank 200": the
        // suicide rows gate on the mineral bank reaching 200 (latched event,
        // the "spawning pool at 200 bank" model) instead of W3's 1184 flip.
        private const int SuicideBankMinerals = 200;

        private static WorkerInstruction GateJump(int attackIndex)
        {
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Jump,
                GateBankMinerals = SuicideBankMinerals,
                JumpToInstructionIndex = attackIndex,
            };
        }

        private static WorkerInstruction LoopJump(int targetIndex)
        {
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Jump,
                JumpToInstructionIndex = targetIndex,
            };
        }

        private static WorkerInstruction AttackRow(ulong hatcheryTag)
        {
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Attack,
                TargetId = hatcheryTag,
                GateBankMinerals = SuicideBankMinerals,
                // NoCondition: the terminal suicide row must execute the frame
                // the book walk reaches it — it has no frame/ability/position
                // signature of its own, and without this flag it would hold at
                // the main-loop pass gate forever (run 20261116203903: zero
                // attacks issued).
                NoCondition = true,
            };
        }

        // Bank-gated hold (owner spec 2026-11-17 06:20): W4 parks and waits
        // for the bank-200 suicide gate instead of W3's flip.
        private static WorkerInstruction BankGateWait()
        {
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.Wait,
                GateBankMinerals = SuicideBankMinerals,
            };
        }

        // owner spec 2026-11-17 06:00: 4-T2's Spray (EFFECT_SPRAY 3684, the
        // command-card "C") at a literal point — its park point, right after
        // the Move row. RelativeFrame 1 so it issues the frame AFTER the Move
        // (same-frame unqueued commands coalesce and the spray would swallow
        // the move — run 20261117060926). NoCondition is moot with the frame
        // gate set; kept for clarity.
        private const int EffectSpray = 3684;

        private static WorkerInstruction SprayRow(float x, float y)
        {
            return new WorkerInstruction
            {
                InstructionSet = Set,
                Command = WorkerInstructionCommand.RawAbility,
                RawAbilityId = EffectSpray,
                LiteralX = x,
                LiteralY = y,
                RelativeFrame = 1,
                NoCondition = true,
            };
        }

        private static float DistanceSquared(Vector2Dto a, Vector2Dto b)
        {
            if (a == null || b == null)
            {
                return float.MaxValue;
            }
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }
    }
}
