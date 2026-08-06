# Expansion Point Drawing - Visual Guide

## What You'll See On Screen

### Game Start (OnStart)

```
Console Output:
  ExpansionPointService initialized
  ExpansionPointDrawService initialized
  BabySharkMiningManager: OnStart called
  InitialMapData: GetNewMiningData called
  ...
  InitialMapData: Expansion[0] townhall placements computed
  InitialMapData: Expansion[1] townhall placements computed
  InitialMapData: Registered expansion draw point E1 at (52.50, 50.00) contested=False
  InitialMapData: Registered expansion draw point E2 at (83.00, 77.00) contested=True
  InitialMapData: Registered expansion draw point E2-Alt1 at (81.00, 83.00) contested=True
  InitialMapData: Registered 3 expansion point(s) for visualization
  BabySharkMiningManager: InitialMapData.GetNewMiningData() completed successfully
```

### Every Frame (OnFrame - Only if SharkyOptions.Debug = true)

```
Console Output (First Frame):
  BabySharkMiningManager.OnFrame: Found 12 workers, drawing labels
  BabySharkMiningManager.DrawExpansionPoints: Drawing 3 expansion points
  BabySharkMiningManager.DrawExpansionPoints: Drew 'E1' at (52.50,50.00,12.00)
  BabySharkMiningManager.DrawExpansionPoints: Drew 'E2' at (83.00,77.00,12.00)
  BabySharkMiningManager.DrawExpansionPoints: Drew 'E2-Alt1' at (81.00,83.00,12.00)
```

### Screen Visualization

```
MAP VIEW (Overhead Camera)

Coordinates marked at bottom of screen:

╔═════════════════════════════════════════════════════════════════╗
║                                                                 ║
║                                                                 ║
║         ● E1                          ● E2          ● E2-Alt1   ║
║      Green Sphere              Yellow Sphere      Orange Sphere ║
║   (Standard Expansion)     (Contested - Primary) (Contested - Alt) ║
║                                                                 ║
║                                                                 ║
║         [Minerals below]          [Minerals below]              ║
║                                                                 ║
║                                                                 ║
╚═════════════════════════════════════════════════════════════════╝

Details:
  ●─── Sphere: 0.75f radius
  └─── Label: Text "E1", "E2", "E2-Alt1" positioned at sphere center
```

## Sphere Details

### Sphere Properties
- **Type**: Sphere primitive (Sharky's debug sphere)
- **Radius**: 0.75f game units
- **Height (Z)**: 12.0f (ensures visibility above terrain)
- **Position**: Expansion townhall placement point (X, Y from computation)

### Label Properties
- **Type**: Text label (Sharky's debug text)
- **Position**: Same as sphere center (Z = 12.0f)
- **Font Size**: 12 (Sharky standard)
- **Text**: "E1", "E2", "E2-Alt1", etc.

## Color Scheme

### Standard Expansion (Not Contested)
```
Color: RGB(0, 255, 0) = Green
Usage: Single sphere + label
Example: "E1"
Meaning: Standard townhall placement, only one valid location
```

### Contested Expansion - Primary
```
Color: RGB(255, 255, 0) = Yellow
Usage: First sphere + label
Example: "E2"
Meaning: Contested base, primary placement option (e.g., favors Player 1)
```

### Contested Expansion - Alternate
```
Color: RGB(255, 165, 0) = Orange
Usage: Second sphere + label
Example: "E2-Alt1"
Meaning: Contested base, alternate placement option (e.g., favors Player 2)
```

## Position Mapping

### Example Map Layout

```
Map Space Coordinates:

╔═════════════════════════════════════════╗
│                                         │
│  (0, 200)                  (200, 200)   │
│  ┌─────────────────────────┐            │
│  │                         │            │
│  │   ● E1 (52.5, 50)       │            │
│  │   Green Sphere          │            │
│  │   Mineral Cluster       │            │
│  │   ╔═════════════╗        │            │
│  │   ║ M M M M    ║        │            │
│  │   ║ M M M M    ║        │            │
│  │   ╚═════════════╝        │            │
│  │                         │            │
│  │                  ● E2 (83.0, 77.0)   │
│  │                  Yellow Sphere       │
│  │                  Mineral Cluster     │
│  │                  ╔═════════════╗     │
│  │                  ║ M M M M    ║     │
│  │                  ║ M M M M    ║     │
│  │                  ╚═════════════╝     │
│  │                   ● E2-Alt1          │
│  │                   (81.0, 83.0)       │
│  │                   Orange Sphere      │
│  │                                      │
│  └─────────────────────────┘            │
│  (0, 0)                  (200, 0)       │
│                                         │
╚═════════════════════════════════════════╝
```

### Sphere Visibility

```
Terrain Height: ~0-2 units
Unit Height: Varies (typically 0-4 units)
Water/Cliffs: Higher elevation

DEBUG SPHERE Z-COORDINATE:
  Z = 12.0f

  This is 10+ units above terrain:
  ✓ Above all terrain features
  ✓ Above all units and structures  
  ✓ Below sky
  ✓ Easily visible and distinguishable
```

## Interaction with Other Visualizations

### With Expansion COM Service (Blue Crosshairs)
```
Blue Crosshair (Cluster Center)
    ↓
    ├─ X: Mineral cluster center
    └─ Z: 12.0f

Green Sphere (Townhall Placement)
    ↓
    ├─ X: 3.75 tiles from cluster (offset)
    └─ Z: 12.0f

Result: Blue crosshair marks cluster, green sphere marks where base builds
```

### With Worker Labels
```
Worker Label (Yellow text above unit)
    └─ Z: unit.Pos.Z + 0.5f = ~0.5-4.5f

Expansion Point (Green sphere)
    └─ Z: 12.0f

Result: No overlap - worker labels are below, expansion points above
```

### With Mineral Labels
```
Mineral Label (Cyan/Magenta text)
    └─ Z: mineral.Pos.Z + 0.5f = ~0.5f

Expansion Point (Green sphere)
    └─ Z: 12.0f

Result: No overlap - minerals labeled at ground level, expansion at sky level
```

## Debugging Checklist

### If You See Nothing:
1. ☐ Check SharkyOptions.Debug = true
2. ☐ Check console for "InitialMapData: Registered expansion draw point" lines
3. ☐ Check console for "BabySharkMiningManager.DrawExpansionPoints: Drawing X expansion points"
4. ☐ Zoom out or adjust camera to see Z=12 level

### If You See Different Colors:
1. ☐ Verify expansion is standard (green) or contested (yellow + orange)
2. ☐ Check console logs for "contested=True/False"
3. ☐ Verify color values match RGB specifications

### If You See Wrong Position:
1. ☐ Verify console shows correct coordinates
2. ☐ Check that ExpansionPointService is computing correctly
3. ☐ Verify mineral clusters are detected properly

### If Labels Are Missing:
1. ☐ Check ManagerDebugService.DrawText() is being called
2. ☐ Verify label names are "E1", "E2", etc. (not blank)
3. ☐ Check console for "Drew 'E1' at..." lines

## Performance Impact

- **Memory**: Minimal - stores only computed points (usually 2-6 expansions)
- **CPU**: Negligible - draws 1-3 spheres + labels per frame when debug enabled
- **Rendering**: Single debug call per point per frame

## Next Steps

1. ✅ All code integrated
2. ✅ All services created and connected
3. ✅ All parameters passed through pipeline
4. ⏭️ **Build solution** (should compile with 0 errors)
5. ⏭️ **Run game with Debug = true**
6. ⏭️ **Observe colored spheres on screen at expansion locations**
7. ⏭️ **Verify console logs show registrations and drawings**
