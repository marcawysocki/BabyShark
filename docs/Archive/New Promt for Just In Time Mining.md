# New prompt for Just In Time Mining.MD

## 🧠 Overview

The game Starts with either 12 or 8 workers. The following applies when an Expansion or Start Location has 3 workers per Mineral Node. I would like to add Just In Time Mining to BabySharkMiningManager.cs This Starcraft 2 AI  BOT cloned from https://github.com/sharknice/Sharky

Assume 12 workers, 8 mineral nodes (4 Near, 4 Far), 2 vespine geysers. 
see below C:\Users\marca\source\repos\BabyShark\BabySharkBot\Managers\BabySharkMiningManager.cs
This was restored from the Backup after you deleted the original. Do not delete any existing code.

The Mining manager C:\Users\marca\source\repos\BabyShark\BabySharkBot\Managers\BabySharkMiningManager.cs Should be were the logic for Just In Time Mining is implemented. The Mining manager should be able to handle both Speed Mining and Just In Time Mining based on the number of workers assigned to a mineral node.

Currently,

We have teams assignments for the workers on the start location. So we keep that for the starting assignment. When the worker returns cargo, It does not go straight back to the town center, it takes the shortest path going from one mineral, returning cargo and then switching to the other Mineral. While speed mining returns cargo and then mines the same mineral, JIT alternates between two minerals.

Initially, the first series of commands will not differ from Historical Speed Mining.
For Example team Teal  Minerals TA/TB, workers T1, T2, and T3, T1 will start on frame zero as the closest to Near Mineral 1 TA. Once mining Starts T1 will mine TA, T3 will mine TB and T3 will wait turns to mine TA. T1 will return cargo, and then move to TB for a turn to mine the alternating Mineral. T2 returns cargo and moves to TA.  T3 will return cargo and also alternate to TB.  The pattern is the same for all teams.

For each expansion, we need to have equivalent teams. So we must have a way of determining it's M1 thru M8, if it has 8 mineral nodes. We know the mineral center for each expansion, and can calculate the nearest minerals Or large minerals   for the "A" Minerals for  the townhall Expansion location. We can determine a clockwise Location of the minerals. From the mineral center, half the minerals should be clockwise And the other half counter-clockwise So if we make M1 the most counter-clockwise from mineral center and M8 the most clockwise from the mineral center and then we have M1 through M8 for 8 mineral clusters On an expansion.

When we have 3 workers on a team we use JIT mining, any other number uses Speed mining. ExaMPLE, we create an expansion. As we create new workers   The first new worker minds the nearest mineral using speed mining, The second Worker mines the next nearest mineral, 3 & 4 mine Large Minerals. 5 thru 8 mines far minerals  Starting with New worker number 9 we have enough to form one JIT team.

## 🛠 Implementation Plan
1. Create MineralNode class with position data and sorting logic
see below C:\Users\marca\source\repos\BabyShark\BabySharkBot\Setup\BaseDtos.cs

2. Implement TeamManager for dynamic team creation (3-worker JIT teams)
see Below C:\Users\marca\source\repos\BabyShark\BabySharkBot\MicroTasks\TeamPatchMiningTask.cs

3. Modify WorkerAssigner to use alternating mineral strategy when applicable
4. Add expansion-specific mineral ordering based on clockwise/counter-clockwise calculations

