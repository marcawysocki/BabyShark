# Mineral Label Drawing System

## Overview
Created a complete mineral label drawing system that visualizes F1-F4 (far minerals) and N1-N4 (near minerals) labels directly on the mineral patches in the StarCraft II game client.

## Architecture

### 1. **MineralLabelService** (BaseDtos.cs)
Service class that stores and manages mineral label data for visualization.

**Key Features:**
- Stores mineral position, label text, and color
- Thread-safe dictionary for label storage
- Simple API: `SetMineralLabel()`, `GetAllMineralLabels()`, `ClearMineralLabels()`

**API Methods:**
```csharp
public void SetMineralLabel(string label, Point position, Color color)
public Dictionary<string, MineralLabelData> GetAllMineralLabels()
public void ClearMineralLabels()
```

### 2. **Integration Points**

#### BabySharkBot.cs (Service Instantiation)
```csharp
var mineralLabelService = new MineralLabelService();
var miningManager = new BabySharkMiningManager(
    workerLabelService, 
    crosshairService, 
    mineralLabelService
);
```

#### BabySharkMiningManager.cs (Drawing)
- Added `_mineralLabelService` field
- Updated constructor to accept `MineralLabelService`
- Added `DrawMineralLabels()` method called every frame
- Uses `ManagerDebugService.DrawText()` to render labels

#### InitialMapData.cs (Label Registration)
- Updated `GetNewMiningData()` to accept `MineralLabelService` parameter
- Added `RegisterMineralLabels()` method to convert OrderedMainMinerals to F1-F4, N1-N4 labels
- Called after greedy mineral ordering calculation

## Label Naming Convention

### The Pumpkin Analogy: Understanding Spatial Relationships

```
                    Townhall (Nose) ← REFERENCE POINT
                         👃
                          |
            W1 W2 W3 (Mustache)
             👨👨👨
                          |
        M8 M7 M6 M5  M4 M3 M2 M1 (Smile/Teeth)
        🦷🦷🦷🦷  🦷🦷🦷🦷
```

**Key Concept**: Near/Far classification is based on **distance from mineral back to Starting Townhall** (where workers return cargo), NOT distance to Center of Mineral Mass (COM).

### Near Minerals (N1-N4)
- **Condition**: Distance to `StartingTownhall[0]` **<** average townhall distance
- **Meaning**: Shorter travel distance to return cargo → Higher mineral per minute (MPM) → **Higher Priority**
- **Label Format**: `N1`, `N2`, `N3`, `N4`

### Far Minerals (F1-F4)
- **Condition**: Distance to `StartingTownhall[0]` **>** average townhall distance
- **Meaning**: Longer travel distance to return cargo → Lower mineral per minute (MPM) → **Secondary Priority**
- **Label Format**: `F1`, `F2`, `F3`, `F4`
- **Color**: Magenta (RGB: 255, 0, 255)
- **Z-Coordinate**: 12.0 (above terrain for visibility)

### Near Minerals (N1-N4)
- **Condition**: `IsNear = true` (distance from COM <= average distance)
- **Label Format**: `N1`, `N2`, `N3`, `N4`
- **Color**: Cyan (RGB: 0, 255, 255)
- **Z-Coordinate**: 12.0 (above terrain for visibility)

## Data Flow

```
OrderedMainMinerals (List<List<OrderedMineral>>)
    ├─ [0] = Start Location 0 minerals (M[8-1] with IsNear flag)
    ├─ [1] = Start Location 1 minerals (M[8-1] with IsNear flag)
    └─ ...
            │
            ▼ (RegisterMineralLabels)
            │
    MineralLabelService._mineralLabels
    ├─ "F1" → Position, Color (magenta)
    ├─ "F2" → Position, Color (magenta)
    ├─ "N1" → Position, Color (cyan)
    ├─ "N2" → Position, Color (cyan)
    └─ ...
            │
            ▼ (OnFrame / DrawMineralLabels)
            │
    Game Client (SC2APIProtocol Debug Drawing)
    ├─ Text "F1" at mineral position
    ├─ Text "F2" at mineral position
    ├─ Text "N1" at mineral position
    ├─ Text "N2" at mineral position
    └─ ...
```

## Usage in Code

### Registering Minerals (InitialMapData.cs)
```csharp
// After greedy ordering is complete
if (mineralLabelService != null)
{
    RegisterMineralLabels(orderedMainMinerals, mineralLabelService);
}
```

