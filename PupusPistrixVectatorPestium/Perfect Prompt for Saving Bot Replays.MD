
Title: Perfect Prompt for Bot Replays
- Save the replay for local play

Repository / Context:
- Repo URL or local path C:\Users\marca\source\repos\Sharky\PupusPistrixVectatorPestium
- Files to inspect (list exact paths) and which file is active 
    - `SharkyZergExampleBot\Program.cs`,    
    - 'C:\Users\marca\OneDrive\Documents\StarCraft II\Accounts\979875297\1-S2-1-13605814\Replays\Multiplayer\BotReplays\'
    - Posible 'C:\Users\marca\source\repos\Sharky\PupusPistrixVectatorPestium\S2ClientProtocol\'
    - or 'C:\Users\marca\source\repos\Sharky\'
- - Language and runtime: e.g., `C# 13`, `.NET 9`, IDE: `Visual Studio 2022`.
    - Microsoft Visual Studio Community 2022 
        Version 17.14.19
        VisualStudio.17.Release/17.14.19+36623.8
        Microsoft .NET Framework
        Version 4.8.09032

        Installed Version: Community

        ASP.NET and Web Tools   17.14.128.55757
        ASP.NET and Web Tools

        Azure App Service Tools v3.0.0   17.14.128.55757
        Azure App Service Tools v3.0.0

        C# Tools   4.14.0-3.25412.6+c167adef821b9ba2f1c44c227a5e79ca11876461
        C# components used in the IDE. Depending on your project type and settings, a different version of the compiler may be used.

        Cookiecutter   17.0.25079.8
        Provides tools for finding, instantiating and customizing templates in cookiecutter format.

        GitHub Copilot   17.14.1368.60722
        GitHub Copilot is an AI pair programmer that helps you write code faster and with less work.

        Markdown Editor v2   2.0.209
        A full featured Markdown editor with live preview and syntax highlighting. Supports GitHub flavored Markdown.

        Microsoft JVM Debugger   1.0
        Provides support for connecting the Visual Studio debugger to JDWP compatible Java Virtual Machines

        NuGet Package Manager   6.14.1
        NuGet Package Manager in Visual Studio. For more information about NuGet, visit https://docs.nuget.org/

        Python - Profiling support   17.0.25079.8
        Profiling support for Python projects.

        Python with Pylance   17.0.25079.8
        Provides IntelliSense, projects, templates, debugging, interactive windows, and other support for Python developers.

        TypeScript Tools   17.0.40502.2001
        TypeScript Tools for Microsoft Visual Studio

        Visual Basic Tools   4.14.0-3.25412.6+c167adef821b9ba2f1c44c227a5e79ca11876461
        Visual Basic components used in the IDE. Depending on your project type and settings, a different version of the compiler may be used.

        Visual F# Tools   17.14.0-beta.25230.7+c1a9d78ad4a474a716ccffb4367ed5151e3f7c9f
        Microsoft Visual F# Tools

        Visual Studio IntelliCode   2.2
        AI-assisted development for Visual Studio.

Goal (explicit success criteria):
- The AI has permission to fail, ask the user for more information, and tell the user I don't know.
- What must be implemented, fixed or changed (one paragraph).  Make this file the goto template.
- Measurable acceptance criteria (compile, tests pass, performance target, behavior). The template works for C# tasks for this Repo

Inputs:
- Function signatures, sample inputs, or example data.  if (args.Length == 0)


Outputs / Artifacts:
- Files to create/modify (exact file paths).  a game replay containing the Build_Map_Datestamp simular to '12pool_SwarmHost_AbyssalReefAIE_20251115_221236.SC2Replay'

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

Snippet from a python bot that would need an equivlent C# using Sharky Framework instead


async def join_ladder_game(
        host, port, players, realtime, portconfig, save_replay_as=None, step_time_limit=None, game_time_limit=None
):
    ws_url = "ws://{}:{}/sc2api".format(host, port)
    ws_connection = await aiohttp.ClientSession().ws_connect(ws_url, timeout=120)
    client = Client(ws_connection)
    try:
        result = await sc2.main._play_game(players[0], client, realtime, portconfig, step_time_limit, game_time_limit)
        if save_replay_as is not None:
            try:
                await client.save_replay(save_replay_as)
                logging.info("Replay saved to %s", save_replay_as)
            except Exception as exc:  # pylint: disable=broad-except
                logging.error("Failed to save replay to %s: %s", save_replay_as, exc)
        # await client.leave()
        # await client.quit()
    except ConnectionAlreadyClosed:
        logging.error(f"Connection was closed before the game ended")
        return None
    finally:
        await ws_connection.close()

    return result
