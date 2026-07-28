# Mineral Label System - Code Changes Reference

## File 1: BaseDtos.cs

### Addition: MineralLabelService Class
**Location**: End of file, after CrosshairService

```csharp
public class MineralLabelService
{
    /// <summary>
    /// Stores mineral label data: position, label text, and color for visualization.
    /// </summary>
    public class MineralLabelData
    {
        public Point Position { get; set; }
        public string Label { get; set; }
        public Color Color { get; set; }
    }

    private Dictionary<string, MineralLabelData> _mineralLabels = new Dictionary<string, MineralLabelData>();
    private readonly object _lock = new();

    public MineralLabelService() { }

    public void Initialize(ProtobufProxy proxy, Func<ulong, Unit?> getUnitByTag) { }

    public void UpdateRawUnits(List<Unit>? rawUnits) { }

    /// <summary>
    /// Register a mineral label at a specific position with color for visualization.
    /// </summary>
    public void SetMineralLabel(string label, Point position, Color color)
    {
        if (position == null || label == null)
        {
            Console.WriteLine("MineralLabelService.SetMineralLabel: Invalid position or label");
            return;
        }

        lock (_lock)
        {
            var mineralData = new MineralLabelData
            {
                Position = position,
                Label = label,
                Color = color
            };

            _mineralLabels[label] = mineralData;
            Console.WriteLine($"MineralLabelService: Registered mineral label '{label}' at ({position.X:F2},{position.Y:F2}) with color RGB({color.R},{color.G},{color.B})");
        }
    }

    /// <summary>
    /// Get all registered mineral labels for drawing.
    /// </summary>
    public Dictionary<string, MineralLabelData> GetAllMineralLabels()
    {
        lock (_lock)
        {
            return new Dictionary<string, MineralLabelData>(_mineralLabels);
        }
    }

    /// <summary>
    /// Clear all mineral labels.
    /// </summary>
    public void ClearMineralLabels()
    {
        lock (_lock)
        {
            _mineralLabels.Clear();
        }
    }

    public Request? BuildDebugRequest(List<Unit>? rawUnits) => null;
}
```

---

## File 2: InitialMapData.cs

### Change 1: Method Signature
**Before:**
```csharp
public MawBaseLocationData GetNewMiningData(
    ResponseGameInfo gameInfo, 
    ResponseData data, 
    ResponseObservation observation, 
    Point2D startLoc = null, 
    WorkerLabelService? workerLabelService = null, 
    CrosshairService? crosshairService = null)
```

**After:**
```csharp
public MawBaseLocationData GetNewMiningData(
    ResponseGameInfo gameInfo, 
    ResponseData data, 
    ResponseObservation observation, 
    Point2D startLoc = null, 
    WorkerLabelService? workerLabelService = null, 
    CrosshairService? crosshairService = null, 
    MineralLabelService? mineralLabelService = null)  // ← Added parameter
```

---

### Change 2: Call RegisterMineralLabels After Greedy Ordering
**Location**: After `tempBaseDto.OrderedMainMinerals = orderedMainMinerals;`

```csharp
tempBaseDto.OrderedMainMinerals = orderedMainMinerals;
Console.WriteLine($"InitialMapData: Calculated greedy mineral ordering for all start locations");

// Register mineral labels (F1-F4, N1-N4) with MineralLabelService if provided
if (mineralLabelService != null)
{
    RegisterMineralLabels(orderedMainMinerals, mineralLabelService);
}
```

---

### Addition: RegisterMineralLabels Method
**Location**: Before GreedyOrderMinerals method

