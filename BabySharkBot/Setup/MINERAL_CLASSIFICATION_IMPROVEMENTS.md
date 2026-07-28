# Mineral Classification Improvements

## Problem Identified
Some maps have center large mineral nodes with small nodes nearby, causing incorrect Near/Far classification:
- Map Example 1: Expected 3N/5F, but got problematic boundary cases
- Map Example 2: A Far mineral was actually closer than some Near minerals (exactly at average threshold)

## Solutions Implemented

### 1. Threshold Offset (0.25 units)
**File**: `BabySharkBot/Setup/InitialMapData.cs`

Changed the Near/Far threshold from:
```csharp
IsNear = distanceToTownhall <= avgTownhallDistance
```

To:
```csharp
var nearThreshold = avgTownhallDistance > 0.25f ? avgTownhallDistance - 0.25f : avgTownhallDistance;
IsNear = distanceToTownhall <= nearThreshold
```

**Impact**: 
- Minerals exactly at the average are now classified as Far
- Creates cleaner 3N/5F splits on maps with central clusters
- Avoids edge cases where minerals are at boundary

**Applied To**:
- M[8] classification (first mineral in greedy chain)
- M[7-1] classification (remaining greedy chain)

### 2. Mineral Size Detection
**File**: `BabySharkBot/Setup/InitialMapData.cs` → `ClassifyMineralSizes()` method

New method analyzes mineral patches to classify as Small, Normal, or Large:

```csharp
Heuristic:
- Many close neighbors (≥3 within 3.5 units) → Normal
- Few neighbors (≤1) AND Far AND far from average → Large  
- Otherwise → Normal
```

**Purpose**: 
- Identify strategically important large minerals that are far from townhall
- These become L1-L4 labels instead of F1-F4

### 3. Classification Summary
**File**: `BabySharkBot/Setup/InitialMapData.cs` → Console output

Console now logs classification counts:
```
InitialMapData.GreedyOrderMinerals: Classification summary: 3N, 1L, 4F
```

Formats:
- `3N, 5F` → 3 Near, 5 Far (no large minerals)
- `4N, 0L, 4F` → 4 Near, 0 Large, 4 Far
- `3N, 1L, 5F` → 3 Near, 1 Large Far, 4 regular Far

### 4. Data Model Updates
**File**: `BabySharkBot/Setup/BaseDtos.cs`

#### New MineralSize Enum
```csharp
public enum MineralSize
{
    Small = 0,
    Normal = 1,
    Large = 2
}
```

#### OrderedMineral Properties Added
- `float DistanceToTownhall` → Stores distance for classification reference
- `MineralSize Size` → Classification (Small/Normal/Large)

## Output Format

### Console Logging Example
```
InitialMapData.GreedyOrderMinerals: Start[0] ordering complete:
InitialMapData.GreedyOrderMinerals: Classification summary: 3N, 1L, 4F
  M[8] = mineral[5] at (24.50,32.25) distance=18.75 Large L8
  M[7] = mineral[2] at (18.30,28.10) distance=14.20 Normal F7
  M[6] = mineral[4] at (22.15,30.50) distance=16.80 Normal F6
  ...
  M[1] = mineral[0] at (35.20,40.10) distance=8.50 Normal N1
InitialMapData.ClassifyMineralSizes: Classified 8 minerals, avg inter-mineral distance = 12.34
```

## Worker Assignment Implications

With classification counts available (3N, 1L, 4F), worker assignment can now:

### Pattern 1: 3N, 5F (No Large Minerals)
```
W1 → N1 (primary Near)
W2 → N2 + F1 (hybrid: secure Near, then overflow to Far)
W3 → N3 + F2
W4 → F3 + F4 (Far specialists)
```

### Pattern 2: 3N, 1L, 4F (One Large Mineral)
```
W1 → N1 (primary Near)
W2 → N2 + F1
W3 → N3 + L1 (Large mineral is valuable, pair with Near)
W4 → F2 + F3 + F4 (Far specialists get remaining)
```

### Pattern 3: 4N, 0L, 4F (Good split, no Large)
```
W1 → N1
W2 → N2
W3 → N3 + F1
W4 → N4 + F2 + F3 + F4
```

## Testing Recommendations

1. **Test on problematic maps** with center clusters
   - Expected: Cleaner N/F splits due to 0.25 offset
   - Expected: L1-L4 labels for isolated large minerals

2. **Verify console output**
   - Check classification counts: 3N, 1L, 5F format
   - Check mineral sizes printed as Small/Normal/Large
   - Check that Large minerals are actually far from townhall

3. **In-game verification**
   - Launch with Debug enabled
   - Verify N1-N4 labels appear near townhall (Cyan)
   - Verify F1-F4 labels appear farther away (Magenta)
   - Verify L1-L4 labels appear at isolated large minerals (different color?)

4. **Map variations to test**
   - TritonLE (3 clusters)
   - Altitude (center cluster)
   - Maps with non-standard mineral distributions

## Configuration Tuning

If 0.25 offset is too aggressive or too lenient, adjust in `InitialMapData.cs`:
- Current: `avgTownhallDistance - 0.25f`
- Increase offset: `avgTownhallDistance - 0.35f` (more Far)
- Decrease offset: `avgTownhallDistance - 0.15f` (more Near)

For mineral size detection, adjust proximity threshold:
- Current: `proximityThreshold = 3.5f`
- Increase: Fewer minerals classified as Large
- Decrease: More minerals classified as Large

## Files Modified
1. `BabySharkBot/Setup/BaseDtos.cs`
   - Added MineralSize enum
   - Added Size property to OrderedMineral
   - Added DistanceToTownhall property to OrderedMineral

2. `BabySharkBot/Setup/InitialMapData.cs`
   - Updated M[8] classification with 0.25 offset
   - Updated M[7-1] classification with 0.25 offset
   - Added ClassifyMineralSizes() method
   - Added classification summary logging
   - Updated console output format

## Status
✅ Code changes implemented
✅ Compiles without errors
⏳ Awaiting in-game testing
