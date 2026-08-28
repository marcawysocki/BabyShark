# Mining telemetry comparison: frames 0-120

Sources:

- BabyShark: `C:\Users\marca\source\repos\BabyShark\BabySharkBot\bin\Debug\net9.0\data\mining_tests\worker_commands_BabyShark_20260824192644.jsonl`
- Sharky_Measured: `C:\Users\marca\source\repos\Sharky_Measured\SharkyZergExampleBot\bin\Debug\net9.0\data\mining_tests\worker_commands_Sharky_20260824181504.jsonl`

## Scope and matching

Records were limited to `GameFrame <= 120`. Workers were matched by exact Frame-0 `WorkerX`, `WorkerY`, and `WorkerType`, not by `WorkerTag`.

- BabyShark records through frame 120: 176
- Sharky records through frame 120: 172
- Exact Frame-0 spatial matches: 12

## Aggregate command totals

| Bot | HARVEST_GATHER (3666) | HARVEST_RETURN (3667) | MOVE (16) | SMART (1) |
|---|---:|---:|---:|---:|
| BabyShark | 63 | 8 | 102 | 3 |
| Sharky_Measured | 48 | 12 | 92 | 20 |

First and last recorded frames by ability:

| Bot | Ability | First frame | Last frame |
|---|---|---:|---:|
| BabyShark | HARVEST_GATHER | 1 | 114 |
| BabyShark | HARVEST_RETURN | 116 | 120 |
| BabyShark | MOVE | 0 | 92 |
| BabyShark | SMART | 91 | 111 |
| Sharky_Measured | HARVEST_GATHER | 1 | 118 |
| Sharky_Measured | HARVEST_RETURN | 98 | 113 |
| Sharky_Measured | MOVE | 0 | 117 |
| Sharky_Measured | SMART | 69 | 119 |

## First recorded returns

BabyShark's first `HARVEST_RETURN` in the telemetry is frame 116:

```text
frame=116 worker=4353163265 pos=131.01465,24.310059 queue=False source=BabySharkMiningManager
```

Sharky_Measured's first `HARVEST_RETURN` in the telemetry is frame 98:

```text
frame=98 worker=4354473985 pos=127.22168,21.371826 queue=False source=MicroManager
```

This is telemetry command-emission timing. It should be compared separately from replay-observed command/handoff timing, which can occur a few frames later.

## Spatially matched worker results

`firstGather` and `firstReturn` are shown as `Sharky/BabyShark`. A blank first-return value means no `HARVEST_RETURN` record for that matched worker through frame 120.

| Frame-0 XY | Sharky tag | BabyShark tag | First gather | First return | Sharky command counts | BabyShark command counts |
|---|---:|---:|---:|---:|---|---|
| (125.5, 21.5) | 4354473985 | 4354473985 | 1/1 | 98/118 | MOVE 9, GATHER 4, SMART 2, RETURN 2 | MOVE 8, GATHER 5, RETURN 3 |
| (126.5, 21.5) | 4353949697 | 4353949697 | 1/1 | 111/none | MOVE 8, GATHER 3, SMART 2, RETURN 2 | MOVE 8, GATHER 5 |
| (127.5, 21.5) | 4353425409 | 4353425409 | 1/1 | none/none | MOVE 7, GATHER 5, SMART 1 | MOVE 8, GATHER 7, SMART 2 |
| (128.5, 21.5) | 4352901121 | 4352901121 | 1/1 | 99/none | MOVE 9, GATHER 4, SMART 2, RETURN 2 | MOVE 8, GATHER 5 |
| (129.5, 21.5) | 4352376833 | 4352376833 | 1/1 | 107/none | MOVE 8, GATHER 4, SMART 2, RETURN 1 | MOVE 8, GATHER 5 |
| (130.5, 22.5) | 4352114689 | 4352114689 | 1/1 | none/none | MOVE 6, GATHER 4, SMART 1 | MOVE 8, GATHER 5 |
| (130.5, 21.5) | 4351852545 | 4351852545 | 1/1 | 105/none | MOVE 8, GATHER 4, SMART 2, RETURN 1 | MOVE 8, GATHER 5 |
| (130.5, 25.5) | 4353687553 | 4353687553 | 1/1 | none/none | MOVE 5, GATHER 3, SMART 1 | MOVE 8, GATHER 6, SMART 1 |
| (130.5, 24.5) | 4353163265 | 4353163265 | 1/1 | 103/116 | MOVE 8, GATHER 4, SMART 2, RETURN 1 | MOVE 8, GATHER 5, RETURN 5 |
| (130.5, 27.5) | 4354736129 | 4354736129 | 1/1 | none/none | MOVE 6, GATHER 4, SMART 1 | MOVE 8, GATHER 5 |
| (130.5, 26.5) | 4354211841 | 4354211841 | 1/1 | 99/none | MOVE 10, GATHER 5, SMART 2, RETURN 2 | MOVE 8, GATHER 5 |
| (130.5, 23.5) | 4352638977 | 4352638977 | 1/1 | 109/none | MOVE 8, GATHER 4, SMART 2, RETURN 1 | MOVE 14, GATHER 5 |

## Interpretation

The attached telemetry shows that both bots issue their first gather commands at frame 1 for every exact Frame-0 spatial match. The difference is after gathering:

- Sharky_Measured emits 12 `HARVEST_RETURN` records through frame 120, with the first at frame 98.
- BabyShark emits 8 `HARVEST_RETURN` records through frame 120, with the first at frame 116.
- The spatial worker at `(130.5, 24.5)` returns at frame 103 in Sharky and frame 116 in BabyShark.
- The spatial worker at `(125.5, 21.5)` returns at frame 98 in Sharky and frame 118 in BabyShark.
- Several spatially matched BabyShark workers have no `HARVEST_RETURN` record by frame 120, while the corresponding Sharky workers return at frames 99, 105, 107, 109, and 111.

The telemetry records are bot-emitted records. They establish what each bot's instrumentation says it issued; replay analysis remains the independent check of what SC2 recorded receiving.
