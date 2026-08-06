# Mineral Label Visual Reference

## Game Client Display - Pumpkin Analogy

### Base Layout Example (Top-Down View)
```
                    [TOWNHALL - NOSE]
                         👃
                          |
            W1 W2 W3 (WORKERS - MUSTACHE)
             👨👨👨
                          |
        F1 F2 N1 COM N2 N3 F3 F4 (MINERALS - SMILE/TEETH)
         🦷  🦷  🦷   ✚   🦷  🦷  🦷  🦷

Legend:
F (Magenta) = Far minerals (outer ring, longer return distance)
N (Cyan)    = Near minerals (inner ring, shorter return distance)
✚           = Center of Mass (visualization reference only)
👃          = Townhall (actual reference point for distance calculations)
```

## Label Placement Strategy

### CORRECT Distance Calculation (Fixed)
```
                    [TOWNHALL] ← THIS IS THE REFERENCE POINT
                         |
                    Distance measured FROM each mineral
                    BACK TO the townhall

Near Minerals (N1-N4):              Far Minerals (F1-F4):
distance ≤ avgTownhallDist          distance > avgTownhallDist
├─ N1 (shortest)                    ├─ F1 (longest)
├─ N2                               ├─ F2
├─ N3                               ├─ F3
└─ N4 (longest in N group)          └─ F4 (shortest in F group)

Priority:
  Highest: Near minerals (N1-N4) - faster cargo return = faster MPM
  Secondary: Far minerals (F1-F4) - longer cargo return = slower MPM
```

**IMPORTANT**: Distance is measured from mineral position to `StartingTownhall[0]` where workers return cargo, NOT to Center of Mass.

## Rendering Information

### Label Appearance
- **Font**: Default Starcraft II debug text
- **Size**: Readable from game camera angle (12+ units above terrain)
- **Position**: Z = 12.0 (above terrain, structures, units)
- **Updates**: Every frame when debug drawing enabled

### Color Scheme

| Label | Color Name | RGB Value | Hex Code | Purpose |
|-------|-----------|-----------|---------|---------|
| F1-F4 | Magenta   | 255,0,255 | #FF00FF | Far minerals (outer ring) |
| N1-N4 | Cyan      | 0,255,255 | #00FFFF | Near minerals (inner ring) |
| COM   | Yellow    | 255,255,0 | #FFFF00 | Center of mass (crosshair) |

### Z-Coordinate Strategy
```
Z-Level   Visibility           Element
------    ----------           -------
13.0+     Highest priority     (reserved for future)
12.0      Debug drawings       ← F1-F4, N1-N4 labels & crosshairs
2.0-5.0   Structures
0-2.0     Terrain/Ground       ← Units walk here
-1.0      Below terrain        (invisible)
```

## Greedy Chain Order

### Processing Phase
```
Step 1: Identify W1 (First Worker)
        W1 = worker furthest from COM
        W1 Position = (X, Y)

Step 2: Find M[8] (Furthest Mineral)
        M[8] = mineral furthest from W1
        Direction from W1 → M[8]
        
Step 3: Build Greedy Chain (M[7-1])
        M[7] = closest mineral to M[8]
        M[6] = closest mineral to M[7]
        ... continuing inward ...
        M[1] = closest mineral to M[2]
        
Step 4: Classify Near/Far
        avgDist = average distance all minerals to COM
        for each mineral:
            if distance to COM < avgDist:
                IsNear = true  (N1-N4)
            else:
                IsNear = false (F1-F4)
                
Step 5: Assign Labels
        Far minerals numbered F1-F4 in order found
        Near minerals numbered N1-N4 in order found
```

## Example Game State

### Terran Natural Expansion
```
        COMMAND CENTER (37.5, 50.0)
               |
            COM (38.2, 48.5)
         [Crosshair - Yellow]
         
Outer Ring (Far Minerals - Magenta):
   F1 at (28.0, 55.0) ← Furthest from W1
   F2 at (32.0, 58.0)
   F3 at (45.0, 58.0)
   F4 at (48.0, 52.0) ← Closest in far group

Inner Ring (Near Minerals - Cyan):
   N1 at (38.0, 40.0) ← Closest to COM
   N2 at (32.0, 38.0)
   N3 at (45.0, 38.0)
   N4 at (50.0, 42.0) ← Farthest in near group
```

