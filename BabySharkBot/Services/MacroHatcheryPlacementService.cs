using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BabySharkBot.Setup;
using SC2APIProtocol;
using Sharky;
using Sharky.Builds.BuildingPlacement;
using Sharky.DefaultBot;

#nullable enable

namespace BabySharkBot.Services
{
    /// <summary>
    /// Macro Hatchery placement (owner spec 2026-10-16): at the entrance of the
    /// starting location, tucked into the corner wall at the top of the ramp.
    /// The center is 2.5 from the wall (hatchery footprint flush with both wall
    /// faces — the wall on this map is a corner shape) and the hatchery touches
    /// one side edge of the ramp. Geometry is derived from Sharky's WallData
    /// (RampCenter + the two WallSegments forming the corner), so it recomputes
    /// per start location and keeps working on Magannatha when we advance to
    /// other maps.
    ///
    /// First-attempt workflow (owner spec): draw the circle like the Spawning
    /// Pool placement, then watch a game to see if the timing is late/too soon
    /// and whether the spot is a valid build location (logged per start).
    ///
    /// Sharky note: WallDataService.GetWallData generation reads
    /// BaseData.EnemyBaseLocations, which is null in this bot (Sharky's
    /// BaseManager never populates it here — run13 postmortem). We fill it
    /// surgically from the two start locations JUST for the generation call and
    /// restore the original (null) afterwards, so no other system sees the
    /// substitute list. The result is cached + saved to data/wall/, so this
    /// happens once per map.
    /// </summary>
    public class MacroHatcheryPlacementService
    {
        // Hatchery footprint radius: "the center is 2.5 from the wall" (owner
        // spec) — a hatchery is a 5x5 building, so a center 2.5 from a wall face
        // puts the footprint edge exactly on the wall.
        public const float HatcheryRadius = 2.5f;

        private readonly DefaultSharkyBot _defaultBot;

        public MacroHatcheryPlacementService(DefaultSharkyBot defaultBot)
        {
            _defaultBot = defaultBot;
        }

        /// <summary>
        /// Sharky's MapData grid (walkable/buildable/height per cell) is normally
        /// built by Sharky's MapManager.OnStart — but BabyShark clears Sharky's
        /// managers (BabySharkBot.cs), so the grid stays empty and every
        /// pathing-based query dies with "Columns is 0" (run15 postmortem). This
        /// runs Sharky's own MapManager.OnStart once to build the grid from the
        /// raw start data. Idempotent; safe to call every OnStart.
        /// </summary>
        public void EnsureMapDataInitialized(ResponseGameInfo gameInfo)
        {
            var mapData = _defaultBot?.MapData;
            if (mapData == null || (mapData.Map != null && mapData.MapWidth > 0))
            {
                return;
            }
            if (gameInfo?.StartRaw?.PathingGrid == null || gameInfo.StartRaw.TerrainHeight == null || gameInfo.StartRaw.PlacementGrid == null)
            {
                return;
            }

            var mapManager = new Sharky.Managers.MapManager(
                mapData,
                _defaultBot.ActiveUnitData,
                _defaultBot.SharkyOptions,
                _defaultBot.SharkyUnitData,
                _defaultBot.DebugService,
                _defaultBot.WallDataService);
            mapManager.OnStart(gameInfo, null, null, null, 0, null);
            Console.WriteLine($"[MACRO HATCHERY] Sharky MapData initialized {mapData.MapWidth}x{mapData.MapHeight}");
        }

