using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using MemoryPack;
using Newtonsoft.Json;
using SC2APIProtocol;
using Sharky;
using System.Numerics;
using BabySharkBot.Services;

#nullable enable

namespace BabySharkBot.Setup
{
    /// <summary>
    /// Generates initial map data when a new map is encountered.
    /// Handles single-pass unit scanning to collect minerals, vespene, and starting units.
    /// Performs greedy mineral ordering and calculates Near/Far classification for optimized mining.
    /// Registers Center of Mass (COM) for visualization.
    /// </summary>
    public class InitialMapData
    {
        private List<List<HarvestReturnCargoPointDto>> _expansionMineralCargoPoints = new List<List<HarvestReturnCargoPointDto>>();
        private List<List<HarvestReturnCargoPointDto>> _expansionVespeneCargoPoints = new List<List<HarvestReturnCargoPointDto>>();
        private List<List<TeamPatchAssignmentDto>> _teamPatchAssignments = new List<List<TeamPatchAssignmentDto>>();

        public MawBaseLocationData GetNewMiningData(ResponseGameInfo gameInfo, ResponseData data, ResponseObservation observation, Point2D? startLoc = null, CrosshairService? crosshairService = null, ExpansionCOMService? expansionCOMService = null, ExpansionPointService? expansionPointService = null, ExpansionPointDrawService? expansionPointDrawService = null, ProvisionalExpansionService? provisionalExpansionService = null, Sharky.Pathing.MapDataService? mapDataService = null, WorkerLabelService? workerLabelService = null)
        {
            Console.WriteLine("InitialMapData: GetNewMiningData called");

            
        

            var mineralTypes = new HashSet<Sharky.UnitTypes>
            {
                Sharky.UnitTypes.NEUTRAL_MINERALFIELD,
                Sharky.UnitTypes.NEUTRAL_MINERALFIELD750,
                Sharky.UnitTypes.NEUTRAL_RICHMINERALFIELD,
                Sharky.UnitTypes.NEUTRAL_RICHMINERALFIELD750,
                Sharky.UnitTypes.NEUTRAL_PURIFIERMINERALFIELD,
                Sharky.UnitTypes.NEUTRAL_PURIFIERMINERALFIELD750,
                Sharky.UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD,
                Sharky.UnitTypes.NEUTRAL_PURIFIERRICHMINERALFIELD750,
                Sharky.UnitTypes.NEUTRAL_LABMINERALFIELD,
                Sharky.UnitTypes.NEUTRAL_LABMINERALFIELD750,
                Sharky.UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD,
                Sharky.UnitTypes.NEUTRAL_BATTLESTATIONMINERALFIELD750
            };

            var suspectedMineralWall = new HashSet<Sharky.UnitTypes>
            {
                Sharky.UnitTypes.NEUTRAL_RICHMINERALFIELD
            };

            var vespeneTypes = new HashSet<Sharky.UnitTypes>
            {
                Sharky.UnitTypes.NEUTRAL_VESPENEGEYSER,
                Sharky.UnitTypes.NEUTRAL_SPACEPLATFORMGEYSER,
                Sharky.UnitTypes.NEUTRAL_SHAKURASVESPENEGEYSER,
                Sharky.UnitTypes.NEUTRAL_RICHVESPENEGEYSER,
                Sharky.UnitTypes.NEUTRAL_PURIFIERVESPENEGEYSER,
                Sharky.UnitTypes.NEUTRAL_PROTOSSVESPENEGEYSER
            };

            // Lists will be populated in a single pass after we have start locations
            // so that units may also be assigned to their nearest start when needed.
            var mineralList = new List<Vector2Dto>();
            var vespeneList = new List<Vector2Dto>();
            var expansionMineralsList = new List<Vector2Dto>();  // Minerals not near any start location
            var mineralTypeByPosition = new Dictionary<string, uint>();
            // Temporary list of starting workers discovered in the raw units scan.
            // Store tag, position, and unit type so we can register labels immediately when a tag is available.
            var workerList = new List<(ulong Tag, float X, float Y, float Z, uint UnitType)>();

            // Extract all start locations from the game info

            var startLocations = new List<Vector2Dto>();
            var startLocationMineralCenters = new List<Vector2Dto>();
            int startLocationIndex = 0;


            // Use only the first API start location for processing. Do not iterate over
            // multiple start entries from the API. We will always ensure the serialized
            // record contains `ExpectedStartLocations` entries by appending zeroed
            // placeholders if needed, but processing (unit assignment / labeling)
            // uses the single real start location (index 0).
            var apiStarts = gameInfo?.StartRaw?.StartLocations;
            var apiLoc = observation?.Observation?.RawData?.Units?.FirstOrDefault(u => u != null && u.Alliance == Alliance.Self && (u.UnitType == (uint)Sharky.UnitTypes.ZERG_HATCHERY || u.UnitType == (uint)Sharky.UnitTypes.TERRAN_COMMANDCENTER || u.UnitType == (uint)Sharky.UnitTypes.PROTOSS_NEXUS))?.Pos;
            try
            {
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
                else if (apiStarts != null && apiStarts.Count > 0)
                {
                    // No observed base unit; use API starts in order (first is slot 0)
                    foreach (var s in apiStarts) startLocations.Add(new Vector2Dto(s.X, s.Y));
                }

            }
            catch { }

            try
            {
                var dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "data", "base");
                Directory.CreateDirectory(dataFolder);
                var csvPath = Path.Combine(dataFolder, "InitialMapData.csv");
                var start1 = startLocations.Count > 1 ? startLocations[1] : null;
                var start2 = startLocations.Count > 2 ? startLocations[2] : null;
                var townHall = apiLoc;
                var csvLine = string.Join(",", new[]
                {
                    EscapeCsv(gameInfo?.MapName ?? string.Empty),
                    "Start0", apiLoc != null ? apiLoc.X.ToString("F2") : "0",
                    apiLoc != null ? apiLoc.Y.ToString("F2") : "0",
                    "Start1", start1 != null ? start1.X.ToString("F2") : "0",
                    start1 != null ? start1.Y.ToString("F2") : "0",
                    "Start2", start2 != null ? start2.X.ToString("F2") : "0",
                    start2 != null ? start2.Y.ToString("F2") : "0",
                    "TownHall", townHall != null ? townHall.X.ToString("F2") : "0",
                    townHall != null ? townHall.Y.ToString("F2") : "0"
                });
                File.AppendAllText(csvPath, csvLine + Environment.NewLine, Encoding.UTF8);
                //System.Diagnostics.Debugger.Break();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData: Failed to write CSV log: {ex.Message}");
            }

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

            // Initialize multi-location collections sized to number of start locations
            var numStartLocations = startLocations.Count > 0 ? startLocations.Count : 1;
            var multiMainMinerals = new List<List<Vector2Dto>>();
            var multiMainMineralTags = new List<List<ulong>>();
            var multiMineralResources = new List<List<uint>>();  // Parallel list to track resource amounts per start
            var multiMainVespene = new List<List<Vector2Dto>>();
            var multiStartingUnits = new List<List<WorkerEntryDto>>();
            var multiMineralCenterOfMass = new List<Vector2Dto>();
            var multiMainMineralCargoPoints = new List<List<HarvestReturnCargoPointDto>>();
            var multiMainVespeneCargoPoints = new List<List<HarvestReturnCargoPointDto>>();
            var spawningPoolPlacements = new List<Vector2Dto>();
            var macroHatcheryPlacements = new List<Vector2Dto>();
            var roachWarrenPlacements = new List<Vector2Dto>();
            var startingTownHalls = new Vector2Dto[numStartLocations];
            var baseHasBeenPlayed = new bool[numStartLocations];

            _expansionMineralCargoPoints = new List<List<HarvestReturnCargoPointDto>>();
            _expansionVespeneCargoPoints = new List<List<HarvestReturnCargoPointDto>>();
            _teamPatchAssignments = new List<List<TeamPatchAssignmentDto>>();

            // Initialize each start location's lists and set starting town halls
            for (int i = 0; i < numStartLocations; i++)
            {
                multiMainMinerals.Add(new List<Vector2Dto>());
                multiMainMineralTags.Add(new List<ulong>());
                multiMineralResources.Add(new List<uint>());  // Initialize resources list
                multiMainVespene.Add(new List<Vector2Dto>());
                multiStartingUnits.Add(new List<WorkerEntryDto>());
                _teamPatchAssignments.Add(new List<TeamPatchAssignmentDto>());
                spawningPoolPlacements.Add(null);
                macroHatcheryPlacements.Add(null);
                roachWarrenPlacements.Add(null);
                if (i < startLocations.Count)
                {
                    startingTownHalls[i] = startLocations[i];
                }
                baseHasBeenPlayed[i] = false;
            }

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

            // Prepare trackers for mineral center-of-mass computation per start location
            var comXByStart = new Dictionary<int, double>();  // Accumulate X for each start
            var comYByStart = new Dictionary<int, double>();  // Accumulate Y for each start
            var comDistanceByStart = new Dictionary<int, double>();  // Accumulate distance for each start
            var nodeCountByStart = new Dictionary<int, int>();  // Count nodes per start

            // Track near and far mineral distances per start location (for worker mining assignment)
            var nearMineralDistanceByStart = new Dictionary<int, List<float>>();  // Distances for near (close) minerals
            var farMineralDistanceByStart = new Dictionary<int, List<float>>();   // Distances for far (distant) minerals

            for (int i = 0; i < numStartLocations; i++)
            {
                comXByStart[i] = 0.0;
                comYByStart[i] = 0.0;
                comDistanceByStart[i] = 0.0;
                nodeCountByStart[i] = 0;
                nearMineralDistanceByStart[i] = new List<float>();
                farMineralDistanceByStart[i] = new List<float>();
            }

            // Temp DTO to be populated during the single-pass so BaseLocationData lists
            // (MineralPatches/MainMinerals/VespenePatches/MainVespene) are filled immediately.
            var tempBaseDto = new MawBaseLocationData();
            var nearestMineralDistance = 9f;
            var w4PositionByStart = new Dictionary<int, Vector2Dto>();

            // First-vision mineral index used to track minerals we can actually see from our start.
            var visibleMinerals = new List<MineralDto>();
            var visibleMineralTagToIndex = new Dictionary<ulong, int>();
            var visibleContentsByType = new Dictionary<uint, HashSet<int>>();

            if (observation?.Observation?.RawData?.Units != null)
            {
                foreach (var unit in observation.Observation.RawData.Units)
                {
                    try
                    {
                        if (unit?.Pos == null) continue;
                        var ut = (Sharky.UnitTypes)unit.UnitType;
                        if (!mineralTypes.Contains(ut)) continue;
                        if (unit.DisplayType != DisplayType.Visible) continue;

                        var contents = unit.HasMineralContents ? unit.MineralContents : 0;
                        var mineralIndex = visibleMinerals.Count;
                        var mineralDto = new MineralDto
                        {
                            UnitTag = unit.Tag,
                            UnitType = unit.UnitType,
                            MineralIndex = mineralIndex,
                            Position = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z),
                            MineralContents = contents,
                            MaxMineralContents = contents
                        };

                        visibleMinerals.Add(mineralDto);
                        if (unit.Tag != 0)
                        {
                            visibleMineralTagToIndex[unit.Tag] = mineralIndex;
                        }

                        if (!visibleContentsByType.TryGetValue(unit.UnitType, out var contentsSet))
                        {
                            contentsSet = new HashSet<int>();
                            visibleContentsByType[unit.UnitType] = contentsSet;
                        }
                        contentsSet.Add(contents);

                        Console.WriteLine($"InitialMapData: Visible mineral[{mineralIndex}] type={ut} tag={unit.Tag} contents={contents} pos=({unit.Pos.X:F2},{unit.Pos.Y:F2},{unit.Pos.Z:F2})");
                    }
                    catch { }
                }
            }

