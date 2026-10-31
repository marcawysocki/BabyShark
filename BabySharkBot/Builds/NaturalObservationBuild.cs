using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using BabySharkBot.Services;
using BabySharkBot.Setup;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// Natural-observation build (owner spec 2026-10-28 14:13, "Test 1").
    ///
    /// Part 1 of the SM-point ground-truth work: with ALL worker mining
    /// commands suppressed (SeedRuntimeWorkerInstructions is a no-op in this
    /// mode, collision detours and the T1 return interrupt are gated off), the
    /// engine's natural auto-mining chooses where workers stand to harvest and
    /// return. The telemetry stream (worker_commands_*.jsonl
    /// WorkerObservation records — per-frame position, order ability, carrying
    /// flag, raw order target tag) records exactly where the workers go.
    ///
    /// Production schedule — the ONLY unit commands this build ever issues are
    /// larva morphs (drones/overlords), never worker commands:
    ///   start 50 bank          -> morph drone 13 (bank 0)
    ///   at 100 bank            -> morph Overlord #1, then drone 14
    ///   next 50                -> drone 14 morphs
    ///   at the next 100 bank   -> morph Overlord #2
    ///   then                   -> drones toward 32
    /// No suicide, no buildings, no worker commands. Note: with only two
    /// overlords the 32-drone desire supply-caps at 22 drones — by design; the
    /// owner schedules exactly two overlords for this test.
    /// </summary>
    public class NaturalObservationBuild : BabySharkBuild
    {
        private const int DroneFirstTarget = 13;
        private const int DroneSecondTarget = 14;
        private const int DroneFinalTarget = 32;
        private const int OverlordCostBank = 100;

        private readonly DroneMorphService _droneMorph;

        // 0 = morphing to 13, 1 = waiting for 100 bank (overlord #1),
        // 2 = morphing to 14, 3 = waiting for 100 bank (overlord #2),
        // 4 = morphing toward 32.
        private int _phase;
        private int _lastLogFrame = -1000;

        public NaturalObservationBuild(DefaultSharkyBot defaultSharkyBot) : base(defaultSharkyBot)
        {
            _droneMorph = new DroneMorphService(defaultSharkyBot) { DesiredDroneCount = DroneFirstTarget };
        }

        public override void OnStart(int frame)
        {
            base.OnStart(frame);
            _phase = 0;
            _lastLogFrame = -1000;
            _droneMorph.DesiredDroneCount = DroneFirstTarget;
            Console.WriteLine($"[NATURAL] production schedule armed — drone {DroneFirstTarget}, overlord at {OverlordCostBank} bank x2, then {DroneFinalTarget}; zero worker commands");
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();
            if (observation?.Observation?.RawData?.Units == null)
            {
                return actions;
            }

            var frame = (int)observation.Observation.GameLoop;
            var selfUnits = observation.Observation.RawData.Units
                .Where(u => u.Alliance == Alliance.Self)
                .ToList();
            var drones = selfUnits.Count(u => u.UnitType == (uint)UnitTypes.ZERG_DRONE);
            var pendingDrones = selfUnits.Count(u =>
                u.UnitType == (uint)UnitTypes.ZERG_LARVA
                && u.Orders.Any(o => o.AbilityId == (uint)Abilities.TRAIN_DRONE));
            var overlords = selfUnits.Count(u =>
                u.UnitType == (uint)UnitTypes.ZERG_OVERLORD
                || u.UnitType == (uint)UnitTypes.ZERG_OVERLORDTRANSPORT);
            var pendingOverlords = selfUnits.Count(u =>
                u.UnitType == (uint)UnitTypes.ZERG_LARVA
                && u.Orders.Any(o => o.AbilityId == (uint)Abilities.TRAIN_OVERLORD));
            var bank = MacroData.Minerals;

            switch (_phase)
            {
                case 0:
                    if (drones + pendingDrones >= DroneFirstTarget)
                    {
                        _phase = 1;
                        Log(frame, $"phase 0->1 — drone {DroneFirstTarget} morphing (bank {bank}), waiting for {OverlordCostBank} bank for overlord #1");
                    }
                    break;

                case 1:
                    if (bank >= OverlordCostBank && pendingOverlords == 0 && overlords < 2)
                    {
                        var morph = MorphLarva(selfUnits, (int)Abilities.TRAIN_OVERLORD, "overlord #1");
                        if (morph.Any())
                        {
                            actions.AddRange(morph);
                            MacroData.DesiredUnitCounts[UnitTypes.ZERG_DRONE] = DroneSecondTarget;
                            _droneMorph.DesiredDroneCount = DroneSecondTarget;
                            _phase = 2;
                            Log(frame, $"phase 1->2 — bank {bank}: overlord #1 morph issued, desired workers {DroneSecondTarget}");
                        }
                    }
                    break;

                case 2:
                    if (drones + pendingDrones >= DroneSecondTarget)
                    {
                        _phase = 3;
                        Log(frame, $"phase 2->3 — drone {DroneSecondTarget} morphing (bank {bank}), waiting for {OverlordCostBank} bank for overlord #2");
                    }
                    break;

                case 3:
                    if (bank >= OverlordCostBank && pendingOverlords == 0 && overlords < 3)
                    {
                        var morph = MorphLarva(selfUnits, (int)Abilities.TRAIN_OVERLORD, "overlord #2");
                        if (morph.Any())
                        {
                            actions.AddRange(morph);
                            MacroData.DesiredUnitCounts[UnitTypes.ZERG_DRONE] = DroneFinalTarget;
                            _droneMorph.DesiredDroneCount = DroneFinalTarget;
                            _phase = 4;
                            Log(frame, $"phase 3->4 — bank {bank}: overlord #2 morph issued, desired workers {DroneFinalTarget}");
                        }
                    }
                    break;

                case 4:
                    if (frame - _lastLogFrame >= 22)
                    {
                        _lastLogFrame = frame;
                        Log(frame, $"phase 4 — drones {drones}/{DroneFinalTarget} bank {bank} foodLeft {MacroData.FoodLeft} (supply cap expected at 22 with 2 overlords)");
                    }
                    break;
            }

            // Drone morphing runs every frame (no-op once the desired count is
            // reached or supply/bank blocks it). Larva morphs are NOT worker
            // commands — workers are never touched.
            actions.AddRange(_droneMorph.Update(frame, observation));
            return actions;
        }

        private static IEnumerable<SC2APIProtocol.Action> MorphLarva(List<Unit> selfUnits, int abilityId, string label)
        {
            var actions = new List<SC2APIProtocol.Action>();
            var larva = selfUnits.FirstOrDefault(u =>
                u.UnitType == (uint)UnitTypes.ZERG_LARVA && u.Orders.Count == 0);
            if (larva == null)
            {
                Console.WriteLine($"[NATURAL] WARN — no idle larva for {label}");
                return actions;
            }

            var command = new ActionRawUnitCommand
            {
                AbilityId = abilityId,
            };
            command.UnitTags.Add(larva.Tag);
            actions.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = command } });
            Console.WriteLine($"[NATURAL] morphing larva {larva.Tag} -> {label} (ability {abilityId})");
            return actions;
        }

        private static void Log(int frame, string message)
        {
            Console.WriteLine($"[NATURAL] frame={frame} {message}");
        }
    }
}
