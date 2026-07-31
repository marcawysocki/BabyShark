# BabyShark Naming Conventions

## Domain Metaphors
Some service names use playful/domain metaphors. These are **functional production code**.
- `chrisCrossAppleSause` = worker initialization (sitting cross-legged / attention)
- `PupusPistrixVectatorPestium` = Documentation and prompt collection for StarCraft II bot development.

Always check XML `<summary>` comments before interpreting a filename as a placeholder.

## File Categories
- `Managers/` — Stateful orchestrators (e.g., `BabySharkMiningManager`)
- `Services/` — Stateless logic (e.g., `JitPrepositionService`, `chrisCrossAppleSause`)
- `MicroTasks/` — Unit-level behaviors (e.g., `TeamPatchMiningTask`, `CustomMiningTask`)
- `Setup/` — Map data, initialization, DTOs (e.g., `InitialMapData`, `BaseDtos.cs`)
