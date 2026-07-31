The user wants a structured analysis of the BabyShark codebase, specificall[11D[K
specifically to generate context layers for a downstream Kimi AI assistant.[10D[K
assistant. The prompt emphasizes "Just In Time Mining", budget limits (<256[5D[K
(<256 files), and prioritizing custom scripts over the base `sharknice/Shar[15D[K
`sharknice/Sharky` framework.

I need to look at the file list provided in the prompt to identify:
1. **BabyShark specific files**: These are the core task-related files (e.g[4D[K
(e.g., `CustomMiningTask.cs`, `JitPrepositionService.cs`, `MineralReturnRat[17D[K
`MineralReturnRateTrackerService.cs`).
2. **Active development context**: The "New Promt for Just In Time Mining.m[8D[K
Mining.md" and related documentation suggest JIT mining is a key focus.
3. **Service layers**: `BabySharkMiningManager.cs` and managers like `CcaMa[6D[K
`CcaManager.cs` are crucial for the mining logic.
4. **Base framework**: I will largely ignore files from `sharknice/Sharky` [K
unless they are directly referenced by BabyShark's unique features (like `P[2D[K
`ProcessVisableUnits.cs` which seems to handle unit visibility).

The goal is to produce a clean markdown file named 'KimiK3.md' with direct [K
GitHub raw links, but the prompt also asks for specific sections: Reasoning[9D[K
Reasoning Context, Task Decomposition, and then the actionable KimiK3.md li[2D[K
list. I will structure my response accordingly, prioritizing JIT mining opt[3D[K
optimization targets and worker allocation routines as requested.

Key files to highlight based on the "Just In Time Mining" context:
- `BabySharkBot/MicroTasks/CustomMiningTask.cs` - The core JIT task impleme[7D[K
implementation.
- `BabySharkBot/Services/JitPrepositionService.cs` - JIT prepositioning ser[3D[K
service.
- `BabySharkBot/Managers/BabySharkMiningManager.cs` - Mining manager for co[2D[K
coordinating workers and tasks.
- `BabySharkBot/Services/MineralReturnRateTrackerService.cs` - Tracking min[3D[K
mineral return rates, crucial for JIT optimization.
- `BabySharkBot/New Promt for Just In Time Mining.md` - The current prompt/[7D[K
prompt/instruction file for JIT mining.
- `BabySharkBot/BabySharkBot.cs` and `BabySharkBot/Program.cs` - Entry poin[4D[K
points and main bot logic.
- `BabySharkBot/Services/chrisCrossAppleSause.cs` - Explicitly mentioned as[2D[K
as a critical worker initialization logic (from the prompt's "NAMES RESOLUT[7D[K
RESOLUTION MAPPINGS" section).

I will structure the response to first provide the domain knowledge transfe[7D[K
transfer (Reasoning Context), then break down the JIT mining task into acti[4D[K
actionable sub-tasks, and finally generate the KimiK3.md file list with dir[3D[K
direct links.
</think>

===START_REASONING_CONTEXT===
BabyShark 的 Just In Time Mining (JIT) 系统是目前开发的核心焦点。它不是简单[K
的抢矿，而是基于 `MineralReturnRateTrackerService` 动态计算矿工的回矿效率，[K
并配合 `JitPrepositionService` 在矿脉上“呼吸式”移动以最大化采集频率。

**核心文件映射
