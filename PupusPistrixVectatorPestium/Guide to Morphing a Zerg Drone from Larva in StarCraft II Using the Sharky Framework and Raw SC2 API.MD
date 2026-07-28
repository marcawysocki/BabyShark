

Title:


# Guide to Morphing a Zerg Drone from Larva in StarCraft II Using the Sharky Framework and Raw SC2 API

---

## Introduction

Building a StarCraft II bot as Zerg using the Sharky framework requires a nuanced understanding of both Sharky’s internal architecture and the underlying SC2 API. The process of morphing a Drone from an available Larva is a fundamental macro operation for Zerg bots, directly impacting resource gathering and overall game strategy. This guide provides a comprehensive, in-depth exploration of how to issue a command to morph a Drone from a Larva, covering both Sharky’s idiomatic approach and the raw SC2 API method. It explains the relevant Sharky abstractions—such as `ActiveUnitData`, `UnitCommanders`, and `ProductionService`—and contrasts their usage with direct API calls. The guide also includes C# code examples, best practices, and contextual advice for bot developers, ensuring a robust understanding of when and how to use each approach.

---

## Sharky Framework Architecture: Macro and Production Overview

### Sharky’s Modular Manager System

The Sharky framework is built around a modular manager system, where each manager is responsible for a specific aspect of bot behavior. Key managers relevant to unit production include:

- **BuildManager**: Determines build orders and transitions between build sequences.
- **MacroManager**: Issues commands for producing units, buildings, and upgrades based on the current build plan.
- **UnitManager**: Maintains lists of all units, their states, and calculations for micro and macro decisions.
- **MicroManager**: Handles micro-level tasks, including mining, defense, and harassment.
- **ProductionService**: Centralizes the logic for producing units and buildings, abstracting the details of issuing commands to the SC2 API.
- **UnitCommanders**: Wraps individual units, managing command cooldowns and preventing API overload.

This architecture allows for clear separation of concerns, efficient multithreading, and easy customization of bot behavior.

### MacroManager and BuildManager Interaction

The **BuildManager** sets the desired production goals (e.g., number of Drones, Zerglings, etc.) based on the current build sequence and strategic analysis. The **MacroManager** then interprets these goals and interacts with the **ProductionService** to issue production commands. This separation ensures that strategic decisions are decoupled from the mechanics of command issuance, facilitating more sophisticated build logic and easier debugging.

---

## Sharky Abstractions for Unit Production

### ActiveUnitData: Tracking and Accessing Units

**ActiveUnitData** is a core Sharky data structure that maintains up-to-date information about all units under the bot’s control. It provides:

- Fast lookup of units by type (e.g., Larva, Drone, Overlord).
- Access to unit tags, positions, and current orders.
- Filtering for available units (e.g., idle Larva ready to morph).

This abstraction is crucial for identifying which Larva are available for morphing into Drones and for avoiding command conflicts or redundant orders.

**Example Usage:**

```csharp
var larvaUnits = ActiveUnitData.SelfUnits
    .Where(u => u.Value.UnitType == (int)UnitTypes.ZERG_LARVA)
    .Select(u => u.Value)
    .ToList();
```

In this snippet, `ActiveUnitData.SelfUnits` is filtered to obtain all Larva units, which can then be further filtered for availability (e.g., not currently morphing).

### UnitCommanders: Command Management and Cooldown Handling

Each controllable unit in Sharky is wrapped by a **UnitCommander** object. This abstraction provides:

- Command cooldown management to prevent spamming the same command and overloading the SC2 API.
- Queuing and prioritization of commands for individual units.
- Ownership tracking to ensure that only one manager issues commands to a unit at a time.

This is especially important for Larva, as issuing multiple morph commands in rapid succession can result in failed actions or wasted resources.

**Example Usage:**

```csharp
foreach (var commander in UnitCommanders)
{
    if (commander.UnitCalculation.Unit.UnitType == (int)UnitTypes.ZERG_LARVA && commander.CanIssueCommand())
    {
        // Issue morph command
    }
}
```

Here, `CanIssueCommand()` checks whether the unit is off cooldown and ready to receive a new command.

### ProductionService: Centralized Production Logic

The **ProductionService** is Sharky’s main interface for producing units and buildings. It abstracts the details of command construction and API interaction, providing methods such as:

