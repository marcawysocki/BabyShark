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
    ///   W4 (canon S3): parked out of the way, joins the suicide.
    ///   The other 8 workers: plain loop on their assigned mineral, join the
    ///       suicide.
    ///   Suicide: every worker ATTACKs the own hatchery once W3 shows the
    ///       1184 return ability (its TB flip). Book ends after the attack row.
    /// </summary>
    public class MagannathaSeFlip68Build : BabySharkBuild
    {
        private const string Set = "flip68";
        private const int GatherDrone = (int)Abilities.HARVEST_GATHER_DRONE;
        private const int ReturnDrone = (int)Abilities.HARVEST_RETURN_DRONE;

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
            if (ta == null || tb == null || bb == null)
            {
                LogPending(frame, $"minerals not resolved yet (TA={(ta == null ? "missing" : "ok")} TB={(tb == null ? "missing" : "ok")} BB={(bb == null ? "missing" : "ok")})");
                return;
            }

            if (!workersByLabel.TryGetValue("W1", out var w1)
                || !workersByLabel.TryGetValue("W2", out var w2)
                || !workersByLabel.TryGetValue("W3", out var w3)
                || !workersByLabel.TryGetValue("W4", out var w4))
            {
                LogPending(frame, "teal worker labels W1-W4 not all resolved yet");
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

            // W4's park point: 5u west / 5u north of the hatchery, out of the
            // mineral lanes (owner-approved parking for the idle worker).
            var parkX = hatchery.Position.X - 5f;
            var parkY = hatchery.Position.Y + 5f;

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
                MoveRow(parkX, parkY, 0),                  // 0: park out of the way
                // owner spec 2026-11-17 06:00: Spray (EFFECT_SPRAY 3684,
                // command-card "C") at the park point right after the Move.
                SprayRow(parkX, parkY),                    // 1
                BankGateWait(),                            // 2: hold until bank 200
                AttackRow(hatchTag),           // 3
            };

            SeedBook(w2.UnitTag, w2Book, relativeFrame, "W2/canon-T1");
            SeedBook(w3.UnitTag, w3Book, relativeFrame, "W3/canon-T2");
            SeedBook(w1.UnitTag, w1Book, relativeFrame, "W1/canon-T3");
            SeedBook(w4.UnitTag, w4Book, relativeFrame, "W4/canon-S3");

            // The mining manager resolves Gather points and Return town halls
            // from AssignedWorkers[startIndex].MiningTargets, so the teal four
            // get exact routes for the minerals their books touch.
            RewriteTargets(assigned, w1.UnitTag, hatchTag, new[] { ta });
            RewriteTargets(assigned, w2.UnitTag, hatchTag, new[] { bb, ta });
            RewriteTargets(assigned, w3.UnitTag, hatchTag, new[] { tb });

            // The other 8 workers: a plain loop on their already-assigned first
            // mineral plus the same suicide tail.
            var tealTags = new HashSet<ulong> { w1.UnitTag, w2.UnitTag, w3.UnitTag, w4.UnitTag };
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
            Console.WriteLine($"[FLIP68] seeded 12 worker books at frame={frame} relative={relativeFrame} startIndex={startIndex} hatch={hatchTag} TA={taTag} TB={tbTag} BB={bbTag} W1wait=({waitX:F4},{waitY:F4})");
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
