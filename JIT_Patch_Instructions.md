# JIT Items 1–3 — Patch Instructions for the Remaining Files

`BabySharkBuildManager.cs` is delivered as a complete replacement file alongside this
document. These are the exact edits for the other three files.

**Why not complete files for these too?** The uploaded snapshot predates the worker
instruction runtime and the pair-table DTO (it has no `ResolveInstructionPoint`,
`StoreReferencedTargetPoint`, `ExecuteRuntimeWorkerInstructions`, or
`JitPairReturnCalculationDto`). Reconstructing full files from it would delete roughly
600+ lines of your instruction-processing code — the exact failure this project has been
burned by before. If you paste your current `BabySharkMiningManager.cs` and `BaseDtos.cs`,
complete merged replacements can be produced instead.

Apply in order. All anchors are quoted verbatim from the code as described in the canon
record (rounds 2–13).

---

## 1. `BabySharkMiningManager.cs` — Edit 5 + Defect Fix 1

### 1a. Resolve store/use rows by parsing the reference (round 7 answer 1)

In `ResolveInstructionPoint`, immediately **after** the existing guard line

```csharp
if (instruction == null || instruction.Point == WorkerInstructionPoint.None || assignedWorker == null) return null;
```

insert:

```csharp
            // List-owned points: an instruction carrying a pair reference such as
            // "0-TA-TB.HarvestA" resolves the stored coordinate from the pair table.
            // The Mining Manager does not look ahead or calculate points; it reads them.
            if (!string.IsNullOrWhiteSpace(instruction.TargetPointReference))
            {
                return ResolvePairReferencePoint(instruction.TargetPointReference);
            }
```

### 1b. Delete the five dead switch arms (Defect Fix 1 — required for compilation)

In the same `ResolveInstructionPoint` switch, delete the arms that read the six
`MiningTargetDto` Jit fields (these are unreachable now that every store/use row carries
a reference, and the fields themselves are deleted in section 2):

```csharp
                WorkerInstructionPoint.JitHarvestA => target.JitHarvestA,
                WorkerInstructionPoint.JitHarvestB => target.JitHarvestB,
                WorkerInstructionPoint.JitWaitPointA => target.JitWaitPointA,
                WorkerInstructionPoint.JitWaitPointB => target.JitWaitPointB,
                WorkerInstructionPoint.JitReturnPoint => target.JitReturnPoint,
```

Keep the `StoredTarget`, `Harvest`, `Return`, `Staging`, and all `Bump*` arms untouched
(bump is frozen; `Harvest`/`Return` still serve the `Sm*` speed-mining phase).

### 1c. Add the resolver method

Add inside the class (anywhere among the other private methods):

```csharp
        private Vector2Dto ResolvePairReferencePoint(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)
                || _mapData?.MainJitPairReturnCalculations == null)
            {
                return null;
            }

            var dot = reference.LastIndexOf('.');
            if (dot <= 0 || dot == reference.Length - 1)
            {
                return null;
            }

            var key = reference.Substring(0, dot);
            var property = reference.Substring(dot + 1);
            var startIndex = Globals.CurrentStartIndex >= 0 ? Globals.CurrentStartIndex : Settings.CurrentSpawnIndex;
            if (startIndex < 0 || startIndex >= _mapData.MainJitPairReturnCalculations.Count)
            {
                return null;
            }

            var pair = _mapData.MainJitPairReturnCalculations[startIndex]
                ?.FirstOrDefault(candidate =>
                    candidate != null
                    && string.Equals(candidate.PairKey, key, StringComparison.OrdinalIgnoreCase));
            if (pair == null)
            {
                Console.WriteLine($"[PAIR REFERENCE MISSING] frame={_currentFrame} reference={reference} start={startIndex}");
                return null;
            }

            return property switch
            {
                "HarvestA" => HasNonZeroPoint(pair.HarvestA) ? pair.HarvestA : null,
                "HarvestB" => HasNonZeroPoint(pair.HarvestB) ? pair.HarvestB : null,
                "ReturnPoint" => HasNonZeroPoint(pair.ReturnPoint) ? pair.ReturnPoint : null,
                "WaitPointA" => HasNonZeroPoint(pair.WaitPointA) ? pair.WaitPointA : null,
                "WaitPointB" => HasNonZeroPoint(pair.WaitPointB) ? pair.WaitPointB : null,
                _ => null
            };
        }
```

