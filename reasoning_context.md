This is an extremely detailed, multi-layered spec that needs careful decomposition before I can start designing code changes. The user has given me a complete architecture overview of their BabyShark bot and now wants a new "Phase 1" overhaul focused on **worker count-aware team mapping** with specific color transitions, pink helper workers (13-15), and speed mining rules at 16 workers.

Let me break this down into the key implementation requirements:

## Key Changes Required
### 1. BuildIne.cs Morphing Logic Overhaul
- Remove the 8-worker check (`if (state.WorkerCount < 12)`) for morphing a drone on frame 0
- New logic: `desiredCount > currentWorkerCount && hasLarvaAvailable` and minerals are >- 50 Morph a drone
- Each new drone morphed needs a team assignment based on current worker count using the mapping table

### 2. Team Color Mapping Service (New or Extended)
Need a `TeamColorService` that handles:
- **8-worker colors**: Green(0,255,0), Purple(128,0,128), Orange(255,165,0), Red(255,0,0)  
- **12-worker colors**: Teal(0,128,128), Salmon(255,128,128), Blue(0,0,255), Yellow(255,255,0)
- **Pink transition color** for workers 13-15: (255,192,203) or similar pink
- Mapping function based on worker count → team index

### 3. TeamLabelRegistrationHelper.cs Updates  
Need to add methods for:
- `RegisterNewWorkerWithTag(tag, teamColor, mineralPairIndex)` - assigns correct label (T1/T2/etc.) and color when a new drone morphs
- The mapping table logic that determines which team the worker joins based on current worker count
- Support for 8-worker → 12-worker color transitions

### 4. Multi-Mineral Pair Reassignment Logic
When a new worker is created (via morph), it needs to:
- Find the appropriate mineral pair based on worker count
- Reassign existing workers and mineral pairs to the correct team colors
- Update `WorkerLabelService` with new labels for all affected workers

### 5. BaseDtos.cs New Fields
Need new DTOs for:
- `ExtractorTrickData`: Team that performs trick, shortest two-point path info, 125-mineral target
- `TeamTransitionInfo`: For each mineral pair (0-3), track current color (8 or 12 worker) and which team workers are assigned
- Possibly new `WorkerColorMapping` struct

### 6. 4-Worker Speed Mining Rules  
When worker count reaches 16, the 4th worker in each team switches to speed mining rules:
- T4 mines TB → switches back after AB
- Y4 mines YB → switches back after BB
- S4 mines SA → switches back after BA
- B4 mines BB → becomes Speed Mining

### 7. Extractor Trick Service (New or Enhanced)
Need a `TeamPatchMiningTask` update for:
- Finding team closest to Vespene Geyser (V1/V2 based on BaseDtos)
- Shortest two-point path calculation
- Worker morphing into extractor at exactly 125 minerals
- Canceling and returning worker to mining

### File Structure I Recommend
```
Managers/
Services/
 ├── TeamColorService.cs                    [NEW] - Color mapping logic (8 vs 12 worker colors)
 ├── TeamLabelRegistrationHelper.cs         [EXISTS, needs update]
 └── MultiMineralReallocator.cs             [NEW] - Mineral pair reassignment on worker creation
MicroTasks/
 └── TeamPatchMiningTask.cs               [UPDATE] - Add extractor trick logic
Setup/
 ├── BaseDtos.cs                            [UPDATE] - New DTOs for extractor trick, team transitions
 └── InitialMapData.cs                    [UPDATE] - New field: OrderedMainMinerals with team indices
```

### Questions I Need Clarified Before Coding
1. **`desiredCount` parameter**: Since it's hardcoded per build, should this be a static constant in each BuildTest.cs file or a method parameter? I recommend the latter for flexibility.
2. **Color RGB values**: The user mentions Green(0,255,0), Purple(128,0,128) etc., but what exact RGB values for Pink (transition)? Suggest (255,192,203) or (255,105,180).
3. **TeamLabelRegistrationHelper.cs**: This is an existing file - should I extend it with new methods or create a new `TeamColorService` alongside it?
4. **TeamTransitionInfo persistence**: When does the 8→12 worker color transition happen? Is it permanent once triggered (i.e., even if workers die and respawn, keep 12-worker colors)? Or is it dynamic based on current count? I recommend: **permanent once triggered** - the first time a team reaches 3 workers in an 8-worker start, that team permanently becomes Teal/Salmon/Blue/Yellow.
5. **Pink worker movement**: The spec says "multi team helpers" but doesn't specify their mining behavior - should they mine both mineral pairs simultaneously or alternate? I recommend: **Alternate** between the two minerals based on current position (same as 3-worker teams).
6. **Extractor trick color**: Which team color do we use for the worker performing the trick? The spec says "team closest to Vespene Geyser" - should this be their normal team color or a special purple/green hybrid? I recommend: **Their normal team color** (e.g., if it's Green team, they stay green during the trick).

### My Recommendation for Implementation Order
1. Phase 1a: Create `TeamColorService.cs` with all mapping constants and methods
2. Phase 1b: Update `BaseDtos.cs` with new DTOs for extractor trick data  
3. Phase 1c: Update `TeamLabelRegistrationHelper.cs` with team assignment logic
4. Phase 1d: Modify `BuildIne.cs` to remove 8-worker check and use new mapping
5. Phase 1e: Update `MultiMineralReallocator.cs` for mineral pair reassignment
6. Phase 1f: Add extractor trick service (TeamPatchMiningTask updates)
7. Phase 1g: Test with both 8 and 12 worker starts

### What I Need From User Before Starting
- Confirm the RGB values for Pink (transition color) - suggest (255,192,203) or let me pick a nice pink
- Confirm whether the 8→12 worker color transition is **permanent** once triggered
- Decide where `desiredCount` comes from: static constant in BuildTest.cs OR method parameter? I recommend method parameter for flexibility
- Confirm if Pink workers should alternate between mineral pairs (current behavior) or have different movement rules

Once you confirm these 3 points, I can start generating the actual code files and modifications. This is a major architectural change so I want to make sure we get it right before writing any code.