### Drawing Minerals (BabySharkMiningManager.cs)
```csharp
public IEnumerable<SC2Action> OnFrame(ResponseObservation observation)
{
    // ... other drawing ...
    DrawMineralLabels();  // Called every frame
    // ... other drawing ...
}
```

## Console Output Example

```
InitialMapData.RegisterMineralLabels: Start[0] M[8] = F1 at (12.50,45.75)
InitialMapData.RegisterMineralLabels: Start[0] M[7] = F2 at (18.30,48.20)
InitialMapData.RegisterMineralLabels: Start[0] M[6] = F3 at (22.10,42.50)
InitialMapData.RegisterMineralLabels: Start[0] M[5] = F4 at (28.75,46.80)
InitialMapData.RegisterMineralLabels: Start[0] M[4] = N1 at (15.20,38.90)
InitialMapData.RegisterMineralLabels: Start[0] M[3] = N2 at (20.50,35.60)
InitialMapData.RegisterMineralLabels: Start[0] M[2] = N3 at (25.80,40.20)
InitialMapData.RegisterMineralLabels: Start[0] M[1] = N4 at (32.10,37.50)
BabySharkMiningManager.DrawMineralLabels: Drawing 8 mineral labels
BabySharkMiningManager.DrawMineralLabels: Drew 'F1' at (12.50,45.75)
BabySharkMiningManager.DrawMineralLabels: Drew 'F2' at (18.30,48.20)
...
```

## Technical Details

### Z-Coordinate Strategy
- All mineral labels use `Z = 12.0f`
- Terrain is approximately Z = 0-2
- Debug visualizations (crosshairs, labels) use Z = 12+ for visibility
- Ensures labels appear above terrain and structures

### Color Scheme
| Category | Label | Color | RGB | Hex |
|----------|-------|-------|-----|-----|
| Far | F1-F4 | Magenta | 255,0,255 | #FF00FF |
| Near | N1-N4 | Cyan | 0,255,255 | #00FFFF |

### Indexing Convention
- Uses descending 8→1 index order (M[8-1])
- Reflects greedy chain order (M[8] = furthest, M[1] = closest to M[2])
- Better for binary serialization and debugging
- F and N labels are re-numbered 1-4 for clarity

## Files Modified

1. **BabySharkBot/Setup/BaseDtos.cs**
   - Added `MineralLabelService` class
   - Added `MineralLabelData` inner class

2. **BabySharkBot/Setup/InitialMapData.cs**
   - Updated `GetNewMiningData()` signature to accept `MineralLabelService`
   - Added `RegisterMineralLabels()` method
   - Calls registration after greedy ordering

3. **BabySharkBot/BabySharkBot.cs**
   - Instantiate `MineralLabelService`
   - Pass to `BabySharkMiningManager`

4. **BabySharkBot/Managers/BabySharkMiningManager.cs**
   - Updated constructor to accept `MineralLabelService`
   - Added `DrawMineralLabels()` method
   - Added call to `DrawMineralLabels()` in `OnFrame()`

## Testing Checklist

- [ ] Build succeeds with no errors
- [ ] Run game with DEBUG mode enabled
- [ ] Verify mineral labels visible on screen
- [ ] F1-F4 labels appear in magenta color
- [ ] N1-N4 labels appear in cyan color
- [ ] Labels positioned at correct mineral locations
- [ ] Console output shows label registration and drawing
- [ ] Multiple start locations show correct labels
- [ ] Labels remain visible throughout game (not flickering)

## Future Enhancements

1. **Worker Assignment Visualization**: Show which worker is assigned to which mineral
   - Draw arrows from workers to assigned minerals
   - Color-code by mineral label (F1, N2, etc.)

2. **Vespene Geyser Labels**: Similar F/N labeling for gas geysers
   - G1-G2 for gas patches at base

3. **Dynamic Label Updates**: Update labels as game state changes
   - Re-assign minerals if workers die or supply changes
   - Reflect actual mining patterns

4. **Opponent Start Visualization**: Mark enemy base locations
   - Red domes at opponent starts
   - Show expected expansion locations

## References

- **DRAWING_PATTERN_GUIDE.md**: Reusable visualization pattern documentation
- **GREEDY_MINERAL_ORDERING.md**: Greedy ordering algorithm documentation
- **BaseDtos.cs**: Service base classes (WorkerLabelService, CrosshairService, MineralLabelService)
- **ManagerDebugService**: Sharky debug drawing wrapper
