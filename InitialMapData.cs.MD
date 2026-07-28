using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MemoryPack;
using Newtonsoft.Json;
using SC2APIProtocol;
using Maw.Setup;
using MemoryPack; // Add this line
using System.Numerics;

namespace Maw.Managers
{
    public class InitialMapData
    {
        public MawBaseLocationData GetNewMiningData(ResponseGameInfo gameInfo, ResponseData data, ResponseObservation observation, Point2D startLoc, WorkerLabelService? workerLabelService = null, CrosshairService? crosshairService = null)
        {
            Console.WriteLine("InitialMapData: GetNewMiningData called");

            // Initialize CrosshairService if provided (no special initialization needed, just placeholder for consistency)
            try
            {
                if (crosshairService != null)
                {
                    // CrosshairService is already initialized by GameConnection.Run() with the proxy
                    // Just ensure it has the latest raw units
                    var rawUnits = observation?.Observation?.RawData?.Units?.Where(u => u != null).ToList();
                    if (rawUnits != null)
                    {
                        crosshairService.UpdateRawUnits(rawUnits);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData: CrosshairService initialization failed: {ex.Message}");
            }

            var mineralTypes = new HashSet<UnitTypes>
            {
                UnitTypes.NEUTRAL_MINERALFIELD,
                UnitTypes.NEUTRAL_MINERALFIELD750,
                UnitTypes.NEUTRAL_RICHMINERALFIELD,
                UnitTypes.NEUTRAL_RICHMINERALFIELD750,
                UnitTypes.NEUTRAL_PURIFIERMINERALFIELD,
                UnitTypes.NEUTRAL_PURIFIERMINERALFIELD750,
                UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD,
                UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD750,
                UnitTypes.NEUTRAL_LABMINERALFIELD,
                UnitTypes.NEUTRAL_LABMINERALFIELD750,
                UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD,
                UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD750
            };

            var vespeneTypes = new HashSet<UnitTypes>
            {
                UnitTypes.NEUTRAL_VESPENEGEYSER,
                UnitTypes.NEUTRAL_RICHVESPENEGEYSER,
                UnitTypes.NEUTRAL_PURIFIERVESPENEGEYSER
            };

            // Lists will be populated in a single pass after we have start locations
            // so that units may also be assigned to their nearest start when needed.
            var mineralList = new List<Vector2Dto>();
            var vespeneList = new List<Vector2Dto>();
            // Temporary list of starting workers discovered in the raw units scan.
            // Store tag and position so we can register labels immediately when a tag is available.
            var workerList = new List<(ulong Tag, float X, float Y)>();

            // Extract all start locations from the game info

            var startLocations = new List<Vector2Dto>();
            var startLocationMineralCenters = new List<Vector2Dto>();
            int startLocationIndex = 0;

            
            // Use only the first API start location for processing. Do not iterate over
            // multiple start entries from the API. We will always ensure the serialized
            // record contains `ExpectedStartLocations` entries by appending zeroed
            // placeholders if needed, but processing (unit assignment / labeling)
            // uses the single real start location (index 0).
            try
            {
                var apiStarts = gameInfo?.StartRaw?.StartLocations;
                var apiLoc = observation?.Observation?.RawData?.Units?.FirstOrDefault(u => u != null && u.Alliance == Alliance.Self && (u.UnitType == (uint)UnitTypes.ZERG_HATCHERY || u.UnitType == (uint)UnitTypes.TERRAN_COMMANDCENTER || u.UnitType == (uint)UnitTypes.PROTOSS_NEXUS))?.Pos;

                // Debug: print what the API returned and the observed base unit
                try
                {
                    if (apiStarts != null) Console.WriteLine($"InitialMapData: API returned {apiStarts.Count} start locations");
                    if (apiStarts != null)
                    {
                        for (int i = 0; i < apiStarts.Count; i++)
                        {
                            var a = apiStarts[i];
                            Console.WriteLine($"InitialMapData: API Start[{i}] = X={a.X:F2}, Y={a.Y:F2}");
                        }
                    }
                    if (apiLoc != null)
                    {
                        Console.WriteLine($"InitialMapData: observed base unit at = X={apiLoc.X:F2}, Y={apiLoc.Y:F2}");
                    }
                    if (startLoc != null)
                    {
                        Console.WriteLine($"InitialMapData: provided startLoc = X={startLoc.X:F2}, Y={startLoc.Y:F2}");
                    }
                }
                catch { }

                // Build startLocations so that apiLoc (observed base) is location 0 if present,
                // and API-provided start locations follow as locations 1,2,... (excluding duplicates).
                const float tol = 0.01f;
                if (apiLoc != null)
                {
                    startLocations.Add(new Vector2Dto(apiLoc.X, apiLoc.Y));
                    startLocationIndex = 0; // apiLoc is now slot 0

                    if (apiStarts != null)
                    {
                        for (int i = 0; i < apiStarts.Count; i++)
                        {
                            var s = apiStarts[i];
                            if (Math.Abs(s.X - apiLoc.X) < tol && Math.Abs(s.Y - apiLoc.Y) < tol) continue; // skip duplicate
                            startLocations.Add(new Vector2Dto(s.X, s.Y));
                        }
                    }
                }
                if (apiStarts != null && apiStarts.Count > 0)
                {
                    // No observed base unit; use API starts in order (first is slot 0)
                    foreach (var s in apiStarts) startLocations.Add(new Vector2Dto(s.X, s.Y));
                    
                }
               
            }
            catch { }

            // Use engine-provided start locations from ResponseGameInfo.StartRaw as authoritative.
            // Do not fallback to external sources here; Maw-only runs rely on the API.

            // Debug: print discovered start locations and player start index
            try
            {
                Console.WriteLine($"InitialMapData: discovered {startLocations.Count} start locations");
                for (int i = 0; i < startLocations.Count; i++)
                {
                    var s = startLocations[i];
                    Console.WriteLine($"InitialMapData: StartLocation[{i}] = X={s.X:F2}, Y={s.Y:F2}");
                }
                Console.WriteLine($"InitialMapData: player StartLocationIndex = {startLocationIndex}");
            }
            catch { }

            // No inference or fallbacks allowed here; rely strictly on gameInfo.StartRaw.StartLocations.

            // Single-pass unit scan: collect minerals, vespene, and workers, and map
            // discovered start units to nearest start location for later use.
            var discoveredStartUnits = new Dictionary<int, List<Unit>>();
            for (int i = 0; i < startLocations.Count; i++) discoveredStartUnits[i] = new List<Unit>();

            // Per-start counters for deterministic labeling of static starters (Hatchery, Overlord, Larva)
            var hatcheryCounters = new Dictionary<int, int>();
            var overlordCounters = new Dictionary<int, int>();
            var larvaCounters = new Dictionary<int, int>();
            for (int i = 0; i < startLocations.Count; i++)
            {
                hatcheryCounters[i] = 0;
                overlordCounters[i] = 0;
                larvaCounters[i] = 0;
            }

            // Prepare trackers for main-mineral center computation collected during the single-pass scan.
            var mainCenterOfMinerals = new List<Vector2Dto>();
            var mainMineralsTemp = new List<Vector2Dto>();
            var mainVespeneTemp = new List<Vector2Dto>();
            // Temp DTO to be populated during the single-pass so BaseLocationData lists
            // (MineralPatches/MainMinerals/VespenePatches/MainVespene) are filled immediately.
            var tempBaseDto = new MawBaseLocationData();
            double comX = 0.0, comY = 0.0, comDistance = 0.0;
            int nodeCount = 0;

            if (observation?.Observation?.RawData?.Units != null)
            {
                foreach (var unit in observation.Observation.RawData.Units)
                {
                    try
                    {
                        if (unit?.Pos == null) continue;
                        var ut = (UnitTypes)unit.UnitType;
                        if (mineralTypes.Contains(ut))
                        {
                            mineralList.Add(new Vector2Dto(unit.Pos.X, unit.Pos.Y));
                            // Also populate the DTO mineral patches immediately during the single-pass
                            try { tempBaseDto.MapLocationData.MineralPatches.Add(new Vector2Dto(unit.Pos.X, unit.Pos.Y)); } catch { }

                            // If this mineral node is within mainDistance (9) of start slot 0, include in COM accumulation
                            // and add to the main-minerals temporary list so we don't need to re-filter later.
                            try
                            {
                                if (startLocations != null && startLocations.Count > 0)
                                {
                                    var s0 = startLocations[0];
                                    // use Vector2.Distance for clarity and correctness
                                    var dist = Vector2.Distance(new Vector2(unit.Pos.X, unit.Pos.Y), new Vector2(s0.X, s0.Y));
                                    if (dist < 9.0f)
                                    {
                                        var v = new Vector2Dto(unit.Pos.X, unit.Pos.Y);
                                        mainCenterOfMinerals.Add(v);
                                        mainMineralsTemp.Add(v);
                                        // also populate DTO lists immediately
                                        tempBaseDto.MainMinerals.Add(new Vector2Dto(unit.Pos.X, unit.Pos.Y));
                                        comX += unit.Pos.X;
                                        comY += unit.Pos.Y;
                                        comDistance += dist;
                                        nodeCount++;
                                    }
                                }
                            }
                            catch { }
                        }
                        else if (vespeneTypes.Contains(ut))
                        {
                            var vpos = new Vector2Dto(unit.Pos.X, unit.Pos.Y);
                            vespeneList.Add(vpos);
                            // Also populate DTO vespene list immediately
                            tempBaseDto.MapLocationData.VespenePatches.Add(vpos);

                            // If vespene geyser is within mainDistance of start slot 0, record it in the temporary list.
                            try
                            {
                                if (startLocations != null && startLocations.Count > 0)
                                {
                                    var s0 = startLocations[0];
                                    var dist = Vector2.Distance(new Vector2(unit.Pos.X, unit.Pos.Y), new Vector2(s0.X, s0.Y));
                                    if (dist < 9.0f)
                                    {
                                        var v = new Vector2Dto(unit.Pos.X, unit.Pos.Y);
                                        mainVespeneTemp.Add(v);
                                        tempBaseDto.MainVespene.Add(v);
                                    }
                                }
                            }
                            catch { }
                        }
                        else if (ut == UnitTypes.ZERG_DRONE || ut == UnitTypes.TERRAN_SCV || ut == UnitTypes.PROTOSS_PROBE)
                        {
                            // collect tag and position so we can match tags to greedy ordering later
                            try { workerList.Add((unit.Tag, unit.Pos.X, unit.Pos.Y)); } catch { }
                        }

                        // If this is a self starting unit, map it to nearest start index
                        if (unit.Alliance == Alliance.Self)
                        {
                            int bestIdx = -1; float bestD = float.MaxValue;
                            for (int si = 0; si < startLocations.Count; si++)
                            {
                                var s = startLocations[si];
                                var d = Vector2.Distance(new Vector2(unit.Pos.X, unit.Pos.Y), new Vector2(s.X, s.Y));
                                if (d < bestD) { bestD = d; bestIdx = si; }
                            }
                            if (bestIdx >= 0 && bestD <= 9f)
                            {
                                if (ut == UnitTypes.ZERG_HATCHERY)
                                {
                                    discoveredStartUnits[bestIdx].Add(unit);
                                    try
                                    {
                                        hatcheryCounters[bestIdx] = hatcheryCounters.GetValueOrDefault(bestIdx) + 1;
                                        var label = $"H{hatcheryCounters[bestIdx]}";
                                        if (workerLabelService != null && unit.Tag != 0)
                                        {
                                            var existing = workerLabelService.GetLabel(unit.Tag);
                                            if (string.IsNullOrEmpty(existing))
                                            {
                                                workerLabelService.SetLabel(label, unit.Tag);
                                                Console.WriteLine($"InitialMapData: Created label {label} -> {unit.Tag}");
                                            }
                                            else
                                            {
                                                Console.WriteLine($"InitialMapData: Existing label for tag {unit.Tag} = {existing}");
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                else if (ut == UnitTypes.ZERG_OVERLORD)
                                {
                                    discoveredStartUnits[bestIdx].Add(unit);
                                    try
                                    {
                                        overlordCounters[bestIdx] = overlordCounters.GetValueOrDefault(bestIdx) + 1;
                                        var label = $"OV{overlordCounters[bestIdx]}";
                                        if (workerLabelService != null && unit.Tag != 0)
                                        {
                                            var existing = workerLabelService.GetLabel(unit.Tag);
                                            if (string.IsNullOrEmpty(existing))
                                            {
                                                workerLabelService.SetLabel(label, unit.Tag);
                                                Console.WriteLine($"InitialMapData: Created label {label} -> {unit.Tag}");
                                            }
                                            else
                                            {
                                                Console.WriteLine($"InitialMapData: Existing label for tag {unit.Tag} = {existing}");
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                else if (ut == UnitTypes.ZERG_LARVA)
                                {
                                    discoveredStartUnits[bestIdx].Add(unit);
                                    try
                                    {
                                        larvaCounters[bestIdx] = larvaCounters.GetValueOrDefault(bestIdx) + 1;
                                        var label = $"L{larvaCounters[bestIdx]}";
                                        if (workerLabelService != null && unit.Tag != 0)
                                        {
                                            var existing = workerLabelService.GetLabel(unit.Tag);
                                            if (string.IsNullOrEmpty(existing))
                                            {
                                                workerLabelService.SetLabel(label, unit.Tag);
                                                Console.WriteLine($"InitialMapData: Created label {label} -> {unit.Tag}");
                                            }
                                            else
                                            {
                                                Console.WriteLine($"InitialMapData: Existing label for tag {unit.Tag} = {existing}");
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                else if (ut == UnitTypes.ZERG_DRONE || ut == UnitTypes.TERRAN_SCV || ut == UnitTypes.PROTOSS_PROBE)
                                {
                                    // Worker: collect position only; labeling deferred until greedy chain
                                    discoveredStartUnits[bestIdx].Add(unit);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // (start-location mineral centers are computed after we determine the
            // global main mineral center-of-mass below)

            // Filter minerals and vespene within distance 9 from start location
            const float mainDistance = 9f;
            // Prefer the temporary lists populated during the single-pass scan when available.
            var mainMinerals = (mainMineralsTemp != null && mainMineralsTemp.Count > 0)
                ? new List<Vector2Dto>(mainMineralsTemp)
                : mineralList.Where(m => Vector2.Distance(new Vector2(m.X, m.Y), new Vector2(startLoc.X, startLoc.Y)) < mainDistance).ToList();

            var mainVespene = (mainVespeneTemp != null && mainVespeneTemp.Count > 0)
                ? new List<Vector2Dto>(mainVespeneTemp)
                : vespeneList.Where(v => Vector2.Distance(new Vector2(v.X, v.Y), new Vector2(startLoc.X, startLoc.Y)) < mainDistance).ToList();

            // Calculate center of mass for main minerals.
            // Prefer the COM accumulated during the single-pass scan (comX/comY/nodeCount).
            Vector2Dto mineralCenterOfMass = null;
            if (nodeCount > 0)
            {
                var avgX = (float)(comX / nodeCount);
                var avgY = (float)(comY / nodeCount);
                mineralCenterOfMass = new Vector2Dto(avgX, avgY);
                try { tempBaseDto.MineralCenterOfMass = new Vector2Dto(avgX, avgY); } catch { }
                try
                {
                    var avgDistance = comDistance / nodeCount;
                    Console.WriteLine($"InitialMapData: main minerals COM=({avgX:F2},{avgY:F2}) nodes={nodeCount} avgDist={avgDistance:F2}");
                }
                catch { }
            }
            else
            {
                if (mainMinerals.Count > 0)
                {
                    float avgX = mainMinerals.Average(m => m.X);
                    float avgY = mainMinerals.Average(m => m.Y);
                    mineralCenterOfMass = new Vector2Dto(avgX, avgY);
                    try { tempBaseDto.MineralCenterOfMass = new Vector2Dto(avgX, avgY); } catch { }
                }
            }

            

   

            // Label discovered workers per start location using the temporary lists.
            // Algorithm: for each start, take the discovered drones (or worker positions)
            // and build a chain starting from the drone furthest from the start mineral
            // center-of-mass. That furthest drone receives label D{N} where N is the
            // number of drones observed for that start. The next chosen (closest to
            // the last) receives D{N-1}, etc. Do not hard-code N; use the observed count.
            try
            {
                
                // Use the workerList collected during the single-pass scan. Filter to workers
                // near the primary start and build the greedy chain from those positions.
                try
                {
                    
                    // Operate directly on workerList (already populated during single-pass) to avoid
                    // creating another filtered copy. Remove out-of-range entries in-place,
                    // then build the ordered chain by selecting and removing entries from the
                    // same list.
                    // Operate directly on workerList (already collected during single-pass).
                    // Remove out-of-range entries in-place, then build the ordered chain by
                    // selecting and removing entries from the same list. Do not create any
                    // additional worker copies.
                        if (workerList != null)
                        {
                            // Require a mineral center; do not fall back to start locations.
                            if (mineralCenterOfMass == null)
                            {
                                Console.WriteLine("InitialMapData: MineralCenterOfMass not available — skipping worker greedy labeling.");
                            }
                            else
                            {
                                // Step 1: single pass to select the furthest worker from the mineral center
                                int bestIdx = -1;
                                float bestD = float.MinValue;
                                for (int wi = 0; wi < workerList.Count; wi++)
                                {
                                    var w = workerList[wi];
                                    var d = Vector2.Distance(new Vector2(w.X, w.Y), new Vector2(mineralCenterOfMass.X, mineralCenterOfMass.Y));
                                    if (d > bestD)
                                    {
                                        bestD = d;
                                        bestIdx = wi;
                                    }
                                }

                                if (bestIdx >= 0)
                                {
                                    // Label the furthest unit with the highest number (count)
                                    var chosen = workerList[bestIdx];
                                    var label = $"D{workerList.Count}";
                                    try
                                    {
                                        tempBaseDto.StartingUnits.Add(new WorkerEntryDto
                                        {
                                            UnitTag = chosen.Tag,
                                            Position = new Vector2Dto(chosen.X, chosen.Y),
                                            UnitType = (uint)UnitTypes.ZERG_DRONE,
                                            Label = label
                                        });
                                        if (workerLabelService != null && chosen.Tag != 0)
                                        {
                                            workerLabelService.SetLabel(label, chosen.Tag);
                                            Console.WriteLine($"InitialMapData: Registered label {label} -> {chosen.Tag} at X={chosen.X:F2}, Y={chosen.Y:F2}");
                                        }
                                    }
                                    catch { }

                                    // set origin to chosen and remove it
                                    float fromX = chosen.X;
                                    float fromY = chosen.Y;
                                    workerList.RemoveAt(bestIdx);

                                    // Subsequent picks: choose the worker closest to the last chosen point
                                    while (workerList.Count > 0)
                                    {
                                        int nearestIdx = -1;
                                        float nearestD = float.MaxValue;
                                        for (int wi2 = 0; wi2 < workerList.Count; wi2++)
                                        {
                                            var w2 = workerList[wi2];
                                            var d2 = Vector2.Distance(new Vector2(w2.X, w2.Y), new Vector2(fromX, fromY));
                                            if (d2 < nearestD)
                                            {
                                                nearestD = d2;
                                                nearestIdx = wi2;
                                            }
                                        }

                                        if (nearestIdx < 0) break;

                                        var chosen2 = workerList[nearestIdx];
                                        var label2 = $"D{workerList.Count}";
                                        try
                                        {
                                            tempBaseDto.StartingUnits.Add(new WorkerEntryDto
                                            {
                                                UnitTag = chosen2.Tag,
                                                Position = new Vector2Dto(chosen2.X, chosen2.Y),
                                                UnitType = (uint)UnitTypes.ZERG_DRONE,
                                                Label = label2
                                            });
                                            if (workerLabelService != null && chosen2.Tag != 0)
                                            {
                                                workerLabelService.SetLabel(label2, chosen2.Tag);
                                                Console.WriteLine($"InitialMapData: Registered label {label2} -> {chosen2.Tag} at X={chosen2.X:F2}, Y={chosen2.Y:F2}");
                                            }
                                        }
                                        catch { }

                                        fromX = chosen2.X;
                                        fromY = chosen2.Y;
                                        workerList.RemoveAt(nearestIdx);
                                    }
                                }
                            }
                        }
                }
                catch { }
               
            }
            catch { }

            // Serialize the mineral and vespene lists into Maw.Globals.currentMapMineralsFullMap (but do not write to disk yet)
            try
            {
                var bytes = MemoryPackSerializer.Serialize(mineralList);
                Maw.Globals.currentMapMineralsFullMap = Convert.ToBase64String(bytes);
                Console.WriteLine($"InitialMapData: serialized {mineralList.Count} mineral patches and {vespeneList.Count} vespene geysers with MemoryPack ({bytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                // Fallback to JSON bytes if MemoryPack serialization isn't available for the DTOs yet
                var json = JsonConvert.SerializeObject(mineralList);
                var bytes = Encoding.UTF8.GetBytes(json);
                Maw.Globals.currentMapMineralsFullMap = Convert.ToBase64String(bytes);
                Console.WriteLine($"InitialMapData: MemoryPack failed ({ex.Message}), saved JSON fallback ({bytes.Length} bytes)");
            }

            // Pass the populated BaseLocationData to CrosshairService for drawing
            try
            {
                if (crosshairService != null && tempBaseDto != null)
                {
                    crosshairService.SetBaseLocationData(tempBaseDto);
                    Console.WriteLine($"InitialMapData: CrosshairService updated with base location data");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData: Failed to set CrosshairService base location data: {ex.Message}");
            }

            return tempBaseDto;
        }
    }

    }
