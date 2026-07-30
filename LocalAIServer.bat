@echo off
title BabyShark Interactive AI Terminal
cls

:: 1. Point the network environment directly to your Linux Mint Server
set OLLAMA_HOST=192.168.0.101

:: 2. Change directory directly into your Visual Studio project repository
cd /d "C:\Users\marca\source\repos\BabyShark\"

echo ======================================================================
echo             BABYSHARK STARCRAFT II AI PIPELINE TERMINAL
echo ======================================================================
echo  [SYSTEM] Connected to Server via 192.168.0.101:11434
echo  [PATH]   Active in C:\Users\marca\source\repos\BabyShark\
echo ======================================================================
echo.
echo Select an option:
echo [1] Launch Interactive Live Chat Window (Bypass LocalPilot)
echo [2] Pipe Entire Codebase for an Architecture/Mining Loop Review
echo [3] Exit Terminal
echo.

set /p choice="Enter choice (1-3): "

if "%choice%"=="1" goto CHAT
if "%choice%"=="2" goto PIPELINE
if "%choice%"=="3" exit

:CHAT
cls
echo Starting direct interactive console... Type /exit to leave the chat.
echo ----------------------------------------------------------------------
powershell -Command "ollama run hf.co/nerkyor/Qwen3.5-122B-A10B-44GB-GPT5.6Sol-SFT-LynnStyle-GGUF"
pause
goto :EOF

:PIPELINE
cls
echo [PROCESSING] Packing all .cs and .md files from BabyShark...
echo [SENDING] Passing repository context to Qwen 122B MoE...
echo ----------------------------------------------------------------------
powershell -Command "Get-ChildItem -Recurse -Include *.cs, *.md | Get-Content | ollama run hf.co/nerkyor/Qwen3.5-122B-A10B-44GB-GPT5.6Sol-SFT-LynnStyle-GGUF 'You are analyzing the full BabyShark StarCraft II AI bot framework. Read the codebase architecture, check WorkerPatchAssignmentService.cs, and provide an optimization report for the harvesting loop based on your Just In Time Mining specifications.'"
echo ----------------------------------------------------------------------
echo Review completed. Press any key to return to the desktop.
pause
exit
