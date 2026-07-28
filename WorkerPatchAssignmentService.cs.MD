using System;
using System.Collections.Generic;
using Maw.Setup;
using Newtonsoft.Json;
using System.IO;

namespace Maw.Setup
{
    // Minimal DTOs used by SpeedMiningTask. These are intentionally small and
    // only include the members referenced from SpeedMiningTask so they compile
    // without pulling in the original larger implementation.
    public class AssignmentEntry { }

    public class LabelEntry
    {
        public double X { get; set; }
        public double Y { get; set; }
        public string UnitType { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    public class StartLocationData
    {
        public int StartId { get; set; }
        public double[] Anchor { get; set; } = Array.Empty<double>();
        public List<LabelEntry> Labels { get; set; } = new();
        public Dictionary<string, AssignmentEntry> Assignments { get; set; } = new();
        public Dictionary<string, float[]> ApproachVectors { get; set; } = new();
        public Dictionary<string, float[]> DropOffPoints { get; set; } = new();
    }

    public class TelemetryInfo
    {
        public int SampleRuns { get; set; }
        public string LastUpdatedUtc { get; set; } = string.Empty;
    }

    public class MapSpeedMiningData
    {
        public string MapName { get; set; } = string.Empty;
        public int SpeedMiningVersion { get; set; }
        public List<StartLocationData> StartLocations { get; set; } = new();
        public TelemetryInfo Telemetry { get; set; } = new();
    }

    /// <summary>
    /// Lightweight replacement/stub for the original WorkerPatchAssignmentService.
    /// Provides the members required by SpeedMiningTask: GetAssignmentForStart and PersistMapSpeedMining.
    /// This implementation is intentionally conservative: it returns an empty StartLocationData
    /// when no persisted map file is found, and it persists map JSON to the canonical file path.
    /// </summary>
    public class WorkerPatchAssignmentService
    {
        public WorkerPatchAssignmentService() { }

        public StartLocationData GetAssignmentForStart(string mapName, double anchorX, double anchorY, int startId)
        {
            // Attempt to load a canonical map file if it exists; otherwise return an empty StartLocationData
            try
            {
                var path = MapFileUtils.CanonicalMapFilePath(mapName, anchorX, anchorY);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var mapData = JsonConvert.DeserializeObject<MapSpeedMiningData>(json);
                    if (mapData?.StartLocations != null && mapData.StartLocations.Count > 0)
                    {
                        // Return the first start location that matches StartId if available
                        var found = mapData.StartLocations.Find(s => s.StartId == startId);
                        if (found != null) return found;
                        return mapData.StartLocations[0];
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            return new StartLocationData
            {
                StartId = startId,
                Anchor = new double[] { Math.Round(anchorX, 3), Math.Round(anchorY, 3) },
                Labels = new List<LabelEntry>(),
                Assignments = new Dictionary<string, AssignmentEntry>(),
                ApproachVectors = new Dictionary<string, float[]>(),
                DropOffPoints = new Dictionary<string, float[]>()
            };
        }

        public void PersistMapSpeedMining(MapSpeedMiningData mapData, string mapName, double anchorX, double anchorY)
        {
            try
            {
                var path = MapFileUtils.CanonicalMapFilePath(mapName, anchorX, anchorY);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(mapData, Formatting.Indented));
            }
            catch
            {
                // best-effort only
            }
        }
    }
}
