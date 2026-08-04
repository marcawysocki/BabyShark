### TASK DECOMPOSITION

1. **Analyze Current Greedy Chain Implementation**
   - Examine `BabySharkMiningManager.cs` to understand current worker assignment logic
   - Identify where COM calculation and greedy sorting occurs
   - Locate the anchor worker determination (W3 vs W4)

2. **Implement Color Team Assignment Logic**
   - Add color assignment rules for M[0-7] based on the provided mapping
   - Handle both 8-worker and 12-worker scenarios
   - Ensure proper assignment of colors: Green, Purple, Red, Orange with their respective team assignments

3. **Update Worker Label Service Integration**
   - Modify `WorkerLabelService` to support color team labels
   - Ensure crosshair visualization uses correct colors
   - Maintain backward compatibility with existing label system

4. **Test and Validate Implementation**
   - Verify COM calculation remains accurate
   - Confirm greedy chain ordering works with new anchor logic
   - Check that color assignments are properly applied to mineral patches

