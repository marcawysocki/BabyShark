# Sharky Framework — Start Location List (3‑Player Map Example)

In the **Sharky (C#) StarCraft II bot framework**, the game exposes starting locations through:

```csharp
ActiveUnitData.GameInfo.StartLocations
```

This is an:

```csharp
IReadOnlyList<Point2D>
```

Each entry is simply a coordinate pair:

```csharp
public struct Point2D {
    public float X;
    public float Y;
}
```

---

## 📍 Example: What the list looks like on a 3‑player map

Sharky will give you something shaped like this:

```csharp
[
    { X = 22.5f, Y = 140.0f },
    { X = 150.0f, Y = 22.0f },
    { X = 140.0f, Y = 150.0f }
]
```

These numbers are just examples — each map has its own coordinates.

There are **no IDs**, no labels, no “start location 1/2/3.”  
Just raw coordinates.

---

## 🧭 Determining *your* start location index

Sharky gives you your actual spawn point:

```csharp
ActiveUnitData.SelfArmyStartLocation
```

To compute which index you spawned at:

```csharp
var my = ActiveUnitData.SelfArmyStartLocation;
var starts = ActiveUnitData.GameInfo.StartLocations;

int myIndex = starts
    .Select((p, i) => new { p, i })
    .OrderBy(e => Vector2.Distance(
        new Vector2(my.X, my.Y),
        new Vector2(e.p.X, e.p.Y)))
    .First().i;
```

---

## 🎯 What Sharky knows on multi‑spawn maps

Sharky **knows**:

- Your spawn location (x, y)
- All possible start locations (list of Point2D)

Sharky **does NOT know**:

- Which start locations are occupied
- Which one belongs to the enemy
- How many players spawned

On 3‑player maps, you must:

- Track unscouted start locations  
- Remove them as you scout  
- Deduce the enemy location  

Sharky’s `ScoutTask` and `EnemyData` can automate this if enabled.

---

## ✔️ Summary

- Sharky exposes start locations as a simple list of `Point2D`.
- There are no IDs — you compute your index manually.
- On 3‑player maps, enemy spawn is unknown until scouted.