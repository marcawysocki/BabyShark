using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;

namespace BabySharkBot.Builds
{
    /// <summary>
    /// BabyShark-native extractor trick build.
    /// Ports logic from BuildTest.cs into the new BabySharkBuild architecture.
    /// </summary>
    public class BabySharkExtractorTrick : BabySharkBuild
    {
        private bool _extractorsRequested;
        private bool _extraDronesQueued;
        private bool _extractorTrickCompleted;
        private readonly Sharky.Builds.MacroServices.BuildingRequestCancellingService _buildingRequestCancellingService;

        public BabySharkExtractorTrick(DefaultSharkyBot defaultBot) : base(defaultBot)
        {
            _buildingRequestCancellingService = defaultBot.BuildingRequestCancellingService;
        }

        public override void OnStart(int frame)
        {
            base.OnStart(frame);
            SetDesiredGases(0);
            SetDesiredProductionCount(UnitTypes.ZERG_HATCHERY, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_DRONE, 15);
            SetDesiredUnitCount(UnitTypes.ZERG_QUEEN, 1);
            SetDesiredUnitCount(UnitTypes.ZERG_OVERLORD, 1);
            _extractorsRequested = false;
            _extraDronesQueued = false;
            _extractorTrickCompleted = false;
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            if (_extractorTrickCompleted) return Array.Empty<SC2APIProtocol.Action>();

            var supply = MacroData.FoodUsed;
            var larvaCount = ActiveUnitData.SelfUnits.Values.Count(u => u.Unit.UnitType == (uint)UnitTypes.ZERG_LARVA);
            var minerals = MacroData.Minerals;

            // Step 1: At 14 supply, 1+ larva, 120+ minerals -> request extractor
            if (!_extractorsRequested && supply >= 14 && larvaCount >= 1 && minerals >= 120)
            {
                SetDesiredGases(1);
                _extractorsRequested = true;
            }

            // Step 2: Once extractor is in progress -> queue extra drone
            var inProgressExtractors = ActiveUnitData.SelfUnits.Values.Count(u =>
                u.Unit.UnitType == (uint)UnitTypes.ZERG_EXTRACTOR &&
                u.Unit.BuildProgress < 1.0f);

            if (_extractorsRequested && !_extraDronesQueued && inProgressExtractors >= 1)
            {
                var currentDroneCount = UnitCountService.Count(UnitTypes.ZERG_DRONE);
                SetDesiredUnitCount(UnitTypes.ZERG_DRONE, currentDroneCount + 1);
                _extraDronesQueued = true;
            }

            // Step 3: When extra drone is queued, cancel extractor
            if (_extraDronesQueued && inProgressExtractors > 0)
            {
                SetDesiredGases(0);
                _buildingRequestCancellingService.RequestCancel(UnitTypes.ZERG_EXTRACTOR, 0);
            }
            else if (_extraDronesQueued && inProgressExtractors == 0)
            {
                _extractorTrickCompleted = true;
                SetDesiredGases(0);
                SetDesiredProductionCount(UnitTypes.ZERG_HATCHERY, 2);
                SetDesiredUnitCount(UnitTypes.ZERG_OVERLORD, CountUnits(UnitTypes.ZERG_OVERLORD) + 1);
            }

            return Array.Empty<SC2APIProtocol.Action>();
        }
    }
}
