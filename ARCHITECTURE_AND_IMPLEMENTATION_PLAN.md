# BabyShark Architecture & Implementation Plan

## Executive Summary

Based on lessons learned from PupusPistrixVectatorPestium, this document outlines the proper architecture for BabyShark's map initialization, worker label system, and debug drawing implementation.

**Key Principles:**
1. Use Sharky's proven drawing system (which works reliably)
2. Implement custom dat-file based map data caching (not JSON)
3. Create unique, persistent worker labels (not generic task labels)
4. Thread-safe dat file loading and creation
5. Centralized label choreography based on builds/maps/opponents

---

## Part 1: Current Understanding of Your Architecture

### 1.1 Map Data Pipeline

```
Game Start
    ↓
Parse Command Line Arguments (ladder) or use hardcoded map (local testing)
    ↓
Determine Map Name
    ↓
Look for Canonical Dat File: Setup/MapData/{MapName}.dat
    ↓
    ├─ EXISTS (return true)
    │   ↓
    │   Load into MawMineralManager.OnStart()
    │   Check Setup/Settings.cs flags
    │   Initialize worker choreography
    │
    └─ NOT EXISTS (return false)
        ↓
        InitialMapData.GetNewMiningData() executes
        ↓
        Parses ResponseObservation.RawData.Units
        ↓
        Identifies minerals, vespene geysers
        ↓
        Calculates Center of Mass (COM)
        ↓
        Serializes to canonical dat file
        ↓
        Returns MawBaseLocationData
        ↓
        Continues to MawMineralManager.OnStart()
```

### 1.2 Dat File Format (Not JSON)

**Rationale:** Binary serialization is faster, smaller, and can use MemoryPack for efficient storage.

```csharp
// Setup/MapData/{MapName}.dat structure (pseudocode)
[MemoryPack Serializable]
public class MapDataSnapshot
{
    public string MapName { get; set; }                    // "MagannathaAIE_v2"
    public Vector2Dto MineralCenterOfMass { get; set; }    // Calculated COM
    public List<Vector2Dto> MineralPatches { get; set; }   // Individual patches
    public List<Vector2Dto> VespenePatches { get; set; }   // Gas locations
    public Point2D MainBaseLocation { get; set; }          // Resource center
    public DateTime CreatedUtc { get; set; }               // Timestamp for validation
}
```

### 1.3 InitialMapData Responsibilities

**File:** `BabySharkBot/Setup/InitialMapData.cs`

```csharp
public class InitialMapData
{
    // Returns MawBaseLocationData with all map resources parsed
    public MawBaseLocationData GetNewMiningData(
        ResponseGameInfo gameInfo,
        ResponseData data,
        ResponseObservation observation,
        Point2D startLoc);
    
    // Implementation:
    // 1. Parse observation.Observation.RawData.Units
    // 2. Filter by UnitType (minerals, gas, refineries)
    // 3. Calculate Center of Mass from mineral positions
    // 4. Return fully populated MawBaseLocationData
}
```

### 1.4 Threading Model

```
Main Game Loop (Frame 0)
    ↓
Check if dat file exists (synchronous quick check)
    ↓
    ├─ YES: Load in background thread (doesn't block frame 0)
    │
    └─ NO: Start InitialMapData in background thread
         ↓
         Parse map data
         ↓
         Serialize to dat file
         ↓
         Signal completion flag
         ↓
         MawMineralManager.OnStart() receives signal
         ↓
         Initialize with map data
```

---

## Part 2: Worker Label System (Custom Labels, NOT Generic Task Labels)

### 2.1 The Problem with Sharky's Current Labels

Sharky's labels look like:
```
"Mining, Minerals, 0"
"Mining, Gas, 0"
"Attack, 0"
```

These are **generic task labels** that don't uniquely identify individual workers.

### 2.2 Your Solution: Unique Worker Labels

Each worker gets a **unique name and choreography**:

```csharp
public class WorkerLabel
{
    public ulong UnitTag { get; set; }                      // Unique unit identifier
    public string WorkerName { get; set; }                  // "Drone-001", "Drone-002", etc.
    public string CurrentRole { get; set; }                 // "Mining", "Building", "Scouting", "Attacking"
    public string CurrentTarget { get; set; }               // Specific patch ID or building location
    public Vector2 CurrentPosition { get; set; }            // For choreography calculations
    public DateTime AssignmentTime { get; set; }            // When this role was assigned
    public Color LabelColor { get; set; }                   // Role-specific color
    public string BuildContext { get; set; }                // Which build assigned this role
    public string MapName { get; set; }                     // Which map this is on
    public string OpponentRace { get; set; }                // How to choreograph against this race
}
```

### 2.3 Persistent Label Dictionary (Prevents Labels Disappearing)

**File:** `BabySharkBot/Managers/MawMineralManager.cs` (or similar)

```csharp
public class MawMineralManager
{
    // Dictionary persists across frames - keys are Unit.Tag
    private Dictionary<ulong, WorkerLabel> _workerLabels = new();
    
    public void OnFrame(ResponseObservation observation)
    {
        // Update or create labels
        foreach (var unit in observation.Observation.RawData.Units)
        {
            if (!_workerLabels.TryGetValue(unit.Tag, out var label))
            {
                // New worker - assign unique name and role
                label = new WorkerLabel
                {
                    UnitTag = unit.Tag,
                    WorkerName = GenerateUniqueName(unit),  // "Drone-001"
                    CurrentRole = DetermineInitialRole(unit),
                    CurrentPosition = new Vector2(unit.Pos.X, unit.Pos.Y),
                    AssignmentTime = DateTime.UtcNow,
                    LabelColor = GetColorForRole(DetermineInitialRole(unit)),
                    BuildContext = _currentBuildName,
                    MapName = _mapName,
                    OpponentRace = _enemyRace.ToString()
                };
                _workerLabels[unit.Tag] = label;
                LogEvent($"WorkerLabelCreated", details: $"Tag={unit.Tag}, Name={label.WorkerName}");
            }
            else
            {
                // Update existing label
                label.CurrentPosition = new Vector2(unit.Pos.X, unit.Pos.Y);
                label.CurrentRole = DetermineCurrentRole(unit);  // May change each frame
            }
        }
        
        // Remove labels for dead units
        var deadTags = _workerLabels.Keys
            .Where(tag => !observation.Observation.RawData.Units.Any(u => u.Tag == tag))
            .ToList();
        foreach (var tag in deadTags)
        {
            LogEvent($"WorkerLabelRemoved", details: $"Tag={tag}, Name={_workerLabels[tag].WorkerName}");
            _workerLabels.Remove(tag);
        }
    }
    
    private string GenerateUniqueName(Unit unit)
    {
        var count = _workerLabels.Count + 1;
        return $"Drone-{count:000}";  // Drone-001, Drone-002, etc.
    }
}
```

### 2.4 Choreography System (Build/Map/Opponent Aware)

```csharp
public class WorkerChoreography
{
    public string BuildName { get; set; }                    // "MutaliskRush", "ZerglingRush"
    public string MapName { get; set; }                      // "MagannathaAIE_v2"
    public Race OpponentRace { get; set; }                   // Affects worker distribution
    
    // Returns role assignment for this worker in this context
    public string DetermineRole(WorkerLabel worker, MapDataSnapshot mapData)
    {
        // Example logic:
        if (OpponentRace == Race.Terran && mapData.ThreatLevel > 0.5)
            return "DefenseScout";
        else if (BuildName.Contains("Mutalisk"))
            return "GasWorker";  // Prioritize gas for mutalisk build
        else
            return "MineralWorker";
    }
}
```

---

## Part 3: Debug Drawing Integration

### 3.1 Using Sharky's DebugService (Proven to Work)

**Why Sharky's system works:**
- Uses SC2 API's native debug drawing
- Serialized to protobuf
- Sent in RequestDebug message each frame
- Rendering happens client-side (reliable)

### 3.2 Custom Label Drawing (Not Sharky's Generic Labels)

