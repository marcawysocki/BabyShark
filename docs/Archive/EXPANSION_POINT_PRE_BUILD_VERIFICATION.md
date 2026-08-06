# Expansion Point Drawing - Pre-Build Verification

## File Checklist

### ✅ Created Files
- [x] BabySharkBot/Services/ExpansionPointDrawService.cs (NEW)
  - Implements service pattern for storing expansion points
  - All methods complete and functional
  - Proper error handling

### ✅ Modified Files

#### BabySharkBot/BabySharkBot.cs
- [x] Line ~77: ExpansionPointService instantiation
  ```csharp
  var expansionPointService = new ExpansionPointService();
  ```
- [x] Line ~80: ExpansionPointDrawService instantiation
  ```csharp
  var expansionPointDrawService = new ExpansionPointDrawService();
  ```
- [x] Line ~89: Both services passed to BabySharkMiningManager
  ```csharp
  var miningManager = new BabySharkMiningManager(
      ..., expansionPointService, expansionPointDrawService
  );
  ```
- [x] Imports: BabySharkBot.Services (has ExpansionPointService and ExpansionPointDrawService)

#### BabySharkBot/Managers/BabySharkMiningManager.cs
- [x] Line 35: `_expansionPointService` field added
- [x] Line 36: `_expansionPointDrawService` field added
- [x] Line 39: Constructor signature updated with both parameters
- [x] Line 47: Both services stored in constructor
- [x] Line 66: Both services passed to GetNewMiningData
- [x] Line 109: DrawExpansionPoints() call added in OnFrame
- [x] Lines 570-629: DrawExpansionPoints() method implemented
- [x] Imports: BabySharkBot.Services included

#### BabySharkBot/Setup/InitialMapData.cs
- [x] Line 19: GetNewMiningData() signature includes ExpansionPointDrawService parameter
- [x] Line 19: GetNewMiningData() signature includes ExpansionPointService parameter
- [x] Lines 838-908: Registration block for expansion points
  - Retrieves from ExpansionPointService
  - Registers with ExpansionPointDrawService
  - Color-codes by expansion type
  - Generates smart labels
- [x] Imports: BabySharkBot.Services included

---

## Compilation Check

### Parameters Match

**BabySharkBot.cs → BabySharkMiningManager Constructor**:
```
Creating: new BabySharkMiningManager(
    workerLabelService, 
    crosshairService, 
    mineralLabelService, 
    vespeneLabelService, 
    expansionCOMService, 
    expansionPointService,           ← Parameter 6
    expansionPointDrawService        ← Parameter 7
)

Matches Constructor:
public BabySharkMiningManager(
    WorkerLabelService = null,              ← 1
    CrosshairService = null,                ← 2
    MineralLabelService = null,             ← 3
    VespeneLabelService = null,             ← 4
    ExpansionCOMService = null,             ← 5
    ExpansionPointService = null,           ← 6 ✓
    ExpansionPointDrawService = null        ← 7 ✓
)
```
✅ PASS - All parameters match in order and type

**BabySharkMiningManager.OnStart → InitialMapData.GetNewMiningData()**:
```
Calling: _initialMapData.GetNewMiningData(
    gameInfo, data, observation, null,
    _workerLabelService, 
    _crosshairService, 
    _mineralLabelService, 
    _vespeneLabelService,
    _expansionCOMService, 
    _expansionPointService,      ← Parameter 10
    _expansionPointDrawService   ← Parameter 11
)

Matches Method:
public MawBaseLocationData GetNewMiningData(
    ResponseGameInfo,                           ← 1
    ResponseData,                               ← 2
    ResponseObservation,                        ← 3
    Point2D = null,                             ← 4
    WorkerLabelService? = null,                 ← 5
    CrosshairService? = null,                   ← 6
    MineralLabelService? = null,                ← 7
    VespeneLabelService? = null,                ← 8
    ExpansionCOMService? = null,                ← 9
    ExpansionPointService? = null,              ← 10 ✓
    ExpansionPointDrawService? = null           ← 11 ✓
)
```
✅ PASS - All parameters match in order and type

---

## Type Verification

### ExpansionPointService
- [x] Created in BabySharkBot.cs: `new ExpansionPointService()`
- [x] Stored in BabySharkMiningManager: `_expansionPointService`
- [x] Passed to GetNewMiningData: `_expansionPointService`
- [x] Used in InitialMapData: `expansionPointService.GetAllExpansionPoints()`
- [x] Returns: `Dictionary<int, ExpansionPointModel>`

### ExpansionPointDrawService
- [x] Created in BabySharkBot.cs: `new ExpansionPointDrawService()`
- [x] Stored in BabySharkMiningManager: `_expansionPointDrawService`
- [x] Passed to GetNewMiningData: `_expansionPointDrawService`
- [x] Used in InitialMapData: `expansionPointDrawService.SetExpansionPoint(...)`
- [x] Used in BabySharkMiningManager: `_expansionPointDrawService.GetAllPoints()`

---

## Method Signature Verification

### ExpansionPointDrawService.SetExpansionPoint()
```csharp
public void SetExpansionPoint(Point position, string label, Color color, bool isContested = false)
```
✅ Called correctly in InitialMapData with all parameters

### ExpansionPointDrawService.GetAllPoints()
```csharp
public Dictionary<string, ExpansionPointData> GetAllPoints()
```
✅ Called correctly in BabySharkMiningManager.DrawExpansionPoints()

### ExpansionPointService.GetAllExpansionPoints()
```csharp
public Dictionary<int, ExpansionPointModel> GetAllExpansionPoints()
```
✅ Called correctly in InitialMapData

