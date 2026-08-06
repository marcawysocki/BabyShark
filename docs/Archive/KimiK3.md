### REASONING CONTEXT

The issue is that team labeling (Teal/Salmon/Blue/Yellow) is currently happening during the observation loop, which violates the requirement that "team labels should be done in chrisCrossAppleSause and only on Frame Zero." The handoff mechanism needs a new manager for observations to ensure labels are only created when new units appear (larva, workers, army units).

Key structural problems identified:
1. Team labeling logic is scattered across observation routines
2. No clear separation between initial labeling (frame zero) and ongoing labeling (new unit creation)
3. The chrisCrossAppleSause service needs to own the initial team assignment process
4. A new manager is required for post-handoff observations that only creates labels for newly spawned units

### TASK DECOMPOSITION

1. **Analyze current labeling implementation** - Find where team labels are currently assigned during observation
2. **Identify chrisCrossAppleSause integration points** - Determine how to move initial labeling there
3. **Design new observation manager** - Create a manager that handles post-handoff observations without creating team labels
4. **Update handoff mechanism** - Ensure proper manager replacement and label cleanup
5. **Validate mineral assignment logic** - Ensure greedy chain assignments remain correct after refactoring

### FILE INDEX

<file_package path="BabySharkBot/BabySharkBot.cs">
```csharp
// BabySharkAI composition root and game-loop proxy
public class BabySharkAI : StartupAwareSharkyBot
{
    private readonly IServiceCollection _services;
    private readonly IOngoingMapData _mapData;
    
    public override void OnStart()
    {
        // Current implementation calls ProcessVisibleUnits which includes team labeling
        // Need to separate initial labeling from ongoing observation
    }
}
```
</file_package>

<file_package path="BabySharkBot/Services/chrisCrossAppleSause.cs">
```csharp
// CCA worker choreography/state machine
public class chrisCrossAppleSause : IService
{
    private readonly WorkerLabelService _workerLabelService;
    private readonly TeamColorService _teamColorService;
    
    public void BuildBumpOrders()
    {
        // Currently only handles bump orders, needs to take over initial team labeling
    }
}
```
</file_package>

<file_package path="BabySharkBot/Managers/BabySharkMiningManager.cs">
```csharp
// Main mining/JIT state and commands
public class BabySharkMiningManager :IManager
{
    private readonly WorkerLabelService _workerLabelService;
    private readonly OngoingMapData _mapData;
    
    public void OnFrame()
    {
        // Currently handles team label observation - needs refactoring
        ProcessVisibleUnits();
    }
}
```
</file_package>

<file_package path="BabySharkBot/Setup/BaseDtos.cs">
```csharp
// MemoryPack DTOs and label/drawing services
public class WorkerLabelService
{
    public void SetLabel(string label, ulong tag, Point? pos = null)
    public string GetLabel(ulong tag)
    public ulong GetTag(string label)
}
```
</file_package>

<file_package path="BabySharkBot/Setup/OngoingMapData.cs">
```csharp
// Current-spawn refresh and assignment resolver
public class OngoingMapData
{
    public void ResolveTeamAssignments(MapData mapData, int startIndex)
    {
        // Currently resolves assignments but doesn't handle label creation
        // Needs to work with new observation manager
    }
}
```
</file_package>

<file_package path="BabySharkBot/Managers/CcaManager.cs">
```csharp
// CCA frame lifecycle and handoff
public class CcaManager : IManager
{
    private readonly chrisCrossAppleSause _ccaService;
    
    public void OnFrame()
    {
        if (_settings.CcaMining)
        {
            // Currently only handles choreography, needs to add initial labeling
        }
    }
}
```
</file_package>

<file_package path="BabySharkBot/Setup/TeamLabelRegistrationHelper.cs">
```csharp
// Worker/team/mineral assignments
public class TeamLabelRegistrationHelper
{
    public void RegisterTeamLabels(ulong[] workerTags, string teamPrefix)
    {
        // Currently creates team labels during observation - needs to be moved
    }
}
```
</file_package>

<file_package path="BabySharkBot/MicroTasks/CustomMiningTask.cs">
```csharp
// Suppresses default Sharky mining labels
public class CustomMiningTask : MiningTask
{
    public override bool OnUnitDetected(UnitCommander uc, Unit u)
    {
        // Currently suppresses labels but doesn't handle team assignment
    }
}
```
</file_package>

<file_package path="BabySharkBot/Managers/BabySharkBuildManager.cs">
```csharp
// Build execution after CCA handoff
public class BabySharkBuildManager : IManager
{
    public void OnFrame()
    {
        // Currently executes builds, needs to work with new observation system
    }
}
```
</file_package>

<file_package path="BabySharkBot/Setup/WorkerLabelChainHelper.cs">
```csharp
// W-chain generation
public class WorkerLabelChainHelper
{
    public List<ulong> GenerateGreedyChain(Vector2Dto centerOfMass, int workerCount)
    {
        // Currently generates chain but doesn't handle team assignment
    }
}
```
</file_package>

<file_package path="BabySharkBot/Setup/InitialMapData.cs">
```csharp
// New-map generation engine
public class InitialMapData : IOngoingMapData
{
    public MawBaseLocationData GetNewMiningData()
    {
        // Currently calculates COM and generates W-chain, needs to integrate with new labeling system
    }
}
```
</file_package>

<file_package path="BabySharkBot/Manager/WorkerLabelChangedEventArgs.cs">
```csharp
// Legacy singular directory for event args
public class WorkerLabelChangedEventArgs : EventArgs
{
    // Currently used for label changes - needs to be updated for new system
}
```
</file_package>
