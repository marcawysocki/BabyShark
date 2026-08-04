### REASONING CONTEXT

Looking at the codebase structure and the specific issue described, I need to analyze the worker mining behavior when starting with fewer than 12 workers (specifically 8-worker starts). The problem is that workers are not executing mining commands properly during initialization.

Key observations from the architecture:
1. `WorkerLabelService` handles worker labeling and tracking
2. `BabySharkMiningManager` orchestrates the greedy chain mining logic
3. The issue appears to be in the worker assignment/labeling phase where workers aren't receiving proper mining orders for 8-worker starts

The mathematical foundation shows that for 8-worker starts:
- Anchor Worker should be W3 (calculated as (Total Workers / 2) - 1)
- We need to ensure the greedy chain properly assigns workers M[0] through M[7]
- Color team assignments need to be updated according to the specified mapping

