Wall Scouting / Overlord Scouting Dependencies

Goal:
- Create a scouting manager for overlord scouting that can handle Wall Scouting, COM scouting then transition into normal game scouting.
- Keep all new functionality in BabySharkBot.
- Do not modify Sharky or SharkyExampleBot. 
- Use BabySharkBot services/managers/tasks for any scouting or wall-provisional logic.
- First objective is suspected mineral wall scouting. Then progress to COM of expansions where the minerals resources are unknown do to fog of war.
- No two Overlords should be sent to the same point. If a point is already being scouted, the next nearest point should be selected.

- BabySharkBot.BabySharkAI
  - Creates and wires BabyShark services.
  - Owns the live ProvisionalExpansionService instance.
  - Passes the service into the underlying bot through ProvisionalExpansionComSource.

- BabySharkBot.Services.ProvisionalExpansionService
  - Stores provisional expansion COM targets.
  - Stores suspected wall COM targets when enabled.
  - Provides GetProvisionalScoutPoints() for scouting targets.
  - Provides MarkScoutComplete(point) when a scout reaches a target.

- BabySharkBot.Setup.InitialMapData
  - Generates expansion candidates.
  - Registers provisional expansion points and suspected wall points.
  - Must be the source of COM coordinates used for scouting targets.

- BabySharkBot.Managers.BabySharkMiningManager
  - Initializes map data.
  - Calls ProvisionalExpansionService.Initialize(_mapData).
  - Keeps BabyShark map data aligned with the provisional expansion service.

- Sharky.DefaultBot.DefaultSharkyBot
  - Underlying Sharky bot used by BabySharkAI.
  - Should only be used as the backing infrastructure.
  - BabyShark-specific logic should not be added here.

- Sharky.MicroTasks.Scout.OverlordScoutTask
  - Current Sharky overlord scout behavior.
  - If kept, it must not be expanded with new Sharky logic.
  - BabyShark replacements should live in BabySharkBot instead.

Important data flow:
1. InitialMapData builds COM / wall candidate data.
2. ProvisionalExpansionService stores the candidate points.
3. BabySharkAI wires the service into the active bot.
4. Scout logic reads provisional points from the service.
5. Scout logic sends overlords to provisional COM targets first.
6. When the scout reaches the point, MarkScoutComplete(point) is called.

Notes:
- Suspected wall scouting must be optional and separate from normal overlord scouting.
- The overlord scout should use BabyShark-owned routing if Sharky behavior is not suitable.
- Keep Sharky untouched; add new scout routing in BabySharkBot services/tasks/managers only.