```csharp
/// <summary>
/// Register mineral labels (F1-F4 for far, N1-N4 for near) with the MineralLabelService.
/// </summary>
private void RegisterMineralLabels(
    List<List<OrderedMineral>> orderedMainMinerals, 
    MineralLabelService mineralLabelService)
{
    if (orderedMainMinerals == null || mineralLabelService == null)
    {
        Console.WriteLine("InitialMapData.RegisterMineralLabels: Invalid input");
        return;
    }

    try
    {
        int farCount = 0;
        int nearCount = 0;

        for (int startIdx = 0; startIdx < orderedMainMinerals.Count; startIdx++)
        {
            var orderedList = orderedMainMinerals[startIdx];
            farCount = 0;
            nearCount = 0;

            foreach (var orderedMineral in orderedList)
            {
                if (orderedMineral == null || orderedMineral.Position == null)
                    continue;

                string label;
                Color labelColor;

                if (orderedMineral.IsNear)
                {
                    nearCount++;
                    label = $"N{nearCount}";
                    // Near minerals: cyan
                    labelColor = new Color { R = 0, G = 255, B = 255 };
                }
                else
                {
                    farCount++;
                    label = $"F{farCount}";
                    // Far minerals: magenta
                    labelColor = new Color { R = 255, G = 0, B = 255 };
                }

                // Convert Vector2Dto to Point for registration
                var position = new Point
                {
                    X = orderedMineral.Position.X,
                    Y = orderedMineral.Position.Y,
                    Z = 12.0f  // Same Z as crosshairs for visibility
                };

                mineralLabelService.SetMineralLabel(label, position, labelColor);
                Console.WriteLine($"InitialMapData.RegisterMineralLabels: Start[{startIdx}] M[{orderedMineral.Index}] = {label} at ({orderedMineral.Position.X:F2},{orderedMineral.Position.Y:F2})");
            }
        }

        Console.WriteLine($"InitialMapData.RegisterMineralLabels: Registered mineral labels for all start locations");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"InitialMapData.RegisterMineralLabels: Error registering mineral labels: {ex.Message}");
    }
}
```

---

## File 3: BabySharkBot.cs

### Changes: Service Instantiation
**Before:**
```csharp
// Create WorkerLabelService for worker label tracking
var workerLabelService = new WorkerLabelService();

// Create CrosshairService for COM visualization
var crosshairService = new CrosshairService();

// Disable Sharky's default MiningTask drawing
// Find the MiningTask in MicroTaskData and replace it with CustomMiningTask
DisableSharkyMiningTaskDrawing();

// Add BabySharkMiningManager with WorkerLabelService and CrosshairService
var miningManager = new BabySharkMiningManager(workerLabelService, crosshairService);
```

**After:**
```csharp
// Create WorkerLabelService for worker label tracking
var workerLabelService = new WorkerLabelService();

// Create CrosshairService for COM visualization
var crosshairService = new CrosshairService();

// Create MineralLabelService for F1-F4, N1-N4 mineral label visualization
var mineralLabelService = new MineralLabelService();  // ← NEW

// Disable Sharky's default MiningTask drawing
// Find the MiningTask in MicroTaskData and replace it with CustomMiningTask
DisableSharkyMiningTaskDrawing();

// Add BabySharkMiningManager with WorkerLabelService, CrosshairService, and MineralLabelService
var miningManager = new BabySharkMiningManager(workerLabelService, crosshairService, mineralLabelService);  // ← UPDATED
```

---

## File 4: BabySharkMiningManager.cs

### Change 1: Field Addition
**Before:**
```csharp
private InitialMapData _initialMapData;
private WorkerLabelService _workerLabelService;
private CrosshairService _crosshairService;
private MawBaseLocationData _mapData;
```

**After:**
```csharp
private InitialMapData _initialMapData;
private WorkerLabelService _workerLabelService;
private CrosshairService _crosshairService;
private MineralLabelService _mineralLabelService;  // ← NEW
private MawBaseLocationData _mapData;
```

---

### Change 2: Constructor
**Before:**
```csharp
public BabySharkMiningManager(
    WorkerLabelService workerLabelService = null, 
    CrosshairService crosshairService = null)
{
    _initialMapData = new InitialMapData();
    _workerLabelService = workerLabelService;
    _crosshairService = crosshairService;
    _mapData = null;
}
```

**After:**
```csharp
public BabySharkMiningManager(
    WorkerLabelService workerLabelService = null, 
    CrosshairService crosshairService = null, 
    MineralLabelService mineralLabelService = null)  // ← NEW PARAMETER
{
    _initialMapData = new InitialMapData();
    _workerLabelService = workerLabelService;
    _crosshairService = crosshairService;
    _mineralLabelService = mineralLabelService;  // ← NEW ASSIGNMENT
    _mapData = null;
}
```

