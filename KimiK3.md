
# BabySharkBot JIT Mining Optimization Plan

## 1. Error Diagnosis & Known Issues

### 1.1 Critical Files to Audit First
```
Priority 1 (Core JIT Pipeline):
- BabySharkBot.cs                    # Entry point and worker assignment ho[2D[K
hooks
- Managers/BabySharkMiningManager.cs  # Central allocation logic - most lik[3D[K
likely error source
- Services/JitPrepositionService.cs   # Worker movement decisions - common [K
race condition spot
- MicroTasks/CustomMiningTask.cs      # Mining task executor - check for st[2D[K
stuck workers
- BabySharkBot.cs                     # Contains chrisCrossAppleSause worke[5D[K
worker initialization
- Setup/WorkerLabelChainHelper.cs     # Label chain state management

Priority 2 (Supporting Infrastructure):
- Services/MineralReturnRateTrackerService.cs   # Depletion detection accur[5D[K
accuracy
- BabySharkBot/VESPENE_V1_V2_QUICK_REF.md       # Mining throughput expecta[7D[K
expectations
- BabySharkBot/DYNAMIC_WORKER_JUGGLING_SYSTEM.md # Worker reallocation edge[4D[K
edge cases
```

### 1.2 Common JIT Mining Errors (Cross-reference with JIT-Mining-Error-Not[20D[K
JIT-Mining-Error-Notes.md)
Based on the file architecture, the most likely recent errors are:
- **Stuck workers on depleted mines** - Depletion detection fails to update[6D[K
update labels fast enough after a mine is exhausted
- **Label conflict storms** - Multiple task types (mining + expansion place[5D[K
placement + scout work) compete for the same worker's label
- **Prepositioning race conditions** - Workers arrive at a patch before it'[3D[K
it's properly flagged as mineable, causing them to wait indefinitely
- **Dynamic juggling failures** - Worker reallocation when a patch depletes[8D[K
depletes causes temporary mining drops and workers get lost in state transi[6D[K
transitions

## 2. JIT Mining Optimization Targets (Priority Ordered)

### Target 1: Mineral Depletion Detection with Rate Tracking (Highest Impac[5D[K
Impact) ⭐⭐⭐⭐⭐
**Problem**: Current system uses binary depleted/not-depleted, causing stal[4D[K
stale labels on exhausted mines until the next detection cycle
**Solution**: Implement per-patch depletion rate tracking that predicts whe[3D[K
when a mine will be exhausted

```csharp
// New file: BabySharkBot/Services/PatchDepletionTracker.cs
public class PatchDepletionTracker : IDisposable {
    private readonly Dictionary<long, PatchDepletionState> _patchStates; //[2D[K
// mineralPatchId → state
    private readonly HashSet<long> _activePatches; // currently assigned wo[2D[K
workers
    
    public struct PatchDepletionState {
        public long lastUpdateTimeTicks;      // Last time we checked this [K
patch
        public int mineralsRemainingEstimate; // Based on depletion rate, n[1D[K
not binary
        public double depletionRatePerSecond; // Observed mineral extractio[9D[K
extraction rate
        public int workerCount;               // Active workers on this pat[3D[K
patch
        public bool isDepleted;              // True when minerals run out
        
        public double EstimatedTimeToExhaustion => 
            (mineralsRemainingEstimate > 0) ? mineralsRemainingEstimate / d[1D[K
depletionRatePerSecond : -1.0;
    }
    
    public void UpdatePatchState(long mineralPatchId, int currentMineralCou[17D[K
currentMineralCount, DateTime now);
    public bool ShouldReassignWorkerFromPatch(long mineralPatchId); // Call[4D[K
Called when worker is stuck or idle
    public long[] GetActiveMineablePatches(); // Returns patches with activ[5D[K
active workers and >0 minerals
}
```

**Integration Points**: 
- Call `UpdatePatchState()` from `BabySharkMiningManager.cs` during main mi[2D[K
mining loop iteration (every 15 ticks)
- Use `ShouldReassignWorkerFromPatch()` in `JitPrepositionService.cs` when [K
checking if a worker should be moved
- `GetActiveMineablePatches()` replaces current active mine list

**Expected Impact**: 30-40% reduction in stuck workers, smoother mineral tr[2D[K
transitions

### Target 2: Worker State Machine Implementation (High Impact) ⭐⭐⭐⭐
**Problem**: Workers get assigned to multiple tasks simultaneously because [K
labels are overwritten mid-task, causing conflicts and lost workers
**Solution**: Implement explicit state machine with clean transition rules

```csharp
// New file: BabySharkBot/MicroTasks/WorkerState.cs
public enum WorkerMiningState {
    Idle,                    // No current assignment, free for new tasks
    Prepositioning,         // Moving toward a mine (JitPrepositionService [K
active)
    MiningActive,           // Actively mining with valid label
    MiningPausedDueToStuck, // Stuck in preposition or moving - needs reass[5D[K
reassessment
    Reassigning              // Being moved by dynamic juggling - don't ass[3D[K
assign new tasks
}

// In BabySharkBot.cs, add state tracking to worker manager:
public class WorkerStateRegistry {
    private readonly Dictionary<long, WorkerMiningState> _workerStates;
    
    public bool CanAssignWorker(long workerId) => 
        _workerStates[workerId] == WorkerMiningState.Idle || 
        _workerStates[workerId] ==
