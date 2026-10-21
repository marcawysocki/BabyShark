using System;
using System.Collections.Generic;
using System.Linq;
using BabySharkBot.Setup;

#nullable enable

namespace BabySharkBot.Managers
{
    /// <summary>
    /// Tracks, per worker, the number of consecutive frames the worker has been
    /// genuinely harvesting: stationary (X,Y unchanged) with a gather ability (1183)
    /// aimed at a mineral. Any movement resets the count to zero — a worker can hold
    /// a harvest order while still walking, and only the frames after it stops moving
    /// count as harvest frames.
    ///
    /// Per mineral, exactly one worker is the ACTIVE HARVESTER (the one whose
    /// stationary-1183 episode started first). Additional workers standing at the
    /// mineral with 1183 are WAITERS: their harvest-frame count stays at zero until
    /// the active harvester flips to 1184 (pickup); at that frame the longest-waiting
    /// waiter is promoted and its count starts at zero from the 1184 frame — never
    /// from when it arrived. A mineral with no worker at its footprint has no
    /// harvester and no waiters.
    ///
    /// The cycle length measured for a mineral is the frames from "stopped moving
    /// with 1183" to the 1183->1184 flip (e.g. stops at 22, flips at 68 => 46).
    /// STAGING RELEASE (owner spec 2026-10-20 20:09): a staged worker holds until
    /// the active harvester has ACCUMULATED StagedReleaseHarvestFrames (32) harvest
    /// frames — "All Workers needs to stage until the active mineral has 32 Harvest
    /// frames" (Y4 was leaving YB too early). The older near-completion rule
    /// (ShouldReleaseStagedWorker: release when cycle - ReleaseLeadFrames frames
    /// are done, the 2026-10-16 "at 32 the staging worker should be given the Smart
    /// command" spec) still drives only the collision/wait near-completion check.
    /// </summary>
    public sealed class HarvestFrameTracker
    {
        /// <summary>Ability id at which a worker carries minerals back (HARVEST_RETURN_DRONE).</summary>
        public const int HarvestReturnAbilityId = 1184;
        /// <summary>Fallback cycle length before any cycle has been measured on a mineral.</summary>
        public const int DefaultCycleFrames = 46;
        /// <summary>How many frames ahead of the 1184 flip a staged worker is released.
        /// Owner spec 2026-10-16 (worker collision pass): 32 — "it takes 46 frames to
        /// harvest... at 32 the staging worker should be given the Smart command on the
        /// mineral", so the release fires with 32 harvest frames still on the active
        /// harvester and the ~20-frame walk-in leaves ~12 — inside the ~15-frame window
        /// before the game re-paths a waiting gatherer away. Refines the 31-frame lead
        /// of 2026-10-13.
        /// 2026-10-20 20:09 REFINEMENT (owner spec "All Workers needs to stage until
        /// the active mineral has 32 Harvest frames"): the STAGING release no longer
        /// uses this near-completion lead — it fires once the active harvester has
        /// ACCUMULATED StagedReleaseHarvestFrames frames (Y4 was leaving YB too
        /// early). This constant now drives only the near-completion check
        /// (ShouldReleaseStagedWorker / IsGatheringNearCompletion).</summary>
        public const int ReleaseLeadFrames = 32;

        /// <summary>Owner spec 2026-10-20 20:09 "All Workers needs to stage until the
        /// active mineral has 32 Harvest frames": a staged worker is released once the
        /// mineral's active harvester has ACCUMULATED this many harvest frames —
        /// regardless of where that falls in the cycle. Replaces the 2026-10-16
        /// near-completion lead for staging (Y4 left YB because it was told to harvest
        /// too soon). The free-mineral immediate release stays: no active harvester
        /// means the mineral is free right now.</summary>
        public const int StagedReleaseHarvestFrames = 32;

