Perfect Prompt for Hallucination Free Code

Title:
- One short sentence describing the task.

Repository / Context:
- Repo URL or local path (e.g., `C:\path\to\repo`).
- Files to inspect (list exact paths) and which file is active (e.g., `SharkyZergExampleBot\Program.cs`, `SharkyZergExampleBot\Builds\BuildTest.cs`).
- Language and runtime: e.g., `C# 13`, `.NET 9`, IDE: `Visual Studio 2022`.

Goal (explicit success criteria):
- What must be implemented, fixed or changed (one paragraph).
- Measurable acceptance criteria (compile, tests pass, performance target, behavior).

Inputs:
- Function signatures, sample inputs, or example data.
- External APIs or third‑party libs allowed (list exact NuGet packages + versions) or explicitly disallowed.

Outputs / Artifacts:
- Files to create/modify (exact file paths).
- Unit tests to add / update (exact test names).
- Expected console/return values or behavior.

Constraints & Non Goals:
- Compatibility constraints (e.g., no new NuGet, support .NET 9).
- Security/privacy constraints.
- What not to change.

Needed Domain Knowledge / Types / Symbols:
- Provide exact class/method/type names or paste small relevant snippets.
- If you expect me to use project symbols, list them (e.g., `DefaultSharkyBot.CreateBot()`, `MicroTaskData`).

Style & Conventions:
- Project style (tabs/spaces, naming conventions, trailing commas, async patterns).
- How to handle errors (throw, return Result, log).

Validation & Tests:
- Commands to run: e.g., `dotnet build`, `dotnet test`.
- Test cases and expected results.
- Linting/formatting steps (e.g., run __Format Document__ or `dotnet format`).

Edge cases & Performance:
- Worst‑case sizes, latency or memory constraints.
- Required behavior on failures/timeouts.

Deliverables:
- Exact set of changes, code snippets, and a short changelog comment for each modified file.
- If code is large, ask to produce only the modified files.

Clarifying questions:
- Request the assistant ask 2–3 clarifying questions if any ambiguity remains.

Tone & Detail:
- Prefer terse, idiomatic code and short explanations (or request detailed comments if wanted).

Example (filled for your repo):
- Title: "Add `GeyserWalkDebugTask` microtask and tests"
- Repo: `C:\Users\marca\source\repos\Sharky\PupusPistrixVectatorPestium` on `master`
- Files: `SharkyZergExampleBot\Program.cs` (active), `SharkyZergExampleBot\Builds\BuildTest.cs`
- Language: `C# 13`, `.NET 9`
- Goal: Implement `GeyserWalkDebugTask` in `SharkyZergExampleBot.Tasks` that toggles a debug mode and registers in `Program.cs`. Must compile and unit tests in `BuildTest.cs` must pass.
- Inputs: none (task is invoked by the framework). Use existing `Sharky` project APIs only.
- Outputs: new file `SharkyZergExampleBot\Tasks\GeyserWalkDebugTask.cs`, update `Program.cs` to register it, add tests in `BuildTest.cs`.
- Constraints: no new NuGet packages, adhere to existing naming and async patterns, must compile with `dotnet build`.
- Validation: run `dotnet build` and `dotnet test` and include commands and expected exit codes.
- Clarifying questions: “Should the task expose a config toggle in `appsettings.json`?” and “Which behaviors must tests assert: registration only or runtime action?”

Checklist before sending to the assistant:
- [ ] Provide exact file paths.
- [ ] Paste small snippets for any unclear APIs.
- [ ] State allowed/disallowed packages.
- [ ] Give acceptance tests or expected outputs.

Send me a filled template for your concrete task and I’ll produce compile‑ready code plus tests and a short changelog.