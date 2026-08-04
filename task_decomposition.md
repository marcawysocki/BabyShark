### TASK DECOMPOSITION

1. **Namespace Standardization**: Convert all KimiK3.md references from BabyShark to BabySharkBot namespace
2. **Directory Structure Mapping**: Create missing Workers/ and ColorTeams/ directories under BabySharkBot/
3. **WorkerLabelService Implementation**: Extract standalone WorkerLabelService from BaseDtos.cs with int-based API for compatibility
4. **GreedyChainColorTeamAssignment Creation**: Implement new color team assignment logic under BabySharkBot/ColorTeams/
5. **Mining Assignment Fix**: Investigate and patch OngoingMapData.ResolveTeamAssignments() to prevent enemy base targeting

