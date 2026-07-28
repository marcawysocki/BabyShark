Write a C# function for a Sharky-based StarCraft 2 bot that calculates and draws the spawning pool placement.

The goal is to place the spawning pool so it touches the vespene on one corner and starts a mini wall. The chosen corner must be away from the minerals so it does not interfere with worker paths to minerals or gas, and it must not leave a walkable gap.

Use:
- `start[0]` hatchery position
- COM mineral position
- `V2` vespene geyser position

Geometry:
- circle A: centered on the geyser, radius `3`
- circle B: centered on the hatchery, radius equal to the hatchery-to-geyser distance minus `1.5`

Those circles intersect at two candidate positions. Choose the candidate that is farther from the mineral center.

## C# example

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SC2APIProtocol;
using Sharky;

namespace BabySharkBot.Services
{
    public class SpawningPoolPlacementService
    {
        private readonly DebugService debugService;

        public SpawningPoolPlacementService(DebugService debugService)
        {
            this.debugService = debugService;
        }

        public Point2D? CalculateSpawningPoolPlacement(Point2D hatcheryStart, Point2D mineralCom, Point2D vespeneV2)
        {
            if (hatcheryStart == null || mineralCom == null || vespeneV2 == null)
            {
                return null;
            }

            var geyserCenter = new Vector2(vespeneV2.X, vespeneV2.Y);
            var hatcheryCenter = new Vector2(hatcheryStart.X, hatcheryStart.Y);
            var mineralCenter = new Vector2(mineralCom.X, mineralCom.Y);

            var hatcheryToGeyser = Vector2.Distance(hatcheryCenter, geyserCenter);
            var hatcheryRadius = Math.Max(0f, hatcheryToGeyser - 1.5f);
            const float geyserRadius = 3f;

            var candidates = IntersectCircles(hatcheryCenter, hatcheryRadius, geyserCenter, geyserRadius);
            if (candidates.Count == 0)
            {
                return null;
            }

            var chosen = candidates[0];
            if (candidates.Count > 1)
            {
                var firstDistance = Vector2.DistanceSquared(candidates[0], mineralCenter);
                var secondDistance = Vector2.DistanceSquared(candidates[1], mineralCenter);
                chosen = secondDistance > firstDistance ? candidates[1] : candidates[0];
            }

            var placement = new Point2D { X = chosen.X, Y = chosen.Y };
            DrawSpawningPoolPlacement(placement);
            return placement;
        }

        private List<Vector2> IntersectCircles(Vector2 c0, float r0, Vector2 c1, float r1)
        {
            var results = new List<Vector2>();
            var d = Vector2.Distance(c0, c1);

            if (d <= 0.0001f)
            {
                return results;
            }

            if (d > r0 + r1 || d < Math.Abs(r0 - r1))
            {
                return results;
            }

            var a = (r0 * r0 - r1 * r1 + d * d) / (2f * d);
            var hSq = Math.Max(0f, r0 * r0 - a * a);
            var h = (float)Math.Sqrt(hSq);

            var direction = (c1 - c0) / d;
            var midpoint = c0 + a * direction;

            var perpendicular = new Vector2(-direction.Y, direction.X);
            var p1 = midpoint + h * perpendicular;
            var p2 = midpoint - h * perpendicular;

            results.Add(p1);
            if (Vector2.DistanceSquared(p1, p2) > 0.0001f)
            {
                results.Add(p2);
            }

            return results;
        }

        private void DrawSpawningPoolPlacement(Point2D placement)
        {
            if (debugService == null || placement == null)
            {
                return;
            }

            debugService.DrawSphere(new Point { X = placement.X, Y = placement.Y, Z = 12 });
        }
    }
}
```

## Notes

- The function prefers the intersection farther from the mineral COM.
- The pool is shifted by half a footprint using the `-1.5` hatchery radius adjustment.
- Add the returned placement to your existing drawing/debug collection if you want to render it with other map overlays.

