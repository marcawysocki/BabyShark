using System;
using System.Collections.Generic;
using SC2APIProtocol;

namespace BabySharkBot.Services
{
    /// <summary>
    /// Centralized team color and prefix mapping for 8-worker, 12-worker, 
    /// pink transition, and speed-mining phases.
    /// </summary>
    public static class TeamColorService
    {
        // --- 8-worker start colors ---
        public static readonly Color Green  = new Color { R = 0,   G = 255, B = 0   };
        public static readonly Color Purple = new Color { R = 128, G = 0,   B = 128 };
        public static readonly Color Red    = new Color { R = 255, G = 0,   B = 0   };
        public static readonly Color Orange = new Color { R = 255, G = 165, B = 0   };

        // --- 12-worker start colors ---
        public static readonly Color Teal   = new Color { R = 0,   G = 255, B = 255 };
        public static readonly Color Salmon = new Color { R = 255, G = 128, B = 128 };
        public static readonly Color Blue   = new Color { R = 0,   G = 0,   B = 255 };
        public static readonly Color Yellow = new Color { R = 255, G = 255, B = 0   };

        // --- Transition color for workers 13-15 ---
        public static readonly Color Pink   = new Color { R = 255, G = 192, B = 203 };

        // --- Mineral pair index → team metadata ---
        // pairIndex: 0=M[0]+M[1], 1=M[2]+M[3], 2=M[4]+M[5], 3=M[6]+M[7]
        public static (string prefix8, string prefix12, Color color8, Color color12) GetTeamMeta(int pairIndex)
        {
            return pairIndex switch
            {
                0 => ("G", "T", Green,  Teal),
                1 => ("P", "S", Purple, Salmon),
                2 => ("R", "B", Red,    Blue),
                3 => ("O", "Y", Orange, Yellow),
                _ => ("W", "W", new Color { R = 255, G = 255, B = 255 }, new Color { R = 255, G = 255, B = 255 })
            };
        }

        public static Color GetColorByPrefix(string prefix)
        {
            return prefix switch
            {
                "G" => Green,  "T" => Teal,
                "P" => Purple, "S" => Salmon,
                "R" => Red,    "B" => Blue,
                "O" => Orange, "Y" => Yellow,
                "Pink" => Pink,
                _ => new Color { R = 255, G = 255, B = 255 }
            };
        }

        public class WorkerAssignment
        {
            public string prefix { get; set; } = "";
            public int workerNum { get; set; }
            public Color color { get; set; } = new Color();
            public bool isPink { get; set; }
            public int pairIndex { get; set; }
        }

        public static WorkerAssignment GetAssignmentForNewWorker(int currentWorkerCount)
        {
            // currentWorkerCount is the number of workers ALREADY in the game.
            // Worker 9 is index 8.
            int workerIndex = currentWorkerCount; 

            if (workerIndex < 8)
            {
                // Should not happen for dynamic assignment if start is 8, but for completeness:
                int pairIdx = workerIndex / 2;
                var meta = GetTeamMeta(pairIdx);
                return new WorkerAssignment { prefix = meta.prefix8, workerNum = (workerIndex % 2) + 1, color = meta.color8, pairIndex = pairIdx };
            }
            
            if (workerIndex >= 8 && workerIndex <= 11)
            {
                // Workers 9-12: becoming the 3rd worker for teams 1-4
                // Transitions: G1->T1, G2->T2, new->T3
                int pairIdx = workerIndex - 8;
                var meta = GetTeamMeta(pairIdx);
                return new WorkerAssignment { prefix = meta.prefix12, workerNum = 3, color = meta.color12, pairIndex = pairIdx };
            }

            // Pink Phase: Workers 13-15
            if (workerIndex == 12) return new WorkerAssignment { prefix = "S", workerNum = 4, color = Pink, isPink = true, pairIndex = 1 }; // S4 (13th)
            if (workerIndex == 13) return new WorkerAssignment { prefix = "Y", workerNum = 4, color = Pink, isPink = true, pairIndex = 3 }; // Y4 (14th)
            if (workerIndex == 14) return new WorkerAssignment { prefix = "B", workerNum = 4, color = Pink, isPink = true, pairIndex = 2 }; // B4 (15th)

            // Speed Mining Trigger: Worker 16
            if (workerIndex == 15)
            {
                return new WorkerAssignment { prefix = "T", workerNum = 4, color = Teal, pairIndex = 0 }; // T4 (16th)
            }

            if (workerIndex >= 16)
            {
                // Generic fallback for extra workers beyond 16
                return new WorkerAssignment { prefix = "W", workerNum = workerIndex + 1, color = new Color { R = 255, G = 255, B = 255 } };
            }

            return new WorkerAssignment { prefix = "W", workerNum = workerIndex + 1, color = new Color { R = 255, G = 255, B = 255 } };
        }

        /// <summary>
        /// Returns transition labels for existing workers when a team forms (e.g. 9th worker forms Teal team).
        /// </summary>
        public static List<(string oldLabel, string newLabel)> GetTransitionsForTeam(int pairIndex)
        {
            var meta = GetTeamMeta(pairIndex);
            return new List<(string, string)>
            {
                ($"{meta.prefix8}1", $"{meta.prefix12}1"),
                ($"{meta.prefix8}2", $"{meta.prefix12}2")
            };
        }

        public static bool IsSpeedMiningPhase(int totalWorkers)
        {
            return totalWorkers >= 16;
        }
    }
}
