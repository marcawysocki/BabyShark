# Harvest and Return Cargo Vectors & Points - Phase 1 Foundation

## Overview
This document provides the **foundation calculations for Phase 1: Just-In-Time Mining**. These are the Harvest/Return Cargo calculations needed to establish the **3 workers per 2 mineral nodes saturation pattern** at game start.

**What this enables**:
- Workers reach minerals with zero delays
- HarvestPoint prevents worker collisions
- DropOffPoint ensures consistent return paths
- Foundation for Phase 2+ choreography

---

## Why This Matters for Phase 1

At game start (Frame 0):
```
12 workers must reach 8 minerals and establish 3:2 pattern

Without Harvest/Return Cargo calculations:
  - Workers pile up at mineral patch
  - Multiple workers try to mine from same spot
  - Result: Idle workers, collision delays

With Harvest/Return Cargo calculations:
  - Each mineral has designated HarvestPoints for 3 workers
  - Workers arrive at predetermined spots
  - Result: 3 workers can efficiently mine simultaneously
```

---

## Found References

### 1. **Sharky MiningInfo.cs** - Core Calculation Engine
**Location**: `Sharky/MiningInfo.cs` (already in your codebase)

This class calculates **HarvestPoint** and **DropOffPoint** for each mineral/gas:

```csharp
public class MiningInfo
{
    public MiningInfo(Unit resourceUnit, Point baseLocation)
    {
        ResourceUnit = resourceUnit;
        Workers = new List<UnitCommander>();

        // Calculate DROPOFF point (worker returns cargo here)
        var baseVector = new Vector2(baseLocation.X, baseLocation.Y);
        var mineralVector = new Vector2(ResourceUnit.Pos.X, ResourceUnit.Pos.Y);

        var angle = Math.Atan2(mineralVector.Y - baseVector.Y, baseVector.X - mineralVector.X);
        DropOffPoint = new Point2D 
        { 
            X = baseVector.X + (float)(-2 * Math.Cos(angle)), 
            Y = baseVector.Y - (float)(-2 * Math.Sin(angle)) 
        };

        // Calculate HARVEST point (worker mines from here)
        var mineAngle = Math.Atan2(baseVector.Y - mineralVector.Y, mineralVector.X - baseVector.X);
        HarvestPoint = new Point2D 
        { 
            X = mineralVector.X + (float)(-.5 * Math.Cos(mineAngle)), 
            Y = mineralVector.Y - (float)(-.5 * Math.Sin(mineAngle)) 
        };
    }

    public List<UnitCommander> Workers { get; set; }
    public Unit ResourceUnit { get; set; }
    public Point2D DropOffPoint { get; set; }      // Return cargo destination
    public Point2D HarvestPoint { get; set; }      // Gather mineral source
}
```

**Key Formulas**:
- **DropOffPoint**: Positioned 2 units away from base, along vector pointing from base toward mineral
- **HarvestPoint**: Positioned 0.5 units away from mineral, along vector pointing from mineral toward base

---

### 2. **Speed Mining Reference - Approach Vectors**
**Found in**: `PupusPistrixVectatorPestium/speed_mining.md` (lines 490-514)

This reference defines **approach vectors** for worker positioning optimization:

```json
{
  "approachVectors": {
    "A": [1, 0],      // Approach from East
    "B": [1, 0],
    "C": [0, 1],      // Approach from South
    "D": [0, 1],
    "E": [-1, 0],     // Approach from West
    "F": [-1, 0],
    "G": [0, -1],     // Approach from North
    "H": [0, -1],
    "I": [1, 1],      // Approach diagonally
    "J": [-1, 1],
    "K": [1, -1],
    "L": [-1, -1]
  },
  "dropOffPoints": {
    "minerals": [...],
    "gas": [...]
  }
}
```

**Purpose**: Pre-computed vectors to orient workers for optimal approach angles to mineral patches and gas geysers.

---

### 3. **Worker Label System Reference - Data Structure**
**Found in**: `PupusPistrixVectatorPestium/WorkerLabel.MD` (lines 336-337)

The MapSpeedMiningData structure includes storage for approach and dropoff data:

```csharp
var start = new StartLocationData
{
    StartId = 0,
    Anchor = new double[] { anchorX, anchorY },
    Labels = workerLabels,
    Assignments = new Dictionary<string, AssignmentEntry>(),
    ApproachVectors = new Dictionary<string, float[]>(),      // Per-mineral vectors
    DropOffPoints = new Dictionary<string, float[]>()         // Per-mineral points
};
```

---

## Implementation Pattern

### Use Case: Worker Assignment to Minerals

```csharp
// 1. Get MiningInfo from Sharky (contains HarvestPoint and DropOffPoint)
var miningInfo = new MiningInfo(mineralUnit, baseLocation);

// 2. Command worker to harvest point
worker.Order(frame, Abilities.GATHER, miningInfo.HarvestPoint);

// 3. Worker will automatically return cargo to nearest resource center
// (Sharky handles the return path)

// 4. For optimization, pre-position workers using approach vectors
// var approachVector = GetApproachVector(mineralIndex);  // A-L
// worker.Order(frame, Abilities.MOVE, ComputeApproachPoint(miningInfo.HarvestPoint, approachVector));
```

---

## Key Integration Points

### In Your BabySharkBot:
- [ ] `MiningInfo` is already used by Sharky - leverage `HarvestPoint` and `DropOffPoint` properties
- [ ] Consider storing `ApproachVectors` in map data for worker pre-positioning
- [ ] Store `DropOffPoints` per base location for predictive worker return pathing
- [ ] Reference `speed_mining.md` for vector-based worker orientation optimization

### Map Data Storage:
- Add `ApproachVectors: Dictionary<string, float[]>` to `MapDataSnapshot`
- Add `DropOffPoints: Dictionary<string, float[]>` to `MapDataSnapshot`
- Load at map initialization time (Frame 0)

---

## Related Files

### Reference Documentation:
- `PupusPistrixVectatorPestium/speed_mining.md` - Full speed mining theory
- `PupusPistrixVectatorPestium/WorkerLabel.MD` - Worker label data structures
- `PupusPistrixVectatorPestium/Frame Zero.MD` - Frame 0 optimization patterns

### Implementation Files:
- `Sharky/MiningInfo.cs` - Core calculation (already available)
- `Sharky/MicroTasks/Mining/MineralMiner.cs` - Uses MiningInfo for worker commands
- `Sharky/MicroControllers/MineralWalker.cs` - Handles mineral walking logic

---

## Next Steps
- [ ] Review actual usage in `MineralMiner.cs` to see HarvestPoint/DropOffPoint consumption
- [ ] Consider pre-positioning workers using approach vectors on Frame 1
- [ ] Add telemetry logging for worker return times (optimization metric)
- [ ] Store learned approach vectors back to map data after successful runs
