Absolutely, Marc — I can create a **clean, production‑ready prompt** for GitHub Copilot that tells it exactly how to use your **Heartbeat_Master.md** and compare heartbeat phrases across files to detect context drift.

Below is a polished prompt you can drop directly into your repo as:

```
heartbeat_context_prompt.md
```

It’s written in the same deterministic, instruction‑driven style as your other prompt files so Copilot behaves consistently.

---

# 🧭 GitHub Copilot Prompt — Heartbeat Context Integrity System

## 🎯 **Goal**
Generate C# and Markdown utilities that implement a **Heartbeat Context Integrity System**.  
This system ensures GitHub Copilot (Claude Haiku 4.5) can:

1. Detect when files fall out of the LLM context window  
2. Compare heartbeat phrases across files  
3. Re‑reference or re‑read files when drift is detected  
4. Maintain deterministic instruction alignment across the project  

---

## 📦 **What Copilot Must Produce**

### 1. **Heartbeat_Master.md**
Contains:
- The canonical heartbeat phrase  
- A self‑test block  
- Instructions for Copilot on how to reload context when mismatches occur  

### 2. **HeartbeatInjector.cs**
A C# utility that:
- Inserts heartbeat comments into generated files  
- Ensures consistent formatting  
- Provides a method to retrieve the heartbeat phrase programmatically  

### 3. **HeartbeatValidator.cs**
A C# utility that:
- Reads heartbeat comments from C# files  
- Compares them to the master heartbeat  
- Reports mismatches  
- Provides a deterministic list of files missing heartbeats  

### 4. **Copilot Self‑Test Instructions**
A block of text Copilot can use to verify context integrity:

- Compare MASTER_HEARTBEAT with FILE_HEARTBEAT  
- If mismatched → re‑read all instruction files  
- Confirm alignment  

---

## 🧩 **Technical Requirements**

### **Heartbeat Format**
Copilot must use this exact format in C# files:

```csharp
// FILE_HEARTBEAT: ZERG-EXPANSION-ANCHOR
```

And in the master file:

```
MASTER_HEARTBEAT: ZERG-EXPANSION-ANCHOR
```

### **Self‑Test Logic**
Copilot must implement:

1. Extract MASTER_HEARTBEAT  
2. Extract FILE_HEARTBEAT from each file  
3. Compare  
4. If mismatch:
   - Re‑read Heartbeat_Master.md  
   - Re‑read all instruction MD files  
   - Re‑read all C# files containing FILE_HEARTBEAT  
   - Rebuild internal instruction state  
   - Confirm alignment  

### **Deterministic Behavior**
Copilot must:
- Never invent heartbeat phrases  
- Never modify the master heartbeat  
- Never generate random values  
- Always use the same heartbeat phrase across all files  

---

## 🧪 **Debug Requirements**
Copilot must generate:

- A list of files missing heartbeat comments  
- A list of mismatched heartbeat values  
- A confirmation message when alignment is restored  

---

## 🧭 **Algorithm Requirements**

### **1. Heartbeat Extraction**
Copilot must generate code that:
- Reads the first 200 characters of each file  
- Searches for `FILE_HEARTBEAT:`  
- Extracts the value  

### **2. Comparison**
Copilot must:
- Compare extracted values to MASTER_HEARTBEAT  
- Report mismatches  

### **3. Drift Detection**
Copilot must detect drift when:
- A file has no heartbeat  
- A file has a different heartbeat  
- Copilot cannot read the file  
- Copilot gives a vague or generic answer  

### **4. Drift Recovery**
Copilot must:
- Reload Heartbeat_Master.md  
- Reload all instruction MD files  
- Reload all C# files containing heartbeat comments  
- Rebuild its internal instruction state  

---

## 📝 **Final Output Format**
Copilot must output:

```
/docs/Heartbeat_Master.md
/src/Infrastructure/HeartbeatInjector.cs
/src/Infrastructure/HeartbeatValidator.cs
```

Each file must be complete, compile‑ready, and integrated with your project.

---

## 🚀 **Prompt to Copilot**
Use this exact instruction block when generating code:

> “Generate the full Heartbeat Context Integrity System as described in this Markdown file. Implement Heartbeat_Master.md, HeartbeatInjector.cs, and HeartbeatValidator.cs. Ensure deterministic behavior, consistent heartbeat phrases, and full compatibility with Visual Studio 2026 and GitHub Copilot (Claude Haiku 4.5).”

---

If you want, I can also generate:

- The actual **Heartbeat_Master.md** file  
- The full C# implementations  
- A Copilot‑friendly **Context Integrity Test Suite**  
- A **project‑wide heartbeat scanner**  

Just tell me what you want next.