Analize the following and integrate with BabySharkMiningManager.cs
using SC2APIProtocol;
using Sharky;
using Sharky.Managers;
using BabySharkBot.Setup;
using BabySharkBot.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SC2Action = SC2APIProtocol.Action;

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Custom mining manager for BabyShark that replaces Sharky's default mining manager.
    /// Handles map data initialization, worker mining coordination, and custom debug drawing.
    /// Provides visualization for:
    /// - Worker labels with names, roles, and targets
    /// - Center of mass (minerals and vespene clusters)
    /// - Worker instructions (arrows/lines showing where workers are headed)
    /// </summary>
    public class BabySharkMiningManager : IManager
    {
        public bool NeverSkip { get; set; } = false;
        public bool SkipFrame { get; set; } = false;
        public double LongestFrame { get; set; } = 0;
        public double TotalFrameTime { get; set; } = 0;

        private InitialMapData _initialMapData;
        private SecondaryMapData _secondaryMapData;
        private OngoingMapData _ongoingMapData;
        private bool _initialMiningManeuvers = true;
        private int _openingFrame = -1;
        private WorkerLabelService _workerLabelService;
        private CrosshairService _crosshairService;
        private MineralLabelService _mineralLabelService;
        private VespeneLabelService _vespeneLabelService;
        private ExpansionCOMService _expansionCOMService;
        private ExpansionPointService _expansionPointService;
        private ExpansionPointDrawService _expansionPointDrawService;
        private ProvisionalExpansionService _provisionalExpansionService;
        private MineralReturnRateTrackerService _mineralReturnRateTrackerService;
        private FrameToTimeConverter _frameToTimeConverter;
        private SpawningPoolPlacementService _spawningPoolPlacementService;
        private Sharky.Pathing.MapDataService _mapDataService;
        private MawBaseLocationData _mapData;  // Store loaded map data for visualization
        private readonly chrisCrossAppleSause _ccaMiningService = new chrisCrossAppleSause();
        private int _lastMineralReturnRateConsoleFrame = -999999;
        private bool _printedTwelveDroneMilestone = false;
        private bool _pausedAfterWorkerInstructions = false;
        private int _workerInstructionDrawCount = 0;
        private bool _spawnLabelDebugBreakTriggered = false;
        private Dictionary<Point2D, List<MineralNode>> _expansionMinerals = new Dictionary<Point2D, List<MineralNode>>();
        private Dictionary<Point2D, List<MiningTeam>> _expansionTeams = new Dictionary<Point2D, List<MiningTeam>>();

        // JIT Mining related classes and structures
        public class MineralNode
        {
            public Point2D Position { get; set; }
            public Unit MineralUnit { get; set; }
            public float AngleFromCenter { get; set; }
            public bool IsLargeMineral { get; set; }
            public string Identifier { get; set; } // M1, M2, etc.
        }

        public class MiningTeam
        {
            public string TeamId { get; set; }
            public List<Unit> Workers { get; set; } = new List<Unit>();
            public MineralNode MineralA { get; set; }
            public MineralNode MineralB { get; set; }
            public Dictionary<ulong, int> WorkerLastMineFrame { get; set; } = new Dictionary<ulong, int>();
            public bool IsJITTeam { get; set; } = false;
        }

        public BabySharkMiningManager(InitialMapData initialMapData, SecondaryMapData secondaryMapData, OngoingMapData ongoingMapData,
            WorkerLabelService workerLabelService, CrosshairService crosshairService, MineralLabelService mineralLabelService,
            VespeneLabelService vespeneLabelService, ExpansionCOMService expansionCOMService, ExpansionPointService expansionPointService,
            ExpansionPointDrawService expansionPointDrawService, ProvisionalExpansionService provisionalExpansionService,
            MineralReturnRateTrackerService mineralReturnRateTrackerService, FrameToTimeConverter frameToTimeConverter,
            SpawningPoolPlacementService spawningPoolPlacementService, Sharky.Pathing.MapDataService mapDataService,
            MawBaseLocationData mapData)
        {
            _initialMapData = initialMapData;
            _secondaryMapData = secondaryMapData;
            _ongoingMapData = ongoingMapData;
            _workerLabelService = workerLabelService;
            _crosshairService = crosshairService;
            _mineralLabelService = mineralLabelService;
            _vespeneLabelService = vespeneLabelService;
            _expansionCOMService = expansionCOMService;
            _expansionPointService = expansionPointService;
            _expansionPointDrawService = expansionPointDrawService;
            _provisionalExpansionService = provisionalExpansionService;
            _mineralReturnRateTrackerService = mineralReturnRateTrackerService;
            _frameToTimeConverter = frameToTimeConverter;
            _spawningPoolPlacementService = spawningPoolPlacementService;
            _mapDataService = mapDataService;
            _mapData = mapData;
        }

        public void InitializeMiningTeams(Point2D expansionPosition, List<Unit> minerals)
        {
            var expansionKey = new Point2D { X = expansionPosition.X, Y = expansionPosition.Y };
            
            // Create mineral nodes with proper ordering
            var mineralNodes = CreateOrderedMineralNodes(expansionPosition, minerals);
            _expansionMinerals[expansionKey] = mineralNodes;

            // Create teams based on mineral pairs
            var teams = new List<MiningTeam>();
            
            // Pair up minerals (M1-M2, M3-M4, etc.)
            for (int i = 0; i < mineralNodes.Count - 1; i += 2)
            {
                var team = new MiningTeam
                {
                    TeamId = $"M{i/2 + 1}",
                    MineralA = mineralNodes[i],
                    MineralB = mineralNodes[i + 1],
                    IsJITTeam = false // Will be set to true when 3 workers are assigned
                };
                teams.Add(team);
            }
            
            _expansionTeams[expansionKey] = teams;
        }

        private List<MineralNode> CreateOrderedMineralNodes(Point2D expansionPosition, List<Unit> minerals)
        {
            var mineralNodes = new List<MineralNode>();
            
            foreach (var mineral in minerals)
            {
                var node = new MineralNode
                {
                    Position = mineral.Pos,
                    MineralUnit = mineral,
                    IsLargeMineral = mineral.UnitType == UnitTypes.PROTOSS_ASSIMILATOR_RICH || 
                                   mineral.UnitType == UnitTypes.TERRAN_REFINERY_RICH || 
                                   mineral.UnitType == UnitTypes.ZERG_EXTRACTOR_RICH ||
                                   mineral.UnitType.ToString().Contains("RICH"),
                    AngleFromCenter = CalculateAngleFromCenter(expansionPosition, mineral.Pos)
                };
                mineralNodes.Add(node);
            }

            // Sort by angle to get clockwise ordering
            mineralNodes.Sort((a, b) => a.AngleFromCenter.CompareTo(b.AngleFromCenter));
            
            // Assign identifiers M1, M2, etc.
            for (int i = 0; i < mineralNodes.Count; i++)
            {
                mineralNodes[i].Identifier = $"M{i + 1}";
            }
            
            return mineralNodes;
        }

        private float CalculateAngleFromCenter(Point2D center, Point2D point)
        {
            float deltaX = point.X - center.X;
            float deltaY = point.Y - center.Y;
            return (float)Math.Atan2(deltaY, deltaX);
        }

        public void AssignWorkerToTeam(Unit worker, Point2D expansionPosition)
        {
            var expansionKey = new Point2D { X = expansionPosition.X, Y = expansionPosition.Y };
            
            if (!_expansionTeams.ContainsKey(expansionKey))
                return;

            var teams = _expansionTeams[expansionKey];
            if (teams.Count == 0)
                return;

            // Find the team with the fewest workers
            var targetTeam = teams.OrderBy(t => t.Workers.Count).First();
            targetTeam.Workers.Add(worker);

            // Check if this team now has 3 workers and should switch to JIT mining
            if (targetTeam.Workers.Count == 3)
            {
                targetTeam.IsJITTeam = true;
            }
        }

        public Point2D GetNextMiningTarget(Unit worker, Point2D expansionPosition, Point2D currentMineralPosition, int currentFrame)
        {
            var expansionKey = new Point2D { X = expansionPosition.X, Y = expansionPosition.Y };
            
            if (!_expansionTeams.ContainsKey(expansionKey))
                return currentMineralPosition; // Fallback to current behavior

            // Find which team this worker belongs to
            var team = FindWorkerTeam(worker, expansionKey);
            if (team == null || !team.IsJITTeam)
                return currentMineralPosition; // Use speed mining for non-JIT teams

            // For JIT teams, alternate between MineralA and MineralB
            var lastMineFrame = team.WorkerLastMineFrame.ContainsKey(worker.Tag) ? 
                team.WorkerLastMineFrame[worker.Tag] : 0;

            // Determine which mineral to mine next based on alternation pattern
            bool shouldMineA = (currentFrame / 10 + team.Workers.IndexOf(worker)) % 2 == 0;
            
            if (shouldMineA)
            {
                team.WorkerLastMineFrame[worker.Tag] = currentFrame;
                return team.MineralA.Position;
            }
            else
            {
                team.WorkerLastMineFrame[worker.Tag] = currentFrame;
                return team.MineralB.Position;
            }
        }

        private MiningTeam FindWorkerTeam(Unit worker, Point2D expansionKey)
        {
            if (!_expansionTeams.ContainsKey(expansionKey))
                return null;

            foreach (var team in _expansionTeams[expansionKey])
            {
                if (team.Workers.Any(w => w.Tag == worker.Tag))
                    return team;
            }
            return null;
        }

        public bool IsJITTeamWorker(Unit worker, Point2D expansionPosition)
        {
            var expansionKey = new Point2D { X = expansionPosition.X, Y = expansionPosition.Y };
            var team = FindWorkerTeam(worker, expansionKey);
            return team != null && team.IsJITTeam;
        }

        // Existing methods would continue here...
        
        public void OnStart()
        {
            // Initialize mining for start location
            // This would call InitializeMiningTeams for the main base
        }

        public void OnFrame()
        {
            // Main frame logic would be here
            // This is where you'd integrate the JIT mining logic
        }

        // ... rest of existing methods
    }
}
