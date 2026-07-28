# N/L/F Mineral Labeling - Quick Reference Card

## Label Types

| Label | Color | Distance | Isolation | Priority | Assignment |
|-------|-------|----------|-----------|----------|------------|
| **N#** | 🟦 Cyan | ≤avg-0.25 | Any | 1st | Primary |
| **L#** | 🟨 Yellow | >avg-0.25 | Isolated | 2nd | Strategic |
| **F#** | 🟪 Magenta | >avg-0.25 | Clustered | 3rd | Overflow |

## Console Patterns

### What to Look For
```
InitialMapData.GreedyOrderMinerals: Classification summary: 3N, 1L, 4F
                                                             ↑   ↑   ↑
                                                    N-count L-count F-count
```

### Total = 8 Always
```
3 + 1 + 4 = 8 ✓
4 + 0 + 4 = 8 ✓
2 + 2 + 4 = 8 ✓
```

## Common Map Patterns

```
Pattern A: 4N, 0L, 4F  (standard split, no large)
Pattern B: 3N, 1L, 4F  (one large mineral)
Pattern C: 3N, 0L, 5F  (more far minerals)
Pattern D: 2N, 2L, 4F  (two large minerals)
Pattern E: 5N, 1L, 2F  (more near minerals)
```

## Color Codes (RGB Values)

```
N# = Cyan   (0, 255, 255)   ← Close to townhall
L# = Yellow (255, 255, 0)   ← Isolated & valuable
F# = Magenta(255, 0, 255)   ← Far & standard
```

## In-Game Visual

```
Near Townhall:     N1  N2  N3  (Cyan close-up)
                    ↓   ↓   ↓
              [ TOWNHALL ]
                    ↑   ↑   ↑
Isolated/Far:      L1  F1  F2  (Yellow + Magenta far out)
                        ↑   ↑
                    F3  F4  F5  (Magenta cluster)
```

## Worker Assignment Template

```
Worker 1:  N# + (optional L#)   ← Primary near + strategic large
Worker 2:  N# + F#              ← Balanced split
Worker 3:  N# + F#              ← Balanced split  
Worker 4:  (remaining) F# + F#  ← Specialists/Overflow
```

## Tuning Quick Dial

Too many N#? → Increase offset (was 0.25, try 0.35)
Too many F#? → Decrease offset (was 0.25, try 0.15)
Too few L#?  → Increase proximity threshold (was 3.5, try 4.0)
Too many L#? → Decrease proximity threshold (was 3.5, try 3.0)

## Debug Command

```powershell
# Run with output
cd "C:\Users\marca\source\repos\BabyShark"
dotnet run --project BabySharkBot/BabySharkBot.csproj 2>&1 | grep "Classification summary"
```

## Expected Output Example

```
[Map Load]
InitialMapData.GreedyOrderMinerals: Start[0] ordering complete:
InitialMapData.GreedyOrderMinerals: Classification summary: 3N, 1L, 4F
  M[8] = mineral[3] at (30.00,40.00) distance=22.80 Large L1
  M[7] = mineral[5] at (22.00,32.00) distance=13.80 Normal F1
  ...
  M[1] = mineral[0] at (10.00,20.00) distance=1.40 Normal N1
InitialMapData.RegisterMineralLabels: Start[0] Summary: 3N, 1L, 4F
```

## Verification Steps

1. ✅ Check console: See "Classification summary"
2. ✅ In-game: Look for Yellow (L#) labels
3. ✅ Verify: N# close, L# isolated, F# clustered
4. ✅ Count: Ensure N + L + F = 8

## Key Facts

- L# labels are **YELLOW** - not Magenta or Cyan
- L# minerals are **ISOLATED** - few neighbors
- L# minerals are **FAR** - beyond near threshold
- L# minerals are **VALUABLE** - worth the distance
- Each mineral gets **ONE LABEL** - no duplicates
- Total always **8 minerals** - N + L + F = 8

## If Something Goes Wrong

```
No L# labels visible?
  → Check: orderedMineral.Size == MineralSize.Large
  → Verify: ClassifyMineralSizes() was called
  → Console: Look for size classification in output

Wrong label colors?
  → Check: RGB values in RegisterMineralLabels()
  → Verify: N#=Cyan, L#=Yellow, F#=Magenta
  → Debug: Print labelColor values

Labels at wrong positions?
  → Check: Position conversion (Vector2Dto → Point)
  → Verify: Z coordinate = 12.0f
  → Debug: Print position values

Classification counts wrong?
  → Check: Sum = 8 (N + L + F)
  → Verify: Each mineral counted once
  → Debug: Enable console logging
```

## Related Files

📄 Full documentation:
- L_LABEL_LARGE_MINERAL_SYSTEM.md
- MINERAL_CLASSIFICATION_COUNTS_GUIDE.md  
- FINAL_SUMMARY_NLF_SYSTEM.md

📋 Implementation:
- BabySharkBot/Setup/InitialMapData.cs (RegisterMineralLabels method)
- BabySharkBot/Setup/BaseDtos.cs (MineralSize enum)

## Status
✅ Implemented   
✅ Compiled      
⏳ Testing       
⏳ Integration   
