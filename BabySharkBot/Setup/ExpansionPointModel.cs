using System;
using System.Collections.Generic;
using BabySharkBot.Setup;
using SC2APIProtocol;
using MemoryPack;

#nullable enable

namespace BabySharkBot.Setup
{
    /// <summary>
    /// Status of an expansion candidate while it is being scouted or verified.
    /// </summary>
    public enum ExpansionPointStatus
    {
        Provisional = 0,
        VerifiedExpansion = 1,
        SuspectedWall = 2,
        VerifiedWall = 3,
        Rejected = 4
    }

    /// <summary>
    /// Represents a single townhall placement option for an expansion.
    /// May be one of multiple placements for contested bases.
    /// </summary>
    [MemoryPackable]
    public partial class TownhallPlacementOption
    {
        /// <summary>
        /// Computed townhall placement point (the "Nose" facing minerals)
        /// </summary>
        public Vector2Dto Point { get; set; } = new Vector2Dto();

        /// <summary>
        /// True if this placement passed placement_grid validation
        /// </summary>
        public bool IsValid { get; set; } = false;

        /// <summary>
        /// Distance from this placement to mineral cluster center
        /// </summary>
        public float DistanceToCluster { get; set; } = 0f;

        /// <summary>
        /// Distance to nearest of the two central mineral nodes.
        /// Used to detect contested bases (if > 0.25f, likely contested)
        /// </summary>
        public float DistanceToCentralNodes { get; set; } = 0f;

        /// <summary>
        /// Which start location this placement favors (0, 1, or -1 if neutral/contested)
        /// </summary>
        public int FavoredStartLocation { get; set; } = -1;

        /// <summary>
        /// Notes on why this placement was computed or rejected
        /// </summary>
        public string ValidationNotes { get; set; } = "";

        [MemoryPackConstructor]
        public TownhallPlacementOption() { }
    }

    /// <summary>
    /// Represents computed townhall placement data for a single expansion cluster.
    /// Contains mineral positions, geyser positions, cluster center, and the computed ideal expansion point.
    /// May contain 1 placement (standard) or 2 placements (contested base).
    /// Follows the same MemoryPack serialization pattern as OrderedMineral and OrderedVespene.
    /// </summary>
    [MemoryPackable]
    public partial class ExpansionPointModel
    {
        /// <summary>
        /// Expansion cluster label (E1, E2, E3, etc.)
        /// Corresponds to ExpansionCOMService index
        /// </summary>
        public int ExpansionIndex { get; set; }

        /// <summary>
        /// Pre-computed mineral cluster center (the "smile")
        /// Used as reference point for townhall positioning logic
        /// </summary>
        public Vector2Dto MineralClusterCenter { get; set; } = new Vector2Dto();

        /// <summary>
        /// Mineral positions in this cluster.
        /// Used for clearance validation and visualization
        /// </summary>
        public List<Vector2Dto> MineralPositions { get; set; } = new List<Vector2Dto>();

        /// <summary>
        /// Geyser positions in this expansion.
        /// Used for clearance validation and visualization
        /// </summary>
        public List<Vector2Dto> GeyserPositions { get; set; } = new List<Vector2Dto>();

        /// <summary>
        /// Computed ideal expansion townhall placement point.
        /// Positioned between mineral cluster and geysers, validated against placement_grid
        /// </summary>
        public Vector2Dto ExpansionPoint { get; set; } = new Vector2Dto();

        /// <summary>
        /// All townhall placement options for this expansion (1 for normal, 2 for contested)
        /// </summary>
        public List<TownhallPlacementOption> PlacementOptions { get; set; } = new List<TownhallPlacementOption>();

        /// <summary>
        /// True if this is a contested base (can be placed multiple ways depending on who takes it).
        /// Detected when calculated TC placement is not more than 0.25f closer than both central nodes.
        /// </summary>
        public bool IsContested { get; set; } = false;

        /// <summary>
        /// Flag indicating if the expansion point is valid (buildable on placement_grid).
        /// False if spiral search was needed; True if ideal point or adjusted point is valid.
        /// </summary>
        public bool IsValid { get; set; } = false;

        /// <summary>
        /// Reason for validation failure or notes on placement decision.
        /// Used for debugging visualization and logging
        /// </summary>
        public string ValidationNotes { get; set; } = "";

        /// <summary>
        /// Distance from ideal expansion point to mineral cluster center.
        /// Helps verify that placement respects Blizzard's standard 3.75 tile offset
        /// </summary>
        public float DistanceToCluster { get; set; } = 0f;

        /// <summary>
        /// Number of spiral search iterations needed to find valid point.
        /// 0 = ideal point was valid, 1+ = spiral search was required
        /// </summary>
        public int SpiralSearchIterations { get; set; } = 0;

        /// <summary>
        /// Current verification state of the expansion candidate.
        /// </summary>
        public ExpansionPointStatus Status { get; set; } = ExpansionPointStatus.Provisional;

        /// <summary>
        /// Notes from scouting or verification.
        /// </summary>
        public string VerificationNotes { get; set; } = "";

        [MemoryPackConstructor]
        public ExpansionPointModel() { }

        public ExpansionPointModel(int expansionIndex, Vector2Dto mineralClusterCenter)
        {
            ExpansionIndex = expansionIndex;
            MineralClusterCenter = mineralClusterCenter;
        }
    }
}
