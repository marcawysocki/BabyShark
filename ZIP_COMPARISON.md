# Attached ZIP Comparison

Compared the attached archive **BabyShark (43) Working-W8 to W1- Minerals-M0 to M7 to TA TB SA SB BA BB YA YB labels and CCA commands for 8 worker .zip** with the current `BabyShark` worktree.

## Archive identity

The archive contains a complete repository, including its own `.git` directory and a nested `BabyShark_CCA_Working` copy. Its checked-out branch is `master` at commit `6da4025`; its `main` ref is `144bbbf`.

The current worktree is at a different commit and has a large uncommitted change set. No files were restored or modified by this comparison.

## Most important difference: worker chain

### Attached ZIP

`BabySharkBot/Setup/WorkerLabelChainHelper.cs` builds the traversal from the farthest worker from mineral COM:

```text
farthest worker -> next closest remaining worker -> ... -> final worker
```

For an 8-worker opening, the traversal labels the first worker `W8`, then `W7`, down to `W1`. The list remains in that traversal order.

### Current worktree

`BabySharkBot/Setup/WorkerLabelChainHelper.cs` builds the same far-end traversal, then reverses it before storage. The stored list is W1-first and labels are assigned `W1`, `W2`, ... `W8`.

That is a behavioral difference, not just a naming difference. Consumers that depend on list position see opposite ends of the worker chain.

## Most important difference: CCA worker-to-mineral pairing

### Attached ZIP

`BabySharkBot/Services/chrisCrossAppleSause.cs`:

```text
worker traversal position 0 = W8/W12
mineral target = mineralsByIndex[count - 1 - wi]
```

This means the far-end worker is paired with the high-end mineral:

```text
W8/W12 -> M[8]/M[7] depending on the notation in use
then downward through the mineral chain
```

The archive explicitly reverses the sorted mineral list for the worker traversal:

```csharp
var workerNumber = greedyOrder.Count - wi;
var targetIndex = mineralsByIndex.Count - 1 - wi;
```

### Current worktree

`BabySharkBot/Services/chrisCrossAppleSause.cs` currently stores workers W1-first and uses:

```csharp
var workerNumber = wi + 1;
var targetIndex = wi;
```

That pairs W1 with the low list position and W8 with the high list position. It is the opposite consumer convention from the attached snapshot.

## Most important difference: mineral index persistence

### Attached ZIP

The archive's `InitialMapData.GreedyOrderMinerals` walks a temporary chain from its anchor using closest remaining minerals, but stores the compatibility index in reverse order:

```csharp
displayIndex = orderedIndices.Count - orderIndex;
```

Thus the first element of the temporary traversal receives the high stored index, and the final element receives the low stored index. The CCA code then sorts by stored index and reverses the target position for the W8/W12-first worker traversal.

### Current worktree

The current `InitialMapData.GreedyOrderMinerals` stores:

```csharp
displayIndex = chainIndex + 1;
```

It also documents and implements a W1-side anchor as the start of the final mineral chain. That is not the same indexing direction as the attached snapshot.

## Team label logic

### Attached ZIP

The archive's `TeamLabelRegistrationHelper` uses the prior Near/Far-oriented pair labeling behavior. Its `ApplyMineralFinalLabels` selects a `nearMineral` and `farMineral`, then labels them as team A and team B. The archive also contains explicit worker-target logic for 8-worker assignments and CCA role behavior.

### Current worktree

The current worktree has subsequent changes in `TeamLabelRegistrationHelper.cs`, including a shared size-based A/B change. That change is not byte-identical to the attached snapshot and should not be treated as a safe preservation of the prior working team labels.

Because the user stated that Salmon, Blue, and Yellow were working, those team mappings should be preserved from the attached snapshot rather than regenerated through the current modified shared function.

## Conclusion

The attached ZIP is not merely documentation. It contains a prior source snapshot whose key behavior matches the earlier working direction:

1. Worker traversal starts at the farthest worker and labels that worker W8/W12.
2. Mineral target pairing reverses against the W8/W12-first worker traversal.
3. The temporary mineral walk and persisted mineral index use the opposite direction from the current W1-side implementation.
4. The current worktree has changed all three relevant areas: worker list orientation, mineral index direction, and CCA target pairing.

The ZIP is therefore a usable restoration baseline for the greedy-chain/CCA behavior, but it should not be copied wholesale over the current worktree without first preserving unrelated user changes. The safe next operation would be a targeted restore or a clean branch/worktree based on the ZIP snapshot, followed by the temporary-chain-to-high-end-anchor adjustment requested by the user.