```csharp
public class MawMineralManager
{
    private DebugService _debugService;
    
    public void DrawWorkerLabels()
    {
        foreach (var label in _workerLabels.Values)
        {
            // Draw unique label
            var labelText = $"{label.WorkerName}\n{label.CurrentRole}";
            var pos = new Point { X = (int)label.CurrentPosition.X, Y = (int)label.CurrentPosition.Y, Z = 12 };
            var color = label.LabelColor;
            
            // Use Sharky's proven DrawText method
            _debugService.DrawText(labelText, pos, color, size: 11);
        }
    }
    
    public void DrawCenterOfMass(Vector2Dto com, Color color)
    {
        // Draw crosshair at COM (can use any Sharky primitives)
        var comPoint = new Point { X = (int)com.X, Y = (int)com.Y, Z = 12 };
        
        // Horizontal line
        _debugService.DrawLine(
            new Point { X = (int)com.X - 5, Y = (int)com.Y, Z = 12 },
            new Point { X = (int)com.X + 5, Y = (int)com.Y, Z = 12 },
            color);
        
        // Vertical line
        _debugService.DrawLine(
            new Point { X = (int)com.X, Y = (int)com.Y - 5, Z = 12 },
            new Point { X = (int)com.X, Y = (int)com.Y + 5, Z = 12 },
            color);
        
        // Center sphere
        _debugService.DrawSphere(comPoint, radius: 2, color: color);
    }
}
```

### 3.3 Drawing Call in Manager OnFrame

```csharp
public class MawMineralManager : SharkyManager
{
    public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
    {
        var actions = new List<SC2APIProtocol.Action>();
        
        // Update internal state
        UpdateWorkerLabels(observation);
        UpdateMineralAssignments();
        
        // Issue mining commands
        actions.AddRange(IssueMiningCommands(observation));
        
        // DEBUG DRAWING (gated by SharkyOptions.Debug)
        if (_sharkyOptions.Debug)
        {
            DrawWorkerLabels();
            DrawCenterOfMass(_mapData.MineralCenterOfMass, new Color { R = 0, G = 255, B = 255 });
            LogEvent("DrawingFrame", frame: observation.Observation.GameLoop);
        }
        
        return actions;
    }
}
```

---

## Part 4: File Changes & Rationale

### 4.1 Files to Create

#### 1. `BabySharkBot/Setup/MapDataSnapshot.cs`
**Purpose:** Dat file serialization structure  
**Content:**
```csharp
namespace BabySharkBot.Setup
{
    [MemoryPackable]
    public class MapDataSnapshot
    {
        public string MapName { get; set; }
        public Vector2Dto MineralCenterOfMass { get; set; }
        public List<Vector2Dto> MineralPatches { get; set; }
        public List<Vector2Dto> VespenePatches { get; set; }
        public Point2D MainBaseLocation { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
```
**Why:** Binary serialization is faster and smaller than JSON for dat files.

#### 2. `BabySharkBot/Setup/MapDataManager.cs`
**Purpose:** Load/save dat files in background thread  
**Content:**
```csharp
namespace BabySharkBot.Setup
{
    public class MapDataManager
    {
        public bool TryLoadMapData(string mapName, out MapDataSnapshot data)
        {
            var path = Path.Combine("Setup/MapData", $"{mapName}.dat");
            if (File.Exists(path))
            {
                data = MemoryPackSerializer.Deserialize<MapDataSnapshot>(
                    File.ReadAllBytes(path));
                return true;
            }
            data = null;
            return false;
        }
        
        public void SaveMapData(MapDataSnapshot data)
        {
            var path = Path.Combine("Setup/MapData", $"{data.MapName}.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var bytes = MemoryPackSerializer.Serialize(data);
            File.WriteAllBytes(path, bytes);
        }
    }
}
```
**Why:** Separates concerns of file I/O from map data generation; allows async loading.

#### 3. `BabySharkBot/Managers/MawMineralManager.cs`
**Purpose:** Worker label management and mining orchestration  
**Content:**
```csharp
namespace BabySharkBot.Managers
{
    public class MawMineralManager : SharkyManager
    {
        private Dictionary<ulong, WorkerLabel> _workerLabels = new();
        private MapDataSnapshot _mapData;
        
        public override IEnumerable<SC2APIProtocol.Action> OnFrame(ResponseObservation observation)
        {
            var actions = new List<SC2APIProtocol.Action>();
            
            UpdateWorkerLabels(observation);
            actions.AddRange(IssueMiningCommands());
            
            if (SharkyOptions.Debug)
            {
                DrawWorkerLabels();
                DrawCenterOfMass();
            }
            
            return actions;
        }
        
        // All implementation as shown in Part 2 above
    }
}
```
**Why:** Centralized worker choreography aware of builds, maps, and opponents.