### 1d. `HasNonZeroPoint` helper

Run `grep -n "HasNonZeroPoint" BabySharkMiningManager.cs`. If it is **not** already
defined in this file, add:

```csharp
        private static bool HasNonZeroPoint(Vector2Dto point)
        {
            return point != null && (point.X != 0f || point.Y != 0f);
        }
```

(`_mapData` and `_currentFrame` already exist in this class per the canon record; if your
file reads map data through `Globals.CurrentMapData` instead, replace `_mapData` with that
in 1a/1c.)

---

## 2. `BaseDtos.cs` — Edit 6

Delete the six Jit properties from `MiningTargetDto` (contiguous block, recorded at
BaseDtos.cs:363-368):

```csharp
        public string JitPairKey { get; set; } = string.Empty;
        public Vector2Dto JitWaitPointA { get; set; } = new Vector2Dto();
        public Vector2Dto JitWaitPointB { get; set; } = new Vector2Dto();
        public Vector2Dto JitHarvestA { get; set; } = new Vector2Dto();
        public Vector2Dto JitHarvestB { get; set; } = new Vector2Dto();
        public Vector2Dto JitReturnPoint { get; set; } = new Vector2Dto();
```

(the exact initializer style may differ — delete whatever declarations carry these six
names.)

**Do not touch** `JitPairReturnCalculationDto` — its `FromResourceUnitTag` /
`ToResourceUnitTag` stay (Q-C8a never ruled; AGENTS.md forbids removing serialized
properties without explicit instruction).

Then verify no assignments remain: `grep -rn "JitPairKey\|JitWaitPointA\|JitHarvestA\|JitHarvestB\|JitReturnPoint" BabySharkBot/`
must return only the `TeamPatchAssignmentDto.JitReturnPoint` / `JitWaitPoint` fields
(_kept_ — `JitWaitPoint` is the CCAw storage the frozen bump code reads) and the deleted
`ApplyJitPairTarget` (already gone in the delivered BuildManager). The only writer of the
six deleted fields was `ApplyJitPairTarget`; if `CreateMiningTarget` also initialized
them, remove those initializers.

---

## 3. `Settings.cs` — Defect Fix 2 (version bump, required)

Round 12's wait radius (1.5u) supersedes the `+0.8` runtime value (1.8u), and the old
mineral-level CCAw reuse gate cannot detect stale stored points. The dat file name is
keyed on this constant (MapDataManager), so bumping invalidates all persisted geometry:

```csharp
        // 0.11: hatcheryRadius 5.5 -> 2.75 (round 13). 0.12: wait circle 1.5u on all 8
        // minerals, canonical prefixed pair keys, five-point reuse gate (items 1-3).
        public const string SpeedMiningVersion = "0.12";
```

---

## Verification checklist

1. `dotnet build BabySharkBot\BabySharkBot.csproj` — expect 0 errors.
2. Repo-wide grep: zero hits for `ApplyJitPairTarget`, `JitPairKey`,
   `target.JitHarvestA`, `target.JitWaitPointA`, `target.JitReturnPoint`,
   `harvestDistance + 0.8f`, `Midpoint(`.
3. Frame-0 console: `jit pair table rows=66` for a 12-worker start (11 labels, i≤j),
   and every `StoreTargetPoint`/`UseTargetPoint` row in the frame-0 JSON dump carries a
   canonical `0-{first}-{second}.{Property}` reference.
4. One replayed spawn: `[BUILD START CCAW POINT CALCULATED] ... radius=1.5` for all 8
   minerals (the 0.12 bump forces recalculation; no `CCAW POINT REUSED` at 1.8u).