        /// <summary>
        /// Computes the Macro Hatchery placement for the base nearest to
        /// <paramref name="selfTownhall"/>. <see cref="MacroHatcheryPlacement.Spec"/>
        /// is the exact spec point (corner + 2.5/2.5 from the wall faces);
        /// <see cref="MacroHatcheryPlacement.Adjusted"/> is the nearest buildable
        /// point found by a small spiral search when the spec point is not legal
        /// (null when the spec point is already legal). The first-attempt
        /// workflow draws both so the owner can judge the spot in-game.
        /// </summary>
        public MacroHatcheryPlacement? GetPlacement(string mapName, Vector2Dto selfTownhall, Vector2Dto? enemyTownhall)
        {
            if (string.IsNullOrWhiteSpace(mapName) || selfTownhall == null || _defaultBot?.WallDataService == null)
            {
                return null;
            }

            var baseData = _defaultBot.BaseData;
            if (baseData == null)
            {
                Console.WriteLine("[MACRO HATCHERY] BaseData null — cannot derive wall data");
                return null;
            }

            List<WallData>? wallDataList = null;
            var originalBaseLocations = baseData.BaseLocations;
            var originalEnemyBaseLocations = baseData.EnemyBaseLocations;
            var filledBaseData = false;
            try
            {
                // Surgical fill (see class note): Sharky's wall generation reads
                // BaseData.BaseLocations / EnemyBaseLocations, and this bot never
                // runs Sharky's BaseManager (Managers.Clear() in BabySharkBot.cs —
                // run14 postmortem), so BOTH are null. Fill them from the two
                // known start locations for the generation call only, then
                // restore. Only [0] of each list is consumed (self base / enemy
                // base) by the wall geometry math.
                var selfBase = new BaseLocation { Location = new Point2D { X = selfTownhall.X, Y = selfTownhall.Y } };
                BaseLocation? enemyBase = enemyTownhall == null
                    ? null
                    : new BaseLocation { Location = new Point2D { X = enemyTownhall.X, Y = enemyTownhall.Y } };
                if (baseData.BaseLocations == null || baseData.BaseLocations.Count == 0)
                {
                    baseData.BaseLocations = enemyBase != null
                        ? new List<BaseLocation> { selfBase, enemyBase }
                        : new List<BaseLocation> { selfBase };
                    filledBaseData = true;
                }
                if ((baseData.EnemyBaseLocations == null || baseData.EnemyBaseLocations.Count == 0)
                    && enemyBase != null)
                {
                    baseData.EnemyBaseLocations = new List<BaseLocation> { enemyBase };
                    filledBaseData = true;
                }

                wallDataList = _defaultBot.WallDataService.GetWallData(mapName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MACRO HATCHERY] GetWallData failed: {ex}");
            }
            finally
            {
                if (filledBaseData)
                {
                    baseData.BaseLocations = originalBaseLocations;
                    baseData.EnemyBaseLocations = originalEnemyBaseLocations;
                }
            }

            if (wallDataList == null)
            {
                return null;
            }

            var wall = wallDataList
                .Where(w => w?.BasePosition != null)
                .OrderBy(w => DistanceSquared(w.BasePosition, selfTownhall))
                .FirstOrDefault();
            if (wall?.RampCenter == null || wall.WallSegments == null || wall.WallSegments.Count < 2)
            {
                Console.WriteLine($"[MACRO HATCHERY] wall data incomplete for map={mapName} rampCenter={(wall?.RampCenter == null ? "null" : "ok")} segments={wall?.WallSegments?.Count ?? -1}");
                return null;
            }

            var spec = CalculatePlacement(wall);
            if (spec == null)
            {
                return null;
            }

            var specValid = IsBuildable(spec);
            var candidates = CalculateCandidates(wall);
            Point2D? adjusted = null;
            if (!specValid)
            {
                // Owner decision 2026-10-16: this is a ZERG wall. The hatchery
                // goes at the wall end FARTHEST from the door — it needs no
                // creep — leaving the door side for the 3x3 Roach Warren, which
                // needs the creep that extends from the hatchery, with a 1u gap
                // by the door where the queen gatekeeper stands. Segment-level
                // distance (not per-candidate): the slide stays corner-hugging
                // instead of running off across the base (run25 postmortem: a
                // slid candidate east of S1 beat the true corner candidate on
                // raw door distance). (The spec point also lands on the ramp
                // band on Magannatha, so the wall-flush candidate is used.)
                var valid = candidates.Where(c => IsBuildable(c.Point));
                var best = valid
                    .OrderByDescending(c => c.SegmentDoorDistanceSquared)
                    .Select(c => c.Point)
                    .FirstOrDefault();
                adjusted = best ?? FindNearestBuildable(spec);
            }

            return WithOwnerTargetSnap(new MacroHatcheryPlacement
            {
                Wall = wall,
                Spec = spec,
                SpecValid = specValid,
                Candidates = candidates.Select(c => c.Point).ToList(),
                Adjusted = adjusted,
            });
        }

