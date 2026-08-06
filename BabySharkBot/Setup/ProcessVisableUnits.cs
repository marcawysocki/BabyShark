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
            ResponseObservation? observation,
            WorkerLabelService? workerLabelService,
            MineralLabelService? mineralLabelService = null,
            VespeneLabelService? vespeneLabelService = null,
            SpawningPoolPlacementService? spawningPoolPlacementService = null)
        {
            if (observation == null || Globals.CurrentObservation == null) return new List<WorkerEntryDto>();

            // The new refactor path: simply extract self workers from the centralized snapshot.
            // ObservationManager already classified them.
            var workerEntries = new List<WorkerEntryDto>();
            var snapshot = Globals.CurrentObservation;

            foreach (var tag in snapshot.AvailableWorkers)
            {
                if (snapshot.SelfUnits.TryGetValue(tag, out var entry))
                {
                    // Preserve existing labels from the service if available
                    var label = workerLabelService?.GetLabel(tag) ?? string.Empty;
                    entry.Label = label;
                    entry.FinalLabel = label;
                    workerEntries.Add(entry);
                }
            }

            // Also include workers already assigned (in Commanders)
            foreach (var kvp in snapshot.SelfUnits)
            {
                if (!snapshot.AvailableWorkers.Contains(kvp.Key))
                {
                    var label = workerLabelService?.GetLabel(kvp.Key) ?? string.Empty;
                    kvp.Value.Label = label;
                    kvp.Value.FinalLabel = label;
                    workerEntries.Add(kvp.Value);
                }
            }

            return workerEntries;
        }

        public static List<WorkerEntryDto> ProcessVisibleUnits(
            ResponseObservation? observation,
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