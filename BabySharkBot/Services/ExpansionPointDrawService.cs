using System;
using System.Collections.Generic;
using Sharky;
using SC2APIProtocol;

namespace BabySharkBot.Services
{
    /// <summary>
    /// Service to store and manage expansion townhall placement points for visualization.
    /// Receives computed expansion points from ExpansionPointService and makes them available for drawing.
    /// 
    /// Drawing logic:
    /// - Standard expansions: Green sphere + label (e.g., "E1")
    /// - Contested expansions: Yellow/Orange spheres for alternate placements (e.g., "E2-Alt")
    /// - Z-coordinate: preserve the real terrain/object Z from registration
    /// </summary>
    public class ExpansionPointDrawService
    {
        private Dictionary<string, ExpansionPointData> _expansionPoints = new Dictionary<string, ExpansionPointData>();

        public class ExpansionPointData
        {
            public Point Position { get; set; }
            public string Label { get; set; }
            public Color Color { get; set; }
            public bool IsContested { get; set; }
        }

        public ExpansionPointDrawService()
        {
            Console.WriteLine("ExpansionPointDrawService initialized");
        }

        /// <summary>
        /// Register an expansion townhall placement point for visualization
        /// </summary>
        public void SetExpansionPoint(Point position, string label, Color color, bool isContested = false)
        {
            if (position == null)
            {
                Console.WriteLine($"ExpansionPointDrawService: Attempted to set null position for {label}");
                return;
            }

            var data = new ExpansionPointData
            {
                Position = position,
                Label = label,
                Color = color,
                IsContested = isContested
            };

            _expansionPoints[label] = data;
            Console.WriteLine($"ExpansionPointDrawService: Registered {label} at ({position.X:F2}, {position.Y:F2}, {position.Z:F2}) contested={isContested}");
        }

        /// <summary>
        /// Get all registered expansion points for drawing
        /// </summary>
        public Dictionary<string, ExpansionPointData> GetAllPoints()
        {
            return new Dictionary<string, ExpansionPointData>(_expansionPoints);
        }

        /// <summary>
        /// Clear all registered points (optional)
        /// </summary>
        public void Clear()
        {
            _expansionPoints.Clear();
            Console.WriteLine("ExpansionPointDrawService: Cleared all points");
        }
    }
}