        /// <summary>Wall-flush candidate with its segment's door distance.</summary>
        public readonly struct MacroCandidate
        {
            public MacroCandidate(Point2D point, float segmentDoorDistanceSquared)
            {
                Point = point;
                SegmentDoorDistanceSquared = segmentDoorDistanceSquared;
            }

            public Point2D Point { get; }
            public float SegmentDoorDistanceSquared { get; }
        }

        /// <summary>
        /// Wall-flush candidate centers: for each wall segment, the point 2.5
        /// inside the BASE side of its ramp-facing face (owner spec: "the
        /// center is 2.5 from the wall"), slid along the face from the corner
        /// over the .5 placement grid (5x5 buildings center on cell centers)
        /// so the footprint lands on legal cells. Corner-most first per
        /// segment; carries the segment's distance to the door for the
        /// hatchery-side selection (far-from-door segment wins).
        /// </summary>
        public static List<MacroCandidate> CalculateCandidates(WallData wall)
        {
            var result = new List<MacroCandidate>();
            var segments = wall.WallSegments.Where(s => s?.Position != null).ToList();
            if (wall.RampCenter == null || segments.Count < 2)
            {
                return result;
            }

            var ramp = new Vector2(wall.RampCenter.X, wall.RampCenter.Y);
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                var segmentCenter = new Vector2(segment.Position.X, segment.Position.Y);
                var otherCenter = new Vector2(
                    segments[(index + 1) % segments.Count].Position.X,
                    segments[(index + 1) % segments.Count].Position.Y);
                var doorDistanceSquared = wall.Door != null
                    ? DistanceSquared(segmentCenter, wall.Door)
                    : 0f;
                AddSegmentCandidates(result, segment, otherCenter, ramp, segmentCenter, doorDistanceSquared);
            }

            return result;
        }

