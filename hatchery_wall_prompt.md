Absolutely, Marc — here is a **clean, production‑ready Markdown prompt file** you can drop directly into your GitHub Copilot workspace. It is written specifically for **Section 7** of our earlier discussion:  
**“Create a Hatchery‑First Wall‑Off Algorithm Using SC2 pathing_grid, placement_grid, and playable_area.”**

This is formatted exactly the way Copilot understands best:  
- Clear goals  
- Explicit constraints  
- Required inputs  
- Required outputs  
- Style expectations  
- Sharky‑compatible C# conventions  
- Deterministic behavior instructions  

You can save this as:

```
hatchery_wall_prompt.md
```

---

# 🧬 GitHub Copilot Prompt — Hatchery‑First Wall‑Off (SC2APIProtocol + Sharky)

## 🎯 **Goal**
Generate C# code for a **Hatchery‑first wall‑off system** for a Zerg bot using **SC2APIProtocol** and the **Sharky framework**.  
The system must use:

- `pathing_grid` (walkable tiles)
- `placement_grid` (buildable tiles)
- `playable_area` (map bounds)
- Hatchery footprint (5×5)
- Sharky services (`MapDataService`, `BuildingService`, `DebugService`, etc.)

The output must be **deterministic, reproducible, and map‑independent**.

---

## 📦 **What the Code Must Produce**
Copilot should generate:

### 1. **HatcheryWallService.cs**
A service that:
- Reads pathing_grid and placement_grid from Sharky’s MapData
- Detects the natural ramp choke
- Generates candidate Hatchery positions
- Validates 5×5 placement using placement_grid
- Simulates path blocking using pathing_grid
- Selects the optimal wall position

### 2. **HatcheryWallTask.cs**
A Sharky task that:
- Runs during early game
- Requests a Hatchery at the chosen wall position
- Draws debug overlays (footprint, choke line, chosen tile)

### 3. **Helper methods**
Copilot must include:
- `GetBit(ImageData img, int x, int y)`
- `IsFootprintBuildable(center, size=5)`
- `BlocksChoke(center)`
- `FindChokeTiles()`
- `GenerateCandidatePositions()`

---

## 🧩 **Technical Requirements**

### **Pathing Grid**
- 1‑bit bitmap  
- 1 = walkable  
- 0 = unwalkable  
- Decode using bit indexing:  
  - `index = y * width + x`  
  - `byteIndex = index >> 3`  
  - `bitIndex = index & 7`

### **Placement Grid**
- 1‑bit bitmap  
- 1 = building allowed  
- 0 = building blocked  
- Must check **all 25 tiles** of the Hatchery footprint.

### **Playable Area**
- RectangleI  
- All coordinate checks must stay inside this rectangle.

---

## 🧱 **Wall‑Off Logic Requirements**

### **Choke Detection**
Copilot must:
- Identify the natural ramp choke using pathing_grid
- Compute the narrowest walkable corridor
- Produce a list of choke tiles

### **Candidate Generation**
Copilot must:
- Generate tiles within radius 6–10 of the choke center
- Filter out tiles outside playable_area

### **Placement Validation**
Copilot must:
- Check 5×5 footprint using placement_grid
- Reject any tile where any footprint cell = 0

### **Path Blocking Simulation**
Copilot must:
- Simulate blocking the footprint in pathing_grid
- Test pathing between:
  - A point inside the base  
  - A point outside the base  
- If no path exists → valid wall

### **Selection Criteria**
Copilot must choose the tile that:
1. Blocks pathing  
2. Is closest to choke center  
3. Has the most footprint tiles on pathing_grid = 1  

---

## 🧪 **Debug Requirements**
Copilot must generate debug overlays:
- Yellow: choke tiles  
- Blue: candidate tiles  
- Green: valid placements  
- Red: final chosen Hatchery center  
- White box: 5×5 footprint  

---

## 🧭 **Coding Style Requirements**
Copilot must follow:
- Sharky naming conventions  
- C# 10 style  
- Deterministic logic  
- No magic numbers (constants only)  
- Full XML documentation comments  
- No async unless required  
- No external dependencies  

---

## 📝 **Final Output Format**
Copilot must output:

```
/src/Sharky/Walling/HatcheryWallService.cs
/src/Sharky/Walling/HatcheryWallTask.cs
/src/Sharky/Walling/Models/HatcheryWallData.cs
```

Each file must be complete, compile‑ready, and integrated with Sharky’s architecture.

---

## 🚀 **Prompt to Copilot**
**Use this exact instruction block when generating code:**

> “Generate the full implementation for HatcheryWallService, HatcheryWallTask, and HatcheryWallData using the specifications in this Markdown file. The code must be deterministic, map‑independent, and compatible with the Sharky framework. Use SC2APIProtocol pathing_grid, placement_grid, and playable_area to compute a Hatchery‑first wall‑off at the natural choke.”

---

If you'd like, I can also generate:

- A **minimal test map** for validating the wall logic  
- A **debug replay script**  
- A **visualization overlay** for SC2 map grids  

Just tell me what you want next.
