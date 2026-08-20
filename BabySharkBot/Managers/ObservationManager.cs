using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using Sharky.Pathing;
using BabySharkBot.Setup;
using BabySharkBot.Services;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Reads the raw ResponseObservation once per frame and populates shared DTO state.
    /// Replaces "Unknown" terminology with "Available" and prepares units for manager labeling.
    /// </summary>
    public class ObservationManager : Sharky.Managers.SharkyManager
    {
        private readonly ActiveUnitData _activeUnitData;
        private readonly SharkyUnitData _sharkyUnitData;
        private readonly BaseData _baseData;
        private readonly MapDataService _mapDataService;
        private readonly UnitDataService _unitDataService;
        private readonly chrisCrossAppleSause _ccaService;
        private bool _startupInitialized;

        private readonly HashSet<UnitTypes> WorkerTypes = new() { UnitTypes.ZERG_DRONE, UnitTypes.TERRAN_SCV, UnitTypes.PROTOSS_PROBE };
        private readonly HashSet<UnitTypes> MineralFieldTypes = new()
        {
            UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD, UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD750,
            UnitTypes.NEUTRAL_LABMINERALFIELD, UnitTypes.NEUTRAL_LABMINERALFIELD750,
            UnitTypes.NEUTRAL_MINERALFIELD, UnitTypes.NEUTRAL_MINERALFIELD750,
            UnitTypes.NEUTRAL_PURIFIERMINERALFIELD, UnitTypes.NEUTRAL_PURIFIERMINERALFIELD750,
            UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD, UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD750,
            UnitTypes.NEUTRAL_RICHMINERALFIELD, UnitTypes.NEUTRAL_RICHMINERALFIELD750
        };
        private readonly HashSet<UnitTypes> GasGeyserTypes = new()
        {
            UnitTypes.NEUTRAL_VESPENEGEYSER, UnitTypes.NEUTRAL_SPACEPLATFORMGEYSER,
            UnitTypes.NEUTRAL_SHAKURASVESPENEGEYSER, UnitTypes.NEUTRAL_RICHVESPENEGEYSER,
            UnitTypes.NEUTRAL_PURIFIERVESPENEGEYSER, UnitTypes.NEUTRAL_PROTOSSVESPENEGEYSER
        };

        private Dictionary<ulong, WorkerEntryDto> _previousSelfUnits = new();
        private HashSet<ulong> _previousVisibleTags = new();

        public ObservationManager(
            ActiveUnitData activeUnitData,
            SharkyUnitData sharkyUnitData,
            BaseData baseData,
            MapDataService mapDataService,
            UnitDataService unitDataService,
            chrisCrossAppleSause ccaService = null)
        {
            _activeUnitData = activeUnitData;
            _sharkyUnitData = sharkyUnitData;
            _baseData = baseData;
            _mapDataService = mapDataService;
            _unitDataService = unitDataService;
            _ccaService = ccaService;
        }

        public override bool NeverSkip => true;

        public override void OnStart(ResponseGameInfo gameInfo, ResponseData data, ResponsePing pingResponse, ResponseObservation observation, uint playerId, string opponentId)
        {
            ProcessObservation(observation);
            InitializeStartupObservation();
        }

        private void InitializeStartupObservation()
        {
            if (_startupInitialized || Settings.WorkerCount == 8 || _ccaService == null || Globals.CurrentMapData == null)
            {
                return;
            }

            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (startIndex < 0)
            {
                return;
            }

            _ccaService.EnableCcaMiningForCurrentSpawn(Globals.CurrentMapData, startIndex);
            _startupInitialized = true;
        }

        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            ProcessObservation(observation);
            return null;
        }

        private void ProcessObservation(ResponseObservation observation)
        {
            if (observation?.Observation?.RawData?.Units == null) return;

            var frame = (int)observation.Observation.GameLoop;
            var relativeFrame = Settings.GetRelativeFrame(frame);
            
            // During the initialization phase, clear all Sharky Commanders to ensure 
            // CCA has absolute authority over the workers.
            if (relativeFrame < 35)
            {
                _activeUnitData.Commanders.Clear();
            }

            // Initialize observation snapshot if it's the first frame or reset it
            if (Globals.CurrentObservation == null)
            {
                Globals.CurrentObservation = new ObservationSnapshotDto();
            }

            Globals.CurrentObservation.Frame = frame;
            Globals.CurrentObservation.ReadyForLabeling.Clear();

            // Clear available collections (terminology change: Unknown -> Available)
            Globals.CurrentObservation.AvailableWorkers.Clear();
            Globals.CurrentObservation.AvailableLarva.Clear();
            Globals.CurrentObservation.AvailableQueens.Clear();
            Globals.CurrentObservation.AvailableOverlords.Clear();
            Globals.CurrentObservation.WorkerPositions.Clear();
            Globals.CurrentObservation.SelfUnits.Clear(); // Refresh self units to prevent staleness

            // Clear frame-specific dictionaries
            Globals.CurrentObservation.Minerals.Clear();
            Globals.CurrentObservation.VisibleMinerals.Clear();
            Globals.CurrentObservation.Vespene.Clear();
            Globals.CurrentObservation.CurrentTownHalls.Clear();
            
            var currentSelfUnits = new Dictionary<ulong, WorkerEntryDto>();
            var currentVisibleTags = new HashSet<ulong>();

            if (relativeFrame % 5 == 0)
            {
                 Console.WriteLine($"ObservationManager: Processing frame {frame} (relative {relativeFrame}). RawUnitsCount={observation.Observation.RawData.Units.Count}");
            }

            foreach (var unit in observation.Observation.RawData.Units)
            {
                currentVisibleTags.Add(unit.Tag);
                if (unit.Alliance == Alliance.Self)
                {
                    ClassifySelfUnit(unit, frame, currentSelfUnits);
                }
                else if (unit.Alliance == Alliance.Enemy)
                {
                    ClassifyEnemyUnit(unit, frame);
                }
                else if (unit.Alliance == Alliance.Neutral)
                {
                    ClassifyNeutralUnit(unit);
                }
            }

            PublishAvailableUnits();

            _previousSelfUnits = currentSelfUnits;
            _previousVisibleTags = currentVisibleTags;
        }

        private void ClassifySelfUnit(Unit unit, int frame, Dictionary<ulong, WorkerEntryDto> currentSelfUnits)
        {
            var ut = (UnitTypes)unit.UnitType;
            
            bool becameVisible = !_previousVisibleTags.Contains(unit.Tag);
            
            if (!Globals.CurrentObservation.SelfUnits.TryGetValue(unit.Tag, out var entry))
            {
                entry = new WorkerEntryDto
                {
                    UnitTag = unit.Tag,
                    UnitType = unit.UnitType,
                    FirstSeenFrame = frame,
                    FirstSeenPosition = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z)
                };
                Globals.CurrentObservation.SelfUnits[unit.Tag] = entry;
            }

            entry.Position = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z);
            entry.UnitType = unit.UnitType;
            entry.BecameVisible = becameVisible;
            entry.IsMorphing = unit.BuildProgress < 1.0f && ut != UnitTypes.ZERG_LARVA;
            entry.IsCompleted = unit.BuildProgress >= 1.0f;
            entry.OrderAbilityIds = unit.Orders?.Select(order => Convert.ToInt32(order.AbilityId)).ToList() ?? new List<int>();
            entry.TargetUnitTag = unit.Orders?.FirstOrDefault()?.TargetUnitTag ?? 0;

            if (unit.UnitType == (uint)UnitTypes.ZERG_HATCHERY
                || unit.UnitType == (uint)UnitTypes.TERRAN_COMMANDCENTER
                || unit.UnitType == (uint)UnitTypes.PROTOSS_NEXUS)
            {
                Globals.CurrentObservation.CurrentTownHalls[unit.Tag] = entry;
            }

            // Explicitly expose X,Y coordinates for CCA service
            Globals.CurrentObservation.WorkerPositions[unit.Tag] = entry.Position;
            var relativeUnitFrame = Settings.GetRelativeFrame(frame);
            
            if (relativeUnitFrame % 5 == 0 && ut == UnitTypes.ZERG_DRONE)
            {
                // Console.WriteLine($"ObservationManager: Tracked Drone {unit.Tag} at ({entry.Position.X:F2},{entry.Position.Y:F2})");
            }
            
            // Cargo Tracking
            bool isCarrying = unit.BuffIds.Any(b => b == 271 || b == 272); // Mineral/Vespene buffs
            bool wasCarrying = false;
            if (_previousSelfUnits.TryGetValue(unit.Tag, out var prevEntry))
            {
                wasCarrying = prevEntry.IsCarrying;
            }
            entry.WasCarrying = wasCarrying;
            entry.IsCarrying = isCarrying;
            entry.JustPickedUp = isCarrying && !wasCarrying;

            currentSelfUnits[unit.Tag] = entry;

            // Readiness Logic
            if (_activeUnitData.Commanders.ContainsKey(unit.Tag))
            {
                return;
            }

            // Note: Per user request, units still morphing (e.g. Eggs) are not labeled as workers during CCA.
            if (entry.IsMorphing)
            {
                return;
            }

            if (WorkerTypes.Contains(ut))
            {
                Globals.CurrentObservation.AvailableWorkers.Add(unit.Tag);
                Globals.CurrentObservation.ReadyForLabeling.Drone.Add(unit.Tag);
            }
            else if (ut == UnitTypes.ZERG_LARVA)
            {
                Globals.CurrentObservation.AvailableLarva.Add(unit.Tag);
            }
            else if (ut == UnitTypes.ZERG_OVERLORD || ut == UnitTypes.ZERG_OVERLORDTRANSPORT)
            {
                Globals.CurrentObservation.AvailableOverlords.Add(unit.Tag);
                Globals.CurrentObservation.ReadyForLabeling.Overlord.Add(unit.Tag);
            }
            else if (ut == UnitTypes.ZERG_QUEEN)
            {
                Globals.CurrentObservation.AvailableQueens.Add(unit.Tag);
                Globals.CurrentObservation.ReadyForLabeling.Queen.Add(unit.Tag);
            }
            else if (ut == UnitTypes.ZERG_ZERGLING)
            {
                Globals.CurrentObservation.ReadyForLabeling.Zergling.Add(unit.Tag);
            }
            else
            {
                Globals.CurrentObservation.ReadyForLabeling.Other.Add(unit.Tag);
            }
        }

        private static void PublishAvailableUnits()
        {
            Settings.AvailableLarva.Clear();
            Settings.AvailableLarva.AddRange(Globals.CurrentObservation.AvailableLarva);

            Settings.AvailableWorker.Clear();
            Settings.AvailableWorker.AddRange(Globals.CurrentObservation.AvailableWorkers);

            Settings.AvailableOverLord.Clear();
            Settings.AvailableOverLord.AddRange(Globals.CurrentObservation.AvailableOverlords);

            Settings.AvailableQueen.Clear();
            Settings.AvailableQueen.AddRange(Globals.CurrentObservation.AvailableQueens);
        }

        private void ClassifyNeutralUnit(Unit unit)
        {
            var ut = (UnitTypes)unit.UnitType;
            if (MineralFieldTypes.Contains(ut))
            {
                var mineral = new MineralDto
                {
                    UnitTag = unit.Tag,
                    UnitType = unit.UnitType,
                    Position = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z),
                    IsVisible = unit.DisplayType == DisplayType.Visible,
                    MineralContents = unit.HasMineralContents ? unit.MineralContents : 0
                };
                Globals.CurrentObservation.Minerals[unit.Tag] = mineral;
                if (mineral.IsVisible && mineral.MineralContents != 0)
                {
                    Globals.CurrentObservation.VisibleMinerals.Add(mineral);
                }
            }
            else if (GasGeyserTypes.Contains(ut))
            {
                Globals.CurrentObservation.Vespene[unit.Tag] = new OrderedVespene
                {
                    UnitTag = unit.Tag,
                    Position = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z)
                };
            }
        }

        private void ClassifyEnemyUnit(Unit unit, int frame)
        {
            if (!Globals.CurrentObservation.EnemyUnits.TryGetValue(unit.Tag, out var enemyDto))
            {
                enemyDto = new EnemyUnitObservationDto 
                { 
                    UnitTag = unit.Tag,
                    Notes = string.Empty
                };
                Globals.CurrentObservation.EnemyUnits[unit.Tag] = enemyDto;
            }

            // Update movement memory
            enemyDto.LastXY = enemyDto.Position;
            enemyDto.Position = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z);

            // Leverage Sharky's UnitCalculation for detailed stats if visible
            if (_activeUnitData.EnemyUnits.TryGetValue(unit.Tag, out var calculation))
            {
                enemyDto.DPS = calculation.Dps;
                
                // Track spells and cooldowns (placeholders for specialized spell tracking)
                if (unit.UnitType == (uint)UnitTypes.TERRAN_BUNKER)
                {
                    // For Bunkers, occupancy is inferred from DPS observation
                    enemyDto.Notes = $"Inferred DPS: {enemyDto.DPS:F1}";
                }
            }
        }
    }
}