#### 4. `BabySharkBot/WorkerLabel.cs`
**Purpose:** Data structure for unique worker identity and choreography  
**Content:**
```csharp
namespace BabySharkBot
{
    public class WorkerLabel
    {
        public ulong UnitTag { get; set; }
        public string WorkerName { get; set; }
        public string CurrentRole { get; set; }
        public string CurrentTarget { get; set; }
        public Vector2 CurrentPosition { get; set; }
        public DateTime AssignmentTime { get; set; }
        public Color LabelColor { get; set; }
        public string BuildContext { get; set; }
        public string MapName { get; set; }
        public string OpponentRace { get; set; }
    }
}
```
**Why:** Single source of truth for worker identity across all subsystems.

### 4.2 Files to Modify

#### 1. `Program.cs`
**Current Issue:** Hard-coded map selection, no dat file loading  
**Changes:**
```csharp
// Parse map name from args (ladder) or use random/hardcoded (local)
var mapName = ExtractMapName(args) ?? maps[random.Next(maps.Length)];
LogEvent("MapResolved", details: mapName);

// Try to load existing dat file
var mapDataManager = new MapDataManager();
bool mapDataExists = mapDataManager.TryLoadMapData(mapName, out var mapData);
LogEvent("MapDataCheck", details: $"exists={mapDataExists}, map={mapName}");

// If dat file doesn't exist, InitialMapData will be triggered in MawMineralManager.OnStart()
var babySharkBot = new BabySharkBot(gameConnection, mapName, mapDataExists);

// Continue with ladder/local runner
```
**Why:** Establishes map identity before bot initialization; signals dat file state.

#### 2. `BabySharkBot/Setup/Settings.cs`
**Current State:** Generic settings  
**Add:** Map data state flags
```csharp
namespace BabySharkBot.Setup
{
    public static class Settings
    {
        public static bool MapDataLoaded { get; set; } = false;
        public static string CurrentMapName { get; set; } = "";
        public static bool UseDatFiles { get; set; } = true;  // Can disable for testing
    }
}
```
**Why:** Allows MawMineralManager.OnStart() to check if map data is available.

#### 3. `BabySharkBot/Setup/InitialMapData.cs`
**Current Issue:** Creates labels, tries to draw (causes hallucinations)  
**Changes:**
- Remove all debug drawing code
- Return only `MawBaseLocationData`
- Add logging via `LogEvent()` for CSV instrumentation
- Ensure fully qualified `Sharky.UnitTypes` for type safety

```csharp
public MawBaseLocationData GetNewMiningData(ResponseGameInfo gameInfo, ResponseData data, ResponseObservation observation, Point2D startLoc)
{
    LogEvent("InitialMapDataStart", details: $"Parsing {observation.Observation.RawData.Units.Count} units");
    
    var mineralTypes = new HashSet<Sharky.UnitTypes> { /* ... */ };
    var vespeneTypes = new HashSet<Sharky.UnitTypes> { /* ... */ };
    
    // Parse minerals and vespene
    var tempBaseDto = new MawBaseLocationData();
    foreach (var unit in observation.Observation.RawData.Units)
    {
        var ut = (Sharky.UnitTypes)unit.UnitType;
        
        if (mineralTypes.Contains(ut))
            tempBaseDto.MapLocationData.MineralPatches.Add(new Vector2Dto(unit.Pos.X, unit.Pos.Y));
        else if (vespeneTypes.Contains(ut))
            tempBaseDto.MapLocationData.VespenePatches.Add(new Vector2Dto(unit.Pos.X, unit.Pos.Y));
    }
    
    // Calculate COM
    if (tempBaseDto.MapLocationData.MineralPatches.Count > 0)
    {
        float avgX = tempBaseDto.MapLocationData.MineralPatches.Average(m => m.X);
        float avgY = tempBaseDto.MapLocationData.MineralPatches.Average(m => m.Y);
        tempBaseDto.MineralCenterOfMass = new Vector2Dto(avgX, avgY);
    }
    
    LogEvent("InitialMapDataEnd", details: $"Found {tempBaseDto.MapLocationData.MineralPatches.Count} minerals, COM={tempBaseDto.MineralCenterOfMass}");
    
    return tempBaseDto;
}
```
**Why:** Single responsibility - data generation only, no drawing. Drawing is MawMineralManager's job.

---