        // A mining worker's position is essentially constant during the gather phase;
        // walking workers move ~0.2-0.4 units per frame. 0.05u squared is well inside
        // observation noise for a truly stationary worker and well below one walk step.
        private const float StationaryEpsilonSquared = 0.0025f;
        // Beyond this distance from the mineral center a worker is not "at the footprint".
        private const float FootprintRadius = 1.5f;
        private const float FootprintRadiusSquared = FootprintRadius * FootprintRadius;

        private sealed class WorkerHarvestState
        {
            public bool HasLastPosition;
            public Vector2Dto LastPosition = new();
            public ulong MineralTag;
            // True while this worker is the mineral's active harvester; only then does
            // HarvestFrames accumulate. Waiters keep their count at zero.
            public bool IsActiveHarvester;
            public int HarvestFrames;
            public int StationaryEpisodeStartFrame = -1;
            public int LastObservedFrame = -1;
        }

        private sealed class MineralHarvestState
        {
            public Vector2Dto Position = new();
            public ulong ActiveHarvesterTag;
            // FIFO: the longest-waiting waiter is promoted first.
            public readonly List<ulong> WaiterTags = new();
            public int LastMeasuredCycleFrames = -1;
            public int LastCycleEndFrame = -1;
        }

        private readonly Dictionary<ulong, WorkerHarvestState> _workers = new();
        private readonly Dictionary<ulong, MineralHarvestState> _minerals = new();

        /// <summary>Clears all per-worker and per-mineral state (new game).</summary>
        public void Reset()
        {
            _workers.Clear();
            _minerals.Clear();
        }

        /// <summary>Measured cycle (stop-moving-with-1183 -> 1184 flip) for a mineral, or -1.</summary>
        public int GetMeasuredCycleFrames(ulong mineralTag) =>
            _minerals.TryGetValue(mineralTag, out var mineral) ? mineral.LastMeasuredCycleFrames : -1;

        /// <summary>Active harvester's current harvest-frame count for a mineral (0 if none).</summary>
        public int GetActiveHarvestFrames(ulong mineralTag) =>
            _minerals.TryGetValue(mineralTag, out var mineral)
            && mineral.ActiveHarvesterTag != 0
            && _workers.TryGetValue(mineral.ActiveHarvesterTag, out var worker)
                ? worker.HarvestFrames
                : 0;

        /// <summary>True when a worker is at the mineral footprint with gather orders but not yet harvesting (waiting).</summary>
        public bool HasWaiter(ulong mineralTag) =>
            _minerals.TryGetValue(mineralTag, out var mineral) && mineral.WaiterTags.Count > 0;

        /// <summary>True when some worker is actively harvesting this mineral right now.</summary>
        public bool HasActiveHarvester(ulong mineralTag) =>
            _minerals.TryGetValue(mineralTag, out var mineral) && mineral.ActiveHarvesterTag != 0;

        public bool IsActiveHarvester(ulong workerTag) =>
            _workers.TryGetValue(workerTag, out var worker) && worker.IsActiveHarvester;

        /// <summary>
        /// Near-completion condition (collision/wait logic only, via
        /// IsGatheringNearCompletion): the active harvester will flip to 1184 within
        /// ReleaseLeadFrames frames. NOT the staging release — per owner spec
        /// 2026-10-20 20:09 staging holds until the active harvester has ACCUMULATED
        /// StagedReleaseHarvestFrames frames (see HasAccumulatedReleaseFrames).
        /// </summary>
        public bool ShouldReleaseStagedWorker(ulong mineralTag)
        {
            if (!_minerals.TryGetValue(mineralTag, out var mineral) || mineral.ActiveHarvesterTag == 0)
            {
                return false;
            }

            var cycle = mineral.LastMeasuredCycleFrames > 0
                ? mineral.LastMeasuredCycleFrames
                : DefaultCycleFrames;
            return GetActiveHarvestFrames(mineralTag) >= cycle - ReleaseLeadFrames;
        }

