using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable

namespace BabySharkBot.Setup
{
    /// <summary>
    /// Hardcoded start-location tables (owner spec 2026-10-16): Magannatha
    /// maps ALWAYS begin their start list in the SOUTH EAST corner, then
    /// resolve CLOCKWISE around the map center. Hardcoding guarantees the
    /// start slots stay stable no matter what order the game API returns
    /// them in (a map update can start the API list at location 1 instead of
    /// 0 — the apiStarts/apiLoc index must never decide slot order).
    ///
    /// ┌───────────────────────────────────────────────────────────────────┐
    /// │ AGENT VERIFY (owner directive 2026-10-16):                        │
    /// │ These coordinates are HARDCODED. If this map is NOT on the        │
    /// │ current ladder pool, or the map receives an update (new version,  │
    /// │ moved starts), VERIFY the starts against a LIVE GAME before       │
    /// │ trusting this table: run one game and compare the                 │
    /// │ "InitialMapData: API Start[i]" log lines and the                  │
    /// │ data/base/InitialMapData.csv rows against the coordinates below.  │
    /// │ All known maps will be hardcoded before go-live; until then, an   │
    /// │ unverified or mismatched entry must WARN LOUDLY (see the WARNING  │
    /// │ log in InitialMapData) instead of failing silently.               │
    /// └───────────────────────────────────────────────────────────────────┘
    /// </summary>
    public static class HardcodedStartOrders
    {
        /// <summary>One map's hardcoded start townhall coordinates. Order in
        /// the table is irrelevant — <see cref="OrderClockwiseFromSouthEast"/>
        /// always resolves the final slot order (SE first, then clockwise).</summary>
        public sealed class MapStartTable
        {
            public MapStartTable(string mapNamePrefix, params Vector2Dto[] starts)
            {
                MapNamePrefix = mapNamePrefix;
                Starts = starts.ToList();
            }

            /// <summary>Case-insensitive prefix match against
            /// gameInfo.MapName (covers "Magannatha AIE", versioned map
            /// file names, etc.).</summary>
            public string MapNamePrefix { get; }
            public List<Vector2Dto> Starts { get; }
        }

        // Magannatha AIE — both start townhalls, verified 2026-10-16 against
        // live games (run30 Start0 game: VA registered at (134.5,27.5);
        // run1 Start1 game: VA at (28.5,134.5)).
        private static readonly MapStartTable[] Tables =
        {
            new MapStartTable("Magannatha",
                new Vector2Dto(127.5f, 24.5f),   // South East corner
                new Vector2Dto(24.5f, 127.5f)),  // North West corner
        };

        /// <summary>Returns the hardcoded start table for this map, or null
        /// when the map is not hardcoded (fall back to the API order).</summary>
        public static MapStartTable? Get(string? mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
            {
                return null;
            }
            return Tables.FirstOrDefault(t =>
                mapName.StartsWith(t.MapNamePrefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Owner spec: start list begins in the SOUTH EAST corner and resolves
        /// CLOCKWISE. Bearing is measured counter-clockwise from East around
        /// the center of the starts; the clockwise distance from the SE
        /// bearing (-45°) is the sort key. On Magannatha this yields
        /// Start[0] = SE (127.5,24.5), Start[1] = NW (24.5,127.5).
        /// </summary>
        public static List<Vector2Dto> OrderClockwiseFromSouthEast(IReadOnlyList<Vector2Dto> starts)
        {
            var list = starts.Where(s => s != null).ToList();
            if (list.Count <= 1)
            {
                return list;
            }

            var cx = list.Average(s => s.X);
            var cy = list.Average(s => s.Y);
            const double seBearing = -45.0; // South East relative to the center

            double ClockwiseKey(Vector2Dto s)
            {
                var bearing = Math.Atan2(s.Y - cy, s.X - cx) * 180.0 / Math.PI;
                var key = (seBearing - bearing) % 360.0;
                if (key < 0)
                {
                    key += 360.0;
                }
                return key;
            }

            return list
                .OrderBy(ClockwiseKey)
                .ThenBy(s => s.X)
                .ThenBy(s => s.Y)
                .ToList();
        }
    }
}
