using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using BabySharkBot.Setup;

namespace BabySharkBot.Services
{
    /// <summary>
    /// Reusable extractor trick for supply expansion.
    /// Finds team closest to V1, waits for 125 minerals, 
    /// builds extractor, morphs 15th worker, cancels extractor.
    /// </summary>
    public class ExtractorTrickService
    {
        private readonly DefaultSharkyBot _defaultBot;
        private readonly Sharky.Builds.MacroServices.BuildingRequestCancellingService _cancelService;
        
        private bool _extractorRequested;
        private bool _extraDroneQueued;
        private bool _trickComplete;

        public ExtractorTrickService(DefaultSharkyBot defaultBot)
        {
            _defaultBot = defaultBot;
            _cancelService = defaultBot.BuildingRequestCancellingService;
        }

        public void Reset()
        {
            _extractorRequested = false;
            _extraDroneQueued = false;
            _trickComplete = false;
        }

        public IEnumerable<SC2APIProtocol.Action> Update(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();

            if (_trickComplete || observation?.Observation?.RawData?.Units == null) 
                return actions;

            var selfUnits = observation.Observation.RawData.Units
                .Where(u => u.Alliance == Alliance.Self).ToList();
            
            int droneCount = selfUnits.Count(u => 
                u.UnitType == (uint)UnitTypes.ZERG_DRONE);
            
            // Pending drones from larva
            int pendingDrones = selfUnits.Count(u => 
                u.UnitType == (uint)UnitTypes.ZERG_LARVA && 
                u.Orders.Any(o => o.AbilityId == (uint)Abilities.TRAIN_DRONE));

            int totalDrones = droneCount + pendingDrones;

            // Only run when we're at 14 drones (or 13 with one morphing) and need the 15th
            if (totalDrones < 14) return actions;
            if (totalDrones >= 15 && !_extractorRequested) 
            {
                 // If we somehow got to 15 without trick, we might be done or skipped it
                 return actions;
            }

            var minerals = _defaultBot.MacroData.Minerals;
            var larva = selfUnits.FirstOrDefault(u => 
                u.UnitType == (uint)UnitTypes.ZERG_LARVA && u.Orders.Count == 0);

            // Step 1: At 125+ minerals, request extractor
            if (!_extractorRequested && !_extraDroneQueued && minerals >= 120 && totalDrones == 14)
            {
                // Find V1 vespene
                var v1 = FindV1Vespene(observation);
                if (v1 != null)
                {
                    _defaultBot.MacroData.DesiredGases = 1;
                    _extractorRequested = true;
                    Console.WriteLine("ExtractorTrickService: Requesting extractor for trick at 14 drones.");
                }
            }

            // Step 2: Once extractor in progress, queue extra drone
            var inProgressExtractors = selfUnits.Count(u =>
                u.UnitType == (uint)UnitTypes.ZERG_EXTRACTOR &&
                u.BuildProgress < 1.0f);

            if (_extractorRequested && !_extraDroneQueued && inProgressExtractors >= 1)
            {
                if (larva != null && minerals >= 50)
                {
                    var morphCmd = new ActionRawUnitCommand
                    {
                        AbilityId = (int)Abilities.TRAIN_DRONE,
                        UnitTags = { larva.Tag }
                    };
                    actions.Add(new SC2APIProtocol.Action { ActionRaw = new ActionRaw { UnitCommand = morphCmd } });
                    _extraDroneQueued = true;
                    _extractorRequested = false;
                    Console.WriteLine($"ExtractorTrickService: Queuing 15th drone from larva {larva.Tag}");
                }
            }

            // Step 3: When extra drone is queued, cancel extractor
            if (_extraDroneQueued)
            {
                if (inProgressExtractors > 0)
                {
                    _defaultBot.MacroData.DesiredGases = 0;
                    _cancelService.RequestCancel(UnitTypes.ZERG_EXTRACTOR, 0);
                    Console.WriteLine("ExtractorTrickService: Cancelling extractor trick.");
                }
                else
                {
                    _trickComplete = true;
                    _defaultBot.MacroData.DesiredGases = 0;
                    Console.WriteLine("ExtractorTrickService: Trick complete.");
                }
            }

            return actions;
        }

        private Unit FindV1Vespene(ResponseObservation observation)
        {
            // Simple heuristic for now: closest vespene to townhall
            var townhall = observation.Observation.RawData.Units.FirstOrDefault(u => 
                u.Alliance == Alliance.Self && 
                (u.UnitType == (uint)UnitTypes.ZERG_HATCHERY));
            
            if (townhall == null) return null;

            return observation.Observation.RawData.Units.Where(u => 
                u.UnitType == (uint)UnitTypes.NEUTRAL_VESPENEGEYSER ||
                u.UnitType == (uint)UnitTypes.NEUTRAL_RICHVESPENEGEYSER ||
                u.UnitType == (uint)UnitTypes.NEUTRAL_SPACEPLATFORMGEYSER)
                .OrderBy(u => Math.Pow(u.Pos.X - townhall.Pos.X, 2) + Math.Pow(u.Pos.Y - townhall.Pos.Y, 2))
                .FirstOrDefault();
        }
    }
}