## Part 5: Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│ Program.cs                                                      │
│ ├─ Parse map name from args or select random                   │
│ ├─ Create MapDataManager                                        │
│ ├─ Call TryLoadMapData(mapName) → returns (bool, MapDataSnapshot)
│ ├─ Set Settings.MapDataLoaded = result                          │
│ ├─ Set Settings.CurrentMapName = mapName                        │
│ └─ Create BabySharkBot with game connection                     │
└──────────────────┬──────────────────────────────────────────────┘
                   │
                   ↓
┌─────────────────────────────────────────────────────────────────┐
│ BabySharkBot.OnStart() / MawMineralManager.OnStart()            │
│ ├─ Check Settings.MapDataLoaded                                 │
│ │  ├─ If true: Use loaded MapDataSnapshot                       │
│ │  └─ If false: Call InitialMapData.GetNewMiningData()          │
│ │     └─ Trigger MapDataManager.SaveMapData() to create .dat    │
│ └─ Initialize worker label tracking                             │
│    └─ Set _workerLabels = new Dictionary<ulong, WorkerLabel>() │
└──────────────────┬──────────────────────────────────────────────┘
                   │
                   ↓
┌─────────────────────────────────────────────────────────────────┐
│ MawMineralManager.OnFrame() - Every Frame                       │
│ ├─ Update worker labels (add/remove/update)                     │
│ ├─ Determine role for each worker                               │
│ │  ├─ Check current build context                               │
│ │  ├─ Check opponent race                                       │
│ │  └─ Check map hazards                                         │
│ ├─ Issue mining commands                                        │
│ │  └─ Use WorkerChoreography.DetermineRole()                    │
│ └─ IF SharkyOptions.Debug:                                      │
│    ├─ Draw each worker label (unique name + role)              │
│    ├─ Draw COM crosshair at MineralCenterOfMass                │
│    └─ Log frame events to CSV                                   │
└──────────────────┬──────────────────────────────────────────────┘
                   │
                   ↓
┌─────────────────────────────────────────────────────────────────┐
│ Sharky Framework (Unmodified)                                   │
│ ├─ DebugService.DrawText() - Renders labels                     │
│ ├─ DebugService.DrawLine() - Renders COM crosshair              │
│ ├─ DebugService.DrawSphere() - Renders COM center               │
│ └─ Sends protobuf RequestDebug to SC2 client                    │
└──────────────────┬──────────────────────────────────────────────┘
                   │
                   ↓
┌─────────────────────────────────────────────────────────────────┐
│ StarCraft II Client                                             │
│ └─ Renders debug drawing (labels, crosshair, spheres)           │
└─────────────────────────────────────────────────────────────────┘
```

---

## Part 6: Why This Architecture Prevents Previous Failures

### Problem 1: Labels Disappearing After Refactoring
**Solution:** Persistent `Dictionary<ulong, WorkerLabel>` dictionary keyed by `Unit.Tag` survives across frame boundaries and refactoring.

### Problem 2: AI Hallucinations on Drawing Code
**Solution:** Separate concerns:
- `InitialMapData.cs` - **ONLY** generates map data, returns `MawBaseLocationData`
- `MawMineralManager.cs` - **ONLY** uses Sharky's proven drawing APIs
- No custom drawing code invented by AI, only Sharky's `DrawText()`, `DrawLine()`, `DrawSphere()`

### Problem 3: Generic Labels Not Suitable for Build Context
**Solution:** `WorkerLabel` includes:
- `WorkerName` - Unique per worker
- `BuildContext` - Which build assigned this role
- `OpponentRace` - Choreography information
- `MapName` - Context-aware behavior

### Problem 4: Drawing COM Crosshair
**Solution:** Use Sharky's primitive drawing:
```csharp
_debugService.DrawLine(/* horizontal */);
_debugService.DrawLine(/* vertical */);
_debugService.DrawSphere(/* center */);
```

---

## Part 7: CSV Instrumentation

### 7.1 Logging Events

Use the `LogEvent()` pattern from PupusPistrixVectatorPestium:

```csharp
// In Program.cs
LogEvent("MapResolved", details: mapName);
LogEvent("MapDataCheck", details: $"exists={mapDataExists}");

