using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Maw.Setup;
using Maw.Managers;

namespace Maw.Setup
{
    /// <summary>
    /// Central service for drawing crosshairs on strategic map locations.
    /// Draws crosshairs on: start location, expansions (self base locations), and mineral center of mass.
    /// Provides direct drawing to SC2 client via proxy each frame.
    /// </summary>
    public class CrosshairService
    {
        private ProtobufProxy? _proxy;
        private Maw.Managers.MawBaseLocationData? _baseLocationData;

        // Cyan color for crosshairs
        private static readonly Color CrosshairColor = new Color { R = 0, G = 255, B = 255 };
        private const float CrosshairSize = 2.0f;

        public CrosshairService() { }

        /// <summary>
        /// Call this once to set up the proxy for drawing.
        /// </summary>
        public void Initialize(ProtobufProxy proxy, Func<ulong, Unit?> getUnitByTag)
        {
            _proxy = proxy;
        }

        /// <summary>
        /// Set the base location data so crosshairs can be drawn on the strategic locations.
        /// </summary>
        public void SetBaseLocationData(Maw.Managers.MawBaseLocationData? baseLocationData)
        {
            _baseLocationData = baseLocationData;
        }

        /// <summary>
        /// Update the most recent raw units list (unused, kept for API compatibility).
        /// </summary>
        public void UpdateRawUnits(List<Unit>? rawUnits)
        {
            // No-op; we draw on base locations, not units
        }

        /// <summary>
        /// Build a debug request containing crosshairs on start location, expansions, and mineral center of mass.
        /// Returns null if there are no locations to draw or debug drawing is disabled.
        /// </summary>
        public Request? BuildDebugRequest(List<Unit>? rawUnits)
        {
            // Only build debug requests when debug drawing is enabled.
            if (!Settings.DebugMode || _baseLocationData == null)
                return null;

            var crosshairReq = new Request();
            crosshairReq.Debug = new RequestDebug();
            var crosshairCmd = new DebugCommand();
            crosshairCmd.Draw = new DebugDraw();

            // Draw crosshair on start location (first entry in StartLocations from MapLocationData)
            try
            {
                if (_baseLocationData.MapLocationData?.StartLocations != null && _baseLocationData.MapLocationData.StartLocations.Count > 0)
                {
                    var startLoc = _baseLocationData.MapLocationData.StartLocations[0];
                    DrawCrosshair(crosshairCmd, startLoc.X, startLoc.Y, 12.0f);
                }
            }
            catch { }

            // Draw crosshairs on all expansion locations (SelfBaseLocations)
            try
            {
                if (_baseLocationData.SelfBaseLocations != null)
                {
                    foreach (var expansion in _baseLocationData.SelfBaseLocations)
                    {
                        DrawCrosshair(crosshairCmd, expansion.X, expansion.Y, 12.0f);
                    }
                }
            }
            catch { }

            // Draw crosshair on mineral center of mass
            try
            {
                if (_baseLocationData.MineralCenterOfMass != null)
                {
                    DrawCrosshair(crosshairCmd, _baseLocationData.MineralCenterOfMass.X, _baseLocationData.MineralCenterOfMass.Y, 12.0f);
                }
            }
            catch { }

            if (crosshairCmd.Draw.Lines.Count > 0)
            {
                crosshairReq.Debug.Debug.Add(crosshairCmd);
                return crosshairReq;
            }

            return null;
        }

        /// <summary>
        /// Helper method to draw a crosshair at the given location.
        /// </summary>
        private static void DrawCrosshair(DebugCommand cmd, float x, float y, float z)
        {
            // Draw vertical line
            var vertLine = new DebugLine
            {
                Color = CrosshairColor,
                Line = new Line
                {
                    P0 = new Point { X = x, Y = y - CrosshairSize, Z = z },
                    P1 = new Point { X = x, Y = y + CrosshairSize, Z = z }
                }
            };

            // Draw horizontal line
            var horizLine = new DebugLine
            {
                Color = CrosshairColor,
                Line = new Line
                {
                    P0 = new Point { X = x - CrosshairSize, Y = y, Z = z },
                    P1 = new Point { X = x + CrosshairSize, Y = y, Z = z }
                }
            };

            cmd.Draw.Lines.Add(vertLine);
            cmd.Draw.Lines.Add(horizLine);
        }
    }
}