### Expected Console Output
```
InitialMapData: Start[0] ordered 8 minerals
InitialMapData.GreedyOrderMinerals: Start[0] M[8] = mineral[2] at distance 25.43 from W1
InitialMapData.GreedyOrderMinerals: Start[0] ordering complete:
  M[8] = mineral[2] at (28.00,55.00) distance=7.21 F1
  M[7] = mineral[5] at (32.00,58.00) distance=10.84 F2
  M[6] = mineral[7] at (45.00,58.00) distance=9.81 F3
  M[5] = mineral[1] at (48.00,52.00) distance=8.94 F4
  M[4] = mineral[3] at (38.00,40.00) distance=8.45 N1
  M[3] = mineral[4] at (32.00,38.00) distance=11.32 N2
  M[2] = mineral[6] at (45.00,38.00) distance=12.15 N3
  M[1] = mineral[0] at (50.00,42.00) distance=13.88 N4
InitialMapData.RegisterMineralLabels: Start[0] M[8] = F1 at (28.00,55.00)
InitialMapData.RegisterMineralLabels: Start[0] M[7] = F2 at (32.00,58.00)
InitialMapData.RegisterMineralLabels: Start[0] M[6] = F3 at (45.00,58.00)
InitialMapData.RegisterMineralLabels: Start[0] M[5] = F4 at (48.00,52.00)
InitialMapData.RegisterMineralLabels: Start[0] M[4] = N1 at (38.00,40.00)
InitialMapData.RegisterMineralLabels: Start[0] M[3] = N2 at (32.00,38.00)
InitialMapData.RegisterMineralLabels: Start[0] M[2] = N3 at (45.00,38.00)
InitialMapData.RegisterMineralLabels: Start[0] M[1] = N4 at (50.00,42.00)
InitialMapData.RegisterMineralLabels: Registered mineral labels for all start locations
BabySharkMiningManager.DrawMineralLabels: Drawing 8 mineral labels
BabySharkMiningManager.DrawMineralLabels: Drew 'F1' at (28.00,55.00)
BabySharkMiningManager.DrawMineralLabels: Drew 'F2' at (32.00,58.00)
BabySharkMiningManager.DrawMineralLabels: Drew 'F3' at (45.00,58.00)
BabySharkMiningManager.DrawMineralLabels: Drew 'F4' at (48.00,52.00)
BabySharkMiningManager.DrawMineralLabels: Drew 'N1' at (38.00,40.00)
BabySharkMiningManager.DrawMineralLabels: Drew 'N2' at (32.00,38.00)
BabySharkMiningManager.DrawMineralLabels: Drew 'N3' at (45.00,38.00)
BabySharkMiningManager.DrawMineralLabels: Drew 'N4' at (50.00,42.00)
```

## Worker Assignment (Next Phase)

Once labels are displayed, workers will be assigned to minerals:

```
Worker Assignment Strategy:
├─ Initial Workers (W1-W4):
│  ├─ W1 → F1 (start with furthest)
│  ├─ W2 → F2
│  ├─ W3 → F3
│  └─ W4 → F4
│
└─ New Workers (W5+):
   ├─ If supply available → N1-N4 (rotate through near minerals)
   └─ Priority on closest minerals for efficiency
```

## Debug Mode Requirements

**To see mineral labels in game:**

1. Enable DEBUG mode in Settings
   ```csharp
   Settings.DebugMode = true;
   ```

2. Run game with `--debug` flag or set via configuration

3. Ensure `SharkyOptions.Debug = true`

4. Labels will appear on screen starting game frame 1

## Performance Considerations

- **CPU**: Label registration: O(n) per start location, where n = 8 minerals
- **Memory**: ~1KB per label data stored
- **Rendering**: Using Sharky's native debug drawing (optimized)
- **Frequency**: Updated every frame (60 FPS = minimal overhead)

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Labels not visible | Check Z=12 coordinate, verify DEBUG mode enabled |
| Wrong colors | Verify RGB values: F1-F4=255,0,255 (magenta), N1-N4=0,255,255 (cyan) |
| Labels flickering | Check if drawn every frame in OnFrame() |
| Labels in wrong positions | Verify OrderedMainMinerals has correct Position values |
| Missing labels | Check if RegisterMineralLabels called, MineralLabelService != null |
| Console no output | Enable debug logging, check exception handlers |
