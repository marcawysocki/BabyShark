# Baseline Mineral Accumulation - Frame Level Data

## Actual Replay Data (Sharky 12 Pool Build)

Frame-by-frame mineral accumulation captured from console logging:

```
MINERALS: Frame 201 (9s) - 115 minerals (change: +5)
MINERALS: Frame 202 (9s) - 120 minerals (change: +5)
MINERALS: Frame 203 (9.1s) - 125 minerals (change: +5)
MINERALS: Frame 205 (9.2s) - 130 minerals (change: +5)
MINERALS: Frame 220 (9.8s) - 135 minerals (change: +5)
MINERALS: Frame 222 (9.9s) - 140 minerals (change: +5)
MINERALS: Frame 223 (10s) - 145 minerals (change: +5)
MINERALS: Frame 225 (10s) - 150 minerals (change: +5)
MINERALS: Frame 248 (11.1s) - 160 minerals (change: +10)
MINERALS: Frame 253 (11.3s) - 165 minerals (change: +5)
MINERALS: Frame 269 (12s) - 170 minerals (change: +5)
MINERALS: Frame 300 (13.4s) - 175 minerals (change: +5)
MINERALS: Frame 303 (13.5s) - 180 minerals (change: +5)
MINERALS: Frame 304 (13.6s) - 185 minerals (change: +5)
MINERALS: Frame 305 (13.6s) - 190 minerals (change: +5)
MINERALS: Frame 333 (14.9s) - 195 minerals (change: +5)
MINERALS: Frame 335 (15s) - 200 minerals (change: +5)
*** 200 MINERALS REACHED AT FRAME 335 (14.96s) ***
```

## Key Milestones

| Minerals | Frame | Time (s) | Notes |
|----------|-------|----------|-------|
| 160 | 248 | 11.1 | **PHASE 1 WORKER PLACEMENT TARGET** - Where JIT should place spawning pool worker |
| 200 | 335 | 14.96 | Where Sharky actually places spawning pool worker |
| **GAP** | **87 frames** | **3.86s** | **Sharky's efficiency loss** |

## Analysis

1. **Mineral accumulation rate**: ~5 minerals per frame on average (from ~9s to 15s)
2. **Sharky's placement delay**: Places worker at 200 minerals (Frame 335) instead of 160 minerals (Frame 248)
3. **Efficiency loss**: 87 frames = 3.86 seconds wasted
4. **JIT improvement potential**: Place worker at Frame 248 (160 minerals), allowing pool to start building while minerals continue to accumulate toward 200 for drones/tech

## Frame Rate Reference
- **SC2 frame rate**: 22.4 fps (1 frame = 0.0446 seconds)
- Frame 248 = 11.07s game time
- Frame 335 = 14.96s game time

## Next Steps
1. Implement Phase 1 JIT system with worker placement at 160 minerals
2. Run replay comparison: JIT placing at Frame 248 vs Sharky at Frame 335
3. Measure actual pool start time with JIT vs current Sharky
4. Capture frame-level timing for Phase 1 worker choreography
