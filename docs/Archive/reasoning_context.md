### REASONING CONTEXT

The issue is that team labeling (Teal/Salmon/Blue/Yellow) is currently happening during the observation loop, which violates the requirement that "team labels should be done in chrisCrossAppleSause and only on Frame Zero." The handoff mechanism needs a new manager for observations to ensure labels are only created when new units appear (larva, workers, army units).

Key structural problems identified:
1. Team labeling logic is scattered across observation routines
2. No clear separation between initial labeling (frame zero) and ongoing labeling (new unit creation)
3. The chrisCrossAppleSause service needs to own the initial team assignment process
4. A new manager is required for post-handoff observations that only creates labels for newly spawned units

