using SC2APIProtocol;
using Sharky;
using System;
using System.Collections.Generic;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Provides debug drawing capabilities to custom managers that don't have direct access to Sharky's DebugService.
    /// This is a workaround since IManager doesn't receive DebugService as a dependency.
    /// Managers can use this to log debug information and visualizations.
    /// </summary>
    public static class ManagerDebugService
    {
        private static DebugService _debugService;
        private static SharkyOptions _sharkyOptions;

        /// <summary>
        /// Initialize the manager debug service with Sharky's debug service and options.
        /// This should be called once during bot initialization.
        /// </summary>
        public static void Initialize(DebugService debugService, SharkyOptions sharkyOptions)
        {
            _debugService = debugService;
            _sharkyOptions = sharkyOptions;
        }

        /// <summary>
        /// Draw text at a specific world position if debug mode is enabled.
        /// </summary>
        public static void DrawText(string text, Point worldPos, Color color, uint size = 12)
        {
            if (_debugService != null && _sharkyOptions?.Debug == true)
            {
                _debugService.DrawText(text, worldPos, color, size);
            }
        }

        /// <summary>
        /// Draw a line between two points if debug mode is enabled.
        /// </summary>
        public static void DrawLine(Point start, Point end, Color color)
        {
            if (_debugService != null && _sharkyOptions?.Debug == true)
            {
                _debugService.DrawLine(start, end, color);
            }
        }

        /// <summary>
        /// Draw a sphere at a specific point if debug mode is enabled.
        /// </summary>
        public static void DrawSphere(Point point, float radius = 2, Color color = null)
        {
            if (_debugService != null && _sharkyOptions?.Debug == true)
            {
                _debugService.DrawSphere(point, radius, color);
            }
        }

        /// <summary>
        /// Check if debug mode is currently enabled.
        /// </summary>
        public static bool IsDebugEnabled => _sharkyOptions?.Debug == true;
    }
}
