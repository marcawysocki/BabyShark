# BabySharkBot

A new Zerg bot based on the Sharky framework, derived from SharkyZergExampleBot.

## Project Structure

```
BabySharkBot/
├── Program.cs                 # Entry point - initializes the bot and connects to SC2
├── ZergBuildChoices.cs        # Build selection logic and build registration
├── ladderbots.json            # Ladder bot configuration file
├── BabySharkBot.csproj        # Project file for .NET 9
├── Builds/
│   └── MutaliskRush.cs        # Example build strategy
└── Manager/
    ├── BaseDtos.cs            # Data Transfer Objects for map and unit data
    └── InitialMapData.cs      # Map analysis and mineral/vespene detection
```

## Key Files

### Program.cs
- Initializes `GameConnection` and `DefaultSharkyBot`
- Registers Zerg-specific builds and micro tasks
- Enables default Zerg micro tasks (Queen injection, creep spread, burrow, etc.)
- Supports both single-player testing and ladder play

### ZergBuildChoices.cs
- Registers available builds for different matchups
- Defines build sequences and transitions
- Can be extended with additional builds and strategies

### BaseDtos.cs
Contains data structures:
- `Vector2Dto` - 2D position data
- `WorkerEntryDto` - Worker unit information with labels
- `MapLocationData` - Mineral and vespene patch collections
- `MawBaseLocationData` - Complete base location information

### InitialMapData.cs
- Analyzes the initial game state
- Detects all mineral patches and vespene geysers
- Calculates mineral center of mass
- Provides base location data to other systems

## Building

```bash
dotnet build
```

## Running (Single Player)
```bash
dotnet run
```

## Running (Ladder)
The bot automatically runs in ladder mode when command-line arguments are provided.

## Extending

Add new builds by:
1. Creating a new class inheriting from `ZergSharkyBuild` in `Builds/`
2. Registering it in `ZergBuildChoices`
3. Adding it to the build sequences

## References

- [Sharky Framework](https://github.com/sharknice/Sharky)
- [SC2APIProtocol](https://github.com/Nizbel/SC2APIProtocol)
