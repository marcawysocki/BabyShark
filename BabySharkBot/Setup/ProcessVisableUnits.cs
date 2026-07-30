using SC2APIProtocol;
using Sharky;
using BabySharkBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable

namespace BabySharkBot.Setup
{
    public static class ProcessVisableUnits
    {
        private static bool IsValidWorkerLabel(string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return false;

            // Allow established greedy chain labels (W1-W12)
            if (label.StartsWith("W", StringComparison.OrdinalIgnoreCase)) return true;

            // Allow established team labels (T1, S1, B1, Y1, G1, P1, O1, R1, H1, Leo, OV etc.)
            string[] teamPrefixes = { "T", "S", "B", "Y", "G", "P", "O", "R", "M", "H", "Leo", "OV" };
            return teamPrefixes.Any(p => label.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Lightweight entry point.
        /// 
        /// - Frame 0-1 (or base not yet configured): runs the full one-time initialization
        ///   (mineral/vespene discovery, worker chain labeling, team registration, etc.).
        /// - Steady-state (frame > 1): extracts only self workers and returns their entries.
        ///   No neutral-unit scanning, no re-labeling, no team setup.
        /// </summary>
        public static List<WorkerEntryDto> ProcessVisibleUnits(
            ResponseObservation observation,
            WorkerLabelService? workerLabelService,
            MineralLabelService? mineralLabelService = null,
            VespeneLabelService? vespeneLabelService = null,
            SpawningPoolPlacementService? spawningPoolPlacementService = null)
        {
            var frame = observation?.Observation?.GameLoop ?? 0;

            // Heavy path: only during the very first frames or when the base setup hasn't finished yet.
            if (frame <= 1 || !Settings.CurrentBaseHasBeenPlayed)
            {
                return InitializeVisibleUnits(observation, workerLabelService, mineralLabelService, vespeneLabelService, spawningPoolPlacementService);
            }

            // Steady-state lightweight path.
            return ExtractWorkerEntries(observation, workerLabelService);
        }

        public static List<WorkerEntryDto> ProcessVisibleUnits(
            ResponseObservation observation,
            WorkerLabelService? workerLabelService)
        {
            return ProcessVisibleUnits(observation, workerLabelService, null, null, null);
        }

        /// <summary>
        /// One-time heavy initialization. Mirrors the original full-frame logic:
        /// discovers minerals/vespenes, assigns W-chain and team labels, registers
        /// hatchery/overlord/larva labels, and runs team-label registration.
        /// </summary>
        private static List<WorkerEntryDto> InitializeVisibleUnits(
            ResponseObservation observation,
            WorkerLabelService? workerLabelService,
            MineralLabelService? mineralLabelService,
            VespeneLabelService? vespeneLabelService,
            SpawningPoolPlacementService? spawningPoolPlacementService)
        {
            var workerEntries = new List<WorkerEntryDto>();
            var visibleMinerals = new List<Unit>();
            var visibleVespene = new List<Unit>();
            var larvaLabelIndex = 13;

            if (observation?.Observation?.RawData?.Units == null) return workerEntries;

            var baseHasBeenPlayed = Settings.WorkerCount == 8
                ? Settings.CurrentBaseHasBeenPlayed8
                : (Settings.WorkerCount == 12 ? Settings.CurrentBaseHasBeenPlayed12 : Settings.CurrentBaseHasBeenPlayed);

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
                        if (baseHasBeenPlayed)
                        {
                            if (mineralLabelService != null && Globals.CurrentMapData != null)
                            {
                                var key = $"{unit.Pos.X:F2},{unit.Pos.Y:F2}";
                                if (Globals.CurrentMapData.MineralFinalLabelsByPosition.TryGetValue(key, out var finalLabel) && !string.IsNullOrWhiteSpace(finalLabel))
                                {
                                    var color = GetFinalLabelColor(finalLabel);
                                    mineralLabelService.SetMineralLabel(finalLabel, new Point
                                    {
                                        X = unit.Pos.X,
                                        Y = unit.Pos.Y,
                                        Z = unit.Pos.Z + 0.5f
                                    }, color, unit.Tag);
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
                        if (baseHasBeenPlayed)
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
                                    }, GetFinalLabelColor(finalLabel));
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

            var orderedWorkers = new List<WorkerEntryDto>(workerEntries);
            var labeledCount = orderedWorkers.Count(w => IsValidWorkerLabel(w.Label));

            if (labeledCount == 0 && orderedWorkers.Count > 0)
            {
                var workerLabelIndex = orderedWorkers.Count;
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
            else if (labeledCount < orderedWorkers.Count)
            {
                var nextIndex = orderedWorkers.Count + 1;
                foreach (var worker in orderedWorkers.Where(w => !IsValidWorkerLabel(w.Label)))
                {
                    var label = $"W{nextIndex}";
                    worker.Label = label;
                    worker.StartLabel = label;
                    worker.FinalLabel = label;
                    if (workerLabelService != null) workerLabelService.SetLabel(label, worker.UnitTag);
                    nextIndex++;
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
                        MapLabelRegistrationHelper.RegisterLabels(
                            Globals.CurrentMapData,
                            Settings.CurrentSpawnIndex,
                            mineralLabelService,
                            vespeneLabelService,
                            spawningPoolPlacementService);

                        TeamLabelRegistrationHelper.EnsureTeamLabelsForStart(
                            Globals.CurrentMapData,
                            Settings.CurrentSpawnIndex,
                            Globals.CurrentMapData.OrderedMainMinerals[Settings.CurrentSpawnIndex],
                            workerEntries,
                            Settings.CurrentSpawnCOM,
                            workerLabelService,
                            Globals.CurrentMapData.TeamPatchAssignments);
                    }
                }
            }

            return orderedWorkers;
        }

        /// <summary>
        /// Steady-state lightweight path.
        /// Only extracts self workers and preserves their existing labels.
        /// Skips neutral scanning, mineral/vespene re-labeling, and team registration.
        /// </summary>
        private static List<WorkerEntryDto> ExtractWorkerEntries(
            ResponseObservation observation,
            WorkerLabelService? workerLabelService)
        {
            var workerEntries = new List<WorkerEntryDto>();
            if (observation?.Observation?.RawData?.Units == null) return workerEntries;

            foreach (var unit in observation.Observation.RawData.Units)
            {
                try
                {
                    if (unit?.Pos == null || unit.DisplayType != DisplayType.Visible || unit.Alliance != Alliance.Self)
                        continue;

                    var ut = (UnitTypes)unit.UnitType;
                    if (ut != UnitTypes.ZERG_DRONE && ut != UnitTypes.TERRAN_SCV && ut != UnitTypes.PROTOSS_PROBE)
                        continue;

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
                catch
                {
                }
            }

            // Back-fill any newly-spawned workers that don't have a label yet
            var labeledCount = workerEntries.Count(w => IsValidWorkerLabel(w.Label));
            if (labeledCount == 0 && workerEntries.Count > 0)
            {
                var workerLabelIndex = workerEntries.Count;
                foreach (var worker in workerEntries)
                {
                    var label = $"W{workerLabelIndex}";
                    worker.Label = label;
                    worker.StartLabel = label;
                    worker.FinalLabel = label;

                    if (workerLabelService != null)
                        workerLabelService.SetLabel(label, worker.UnitTag);
                    workerLabelIndex--;
                }
            }
            else if (labeledCount < workerEntries.Count)
            {
                var nextIndex = workerEntries.Count + 1;
                foreach (var worker in workerEntries.Where(w => !IsValidWorkerLabel(w.Label)))
                {
                    var label = $"W{nextIndex}";
                    worker.Label = label;
                    worker.StartLabel = label;
                    worker.FinalLabel = label;
                    if (workerLabelService != null)
                        workerLabelService.SetLabel(label, worker.UnitTag);
                    nextIndex++;
                }
            }

            return workerEntries;
        }

        public static List<WorkerEntryDto> ProcessVisibleUnits(
            WorkerLabelService? workerLabelService,
            Vector2Dto mineralCenterOfMass,
            bool baseHasBeenPlayed)
        {
            Settings.CurrentSpawnCOM = mineralCenterOfMass;
            Settings.CurrentBaseHasBeenPlayed = baseHasBeenPlayed;
            if (Settings.WorkerCount == 8) Settings.CurrentBaseHasBeenPlayed8 = baseHasBeenPlayed;
            else if (Settings.WorkerCount == 12) Settings.CurrentBaseHasBeenPlayed12 = baseHasBeenPlayed;

            return new List<WorkerEntryDto>();
        }

        public static Color GetFinalLabelColor(string finalLabel)
        {
            if (string.IsNullOrWhiteSpace(finalLabel)) return new Color { R = 255, G = 255, B = 255 };

            if (finalLabel.StartsWith("V", System.StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(finalLabel, "V1", System.StringComparison.OrdinalIgnoreCase) ? new Color { R = 0, G = 255, B = 0 }
                    : string.Equals(finalLabel, "V2", System.StringComparison.OrdinalIgnoreCase) ? new Color { R = 0, G = 0, B = 255 }
                    : new Color { R = 128, G = 0, B = 128 };
            }

            return finalLabel[0] switch
            {
                'T' => new Color { R = 0, G = 255, B = 255 },    // Teal
                'S' or 'M' => new Color { R = 255, G = 0, B = 255 }, // Salmon/Magenta
                'B' => new Color { R = 0, G = 0, B = 255 },     // Blue
                'Y' => new Color { R = 255, G = 255, B = 0 },   // Yellow
                'G' => new Color { R = 0, G = 255, B = 0 },     // Green
                'P' => new Color { R = 128, G = 0, B = 128 },   // Purple
                'O' => new Color { R = 255, G = 165, B = 0 },   // Orange
                'R' => new Color { R = 255, G = 0, B = 0 },     // Red
                _ => new Color { R = 255, G = 255, B = 255 }
            };
        }
    }
}