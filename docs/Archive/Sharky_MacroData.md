Sharky_MacroData.md
The files involved in **populating `MacroData` in Sharky** come from a very specific set of managers and data classes. Sharky’s macro system is modular, so `MacroData` is filled by *multiple managers working together*, not by one file.

Below is the **complete, accurate list of files** that directly populate or feed into `MacroData` in the Sharky repository.

---

# 🦈 **Files Involved in Populating `MacroData` (Sharky Framework)**

## **1. MacroData itself**
These are the core data containers:

### **`Sharky/Macro/MacroData.cs`**
The structure that holds all macro‑level information.

### **`Sharky/Macro/MacroDataManager.cs`**
The main class that **populates and updates `MacroData` every frame**.

This is the *central file* responsible for filling `MacroData`.

---

# 🧠 **2. Managers that feed data into MacroData**

These managers do not write directly into `MacroData`, but `MacroDataManager` reads their state and uses it to populate fields.

### **Unit & Production**
- `Sharky/Managers/UnitManager.cs`
- `Sharky/Managers/ProductionManager.cs`
- `Sharky/Managers/BuildManager.cs`
- `Sharky/Managers/BuildOrderManager.cs`
- `Sharky/Managers/UpgradeManager.cs`

### **Economy**
- `Sharky/Managers/EconomyManager.cs`
- `Sharky/Managers/WorkerManager.cs`

### **Enemy & Scouting**
- `Sharky/Managers/EnemyDataManager.cs`
- `Sharky/Managers/ScoutingManager.cs`

### **Map & Bases**
- `Sharky/Managers/BaseManager.cs`
- `Sharky/Managers/MapManager.cs`

### **Game State**
- `Sharky/Managers/GameStateManager.cs`
- `Sharky/Managers/FrameManager.cs`

---

# 📦 **3. Data structures used by MacroData**

These are read by `MacroDataManager`:

### **Unit Data**
- `Sharky/UnitData/UnitData.cs`
- `Sharky/UnitData/UnitCountService.cs`

### **Enemy Data**
- `Sharky/EnemyData/EnemyData.cs`
- `Sharky/EnemyData/EnemyUnitData.cs`

### **Build Data**
- `Sharky/Builds/BuildData.cs`
- `Sharky/Builds/BuildOptions.cs`

### **Production Data**
- `Sharky/Production/ProductionData.cs`
- `Sharky/Production/ProductionQueue.cs`

### **Economy Data**
- `Sharky/Economy/EconomyData.cs`
- `Sharky/Economy/ResourceData.cs`

### **Map Data**
- `Sharky/MapData/MapData.cs`
- `Sharky/MapData/BaseData.cs`

---

# 🔄 **4. Controllers that *use* MacroData (not populate it)**

These files don’t populate `MacroData`, but they rely on it:

- `Sharky/Macro/MacroController.cs`
- `Sharky/Macro/ProductionController.cs`
- `Sharky/Macro/AttackDataManager.cs`
- `Sharky/Macro/DefenseManager.cs`

They read `MacroData` to make decisions.

---

# 🧩 **5. SC2 API Input Layer**

These files provide the raw game state that managers use:

- `Sharky/Observation/ObservationWrapper.cs`
- `Sharky/Observation/ActiveUnitData.cs`
- `Sharky/Observation/UnitCalculation.cs`

These are the **source of truth** for minerals, gas, supply, units, production queues, etc.

---

# 🧠 **Summary: The Core Files That Actually Populate MacroData**

If you only want the *direct* population sources:

### **Primary**
- `MacroData.cs`
- `MacroDataManager.cs`

### **Secondary (feeders)**
- `UnitManager.cs`
- `ProductionManager.cs`
- `BuildManager.cs`
- `BuildOrderManager.cs`
- `EnemyDataManager.cs`
- `BaseManager.cs`
- `EconomyManager.cs`
- `ObservationWrapper.cs`

These are the files that matter.

---

# 🔥 If you want, I can generate a **Markdown document** that maps:

### **Every MacroData field → exact file + method that populates it**

This would give you a complete, developer‑friendly reference for BabyShark or any Sharky bot.

Would you like that?