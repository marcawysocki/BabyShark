### TASK DECOMPOSITION

1. **Analyze current labeling implementation** - Find where team labels are currently assigned during observation
2. **Identify chrisCrossAppleSause integration points** - Determine how to move initial labeling there
3. **Design new observation manager** - Create a manager that handles post-handoff observations without creating team labels
4. **Update handoff mechanism** - Ensure proper manager replacement and label cleanup
5. **Validate mineral assignment logic** - Ensure greedy chain assignments remain correct after refactoring