---

### Change 3: OnStart Method
**Before:**
```csharp
_mapData = _initialMapData.GetNewMiningData(
    gameInfo, data, observation, null, 
    _workerLabelService, _crosshairService);
```

**After:**
```csharp
_mapData = _initialMapData.GetNewMiningData(
    gameInfo, data, observation, null, 
    _workerLabelService, _crosshairService, _mineralLabelService);  // ← ADDED PARAMETER
```

---

### Change 4: OnFrame Method
**Before:**
```csharp
// Draw all custom visualizations
DrawWorkerLabels(observation);
DrawCenterOfMassLocations();
DrawCenterOfMass();
DrawWorkerInstructions(observation);
```

**After:**
```csharp
// Draw all custom visualizations
DrawWorkerLabels(observation);
DrawCenterOfMassLocations();
DrawCenterOfMass();
DrawMineralLabels();  // ← NEW CALL
DrawWorkerInstructions(observation);
```

---

### Addition: DrawMineralLabels Method
**Location**: Before OnEnd method

```csharp
/// <summary>
/// Draw F1-F4 (far) and N1-N4 (near) mineral labels on the game client.
/// </summary>
private void DrawMineralLabels()
{
    if (_mineralLabelService == null)
    {
        Console.WriteLine("BabySharkMiningManager.DrawMineralLabels: MineralLabelService is null");
        return;
    }

    if (_mapData == null || _mapData.OrderedMainMinerals == null)
    {
        Console.WriteLine("BabySharkMiningManager.DrawMineralLabels: Map data or OrderedMainMinerals not available");
        return;
    }

    try
    {
        var mineralLabels = _mineralLabelService.GetAllMineralLabels();
        Console.WriteLine($"BabySharkMiningManager.DrawMineralLabels: Drawing {mineralLabels.Count} mineral labels");

        // Draw each mineral label using ManagerDebugService.DrawText
        foreach (var kvp in mineralLabels)
        {
            var label = kvp.Key;
            var mineralData = kvp.Value;

            if (mineralData.Position != null)
            {
                // DrawText takes Point directly and Z is already set in mineralData.Position
                ManagerDebugService.DrawText(label, mineralData.Position, mineralData.Color, 12);

                Console.WriteLine($"BabySharkMiningManager.DrawMineralLabels: Drew '{label}' at ({mineralData.Position.X:F2},{mineralData.Position.Y:F2})");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"BabySharkMiningManager.DrawMineralLabels: Error drawing mineral labels: {ex.Message}");
    }
}
```

---

## Summary of Changes

| File | Type | Lines | Purpose |
|------|------|-------|---------|
| BaseDtos.cs | Addition | 70 | MineralLabelService class |
| InitialMapData.cs | Addition | 60 | RegisterMineralLabels method |
| InitialMapData.cs | Modification | 1 | Method signature parameter |
| InitialMapData.cs | Addition | 5 | Call RegisterMineralLabels |
| BabySharkBot.cs | Addition | 1 | Instantiate service |
| BabySharkBot.cs | Modification | 1 | Pass service to manager |
| BabySharkMiningManager.cs | Addition | 1 | Service field |
| BabySharkMiningManager.cs | Modification | 1 | Constructor parameter |
| BabySharkMiningManager.cs | Addition | 1 | Assignment in constructor |
| BabySharkMiningManager.cs | Modification | 1 | GetNewMiningData parameter |
| BabySharkMiningManager.cs | Addition | 1 | DrawMineralLabels call in OnFrame |
| BabySharkMiningManager.cs | Addition | 50 | DrawMineralLabels method |

**Total**: ~200 lines of code added across 4 files

---

## Testing Code Pattern

To verify the implementation works:

```csharp
// In OnFrame or test method:
if (_mineralLabelService != null)
{
    var labels = _mineralLabelService.GetAllMineralLabels();
    Console.WriteLine($"Testing: {labels.Count} labels registered");
    
    foreach (var label in labels.Keys)
    {
        Console.WriteLine($"  - {label}");
    }
}
```

Expected output:
```
Testing: 8 labels registered
  - F1
  - F2
  - F3
  - F4
  - N1
  - N2
  - N3
  - N4
```
