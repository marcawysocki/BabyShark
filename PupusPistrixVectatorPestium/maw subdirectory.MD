Add a directery and subfile link to this file in your MAW vault for easy access.

Title:
- New directory added 
- Help the user gather add this to his projext

Repository / Context:
- Repo URL or local path C:\Users\marca\source\repos\Sharky\PupusPistrixVectatorPestium
- Files to inspect (list exact paths) and which file is active 
- C:\Users\marca\source\repos\PupusPistrixVectatorPestium\Sharky\maw
- C:\Users\marca\source\repos\PupusPistrixVectatorPestium\Sharky\maw\Builds
- C:\Users\marca\source\repos\PupusPistrixVectatorPestium\Sharky\maw\Builds\BuildTest.cs`).

C:\Users\marca\source\repos\PupusPistrixVectatorPestium\Sharky\SharkyZergExampleBot\Program.cs & ZergBuildChoices.cs (active)
    
    Language and runtime: e.g., `C# 13`, `.NET 9`, IDE: `Visual Studio 2026`.
    
    [Contents of Microsoft Visual Studio or VS Code Help About pasted here]
    Visual C++ 2026   00529-30000-00000-AA473
Microsoft Visual C++ 2026

.NET MAUI Tools   18.0.345 (fbde6c01f0)
Visual Studio extension to enable .NET MAUI development.

.NET MAUI UI Tools   18.0.1.33 (remotes/origin/HEAD@44462085bc)
Visual Studio extension to enable .NET MAUI UI tools.

ADL Tools Service Provider   1.0
This package contains services used by Data Lake tools

ASA Service Provider   1.0

ASP.NET and Web Tools   18.0.338.63737
ASP.NET and Web Tools

Azure App Service Tools v3.0.0   18.0.338.63737
Azure App Service Tools v3.0.0

Azure Data Lake Tools for Visual Studio   2.6.5000.0
Microsoft Azure Data Lake Tools for Visual Studio

Azure Functions and Web Jobs Tools   18.0.338.63737
Azure Functions and Web Jobs Tools

Azure Stream Analytics Tools for Visual Studio   2.6.5000.0
Microsoft Azure Stream Analytics Tools for Visual Studio

C# Tools   5.0.0-2.25574.6+7f327e02b43a2486faa9cfdf0a1eae34c06e2d1a
C# components used in the IDE. Depending on your project type and settings, a different version of the compiler may be used.

Cookiecutter   17.0.25266.3
Provides tools for finding, instantiating and customizing templates in cookiecutter format.

GitHub Copilot   18.0.981.45449
GitHub Copilot is an AI pair programmer that helps you write code faster and with less work.

Markdown Editor v2   2.0.217
A full featured Markdown editor with live preview and syntax highlighting. Supports GitHub flavored Markdown.

Microsoft Azure Hive Query Language Service   2.6.5000.0
Language service for Hive query

Microsoft Azure Stream Analytics Language Service   2.6.5000.0
Language service for Azure Stream Analytics

Mono Debugging for Visual Studio   18.0.15 (76f6142551)
Support for debugging Mono processes with Visual Studio

NuGet Package Manager   7.0.2
NuGet Package Manager in Visual Studio. For more information about NuGet, visit https://docs.nuget.org/

Python - Profiling support   17.0.25266.3
Profiling support for Python projects.

Python with Pylance   17.0.25266.3
Provides IntelliSense, projects, templates, debugging, interactive windows, and other support for Python developers.

Razor (ASP.NET Core)   18.0.2.2551302+48a2659b91e7032c97ea024751a6ea9469a94d7f
Provides languages services for ASP.NET Core Razor.

SQL Server Data Tools   18.0.172.3
Microsoft SQL Server Data Tools

Test Adapter for Boost.Test   1.0
Enables Visual Studio's testing tools with unit tests written for Boost.Test.  The use terms and Third Party Notices are available in the extension installation directory.

Test Adapter for Google Test   1.0
Enables Visual Studio's testing tools with unit tests written for Google Test.  The use terms and Third Party Notices are available in the extension installation directory.

ToolWindowHostedEditor   1.0
Hosting json editor into a tool window

TypeScript Tools   18.0.1114.2009
TypeScript Tools for Microsoft Visual Studio

Visual Basic Tools   5.0.0-2.25574.6+7f327e02b43a2486faa9cfdf0a1eae34c06e2d1a
Visual Basic components used in the IDE. Depending on your project type and settings, a different version of the compiler may be used.

Visual F# Tools   18.0.0-rc2.25522.6+db81a081913bbaa32e528305723c07d98fd598b8
Microsoft Visual F# Tools

Visual Studio IntelliCode   2.2.3152.23957
AI-assisted development for Visual Studio.

VSPackage Extension   1.0
VSPackage Visual Studio Extension Detailed Info
    
Goal (explicit success criteria):
- The AI has permission to fail, ask the user for more information, and tell the user I don't know.
- The AI Must not fabricate code or use exambles from outside the Sharky repo for anything implimented.
- Use using "maw.Builds.Zerg;" the way "using Sharky.Builds.Zerg;" is used
Inputs:
- Function signatures, sample inputs, or example data.
- External APIs or third‑party libs allowed (list exact NuGet packages + versions) or explicitly disallowed.

Outputs / Artifacts:
- Files to create/modify (exact file paths).  Perfect Prompt for Hallucination Free Code Template.MD
- Unit tests to add / update (exact test names). N/A
- Expected console/return values or behavior. N/A

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