            tempBaseDto.Minerals = visibleMinerals;
            tempBaseDto.MineralTagToIndex = visibleMineralTagToIndex;
            foreach (var kvp in visibleContentsByType)
            {
                var contentsSet = kvp.Value;
                tempBaseDto.MineralTypeMaxContents[kvp.Key] = contentsSet.Count > 0 ? contentsSet.Max() : 0;
                tempBaseDto.MineralTypeContentsAreUniform[kvp.Key] = contentsSet.Count <= 1;
            }
            tempBaseDto.MismatchedMinerals = visibleContentsByType.Values.Any(contentsSet => contentsSet.Count > 1);
            Console.WriteLine($"InitialMapData: Visible minerals indexed={visibleMinerals.Count}, tagged={visibleMineralTagToIndex.Count}, types={visibleContentsByType.Count}");
            

            if (observation?.Observation?.RawData?.Units != null)
            {
                foreach (var unit in observation.Observation.RawData.Units)
                {
                    try
                    {
                        if (unit?.Pos == null) continue;
                        var ut = (Sharky.UnitTypes)unit.UnitType;
                        if (mineralTypes.Contains(ut))
                        {
                            var mineralPos = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z);
                            var mineralKey = $"{mineralPos.X:F2},{mineralPos.Y:F2},{mineralPos.Z:F2}";
                            mineralTypeByPosition[mineralKey] = unit.UnitType;

                             uint resourceMarker = Convert.ToUInt32(GetMineralResourceMarker(unit, ut));
                             if (tempBaseDto.MineralTypeContentsAreUniform.TryGetValue(unit.UnitType, out var contentsAreUniform))
                             {
                                 if (contentsAreUniform && tempBaseDto.MineralTypeMaxContents.TryGetValue(unit.UnitType, out var maxContentsForType))
                                 {
                                     resourceMarker = (uint)maxContentsForType;
                                 }
                                 else
                                 {
                                     resourceMarker = 0u;
                                 }
                             }


                            mineralList.Add(mineralPos);
                            // Also populate the DTO mineral patches immediately during the single-pass
                            try { tempBaseDto.MapLocationData.MineralPatches.Add(mineralPos); } catch { }

                            // Check which start location this mineral belongs to
                            const float mainDistance = 9.0f;
                            bool assignedToStart = false;

                            // Find first start location within mainDistance and process immediately
                            for (int si = 0; si < startLocations.Count; si++)
                            {
                                var startPos = startLocations[si];
                                var dist = Vector2.Distance(new Vector2(unit.Pos.X, unit.Pos.Y), new Vector2(startPos.X, startPos.Y));
                                if (dist < nearestMineralDistance) { nearestMineralDistance = dist; } // new shortest distance

                                if (dist < mainDistance)

                                {
                                    // If mineral is near a start location, add to that location's minerals
                                    assignedToStart = true;
                                    comDistanceByStart[si] += dist;
                                    multiMainMinerals[si].Add(mineralPos);
                                    multiMainMineralTags[si].Add(unit.Tag);
                                    multiMineralResources[si].Add(resourceMarker);


                                    // Accumulate COM data for this start location
                                    comXByStart[si] += unit.Pos.X;
                                    comYByStart[si] += unit.Pos.Y;
                                    nodeCountByStart[si]++;

                                    // Just collect all distances for now
                                    // Near/far classification will be done after average is calculated
                                    break;  // Exit loop immediately once we find a start location
                                }
                            }

                            // If mineral was not assigned to any start location, add to expansions
                            if (!assignedToStart)
                            {
                                // Mineral is not near any start location - potential expansion mineral
                                expansionMineralsList.Add(mineralPos);
                            }
                        }
                        else if (vespeneTypes.Contains(ut))
                        {
                            var vpos = new Vector2Dto(unit.Pos.X, unit.Pos.Y, unit.Pos.Z);
                            vespeneList.Add(vpos);
                            // Also populate DTO vespene list immediately
                            tempBaseDto.MapLocationData.VespenePatches.Add(vpos);

                            // Check which start location this vespene belongs to
                            const float mainDistance = 9.0f;
                            int assignedToStart = -1;
                            float closestStartDist = float.MaxValue;

                            // Find the closest start location within mainDistance
                            for (int si = 0; si < startLocations.Count; si++)
                            {
                                var startPos = startLocations[si];
                                var dist = Vector2.Distance(new Vector2(unit.Pos.X, unit.Pos.Y), new Vector2(startPos.X, startPos.Y));
                                if (dist < mainDistance && dist < closestStartDist)
                                {
                                    assignedToStart = si;
                                    closestStartDist = dist;
                                }
                            }

                            // If vespene is near a start location, add to that location's vespene
                            if (assignedToStart >= 0)
                            {
                                // Ensure the multiMainVespene list has enough entries
                                while (multiMainVespene.Count <= assignedToStart)
                                {
                                    multiMainVespene.Add(new List<Vector2Dto>());
                                }
                                multiMainVespene[assignedToStart].Add(vpos);
                            }
                        }
                        else if (unit.Alliance == Alliance.Self
                            && (ut == Sharky.UnitTypes.ZERG_DRONE || ut == Sharky.UnitTypes.TERRAN_SCV || ut == Sharky.UnitTypes.PROTOSS_PROBE))
                        {
                            // Collect only self workers for greedy ordering and team assignment.
                            try { workerList.Add((unit.Tag, unit.Pos.X, unit.Pos.Y, unit.Pos.Z, unit.UnitType)); } catch { }
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
                                discoveredStartUnits[bestIdx].Add(unit);
                            }
                        }
                    }
                    catch { }
                }
            }

            //Console.WriteLine($"InitialMapData: mineral categories wall={wallMineralsList.Count}, small={smallMineralsList.Count}, large={largeMineralsList.Count}");

            // Calculate center of mass for minerals at each start location
            // Also track average distance for near and far mineral patches
            // Populate multiMineralCenterOfMass with COM for each start location
            try
            {
                Console.WriteLine($"InitialMapData: nearestMineralDistance={nearestMineralDistance} ");


                for (int si = 0; si < numStartLocations; si++)
                {
                    if (nodeCountByStart[si] > 0)
                    {
                        var avgX = (float)(comXByStart[si] / nodeCountByStart[si]);
                        var avgY = (float)(comYByStart[si] / nodeCountByStart[si]);
                        var comVector = new Vector2Dto(avgX, avgY);
                        multiMineralCenterOfMass.Add(comVector);

                        var avgDistance = comDistanceByStart[si] / nodeCountByStart[si];

                        Console.WriteLine($"InitialMapData: Start[{si}] minerals COM=({avgX:F2},{avgY:F2}) nodes={nodeCountByStart[si]} avgDistance={avgDistance:F2}");

                        // Worker Chain building removed from InitialMapData.
                        // CCA (chrisCrossAppleSause) now owns W1-W12/W8 chain establishment on Frame Zero.

                        // Classify minerals into near/far lists based on avgDistance
                        // This happens after worker labeling so that worker positions are established first
                        if (multiMainMinerals.Count > si && multiMainMinerals[si] != null)
                        {
                            foreach (var mineral in multiMainMinerals[si])
                            {
                                var dist = Vector2.Distance(new Vector2(mineral.X, mineral.Y),
                                                           new Vector2(comVector.X, comVector.Y));

                                if (dist < avgDistance)
                                {
                                    nearMineralDistanceByStart[si].Add(dist);
                                }
                                else
                                {
                                    farMineralDistanceByStart[si].Add(dist);
                                }
                            }
                        }

                        Console.WriteLine($"InitialMapData: Start[{si}] classified minerals - near={nearMineralDistanceByStart[si].Count} far={farMineralDistanceByStart[si].Count}");

                        // Register COM with CrosshairService for visualization
                        if (crosshairService != null)
                        {
                            try
                            {
                                var comPosition = new Point { X = avgX, Y = avgY, Z = 12.0f };
                                var comLabel = $"Start[{si}]";

                                // Yellow for Start[0], Orange for Start[1-2] (opponent)
                                var comColor = si == 0 
                                    ? new Color { R = 255, G = 255, B = 0 }      // Yellow
                                    : new Color { R = 255, G = 165, B = 0 };     // Orange

                                crosshairService.SetCOM(comPosition, comLabel, comColor);
                                Console.WriteLine($"InitialMapData: Registered COM Start[{si}] at ({avgX:F2},{avgY:F2}) Z=12.0 color={(si == 0 ? "Yellow" : "Orange")}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"InitialMapData: Error registering COM with CrosshairService: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        // No minerals for this start location
                        multiMineralCenterOfMass.Add(null);
                        Console.WriteLine($"InitialMapData: Start[{si}] has no minerals");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData: Failed to calculate start location COMs: {ex.Message}");
            }

            // Serialize the mineral and vespene lists into Globals.currentMapMineralsFullMap (but do not write to disk yet)
            try
            {
                var bytes = MemoryPackSerializer.Serialize(mineralList);
                Globals.currentMapMineralsFullMap = Convert.ToBase64String(bytes);
                Console.WriteLine($"InitialMapData: serialized {mineralList.Count} mineral patches and {vespeneList.Count} vespene geysers with MemoryPack ({bytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                // Fallback to JSON bytes if MemoryPack serialization isn't available for the DTOs yet
                var json = JsonConvert.SerializeObject(mineralList);
                var bytes = Encoding.UTF8.GetBytes(json);
                Globals.currentMapMineralsFullMap = Convert.ToBase64String(bytes);
                Console.WriteLine($"InitialMapData: MemoryPack failed ({ex.Message}), saved JSON fallback ({bytes.Length} bytes)");
            }

            // Populate expansion town halls from minerals not assigned to any start location
             // Cluster minerals by proximity AND elevation level (Z coordinate)
             // Find townhall placement locations (nose) offset from mineral clusters (smile)
             try
             {
                 var expansionTownhalls = new List<Vector2Dto>();
                 var expansionNearDistances = new List<List<float>>();  // Near mineral distances per expansion
                 var expansionFarDistances = new List<List<float>>();   // Far mineral distances per expansion
                 var expansionClusters = new List<List<Vector2Dto>>();  // Store mineral clusters for ExpansionPointService

                 // MINERAL LABELING: Create global labels M1, M2, M3... for all expansion minerals
                 // Map from Vector2Dto (position) to label (e.g., "M1", "M42", etc.)
                 var mineralLabels = new Dictionary<Vector2Dto, string>();
                 int globalMineralIndex = 1;
                 foreach (var mineral in expansionMineralsList)
                 {
                     mineralLabels[mineral] = $"M{globalMineralIndex}";
                     globalMineralIndex++;
                 }
                 Console.WriteLine($"InitialMapData: Assigned global labels M1-M{globalMineralIndex - 1} to {expansionMineralsList.Count} expansion minerals");

                 // Calculate expansion centers by clustering expansion minerals
                 // Use iterative clustering: keep adding minerals near ANY member of cluster (not just starting mineral)
                 // CLUSTER COMPLETION: Every mineral in final cluster has been checked and can find no more neighbors
                 var usedMinerals = new HashSet<Vector2Dto>();
                 const float clusterDistance = 4.5f;
                 const float elevationTolerance = 0.5f;  // Minerals within 0.5 units Z can be in same cluster
                 const int minMineralsForExpansion = 6;  // Mineral walls < 6, expansion bases >= 6
                  int provisionalCounter = 1;  // Track provisional wall labels (P1, P2, P3...)
                 int wallCounter = 1;

                 foreach (var startMineral in expansionMineralsList)
                 {
                     if (usedMinerals.Contains(startMineral)) continue;

                     // Initialize cluster with this mineral
                     var cluster = new List<Vector2Dto> { startMineral };
                     usedMinerals.Add(startMineral);

                     // Get elevation of this cluster
                     float clusterZ = startMineral.Z;

                     // Iteratively add nearby minerals (keep looping until no new minerals added)
                     // Loop continues while ANY cluster member finds a new neighbor
                     bool addedInThisPass = true;
                     while (addedInThisPass)
                     {
                         addedInThisPass = false;
                         foreach (var candidate in expansionMineralsList)
                         {
                             if (usedMinerals.Contains(candidate)) continue;

                             // Check if candidate is at similar elevation
                             if (Math.Abs(candidate.Z - clusterZ) > elevationTolerance) continue;

                             // Check if candidate is near ANY member of the cluster
                             bool nearCluster = false;
                             foreach (var clusterMember in cluster)
                             {
                                 var d = Vector2.Distance(
                                     new Vector2(candidate.X, candidate.Y),
                                     new Vector2(clusterMember.X, clusterMember.Y));
                                 if (d < clusterDistance)
                                 {
                                     nearCluster = true;
                                     break;
                                 }
                             }

                             if (nearCluster)
                             {
                                 cluster.Add(candidate);
                                 usedMinerals.Add(candidate);
                                 addedInThisPass = true;  // Found one, will loop again to check if this new member finds more
                             }
                         }
                     }
                     // At this point: cluster is COMPLETE - every member has been checked, no new neighbors found

                     // Calculate center of this mineral cluster (the "smile")
                      var centerX = (float)cluster.Average(m => m.X);
                      var centerY = (float)cluster.Average(m => m.Y);
                      var centerZ = (float)cluster.Average(m => m.Z);  // Get true Z coordinate from minerals

                      var clusterMineralTypes = cluster
                          .Select(m =>
                          {
                              var key = $"{m.X:F2},{m.Y:F2},{m.Z:F2}";
                              return mineralTypeByPosition.TryGetValue(key, out var type) ? (Sharky.UnitTypes?)type : null;
                          })
                          .Where(type => type.HasValue)
                          .Select(type => type.Value)
                          .Distinct()
                          .ToList();

                      var isSuspectedWall = clusterMineralTypes.Count > 0 && clusterMineralTypes.All(type => suspectedMineralWall.Contains(type));

                     // MINERAL WALL FILTER: Only treat clusters >= minMineralsForExpansion as expansion bases
                     // Smaller clusters are mineral walls (obstacles/resources, not base locations)
                      if (cluster.Count >= minMineralsForExpansion && !isSuspectedWall)
                     {
                           var expansionLabel = $"E{expansionTownhalls.Count + 1}";

                          // This is an expansion base (has enough minerals to support workers)
                         var expansionCenter = new Vector2Dto(centerX, centerY, centerZ);
                         expansionTownhalls.Add(expansionCenter);
                         expansionClusters.Add(cluster);  // Store cluster minerals for ExpansionPointService

                         var expansionPointModel = new ExpansionPointModel(expansionTownhalls.Count - 1, expansionCenter)
                         {
                             MineralPositions = cluster,
                             ExpansionPoint = expansionCenter,
                             IsValid = false,
                              ValidationNotes = $"Expansion candidate {expansionLabel}",
                             Status = ExpansionPointStatus.Provisional
                         };

                         if (provisionalExpansionService != null)
                         {
                             provisionalExpansionService.AddProvisional(expansionTownhalls.Count - 1, expansionPointModel);
                         }

                           // UPDATE MINERAL LABELS: Add expansion suffix (e.g., M1 becomes M1-E1)
                         var clusterLabels = new List<string>();
                         foreach (var mineralInCluster in cluster)
                         {
                             if (mineralLabels.ContainsKey(mineralInCluster))
                             {
                                 var originalLabel = mineralLabels[mineralInCluster];
                                  var expansionMineralLabel = $"{originalLabel}-{expansionLabel}";
                                  mineralLabels[mineralInCluster] = expansionMineralLabel;
                                  clusterLabels.Add(expansionMineralLabel);
                             }
                         }
                          Console.WriteLine($"[EXPANSION-{expansionLabel}] Base Found: {cluster.Count} minerals at ({centerX:F2}, {centerY:F2}, {centerZ:F2}) | Labels: {string.Join(", ", clusterLabels.Take(3))}...");

                         // Register expansion COM with service for visualization (blue crosshairs)
                          if (expansionCOMService != null)
                         {
                             int expansionIndex = expansionTownhalls.Count - 1;  // Index of the expansion we just added
                             expansionCOMService.Set(expansionIndex, expansionCenter);
                         }

                          if (expansionPointDrawService != null)
                          {
                              var drawPoint = new Point
                              {
                                  X = expansionCenter.X,
                                  Y = expansionCenter.Y,
                                  Z = expansionCenter.Z
                              };

                              expansionPointDrawService.SetExpansionPoint(drawPoint, expansionLabel, new Color { R = 0, G = 255, B = 0 }, false);
                          }

                         // Calculate distances from expansion center to each mineral in cluster
                         var nearDistances = new List<float>();
                         var farDistances = new List<float>();
                         const float nearFarThreshold = 2.5f;  // Distance threshold for near vs far within expansion cluster

                         foreach (var clusterMineral in cluster)
                         {
                             var dist = Vector2.Distance(new Vector2(centerX, centerY), new Vector2(clusterMineral.X, clusterMineral.Y));
                             if (dist < nearFarThreshold)
                             {
                                 nearDistances.Add(dist);
                             }
                             else
                             {
                                 farDistances.Add(dist);
                             }
                         }

                         expansionNearDistances.Add(nearDistances);
                         expansionFarDistances.Add(farDistances);

                         // Calculate averages
                         float avgNearDist = nearDistances.Count > 0 ? nearDistances.Average() : 0f;
                         float avgFarDist = farDistances.Count > 0 ? farDistances.Average() : 0f;

                          Console.WriteLine($"InitialMapData: Expansion[{expansionTownhalls.Count - 1}] {expansionLabel} Smile COM=({centerX:F2},{centerY:F2},{centerZ:F2}) minerals={cluster.Count} | near={nearDistances.Count} avgNear={avgNearDist:F2} far={farDistances.Count} avgFar={avgFarDist:F2}");
                         if (expansionTownhalls.Count >= 20) break; // Limit to 20 expansions
                     }
                     else
                     {
                          var provisionalLabel = $"P{provisionalCounter}";

                          // This is a suspected mineral wall or low-content cluster.
                          foreach (var mineralInCluster in cluster)
                          {
                              if (mineralLabels.ContainsKey(mineralInCluster))
                              {
                                  var originalLabel = mineralLabels[mineralInCluster];
                                  mineralLabels[mineralInCluster] = $"{originalLabel}-{provisionalLabel}";
                              }
                          }

                          var wallLabels = cluster.Select(m => mineralLabels.ContainsKey(m) ? mineralLabels[m] : "?").ToList();
                          Console.WriteLine($"[MINERAL-WALL-{provisionalLabel}] Found: {cluster.Count} minerals at ({centerX:F2}, {centerY:F2}, {centerZ:F2}) | Labels: {string.Join(", ", wallLabels)} - NOT used as expansion base");

                          if (provisionalExpansionService != null)
                          {
                              var wallIndex = -wallCounter;
                              wallCounter++;

                              var wallCandidate = new ExpansionPointModel(wallIndex, new Vector2Dto(centerX, centerY, centerZ))
                              {
                                  MineralPositions = cluster,
                                  IsValid = false,
                                  ValidationNotes = $"Suspected mineral wall {provisionalLabel}: {cluster.Count} minerals",
                                  Status = ExpansionPointStatus.SuspectedWall
                              };

                              provisionalExpansionService.AddProvisional(wallIndex, wallCandidate, suspectedWall: true);
                          }

                          if (expansionPointDrawService != null)
                          {
                              var drawPoint = new Point
                              {
                                  X = centerX,
                                  Y = centerY,
                                  Z = centerZ
                              };

                              expansionPointDrawService.SetExpansionPoint(drawPoint, provisionalLabel, new Color { R = 255, G = 255, B = 0 }, true);
                          }

                          provisionalCounter++;  // Increment provisional label counter
                     }
                 }

                tempBaseDto.ExpansionTownhalls = expansionTownhalls;

                 // Store the mineral labels (M1-P1, M14-P1, etc.) in the DTO for later access
                foreach (var kvp in mineralLabels)
                {
                    var mineral = kvp.Key;
                    var label = kvp.Value;
                    // Use position as key: "X,Y,Z"
                    string posKey = $"{mineral.X:F2},{mineral.Y:F2},{mineral.Z:F2}";
                    tempBaseDto.ExpansionMineralLabels[posKey] = label;
                }
                Console.WriteLine($"InitialMapData: Stored {tempBaseDto.ExpansionMineralLabels.Count} expansion mineral labels | identified {expansionTownhalls.Count} expansion town hall locations from {expansionMineralsList.Count} unassigned minerals (clustering complete)");

                // Compute and register expansion townhall placements using the reusable Sharky-style base-location service.
                try
                {
                    if (expansionTownhalls.Count > 0)
                    {
                        Console.WriteLine($"InitialMapData: Computing expansion townhall placements for {expansionTownhalls.Count} expansions");

                        var baseLocationService = new BaseLocationCalculationService(gameInfo.StartRaw.PlacementGrid);

                        if (expansionPointDrawService != null)
                        {
                            expansionPointDrawService.Clear();
                        }

                        var vespenePositions = observation?.Observation?.RawData?.Units?
                            .Where(u => u != null &&
                                (u.UnitType == (uint)Sharky.UnitTypes.NEUTRAL_VESPENEGEYSER ||
                                 u.UnitType == (uint)Sharky.UnitTypes.NEUTRAL_RICHVESPENEGEYSER ||
                                 u.UnitType == (uint)Sharky.UnitTypes.NEUTRAL_SPACEPLATFORMGEYSER))
                            .Select(u => new Vector2Dto(u.Pos.X, u.Pos.Y, u.Pos.Z))
                            .ToList() ?? new List<Vector2Dto>();

                        for (int i = 0; i < expansionTownhalls.Count && i < expansionClusters.Count; i++)
                        {
                            var cluster = expansionClusters[i];
                            var center = expansionTownhalls[i];
                            var nearbyVespenes = vespenePositions
                                .Where(v => Vector2.Distance(new Vector2(v.X, v.Y), new Vector2(center.X, center.Y)) < 15f)
                                .ToList();

                            var baseLocationResult = baseLocationService.CalculateBaseLocation(cluster, nearbyVespenes);
                            var townhallLocation = baseLocationResult.IsValid && baseLocationResult.Location != null
                                ? new Vector2Dto(baseLocationResult.Location.X, baseLocationResult.Location.Y, center.Z)
                                : center;

                            expansionTownhalls[i] = townhallLocation;

                            var expansionPointModel = new ExpansionPointModel(i, center)
                            {
                                MineralPositions = cluster,
                                GeyserPositions = nearbyVespenes,
                                ExpansionPoint = townhallLocation,
                                IsValid = baseLocationResult.IsValid,
                                ValidationNotes = baseLocationResult.ValidationNotes,
                                DistanceToCluster = Vector2.Distance(new Vector2(townhallLocation.X, townhallLocation.Y), new Vector2(center.X, center.Y))
                            };

                            expansionPointModel.PlacementOptions.Add(new TownhallPlacementOption
                            {
                                Point = townhallLocation,
                                IsValid = baseLocationResult.IsValid,
                                DistanceToCluster = expansionPointModel.DistanceToCluster,
                                ValidationNotes = baseLocationResult.ValidationNotes
                            });

                            tempBaseDto.ExpansionPoints[i] = expansionPointModel;
                             _expansionMineralCargoPoints.Add(BuildHarvestReturnCargoPoints(cluster, townhallLocation));
                             _expansionVespeneCargoPoints.Add(BuildHarvestReturnCargoPoints(nearbyVespenes, townhallLocation));

                            if (expansionPointDrawService != null)
                            {
                                string label = $"E{i + 1}";
                                Color color = baseLocationResult.IsValid
                                    ? new Color { R = 0, G = 255, B = 0 }
                                    : new Color { R = 255, G = 255, B = 0 };

                                var drawPoint = new Point
                                {
                                    X = townhallLocation.X,
                                    Y = townhallLocation.Y,
                                    Z = townhallLocation.Z
                                };

                                expansionPointDrawService.SetExpansionPoint(drawPoint, label, color, false);
                                Console.WriteLine($"InitialMapData: Registered expansion draw point {label} at ({drawPoint.X:F2}, {drawPoint.Y:F2}, Z={drawPoint.Z:F2}) valid={baseLocationResult.IsValid}");
                            }

                            Console.WriteLine($"InitialMapData: Expansion[{i}] townhall placement computed at ({townhallLocation.X:F2}, {townhallLocation.Y:F2}, {townhallLocation.Z:F2}) valid={baseLocationResult.IsValid} notes={baseLocationResult.ValidationNotes}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"InitialMapData: Failed to compute expansion townhall placements: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData: Failed to populate expansion townhalls: {ex.Message}");
            }

            // Calculate greedy mineral ordering for each start location.
            // InitialMapData runs only for a new map the first time it is played.
            // The temporary chain is built from the furthest mineral back to the nearest mineral.
            // The furthest mineral becomes the new anchor for the remaining minerals.
            var orderedMainMinerals = new List<List<OrderedMineral>>();
            try
            {

                for (int si = 0; si < numStartLocations; si++)
                {
                    var orderedList = new List<OrderedMineral>();
                    var townhallPosition = startingTownHalls[si];

                    // Get W1 position (furthest worker from COM) for this start
                    Vector2Dto w1Position = null;
                    if (multiStartingUnits.Count > si && multiStartingUnits[si].Count > 0)
                    {
                        w1Position = multiStartingUnits[si][0].Position;
                    }

                    // Get COM and minerals for this start
                    Vector2Dto com = null;
                    if (multiMineralCenterOfMass.Count > si)
                    {
                        com = multiMineralCenterOfMass[si];
                    }
                    List<Vector2Dto> minerals = null;
                    if (multiMainMinerals.Count > si)
                    {
                        minerals = multiMainMinerals[si];
                    }

                    if (w1Position != null && minerals != null && minerals.Count > 0)
                    {
                        // Get resource amounts for this start's minerals
                        List<uint> resources = null;
                        if (multiMineralResources.Count > si)
                        {
                            resources = multiMineralResources[si];
                        }

                        if (resources != null && resources.Count > 0)
                        {
                            var distinctResourceCount = resources.Distinct().Count();
                            Console.WriteLine($"InitialMapData: Start[{si}] resource markers detected: {distinctResourceCount} distinct value(s)");

                            if (distinctResourceCount <= 1 || resources.All(r => r == 0u))
                            {
                                resources = BuildMineralResourceMarkersByDistance(minerals, townhallPosition);
                                Console.WriteLine($"InitialMapData: Start[{si}] rebuilt resource markers from distance ordering ({resources.Count} entries)");
                            }
                        }
                        else
                        {
                            resources = BuildMineralResourceMarkersByDistance(minerals, townhallPosition);
                            Console.WriteLine($"InitialMapData: Start[{si}] created distance-based resource markers ({resources.Count} entries)");
                        }

                        // Perform greedy ordering using W12 as the anchor for the initial closest-mineral pick.
                        // If W12 is unavailable, fall back to the last observed worker for that start.
                        // SecondaryMapData should mirror this same greedy-chain logic when a new start location
                        // is encountered for the first time on an already known map.
                        var anchorPosition = w1Position;
                        if (multiStartingUnits.Count > si && multiStartingUnits[si].Count > 0)
                        {
                            var workersForStart = multiStartingUnits[si];
                            var workerCount = workersForStart.Count;

                            // Use dynamic anchor logic based on worker count: W3 for 8-workers, W4 for 12-workers
                            var anchorLabel = workerCount == 8 ? "W3" : "W4";
                            var anchorWorker = workersForStart.FirstOrDefault(w => w.Label == anchorLabel);
                            if (anchorWorker != null)
                            {
                                anchorPosition = anchorWorker.Position;
                                Console.WriteLine($"InitialMapData: Start[{si}] using {anchorLabel} as greedy anchor at ({anchorPosition.X:F2},{anchorPosition.Y:F2})");
                            }
                            else
                            {
                                anchorPosition = workersForStart[0].Position;
                                Console.WriteLine($"InitialMapData: Start[{si}] {anchorLabel} not found; fallback to {workersForStart[0].Label} as greedy anchor");
                            }
                        }

                        orderedList = GreedyOrderMinerals(minerals, anchorPosition, com, townhallPosition, si, resources, multiMainMineralTags[si]);
                        Console.WriteLine($"InitialMapData: Start[{si}] ordered {orderedList.Count} minerals");
                    }

                    orderedMainMinerals.Add(orderedList);
                }

                tempBaseDto.OrderedMainMinerals = orderedMainMinerals;
                Console.WriteLine($"InitialMapData: Calculated greedy mineral ordering for all start locations");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData: Failed to calculate greedy mineral ordering: {ex.Message}");
            }

            try
            {
                tempBaseDto.MainMineralJitCargoPoints = BuildMultiLocationJitCargoPoints(orderedMainMinerals, tempBaseDto.MainMineralCargoPoints);
                Console.WriteLine($"InitialMapData: Calculated JIT mineral cargo geometry for all start locations");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData: Failed to calculate JIT mineral cargo geometry: {ex.Message}");
            }

            // Calculate vespene ordering for each start location.
            // InitialMapData only runs for a new map the first time it is played.
            // SecondaryMapData should use the same learned-worker ordering for first-time unplayed starts.
            // Use W4 as the anchor and label only the closest geyser as V1 and the farthest as V2.
            var orderedMainVespenes = new List<List<OrderedVespene>>();
            try
            {
                Console.WriteLine($"InitialMapData: Starting vespene ordering for {numStartLocations} start location(s)");
                for (int si = 0; si < numStartLocations; si++)
                {
                    var orderedList = new List<OrderedVespene>();
                    var townhallPosition = startingTownHalls[si];

                    // Use the anchor position saved when workers were labeled above.
                    Vector2Dto w4Position = null;
                    var anchorLabel = workerList.Count == 8 ? "W3" : "W4";
                    if (w4PositionByStart.TryGetValue(si, out var savedW4Position))
                    {
                        w4Position = savedW4Position;
                        Console.WriteLine($"InitialMapData: Start[{si}] saved anchor {anchorLabel} position found at ({w4Position.X:F2},{w4Position.Y:F2})");
                    }
                    else if (multiStartingUnits.Count > si && multiStartingUnits[si].Any(w => w.Label == anchorLabel))
                    {
                        w4Position = multiStartingUnits[si].First(w => w.Label == anchorLabel).Position;
                        Console.WriteLine($"InitialMapData: Start[{si}] using {anchorLabel} as vespene anchor");
                    }
                    else if (multiStartingUnits.Count > si && multiStartingUnits[si].Count > 0)
                    {
                        // Fallback to furthest worker if anchor not found
                        w4Position = multiStartingUnits[si][0].Position;
                        Console.WriteLine($"InitialMapData: Start[{si}] fallback using W{workerList.Count} as vespene anchor");
                    }

                    // Get vespenes for this start
                    List<Vector2Dto> vespenes = null;
                    if (multiMainVespene.Count > si)
                    {
                        vespenes = multiMainVespene[si];
                    }

                    int vespeneCount = vespenes?.Count ?? 0;
                    Console.WriteLine($"InitialMapData: Start[{si}] - W4Position={w4Position != null}, VespeneCount={vespeneCount}");

                    if (w4Position != null && vespenes != null && vespenes.Count > 0)
                    {
                        // Pick the closest geyser as V1 and the farthest geyser as V2.
                        var vespeneDistances = vespenes
                            .Select((vespene, idx) => new 
                            { 
                                Vespene = vespene, 
                                Index = idx, 
                                Distance = Vector2.Distance(
                                    new Vector2(vespene.X, vespene.Y),
                                    new Vector2(w4Position.X, w4Position.Y)
                                )
                            })
                            .OrderBy(v => v.Distance)
                            .ToList();

                        var closest = vespeneDistances.FirstOrDefault();
                        var farthest = vespeneDistances.LastOrDefault();

                        if (closest != null)
                        {
                            var linePoints = townhallPosition != null ? BuildMineralLinePoints(closest.Vespene, townhallPosition) : (null, null, null, null);
                            orderedList.Add(new OrderedVespene
                            {
                                Position = closest.Vespene,
                                HarvestPoint = linePoints.Item1,
                                ReturnPoint = linePoints.Item3,
                                Index = 1,
                                DistanceToW4 = closest.Distance,
                                Label = "V1"
                            });
                            Console.WriteLine($"InitialMapData: Start[{si}] V1 = vespene at distance {closest.Distance:F2} from W4");
                        }

                        if (farthest != null && farthest.Index != closest?.Index)
                        {
                            var linePoints = townhallPosition != null ? BuildMineralLinePoints(farthest.Vespene, townhallPosition) : (null, null, null, null);
                            orderedList.Add(new OrderedVespene
                            {
                                Position = farthest.Vespene,
                                HarvestPoint = linePoints.Item1,
                                ReturnPoint = linePoints.Item3,
                                Index = 2,
                                DistanceToW4 = farthest.Distance,
                                Label = "V2"
                            });
                            Console.WriteLine($"InitialMapData: Start[{si}] V2 = vespene at distance {farthest.Distance:F2} from W4");
                        }
                    }
                    else if (w4Position == null && multiStartingUnits.Count > si && multiStartingUnits[si].Count > 0)
                    {
                        Console.WriteLine($"InitialMapData: Start[{si}] has fewer than 4 workers - cannot assign W4 for vespene ordering");
                    }

                    orderedMainVespenes.Add(orderedList);
                    Console.WriteLine($"InitialMapData: Start[{si}] vespene ordering complete: {orderedList.Count} vespenes ordered");
                }

                tempBaseDto.OrderedMainVespene = orderedMainVespenes;
                Console.WriteLine($"InitialMapData: Calculated vespene ordering for all start locations");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData: Failed to calculate vespene ordering: {ex.Message}");
            }

                // Populate multi-location data into tempBaseDto before returning
            try
            {
                tempBaseDto.StartingTownHall = startingTownHalls;
                tempBaseDto.MainMinerals = multiMainMinerals;
                tempBaseDto.MainVespene = multiMainVespene;
                tempBaseDto.MineralCenterOfMass = multiMineralCenterOfMass;
                tempBaseDto.StartingUnits = multiStartingUnits;

                // Build initial team assignments so they are available for CCA/JIT mining immediately
                TeamLabelRegistrationHelper.RegisterTeamLabels(tempBaseDto, orderedMainMinerals, multiStartingUnits, multiMineralCenterOfMass, workerLabelService, _teamPatchAssignments);

                tempBaseDto.TeamPatchAssignments = _teamPatchAssignments;
                tempBaseDto.SpawningPoolPlacements = spawningPoolPlacements;
                tempBaseDto.MacroHatcheryPlacements = macroHatcheryPlacements;
                tempBaseDto.RoachWarrenPlacements = roachWarrenPlacements;

                var workerCount = workerList?.Count ?? 12;
                Settings.WorkerCount = workerCount;

                tempBaseDto.BaseHasBeenPlayed = baseHasBeenPlayed;
                tempBaseDto.BaseHasBeenPlayed8 = new bool[numStartLocations];
                tempBaseDto.BaseHasBeenPlayed12 = new bool[numStartLocations];

                if (workerCount == 8)
                {
                    Array.Copy(baseHasBeenPlayed, tempBaseDto.BaseHasBeenPlayed8, baseHasBeenPlayed.Length);
                }
                else if (workerCount == 12)
                {
                    Array.Copy(baseHasBeenPlayed, tempBaseDto.BaseHasBeenPlayed12, baseHasBeenPlayed.Length);
                }

                tempBaseDto.AssignmentsByWorkerCount[workerCount] = _teamPatchAssignments;

                tempBaseDto.M1IsFar = new bool[numStartLocations];
                tempBaseDto.M8IsFar = new bool[numStartLocations];
                for (int i = 0; i < numStartLocations; i++)
                {
                    if (orderedMainMinerals.Count > i && orderedMainMinerals[i] != null)
                    {
                        var orderedList = orderedMainMinerals[i];
                        tempBaseDto.M1IsFar[i] = orderedList.Any(m => m != null && m.Index == 1 && m.IsFar);
                        tempBaseDto.M8IsFar[i] = orderedList.Any(m => m != null && m.Index == 8 && m.IsFar);
                    }
                }
                Settings.M1IsFar = tempBaseDto.M1IsFar;
                Settings.M8IsFar = tempBaseDto.M8IsFar;
                tempBaseDto.MainMineralCargoPoints = BuildMultiLocationCargoPoints(multiMainMinerals, startingTownHalls);
                tempBaseDto.MainVespeneCargoPoints = BuildMultiLocationCargoPoints(multiMainVespene, startingTownHalls);
                tempBaseDto.MainMineralJitCargoPoints = BuildMultiLocationJitCargoPoints(tempBaseDto.OrderedMainMinerals, tempBaseDto.MainMineralCargoPoints);
                tempBaseDto.ExpansionMineralCargoPoints = _expansionMineralCargoPoints;
                tempBaseDto.ExpansionVespeneCargoPoints = _expansionVespeneCargoPoints;
                tempBaseDto.ExpansionMineralCenterOfMass = (tempBaseDto.ExpansionTownhalls ?? new List<Vector2Dto>()).Select(t => t == null ? null : new Vector2Dto(t.X, t.Y, t.Z)).Where(v => v != null).ToList();
                tempBaseDto.ExpansionMineralJitCargoPoints = BuildMultiLocationJitCargoPoints(tempBaseDto.OrderedMainMinerals, tempBaseDto.MainMineralCargoPoints, tempBaseDto.ExpansionMineralCargoPoints);

                Settings.CurrentSpawnIndex = startLocationIndex;
                Settings.CurrentSpawnLocation = startLocationIndex < startingTownHalls.Length && startingTownHalls[startLocationIndex] != null
                    ? startingTownHalls[startLocationIndex]
                    : new Vector2Dto();
                Settings.CurrentSpawnCOM = startLocationIndex < multiMineralCenterOfMass.Count && multiMineralCenterOfMass[startLocationIndex] != null
                    ? multiMineralCenterOfMass[startLocationIndex]
                    : new Vector2Dto();
                Settings.CurrentBaseHasBeenPlayed = false;

                Console.WriteLine($"InitialMapData: Populated multi-location data - {numStartLocations} start locations, {tempBaseDto.ExpansionTownhalls.Count} expansions");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData: Failed to populate multi-location data: {ex.Message}");
            }

            return tempBaseDto;
        }

        private List<List<HarvestReturnCargoPointDto>> BuildMultiLocationCargoPoints(List<List<Vector2Dto>> resourceGroups, Vector2Dto[] townhalls)
        {
            var cargoPoints = new List<List<HarvestReturnCargoPointDto>>();

            if (resourceGroups == null)
            {
                return cargoPoints;
            }

            for (int i = 0; i < resourceGroups.Count; i++)
            {
                var townhall = townhalls != null && i < townhalls.Length ? townhalls[i] : null;
                cargoPoints.Add(BuildHarvestReturnCargoPoints(resourceGroups[i], townhall));
            }

            return cargoPoints;
        }

        private List<List<MiningPairCargoPointDto>> BuildMultiLocationJitCargoPoints(List<List<OrderedMineral>> orderedMinerals, List<List<HarvestReturnCargoPointDto>> mainCargoPoints, List<List<HarvestReturnCargoPointDto>> expansionCargoPoints = null)
        {
            var jitPoints = new List<List<MiningPairCargoPointDto>>();

            if (orderedMinerals == null)
            {
                return jitPoints;
            }

            for (int i = 0; i < orderedMinerals.Count; i++)
            {
                var minerals = orderedMinerals[i] ?? new List<OrderedMineral>();
                var cargoPoints = mainCargoPoints != null && i < mainCargoPoints.Count ? mainCargoPoints[i] : new List<HarvestReturnCargoPointDto>();
                jitPoints.Add(BuildJitPairCargoPoints(minerals, cargoPoints));
            }

            if (expansionCargoPoints != null && expansionCargoPoints.Count > 0)
            {
                for (int i = 0; i < expansionCargoPoints.Count; i++)
                {
                    var cargoPoints = expansionCargoPoints[i] ?? new List<HarvestReturnCargoPointDto>();
                    if (jitPoints.Count <= i)
                    {
                        jitPoints.Add(BuildJitPairCargoPoints(new List<OrderedMineral>(), cargoPoints));
                    }
                    else if (jitPoints[i].Count == 0)
                    {
                        jitPoints[i] = BuildJitPairCargoPoints(new List<OrderedMineral>(), cargoPoints);
                    }
                }
            }

            return jitPoints;
        }

        private List<MiningPairCargoPointDto> BuildJitPairCargoPoints(List<OrderedMineral> orderedMinerals, List<HarvestReturnCargoPointDto> cargoPoints)
        {
            var jitPairs = new List<MiningPairCargoPointDto>();

            if (orderedMinerals == null || orderedMinerals.Count == 0 || cargoPoints == null || cargoPoints.Count == 0)
            {
                return jitPairs;
            }

            var orderedByIndex = orderedMinerals
                .Where(m => m != null && m.Position != null)
                .OrderBy(m => m.Index)
                .ToList();

            for (int i = 0; i + 1 < orderedByIndex.Count; i += 2)
            {
                var first = orderedByIndex[i];
                var second = orderedByIndex[i + 1];
                var firstCargo = cargoPoints.FirstOrDefault(c => IsSamePosition(c?.ResourcePosition, first.Position));
                var secondCargo = cargoPoints.FirstOrDefault(c => IsSamePosition(c?.ResourcePosition, second.Position));

                var firstReturn = firstCargo?.ReturnPoint ?? first.ReturnPoint ?? new Vector2Dto(first.Position.X, first.Position.Y, first.Position.Z);
                var secondReturn = secondCargo?.ReturnPoint ?? second.ReturnPoint ?? new Vector2Dto(second.Position.X, second.Position.Y, second.Position.Z);
                var jitReturn = new Vector2Dto((firstReturn.X + secondReturn.X) * 0.5f, (firstReturn.Y + secondReturn.Y) * 0.5f, (firstReturn.Z + secondReturn.Z) * 0.5f);

                jitPairs.Add(new MiningPairCargoPointDto
                {
                    PairIndex = i / 2 + 1,
                    Label = $"M{first.Index}/M{second.Index}",
                    FirstMineralPosition = first.Position,
                    SecondMineralPosition = second.Position,
                    JitReturnPoint = jitReturn,
                    FirstHarvestPoint = firstCargo?.HarvestPoint ?? first.HarvestPoint ?? new Vector2Dto(first.Position.X, first.Position.Y, first.Position.Z),
                    SecondHarvestPoint = secondCargo?.HarvestPoint ?? second.HarvestPoint ?? new Vector2Dto(second.Position.X, second.Position.Y, second.Position.Z),
                    FirstReturnPoint = firstReturn,
                    SecondReturnPoint = secondReturn
                });
            }

            return jitPairs;
        }

        private bool IsSamePosition(Vector2Dto a, Vector2Dto b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            return Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) < 0.01f;
        }

        private List<HarvestReturnCargoPointDto> BuildHarvestReturnCargoPoints(IEnumerable<Vector2Dto> resources, Vector2Dto townhallPosition)
        {
            var cargoPoints = new List<HarvestReturnCargoPointDto>();

            if (resources == null || townhallPosition == null)
            {
                return cargoPoints;
            }

            foreach (var resource in resources)
            {
                if (resource == null)
                {
                    continue;
                }

                cargoPoints.Add(BuildHarvestReturnCargoPoint(resource, townhallPosition));
            }

            return cargoPoints;
        }

        private static string EscapeCsv(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        }

        private (Vector2Dto HarvestPoint, Vector2Dto SmHarvestPoint, Vector2Dto ReturnPoint, Vector2Dto SmReturnPoint) BuildMineralLinePoints(Vector2Dto resourcePosition, Vector2Dto townhallPosition)
        {
            var baseVector = new Vector2(townhallPosition.X, townhallPosition.Y);
            var resourceVector = new Vector2(resourcePosition.X, resourcePosition.Y);
            var direction = resourceVector - baseVector;
            var distance = direction.Length();

            if (distance <= 0.0001f)
            {
                return (
                    new Vector2Dto(resourcePosition.X, resourcePosition.Y, resourcePosition.Z),
                    new Vector2Dto(resourcePosition.X, resourcePosition.Y, resourcePosition.Z),
                    new Vector2Dto(townhallPosition.X, townhallPosition.Y, townhallPosition.Z),
                    new Vector2Dto(townhallPosition.X, townhallPosition.Y, townhallPosition.Z));
            }

            var unitDirection = Vector2.Normalize(direction);
            const float hatcheryRadius = 2.75f;
            const float mineralRadius = 1.0f;
            const float smallInset = 1.75f;

            var returnPoint = GetPointOnLine(baseVector, unitDirection, hatcheryRadius, townhallPosition.Z);
            var harvestPoint = GetPointOnLine(resourceVector, -unitDirection, mineralRadius, resourcePosition.Z);

            return (
                harvestPoint,
                GetPointOnLine(resourceVector, -unitDirection, mineralRadius + smallInset, resourcePosition.Z),
                returnPoint,
                GetPointOnLine(baseVector, unitDirection, hatcheryRadius - smallInset, townhallPosition.Z));
        }

        private Vector2Dto CalculateSpawningPoolPlacement(Vector2Dto hatcheryStart, Vector2Dto mineralCom, Vector2Dto vespeneV2)
        {
            var hatchery = new Vector2(hatcheryStart.X, hatcheryStart.Y);
            var mineralCenter = new Vector2(mineralCom.X, mineralCom.Y);
            var geyserCenter = new Vector2(vespeneV2.X, vespeneV2.Y);

            var hatcheryToGeyser = Vector2.Distance(hatchery, geyserCenter);
            var hatcheryRadius = Math.Max(0f, hatcheryToGeyser - 1.5f);
            const float geyserRadius = 3f;

            var candidates = IntersectCircles(hatchery, hatcheryRadius, geyserCenter, geyserRadius);
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

            return new Vector2Dto(chosen.X, chosen.Y, hatcheryStart.Z);
        }

        private (Vector2Dto MacroHatchery, Vector2Dto RoachWarren) CalculateMacroHatcheryAndRoachWarrenPlacements(Vector2Dto hatcheryStart, Vector2Dto mineralCom, List<Vector2Dto> vespenes)
        {
            if (hatcheryStart == null || mineralCom == null || vespenes == null || vespenes.Count == 0)
            {
                return (null, null);
            }

            var hatchery = new Vector2(hatcheryStart.X, hatcheryStart.Y);
            var mineralCenter = new Vector2(mineralCom.X, mineralCom.Y);
            var rampDirection = Vector2.Normalize(mineralCenter - hatchery);
            if (float.IsNaN(rampDirection.X) || float.IsNaN(rampDirection.Y))
            {
                rampDirection = new Vector2(0, 1);
            }

            var vespene = vespenes.Count > 1 ? vespenes[1] : vespenes[0];
            var geyserCenter = new Vector2(vespene.X, vespene.Y);
            var desiredMacro = hatchery + rampDirection * 6f;
            var desiredRoach = hatchery + rampDirection * 10f;

            var macroCandidates = GetBuildCandidatesNearRamp(desiredMacro, geyserCenter, mineralCenter, 5f);
            var macro = SelectBestBuildCandidate(macroCandidates, mineralCenter, geyserCenter, 5f, 3f);

            var roachCandidates = GetBuildCandidatesNearRamp(desiredRoach, geyserCenter, mineralCenter, 3f);
            var roach = SelectBestBuildCandidate(roachCandidates, mineralCenter, geyserCenter, 3f, 1f);

            if (macro == null && roach != null)
            {
                macro = roach;
                roach = null;
            }

            if (macro != null && roach != null)
            {
                var macroVec = new Vector2(macro.X, macro.Y);
                var roachVec = new Vector2(roach.X, roach.Y);
                var gap = Vector2.Distance(macroVec, roachVec) - 4f;
                if (gap < 1f)
                {
                    roach = new Vector2Dto(roach.X + rampDirection.X * 2f, roach.Y + rampDirection.Y * 2f, hatcheryStart.Z);
                }
            }

            return (macro, roach);
        }

        private List<Vector2Dto> GetBuildCandidatesNearRamp(Vector2 desired, Vector2 geyser, Vector2 mineralCenter, float footprintRadius)
        {
            var candidates = new List<Vector2Dto>();
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dy = -3; dy <= 3; dy++)
                {
                    var candidate = new Vector2Dto(desired.X + dx * 0.5f, desired.Y + dy * 0.5f);
                    var candidateVec = new Vector2(candidate.X, candidate.Y);
                    if (Vector2.Distance(candidateVec, geyser) < 3.1f && Vector2.Distance(candidateVec, mineralCenter) > 2.5f)
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            return candidates;
        }

        private Vector2Dto SelectBestBuildCandidate(List<Vector2Dto> candidates, Vector2 mineralCenter, Vector2 geyserCenter, float minGeyserDistance, float minWallGap)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            return candidates
                .OrderByDescending(c => Vector2.DistanceSquared(new Vector2(c.X, c.Y), mineralCenter))
                .ThenBy(c => Vector2.DistanceSquared(new Vector2(c.X, c.Y), geyserCenter))
                .FirstOrDefault();
        }

        private List<Vector2> IntersectCircles(Vector2 c0, float r0, Vector2 c1, float r1)
        {
            var results = new List<Vector2>();
            var d = Vector2.Distance(c0, c1);

            if (d <= 0.0001f || d > r0 + r1 || d < Math.Abs(r0 - r1))
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

        private HarvestReturnCargoPointDto BuildHarvestReturnCargoPoint(Vector2Dto resourcePosition, Vector2Dto townhallPosition)
        {
            var baseVector = new Vector2(townhallPosition.X, townhallPosition.Y);
            var resourceVector = new Vector2(resourcePosition.X, resourcePosition.Y);

            var direction = resourceVector - baseVector;
            var distance = direction.Length();
            if (distance <= 0.0001f)
            {
                return new HarvestReturnCargoPointDto
                {
                    ResourcePosition = new Vector2Dto(resourcePosition.X, resourcePosition.Y, resourcePosition.Z),
                    HarvestPoint = new Vector2Dto(resourcePosition.X, resourcePosition.Y, resourcePosition.Z),
                    SmHarvestPoint = new Vector2Dto(resourcePosition.X, resourcePosition.Y, resourcePosition.Z),
                    ReturnPoint = new Vector2Dto(townhallPosition.X, townhallPosition.Y, townhallPosition.Z),
                    SmReturnPoint = new Vector2Dto(townhallPosition.X, townhallPosition.Y, townhallPosition.Z)
                };
            }

            var unitDirection = Vector2.Normalize(direction);
            const float hatcheryRadius = 5.5f;
            const float mineralRadius = 1.0f;
            const float smallInset = 1.75f;

            var returnPoint = GetPointOnLine(baseVector, unitDirection, hatcheryRadius, townhallPosition.Z);
            var harvestPoint = GetPointOnLine(resourceVector, -unitDirection, mineralRadius, resourcePosition.Z);

            var smReturnPoint = GetPointOnLine(baseVector, unitDirection, hatcheryRadius - smallInset, townhallPosition.Z);
            var smHarvestPoint = GetPointOnLine(resourceVector, -unitDirection, mineralRadius + smallInset, resourcePosition.Z);

            return new HarvestReturnCargoPointDto
            {
                ResourcePosition = new Vector2Dto(resourcePosition.X, resourcePosition.Y, resourcePosition.Z),
                HarvestPoint = harvestPoint,
                SmHarvestPoint = smHarvestPoint,
                ReturnPoint = returnPoint,
                SmReturnPoint = smReturnPoint
            };
        }

        private Vector2Dto GetPointOnLine(Vector2 start, Vector2 unitDirection, float distanceFromStart, float z)
        {
            return new Vector2Dto(
                start.X + unitDirection.X * distanceFromStart,
                start.Y + unitDirection.Y * distanceFromStart,
                z);
        }

        /// <summary>
        /// Find the closest worker-mineral pair among two minerals.
        /// Returns the mineral and worker pair with smallest distance.
        /// </summary>
        private (OrderedMineral Mineral, WorkerEntryDto Worker) FindClosestWorkerMineralPair(List<WorkerEntryDto> workers, OrderedMineral m1, OrderedMineral m2)
        {
            if (workers == null || workers.Count == 0 || m1 == null || m2 == null)
                return (null, null);

            OrderedMineral closestMineral = null;
            WorkerEntryDto closestWorker = null;
            float closestDistance = float.MaxValue;

            // Calculate all worker-to-mineral distances
            foreach (var worker in workers)
            {
                var workerPos = new Vector2(worker.Position.X, worker.Position.Y);

                // Distance to m1
                var distToM1 = Vector2.Distance(workerPos, new Vector2(m1.Position.X, m1.Position.Y));
                if (distToM1 < closestDistance)
                {
                    closestDistance = distToM1;
                    closestMineral = m1;
                    closestWorker = worker;
                }

                // Distance to m2
                var distToM2 = Vector2.Distance(workerPos, new Vector2(m2.Position.X, m2.Position.Y));
                if (distToM2 < closestDistance)
                {
                    closestDistance = distToM2;
                    closestMineral = m2;
                    closestWorker = worker;
                }
            }

            return (closestMineral, closestWorker);
        }

        /// <summary>
        /// Register mineral labels with the MineralLabelService.
        /// </summary>
        private void RegisterMineralLabels(List<List<OrderedMineral>> orderedMainMinerals, MineralLabelService mineralLabelService, MawBaseLocationData mapData)
        {
            if (orderedMainMinerals == null || mineralLabelService == null || mapData == null)
            {
                Console.WriteLine("InitialMapData.RegisterMineralLabels: Invalid input");
                return;
            }

            try
            {
                int farCount = 0;
                int nearCount = 0;
                int largeCount = 0;

                for (int startIdx = 0; startIdx < orderedMainMinerals.Count; startIdx++)
                {
                    var orderedList = orderedMainMinerals[startIdx];
                    farCount = 0;
                    nearCount = 0;
                    largeCount = 0;

                    foreach (var orderedMineral in orderedList)
                    {
                        if (orderedMineral == null || orderedMineral.Position == null)
                            continue;

                        var displayIndex = orderedMineral.Index;
                        var label = !string.IsNullOrWhiteSpace(orderedMineral.FinalLabel)
                            ? orderedMineral.FinalLabel
                            : !string.IsNullOrWhiteSpace(orderedMineral.Label)
                                ? orderedMineral.Label
                                : $"M{displayIndex}";
                        var labelColor = ProcessVisableUnits.GetFinalLabelColor(label);

                        // Convert Vector2Dto to Point for registration (Z includes terrain height + 0.5f offset)
                        var position = new Point
                        {
                            X = orderedMineral.Position.X,
                            Y = orderedMineral.Position.Y,
                            Z = orderedMineral.Position.Z + 0.5f  // 0.5 units above the mineral
                        };

                        mineralLabelService.SetMineralLabel(label, position, labelColor, orderedMineral.UnitTag);
                        
                        mapData.MineralFinalLabelsByPosition ??= new Dictionary<string, string>();
                        mapData.MineralFinalLabelsByPosition[$"{orderedMineral.Position.X:F2},{orderedMineral.Position.Y:F2}"] = label;

                        Console.WriteLine($"InitialMapData.RegisterMineralLabels: Start[{startIdx}] M{displayIndex} = {label} at ({orderedMineral.Position.X:F2},{orderedMineral.Position.Y:F2})");
                    }

                    var finalLabels = orderedList.Count(m => !string.IsNullOrWhiteSpace(m.FinalLabel));
                    Console.WriteLine($"InitialMapData.RegisterMineralLabels: Start[{startIdx}] Summary: {finalLabels} final labels registered");
                }

                Console.WriteLine($"InitialMapData.RegisterMineralLabels: Registered mineral labels for all start locations");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData.RegisterMineralLabels: Error registering mineral labels: {ex.Message}");
            }
        }

        private int AssignPhaseOneWorkers(List<List<OrderedMineral>> orderedMainMinerals, List<List<WorkerEntryDto>> multiStartingUnits, WorkerLabelService workerLabelService, HashSet<string> assignedPrefixes)
        {
            if (orderedMainMinerals == null || multiStartingUnits == null || workerLabelService == null)
            {
                return 0;
            }

            var totalAssignments = 0;

            for (int si = 0; si < orderedMainMinerals.Count; si++)
            {
                var minerals = orderedMainMinerals[si];
                var workers = multiStartingUnits.Count > si ? multiStartingUnits[si] : null;

                if (minerals == null || workers == null || workers.Count == 0)
                {
                    continue;
                }

                var nearMinerals = minerals
                    .Where(m => m != null && m.IsNear && m.Position != null)
                    .OrderBy(m => m.Index)
                    .ToList();

                if (nearMinerals.Count == 0)
                {
                    continue;
                }

                var remainingWorkers = workers.ToList();

                foreach (var mineral in nearMinerals)
                {
                    if (remainingWorkers.Count == 0)
                    {
                        break;
                    }

                    var target = new Vector2(mineral.Position.X, mineral.Position.Y);
                    var closestWorker = remainingWorkers
                        .OrderBy(w => Vector2.Distance(new Vector2(w.Position.X, w.Position.Y), target))
                        .FirstOrDefault();

                    if (closestWorker == null)
                    {
                        continue;
                    }

                    var prefix = GetPhaseOneLabelPrefix(mineral);
                    if (string.IsNullOrEmpty(prefix))
                    {
                        continue;
                    }

                    if (assignedPrefixes.Contains(prefix))
                    {
                        continue;
                    }

                    assignedPrefixes.Add(prefix);
                    var newLabel = $"{prefix}1";
                    var oldLabel = closestWorker.Label;

                    closestWorker.Label = newLabel;
                    if (closestWorker.UnitTag != 0)
                    {
                       // workerLabelService.SetLabel(newLabel, closestWorker.UnitTag);
                    }

                    Console.WriteLine($"Worker Initial Mining Assignment: {oldLabel} has been changed to {newLabel}");
                    remainingWorkers.Remove(closestWorker);
                    totalAssignments++;
                }
            }

            return totalAssignments;
        }

        private void AssignPhaseTwoLargeWorkers(List<List<OrderedMineral>> orderedMainMinerals, List<List<WorkerEntryDto>> multiStartingUnits, WorkerLabelService workerLabelService, HashSet<string> assignedPrefixes, HashSet<int> reservedMineralIndices, int remainingAssignments)
        {
            if (orderedMainMinerals == null || multiStartingUnits == null || workerLabelService == null || remainingAssignments <= 0)
            {
                return;
            }

            for (int si = 0; si < orderedMainMinerals.Count; si++)
            {
                var minerals = orderedMainMinerals[si];
                var workers = multiStartingUnits.Count > si ? multiStartingUnits[si] : null;

                if (minerals == null || workers == null || workers.Count == 0)
                {
                    continue;
                }

                var largeMinerals = minerals
                    .Where(m => m != null && m.Size == MineralSize.Large && !m.IsNear && m.Position != null)
                    .OrderBy(m => m.Index)
                    .ToList();

                if (largeMinerals.Count == 0)
                {
                    continue;
                }

                var remainingWorkers = workers
                    .Where(w => string.IsNullOrEmpty(w.Label) || w.Label.StartsWith("W"))
                    .ToList();

                foreach (var mineral in largeMinerals)
                {
                    if (remainingAssignments <= 0)
                    {
                        break;
                    }

                    if (remainingWorkers.Count == 0)
                    {
                        break;
                    }

                    // Phase 2 should use the actual mineral X/Y because the worker goes directly to the patch first.
                    // Speed mining style points are only used after the worker is already in motion or harvesting.
                    var targetPoint = mineral.Position;

                    if (targetPoint == null)
                    {
                        continue;
                    }

                    var target = new Vector2(targetPoint.X, targetPoint.Y);
                    var closestWorker = remainingWorkers
                        .OrderBy(w => Vector2.Distance(new Vector2(w.Position.X, w.Position.Y), target))
                        .FirstOrDefault();

                    if (closestWorker == null)
                    {
                        continue;
                    }

                    var prefix = GetPhaseOneLabelPrefix(mineral);
                    if (string.IsNullOrEmpty(prefix) || assignedPrefixes.Contains(prefix))
                    {
                        continue;
                    }

                    var newLabel = $"{prefix}1";
                    var oldLabel = closestWorker.Label;

                    closestWorker.Label = newLabel;
                    if (closestWorker.UnitTag != 0)
                    {
                        workerLabelService.SetLabel(newLabel, closestWorker.UnitTag);
                    }

                    Console.WriteLine($"Worker Initial Mining Assignment: {oldLabel} has been changed to {newLabel}");
                    remainingWorkers.Remove(closestWorker);
                    assignedPrefixes.Add(prefix);
                    reservedMineralIndices?.Add(mineral.OriginalIndex);
                    remainingAssignments--;
                }
            }
        }

        private int AssignPhaseThreeFarWorkers(List<List<OrderedMineral>> orderedMainMinerals, List<List<WorkerEntryDto>> multiStartingUnits, List<Vector2Dto> multiMineralCenterOfMass, WorkerLabelService workerLabelService, HashSet<int> reservedMineralIndices)
        {
            if (orderedMainMinerals == null || multiStartingUnits == null || workerLabelService == null)
            {
                return 0;
            }

            var totalAssignments = 0;

            for (int si = 0; si < orderedMainMinerals.Count; si++)
            {
                var minerals = orderedMainMinerals[si];
                var workers = multiStartingUnits.Count > si ? multiStartingUnits[si] : null;
                var com = multiMineralCenterOfMass != null && multiMineralCenterOfMass.Count > si ? multiMineralCenterOfMass[si] : null;

                if (minerals == null || workers == null || com == null)
                {
                    continue;
                }

                var farMinerals = minerals
                    .Where(m => m != null && !m.IsNear && m.Position != null && (reservedMineralIndices == null || !reservedMineralIndices.Contains(m.OriginalIndex)))
                    .OrderBy(m => m.Index)
                    .Take(4)
                    .ToList();

                if (farMinerals.Count == 0)
                {
                    continue;
                }

                var remainingWorkers = workers
                    .Where(w => w != null && (string.IsNullOrEmpty(w.Label) || w.Label.StartsWith("W")))
                    .ToList();

                if (remainingWorkers.Count < farMinerals.Count)
                {
                    continue;
                }

                // A better scenario could use a weight system across all phases so that some Phase 1
                // and Phase 2 decisions could be different and the overall outcome better. This is
                // good enough for now.

                var selectedWorkers = remainingWorkers
                    .OrderBy(w => Vector2.Distance(new Vector2(w.Position.X, w.Position.Y), new Vector2(com.X, com.Y)))
                    .Take(farMinerals.Count)
                    .ToList();

                var bestAssignment = FindBestWorkerMineralAssignment(selectedWorkers, farMinerals);
                if (bestAssignment.Count == 0)
                {
                    continue;
                }

                var phasePrefixCounts = new Dictionary<string, int>();
                foreach (var assignment in bestAssignment)
                {
                    var prefix = GetPhaseOneLabelPrefix(assignment.Mineral);
                    if (string.IsNullOrEmpty(prefix))
                    {
                        continue;
                    }

                    if (!phasePrefixCounts.ContainsKey(prefix))
                    {
                        phasePrefixCounts[prefix] = 0;
                    }

                    phasePrefixCounts[prefix]++;
                    var newLabel = $"{prefix}2";
                    var oldLabel = assignment.Worker.Label;

                    assignment.Worker.Label = newLabel;
                    if (assignment.Worker.UnitTag != 0)
                    {
                       // workerLabelService.SetLabel(newLabel, assignment.Worker.UnitTag);
                    }

                    Console.WriteLine($"Worker Initial Mining Assignment: {oldLabel} has been changed to {newLabel}");
                    totalAssignments++;
                }
            }

            return totalAssignments;
        }

        private void AssignPhaseFourWaitingWorkers(List<List<WorkerEntryDto>> multiStartingUnits, WorkerLabelService workerLabelService)
        {
            if (multiStartingUnits == null || workerLabelService == null)
            {
                return;
            }

            var phaseFourPrefixes = new[] { "T", "M", "B", "Y" };

            for (int si = 0; si < multiStartingUnits.Count; si++)
            {
                var workers = multiStartingUnits[si];
                if (workers == null || workers.Count == 0)
                {
                    continue;
                }

                var waitingWorkers = workers
                    .Where(w => w != null && (string.IsNullOrEmpty(w.Label) || w.Label.StartsWith("W")))
                    .Take(phaseFourPrefixes.Length)
                    .ToList();

                for (int i = 0; i < waitingWorkers.Count && i < phaseFourPrefixes.Length; i++)
                {
                    var worker = waitingWorkers[i];
                    var newLabel = $"{phaseFourPrefixes[i]}3";
                    var oldLabel = worker.Label;

                    worker.Label = newLabel;
                    if (worker.UnitTag != 0)
                    {
                        workerLabelService.SetLabel(newLabel, worker.UnitTag);
                    }

                    Console.WriteLine($"Worker Initial Mining Assignment: {oldLabel} has been changed to {newLabel}");
                }
            }
        }

        private List<(WorkerEntryDto Worker, OrderedMineral Mineral, float Distance)> FindBestWorkerMineralAssignment(List<WorkerEntryDto> workers, List<OrderedMineral> minerals)
        {
            var results = new List<(WorkerEntryDto Worker, OrderedMineral Mineral, float Distance)>();
            if (workers == null || minerals == null || workers.Count == 0 || minerals.Count == 0)
            {
                return results;
            }

            var workerCount = Math.Min(workers.Count, minerals.Count);
            var workerIndices = Enumerable.Range(0, workerCount).ToArray();
            var bestDistance = float.MaxValue;
            List<int> bestOrder = null;

            foreach (var permutation in GetPermutations(workerIndices, workerCount))
            {
                var totalDistance = 0f;
                for (int i = 0; i < workerCount; i++)
                {
                    var worker = workers[permutation[i]];
                    var mineral = minerals[i];
                    var target = mineral.SmHarvestPoint != null ? mineral.SmHarvestPoint : mineral.HarvestPoint;
                    if (worker?.Position == null || target == null)
                    {
                        totalDistance = float.MaxValue;
                        break;
                    }

                    totalDistance += Vector2.Distance(new Vector2(worker.Position.X, worker.Position.Y), new Vector2(target.X, target.Y));
                }

                if (totalDistance < bestDistance)
                {
                    bestDistance = totalDistance;
                    bestOrder = permutation.ToList();
                }
            }

            if (bestOrder == null)
            {
                return results;
            }

            for (int i = 0; i < workerCount; i++)
            {
                results.Add((workers[bestOrder[i]], minerals[i], bestDistance));
            }

            return results;
        }

        private IEnumerable<int[]> GetPermutations(int[] items, int length)
        {
            if (length == 1)
            {
                foreach (var item in items)
                {
                    yield return new[] { item };
                }
                yield break;
            }

            for (int i = 0; i < items.Length; i++)
            {
                var remaining = items.Where((_, index) => index != i).ToArray();
                foreach (var permutation in GetPermutations(remaining, length - 1))
                {
                    yield return (new[] { items[i] }).Concat(permutation).ToArray();
                }
            }
        }

        private string GetPhaseOneLabelPrefix(OrderedMineral mineral)
        {
            if (mineral == null)
            {
                return null;
            }

            if (mineral.IsNear)
            {
                return mineral.Index switch
                {
                    1 or 2 => "T",
                    3 or 4 => "M",
                    5 or 6 => "B",
                    7 or 8 => "Y",
                    _ => null
                };
            }

            return mineral.Index switch
            {
                1 or 2 => "T",
                3 or 4 => "M",
                5 or 6 => "B",
                7 or 8 => "Y",
                _ => null
            };
        }

        /// <summary>
        /// Register vespene labels (V1, V2, V3, etc.) with the VespeneLabelService.
        /// </summary>
        private void RegisterVespeneLabels(List<List<OrderedVespene>> orderedMainVespenes, VespeneLabelService vespeneLabelService, MawBaseLocationData mapData)
        {
            if (orderedMainVespenes == null || vespeneLabelService == null || mapData == null)
            {
                Console.WriteLine("InitialMapData.RegisterVespeneLabels: Invalid input");
                return;
            }

            Console.WriteLine($"InitialMapData.RegisterVespeneLabels: Starting with {orderedMainVespenes.Count} start location(s)");

            try
            {
                for (int startIdx = 0; startIdx < orderedMainVespenes.Count; startIdx++)
                {
                    var orderedList = orderedMainVespenes[startIdx];
                    Console.WriteLine($"InitialMapData.RegisterVespeneLabels: Start[{startIdx}] has {orderedList.Count} vespenes");

                    foreach (var orderedVespene in orderedList)
                    {
                        if (orderedVespene == null || orderedVespene.Position == null)
                            continue;

                        string label = orderedVespene.Label;  // V1 or V2
                        Color labelColor = ProcessVisableUnits.GetFinalLabelColor(label);

                        // Convert Vector2Dto to Point for registration (Z includes terrain height + 2.5f offset for gas visibility)
                        var position = new Point
                        {
                            X = orderedVespene.Position.X,
                            Y = orderedVespene.Position.Y,
                            Z = orderedVespene.Position.Z + 1.0f  // 0.5 units above the geyser for visibility
                        };

                        vespeneLabelService.SetVespeneLabel(label, position, labelColor);

                        mapData.VespeneFinalLabelsByPosition ??= new Dictionary<string, string>();
                        mapData.VespeneFinalLabelsByPosition[$"{orderedVespene.Position.X:F2},{orderedVespene.Position.Y:F2}"] = label;

                        Console.WriteLine($"InitialMapData.RegisterVespeneLabels: Start[{startIdx}] {label} = vespene at ({orderedVespene.Position.X:F2},{orderedVespene.Position.Y:F2}) distance to W4={orderedVespene.DistanceToW4:F2}");
                    }

                    // Count vespene labels
                    int vespeneCount = orderedList.Count;
                    Console.WriteLine($"InitialMapData.RegisterVespeneLabels: Start[{startIdx}] Summary: {vespeneCount} vespene geyser(s) labeled");
                }

                Console.WriteLine($"InitialMapData.RegisterVespeneLabels: Registered vespene labels for all start locations");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData.RegisterVespeneLabels: Error registering vespene labels: {ex.Message}");
            }
        }

        /// <summary>
        /// Perform greedy ordering of minerals based on distance from W12.
        /// Phase 1: Find M[8] = mineral closest to W12
        /// Phase 2: Greedy chain M[7-1] = closest remaining mineral to current position (descending)
        /// Phase 3: Classify each as Near or Far based on distance to Starting Townhall (cargo return efficiency)
        /// </summary>
        private List<OrderedMineral> GreedyOrderMinerals(List<Vector2Dto> minerals, Vector2Dto anchorPosition, Vector2Dto comPosition, Vector2Dto townhallPosition, int startIndex, List<uint> mineralResources = null, List<ulong> mineralTags = null)
        {
            var result = new List<OrderedMineral>();

            if (minerals == null || minerals.Count == 0 || anchorPosition == null)
            {
                Console.WriteLine($"InitialMapData.GreedyOrderMinerals: Invalid input for Start[{startIndex}]");
                return result;
            }

            // Build a greedy path from the anchor worker to the closest mineral, then keep chaining
            // from each chosen mineral to the closest remaining mineral.
            var remainingIndices = new List<int>();
            for (int i = 0; i < minerals.Count; i++)
            {
                remainingIndices.Add(i);
            }

            var orderedIndices = new List<int>();
            var currentAnchor = new Vector2(anchorPosition.X, anchorPosition.Y);

            while (remainingIndices.Count > 0)
            {
                int chosenIdx = -1;
                float chosenDistance = float.MaxValue;

                foreach (var idx in remainingIndices)
                {
                    var mineral = minerals[idx];
                    var dist = Vector2.Distance(new Vector2(mineral.X, mineral.Y), currentAnchor);
                    if (dist < chosenDistance)
                    {
                        chosenDistance = dist;
                        chosenIdx = idx;
                    }
                }

                if (chosenIdx < 0)
                {
                    break;
                }

                orderedIndices.Add(chosenIdx);
                remainingIndices.Remove(chosenIdx);
                currentAnchor = new Vector2(minerals[chosenIdx].X, minerals[chosenIdx].Y);
            }

            var nearFarThreshold = townhallPosition != null
                ? minerals.Average(m => Vector2.Distance(new Vector2(m.X, m.Y), new Vector2(townhallPosition.X, townhallPosition.Y)))
                : float.MaxValue;
            nearFarThreshold = nearFarThreshold > 0.25f ? nearFarThreshold - 0.25f : nearFarThreshold;

            for (int orderIndex = 0; orderIndex < orderedIndices.Count; orderIndex++)
            {
                var mineralIndex = orderedIndices[orderIndex];
                var mineral = minerals[mineralIndex];
                var displayIndex = orderedIndices.Count - orderIndex;
                var distFromCom = comPosition != null
                    ? Vector2.Distance(new Vector2(mineral.X, mineral.Y), new Vector2(comPosition.X, comPosition.Y))
                    : float.MaxValue;
                var distanceToTownhall = townhallPosition != null
                    ? Vector2.Distance(new Vector2(mineral.X, mineral.Y), new Vector2(townhallPosition.X, townhallPosition.Y))
                    : float.MaxValue;
                var linePoints = townhallPosition != null ? BuildMineralLinePoints(mineral, townhallPosition) : (null, null, null, null);

                result.Add(new OrderedMineral
                {
                    Position = mineral,
                    HarvestPoint = linePoints.Item1,
                    SmHarvestPoint = linePoints.Item2,
                    ReturnPoint = linePoints.Item3,
                    SmReturnPoint = linePoints.Item4,
                    Index = displayIndex,
                    OriginalIndex = mineralIndex,
                    DistanceFromCOM = distFromCom,
                    DistanceToTownhall = distanceToTownhall,
                    IsNear = distanceToTownhall <= nearFarThreshold,
                    IsLarge = distanceToTownhall <= nearFarThreshold || displayIndex == 3 || displayIndex == 4,
                    IsFar = !(distanceToTownhall <= nearFarThreshold || displayIndex == 3 || displayIndex == 4),
                    Resources = mineralResources != null && mineralIndex < mineralResources.Count ? mineralResources[mineralIndex] : 0,
                    UnitTag = mineralTags != null && mineralIndex < mineralTags.Count ? mineralTags[mineralIndex] : 0
                });

                if (displayIndex == 1 || displayIndex == 8)
                {
                    Console.WriteLine($"InitialMapData.GreedyOrderMinerals: Start[{startIndex}] M{displayIndex} IsFar={result[^1].IsFar} IsNear={result[^1].IsNear} IsLarge={result[^1].IsLarge} distanceToTownhall={distanceToTownhall:F2}");
                    //System.Diagnostics.Debugger.Break();
                }
            }

            // Log the ordering
            Console.WriteLine($"InitialMapData.GreedyOrderMinerals: Start[{startIndex}] ordering complete:");

            // Detect mineral sizes and classify. If the marker data is uniform or unavailable,
            // fall back to the Near/Far split because this map uses distance patterns.
            var distinctResourceValues = result.Select(ord => ord.Resources).Distinct().ToList();
            if (distinctResourceValues.Count <= 1 || distinctResourceValues.All(v => v == 0u))
            {
                Console.WriteLine($"InitialMapData.GreedyOrderMinerals: Start[{startIndex}] uniform resource markers; classifying by Near/Far pattern.");
                ClassifyMineralSizesByNearFar(result);
            }
            else
            {
                ClassifyMineralSizes(result, minerals);
            }

            // Count classification summary
            int nearCount = 0, largeCount = 0, farCount = 0;
            foreach (var ord in result)
            {
                if (ord.Size == MineralSize.Large && !ord.IsNear)
                    largeCount++;
                else if (ord.IsNear)
                    nearCount++;
                else
                    farCount++;
            }

            Console.WriteLine($"InitialMapData.GreedyOrderMinerals: Classification summary: {nearCount}N, {largeCount}L, {farCount}F");

            foreach (var ord in result)
            {
                string sizeStr = ord.Size == MineralSize.Large ? "Large" : (ord.Size == MineralSize.Normal ? "Normal" : "Small");
                // ord.Index is intentionally reversed by greedy ordering (M8..M1); keep this log format unchanged.
                var label = ord.IsNear ? $"N{ord.Index}" : (ord.Size == MineralSize.Large ? $"L{ord.Index}" : $"F{ord.Index}");
                Console.WriteLine($"  M[{ord.Index}] = mineral[{ord.OriginalIndex}] tag={ord.Position?.X:F2},{ord.Position?.Y:F2} at ({ord.Position.X:F2},{ord.Position.Y:F2}) distance={ord.DistanceToTownhall:F2} {sizeStr} {label}");
            }

            return result;
        }

        /// <summary>
        /// Classify mineral patches by size based on actual resource distribution at this start location.
        /// Finds the two distinct resource levels (e.g., 400 and 1800) and classifies:
        /// - Large: minerals matching the HIGHER resource amount
        /// - Normal: minerals matching the LOWER resource amount
        /// This is map-agnostic and works with any resource values.
        /// </summary>
        private void ClassifyMineralSizes(List<OrderedMineral> orderedMinerals, List<Vector2Dto> allMinerals)
        {
            if (orderedMinerals == null || orderedMinerals.Count == 0)
                return;

            try
            {
                // Collect all unique resource amounts and their counts
                var resourceGroups = new Dictionary<uint, int>();
                foreach (var ord in orderedMinerals)
                {
                    if (!resourceGroups.ContainsKey(ord.Resources))
                        resourceGroups[ord.Resources] = 0;
                    resourceGroups[ord.Resources]++;
                }

                // Sort by resource amount to identify low and high
                var sortedResources = resourceGroups.Keys.OrderBy(r => r).ToList();

                uint lowResourceValue = 0;
                uint highResourceValue = 0;

                if (sortedResources.Count >= 2)
                {
                    // Two or more distinct resource types - use lowest and highest
                    lowResourceValue = sortedResources[0];
                    highResourceValue = sortedResources[sortedResources.Count - 1];
                    Console.WriteLine($"InitialMapData.ClassifyMineralSizes: Found {sortedResources.Count} distinct resource levels: {string.Join(", ", sortedResources)}");
                }
                else if (sortedResources.Count == 1)
                {
                    // Only one resource type - treat all as Normal
                    lowResourceValue = sortedResources[0];
                    highResourceValue = sortedResources[0];
                    Console.WriteLine($"InitialMapData.ClassifyMineralSizes: Only one resource level found: {lowResourceValue}");
                }

                int largeCount = 0;
                int normalCount = 0;

                // Classify based on which distribution the mineral belongs to
                foreach (var ord in orderedMinerals)
                {
                var isLarge = ord.IsNear || ord.Index == 3 || ord.Index == 4;
                ord.Size = isLarge ? MineralSize.Large : MineralSize.Normal;
                ord.IsLarge = isLarge;
                ord.IsFar = !isLarge;
                if (isLarge)
                {
                    largeCount++;
                }
                else
                {
                    normalCount++;
                }
                }

                Console.WriteLine($"InitialMapData.ClassifyMineralSizes: Classified {orderedMinerals.Count} minerals: {largeCount} Large ({highResourceValue} resources), {normalCount} Normal ({lowResourceValue} resources)");
                foreach (var ord in orderedMinerals)
                {
                    Console.WriteLine($"  M[{ord.Index}] Resources={ord.Resources} -> {(ord.Size == MineralSize.Large ? "Large" : "Normal")} (IsNear={ord.IsNear})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData.ClassifyMineralSizes: Error during classification: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback classification when mineral resource markers are uniform or missing.
        /// Near minerals are treated as large, far minerals as normal.
        /// </summary>
        private void ClassifyMineralSizesByNearFar(List<OrderedMineral> orderedMinerals)
        {
            if (orderedMinerals == null || orderedMinerals.Count == 0)
                return;

            int largeCount = 0;
            int normalCount = 0;

            foreach (var ord in orderedMinerals)
            {
                var isLarge = ord.IsNear || ord.Index == 3 || ord.Index == 4;
                ord.Size = isLarge ? MineralSize.Large : MineralSize.Normal;
                ord.IsLarge = isLarge;
                ord.IsFar = !isLarge;
                if (isLarge)
                {
                    largeCount++;
                }
                else
                {
                    normalCount++;
                }
            }

            Console.WriteLine($"InitialMapData.ClassifyMineralSizesByNearFar: Classified {orderedMinerals.Count} minerals: {largeCount} Near/Large, {normalCount} Far/Normal");
        }

        /// <summary>
        /// Determine a mineral resource marker from observed contents, type, and known wall/rich mineral patterns.
        /// </summary>
        private uint GetMineralResourceMarker(Unit unit, Sharky.UnitTypes unitType)
        {
            return 0u;
        }

        /// <summary>
        /// Build fallback mineral resource markers using proximity to the townhall.
        /// Closest half are treated as large (1800), remaining minerals are normal (400).
        /// </summary>
        private List<uint> BuildMineralResourceMarkersByDistance(List<Vector2Dto> minerals, Vector2Dto townhallPosition)
        {
            var result = new List<uint>();
            if (minerals == null || minerals.Count == 0)
                return result;

            if (townhallPosition == null)
            {
                for (int i = 0; i < minerals.Count; i++)
                    result.Add(400u);
                return result;
            }

            var ordered = minerals
                .Select((m, idx) => new
                {
                    Mineral = m,
                    Index = idx,
                    Distance = Vector2.Distance(new Vector2(m.X, m.Y), new Vector2(townhallPosition.X, townhallPosition.Y))
                })
                .OrderBy(x => x.Distance)
                .ToList();

            ordered.Reverse();

            // Assign V1 (furthest), V2 (next closest), etc.
            for (int vi = 0; vi < ordered.Count; vi++)
            {
                var vd = ordered[vi];
                var label = $"V{vi + 1}";  // V1, V2, V3, etc.

                result.Add(vi < (ordered.Count / 2) ? 1800u : 400u);
            }

            Console.WriteLine($"InitialMapData.BuildMineralResourceMarkersByDistance: {ordered.Count / 2} large, {ordered.Count - ordered.Count / 2} normal based on distance to townhall");
            return result;
        }

        /// <summary>
        /// Fallback method: Analyze mineral distribution by position clustering.
        /// When unit type names don't reveal sizes, use spatial analysis.
        /// Large minerals tend to be further apart; small minerals cluster in pairs.
        /// </summary>
        private List<uint> AnalyzeMineralDistribution(List<Vector2Dto> minerals, Vector2Dto w1Position)
        {
            var result = new List<uint>();
            if (minerals == null || minerals.Count == 0)
                return result;

            try
            {
                // For standard 8-mineral starts: 4 large + 4 small
                // Strategy: Find pairwise distances, cluster minerals
                // Closest pairs are likely small deposits; isolated minerals are large

                var distances = new List<(int idx1, int idx2, float dist)>();
                for (int i = 0; i < minerals.Count; i++)
                {
                    for (int j = i + 1; j < minerals.Count; j++)
                    {
                        var d = Vector2.Distance(new Vector2(minerals[i].X, minerals[i].Y), new Vector2(minerals[j].X, minerals[j].Y));
                        distances.Add((i, j, d));
                    }
                }

                // Sort by distance - shortest distances first
                distances = distances.OrderBy(x => x.dist).ToList();

                // Mark minerals in pairs (closest pairs are small minerals)
                var isPaired = new HashSet<int>();
                var smallMinerals = new HashSet<int>();

                int pairsFound = 0;
                foreach (var (idx1, idx2, dist) in distances)
                {
                    // If distance < 3.0, likely a small pair
                    if (dist < 3.0f && !isPaired.Contains(idx1) && !isPaired.Contains(idx2))
                    {
                        isPaired.Add(idx1);
                        isPaired.Add(idx2);
                        smallMinerals.Add(idx1);
                        smallMinerals.Add(idx2);
                        pairsFound++;
                        if (pairsFound >= (minerals.Count / 2))
                            break; // Found enough pairs
                    }
                }

                // Classify: paired minerals = 400u (small), others = 1800u (large)
                for (int i = 0; i < minerals.Count; i++)
                {
                    result.Add(smallMinerals.Contains(i) ? 400u : 1800u);
                }

                Console.WriteLine($"InitialMapData.AnalyzeMineralDistribution: Clustered {minerals.Count} minerals - Found {pairsFound} pairs of small deposits, {minerals.Count - smallMinerals.Count} large deposits");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitialMapData.AnalyzeMineralDistribution: Error during analysis - {ex.Message}. Defaulting to all 400u.");
                for (int i = 0; i < minerals.Count; i++)
                    result.Add(400u);
            }

            return result;
        }
    }
}
