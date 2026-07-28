# BabyShark Debug Visualization Pattern Guide

## Overview
Adding a debug visualization requires three coordinated pieces:

1. **Service** — stores visualization data
2. **Manager** — draws the data every frame
3. **Registration** — populates the service where the real values are known

Do not skip any of these steps.

## Critical Rule: Real Z Only
Use the real Z coordinate of the object or terrain at that X/Y location.

Rules:
- Never use `Z = 12`
- Never use arbitrary floating Z values
- Never move a marker upward just to make it visible
- Never draw a point at the COM unless that is the real object location
- If the object is on the ground, draw it on the ground
- If the object is on a ramp or higher plateau, use that real height
- For text labels above a unit, only use `unit.Pos.Z + 1.5f` when the label is intentionally above the unit

If the marker is hard to see, fix the data or the draw call. Do not fake the height.

## Required Pattern

### 1. Service
Create a service that stores visualization data.

The service should:
- use a dictionary keyed by a stable identifier
- expose `Set(...)`
- expose `GetAll()`
- optionally expose `Clear()`

Example:
```csharp
public class YourVisualizationService
{
    private Dictionary<string, YourData> _registry = new Dictionary<string, YourData>();

    public class YourData
    {
        public Point Position { get; set; }
        public string Label { get; set; }
        public Color Color { get; set; }
    }

    public void Set(Point position, string label, Color color)
    {
        _registry[label] = new YourData
        {
            Position = position,
            Label = label,
            Color = color
        };

        Console.WriteLine($"Registered {label} at ({position.X:F2},{position.Y:F2},{position.Z:F2})");
    }

    public Dictionary<string, YourData> GetAll()
    {
        return new Dictionary<string, YourData>(_registry);
    }

    public void Clear()
    {
        _registry.Clear();
    }
}
```

### 2. Manager
Add the service as a field in `BabySharkMiningManager`, inject it through the constructor, and draw it in `OnFrame()`.

The draw method should:
- check `ManagerDebugService.IsDebugEnabled`
- check the service is not null
- retrieve all entries
- draw each item using the stored X/Y/Z exactly as registered

Example:
```csharp
private void DrawYourVisualizations()
{
    if (!ManagerDebugService.IsDebugEnabled || _yourVisualizationService == null)
    {
        return;
    }

    var allData = _yourVisualizationService.GetAll();
    if (allData == null || allData.Count == 0)
    {
        return;
    }

    foreach (var kvp in allData)
    {
        var data = kvp.Value;

        ManagerDebugService.DrawSphere(data.Position, 0.5f, data.Color);
        ManagerDebugService.DrawText(data.Label, data.Position, data.Color, 12);

        Console.WriteLine($"Drew {data.Label} at ({data.Position.X:F2},{data.Position.Y:F2},{data.Position.Z:F2})");
    }
}
```

Do not modify the Z value inside the draw method unless the visualization explicitly requires a label offset above a unit.

### 3. Registration
Register the data only where the real values are known.

Example:
```csharp
var position = new Point
{
    X = x,
    Y = y,
    Z = actualZ
};

yourVisualizationService.Set(position, label, color);
```

Do not guess X/Y/Z.
Do not use a fake visibility offset.
Do not register points above the COM unless the visualized object is actually there.

## Example: Expansion Placement Marker
For expansion townhall markers:
- X/Y must be the actual townhall placement point
- Z must be the actual terrain Z for that expansion
- the marker must not float above the map
- the marker must not be placed at the COM unless the TC actually belongs there
- the marker must reflect the real TC placement location

## Debugging Rules
If a visualization is not visible:

1. Verify the service was populated
2. Verify the manager draw method is being called
3. Verify the X/Y/Z are correct
4. Verify the marker is using the real terrain/object Z
5. Do not fix visibility by using `Z = 12`

## Drawing Primitives
Use Sharky primitives only:
- `DrawSphere`
- `DrawText`
- `DrawLine`

Use the real position for all primitives.

## Key Takeaways
- Services store visualization data
- Manager draws every frame
- Registration happens where real data exists
- Use actual Z values
- Never float debug markers in the air

## Hard Rules
- No Z = 12
- No floating markers
- No fake visibility offsets
- No guessing
- No drawing at COM unless COM is the real object location
- No using average Z unless that is the actual intended real height

