# SpeedMiningTask Perfect Prompt for Hallucination Free Code

**Title:**  
Implement `SpeedMiningTask` for optimized frame‑0 worker assignment and push‑acceleration.

**Repository / Context:**  
- Repo: `C:\Users\marca\source\repos\Sharky\PupusPistrixVectatorPestium`  
- Language/runtime: `C# 13`, `.NET 9`, IDE: `Visual Studio 2026`

**Goal (explicit success criteria):**  
Translate pseudocode derived from Copilot Vision + SpeedMining.MD into Sharky‑compatible C# code. Implement a `SpeedMiningTask` that:  
- Pre‑splits 12 workers at frame 0.  
- Assigns 8 primaries to close mineral patches.  
- Uses 4 push workers for controlled acceleration.  
- Applies predictive gather buffering and angle optimization.  
Must compile, integrate into Sharky bot, and achieve expected mineral benchmarks (240–260 minerals by 1:00).  

**Inputs:**  
- Function signatures: `IssueOrder`, `QueueOrder`, `GetAssignedPatch`, `ComputeBestEntryAngle`.  
- External APIs: Sharky services only (`ActionService`, `BaseData`, `MapData`).  
- No new NuGet packages allowed.

**Outputs / Artifacts:**  
- No unit tests required initially, but integration must not break existing tests.

**Constraints & Non Goals:**  
- No new NuGet dependencies.  
- Must support `.NET 9`.  
- Do not alter unrelated bot modules.  
- Security/privacy constraints: none beyond repo conventions.

**Needed Domain Knowledge / Types / Symbols:**  
- `DefaultSharkyBot.CreateBot()`  
- `MicroTaskData`  
- `ActionService`  
- `BaseData`, `MapData`  
- Worker assignment logic from Sharky core.

**Style & Conventions:**  
- Follow Sharky naming conventions.  
- Use tabs/spaces consistent with repo.  
- Handle errors by logging or ignoring gracefully (no exceptions thrown unless critical).  

**Validation & Tests:**  
- Run `dotnet build` → must succeed.  
- Run `dotnet test` → all existing tests must pass.  
- Optional: measure minerals at 1:00 in debug runs.

**Edge cases & Performance:**  
- Handle fewer than 12 workers gracefully.  
- Avoid deadlocks if patches are already saturated.  
- Ensure frame‑accurate logic does not stall bot loop.

**Deliverables:**  
- `SpeedMiningTask.cs` with full implementation.  
- `Program.cs` updated to register task.  
- Short changelog comment in each modified file.

**Clarifying questions:**  
1. Should push‑acceleration be configurable (on/off) via settings?  
2. Should predictive gather buffering remain active after saturation?  
3. Do we need unit tests for worker assignment, or is integration testing sufficient?

**Tone & Detail:**  
- Terse, idiomatic C#.  
- Inline comments only where logic is non‑obvious (frame‑accurate sequences).  