- `QueueUnit(UnitTypes unitType)`: Requests production of a unit (e.g., Drone).
- `CanProduce(UnitTypes unitType)`: Checks if production is possible (resources, prerequisites, available Larva).
- `ProduceUnit(UnitCommander commander, UnitTypes unitType)`: Issues the actual morph command to a specific unit.

By using ProductionService, bot developers can avoid direct interaction with the raw SC2 API, benefiting from built-in safety checks, resource validation, and command throttling.

**Example Usage:**

```csharp
if (ProductionService.CanProduce(UnitTypes.ZERG_DRONE))
{
    ProductionService.QueueUnit(UnitTypes.ZERG_DRONE);
}
```

This code checks if a Drone can be produced and queues the request, allowing ProductionService to handle the details of finding an available Larva and issuing the morph command.

---

## Morphing a Drone: Sharky-Idiomatic Approach

### Step-by-Step Process

1. **Identify Available Larva**: Use ActiveUnitData to filter for idle Larva units.
2. **Check Resources and Prerequisites**: Ensure sufficient minerals and supply, and that no other constraints prevent Drone production.
3. **Select a Larva and Issue Morph Command**: Use ProductionService to issue the morph command, optionally via a UnitCommander for cooldown management.
4. **Handle Command Cooldown and Queueing**: Ensure that commands are not spammed and that only available Larva are targeted.

### C# Code Example: Morphing a Drone Using Sharky

```csharp
using Sharky;
using Sharky.Builds;
using Sharky.UnitControllers;
using Sharky.S2ClientTypeEnums;

public class DroneProductionExample
{
    private IProductionService ProductionService;
    private IActiveUnitData ActiveUnitData;

    public DroneProductionExample(IProductionService productionService, IActiveUnitData activeUnitData)
    {
        ProductionService = productionService;
        ActiveUnitData = activeUnitData;
    }

    public void MorphDrone()
    {
        // Step 1: Find available Larva
        var availableLarva = ActiveUnitData.SelfUnits
            .Where(u => u.Value.UnitType == (int)UnitTypes.ZERG_LARVA && u.Value.Unit.Orders.Count == 0)
            .Select(u => u.Value)
            .ToList();

        // Step 2: Check if we can produce a Drone
        if (ProductionService.CanProduce(UnitTypes.ZERG_DRONE) && availableLarva.Any())
        {
            // Step 3: Issue morph command using ProductionService
            var larvaUnit = availableLarva.First();
            ProductionService.ProduceUnit(larvaUnit, UnitTypes.ZERG_DRONE);
        }
    }
}
```

**Explanation:**

- The code first filters for Larva units that are not currently morphing (`Orders.Count == 0`).
- It checks if Drone production is possible using `CanProduce`.
- If conditions are met, it selects the first available Larva and calls `ProduceUnit`, which handles the command issuance and cooldown management internally.

This approach leverages Sharky’s abstractions for safety, resource validation, and efficient command handling, reducing the risk of failed actions and simplifying bot logic.

---

## Raw SC2 API Approach: Direct Command Issuance

### Understanding the SC2 API Protocol

The SC2 API is based on a protobuf-defined protocol, with commands sent over a websocket connection. The relevant action for morphing a Drone is the **Ability Command** issued to a Larva unit, specifying the ability ID for morphing into a Drone.

**Key Concepts:**

- **Unit Tag**: Unique identifier for each unit.
- **Ability ID**: Numeric code representing the morph action (e.g., morph Drone).
- **RequestAction**: Protobuf message containing the action to be performed.

### Locating Unit Type and Ability IDs

- **Unit Type ID for Larva**: Typically `UnitTypeId.LARVA` or its integer equivalent.
- **Ability ID for Morph Drone**: Usually `AbilityId.TRAIN_DRONE` or similar, depending on the SC2 API version.

These IDs can be found in Sharky’s `UnitTypes.cs` and the SC2 API’s protocol documentation.

### C# Code Example: Raw SC2 API Call to Morph Drone