        /// <summary>
        /// STAGING release condition (owner spec 2026-10-20 20:09 "All Workers needs
        /// to stage until the active mineral has 32 Harvest frames"): the active
        /// harvester has accumulated StagedReleaseHarvestFrames harvest frames. The
        /// caller combines this with HasActiveHarvester for the free-right-now case.
        /// </summary>
        public bool HasAccumulatedReleaseFrames(ulong mineralTag) =>
            GetActiveHarvestFrames(mineralTag) >= StagedReleaseHarvestFrames;

        /// <summary>
        /// Advances the tracker one frame. workers: live observed workers with position,
        /// order ability ids and first-order target tag. isGatherAbility / ability ids use
        /// the raw ability ids (1183 gather, 1184 return). mineralPosition resolves a
        /// mineral tag to its footprint position; unknown tags are ignored.
        /// </summary>
        public void Update(
            int frame,
            IEnumerable<(ulong Tag, Vector2Dto Pos, IReadOnlyList<int> OrderAbilityIds, ulong TargetUnitTag)> workers,
            Func<int, bool> isGatherAbility,
            Func<ulong, Vector2Dto?> mineralPosition)
        {
            var liveTags = new HashSet<ulong>();

            foreach (var worker in workers ?? Enumerable.Empty<(ulong, Vector2Dto, IReadOnlyList<int>, ulong)>())
            {
                liveTags.Add(worker.Tag);
                var state = GetOrCreateWorker(worker.Tag);

                var moved = state.HasLastPosition
                    && DistanceSquared(state.LastPosition, worker.Pos) > StationaryEpsilonSquared;
                state.LastPosition = worker.Pos;
                state.HasLastPosition = true;
                state.LastObservedFrame = frame;

                var primaryAbility = worker.OrderAbilityIds != null && worker.OrderAbilityIds.Count > 0
                    ? worker.OrderAbilityIds[0]
                    : 0;
                var gatherAbility = isGatherAbility(primaryAbility);

                // Movement resets harvest frames to zero, always — even for the active
                // harvester. A worker walking with 1183 is not harvesting.
                if (moved)
                {
                    state.HarvestFrames = 0;
                    state.StationaryEpisodeStartFrame = -1;
                }

                // Resolve mineral attachment: gather ability aimed at a known mineral.
                var mineralTag = gatherAbility && worker.TargetUnitTag != 0
                    && mineralPosition(worker.TargetUnitTag) != null
                        ? worker.TargetUnitTag
                        : 0;

                if (mineralTag != 0)
                {
                    AttachToMineral(frame, state, worker.Tag, mineralTag, mineralPosition(worker.TargetUnitTag)!);
                }
                else
                {
                    DetachFromMineral(state, worker.Tag, flippedToReturn: primaryAbility == HarvestReturnAbilityId);
                }
            }

            // Workers absent from the observation are gone: release whatever they held.
            foreach (var tag in _workers.Keys.Where(tag => !liveTags.Contains(tag)).ToList())
            {
                DetachFromMineral(_workers[tag], tag, flippedToReturn: false);
                _workers.Remove(tag);
            }
        }

        private WorkerHarvestState GetOrCreateWorker(ulong tag)
        {
            if (!_workers.TryGetValue(tag, out var state))
            {
                state = new WorkerHarvestState();
                _workers[tag] = state;
            }
            return state;
        }