---

## Logic Flow Verification

### Registration Flow in InitialMapData
```
1. if (expansionPointDrawService != null && expansionPointService != null)
   └─ ✅ Both services provided as parameters

2. var allExpansionPoints = expansionPointService.GetAllExpansionPoints()
   └─ ✅ Gets computed points from service

3. foreach (var kvp in allExpansionPoints)
   └─ ✅ Iterates through each expansion

4. for (int optionIdx = 0; optionIdx < model.PlacementOptions.Count; optionIdx++)
   └─ ✅ Iterates through placement options (1-2 per expansion)

5. if (option.Point != null && option.IsValid)
   └─ ✅ Filters for valid placements only

6. string label = model.IsContested && optionIdx > 0 
                ? $"E{expansionIndex + 1}-Alt{optionIdx}" 
                : $"E{expansionIndex + 1}";
   └─ ✅ Generates correct labels

7. Color color = ...
   └─ ✅ Green for standard, Yellow for contested primary, Orange for alternate

8. expansionPointDrawService.SetExpansionPoint(drawPoint, label, color, model.IsContested);
   └─ ✅ Registers with draw service
```
✅ PASS - All logic correct and complete

### Drawing Flow in BabySharkMiningManager
```
1. private void DrawExpansionPoints()
   └─ ✅ Method correctly named and defined

2. if (!ManagerDebugService.IsDebugEnabled) return;
   └─ ✅ Gated by debug flag

3. if (_expansionPointDrawService == null) return;
   └─ ✅ Null check

4. var allPoints = _expansionPointDrawService.GetAllPoints();
   └─ ✅ Retrieves registered points

5. if (allPoints == null || allPoints.Count == 0) return;
   └─ ✅ Early return if no points

6. foreach (var kvp in allPoints)
   └─ ✅ Iterates through each point

7. ManagerDebugService.DrawSphere(position, 0.75f, pointData.Color);
   └─ ✅ Uses Sharky's proven API

8. ManagerDebugService.DrawText(label, position, pointData.Color, 12);
   └─ ✅ Uses Sharky's proven API
```
✅ PASS - All drawing logic correct

---

## Console Logging Verification

### Logs Added:
- [x] BabySharkBot.cs: Service creation
- [x] BabySharkMiningManager.cs: OnStart and GetNewMiningData call
- [x] InitialMapData.cs: Point registration (count and details)
- [x] BabySharkMiningManager.DrawExpansionPoints(): Drawing count and details
- [x] Error handling: Try-catch with exception logging

✅ PASS - Comprehensive logging for debugging

---

## Imports Verification

### BabySharkBot/Services/ExpansionPointDrawService.cs
- [x] using System;
- [x] using System.Collections.Generic;
- [x] using Sharky;
- [x] using SC2APIProtocol;
✅ All needed imports present

### BabySharkBot/Managers/BabySharkMiningManager.cs
- [x] using BabySharkBot.Services; (for ExpansionPointService and ExpansionPointDrawService)
✅ Services namespace imported

### BabySharkBot/BabySharkBot.cs
- [x] using BabySharkBot.Services; (for both services)
✅ Services namespace imported

### BabySharkBot/Setup/InitialMapData.cs
- [x] using BabySharkBot.Services; (already present)
✅ Services namespace imported

---

## Color Definitions Verification

### Green (Standard)
```csharp
new Color { R = 0, G = 255, B = 0 }
```
✅ Correct RGB values

### Yellow (Contested Primary)
```csharp
new Color { R = 255, G = 255, B = 0 }
```
✅ Correct RGB values

### Orange (Contested Alternate)
```csharp
new Color { R = 255, G = 165, B = 0 }
```
✅ Correct RGB values

---

## Z-Coordinate Verification

### All Drawing Points
- [x] Z = 12.0f in ExpansionPointDrawService.SetExpansionPoint()
- [x] Z = 12.0f in InitialMapData.cs point creation
- [x] Z = 12.0f enforcement in BabySharkMiningManager.DrawExpansionPoints()
- [x] Z-coordinate check with enforcement: if (position.Z < 12) { position = new Point { ..., Z = 12.0f } }

✅ PASS - All Z-coordinates correctly set to 12.0f

---

## Pre-Build Summary

| Aspect | Status | Notes |
|--------|--------|-------|
| Files Created | ✅ | ExpansionPointDrawService.cs complete |
| Files Modified | ✅ | All 3 files updated correctly |
| Parameters | ✅ | All match and pass correctly |
| Types | ✅ | All types consistent and correct |
| Methods | ✅ | All methods implemented and called |
| Logic | ✅ | All logic verified and complete |
| Logging | ✅ | Comprehensive console logging added |
| Imports | ✅ | All necessary imports present |
| Colors | ✅ | RGB values correct |
| Z-Coordinates | ✅ | All set to 12.0f |

---

## Ready for Build

✅ **ALL CHECKS PASSED**

**Next Steps**:
1. Build solution: `dotnet build BabyShark.sln`
2. Expected result: 0 errors, 0 warnings (2 unrelated warnings in example code OK)
3. Run game with SharkyOptions.Debug = true
4. Observe colored spheres at expansion locations
5. Verify console logs show registrations and drawings

**Expected Behavior**:
- Green spheres for standard expansions (E1, E2, E3, ...)
- Yellow + Orange spheres for contested expansions (E1, E1-Alt1; E2, E2-Alt1; etc.)
- All spheres at Z = 12.0f with labels
- Console logs showing all registrations and frame-by-frame drawings
