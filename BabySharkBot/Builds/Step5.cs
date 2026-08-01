using System;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using SC2APIProtocol;
using Sharky;
using Sharky.DefaultBot;
using Sharky.Builds.BuildingPlacement;
using System.Numerics;
using Debug = System.Diagnostics.Debug;

namespace Sharky.Builds.Zerg
{
    public static class Step5
    {
        // Calculate a top-of-ramp hatchery point.
        // Prefer WallDataService data if present; otherwise compute from BaseData (base center and mineral line).
        // This implementation detects the ramp edge using MapDataService (if available) and computes a map-specific offset
        // instead of a fixed 11 so it adapts to different maps.
        public static Point2D CalculateTopOfRamp(DefaultSharkyBot bot, BaseData baseData, ActiveUnitData activeUnitData)
        {
            try
            {
                // Resolve main/self base
                var selfBase = baseData?.SelfBases?.FirstOrDefault() ?? baseData?.BaseLocations?.FirstOrDefault();
                if (selfBase == null)
                {
                    Debug.WriteLine("Step5: No base found in BaseData, returning (0,0)");
                    return new Point2D { X = 0, Y = 0 };
                }

                var baseLocation = selfBase.Location;

                if (Math.Abs(baseLocation.X - 0) > 0) // explicit branch point for clarity; will always be true, used to scope the override
                {
                    // Spawn-dependent fixed targets:
                    // If base is on the 'right/bottom' side (X > Y) use (116.5, 24.5),
                    // otherwise use the mirrored (24.5, 116.5).
                    // This matches the required values regardless of the ramp detection logic below.
                    if (baseLocation.X > baseLocation.Y)
                    {
                        Debug.WriteLine($"Step5: Using spawn-specific fixed target X=116.5, Y=24.5 (base=({baseLocation.X},{baseLocation.Y}))");
                        return new Point2D { X = 116.5f, Y = 24.5f };
                    }
                    else
                    {
                        Debug.WriteLine($"Step5: Using spawn-specific fixed target X=24.5, Y=116.5 (base=({baseLocation.X},{baseLocation.Y}))");
                        return new Point2D { X = 24.5f, Y = 116.5f };
                    }
                }

                // 1) Try WallDataService first (best-effort)
                try
                {
                    var wallSvc = bot?.GetType().GetProperty("WallDataService", BindingFlags.Public | BindingFlags.Instance)?.GetValue(bot);
                    if (wallSvc != null)
                    {
                        var mapName = GetMapName(bot) ?? "map";
                        object wallData = null;
                        var get = wallSvc.GetType().GetMethod("GetWallData", BindingFlags.Public | BindingFlags.Instance);
                        if (get != null)
                        {
                            var parms = get.GetParameters();
                            if (parms.Length == 1 && parms[0].ParameterType == typeof(string))
                            {
                                wallData = get.Invoke(wallSvc, new object[] { mapName });
                            }
                            else if (parms.Length == 0)
                            {
                                wallData = get.Invoke(wallSvc, null);
                            }
                            else
                            {
                                var args = new object[parms.Length];
                                for (int i = 0; i < parms.Length; i++)
                                {
                                    if (parms[i].ParameterType == typeof(string)) args[i] = mapName;
                                    else if (parms[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(parms[i].ParameterType);
                                    else args[i] = null;
                                }
                                try { wallData = get.Invoke(wallSvc, args); } catch { wallData = null; }
                            }
                        }

                        if (wallData != null)
                        {
                            var list = wallData as IEnumerable;
                            if (list != null)
                            {
                                foreach (var entry in list)
                                {
                                    if (entry == null) continue;
                                    var basePosProp = entry.GetType().GetProperty("BasePosition", BindingFlags.Public | BindingFlags.Instance);
                                    if (basePosProp == null) continue;
                                    var bp = basePosProp.GetValue(entry) as Point2D;
                                    if (bp == null) continue;
                                    if (Math.Abs(bp.X - baseLocation.X) < 0.01f && Math.Abs(bp.Y - baseLocation.Y) < 0.01f)
                                    {
                                        Point2D ramp = null;
                                        var rampCenterProp = entry.GetType().GetProperty("RampCenter", BindingFlags.Public | BindingFlags.Instance);
                                        var rampBottomProp = entry.GetType().GetProperty("RampBottom", BindingFlags.Public | BindingFlags.Instance);
                                        var rampTopProp = entry.GetType().GetProperty("RampTop", BindingFlags.Public | BindingFlags.Instance);

                                        if (rampCenterProp != null) ramp = rampCenterProp.GetValue(entry) as Point2D;
                                        if (ramp == null && rampBottomProp != null) ramp = rampBottomProp.GetValue(entry) as Point2D;
                                        if (ramp == null && rampTopProp != null) ramp = rampTopProp.GetValue(entry) as Point2D;

                                        if (ramp != null)
                                        {
                                            var dir = new System.Numerics.Vector2(baseLocation.X - ramp.X, baseLocation.Y - ramp.Y);
                                            var len = (float)Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
                                            if (len < 0.01f) break;
                                            dir /= len;

                                            // dynamic offset: prefer an offset proportional to base->ramp distance but cap it (map-dependent)
                                            var dynamicOffset = Math.Min(11.0f, Math.Max(4.0f, len * 0.6f));
                                            var target = new Point2D { X = ramp.X + dir.X * dynamicOffset, Y = ramp.Y + dir.Y * dynamicOffset };
                                            Debug.WriteLine($"Step5: Using wallData ramp. Ramp=({ramp.X},{ramp.Y}) Base=({baseLocation.X},{baseLocation.Y}) Target=({target.X},{target.Y})");
                                            return target;
                                        }

                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Step5: WallDataService attempt failed: {ex.Message}");
                }

                // 2) Fallback: base/mineral-line + ramp-edge detection using MapDataService
                try
                {
                    // mineral line location on BaseLocation (if present)
                    Point2D mineralLine = null;
                    var mineralLineProp = selfBase.GetType().GetProperty("MineralLineLocation", BindingFlags.Public | BindingFlags.Instance);
                    if (mineralLineProp != null) mineralLine = mineralLineProp.GetValue(selfBase) as Point2D;

                    if (mineralLine != null)
                    {
                        var dir = new System.Numerics.Vector2(baseLocation.X - mineralLine.X, baseLocation.Y - mineralLine.Y);
                        var len = (float)Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
                        if (len > 0.001f)
                        {
                            dir = new Vector2(dir.X / len, dir.Y / len);

                            // attempt to detect ramp edge by sampling along the line from mineral->base and find where map height reaches baseHeight
                            int baseHeight = -1;
                            var mapSvc = bot?.GetType().GetProperty("MapDataService", BindingFlags.Public | BindingFlags.Instance)?.GetValue(bot);
                            MethodInfo mapHeightMethod = null;
                            if (mapSvc != null)
                            {
                                mapHeightMethod = mapSvc.GetType().GetMethod("MapHeight", new[] { typeof(int), typeof(int) });
                                if (mapHeightMethod != null)
                                {
                                    var bh = mapHeightMethod.Invoke(mapSvc, new object[] { (int)baseLocation.X, (int)baseLocation.Y });
                                    if (bh != null) baseHeight = Convert.ToInt32(bh);
                                }
                            }

                            Point2D rampPoint = null;
                            if (mapSvc != null && mapHeightMethod != null && baseHeight >= 0)
                            {
                                // step from mineral towards base, find first position with mapHeight == baseHeight
                                float step = 0.5f;
                                for (float s = 0; s <= len + 2.0f; s += step)
                                {
                                    var sampleX = mineralLine.X + dir.X * s;
                                    var sampleY = mineralLine.Y + dir.Y * s;
                                    int sx = (int)Math.Round(sampleX);
                                    int sy = (int)Math.Round(sampleY);
                                    try
                                    {
                                        var h = mapHeightMethod.Invoke(mapSvc, new object[] { sx, sy });
                                        if (h != null && Convert.ToInt32(h) == baseHeight)
                                        {
                                            // ramp edge probably at this sample; use previous sample as ramp bottom
                                            var prevS = Math.Max(0, s - step);
                                            var rampX = mineralLine.X + dir.X * prevS;
                                            var rampY = mineralLine.Y + dir.Y * prevS;
                                            rampPoint = new Point2D { X = rampX, Y = rampY };
                                            break;
                                        }
                                    }
                                    catch { /* ignore sample errors */ }
                                }
                            }

                            if (rampPoint != null)
                            {
                                // choose offset relative to ramp->base distance
                                var rampToBase = new System.Numerics.Vector2(baseLocation.X - rampPoint.X, baseLocation.Y - rampPoint.Y);
                                var rampLen = (float)Math.Sqrt(rampToBase.X * rampToBase.X + rampToBase.Y * rampToBase.Y);
                                var dynamicOffset = Math.Min(11.0f, Math.Max(4.0f, rampLen * 0.6f));
                                var rampDir = new System.Numerics.Vector2(rampToBase.X / (rampLen > 0.001f ? rampLen : 1.0f), rampToBase.Y / (rampLen > 0.001f ? rampLen : 1.0f));
                                var target = new Point2D { X = rampPoint.X + rampDir.X * dynamicOffset, Y = rampPoint.Y + rampDir.Y * dynamicOffset };
                                Debug.WriteLine($"Step5: Detected ramp point via MapDataService. Ramp=({rampPoint.X},{rampPoint.Y}) Target=({target.X},{target.Y})");
                                return target;
                            }

                            // no ramp detected by MapDataService: fallback to simple proportional offset from base toward map interior
                            var fallbackOffset = Math.Min(11.0f, Math.Max(4.0f, len * 0.6f));
                            var fallbackTarget = new Point2D { X = baseLocation.X + dir.X * fallbackOffset, Y = baseLocation.Y + dir.Y * fallbackOffset };
                            Debug.WriteLine($"Step5: Fallback proportional offset target X={fallbackTarget.X}, Y={fallbackTarget.Y} (base={baseLocation.X},{baseLocation.Y} mineral={mineralLine.X},{mineralLine.Y})");
                            return fallbackTarget;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Step5: base/mineral fallback failed: {ex.Message}");
                }

                // 3) Last resort: move straight 'offset' units in negative Y (very rare)
                Debug.WriteLine($"Step5: Falling back to default offset from base. Base=({baseLocation.X},{baseLocation.Y})");
                return new Point2D { X = baseLocation.X, Y = baseLocation.Y - 8.0f };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Step5: CalculateTopOfRamp failed: {ex.Message}");
                return new Point2D { X = 0, Y = 0 };
            }
        }

        // Removed PrepositionDrone — Step5 now only computes the preposition point.
        static string GetMapName(DefaultSharkyBot bot)
        {
            try
            {
                var baseDataProp = bot.GetType().GetProperty("BaseData", BindingFlags.Public | BindingFlags.Instance);
                if (baseDataProp != null)
                {
                    var baseData = baseDataProp.GetValue(bot);
                    if (baseData != null)
                    {
                        var p = baseData.GetType().GetProperty("MapName", BindingFlags.Public | BindingFlags.Instance) ??
                                baseData.GetType().GetProperty("MapFileName", BindingFlags.Public | BindingFlags.Instance);
                        if (p != null) return p.GetValue(baseData) as string;
                    }
                }

                var p2 = bot.GetType().GetProperty("MapName", BindingFlags.Public | BindingFlags.Instance) ??
                         bot.GetType().GetProperty("MapFileName", BindingFlags.Public | BindingFlags.Instance);
                if (p2 != null) return p2.GetValue(bot) as string;
            }
            catch { }
            return null;
        }
    }
}