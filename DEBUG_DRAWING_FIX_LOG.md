# MawDebugDrawingManager Crosshairs Fix - Final Attempt Documentation

## Issue
**Error Code:** CS0117  
**Error Message:** `'DebugLine' does not contain a definition for 'P0'`

## Root Cause Analysis
The SC2APIProtocol `DebugLine` class does not have direct `P0` and `P1` properties. Instead, it has a `Line` property that is of type `Line`, and the `Line` class contains the `P0` and `P1` properties.

## Solution Applied
Changed the crosshair drawing code in `MawDebugDrawingManager.BuildDebugRequest()` method to wrap the point definitions in a `Line` object.

### Code Pattern (Verified from Sharky\DebugService.cs line 35)
```csharp
new DebugLine() { Color = color, Line = new Line() { P0 = start, P1 = end } }
```

## Changes Made to MawDebugDrawingManager.cs

### Lines 183-190 (Horizontal Crosshair Line)
**Before:**
```csharp
debugCmd.Draw.Lines.Add(new DebugLine
{
    P0 = new Point { X = comPos.X - crosshairSize, Y = comPos.Y, Z = 12 },
    P1 = new Point { X = comPos.X + crosshairSize, Y = comPos.Y, Z = 12 },
    Color = new Color { R = 0, G = 255, B = 255 }
});
```

**After:**
```csharp
debugCmd.Draw.Lines.Add(new DebugLine
{
    Line = new Line
    {
        P0 = new Point { X = comPos.X - crosshairSize, Y = comPos.Y, Z = 12 },
        P1 = new Point { X = comPos.X + crosshairSize, Y = comPos.Y, Z = 12 }
    },
    Color = new Color { R = 0, G = 255, B = 255 }
});
```

### Lines 192-199 (Vertical Crosshair Line)
**Before:**
```csharp
debugCmd.Draw.Lines.Add(new DebugLine
{
    P0 = new Point { X = comPos.X, Y = comPos.Y - crosshairSize, Z = 12 },
    P1 = new Point { X = comPos.X, Y = comPos.Y + crosshairSize, Z = 12 },
    Color = new Color { R = 0, G = 255, B = 255 }
});
```

**After:**
```csharp
debugCmd.Draw.Lines.Add(new DebugLine
{
    Line = new Line
    {
        P0 = new Point { X = comPos.X, Y = comPos.Y - crosshairSize, Z = 12 },
        P1 = new Point { X = comPos.X, Y = comPos.Y + crosshairSize, Z = 12 }
    },
    Color = new Color { R = 0, G = 255, B = 255 }
});
```

## What This Fixes
✓ Crosshair horizontal line will now draw correctly  
✓ Crosshair vertical line will now draw correctly  
✓ Center sphere will draw (unchanged)  
✓ COM label will display (unchanged)  
✓ Worker labels continue to draw unchanged (worker label drawing code untouched)

## Verification Points
- Worker label service still builds debug requests (lines 153-161)
- All label commands are added to the request (lines 157-160)
- COM crosshairs are drawn in a separate try block (lines 163-224)
- Status text is added in its own try block (lines 226-237)
- No worker label code was modified
