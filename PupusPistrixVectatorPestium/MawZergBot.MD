# MawZergBot — create working copy of SharkyZergExampleBot in Maw

Goal
- Produce a working copy of `SharkyZergExampleBot` inside the `Maw` directory so it builds and runs using the local `Sharky` project.

Prerequisites
- .NET 9 SDK installed (repo uses `net9.0`).
- Solution root: `C:\Users\marca\source\repos\PupusPistrixVectatorPestium`
- Source project: `SharkyZergExampleBot`
- Destination: `Maw\MawZergBot`

High-level steps
1. Copy the project directory into `Maw`.
2. Add the copied project to the solution.
3. Update the copied `.csproj` to match repo TFMs and correct ProjectReference paths.
4. Fix Program.cs usings / build-choice class names if needed (manual edit).
5. Remove or repair broken ProjectReferences (e.g., missing `S2ClientProtocol`).
6. Restore and build.

Commands (run from solution root)
- Copy files (PowerShell / robust):

Note: `/MIR` mirrors the source. Use `/E` if you prefer to copy only contents without mirroring deletions.

- Add new project to the solution:

Verify and patch the copied `.csproj`
- Minimal recommended content for `.\Maw\MawZergBot\MawZergBot.csproj`:
