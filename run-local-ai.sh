#!/bin/bash
# Target project working directory
TARGET_DIR="$HOME/deployed/BabyShark"
cd "$TARGET_DIR" || exit 1

echo "[ENGINE] Assembling rich-context matrix files..."

# 1. Clear out older output artifacts from the previous execution loop
rm -f reasoning_context.md task_decomposition.md KimiK3_file_index.md KimiK3.md

# 2. Build a unified payload map containing instructions and source code definitions
SYSTEM_PROMPT="You are the BabyShark Master Rich-Context Agentic Framework. Analyze the context and write three distinct sec
tions using explicit structural Markdown headers: '### REASONING CONTEXT', '### TASK DECOMPOSITION', and '### FILE INDEX'."

{
  echo "=== SYSTEM ARCHITECTURE CONTEXT ==="
  if [ -f "./ARCHITECTURE.md" ]; then cat "./ARCHITECTURE.md"; else echo "No ARCHITECTURE.md provided."; fi
  echo -e "\n=== DEVELOPMENT CONVENTIONS ==="
  if [ -f "./CONVENTIONS.md" ]; then cat "./CONVENTIONS.md"; else echo "No CONVENTIONS.md provided."; fi
  echo -e "\n=== OBJECTIVE LEDGER (QWEN.MD) ==="
  if [ -f "./qwen.md" ]; then cat "./qwen.md"; else echo "No qwen.md provided."; fi
} > context_payload.tmp

echo "[ENGINE] Executing contextual analysis loop..."

# 3. Call the API endpoint using standard chat structure parameters
RAW_RESPONSE=$(curl -s http://localhost:11434/api/chat -d "{
  \"model\": \"babyshark-engine:latest\",
  \"messages\": [
    { \"role\": \"system\", \"content\": $(jq -Rs . <<< "$SYSTEM_PROMPT") },
    { \"role\": \"user\", \"content\": $(jq -Rs . < context_payload.tmp) }
  ],
  \"options\": { \"temperature\": 0.2 },
  \"stream\": false
}" | jq -r '.message.content')

# Clean up temp matrix files
rm -f context_payload.tmp

# 4. Parse the response into clean independent artifact structures
echo "[ENGINE] Splitting unified generation into artifact layers..."

# Extract everything between '### REASONING CONTEXT' and the next section header
echo "$RAW_RESPONSE" | sed -n '/### REASONING CONTEXT/,/### TASK DECOMPOSITION/p' | sed '$d' > reasoning_context.md

# Extract everything between '### TASK DECOMPOSITION' and the next section header
echo "$RAW_RESPONSE" | sed -n '/### TASK DECOMPOSITION/,/### FILE INDEX/p' | sed '$d' > task_decomposition.md

# Extract everything from '### FILE INDEX' to the end of the payload
echo "$RAW_RESPONSE" | sed -n '/### FILE INDEX/,$p' > KimiK3_file_index.md

# 5. Validation Check
if [ ! -s reasoning_context.md ] || [ ! -s task_decomposition.md ] || [ ! -s KimiK3_file_index.md ]; then
    echo "[WARNING] One or more context layers generated empty. Creating structural fallbacks..."
    echo "$RAW_RESPONSE" > KimiK3.md
fi

echo "[ENGINE] Multi-artifact matrices generated and finalized successfully."