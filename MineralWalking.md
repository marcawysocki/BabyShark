MineralWalking.md

Mineral Walking in StarCraft II (Sharky & BabyShark Reference)

Overview

Mineral walking is a micro technique where a worker issues a Smart command on a mineral patch, causing the StarCraft II engine to temporarily treat the worker as non‑colliding. This allows the worker to slide through other units instead of bumping into them, improving early‑game income.

Bots use mineral walking to ensure workers reach mineral patches as quickly and smoothly as possible, especially during the first few seconds of the game.

Why Mineral Walking Works

When a worker receives a Smart command targeting a mineral patch:

The worker enters a special “resource interaction” movement state.

Collision with other units is disabled.

The worker can pass through other workers without slowing down.

The worker reaches the mineral patch faster and begins harvesting sooner.

This behavior is built into the SC2 engine and is not specific to bots — human players can do it manually by right‑clicking minerals.

Command Sequence

Bots typically use the following sequence to perform mineral walking:

Move toward the mineral patch

unit.Order(Abilities.MOVE, mineralPoint);

Smart (mineral walk)

unit.Order(Abilities.SMART, mineralTag);

Gather

unit.Order(Abilities.HARVEST_GATHER, mineralTag);

The key step is #2 — the Smart command.This is what activates collision‑free movement.

How Sharky Implements Mineral Walking

Sharky uses several components to coordinate mineral walking:

WorkerMineralWalkingService

MineralWalkingMicroController

RallyWorkersTask

Sharky’s logic:

Identify the optimal mineral patch.

Issue a Move command toward the mineral’s “mineral point” (center of footprint).

Immediately issue a Smart command on the mineral.

Once the worker is close enough, issue a Gather command.

This ensures workers never bump into each other during the opening seconds.

How BabyShark Implements Mineral Walking

BabyShark uses a simplified version of Sharky’s approach:

Move toward the mineral.

Smart command on the mineral (mineral walk).

Gather once in range.

Even with minimal logic, BabyShark still benefits from collision‑free worker movement.

Benefits

Faster worker arrival at mineral patches

Reduced worker bumping

Higher early‑game income (40–60 minerals in the first minute)

More consistent openings for build orders

Summary

Mineral walking is a simple but powerful technique:

Issue a Smart command on a mineral patch to temporarily disable collision and allow workers to slide through other units.

Sharky and BabyShark both use this technique to optimize early‑game economy.