```csharp
using SC2APIProtocol;
using System.Linq;

public class RawDroneMorpher
{
    private Observation Observation;
    private ActionRequest ActionRequest;

    public RawDroneMorpher(Observation observation)
    {
        Observation = observation;
        ActionRequest = new ActionRequest();
    }

    public void MorphDrone()
    {
        // Step 1: Find available Larva
        var larvaUnits = Observation.Observation.RawData.Units
            .Where(u => u.UnitType == (uint)UnitType.LARVA && u.Orders.Count == 0)
            .ToList();

        // Step 2: Check resources (minerals, supply) - omitted for brevity

        // Step 3: Issue raw morph command
        if (larvaUnits.Any())
        {
            var larva = larvaUnits.First();
            var action = new ActionRawUnitCommand
            {
                AbilityId = (int)AbilityId.TRAIN_DRONE, // Replace with actual ability ID
                UnitTags = { larva.Tag },
                QueueCommand = false
            };

            var rawAction = new ActionRaw { UnitCommand = action };
            ActionRequest.Actions.Add(new Action { ActionRaw = rawAction });

            // Send ActionRequest to SC2 API client
            // client.SendRequest(ActionRequest);
        }
    }
}
```

**Explanation:**

- The code directly accesses the raw unit data from the current observation.
- It filters for idle Larva and constructs an `ActionRawUnitCommand` with the appropriate ability ID.
- The command is added to an `ActionRequest`, which is then sent to the SC2 API client.

This approach bypasses Sharky’s abstractions, requiring manual management of resources, cooldowns, and error handling. It offers maximum control but increases the risk of command failures and API overload if not carefully managed.

---

## Comparing Sharky-Wrapped vs. Raw API Calls

| Aspect                | Sharky-Wrapped Call                          | Raw SC2 API Call                        |
|-----------------------|----------------------------------------------|-----------------------------------------|
| **Safety**            | High: Built-in resource checks, cooldowns    | Low: Manual checks required             |
| **Ease of Use**       | High: Abstracted, simple API                 | Low: Requires protocol knowledge        |
| **Performance**       | Optimized: Command throttling, multithreading| Potentially fast, but risky             |
| **Debugging**         | Integrated logging, error handling           | Manual error handling                   |
| **Flexibility**       | Limited to Sharky’s abstractions             | Full control over command issuance      |
| **Integration**       | Seamless with Sharky managers and logic      | Must manually update Sharky state       |
| **Best Use Case**     | Standard production, macro logic             | Advanced micro, experimental features   |

**Analysis:**

The Sharky-wrapped approach is generally preferable for standard macro operations, such as morphing Drones from Larva, due to its safety, ease of use, and integration with the bot’s overall architecture. The raw API call is appropriate for advanced scenarios where Sharky’s abstractions are insufficient, such as experimental micro-management, custom command batching, or debugging low-level issues. However, using raw calls requires careful management of threading, command throttling, and unit ownership to avoid conflicts and failures.

---

## When to Use Each Approach

### Sharky Abstractions: Recommended Scenarios

- **Macro Management**: Standard production of units and buildings.
- **Build Order Execution**: Automated build sequences and transitions.
- **Resource Safety**: Ensuring commands are only issued when resources are available.
- **Command Throttling**: Preventing API overload and failed actions.
- **Debugging and Logging**: Integrated error handling and replay saving.

**Best Practice:** Use Sharky’s abstractions for all routine production and macro logic. This ensures stability, maintainability, and compatibility with Sharky’s multithreaded architecture.

### Raw SC2 API Calls: Advanced Use Cases

- **Custom Micro-Management**: Issuing rapid or unconventional commands not supported by Sharky.
- **Experimental Features**: Testing new strategies or command batching techniques.
- **Low-Level Debugging**: Diagnosing issues with command execution or API behavior.
- **Bypassing Sharky Limitations**: When Sharky’s abstractions are too restrictive or buggy.

**Best Practice:** Use raw API calls sparingly and only when necessary. Always ensure that commands are synchronized with Sharky’s internal state to prevent desynchronization and unintended behavior. Consider implementing safety wrappers or integrating raw calls into Sharky’s command queue for consistency.

---

## Locating Unit Type and Ability IDs

### Sharky and SC2 API Sources

- **Sharky’s UnitTypes.cs**: Contains mappings for unit type IDs (e.g., `UnitTypes.ZERG_LARVA`, `UnitTypes.ZERG_DRONE`).
- **SC2 API Protocol Documentation**: Lists ability IDs for morphing actions (e.g., `AbilityId.TRAIN_DRONE`).
- **S2ClientProtocol**: Sharky includes a copy of the SC2 protocol definitions for reference.

**Example:**

