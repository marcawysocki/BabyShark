# INSTRUCTIONS FOR KIMI-DESKTOP (KIMI K3 CONTEXT)
You are Kimi-Desktop. Review the pre-processed code architecture layout metrics compiled by our local 48B engine.
YOUR TASK: Output a deployment-ready 'Accio-Desktop.md' artifact folder block containing precise, complete C# code replacements for the Accio 'Coder' assistant. Implement the 8-worker W3 calculation anchor logic and the color team team matrices correctly.

## LOCAL PRE-PROCESSING ANALYTICS
## BabyShark Codebase Analysis for Greedy Chain Color Team Assignment

### Current Development Task: Update Greedy Chain for Color Team Assignment

Based on the architecture analysis and the specific task requirements, here are the necessary files to execute this development:

---

### **Critical Implementation Files**

1. **`Services/chrisCrossAppleSause.cs`** - *Primary file requiring modification*
   - Contains the worker initialization logic that needs to be updated
   - Implements the greedy chain algorithm for mineral ordering
   - Currently handles W3 as anchor point (needs update for 8-worker vs 12-worker scenarios)

2. **`Setup/InitialMapData.cs`** - *Supporting file*
   - Contains the `GetNewMiningData()` method that populates `OrderedMainMinerals`
   - May need updates to support the new color team assignment logic

3. **`Managers/BabySharkMiningManager.cs`** - *Integration point*
   - Uses the output from `chrisCrossAppleSause` for worker choreography
   - May require adjustments to handle the new color assignments

---

### **Supporting Files (Read-Only)**

4. **`Setup/BaseDtos.cs`** - *Data structures*
   - Contains `OrderedMainMinerals` class definition
   - May need property additions for color team information

5. **`Services/JitPrepositionService.cs`** - *Related service*
   - Could be referenced by the updated greedy chain logic
   - Provides pre-positioning utilities that may benefit from color assignments

---

### **Key Implementation Notes**

The current `chrisCrossAppleSause` service implements:
- Center of Mass calculation for 8 starting mineral nodes
- Worker assignment using W3 as anchor point (incorrect for 8-worker starts)
- Greedy chain sorting from furthest worker to nearest

**Required Changes:**
1. Update anchor point determination logic (W3 for 8-workers, W4 for 12-workers)
2. Implement color team assignments based on the provided mapping
3. Ensure proper mineral ordering with color team information
