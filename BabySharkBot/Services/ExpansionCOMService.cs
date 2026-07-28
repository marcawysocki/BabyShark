using System;
using System.Collections.Generic;
using BabySharkBot.Setup;
using Sharky;
using SC2APIProtocol;

namespace BabySharkBot.Services
{
    /// <summary>
    /// Service to store and manage Expansion Center-Of-Mass (COM) positions for visualization.
    /// Each expansion's "smile" (mineral cluster center) is stored as a Point for blue crosshair drawing.
    /// </summary>
    public class ExpansionCOMService
    {
        private Dictionary<int, SC2APIProtocol.Point> _expansionCOMPositions = new Dictionary<int, SC2APIProtocol.Point>();

        public ExpansionCOMService()
        {
            Console.WriteLine("ExpansionCOMService initialized");
        }

        /// <summary>
        /// Register an expansion COM position (the "smile" center of the mineral cluster)
        /// </summary>
        public void Set(int expansionIndex, Vector2Dto comPosition)
        {
            if (comPosition == null)
            {
                Console.WriteLine($"ExpansionCOMService: Attempted to set null position for expansion {expansionIndex}");
                return;
            }

            var point = new SC2APIProtocol.Point { X = comPosition.X, Y = comPosition.Y, Z = comPosition.Z };
            _expansionCOMPositions[expansionIndex] = point;
            Console.WriteLine($"ExpansionCOMService: Set expansion {expansionIndex} COM at ({comPosition.X:F2}, {comPosition.Y:F2}, {comPosition.Z:F2})");
        }

        /// <summary>
        /// Get all expansion COM positions
        /// </summary>
        public Dictionary<int, SC2APIProtocol.Point> Get()
        {
            return new Dictionary<int, SC2APIProtocol.Point>(_expansionCOMPositions);
        }

        /// <summary>
        /// Clear all stored positions
        /// </summary>
        public void Clear()
        {
            _expansionCOMPositions.Clear();
            Console.WriteLine("ExpansionCOMService: Cleared all expansion COM positions");
        }

        /// <summary>
        /// Get count of registered expansions
        /// </summary>
        public int Count => _expansionCOMPositions.Count;
    }
}
