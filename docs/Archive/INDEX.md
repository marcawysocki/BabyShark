# BabyShark Documentation Index 📚

## Quick Navigation

Start here based on what you need:

### 🚀 Getting Started
- **First time here?** → Start with `QUICK_REFERENCE.md`
- **Want overview?** → Read `SESSION_SUMMARY.md`
- **Need system design?** → See `ARCHITECTURE_DIAGRAM.md`

### 💻 For Developers
- **Adding visualizations?** → `DRAWING_PATTERN_GUIDE.md`
- **Understanding greedy ordering?** → `GREEDY_MINERAL_ORDERING.md`
- **Visual walkthrough?** → `GREEDY_MINERAL_ORDERING_VISUAL.md`
- **Testing your changes?** → `TESTING_CHECKLIST.md`

### 📊 For Project Management
- **What's complete?** → `IMPLEMENTATION_STATUS.md`
- **What was done today?** → `SESSION_SUMMARY.md`
- **Full architecture?** → `ARCHITECTURE_DIAGRAM.md`

---

## Document Descriptions

### 1. **QUICK_REFERENCE.md** ⭐ START HERE
```
├─ Current Status Overview
├─ Code Architecture Summary
├─ Key Files & Line Numbers
├─ Testing Checklist
├─ FAQ (Frequently Asked Questions)
├─ Support References
└─ How to use OrderedMainMinerals
```
**When to use**: You need a quick answer or overview

---

### 2. **DRAWING_PATTERN_GUIDE.md**
```
├─ Overview: Service pattern for visualizations
├─ Step-by-Step: Creating a new visualization
├─ Service Template Code
├─ Manager Integration Code
├─ Drawing Method Template
├─ Registration Pattern
├─ Complete Example: Opponent Start Domes (red)
├─ Key Takeaways
├─ Debugging Checklist
└─ Files Modified By Pattern
```
**When to use**: You want to add a NEW debug visualization (crosshairs, markers, arrows, domes, etc.)

---

### 3. **GREEDY_MINERAL_ORDERING.md**
```
├─ Summary
├─ What Was Added (OrderedMineral class, algorithm)
├─ Implementation Details
├─ Console Output Examples
├─ Usage: Accessing Ordered Minerals
├─ Key Insights
├─ Testing Checklist
├─ Files Modified
└─ Next Steps
```
**When to use**: You need detailed information about the greedy algorithm

---

### 4. **GREEDY_MINERAL_ORDERING_VISUAL.md**
```
├─ Visual Algorithm Guide
├─ Example: 8 Minerals, 1 Worker, COM
├─ Phase 1: Find M[0] with distances
├─ Phase 2: Greedy Chain iterations 1-7
├─ Phase 3: Near/Far Classification
├─ Worker Assignment (Future Phase)
├─ Code Algorithm (Pseudocode)
├─ Key Takeaways
└─ Edge Cases Handled
```
**When to use**: You want to UNDERSTAND how the algorithm works visually

---

### 5. **IMPLEMENTATION_STATUS.md**
```
├─ Complete Components
├─ Data Flow Diagram
├─ How to Use OrderedMainMinerals
├─ Verification Checklist
├─ Documentation Files Created
├─ Next Steps
├─ Known Limitations
└─ Testing Strategy Table
```
**When to use**: You want to know what's done and what's next

---

### 6. **SESSION_SUMMARY.md**
```
├─ What Was Done This Session
├─ Files Modified with details
├─ Build Status
├─ How to Use Results
├─ Testing Instructions
├─ Key Insights
├─ Code Quality Assessment
├─ Session Statistics
├─ Important Notes
└─ Ready for Next Phase
```
**When to use**: You want a summary of today's work

---

### 7. **ARCHITECTURE_DIAGRAM.md**
```
├─ Complete System Architecture (ASCII diagram)
├─ BabySharkAI Initialization
├─ Game Start: InitialMapData
├─ Every Frame: OnFrame()
├─ Data Structures
├─ Future Phase: Worker Assignment
├─ Key Coordination Points
└─ Quick Navigation
```
**When to use**: You need to understand the complete system flow

---

### 8. **TESTING_CHECKLIST.md**
```
├─ ✅ What Was Implemented (checked)
├─ 🧪 Pre-Testing Checklist
├─ 🎮 In-Game Testing Steps
├─ ❌ Troubleshooting Guide
├─ 📊 Expected Results by Map Type
├─ ✅ Verification Success Criteria
├─ 🚀 Next Steps After Verification
├─ 📞 Quick Lookup Decision Tree
└─ ✨ Success Definition
```
**When to use**: You're testing the implementation or debugging issues

---

### 9. **This File: INDEX.md**
```
This navigation guide for all documentation
```

---

## By Scenario

### Scenario: "I'm new, where do I start?"
```
1. QUICK_REFERENCE.md (5 min read)
2. ARCHITECTURE_DIAGRAM.md (understand flow)
3. GREEDY_MINERAL_ORDERING_VISUAL.md (learn algorithm)
4. Ready to implement? → Choose from Developer section
```

### Scenario: "The crosshairs aren't showing"
```
TESTING_CHECKLIST.md → Troubleshooting → "If Crosshairs Not Visible"
(Check Z coordinate, debug enabled, SetCOM called, drawing executed)
```

### Scenario: "I need to add a new visualization"
```
DRAWING_PATTERN_GUIDE.md → Read complete pattern
→ Follow step-by-step (service, instantiation, injection, drawing)
→ Use code templates provided
```

### Scenario: "I don't understand the greedy chain"
```
GREEDY_MINERAL_ORDERING_VISUAL.md → Read Phase 1, 2, 3 sections
→ See actual distances calculated
→ Understand why M[0] is furthest
→ See iterations building M[1-7]
```

