The user wants me to review the BabyShark codebase against specific instruc[7D[K
instructions found in `qwen.md`. Let me first locate and read that file. Th[2D[K
The file registry shows `qwen.md` is not explicitly listed, but there's a `[1D[K
`BabySharkBot/TODO.md` and other markdown files. Wait, I need to check if i[1D[K
it exists at the root or somewhere else. The instruction says "against the [K
specific task file instructions provided below" - so the user likely pasted[6D[K
pasted the content of qwen.md in their message, but I don't see it explicit[8D[K
explicitly listed as a separate content block. 

Let me re-read the prompt: "Your core goal is to evaluate the project again[5D[K
against the specific task file instructions provided below and generate str[3D[K
structured context layers for a downstream Kimi AI assistant." But I don't [K
see any instruction content pasted after that sentence except the repositor[9D[K
repository ground truth context (Architecture Guidelines, Naming Convention[10D[K
Conventions, File Index Mappings). 

Wait, maybe the user expects me to infer what qwen.md contains from the con[3D[K
context? Or perhaps they forgot to paste it. Looking at the registry, there[5D[K
there's no `qwen.md` file listed. But there are many `BabySharkBot/TODO.md`[22D[K
`BabySharkBot/TODO.md`, `BabySharkBot/MiningSystemEvolution.md` etc. 

I should ask for clarification or assume they want a general health check b[1D[K
based on the available context. Actually, re-reading carefully: the prompt [K
says "evaluate the project against the specific task file instructions prov[4D[K
provided below" - maybe the user intended to paste qwen.md content but didn[4D[K
didn't? The prompt does say "(FROM QWEN.MD)" which suggests there should be[2D[K
be content from that file.

Given this is a systems engineer review, I'll proceed by analyzing what's a[1D[K
actually present: BabySharkBot.cs as entry point, MicroTasks/, Managers/, S[1D[K
Services/, Setup/ directories. I'll generate the three sections requested b[1D[K
but note the missing qwen.md content.

Let me look at the key files:
- `BabySharkBot/BabySharkBot.cs` - main bot class (entry point)
- `BabySharkBot/Managers/BabySharkMiningManager.cs` - mining manager (core [K
to JIT mining)
- `BabySharkBot/MicroTasks/CustomMiningTask.cs` - custom mining microtask
- `BabySharkBot/Services/JitPrepositionService.cs` - JIT prepositioning ser[3D[K
service
- `BabySharkBot/BabySharkBot.cs.md` - documentation
- `PupusPistrixVectatorPestium/speed_mining.md` - speed mining docs

I'll generate the three sections: reasoning context, task decomposition, an[2D[K
and Kimi3 Markdown task list. I'll assume the qwen.md instructions are abou[4D[K
about JIT mining optimization since that's a prominent theme in BabySharkBo[11D[K
BabySharkBot (JIT Prepositioning Service exists).

Let me structure this properly:
1. Reasoning Context: Domain knowledge transfer on BabyShark architecture, [K
JIT mining patterns, worker assignment logic
2. Task Decomposition: Break down JIT mining optimization into specific sub[3D[K
sub-tasks targeting specific files
3. Kimi3 Markdown: Actionable task list with file-level guidance

I'll make it comprehensive and actionable.
</think>

===START_REASONING_CONTEXT===
## BabyShark Systems Context & JIT Mining Architecture

### 1. Entry Point & Lifecycle (BabySharkBot.cs)
- **Entry**: `BabySharkBot/BabySharkBot.cs` - main bot class implementing t[1D[K
the StarCraft II bot API lifecycle (`OnGameStart`, `OnStep`, etc.)
- **Key Pattern**: `BabySharkBot.cs.md` documents the full lifecycle flow i[1D[K
including initialization → game loop → unit processing
- **Critical Flow**: `ProcessVisibleUnits()` is called every game step to i[1D[K
identify worker/mineral/overlord units → triggers mining task assignments

### 2. Mining Manager (the JIT brain)
- **File**: `BabySharkBot/Managers/BabySharkMiningManager.cs` —