        private static float DistanceSquared(Vector2 a, Point2D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static void AddSegmentCandidates(
            List<MacroCandidate> result,
            WallSegment segment,
            Vector2 otherCenter,
            Vector2 ramp,
            Vector2 segmentCenter,
            float doorDistanceSquared)
        {
            var half = segment.Size / 2f;
            var corner = NearestCorner(segmentCenter, half, otherCenter);
            var towardRamp = BestAxisNormal(Normalize(ramp - segmentCenter));
            var baseSide = -towardRamp;
            // Along-face direction pointing from the corner toward the far end
            // of this segment (away from where the other segment sits).
            var along = Math.Abs(towardRamp.X) > 0.5f
                ? new Vector2(0, segmentCenter.Y >= otherCenter.Y ? 1 : -1)
                : new Vector2(segmentCenter.X >= otherCenter.X ? 1 : -1, 0);

            var base0 = corner + HatcheryRadius * baseSide;
            var offsets = new[] { 0f, 0.5f, -0.5f, 1f, -1f, 1.5f, -1.5f, 2f, -2f, 2.5f, -2.5f, 3f, -3f };
            foreach (var offset in offsets)
            {
                var point = base0 + offset * along;
                // Snap each coordinate onto the .5 cell-center grid (5x5
                // buildings place on cell centers).
                var snapped = new Point2D
                {
                    X = MathF.Floor(point.X) + 0.5f,
                    Y = MathF.Floor(point.Y) + 0.5f
                };
                if (!result.Any(c => Math.Abs(c.Point.X - snapped.X) < 0.01f && Math.Abs(c.Point.Y - snapped.Y) < 0.01f))
                {
                    result.Add(new MacroCandidate(snapped, doorDistanceSquared));
                }
            }
        }

        private static float DistanceSquaredTo(Point2D point, Point2D target)
        {
            var dx = point.X - target.X;
            var dy = point.Y - target.Y;
            return dx * dx + dy * dy;
        }

        private static float DistanceSquaredTo(Point2D point, Vector2Dto target)
        {
            var dx = point.X - target.X;
            var dy = point.Y - target.Y;
            return dx * dx + dy * dy;
        }

        private bool IsBuildable(Point2D point)
        {
            return _defaultBot.BuildingService != null
                && _defaultBot.BuildingService.AreaBuildable(point.X, point.Y, HatcheryRadius);
        }

        // Spiral search outward from the spec point for the nearest buildable
        // center (owner spec requires a LEGAL placement; run16: the exact corner
        // pocket overlaps the ramp band on Magannatha). Rings at 0.5 steps up to
        // 3 units, 16 points per ring — small enough to stay in the pocket.
        private Point2D? FindNearestBuildable(Point2D spec)
        {
            if (_defaultBot.BuildingService == null)
            {
                return null;
            }

            for (var radius = 0.5f; radius <= 3.01f; radius += 0.5f)
            {
                for (var step = 0; step < 16; step++)
                {
                    var angle = step * MathF.PI / 8f;
                    var candidate = new Point2D
                    {
                        X = spec.X + radius * MathF.Cos(angle),
                        Y = spec.Y + radius * MathF.Sin(angle)
                    };
                    if (IsBuildable(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Owner correction 2026-10-16: the offset target can land on the ramp
        /// band (unbuildable). The final point is the nearest LEGAL .5-grid
        /// placement to the owner target — as close to the indicated spot as
        /// the flat ground allows. Exhaustive .5-grid scan within
        /// <paramref name="maxRadius"/>, nearest distance wins (exact target
        /// first when it happens to be legal). Falls back to the grid-derived
        /// chosen point when nothing is legal nearby.
        /// </summary>
        public MacroHatcheryPlacement WithOwnerTargetSnap(MacroHatcheryPlacement placement, float maxRadius = 8f)
        {
            var chosen = placement.Adjusted ?? placement.Spec;
            if (chosen == null || _defaultBot.BuildingService == null)
            {
                placement.Final = chosen;
                placement.FinalValid = chosen != null && IsBuildable(chosen);
                return placement;
            }

            var ownerTarget = MacroHatcheryOwnerOffset.Apply(chosen);
            placement.OwnerTarget = ownerTarget;

            Point2D? best = null;
            var bestDist = float.MaxValue;
            var options = new List<(float dist, Point2D point)>();
            for (var x = ownerTarget.X - maxRadius; x <= ownerTarget.X + maxRadius + 0.001f; x += 0.5f)
            {
                for (var y = ownerTarget.Y - maxRadius; y <= ownerTarget.Y + maxRadius + 0.001f; y += 0.5f)
                {
                    var dx = x - ownerTarget.X;
                    var dy = y - ownerTarget.Y;
                    var dist = dx * dx + dy * dy;
                    if (dist > maxRadius * maxRadius)
                    {
                        continue;
                    }
                    var point = new Point2D { X = x, Y = y };
                    if (!IsBuildable(point))
                    {
                        continue;
                    }
                    if (dist < bestDist)
                    {
                        best = point;
                        bestDist = dist;
                    }
                    if (dist <= 25f)
                    {
                        options.Add((dist, point));
                    }
                }
            }

            placement.LegalNearOwnerTarget = options
                .OrderBy(o => o.dist)
                .Take(8)
                .Select(o => o.point)
                .ToList();
            placement.Final = best ?? chosen;
            placement.FinalValid = best != null;
            return placement;
        }

        /// <summary>
        /// Corner geometry: the two wall segments form an L at the top of the
        /// ramp. The hatchery sits in the pocket of that L — flush (2.5) with
        /// the face of each segment that looks toward the ramp, i.e. its edge
        /// runs from the wall corner to the ramp's side edge.
        /// </summary>
        public static Point2D? CalculatePlacement(WallData wall)
        {
            var segments = wall.WallSegments.Where(s => s?.Position != null).ToList();
            if (wall.RampCenter == null || segments.Count < 2)
            {
                return null;
            }

            var ramp = new Vector2(wall.RampCenter.X, wall.RampCenter.Y);
            var first = segments[0];
            var second = segments[1];
            var firstCenter = new Vector2(first.Position.X, first.Position.Y);
            var secondCenter = new Vector2(second.Position.X, second.Position.Y);
            var firstHalf = first.Size / 2f;
            var secondHalf = second.Size / 2f;

            // Shared corner of the L: each segment's corner vertex nearest the
            // other segment's center, averaged (they describe the same physical
            // corner from opposite sides).
            var corner = (NearestCorner(firstCenter, firstHalf, secondCenter)
                + NearestCorner(secondCenter, secondHalf, firstCenter)) / 2f;

            // Pocket normals: per segment, the axis-aligned unit normal of the
            // face that looks toward the ramp (the pocket sits between the wall
            // and the ramp, on the base side of the ramp top).
            var firstNormal = BestAxisNormal(Normalize(ramp - firstCenter));
            var secondNormal = BestAxisNormal(Normalize(ramp - secondCenter));

            var center = corner + HatcheryRadius * firstNormal + HatcheryRadius * secondNormal;
            return new Point2D { X = center.X, Y = center.Y };
        }

        /// <summary>
        /// Draws the footprint circle + label, like the Spawning Pool placement
        /// sphere. Green = spec point is buildable; yellow = spec point failed,
        /// showing the spiral-adjusted legal spot; red = nothing buildable
        /// nearby. When adjusted, the red spec circle is drawn too so the
        /// offset is visible in-game.
        /// </summary>
        public void DrawPlacement(MacroHatcheryPlacement placement)
        {
            if (_defaultBot?.DebugService == null || placement?.Spec == null)
            {
                return;
            }

            var green = new Color { R = 0, G = 255, B = 0 };
            var red = new Color { R = 255, G = 0, B = 0 };
            var yellow = new Color { R = 255, G = 255, B = 0 };

            var drawn = placement.Final;
            if (drawn == null)
            {
                return;
            }

            // Owner correction 2026-10-16: "must be on flat even surface, no
            // obstructions" — the circle color now reports the FINAL point's
            // grid validity: green = legal as drawn, yellow = snapped from
            // the grid candidate, red = nothing legal nearby.
            var adjusted = placement.Adjusted != null;
            var drawnColor = !placement.FinalValid ? red : adjusted ? yellow : green;
            _defaultBot.DebugService.DrawSphere(new Point { X = drawn.X, Y = drawn.Y, Z = 12 }, HatcheryRadius, drawnColor);
            _defaultBot.DebugService.DrawText($"MacroHatchery{(placement.FinalValid ? adjusted ? " snap" : "" : " INVALID")}", new Point { X = drawn.X, Y = drawn.Y, Z = 12.5f }, drawnColor, 12);

            // Candidate dots (yellow) so the owner can eyeball which wall-flush
            // spot sits right against the ramp edge; the red spec circle shows
            // the raw corner point when it missed.
            foreach (var candidate in placement.Candidates)
            {
                _defaultBot.DebugService.DrawSphere(new Point { X = candidate.X, Y = candidate.Y, Z = 12 }, 0.4f, yellow);
            }

            if (adjusted)
            {
                _defaultBot.DebugService.DrawSphere(new Point { X = placement.Spec.X, Y = placement.Spec.Y, Z = 12 }, HatcheryRadius, red);
            }

            // White dot: the owner's arrow target (offset applied, before the
            // legality snap), so the owner sees where he pointed vs where the
            // nearest flat legal ground actually is.
            if (placement.OwnerTarget != null)
            {
                var white = new Color { R = 255, G = 255, B = 255 };
                _defaultBot.DebugService.DrawSphere(new Point { X = placement.OwnerTarget.X, Y = placement.OwnerTarget.Y, Z = 12 }, 0.4f, white);
            }

            // Cyan dots: the legal flat-ground options nearest the owner
            // target that the snap chose from (nearest = also where Final is).
            var cyan = new Color { R = 0, G = 255, B = 255 };
            foreach (var option in placement.LegalNearOwnerTarget)
            {
                _defaultBot.DebugService.DrawSphere(new Point { X = option.X, Y = option.Y, Z = 12 }, 0.4f, cyan);
            }
        }

        private static Vector2 NearestCorner(Vector2 segmentCenter, float half, Vector2 toward)
        {
            var dx = toward.X >= segmentCenter.X ? half : -half;
            var dy = toward.Y >= segmentCenter.Y ? half : -half;
            return new Vector2(segmentCenter.X + dx, segmentCenter.Y + dy);
        }

        private static Vector2 BestAxisNormal(Vector2 direction)
        {
            var candidates = new[]
            {
                new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1)
            };
            return candidates.OrderByDescending(n => Vector2.Dot(n, direction)).First();
        }

        private static Vector2 Normalize(Vector2 v)
        {
            var length = v.Length();
            return length < 0.0001f ? new Vector2(1, 0) : v / length;
        }

        private static float DistanceSquared(Point2D a, Vector2Dto b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }
    }

    /// <summary>Result of a Macro Hatchery placement calculation.</summary>
    public class MacroHatcheryPlacement
    {
        public WallData? Wall { get; set; }
        public Point2D? Spec { get; set; }
        public bool SpecValid { get; set; }

        /// <summary>Wall-flush candidate centers (corner-most first), for the
        /// owner to eyeball against the ramp in-game.</summary>
        public List<Point2D> Candidates { get; set; } = new List<Point2D>();

        public Point2D? Adjusted { get; set; }

        // Owner override 2026-10-16: "Please move the Hatchery location to the
        // indicated spot" — the owner pointed ~left 4 / up 3 from the grid-
        // derived corner candidate (toward the ramp corner). Owner correction
        // same day: "You can not place on a ramp, must be on flat even
        // surface. No obstructions. First get the location correct and a
        // legal placement." So the owner offset gives the DIRECTION, and the
        // final point is the nearest LEGAL .5-grid placement to that target
        // (SnapToBuildable) — as close to the arrow as the flat ground
        // allows. The offset is applied LAST, on top of whatever the geometry
        // picked.
        public Point2D? OwnerTarget { get; set; }
        public Point2D? Final { get; set; }
        public bool FinalValid { get; set; }

        /// <summary>Legal .5-grid placements near the owner target, nearest
        /// first, so the owner can see the flat-ground options the snap
        /// chose from.</summary>
        public List<Point2D> LegalNearOwnerTarget { get; set; } = new List<Point2D>();
    }

    /// <summary>Owner's visual correction (owner spec 2026-10-16): the grid-
    /// derived corner candidate sits ~3 units too low and ~4 units too right
    /// of the intended corner spot. Left 4 / up 3 in game coordinates.</summary>
    public static class MacroHatcheryOwnerOffset
    {
        public const float Left = 4f;
        public const float Up = 3f;

        public static Point2D Apply(Point2D chosen)
        {
            return new Point2D { X = chosen.X - Left, Y = chosen.Y - Up };
        }
    }
}
