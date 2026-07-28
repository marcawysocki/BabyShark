using SC2APIProtocol;
using Sharky;
using BabySharkBot.Services;
using System.Collections.Generic;

namespace BabySharkBot.Setup
{
    public static class ProcessVisableUnits
    {
        public static List<WorkerEntryDto> ProcessVisibleUnits(
            ResponseObservation observation,
            WorkerLabelService? workerLabelService,
            MineralLabelService? mineralLabelService = null,
            VespeneLabelService? vespeneLabelService = null,
            SpawningPoolPlacementService? spawningPoolPlacementService = null)
        {
            var workerEntries = new List<WorkerEntryDto>();
            var visibleMinerals = new List<Unit>();
            var visibleVespene = new List<Unit>();
            var larvaLabelIndex = 13;

            if (observation?.Observation?.RawData?.Units != null)
            {
                foreach (var unit in observation.Observation.RawData.Units)
                {
                    try
                    {
                        if (unit?.Pos == null || unit.DisplayType != DisplayType.Visible)
                        {
                            continue;
                        }

                        var ut = (UnitTypes)unit.UnitType;

                        if (unit.Alliance == Alliance.Self)
                        {
                            if (ut == UnitTypes.ZERG_OVERLORD)
                            {
                                if (workerLabelService != null)
                                {
                                    workerLabelService.SetLabel("OV1", unit.Tag);
                                }
                            }
                            else if (ut == UnitTypes.ZERG_LARVA)
                            {
                                if (workerLabelService != null)
                                {
                                    workerLabelService.SetLabel($"Leo{larvaLabelIndex}", unit.Tag);
                                    larvaLabelIndex++;
                                }
                            }
                            else if (ut == UnitTypes.ZERG_HATCHERY || ut == UnitTypes.TERRAN_COMMANDCENTER || ut == UnitTypes.PROTOSS_NEXUS)
                            {
                                if (workerLabelService != null)
                                {
                                    workerLabelService.SetLabel("H1", unit.Tag);
                                }
                            }
                            else if (ut == UnitTypes.ZERG_DRONE || ut == UnitTypes.TERRAN_SCV || ut == UnitTypes.PROTOSS_PROBE)
                            {
                                var label = workerLabelService?.GetLabel(unit.Tag) ?? string.Empty;
                                workerEntries.Add(new WorkerEntryDto
                                {
                                    UnitTag = unit.Tag,
                                    Position = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z),
                                    UnitType = unit.UnitType,
                                    Label = label,
                                    StartLabel = label,
                                    FinalLabel = label
                                });
                            }
                        }
                        else if (unit.Alliance == Alliance.Neutral &&
                                 (ut == UnitTypes.NEUTRAL_MINERALFIELD || ut == UnitTypes.NEUTRAL_MINERALFIELD750 || ut == UnitTypes.NEUTRAL_RICHMINERALFIELD || ut == UnitTypes.NEUTRAL_RICHMINERALFIELD750 || ut == UnitTypes.NEUTRAL_PURIFIERMINERALFIELD || ut == UnitTypes.NEUTRAL_PURIFIERMINERALFIELD750 || ut == UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD || ut == UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD750 || ut == UnitTypes.NEUTRAL_LABMINERALFIELD || ut == UnitTypes.NEUTRAL_LABMINERALFIELD750 || ut == UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD || ut == UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD750))
                        {
                            if (Settings.CurrentBaseHasBeenPlayed)
                            {
                                if (mineralLabelService != null && Globals.CurrentMapData != null)
                                {
                                    var key = $"{unit.Pos.X:F2},{unit.Pos.Y:F2}";
                                    if (Globals.CurrentMapData.MineralFinalLabelsByPosition.TryGetValue(key, out var finalLabel) && !string.IsNullOrWhiteSpace(finalLabel))
                                    {
                                        mineralLabelService.SetMineralLabel(finalLabel, new Point
                                        {
                                            X = unit.Pos.X,
                                            Y = unit.Pos.Y,
                                            Z = unit.Pos.Z + 0.5f
                                        }, finalLabel.StartsWith("T", System.StringComparison.OrdinalIgnoreCase) ? new Color { R = 0, G = 255, B = 255 }
                                          : finalLabel.StartsWith("S", System.StringComparison.OrdinalIgnoreCase) ? new Color { R = 255, G = 105, B = 180 }
                                          : finalLabel.StartsWith("B", System.StringComparison.OrdinalIgnoreCase) ? new Color { R = 0, G = 0, B = 255 }
                                          : finalLabel.StartsWith("Y", System.StringComparison.OrdinalIgnoreCase) ? new Color { R = 255, G = 255, B = 0 }
                                          : new Color { R = 255, G = 255, B = 255 });
                                    }
                                }
                            }
                            else
                            {
                                visibleMinerals.Add(unit);
                            }
                        }
                        else if (unit.Alliance == Alliance.Neutral &&
                                 (ut == UnitTypes.NEUTRAL_VESPENEGEYSER || ut == UnitTypes.NEUTRAL_SPACEPLATFORMGEYSER || ut == UnitTypes.NEUTRAL_SHAKURASVESPENEGEYSER || ut == UnitTypes.NEUTRAL_RICHVESPENEGEYSER || ut == UnitTypes.NEUTRAL_PURIFIERVESPENEGEYSER || ut == UnitTypes.NEUTRAL_PROTOSSVESPENEGEYSER))
                        {
                            if (Settings.CurrentBaseHasBeenPlayed)
                            {
                                if (vespeneLabelService != null && Globals.CurrentMapData != null)
                                {
                                    var key = $"{unit.Pos.X:F2},{unit.Pos.Y:F2}";
                                    if (Globals.CurrentMapData.VespeneFinalLabelsByPosition.TryGetValue(key, out var finalLabel) && !string.IsNullOrWhiteSpace(finalLabel))
                                    {
                                        vespeneLabelService.SetVespeneLabel(finalLabel, new Point
                                        {
                                            X = unit.Pos.X,
                                            Y = unit.Pos.Y,
                                            Z = unit.Pos.Z + 1.0f
                                        }, string.Equals(finalLabel, "V1", System.StringComparison.OrdinalIgnoreCase) ? new Color { R = 0, G = 255, B = 0 }
                                          : string.Equals(finalLabel, "V2", System.StringComparison.OrdinalIgnoreCase) ? new Color { R = 0, G = 0, B = 255 }
                                          : new Color { R = 128, G = 0, B = 128 });
                                    }
                                }
                            }
                            else
                            {
                                visibleVespene.Add(unit);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            // Phase 1: keep the existing worker order and apply W12 through W1 directly.
            var orderedWorkers = new List<WorkerEntryDto>(workerEntries);

            var hasWorkerLabels = orderedWorkers.Count > 0;
            if (hasWorkerLabels)
            {
                foreach (var worker in orderedWorkers)
                {
                    if (string.IsNullOrWhiteSpace(worker.Label) || !worker.Label.StartsWith("W", System.StringComparison.OrdinalIgnoreCase))
                    {
                        hasWorkerLabels = false;
                        break;
                    }
                }
            }

            if (!hasWorkerLabels)
            {
                var workerLabelIndex = 12;
                foreach (var worker in orderedWorkers)
                {
                    var label = $"W{workerLabelIndex}";
                    worker.Label = label;
                    worker.StartLabel = label;
                    worker.FinalLabel = label;

                    if (workerLabelService != null)
                    {
                        workerLabelService.SetLabel(label, worker.UnitTag);
                    }

                    workerLabelIndex--;
                }
            }

            // Phase 2: only on first play, draw minerals first, then pause.
            if (!Settings.CurrentBaseHasBeenPlayed)
            {
                var hasMapData = Globals.CurrentMapData != null;
                var hasSpawnIndex = Settings.CurrentSpawnIndex >= 0;

                if (hasMapData && hasSpawnIndex)
                {
                    var hasMineralData = Globals.CurrentMapData.OrderedMainMinerals.Count > Settings.CurrentSpawnIndex;
                    var hasWorkerData = Globals.CurrentMapData.StartingUnits.Count > Settings.CurrentSpawnIndex;

                    if (hasMineralData && hasWorkerData)
                    {
                        // Phase 2a: draw the stored mineral labels first.
                        MapLabelRegistrationHelper.RegisterLabels(
                            Globals.CurrentMapData,
                            Settings.CurrentSpawnIndex,
                            mineralLabelService,
                            vespeneLabelService,
                            spawningPoolPlacementService);

                        // Pause here so the mineral labels can be inspected before worker relabeling.
                        //System.Diagnostics.Debugger.Break();

                        // Phase 2b: apply worker labels after the mineral labels are visible.
                        TeamLabelRegistrationHelper.EnsureTeamLabelsForStart(
                            Globals.CurrentMapData,
                            Settings.CurrentSpawnIndex,
                            Globals.CurrentMapData.OrderedMainMinerals[Settings.CurrentSpawnIndex],
                            workerEntries,
                            Settings.CurrentSpawnCOM,
                            workerLabelService,
                            Globals.CurrentMapData.TeamPatchAssignments);

                        // Pause again after the worker labels are on-screen.
                        //System.Diagnostics.Debugger.Break();
                    }
                }
            }

            return workerEntries;
        }

        public static List<WorkerEntryDto> ProcessVisibleUnits(
            ResponseObservation observation,
            WorkerLabelService? workerLabelService)
        {
            return ProcessVisibleUnits(observation, workerLabelService, null, null, null);
        }

        public static List<WorkerEntryDto> ProcessVisibleUnits(
            WorkerLabelService? workerLabelService,
            Vector2Dto mineralCenterOfMass,
            bool baseHasBeenPlayed)
        {
            Settings.CurrentSpawnCOM = mineralCenterOfMass;
            Settings.CurrentBaseHasBeenPlayed = baseHasBeenPlayed;
            return new List<WorkerEntryDto>();
        }
    }
}