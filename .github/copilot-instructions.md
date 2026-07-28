# Copilot Instructions

## Project Guidelines
- Do not modify any files in directories whose name starts with Sharky; only edit New Baby Shark files. The Sharky code is restored from the upstream Sharky repo and must remain untouched.
- Do not modify any S2ClientProtocol files.
- Do not modify InitialMapData.cs; it is untouchable. The user restored it from backup and reverted prior changes, so future fixes must avoid touching InitialMapData.cs. Greedy label assignment and related debug label code do not belong in InitialMapData anymore. **InitialMapData should not check for existing processed data or crosshair state; when it runs, that processed data does not exist yet and crosshair handling must not depend on it.**
- Treat the Sharky repository as an example reference only; BabyShark should not be forced to hook into Sharky's default role or commander semantics.
- BabyShark worker/resource path should remove Sharky dependencies entirely; just-in-time mining should own all worker commands including gas/return behavior. `ProcessVisibleUnits` may be a separate observation concern if it replaces commander-like behavior; keep current label names and team mappings fixed. First implementation focus is the first three seconds of the game.
- BabyShark should make a direct copy of Sharky's UnitManager behavior for observation, army defense, and offense, then adapt worker roles later for just-in-time mining. **BabyShark's new unit manager should be a complete replacement of Sharky's, preserving debug drawing and death tracking, while ProcessVisibleUnits must supply observed data and initial worker roles as team assignments for mining and chrisCrossAppleSause.**

## General Implementation Guidelines
- Prefer simple implementations over elaborate systems; preserve existing working logic and avoid inventing fallbacks when fixing label replay or label-related issues. Avoid unnecessary testing logic when a straightforward solution will work. Use the same existing rules; do not invent new rules or alternate ordering or labeling logic when fixing replay behavior. Do not fabricate labels; use only labels derived from stored or observed data, and preserve existing working logic when fixing replay/draw paths.
- Prefer simple if/else implementations and avoid introducing helper methods or extra abstractions when a direct change will do.
- Use MemoryPack's SerializeAsync/DeserializeAsync with Stream/FileStream to persist bot data to .dat files; do not use JSON or raw file copies for save/load operations. Use stream-based async I/O for saving and loading. When running ladder/live builds, write BaseDtos .dat files to the live executable directory (the deployed/run-time binary location), not only the Debug/bin working directory; detect and prefer the process executable path so persistence works in both Debug and live runs.
- Do not hardcode expected map results for mining assignments; preserve Near/Large/Far mineral logic and make assignments work for any map. Initial worker W1-W12 labels should be drawn in white.
- Ensure the custom mining cycle remains within the BabyShark mining loop and does not revert to Sharky build logic; avoid changes that would cause the task to stop and hand control back to default Sharky behavior. The mining test must keep running visibly until fixed; do not pause, abandon, or revert to Sharky build behavior when the custom mining loop breaks.
- For the mineral learning manager, only BabyShark code should be touched; Sharky must remain unchanged. Key learning to the current spawn XY coordinates, not only by the engine's start index. If a previously saved start index maps to a different XY, store it as a separate start location. Only reuse saved data when the same XY is seen again. Any data collected while an enemy is in sight is invalid and must not be saved. The learning manager should cancel any mining so no minerals are collected during the test.
- Trigger a one-time expensive first-spawn calculation per start location when apiLoc (spawn XY) matches a recorded start location.
- Determine worker ordering W12..W1 using a greedy nearest-neighbor chain anchored at W12 for initial calculation and replay: W11 is the worker nearest to W12, then choose each subsequent worker as the one nearest to the previously chosen worker. Do not sort workers by distance from the COM or use a farthest-first rule.
- Label minerals relative to worker positions and compute worker->team, mineral, and vespene labels during that first-spawn calculation.
- Store the resulting labels keyed by the start-location XY/COM; do not copy labels across different start locations. Use W12 through W1 as the constant worker order across spawns; for resources, labels are keyed by X/Y, and if the base has been played, W12 through W1 final labels should already be known.
- On future spawns for the same start XY, reuse the stored per-worker/team/mineral/vespene labels and only refresh tag IDs as needed.
- When asked to proceed on the mining/RLMatrix task, only make changes that directly implement the requested learning-service behavior. Do not modify InitialMapData.cs. Focus debugging and fixes on SecondaryMapData/OngoingMapData and runtime replay/draw paths when investigating worker label problems. Avoid unrelated changes.
- On startup, first load or wait for serialized map data as the first executable work after resolving the map name. Check a startup-ready boolean: if the serialized file is missing or not readable by the time game connection initializes, set `Settings.MapDataLoaded` to false and the bot should fall back to running InitialMapData rather than waiting further; after 2-5 seconds of game-connection startup, failure to read the file is treated as a system problem the bot cannot resolve. Then run greedy worker labeling W12 through W1 exactly once. Ensure that the mining manager uses visible raw observation units for X/Y and unit IDs. Spawn-specific team information must be reused across replays, and townhall X/Y must be kept exact if it differs from initial map data.
- Start the map-data loading thread in Program.cs after the map name is known, ensuring it completes before OnStart runs; OnStart should not wait, as it should execute a few seconds after the thread has finished. Move the current OnStart startup logic into BabySharkBot/BabySharkAI.
- Do not use Globals.CurrentMapData as a fallback for BabySharkMiningManager startup; after the InitialMapData path, source startup labeling data from observed visible units.
- When using stored COM, do not add null/availability checks; use it directly.
- Use observedMinerals (or similar) for the raw temporary mineral list; reserve M8-to-M1 for the interim greedy label ordering.
- Keep debug drawing information enabled and visible when working on mining label flow so correctness can be verified.
- Do not pass observation through the startup path overloads when they are only meant to set current spawn COM and played-state; keep the runtime path simple and direct.
- In the mining label flow, always establish the worker identities W12 through W1 first from worker positions; team and final labels are derived afterward and do not need to be recomputed on replay once established.
- In the observation loop, always collect the worker list; if the base has been played, use resource X/Y plus unit ID to refresh the final label; otherwise, add the resource to a list instead. If the base has not been played, build mineral and vespene lists for processing outside the observation loop.
- Keep logic only in the file/line the user explicitly specifies; avoid moving or adding related logic in other files unless the user asks.
- Use `if (!Settings.CurrentBaseHasBeenPlayed)` as the single gate for team logic in the visible-units flow; do not spread that logic into other files unless explicitly instructed. Keep team logic owned by `ProcessVisibleUnits.cs`; avoid creating or moving team-label logic into other helpers unless explicitly asked.
- For cca bumping, use the shared line target only when the bump flag is true; when false, each worker should move to its individual target based on its label. Apply fallback target rules for ccaMining to all workers, not just T1; T1 was only an example and should not be special-cased unless explicitly requested. Bumping turns off based on the difference in distance between the two workers in a pair, not worker-to-mineral distance; if the pair drifts too far apart or bumping ceases to be effective on later 5-frame checks, the bump flag becomes false.
- In `ProcessVisibleUnits`, create labels on `OnStart` using the user's rules instead of Sharky rules, then continue observing raw data every frame and only create labels for existing units that do not already have a label.
- BabyShark worker roles and behavior should be different from Sharky’s default roles; the bot should not assume Sharky mining/role semantics when assigning or acting on workers.
- Collecting resources should not be connected to Sharky in any way; resource collection logic should be owned entirely by BabyShark.

## Mining Assignment Instructions
- For mining assignment changes, use the phase-based worker label system:
  - Team 1 workers T1-T3 map to Minerals 1-2 (TA/TB).
  - Team 2 workers S1-S3 map to Minerals 3-4 (SA/SB).
  - Team 3 workers B1-B3 map to Minerals 5-6 (BA/BB).
  - Team 4 workers Y1-Y3 map to Minerals 7-8 (YA/YB).
- Near and Large minerals use final designation A, Far minerals use final designation B. Worker labels are assigned in phases from W12 down to W1, with Phase 1 selecting workers closest to near minerals and Phases 2-4 assigning the remaining team labels. Mineral ordering is anchored from W12 so Mineral 8 is closest to W12 and subsequent minerals are ordered backward from that anchor.
- Emit console logs like 'Worker Initial Mining Assignment: W3 has been changed to T1'.
- For mining label visualization, use the mineral band colors (T/M/B/Y) rather than Near/Far colors when drawing point markers.
- Persist and replay start-location worker/team assignments using W1-W12 identity and known start-location COM rather than recalculating from scratch each game. Expand StartingUnits to carry preloaded per-worker team information for each start location so assignments persist without recalculation. Trigger a one-time expensive first-spawn calculation per apiLoc (start XY) to compute worker ordering using a greedy nearest-neighbor chain anchored at W12 (W11 nearest to W12, W10 nearest to W11, etc.), label minerals and vespene relative to worker positions, store the resulting labels keyed by start XY, and on subsequent spawns for that same XY reuse the stored labels while only refreshing tag IDs. Do not copy labels across different starts.
- For mining cycle scoring, rank the fastest total time for all 12 workers, with higher weight on Near minerals, then Large minerals, then Far minerals. Use a weighted time score where Large is multiplied by 1.25 and Far by 1.75, then sum across all 12; lowest score wins.
- For mining label assignment, Teal should use W2 as T2 when Mineral 1 is Far; otherwise W4 should be T2.

## Overlord Scouting Instructions
- For overlord scouting in BabySharkBot, do not send the overlord to random spots or non-COM base locations; it should go directly to provisional expansion COM points in a deterministic order.
- Do not resort collections after the fact; preserve the original intended order and fix ordering logic at the source.

## Larva Labeling Instructions
- For larva labels, use a simple Leo + index label and increment the index; do not add extra less-than checks or guard logic unless required.

## User Preferences
- Ground answers only in actual workspace data; do not invent nonexistent or inferred data.
- Make BabySharkMiningManager OnFrame-only.
- TeamLabelRegistrationHelper should not run before ProcessVisibleUnits; initial worker labels must exist before team labels are applied, because team labeling cannot rely on information that does not yet exist.