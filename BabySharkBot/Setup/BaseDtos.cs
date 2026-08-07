using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using SC2APIProtocol;
using System.Numerics;
using Sharky;
using MemoryPack;
using BabySharkBot.Managers;
using BabySharkBot.Manager;

#nullable enable

namespace BabySharkBot.Setup
{
    // Data Transfer Objects for base location information
    [MemoryPackable]
    public partial class Vector2Dto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        [MemoryPackConstructor]
        public Vector2Dto() { }

        public Vector2Dto(float x, float y)
        {
            X = x;
            Y = y;
            Z = 0f;
        }

        public Vector2Dto(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    [MemoryPackable]
    public partial class WorkerEntryDto
    {
        public ulong UnitTag { get; set; }
        public Vector2Dto Position { get; set; } = new Vector2Dto();
        public uint UnitType { get; set; }
        public string Label { get; set; } = string.Empty;
        public string StartLabel { get; set; } = string.Empty;
        public string FinalLabel { get; set; } = string.Empty;
        public int FirstSeenFrame { get; set; }
        public Vector2Dto FirstSeenPosition { get; set; } = new Vector2Dto();
        public bool BecameVisible { get; set; }
        public bool IsMorphing { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsCarrying { get; set; }
        public bool WasCarrying { get; set; }
        public bool JustPickedUp { get; set; }
    }

    /// <summary>
    /// Represents a mineral patch indexed in the order it was first encountered.
    /// </summary>
    [MemoryPackable]
    public partial class MineralDto
    {
        public ulong UnitTag { get; set; }
        public uint UnitType { get; set; }
        public int MineralIndex { get; set; }
        public Vector2Dto Position { get; set; } = new Vector2Dto();
        public int MineralContents { get; set; }
        public int MaxMineralContents { get; set; }
    }

    /// <summary>
    /// Mineral size classification: Small (~45 radius), Normal (~57 radius), Large (~90+ radius)
    /// Used to distinguish strategic large minerals from normal patches
    /// </summary>
    public enum MineralSize
    {
        Small = 0,
        Normal = 1,
        Large = 2
    }

    /// <summary>
    /// Represents a mineral patch ordered by greedy chain from a starting worker (W1).
    /// M[8] is furthest from W1, M[7] is closest to M[8], etc., down to M[1].
    /// After ordering, classified as Near or Far based on distance to starting townhall.
    /// Large minerals that are Far become L1-L4 instead of F1-F4.
    /// Descending index (8→1) for easier binary serialization and debugging.
    /// </summary>
    [MemoryPackable]
    public partial class OrderedMineral
    {
        /// <summary>
        /// Position coordinates of this mineral patch
        /// </summary>
        public Vector2Dto Position { get; set; } = new Vector2Dto();

        /// <summary>
        /// Harvest point on the line from mineral to hatchery.
        /// </summary>
        public Vector2Dto HarvestPoint { get; set; } = new Vector2Dto();

        /// <summary>
        /// Small harvest point, 1u inward from HarvestPoint.
        /// </summary>
        public Vector2Dto SmHarvestPoint { get; set; } = new Vector2Dto();

        /// <summary>
        /// Return point on the line from mineral to hatchery.
        /// </summary>
        public Vector2Dto ReturnPoint { get; set; } = new Vector2Dto();

        /// <summary>
        /// Small return point, 1u inward from ReturnPoint.
        /// </summary>
        public Vector2Dto SmReturnPoint { get; set; } = new Vector2Dto();

        /// <summary>
        /// Index in greedy chain: 8-1 (M8-M1, descending)
        /// 8 = furthest from W1 (first in greedy chain)
        /// 7-1 = greedy chain closest to previous (descending)
        /// Descending order for binary serialization and debugging ease
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// True if mineral is near townhall, False if far
        /// Used with Size to determine N1-N4, F1-F4, or L1-L4 labeling
        /// Threshold: distance_to_townhall <= (avg_townhall_distance - 0.25)
        /// </summary>
        public bool IsNear { get; set; }

        /// <summary>
        /// True if this mineral is the far mineral for its spawn layout.
        /// Persisted so the same mining behavior can be replayed across games.
        /// </summary>
        public bool IsFar { get; set; }

        /// <summary>
        /// Distance from this mineral to the mineral center of mass
        /// Used for visualization reference only
        /// </summary>
        public float DistanceFromCOM { get; set; }

        /// <summary>
        /// Distance from this mineral to the starting townhall
        /// Used for Near/Far classification threshold
        /// </summary>
        public float DistanceToTownhall { get; set; }

        /// <summary>
        /// Size classification of this mineral patch (Small, Normal, or Large)
        /// Large minerals that are Far become L1-L4 instead of F1-F4
        /// </summary>
        public MineralSize Size { get; set; } = MineralSize.Normal;

        /// <summary>
        /// True when this mineral patch is classified as Large.
        /// </summary>
        public bool IsLarge { get; set; }

        /// <summary>
        /// Original index in multiMainMinerals[si] before ordering
        /// Useful for cross-referencing with unordered mineral lists
        /// </summary>
        public int OriginalIndex { get; set; }

        /// <summary>
        /// Resource amount from SC2 unit data (typically 400 for small, 1800 for large)
        /// Used to determine if this mineral is Large based on actual resource count
        /// </summary>
        public uint Resources { get; set; }

        /// <summary>
        /// Team-based label assignment: N# (near) or F# (far) per worker team
        /// Set by AssignWorkersToMineralsPerTeam() based on worker proximity tests
        /// Replaces the old L# (large far) system - now only N# and F# are used
        /// </summary>
        public string TeamLabel { get; set; } = "";

        /// <summary>
        /// Fully registered mineral label, such as M8-N4 or M7-F4.
        /// Set by InitialMapData after team assignment so the mineral label itself is explicit.
        /// </summary>
        public string Label { get; set; } = "";

        /// <summary>
        /// Unit tag from SC2 unit data.
        /// </summary>
        public ulong UnitTag { get; set; } = 0;

        /// <summary>
        /// Final replay label for the mineral, such as TA, TB, MA, MB, BA, BB, YA, or YB.
        /// This is stored so secondary map data can replay mineral identity from the observed X/Y.
        /// </summary>
        public string FinalLabel { get; set; } = "";

        /// <summary>
        /// Team number this mineral is assigned to (1-4)
        /// Used for coordinating worker assignments
        /// </summary>
        public int TeamNumber { get; set; } = 0;
    }

    /// <summary>
    /// Represents a vespene geyser ordered by greedy chain from the 4th starting worker (W4).
    /// V1 is closest to W4, V2 is next closest to W4 (typically 2 vespenes per base in SC2).
    /// </summary>
    [MemoryPackable]
    public partial class OrderedVespene
    {
        /// <summary>
        /// Position coordinates of this vespene geyser
        /// </summary>
        public Vector2Dto Position { get; set; } = new Vector2Dto();

        /// <summary>
        /// Harvest point on the line from geyser to hatchery.
        /// </summary>
        public Vector2Dto HarvestPoint { get; set; } = new Vector2Dto();

        /// <summary>
        /// Return point on the line from geyser to hatchery.
        /// </summary>
        public Vector2Dto ReturnPoint { get; set; } = new Vector2Dto();

        /// <summary>
        /// Index in greedy chain: 1-2 (V1-V2)
        /// 1 = closest to W4
        /// 2 = next closest to W4
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Distance from this vespene to W4 (4th starting worker)
        /// Used for ordering: lower distance = lower index (closer to W4)
        /// </summary>
        public float DistanceToW4 { get; set; }

        /// <summary>
        /// Label assignment: V1 or V2 per base location
        /// Set by InitialMapData based on distance ordering to W4
        /// </summary>
        public string Label { get; set; } = "";

        /// <summary>
        /// Unit tag from SC2 unit data.
        /// </summary>
        public ulong UnitTag { get; set; } = 0;
    }

    /// <summary>
    /// Harvest and return cargo geometry for a single resource node.
    /// </summary>
    [MemoryPackable]
    public partial class HarvestReturnCargoPointDto
    {
        public ulong UnitTag { get; set; }
        public uint UnitType { get; set; }
        public string Label { get; set; } = "";
        public Vector2Dto ResourcePosition { get; set; } = new Vector2Dto();
        public Vector2Dto HarvestPoint { get; set; } = new Vector2Dto();
        public Vector2Dto SmHarvestPoint { get; set; } = new Vector2Dto();
        public Vector2Dto ReturnPoint { get; set; } = new Vector2Dto();
        public Vector2Dto SmReturnPoint { get; set; } = new Vector2Dto();

        [MemoryPackConstructor]
        public HarvestReturnCargoPointDto() { }
    }

    /// <summary>
    /// Shared JIT mining geometry for a pair of minerals.
    /// The pair is ordered as A/B for alternating workers.
    /// </summary>
    [MemoryPackable]
    public partial class MiningPairCargoPointDto
    {
        public int PairIndex { get; set; }
        public string Label { get; set; } = string.Empty;
        public Vector2Dto FirstMineralPosition { get; set; } = new Vector2Dto();
        public Vector2Dto SecondMineralPosition { get; set; } = new Vector2Dto();
        public Vector2Dto JitReturnPoint { get; set; } = new Vector2Dto();
        public Vector2Dto FirstHarvestPoint { get; set; } = new Vector2Dto();
        public Vector2Dto SecondHarvestPoint { get; set; } = new Vector2Dto();
        public Vector2Dto FirstReturnPoint { get; set; } = new Vector2Dto();
        public Vector2Dto SecondReturnPoint { get; set; } = new Vector2Dto();

        [MemoryPackConstructor]
        public MiningPairCargoPointDto() { }
    }

    [MemoryPackable]
    public partial class TeamPatchAssignmentDto
    {
        public int TeamNumber { get; set; }
        public string NearLabel { get; set; } = string.Empty;
        public string FarLabel { get; set; } = string.Empty;
        public string TeamId { get; set; } = string.Empty;
        public List<WorkerEntryDto> Workers { get; set; } = new List<WorkerEntryDto>();
        public List<OrderedMineral> Minerals { get; set; } = new List<OrderedMineral>();
        public Vector2Dto JitReturnPoint { get; set; } = new Vector2Dto();
        public Vector2Dto JitWaitPoint { get; set; } = new Vector2Dto();

        [MemoryPackConstructor]
        public TeamPatchAssignmentDto() { }
    }


    [MemoryPackable]
    public partial class ExtractorTrickData
    {
        public int StartIndex { get; set; }
        public string TeamPrefix { get; set; } = ""; // Team closest to V1
        public Vector2Dto V1Position { get; set; } = new();
        public Vector2Dto ShortestPathPoint { get; set; } = new(); // Townhall → V1 midpoint
        public float DistanceToV1 { get; set; }
    }

    [MemoryPackable]
    public partial class TeamTransitionInfo
    {
        public int PairIndex { get; set; }
        public string CurrentPrefix { get; set; } = "";
        public bool HasTransitionedTo12Color { get; set; }
        public bool Has4thWorker { get; set; }
        public bool IsSpeedMining { get; set; }
    }

    [MemoryPackable]
    public partial class PinkWorkerAssignment
    {
        public int WorkerNumber { get; set; } // 13, 14, or 15
        public string Label { get; set; } = "";
        public string PrimaryMineralLabel { get; set; } = "";
        public string SecondaryMineralLabel { get; set; } = "";
        public string FinalTeamPrefix { get; set; } = "";
    }

    [MemoryPackable]
    public partial class MapLocationData
    {
        public List<Vector2Dto> MineralPatches { get; set; } = new List<Vector2Dto>();
        public List<Vector2Dto> VespenePatches { get; set; } = new List<Vector2Dto>();
    }

    [MemoryPackable]
    public partial class EnemyUnitObservationDto
    {
        public ulong UnitTag { get; set; }
        public Vector2Dto Position { get; set; } = new();
        public Vector2Dto LastXY { get; set; } = new();
        public string Notes { get; set; } = string.Empty;
        public float DPS { get; set; }
        public List<string> Spells { get; set; } = new();
        public List<float> CoolDowns { get; set; } = new();
    }

    [MemoryPackable]
    public partial class UnitReadyForLabelingDto
    {
        public List<ulong> Overlord { get; set; } = new();
        public List<ulong> Zergling { get; set; } = new();
        public List<ulong> Drone { get; set; } = new();
        public List<ulong> Queen { get; set; } = new();
        public List<ulong> Other { get; set; } = new();

        public void Clear()
        {
            Overlord.Clear();
            Zergling.Clear();
            Drone.Clear();
            Queen.Clear();
            Other.Clear();
        }
    }

    [MemoryPackable]
    public partial class ObservationSnapshotDto
    {
        public int Frame { get; set; }
        public List<ulong> AvailableWorkers { get; set; } = new();
        public List<ulong> AvailableLarva { get; set; } = new();
        public List<ulong> AvailableQueens { get; set; } = new();
        public List<ulong> AvailableOverlords { get; set; } = new();
        public UnitReadyForLabelingDto ReadyForLabeling { get; set; } = new();
        public Dictionary<ulong, EnemyUnitObservationDto> EnemyUnits { get; set; } = new();
        public Dictionary<ulong, WorkerEntryDto> SelfUnits { get; set; } = new();
        public Dictionary<ulong, Vector2Dto> WorkerPositions { get; set; } = new();
        public Dictionary<ulong, MineralDto> Minerals { get; set; } = new();
        public Dictionary<ulong, OrderedVespene> Vespene { get; set; } = new();
    }

    [MemoryPackable]
    public partial class MawBaseLocationData
    {
        public Vector2Dto[] StartingTownHall { get; set; } = new Vector2Dto[0];
        public List<Vector2Dto> ExpansionTownhalls { get; set; } = new List<Vector2Dto>();
        public MapLocationData MapLocationData { get; set; } = new MapLocationData();
        public List<MineralDto> Minerals { get; set; } = new List<MineralDto>();
        public Dictionary<ulong, int> MineralTagToIndex { get; set; } = new Dictionary<ulong, int>();
        public Dictionary<uint, int> MineralTypeMaxContents { get; set; } = new Dictionary<uint, int>();
        public Dictionary<uint, bool> MineralTypeContentsAreUniform { get; set; } = new Dictionary<uint, bool>();
        public bool MismatchedMinerals { get; set; } = false;
        public List<List<Vector2Dto>> MainMinerals { get; set; } = new List<List<Vector2Dto>>();
        public List<List<OrderedMineral>> OrderedMainMinerals { get; set; } = new List<List<OrderedMineral>>();
        public List<List<Vector2Dto>> MainVespene { get; set; } = new List<List<Vector2Dto>>();
        public List<List<HarvestReturnCargoPointDto>> MainMineralCargoPoints { get; set; } = new List<List<HarvestReturnCargoPointDto>>();
        public List<List<MiningPairCargoPointDto>> MainMineralJitCargoPoints { get; set; } = new List<List<MiningPairCargoPointDto>>();
        public List<List<HarvestReturnCargoPointDto>> MainVespeneCargoPoints { get; set; } = new List<List<HarvestReturnCargoPointDto>>();
        public List<List<OrderedVespene>> OrderedMainVespene { get; set; } = new List<List<OrderedVespene>>();
        public Dictionary<string, string> MineralFinalLabelsByPosition { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> VespeneFinalLabelsByPosition { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ExpansionMineralLabels { get; set; } = new Dictionary<string, string>();
        public List<Vector2Dto> MineralCenterOfMass { get; set; } = new List<Vector2Dto>();
        public List<Vector2Dto> ExpansionMineralCenterOfMass { get; set; } = new List<Vector2Dto>();
        public List<List<WorkerEntryDto>> StartingUnits { get; set; } = new List<List<WorkerEntryDto>>();
        public List<List<WorkerEntryDto>> SecondaryStartingUnits { get; set; } = new List<List<WorkerEntryDto>>();
        public Dictionary<int, List<List<WorkerEntryDto>>> StartingUnitsByWorkerCount { get; set; } = new Dictionary<int, List<List<WorkerEntryDto>>>();
        public List<List<OrderedMineral>> SecondaryOrderedMainMinerals { get; set; } = new List<List<OrderedMineral>>();
        public List<Vector2Dto> SecondaryMineralCenterOfMass { get; set; } = new List<Vector2Dto>();
        public List<List<TeamPatchAssignmentDto>> SecondaryTeamPatchAssignments { get; set; } = new List<List<TeamPatchAssignmentDto>>();
        public List<List<TeamPatchAssignmentDto>> TeamPatchAssignments { get; set; } = new List<List<TeamPatchAssignmentDto>>();
        public List<Vector2Dto> SpawningPoolPlacements { get; set; } = new List<Vector2Dto>();
        public List<Vector2Dto> MacroHatcheryPlacements { get; set; } = new List<Vector2Dto>();
        public List<Vector2Dto> RoachWarrenPlacements { get; set; } = new List<Vector2Dto>();
        public bool[] BaseHasBeenPlayed8 { get; set; } = new bool[0];
        public bool[] BaseHasBeenPlayed12 { get; set; } = new bool[0];
        public bool[] BaseHasBeenPlayed { get; set; } = new bool[0];
        public Dictionary<int, List<List<TeamPatchAssignmentDto>>> AssignmentsByWorkerCount { get; set; } = new Dictionary<int, List<List<TeamPatchAssignmentDto>>>();
        public bool[] M1IsFar { get; set; } = new bool[0];
        public bool[] M8IsFar { get; set; } = new bool[0];
        public Dictionary<string, bool> AssignmentFlags { get; set; } = new Dictionary<string, bool>();
        public Dictionary<int, Dictionary<string, bool>> AssignmentFlagsByStart { get; set; } = new Dictionary<int, Dictionary<string, bool>>();
        public Dictionary<int, ExpansionPointModel> ExpansionPoints { get; set; } = new Dictionary<int, ExpansionPointModel>();
        public List<List<HarvestReturnCargoPointDto>> ExpansionMineralCargoPoints { get; set; } = new List<List<HarvestReturnCargoPointDto>>();
        public List<List<MiningPairCargoPointDto>> ExpansionMineralJitCargoPoints { get; set; } = new List<List<MiningPairCargoPointDto>>();
        public List<List<HarvestReturnCargoPointDto>> ExpansionVespeneCargoPoints { get; set; } = new List<List<HarvestReturnCargoPointDto>>();
        public Dictionary<int, ExpansionPointModel> ProvisionalExpansionPoints { get; set; } = new Dictionary<int, ExpansionPointModel>();
        public List<ExtractorTrickData> ExtractorTrickDataByStart { get; set; } = new();
        public List<TeamTransitionInfo> TeamTransitions { get; set; } = new();
        public List<PinkWorkerAssignment> PinkWorkerAssignments { get; set; } = new();
    }

    public class WorkerLabelService
    {
        private readonly Dictionary<string, ulong> _labelToTag = new();
        private readonly Dictionary<ulong, string> _tagToLabel = new();
        private readonly object _lock = new();

        public WorkerLabelService() { }

        public void Initialize(ProtobufProxy proxy, Func<ulong, Unit?> getUnitByTag) { }
        public void UpdateRawUnits(List<Unit>? rawUnits) { }

        public void SetLabel(string label, ulong tag, Point? pos = null)
        {
            if (string.IsNullOrWhiteSpace(label) || tag == 0)
            {
                return;
            }

            lock (_lock)
            {
                var mappingChanged = !_tagToLabel.TryGetValue(tag, out var existingLabel)
                    || !string.Equals(existingLabel, label, StringComparison.Ordinal);

                if (_tagToLabel.TryGetValue(tag, out existingLabel)
                    && !string.Equals(existingLabel, label, StringComparison.Ordinal))
                {
                    _tagToLabel.Remove(tag);
                    if (_labelToTag.TryGetValue(existingLabel, out var mappedTag) && mappedTag == tag)
                    {
                        _labelToTag.Remove(existingLabel);
                    }
                }

                if (_labelToTag.TryGetValue(label, out var existingTag) && existingTag != tag)
                {
                    _labelToTag.Remove(label);
                    if (_tagToLabel.TryGetValue(existingTag, out var mappedLabel)
                        && string.Equals(mappedLabel, label, StringComparison.Ordinal))
                    {
                        _tagToLabel.Remove(existingTag);
                    }
                }

                _labelToTag[label] = tag;
                _tagToLabel[tag] = label;

                if (mappingChanged)
                {
                    Console.WriteLine($"WorkerLabelService: Label set {label} -> {tag}");
                    LabelChanged?.Invoke(this, new WorkerLabelChangedEventArgs(tag, label));
                }
            }
        }

        public string? GetLabel(ulong tag)
        {
            lock (_lock)
            {
                if (_tagToLabel.TryGetValue(tag, out var label))
                {
                    return label;
                }
                return null;
            }
        }

        public ulong? GetTag(string label)
        {
            lock (_lock)
            {
                if (_labelToTag.TryGetValue(label, out var tag))
                {
                    return tag;
                }
                return null;
            }
        }

        public void RemoveLabel(string label)
        {
            lock (_lock)
            {
                if (_labelToTag.TryGetValue(label, out var tag))
                {
                    _labelToTag.Remove(label);
                    _tagToLabel.Remove(tag);
                    Console.WriteLine($"WorkerLabelService: Label removed {label} (tag {tag})");
                }
            }
        }

        public void RemoveLabelByTag(ulong tag)
        {
            lock (_lock)
            {
                if (_tagToLabel.TryGetValue(tag, out var label))
                {
                    _tagToLabel.Remove(tag);
                    _labelToTag.Remove(label);
                    Console.WriteLine($"WorkerLabelService: Label removed by tag {tag} (label {label})");
                }
            }
        }

        public IReadOnlyDictionary<string, ulong> GetAllLabels()
        {
            lock (_lock)
            {
                return new Dictionary<string, ulong>(_labelToTag);
            }
        }

        public Request? BuildDebugRequest(List<Unit>? rawUnits) => null;
        public string AssignLabelIfMissing(uint unitType, ulong tag) => "";
        public void AssignD1ToD12ForVFormation(IEnumerable<Unit>? workers) { }
        public event EventHandler<WorkerLabelChangedEventArgs>? LabelChanged;
    }

    public class CrosshairService
    {
        private ProtobufProxy? _proxy;
        private MawBaseLocationData? _baseLocationData;
        private List<Unit> _rawUnits = new List<Unit>();
        private Dictionary<string, COMData> _comRegistry = new Dictionary<string, COMData>();

        public class COMData
        {
            public Point Position { get; set; } = new Point();
            public string Label { get; set; } = string.Empty;
            public Color Color { get; set; } = new Color();
        }

        public CrosshairService() { }

        public void Initialize(ProtobufProxy proxy, Func<ulong, Unit?> getUnitByTag) { _proxy = proxy; }

        public void SetBaseLocationData(MawBaseLocationData? baseLocationData) { _baseLocationData = baseLocationData; }

        public void UpdateRawUnits(List<Unit>? rawUnits) 
        {
            if (rawUnits != null)
            {
                _rawUnits = new List<Unit>(rawUnits);
            }
        }

        public void SetCOM(Point position, string label, Color color)
        {
            if (position == null || label == null)
            {
                Console.WriteLine("CrosshairService.SetCOM: Invalid position or label");
                return;
            }

            var comData = new COMData
            {
                Position = position,
                Label = label,
                Color = color
            };

            _comRegistry[label] = comData;
            Console.WriteLine($"CrosshairService: Registered COM '{label}' at ({position.X:F2},{position.Y:F2})");
        }

        public Dictionary<string, COMData> GetAllCOMs()
        {
            return new Dictionary<string, COMData>(_comRegistry);
        }

        public void ClearCOMs()
        {
            _comRegistry.Clear();
        }

        public Request? BuildDebugRequest(List<Unit>? rawUnits) => null;
    }

    public class MineralLabelService
    {
        public class MineralLabelData
        {
            public Point Position { get; set; } = new Point();
            public string Label { get; set; } = string.Empty;
            public Color Color { get; set; } = new Color();
            public ulong Tag { get; set; }
        }

        private Dictionary<string, MineralLabelData> _mineralLabels = new Dictionary<string, MineralLabelData>();
        private readonly object _lock = new();

        public MineralLabelService() { }

        public void Initialize(ProtobufProxy proxy, Func<ulong, Unit?> getUnitByTag) { }

        public void UpdateRawUnits(List<Unit>? rawUnits) { }

        public void SetMineralLabel(string label, Point position, Color color, ulong tag = 0)
        {
            if (position == null || label == null)
            {
                Console.WriteLine("MineralLabelService.SetMineralLabel: Invalid position or label");
                return;
            }

            lock (_lock)
            {
                bool changed = false;
                if (!_mineralLabels.TryGetValue(label, out var existing))
                {
                    changed = true;
                }
                else if (existing.Tag != tag || existing.Position.X != position.X || existing.Position.Y != position.Y)
                {
                    changed = true;
                }

                if (changed)
                {
                    var mineralData = new MineralLabelData
                    {
                        Position = position,
                        Color = color,
                        Label = label,
                        Tag = tag
                    };

                    _mineralLabels[label] = mineralData;
                    Console.WriteLine($"MineralLabelService: Registered mineral label '{label}' at ({position.X:F2},{position.Y:F2}) tag={tag}.");
                }
            }

        }

        public Dictionary<string, MineralLabelData> GetAllMineralLabels()
        {
            lock (_lock)
            {
                return new Dictionary<string, MineralLabelData>(_mineralLabels);
            }
        }

        public void ClearMineralLabels()
        {
            lock (_lock)
            {
                _mineralLabels.Clear();
            }
        }

        public Request? BuildDebugRequest(List<Unit>? rawUnits) => null;
    }

    public class VespeneLabelService
    {
        public class VespeneLabelData
        {
            public Point Position { get; set; } = new Point();
            public string Label { get; set; } = string.Empty;
            public Color Color { get; set; } = new Color();
        }

        private Dictionary<string, VespeneLabelData> _vespeneLabels = new Dictionary<string, VespeneLabelData>();
        private readonly object _lock = new();

        public VespeneLabelService() { }

        public void Initialize(ProtobufProxy proxy, Func<ulong, Unit?> getUnitByTag) { }

        public void UpdateRawUnits(List<Unit>? rawUnits) { }

        public void SetVespeneLabel(string label, Point position, Color color)
        {
            if (position == null || label == null)
            {
                Console.WriteLine("VespeneLabelService.SetVespeneLabel: Invalid position or label");
                return;
            }

            lock (_lock)
            {
                bool changed = false;
                if (!_vespeneLabels.TryGetValue(label, out var existing))
                {
                    changed = true;
                }
                else if (existing.Position.X != position.X || existing.Position.Y != position.Y)
                {
                    changed = true;
                }

                if (changed)
                {
                    var vespeneData = new VespeneLabelData
                    {
                        Position = position,
                        Label = label,
                        Color = color
                    };

                    _vespeneLabels[label] = vespeneData;
                    Console.WriteLine($"VespeneLabelService: Registered vespene label '{label}' at ({position.X:F2},{position.Y:F2})");
                }
            }
        }

        public Dictionary<string, VespeneLabelData> GetAllVespeneLabels()
        {
            lock (_lock)
            {
                return new Dictionary<string, VespeneLabelData>(_vespeneLabels);
            }
        }

        public void ClearVespeneLabels()
        {
            lock (_lock)
            {
                _vespeneLabels.Clear();
            }
        }

        public Request? BuildDebugRequest(List<Unit>? rawUnits) => null;
    }

    [MemoryPackable]
    public partial class MineralNode
    {
        public Vector2Dto Position { get; set; } = new Vector2Dto();
        public ulong MineralUnitTag { get; set; }
        public float AngleFromCenter { get; set; }
        public bool IsLargeMineral { get; set; }
        public string Identifier { get; set; } = string.Empty;
        public bool IsNearMineral { get; set; }
        public float DistanceFromTownHall { get; set; }

        [MemoryPackConstructor]
        public MineralNode() { }
    }

    [MemoryPackable]
    public partial class MiningTeam
    {
        public string TeamId { get; set; } = string.Empty;
        public List<ulong> WorkerTags { get; set; } = new List<ulong>();
        public List<WorkerEntryDto> Workers { get; set; } = new List<WorkerEntryDto>();
        public MineralNode? MineralA { get; set; }
        public MineralNode? MineralB { get; set; }
        public Dictionary<ulong, int> WorkerLastMineFrame { get; set; } = new Dictionary<ulong, int>();
        public Dictionary<ulong, bool> WorkerLastMinedA { get; set; } = new Dictionary<ulong, bool>();
        public bool IsJITTeam { get; set; } = false;
        public Vector2Dto ExpansionPosition { get; set; } = new Vector2Dto();
        public Vector2Dto JitWaitPoint { get; set; } = new Vector2Dto();
        public int TeamIndex { get; set; }

        [MemoryPackConstructor]
        public MiningTeam() { }
    }
}