### Scenario: "I want to use OrderedMainMinerals for worker assignment"
```
ARCHITECTURE_DIAGRAM.md → Future Phase section
→ QUICK_REFERENCE.md → "How to use OrderedMainMinerals"
→ GREEDY_MINERAL_ORDERING.md → Usage Examples
```

### Scenario: "What happened in this session?"
```
SESSION_SUMMARY.md → Overview of all changes
→ IMPLEMENTATION_STATUS.md → What's complete
→ Files Modified (check specific files)
```

### Scenario: "I'm testing, something seems wrong"
```
TESTING_CHECKLIST.md → Choose your issue → Troubleshooting
→ Or use Quick Lookup Decision Tree
→ Check specific error message
```

---

## File Organization

```
Workspace Root/
├── Documentation Files (created this session):
│   ├── DRAWING_PATTERN_GUIDE.md
│   ├── GREEDY_MINERAL_ORDERING.md
│   ├── GREEDY_MINERAL_ORDERING_VISUAL.md
│   ├── MINERAL_LABEL_DRAWING.md ✨ NEW
│   ├── MINERAL_LABEL_INTEGRATION.md ✨ NEW
│   ├── MINERAL_LABEL_VISUAL_GUIDE.md ✨ NEW
│   ├── IMPLEMENTATION_STATUS.md
│   ├── SESSION_SUMMARY.md
│   ├── QUICK_REFERENCE.md
│   ├── ARCHITECTURE_DIAGRAM.md
│   ├── TESTING_CHECKLIST.md
│   └── INDEX.md (this file)
│
├── Source Code (BabySharkBot project):
│   ├── BabySharkBot/
│   │   ├── BabySharkBot.cs (modified: added MineralLabelService)
│   │   ├── Setup/
│   │   │   ├── BaseDtos.cs (modified: added MineralLabelService class)
│   │   │   └── InitialMapData.cs (modified: added RegisterMineralLabels method)
│   │   ├── Managers/
│   │   │   ├── BabySharkMiningManager.cs (modified: added DrawMineralLabels)
│   │   │   └── ManagerDebugService.cs
│   │   └── MicroTasks/
│   │       └── CustomMiningTask.cs
│   │
│   └── Reference Files (DO NOT EDIT):
│       ├── Just In Time Mining.MD (reference)
│       ├── Other .MD files (references)
│       └── Sharky/ (framework code)
│
└── Build Output:
    └── bin/Debug/net9.0/
        └── BabySharkBot.dll
```

---

## Key Files Modified This Session

| File | Changes | Impact |
|------|---------|--------|
| `BaseDtos.cs` | Added OrderedMineral class, OrderedMainMinerals field | Data structure for greedy chain |
| `InitialMapData.cs` | Fixed Z=12, added greedy ordering algorithm | COM visualization works, minerals ordered |

---

## Implementation Timeline

```
Session Flow:
1. Identified: Crosshairs not showing (Z=0 was underground)
2. Fixed: Changed Z to 12 (terrain is 0-2)
3. Analyzed: Worker label drawing pattern
4. Discovered: Same pattern works for COMs
5. Planned: Greedy mineral ordering algorithm
6. Implemented: OrderedMineral class + GreedyOrderMinerals method
7. Integrated: Called in InitialMapData at right time
8. Documented: 8 comprehensive reference documents
9. Tested: Build succeeds, ready for game run
```

---

## Status Summary

| Component | Status | Reference |
|-----------|--------|-----------|
| COM Visualization | ✅ Fixed (Z=12) | TESTING_CHECKLIST.md |
| OrderedMineral Class | ✅ Complete | GREEDY_MINERAL_ORDERING.md |
| Greedy Algorithm | ✅ Implemented | ARCHITECTURE_DIAGRAM.md |
| Mineral Label Service | ✅ Implemented | MINERAL_LABEL_DRAWING.md |
| Mineral Label Registration | ✅ Complete | MINERAL_LABEL_INTEGRATION.md |
| Mineral Label Drawing | ✅ Complete | MINERAL_LABEL_VISUAL_GUIDE.md |
| Integration | ✅ Complete | SESSION_SUMMARY.md |
| Documentation | ✅ Complete | This index |
| Build | ✅ Successful | IMPLEMENTATION_STATUS.md |
| Ready to Test | ✅ Yes | QUICK_REFERENCE.md |

---

## Next Phase

After verifying everything works in-game:
1. Use OrderedMainMinerals for worker assignment
2. Create F1-F4 (far) and N1-N4 (near) labels
3. Route workers to minerals
4. Add opponent visualization
5. Implement vespene ordering

→ See: `ARCHITECTURE_DIAGRAM.md` → "Future Phase" section

---

## Quick Commands

### Build
```powershell
dotnet build BabySharkBot
```

### Run Game
```
Launch SC2 API with BabyShark
Check console for initialization messages
```

### Check Build Output
```powershell
ls BabySharkBot/bin/Debug/net9.0/
```

---

## Contact Reference

If you need to understand:
- **Visualization pattern**: DRAWING_PATTERN_GUIDE.md
- **Greedy algorithm**: GREEDY_MINERAL_ORDERING_VISUAL.md
- **How to use data**: QUICK_REFERENCE.md
- **System architecture**: ARCHITECTURE_DIAGRAM.md
- **What's working**: TESTING_CHECKLIST.md

Everything is documented. Start with the scenario that matches your need above! 🎯

---

## Last Updated
Session: Greedy Mineral Ordering Implementation  
Date: This session  
Status: ✅ Complete, ready for testing

---

**Start here:** Pick your scenario from "By Scenario" section above, then follow the recommended reading order.

Happy coding! 🚀
