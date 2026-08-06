Observation_deep_technical_breakdown.md

Here’s the complete, **deep technical breakdown** of *exactly what information is available about an enemy unit* in StarCraft II — **for bots**, **for the API**, and **for the game engine itself**.  
This is tailored to your SC2 bot‑developer workflow (Sharky, BWAPI‑style thinking, frame‑based logic).

---

# 🎯 **What Information Is Available About an Enemy Unit in SC2 (API + Game Engine)**

## ✅ **1. If the enemy unit is *visible***  
You get **full real‑time data**:

### **Basic Unit Data**
- Unit type (Marine, Zealot, etc.)
- Unit tag (unique ID)
- Position (x,y,z)
- Facing direction
- Movement speed
- Radius
- Alliance (enemy, neutral)

### **Combat Stats**
- Current HP  
- Max HP  
- Current Shields  
- Max Shields  
- Current Energy  
- Max Energy  
- Armor  
- Weapon cooldowns  
- Weapon ranges  
- Weapon damage  
- Attack upgrades (deduced from damage values)

### **Status Effects**
You can detect:
- Stimpack  
- Guardian Shield  
- Fungal Growth  
- Time Warp  
- Cloaked  
- Burrowed  
- Disruptor Nova  
- Lock‑On  
- Blinding Cloud  
- Parasitic Bomb  
- Neural Parasite  
- Etc.

### **Abilities & Cooldowns**
You can see:
- Whether an ability is currently active (e.g., Pulsar Beam ON)
- Whether the unit is channeling (e.g., Neural Parasite)
- Whether the unit is in a special state (sieged, uprooted, etc.)

**BUT you cannot directly read cooldown timers.**  
You infer cooldowns from:
- Weapon cooldown values  
- Ability availability (if the API reports ability is usable)

### **Orders**
You can see:
- Current order (attack, move, cast spell)
- Target unit tag
- Target position
- Order queue

This is extremely powerful for bot micro.

---

# ❗ **2. If the enemy unit is *not visible***  
You get **limited memory data**, depending on game rules.

### **Fog‑of‑War Memory**
If you saw the unit before, you retain:
- Last known position  
- Last known type  
- Last known health  
- Last known order (sometimes)  
- Last known burrow/cloak state  

But you **do NOT** get:
- Updated HP  
- Updated energy  
- Updated cooldowns  
- Updated position  
- Updated upgrades  
- Updated buffs/debuffs  

Fog‑of‑war memory is *static* until you see the unit again.

### **Hidden Units**
If the enemy is:
- Cloaked  
- Burrowed  
- Behind terrain  
- Out of vision  

You get **nothing** until detection or vision reveals it.

---

# 🧠 **3. What You Can Infer (Even Without Vision)**  
Bots can infer **a surprising amount**:

### **Tech Tree Inference**
If you see:
- A Spire → Mutalisks or Corruptors possible  
- A Twilight Council → Blink or Charge timing  
- A Ghost Academy → EMP threat  
- A Fusion Core → Battlecruiser timing  

### **Upgrade Inference**
You can infer upgrades by:
- Damage taken  
- Damage dealt  
- Movement speed changes  
- Attack speed changes  
- Visual effects (e.g., blue flame Hellions)

### **Production Inference**
If you see:
- Reactor Barracks → mass bio  
- Double Robo → Immortal/Colossus  
- Spawning Pool timing → early aggression  
- Lair timing → tech rush  

### **Army Movement Prediction**
Based on:
- Map control  
- Creep spread  
- Known rally points  
- Standard build orders  
- Last known enemy army position  

---

# 🧩 **4. What Information Is Available About *Your Own Bot’s Units***  
You get **full internal state**, including:

### **Unit Data**
- HP, shields, energy  
- Cooldowns  
- Buffs/debuffs  
- Orders  
- Pathing  
- Ability availability  
- Weapon cooldowns  
- Movement speed  
- Facing direction  

### **Game State**
- Minerals, gas  
- Supply  
- Tech tree  
- Upgrades  
- Production queues  
- Map visibility  
- Creep  
- Power fields  
- Larva count  
- Chrono boost  
- Mule mining efficiency  

### **Bot‑Specific Internal State**
Your bot can track:
- Custom roles (e.g., “Harass”, “Scout”, “Defend”)  
- Custom squad assignments  
- Custom micro logic  
- Custom memory (enemy tech, timings, etc.)

Sharky uses:
- Unit roles  
- Micro controllers  
- Managers  
- Observers  
- Strategy modules  

---

# 🏆 **5. Summary Table**

| Category | Visible Enemy | Fog‑of‑War Enemy | Your Bot |
|---------|----------------|------------------|----------|
| Position | Yes | Last known | Yes |
| HP/Shields | Yes | Last known | Yes |
| Energy | Yes | Last known | Yes |
| Cooldowns | Partial | No | Yes |
| Orders | Yes | Last known | Yes |
| Buffs | Yes | No | Yes |
| Cloak/Burrow | Yes | Last known | Yes |
| Tech tree | Inferred | Inferred | Yes |
| Upgrades | Inferred | Inferred | Yes |
| Movement | Yes | No | Yes |
| Production | Inferred | Inferred | Yes |

---

# 🎯 If you want, I can also generate:
- A **full SC2 API unit‑information reference**  
- A **Sharky‑specific breakdown** of how it tracks enemy units  
- A **table of all unit spells + cooldowns**  
- A **guide on how to infer enemy tech with perfect accuracy**  
- A **frame‑by‑frame micro logic template** for your bot  

Just tell me what direction you want to go next.