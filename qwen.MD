# Qwen File-Mapping Directive (Strict 50-File Budget)

## Objective
Analyze the codebase structure. Identify the necessary files required to execute our current development tasks. Focus on Just In Time Mining.

## Strict Rules
1. **Prioritize BabyShark**: List my custom scripts and implementation files first.
2. **Filter Out Base Framework**: Completely ignore files from the cloned `sharknice/Sharky` dependency tracking unless a specific class is critically broken or directly referenced by my task.
3. **Budget Limit**: The resulting table must contain fewer than 256 files to keep a safety buffer for the Kimi K3 cloud limit.

## Output Format
Generate a clean markdown list explicitly named 'KimiK3.md'. Format each entry as a direct, absolute GitHub raw link using our repository paths:
https://githubusercontent.com[Path/To/File.cs]

Do not write markdown code blocks or introduction text. Print the link list immediately.