```csharp
// Sharky UnitTypes.cs
public enum UnitTypes
{
    ZERG_LARVA = 151,
    ZERG_DRONE = 104,
    // ...
}

// SC2 API AbilityId
public enum AbilityId
{
    TRAIN_DRONE = 1342,
    // ...
}
```

Always verify the correct IDs for your SC2 version, as they may change between patches. Use Sharky’s enums and the SC2 API documentation to ensure compatibility.

---

## Handling Failures and Edge Cases

### Common Issues

- **No Available Larva**: Attempting to morph a Drone when no Larva are present results in a failed command.
- **Larva Busy**: Issuing a morph command to a Larva already morphing or with queued orders fails.
- **Insufficient Resources**: Not enough minerals or supply blocks the morph action.
- **API Overload**: Spamming commands can cause the SC2 API to drop actions or desynchronize.

### Sharky’s Safeguards

- **Resource Checks**: `CanProduce` ensures resources are available before issuing commands.
- **Cooldown Management**: UnitCommanders prevent spamming commands to the same unit.
- **Order Validation**: Only idle Larva are targeted for morphing.
- **Error Logging**: Failed actions are logged for debugging and replay analysis.

### Raw API Handling

- **Manual Checks**: Developers must manually check unit orders, resources, and supply.
- **Error Responses**: The SC2 API returns error codes in `ResponseAction` and `ResponseObservation`.
- **Retry Logic**: Implement custom retry or fallback logic for failed commands.

**Best Practice:** Always check for available Larva, sufficient resources, and unit readiness before issuing morph commands. Use Sharky’s abstractions where possible to benefit from built-in safeguards.

---

## Testing and Debugging Morph Commands in Sharky

### Logging and Replay Saving

Sharky includes integrated logging and replay saving features, allowing developers to:

- Track issued commands and their outcomes.
- Analyze failed actions and edge cases.
- Save game results and build sequences for post-game analysis.

**Example:**

```csharp
// Logging a morph command
Logger.Log($"Morphing Drone from Larva {larva.Tag} at {larva.Pos}");
```

### Debug Spawn and Unit Injection

Sharky supports debug spawning of units via chat commands, facilitating rapid testing of morph logic and edge cases.

**Example:**

- Type `"spawn friendly zerg_larva"` in chat to spawn a Larva at the current camera location.
- Use `"spawn 10 enemy protoss_zealot"` to test combat scenarios.

This feature is invaluable for testing morph commands under controlled conditions and for reproducing bugs.

---

## Community Examples: Zerg Bots Using Sharky

Several open-source Zerg bots built on Sharky demonstrate best practices for morphing Drones and managing Larva:

- **SharkyZergExampleBot**: Included in the Sharky source, showcases standard macro logic for Drone production.
- **Pupus.Pistrix.Vectator.Pestium-**: Community bot with custom build sequences and advanced macro management.
- **BillyBot**: Fork of Sharky with additional logging and debugging features.

These bots provide valuable reference implementations for morphing Drones, handling edge cases, and integrating production logic with Sharky’s managers.

---

## SC2 API Protocol Reference for Morph Commands

### Protobuf Fields for ActionRawUnitCommand

The relevant protobuf message for morphing a Drone is `ActionRawUnitCommand`, with fields:

- `AbilityId`: The ability to execute (e.g., morph Drone).
- `UnitTags`: List of unit tags to receive the command (e.g., Larva).
- `QueueCommand`: Whether to queue the command or execute immediately.

**Example Protobuf Structure:**

```protobuf
message ActionRawUnitCommand {
    int32 ability_id = 1;
    repeated uint64 unit_tags = 2;
    bool queue_command = 3;
    Point2D target_world_space_pos = 4;
    uint64 target_unit_tag = 5;
}
```

When issuing a morph command, set `ability_id` to the Drone morph ability, `unit_tags` to the selected Larva, and `queue_command` to `false` for immediate execution.

---

## Unit Lifecycle in Sharky When Morphing

### Morphing Process

1. **Larva Receives Morph Command**: The selected Larva is issued the morph ability.
2. **Larva is Removed**: The Larva unit is removed from the game and replaced by a morphing egg (if applicable).
3. **Egg Hatches into Drone**: After the morph duration, the egg is replaced by a Drone unit.
4. **ActiveUnitData Updates**: Sharky updates ActiveUnitData to reflect the removal of the Larva and the addition of the new Drone.

