**Sharky processes raw SC2 observations into labeled game objects, then routes those labeled units into modular Commander components which produce `SC2APIProtocol.Action` objects; your code must be wired into Sharky’s observation → labeling → commander registration → action pipeline to have roles and commanders take effect.**  

### How Sharky handles observations and unit roles
**Raw observation ingestion:** Sharky consumes the SC2 API observation snapshot (the `Observation`/`RawData.Units` feed) and filters units by visibility/position before any higher‑level processing. **Actions are emitted as SC2API `Action` objects (e.g., `ActionRawUnitCommand`) that the framework sends to the game.**   [blizzard.github.io](https://blizzard.github.io/s2client-api/)  [Github](https://github.com/sharknice/Sharky)

**Labeling and role assignment:** Sharky separates concerns: a *detection* phase converts raw units into DTOs (workers, buildings, neutrals), a *labeling* phase maps persistent labels (worker IDs, mineral IDs, team patches) onto those DTOs, and a *role/assignment* phase groups labeled units into roles or teams that Commanders will act on. The framework keeps map data and label services centrally so commanders can resolve a live unit tag back to a role name.   [Github](https://github.com/sharknice/Sharky)

**Commanders and action generation:** Sharky uses modular commander components that run each frame (or on a schedule), read the labeled unit state, compute decisions, and return `Action` objects to the engine. Commanders are the place where *roles* (e.g., miner, scout, micro controller) are implemented and where unit tags are translated into concrete move/ability commands.   [Github](https://github.com/sharknice/Sharky)  [blizzard.github.io](https://blizzard.github.io/s2client-api/)

---

### Why your code may not be connecting to Sharky commanders/roles
**Missing registration in Sharky’s pipeline.** If your processing produces `WorkerEntryDto` objects but you never register the services or DTOs with Sharky’s central module/commander manager, Commanders won’t see them. **You must register label services and any DTO providers with the bot’s module/commander system.**   [Github](https://github.com/sharknice/Sharky)

**Not exposing labeled state to commanders.** Commanders expect a shared state or service they can query each frame (map data, label services, worker lists). If your code only returns local lists and doesn’t update the shared services or global state, commanders can’t resolve labels to live unit tags.

**No commander implementation or missing hook to return actions.** Creating movement logic (e.g., building `ActionRawUnitCommand`) is not enough unless that logic runs inside a Commander registered to Sharky’s update loop and returns its `Action` list to the framework for submission.   [Github](https://github.com/sharknice/Sharky)  [blizzard.github.io](https://blizzard.github.io/s2client-api/)

**Timing / first‑play map-data gating.** Sharky often gates label registration and team assignment on map data and settings (first-play flags, spawn index). If your code only runs under certain `Settings` conditions, it may never populate labels for commanders to use.

---

### Practical checklist to connect your code to Sharky
1. **Register your services** (worker/mineral/vespene label services) with Sharky’s service/module registry.   [Github](https://github.com/sharknice/Sharky)  
2. **Expose the worker DTOs to shared state** (so commanders can query them each frame).  
3. **Implement or extend a Commander** that reads the shared DTOs and returns `SC2APIProtocol.Action` objects each frame.   [Github](https://github.com/sharknice/Sharky)  [blizzard.github.io](https://blizzard.github.io/s2client-api/)  
4. **Ensure map data and Settings flags** are set so label registration runs on first play.  
5. **Add logging and frame‑by‑frame traces** to confirm commanders receive the labeled units and that actions are being returned.

If you want, I’ll produce a short patch-style plan showing exactly where to call your `ProcessVisibleUnits` results inside Sharky’s commander registration and a minimal Commander skeleton that returns a move `Action` for a labeled worker.