        private void AttachToMineral(int frame, WorkerHarvestState state, ulong workerTag, ulong mineralTag, Vector2Dto mineralPos)
        {
            // The worker no longer belongs to its previous mineral.
            if (state.MineralTag != 0 && state.MineralTag != mineralTag)
            {
                DetachFromMineral(state, workerTag, flippedToReturn: false);
            }

            if (!_minerals.TryGetValue(mineralTag, out var mineral))
            {
                mineral = new MineralHarvestState { Position = mineralPos };
                _minerals[mineralTag] = mineral;
            }

            // Footprint rule: any time there is no worker at the footprint of the mineral,
            // that mineral has no harvesters. Movement already zeroed the frame count.
            var atFootprint = DistanceSquared(state.LastPosition, mineral.Position) <= FootprintRadiusSquared;

            if (!atFootprint)
            {
                // Still travelling toward the mineral; not harvesting, not waiting.
                if (state.MineralTag == mineralTag)
                {
                    DetachFromMineral(state, workerTag, flippedToReturn: false);
                }
                return;
            }

            state.MineralTag = mineralTag;

            if (mineral.ActiveHarvesterTag == 0)
            {
                // No current harvester (initial state, or previous harvester just left):
                // this worker becomes the active harvester. Its count starts NOW — zero —
                // regardless of how long it has been standing here (a promoted waiter
                // only starts accumulating from the previous worker's 1184 frame).
                mineral.ActiveHarvesterTag = workerTag;
                mineral.WaiterTags.Remove(workerTag);
                state.IsActiveHarvester = true;
                state.HarvestFrames = 0;
                state.StationaryEpisodeStartFrame = frame;
                return;
            }

            if (mineral.ActiveHarvesterTag == workerTag)
            {
                // Active harvester, stationary with 1183 at the mineral: one harvest frame.
                // (Movement was already handled above with the reset-to-zero rule.)
                if (state.StationaryEpisodeStartFrame < 0)
                {
                    state.StationaryEpisodeStartFrame = frame;
                }
                state.HarvestFrames++;
                return;
            }

            // Someone else is the active harvester: this worker is a waiter. It stands
            // at the mineral with 1183 but does NOT accumulate harvest frames.
            state.IsActiveHarvester = false;
            state.HarvestFrames = 0;
            state.StationaryEpisodeStartFrame = -1;
            if (!mineral.WaiterTags.Contains(workerTag))
            {
                mineral.WaiterTags.Add(workerTag);
            }
        }

        private void DetachFromMineral(WorkerHarvestState state, ulong workerTag, bool flippedToReturn)
        {
            var mineralTag = state.MineralTag;
            state.MineralTag = 0;
            state.IsActiveHarvester = false;

            if (mineralTag == 0 || !_minerals.TryGetValue(mineralTag, out var mineral))
            {
                state.HarvestFrames = 0;
                state.StationaryEpisodeStartFrame = -1;
                return;
            }

            if (mineral.ActiveHarvesterTag == workerTag)
            {
                // 1183 -> 1184 transition at the mineral: measure the cycle from the
                // stationary episode start to this frame (stop at 22, flip at 68 => 46),
                // then promote the longest-waiting waiter with its count starting at
                // zero from THIS frame.
                if (flippedToReturn && state.StationaryEpisodeStartFrame >= 0)
                {
                    var cycle = state.LastObservedFrame - state.StationaryEpisodeStartFrame;
                    if (cycle > 0)
                    {
                        mineral.LastMeasuredCycleFrames = cycle;
                        mineral.LastCycleEndFrame = state.LastObservedFrame;
                    }
                }

                mineral.ActiveHarvesterTag = 0;
                state.HarvestFrames = 0;
                state.StationaryEpisodeStartFrame = -1;

                var next = mineral.WaiterTags.FirstOrDefault(tag =>
                    _workers.TryGetValue(tag, out var candidate) && candidate.MineralTag == mineralTag);
                if (next != 0)
                {
                    mineral.WaiterTags.Remove(next);
                    mineral.ActiveHarvesterTag = next;
                    var nextState = _workers[next];
                    nextState.IsActiveHarvester = true;
                    // Per spec: the promoted worker's harvest frames start at zero on
                    // the frame the previous worker got its 1184 — not from arrival.
                    nextState.HarvestFrames = 0;
                    nextState.StationaryEpisodeStartFrame = state.LastObservedFrame;
                }
            }
            else
            {
                mineral.WaiterTags.Remove(workerTag);
                state.HarvestFrames = 0;
                state.StationaryEpisodeStartFrame = -1;
            }
        }

        private static float DistanceSquared(Vector2Dto a, Vector2Dto b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }
    }
}