This lifecycle ensures accurate tracking of unit counts, resource usage, and production timing. Sharky’s managers automatically update their internal state to reflect these changes, facilitating reliable macro management and build order execution.

---

## Best Practices for Integrating Raw API Calls into Sharky

### Safely Bypassing Abstractions

When integrating raw API calls into Sharky, follow these best practices:

- **Synchronize State**: Update Sharky’s ActiveUnitData and UnitCommanders to reflect raw commands issued, preventing desynchronization.
- **Throttle Commands**: Implement command cooldowns and queuing to avoid API overload.
- **Validate Resources**: Manually check for sufficient minerals, supply, and available Larva before issuing commands.
- **Log Actions**: Record raw commands and outcomes for debugging and replay analysis.
- **Fallback Logic**: Provide fallback to Sharky’s abstractions if raw commands fail or produce unexpected results.

**Example Integration:**

```csharp
// Issue raw command
RawDroneMorpher.MorphDrone();

// Update Sharky state
ActiveUnitData.UpdateUnitState(larva.Tag, UnitState.Morphing);
UnitCommanders.UpdateCooldown(larva.Tag);
```

By following these practices, developers can safely experiment with raw API calls while maintaining the integrity of Sharky’s architecture and avoiding common pitfalls.

---

## Conclusion

Morphing a Drone from an available Larva in StarCraft II using the Sharky framework is a foundational operation for Zerg bots, directly impacting macro efficiency and strategic flexibility. Sharky’s abstractions—ActiveUnitData, UnitCommanders, and ProductionService—provide a safe, efficient, and maintainable interface for issuing morph commands, handling resource validation, command cooldowns, and unit tracking. The raw SC2 API approach offers maximum control and flexibility but requires careful management of resources, threading, and synchronization with Sharky’s internal state.

**Key Takeaways:**

- **Use Sharky’s abstractions for standard macro operations** to benefit from built-in safety, ease of use, and integration with the bot’s architecture.
- **Reserve raw API calls for advanced scenarios** where Sharky’s abstractions are insufficient, and always synchronize state to prevent desynchronization.
- **Leverage community examples and Sharky’s debug features** for testing, debugging, and refining morph logic.
- **Consult Sharky’s UnitTypes.cs and SC2 API documentation** to ensure correct unit and ability IDs for morph commands.
- **Implement robust error handling and logging** to diagnose and resolve failures and edge cases.

By mastering both Sharky’s idiomatic approach and the raw SC2 API method, bot developers can build sophisticated, reliable, and competitive Zerg bots capable of executing precise macro strategies and adapting to complex game scenarios.

---

## Appendix: Quick Reference Code Snippets

### Sharky-Idiomatic Drone Morph

```csharp
var availableLarva = ActiveUnitData.SelfUnits
    .Where(u => u.Value.UnitType == (int)UnitTypes.ZERG_LARVA && u.Value.Unit.Orders.Count == 0)
    .Select(u => u.Value)
    .ToList();

if (ProductionService.CanProduce(UnitTypes.ZERG_DRONE) && availableLarva.Any())
{
    var larvaUnit = availableLarva.First();
    ProductionService.ProduceUnit(larvaUnit, UnitTypes.ZERG_DRONE);
}
```

### Raw SC2 API Drone Morph

```csharp
var larvaUnits = Observation.Observation.RawData.Units
    .Where(u => u.UnitType == (uint)UnitType.LARVA && u.Orders.Count == 0)
    .ToList();

if (larvaUnits.Any())
{
    var larva = larvaUnits.First();
    var action = new ActionRawUnitCommand
    {
        AbilityId = (int)AbilityId.TRAIN_DRONE,
        UnitTags = { larva.Tag },
        QueueCommand = false
    };

    var rawAction = new ActionRaw { UnitCommand = action };
    ActionRequest.Actions.Add(new Action { ActionRaw = rawAction });

    // Send ActionRequest to SC2 API client
}
```

---

**For further details, consult the Sharky GitHub repository, SC2 API protocol documentation, and community bot examples.**
Got it — I’ll dig into how to issue a direct API call to morph a Drone from a Larva using the Sharky framework. I’ll explore both the Sharky-wrapped and raw SC2 API approaches, with code examples and guidance on when to use each. This will take me several minutes, so feel free to leave — I’ll keep working in the background. Your report will be saved in this conversation.