// In MawMineralManager.OnFrame()
LogEvent("DrawingFrame", frame: observation.Observation.GameLoop);
LogEvent("WorkerLabelCreated", details: $"Tag={unit.Tag}, Name={label.WorkerName}");
LogEvent("WorkerLabelRemoved", details: $"Tag={tag}, Name={_workerLabels[tag].WorkerName}");
LogEvent("InitialMapDataStart", details: $"Parsing {unitCount} units");
LogEvent("InitialMapDataEnd", details: $"Found {mineralCount} minerals, COM at ({com.X}, {com.Y})");
```

### 7.2 CSV Output Structure

```
TimestampUtc,MonotonicMs,Event,Frame,DurationMs,Details
2025-01-15T10:30:45.123Z,0,ProgramStart,,,
2025-01-15T10:30:45.234Z,111,MapResolved,,,"MagannathaAIE_v2"
2025-01-15T10:30:45.245Z,122,MapDataCheck,,,"exists=true"
2025-01-15T10:30:45.356Z,233,BotInitialized,,,"BabySharkBot ready"
2025-01-15T10:30:46.012Z,889,DrawingFrame,0,,"Labels drawn, COM visible"
2025-01-15T10:30:46.023Z,900,WorkerLabelCreated,,,"Tag=4294967295, Name=Drone-001"
```

---

## Part 8: Testing & Validation

### 8.1 Test Scenario 1: Map Data Creation
1. Delete `Setup/MapData/MagannathaAIE_v2.dat`
2. Run Program.cs with local test
3. Verify:
   - CSV shows `MapDataCheck exists=false`
   - CSV shows `InitialMapDataStart` and `InitialMapDataEnd`
   - `.dat` file is created
   - Worker labels appear on screen
   - COM crosshair appears on screen

### 8.2 Test Scenario 2: Map Data Loading
1. Run again with same map
2. Verify:
   - CSV shows `MapDataCheck exists=true`
   - `InitialMapDataStart` NOT in CSV (data wasn't generated)
   - Worker labels appear immediately
   - COM crosshair appears immediately

### 8.3 Test Scenario 3: Worker Label Persistence
1. Run bot for 10+ frames
2. Pause game
3. Verify:
   - Each worker has consistent unique name (Drone-001, Drone-002, etc.)
   - Role labels update based on activity
   - Labels don't flicker or disappear

### 8.4 Test Scenario 4: Debug Drawing Verification
1. Set `SharkyOptions.Debug = true`
2. Run frame 0-100
3. Verify in SC2 client:
   - "Drone-001", "Drone-002" etc. appear above workers
   - Each label shows role (Mining, Building, etc.)
   - COM crosshair (horizontal + vertical lines) appears
   - COM center sphere appears

---

## Part 9: Implementation Checklist

- [ ] Create `MapDataSnapshot.cs` in `Setup/`
- [ ] Create `MapDataManager.cs` in `Setup/`
- [ ] Create `WorkerLabel.cs` in `BabySharkBot/`
- [ ] Create `MawMineralManager.cs` in `Managers/`
- [ ] Update `Program.cs` to parse map name and call `MapDataManager`
- [ ] Update `Settings.cs` to track map data state
- [ ] Update `InitialMapData.cs` to remove all drawing code
- [ ] Update `InitialMapData.cs` to add logging/CSV events
- [ ] Implement `MawMineralManager.UpdateWorkerLabels()`
- [ ] Implement `MawMineralManager.DrawWorkerLabels()`
- [ ] Implement `MawMineralManager.DrawCenterOfMass()`
- [ ] Test map data creation (scenario 1)
- [ ] Test map data loading (scenario 2)
- [ ] Test worker label persistence (scenario 3)
- [ ] Test debug drawing (scenario 4)
- [ ] Validate CSV event logging

---

## Part 10: Key Takeaways

1. **Separation of Concerns:** InitialMapData generates data; MawMineralManager draws and choreographs
2. **Persistent State:** Use `Dictionary<ulong, WorkerLabel>` to prevent label loss
3. **Proven Drawing APIs:** Use only Sharky's `DrawText()`, `DrawLine()`, `DrawSphere()` - never invent custom drawing code
4. **Build-Aware Choreography:** Each worker's role depends on current build, map, and opponent
5. **CSV Instrumentation:** Track all state changes for debugging and validation
6. **Unique Labels:** "Drone-001", "Drone-002" instead of generic "Mining, Minerals, 0"

---

**Document Version:** 1.0  
**Created:** [Current Date]  
**Author:** Architecture Analysis (from PupusPistrixVectatorPestium standards)  
**Status:** Ready for implementation
