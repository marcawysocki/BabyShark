### FILE INDEX

Based on the architecture and current development task, I need to examine:

**Core Implementation Files:**
- `Services/WorkerLabelService.cs` - Worker tracking and labeling logic
- `Managers/BabySharkMiningManager.cs` - Mining orchestration and greedy chain implementation
- `Setup/InitialMapData.cs` - Map initialization and worker data collection
- `MicroTasks/` directory - Contains mining task implementations

**Configuration Files:**
- Color team assignment mappings (likely in a configuration or setup file)

**Critical Analysis Questions:**
1. How does the system differentiate between 8-worker vs 12-worker start scenarios?
2. Are there specific conditions that prevent mining commands from executing for fewer workers?
3. Is the greedy chain logic properly handling the reduced worker count?

