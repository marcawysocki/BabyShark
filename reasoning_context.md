### REASONING CONTEXT

The user wants me to analyze their BabyShark StarCraft II bot codebase and identify necessary files for development tasks. The key context is:

1. **Current Task**: Update the greedy mineral ordering chain for color team assignment
2. **Architecture**: BabyShark uses Sharky framework with specific service/manager pattern
3. **Key Components**:
   - `WorkerLabelService` (tracks workers)
   - `CrosshairService` (visualization)
   - `BabySharkMiningManager` (orchestrates mining)
   - `InitialMapData` (generates map data)
   - `OrderedMainMinerals` (greedy ordering)

4. **Specific Change Needed**: Update greedy chain to assign colors based on worker pairs:
   - 8-worker: W3 anchor instead of W4
   - Color assignments: Green, Purple, Red, Orange for mineral pairs
   - For 12-worker: Teal, Salmon, Blue, Yellow

5. **Development Pattern**: 
   - Local AI analyzes code → passes to KimiK3 → returns files for "Coder" Accio Work's AI assistant
   - Must prioritize BabyShark custom scripts over Sharky framework files
   - Focus on implementation files, not base framework dependencies

