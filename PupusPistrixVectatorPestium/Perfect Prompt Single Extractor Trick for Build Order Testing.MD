
Title: Perfect Prompt Single Extractor Trick for Build Order Testing
- Build Order testing in Sharky Framework  

Repository / Context:
- Repo URL or local path C:\Users\marca\source\repos\Sharky\PupusPistrixVectatorPestium
- Files to inspect (list exact paths) and which file is active 
    - '..\SharkyZergExampleBot\Program.cs', 
    - '..\SharkyZergExampleBot\ZergBuildChoices.cs'
    - `..\SharkyZergExampleBot\Builds\BuildTest.cs`,    
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
- The AI Must not fabricate code or use exambles from outside the Sharky repo for anything implimented.
- What must be implemented, fixed or changed (one paragraph).  This a working test of the single extractor trick for build order testing in Sharky Framework. 
- Discuss a strategy for Global Variables that will make updating build orders easier. Such as cost of buildings and units which will be used in multiple build orders. They are subject to change with new patches
- The AI should impliment pre moving to the extractor.  Discuss more in depth and pros and cons of the different methods.

Quick note / gotchas
•	The example uses fixed travel/build durations. If you prefer distance-based travel time, compute duration from drone position to extractor position and pass that into ScheduleBuild.

- 
- Most Maps will not vary greatly on distance. So time might be best.  A factor would be which drones get tasked, meaning. It could choose the furthest drone for the job.
- For now getting steps working are a higher prioity than complexity to save 5 or less minerals.
- 