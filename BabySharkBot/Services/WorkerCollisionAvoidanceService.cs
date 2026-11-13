using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Sharky;
using BabySharkBot.Setup;

namespace BabySharkBot.Services
{
    public enum WorkerCollisionIntervention
    {
        /// <summary>No intervention; the instruction list executes normally.</summary>
        None,
        /// <summary>A mineral-walk SMART was issued this frame; skip list execution.</summary>
        BeginDetour,
        /// <summary>The detour is still running; keep skipping list execution.</summary>
        ContinueDetour,
        /// <summary>The pair is clear or passed; the mining manager re-issues the suspended list row once.</summary>
        EndDetour
    }

    /// <summary>
    /// Worker-collision pass-through detection, implemented as a service to
    /// BabySharkMiningManager instead of a competing IManager. Detects the verified
    /// pass-through event — a non-carrying worker on a head-on course with a carrying
    /// worker (T1 walking out to its mineral meeting T2 returning cargo) — and directs
    /// the mining manager to SMART the worker onto its own current mineral target so
    /// the two workers overlap instead of pathing around each other (mineral walk).
    ///
    /// Detection has two ranges: contact range (~0.35u, about to bump — always walk
    /// through) and oncoming range (2.0u — only when the pair is CLOSING, i.e. moving
    /// toward each other; at 2.0u SC2 pathing has not yet committed both workers to
    /// the long avoidance lines, so the mineral-walk SMART still lands in time to
    /// prevent the dodge). Frame-over-frame positions are kept here to decide "closing".
    ///
    /// Pass-through: once the detouring worker's x,y passes the avoided worker's x,y
    /// (dot of the current offset with the start offset goes negative), the detour ends
    /// immediately and the mining manager re-issues the suspended row — for an outbound
    /// worker that is its Move command. Both workers then repel each other along their
    /// move vectors, which is the collision-mechanics bump boost toward their targets.
    ///
    /// ALREADY-HEADING SKIP: no detour begins when the worker's current order already
    /// targets its own mineral (TargetUnitTag == ownMineralTag, e.g. a list-issued
    /// harvest or an executing queued gather). The worker is already mineral-walking;
    /// a detour would only freeze its instruction list and, if it is mid-gather,
    /// reset its dwell.
    ///
    /// LIST compatibility: while a detour runs, the mining manager suspends the worker's
    /// instruction list WITHOUT advancing the instruction pointer, so the LIST
    /// instructions resume where they left off (e.g. the move down to the hatchery turn
    /// point, then the queued return).
    /// </summary>
    public sealed class WorkerCollisionAvoidanceService
    {
        private const float WorkerPassThroughRange = 0.35f;
        // Oncoming detection begins 0.5u of travel sooner (owner spec 2026-10-12):
        // 2.0u instead of 1.5u — at typical worker walk speed (~0.2-0.3 u/frame) that
        // is roughly 2 frames earlier, so the mineral-walk SMART fires before SC2
        // pathing commits both workers to the long dodge lines (the worker visibly
        // swerving before the detour's red phase begins).
        private const float OncomingDetectionRange = 2.0f;
        private const float ClosingEpsilon = 0.0001f;
        // Safety valve: a detour normally lasts well under 30 frames. If the pair never
        // separates (e.g. both workers park on the same mineral), force the resume so the
        // worker's instruction list cannot stay frozen forever.
        private const int MaxDetourFrames = 100;

        // Last observed position per worker, for the closing-pair test. Only workers the
        // service has been asked about appear here; entries for dead/absent workers are
        // dropped lazily on lookup failure.
        private readonly Dictionary<ulong, Vector2Dto> _previousPositions = new Dictionary<ulong, Vector2Dto>();

        /// <summary>
        /// Decides this frame's intervention for one worker. <paramref name="action"/> is
        /// set only for <see cref="WorkerCollisionIntervention.BeginDetour"/>.
        /// </summary>
        public WorkerCollisionIntervention GetIntervention(
            RuntimeWorkerState runtimeWorker,
            AssignedWorkerDto assignedWorker,
            IReadOnlyList<AssignedWorkerDto> allAssignedWorkers,
            ObservationSnapshotDto snapshot,
            int currentFrame,
            out SC2APIProtocol.Action action)
        {
            action = null;
            if (runtimeWorker == null || assignedWorker == null || snapshot?.SelfUnits == null)
            {
                return WorkerCollisionIntervention.None;
            }

            if (Settings.ccaMining || Settings.SimulatedStartActive)
            {
                return WorkerCollisionIntervention.None;
            }

            if (!snapshot.SelfUnits.TryGetValue(runtimeWorker.UnitTag, out var observedSelf)
                || observedSelf?.Position == null)
            {
                return WorkerCollisionIntervention.None;
            }

            var selfPosition = observedSelf.Position;
            if (runtimeWorker.CollisionDetourActive)
            {
                var intervention = ContinueOrEndDetour(
                    runtimeWorker, assignedWorker, allAssignedWorkers, snapshot, selfPosition, currentFrame);
                RememberPosition(runtimeWorker.UnitTag, selfPosition);
                return intervention;
            }

            // Pass-through policy (same as the verified steady-state runs): only a
            // NON-carrying worker mineral-walks through a carrying one. Two carrying
            // workers keep their return paths; two empty workers repath around each other.
            if (runtimeWorker.IsCarrying)
            {
                RememberPosition(runtimeWorker.UnitTag, selfPosition);
                return WorkerCollisionIntervention.None;
            }

            var nearestCarrying = FindNearestCarryingWorker(
                runtimeWorker.UnitTag, selfPosition, allAssignedWorkers, snapshot, OncomingDetectionRange);
            if (nearestCarrying.UnitTag == 0)
            {
                RememberPosition(runtimeWorker.UnitTag, selfPosition);
                return WorkerCollisionIntervention.None;
            }

            var inContact = nearestCarrying.DistanceSquared <= WorkerPassThroughRange * WorkerPassThroughRange;
            if (!inContact
                && !IsClosing(runtimeWorker.UnitTag, nearestCarrying.UnitTag, selfPosition, nearestCarrying.Position))
            {
                // Within 1.5u but not closing — the pair is separating or crossing at an
                // angle; pathing will handle it and a SMART here would fight it.
                RememberPosition(runtimeWorker.UnitTag, selfPosition);
                return WorkerCollisionIntervention.None;
            }

            // Corridor test (owner spec 2026-11-08 20:22): closing inside 2.0u
            // is not enough — T3 mineral-walked when T1/T2 passed nearby on
            // courses that never cross ("T3 will not encounter another worker
            // in its corridor ... these workers are not on the same path").
            // Project both workers' frame velocities to their closest point of
            // approach; only when the pair actually comes within the contact
            // corridor within the next few frames do the paths intersect and
            // the detour help. Head-on same-line meetings (the verified
            // pass-through / cross-bump cases) have CPA ~ 0 and still trigger.
            if (!inContact
                && !PathsIntersect(runtimeWorker.UnitTag, nearestCarrying.UnitTag, selfPosition, nearestCarrying.Position, out var closestApproach, out var framesToApproach))
            {
                Console.WriteLine($"[WORKER COLLISION] frame={currentFrame} worker={runtimeWorker.UnitTag} command=SKIPPED reason=paths-diverge avoided={nearestCarrying.UnitTag} closest={closestApproach:F2} inFrames={framesToApproach:F1}");
                RememberPosition(runtimeWorker.UnitTag, selfPosition);
                return WorkerCollisionIntervention.None;
            }

            // Own-mineral resolution (Pattern B fallout, run 060735): under
            // --staymin the four second miners switch minerals after their
            // first flip but Mti never advances, so MiningTargets[Mti] stays
            // the FIRST mineral (Y5's BB) while the book walks the worker to
            // its new one — a detour Smart to the stale mineral fights the
            // book (Y5 yanked off its BA line f179-205). Prefer the mineral
            // on the worker's CURRENT instruction row, then a few rows ahead
            // (the walk direction), then behind; fall back to Mti.
            var ownMineralTag = ResolveOwnMineralTag(runtimeWorker, assignedWorker, snapshot);
            if (ownMineralTag == 0
                || !snapshot.Minerals.TryGetValue(ownMineralTag, out var mineral)
                || mineral == null
                || mineral.UnitTag != ownMineralTag)
            {
                Console.WriteLine($"[WORKER COLLISION] frame={currentFrame} worker={runtimeWorker.UnitTag} command=DETOUR-SKIPPED reason=no-verified-own-mineral mti={assignedWorker.Mti} avoided={nearestCarrying.UnitTag}");
                RememberPosition(runtimeWorker.UnitTag, selfPosition);
                return WorkerCollisionIntervention.None;
            }

            // Already mineral-walking: if the worker's current order already targets its
            // own mineral (the list issued a harvest/gather to it, e.g. CCAw#4, or a
            // queued gather is executing), the worker is already doing exactly what a
            // detour SMART would do. Beginning a detour here is a redundant re-order
            // that suspends the instruction list for no benefit — and if the worker is
            // already gathering, the suspended row re-issue can pull it off the mineral
            // and reset its dwell. Let the existing order run.
            if (observedSelf.TargetUnitTag == ownMineralTag)
            {
                Console.WriteLine($"[WORKER COLLISION] frame={currentFrame} worker={runtimeWorker.UnitTag} command=SKIPPED reason=already-heading-to-own-mineral targetMineral={ownMineralTag} avoided={nearestCarrying.UnitTag} trigger={(inContact ? "contact" : "oncoming-closing")} orders=[{string.Join(",", observedSelf.OrderAbilityIds)}] listIndex={runtimeWorker.CurInstrIdx}/{(runtimeWorker.Instructions?.Count ?? 0)}");
                RememberPosition(runtimeWorker.UnitTag, selfPosition);
                return WorkerCollisionIntervention.None;
            }

            // Parked-at-my-mineral skip (2026-11-07, Pattern A run 171404/172234
            // analysis): when the avoided carrier is STATIONARY and parked AT the
            // walker's own mineral (a seated harvester, or a partner frozen
            // post-pickup on the stand point), a detour SMART does what the
            // arrival gate's queued Smart will do 1-2 frames later, but suspends
            // the book and starts the engine Gather deceleration ~1u out —
            // measured cost ~10-13f on T1/Y1 out legs (the f113 Avoid-partner
            // detour: T1 arrived with speed 0.154 u/f, was Gather-decelerated to
            // a stop, then micro-adjusted 7f more). The trigger range (2.0u from
            // the blocker) already bounds the walker's distance to its mineral.
            // Let the list keep running: the gate's Move-SMH + queued Smart lands
            // the phase-through, and the engine paths the final approach around
            // the parked body.
            if (mineral.Position != null
                && nearestCarrying.Position != null
                && IsStationary(nearestCarrying.UnitTag, nearestCarrying.Position)
                && DistanceSquared(nearestCarrying.Position, mineral.Position) <= BlockerAtMineralRange * BlockerAtMineralRange)
            {
                Console.WriteLine($"[WORKER COLLISION] frame={currentFrame} worker={runtimeWorker.UnitTag} command=SKIPPED reason=blocker-parked-at-own-mineral targetMineral={ownMineralTag} avoided={nearestCarrying.UnitTag} listIndex={runtimeWorker.CurInstrIdx}/{(runtimeWorker.Instructions?.Count ?? 0)}");
                RememberPosition(runtimeWorker.UnitTag, selfPosition);
                return WorkerCollisionIntervention.None;
            }

            runtimeWorker.CollisionDetourActive = true;
            runtimeWorker.CollisionDetourStartFrame = currentFrame;
            runtimeWorker.CollisionAvoidedUnitTag = nearestCarrying.UnitTag;
            runtimeWorker.CollisionDetourStartOffsetX = selfPosition.X - nearestCarrying.Position.X;
            runtimeWorker.CollisionDetourStartOffsetY = selfPosition.Y - nearestCarrying.Position.Y;
            runtimeWorker.CollisionDetourEngaged = false;
            runtimeWorker.CollisionDetourPrevPairDist = -1f;
            runtimeWorker.CollisionDetourSeparatingFrames = 0;
            action = CreateMineralWalkSmart(runtimeWorker.UnitTag, ownMineralTag);
            Console.WriteLine($"[WORKER COLLISION] frame={currentFrame} worker={runtimeWorker.UnitTag} command=SMART targetMineral={ownMineralTag} event=MINERAL_WALK_START avoided={nearestCarrying.UnitTag} trigger={(inContact ? "contact" : "oncoming-closing")} listIndex={runtimeWorker.CurInstrIdx}/{(runtimeWorker.Instructions?.Count ?? 0)}");
            RememberPosition(runtimeWorker.UnitTag, selfPosition);
            return WorkerCollisionIntervention.BeginDetour;
        }

        private WorkerCollisionIntervention ContinueOrEndDetour(
            RuntimeWorkerState runtimeWorker,
            AssignedWorkerDto assignedWorker,
            IReadOnlyList<AssignedWorkerDto> allAssignedWorkers,
            ObservationSnapshotDto snapshot,
            Vector2Dto selfPosition,
            int currentFrame)
        {
            string endReason = null;
            var detourExceeded = currentFrame - runtimeWorker.CollisionDetourStartFrame > MaxDetourFrames;
            if (detourExceeded)
            {
                endReason = "max-duration";
            }
            else if (HasPassedAvoidedWorker(runtimeWorker, assignedWorker, snapshot, selfPosition, out var passMode))
            {
                endReason = passMode == "hatchery" ? "passed-hatchery" : "passed-offset";
            }
            else
            {
                // Crossed-separating exit (owner spec 2026-11-07: end the detour
                // 2-4 frames after the crossing). Track the distance to the
                // AVOIDED worker specifically — not any carrying worker — and end
                // once the pair has closed and then separated for 2 consecutive
                // frames beyond contact range. The passed-hatchery radial margin
                // alone takes 13-15f when the avoided worker delivers and
                // immediately walks back out (its radial distance grows again),
                // leaving the walker on engine Gather for most of its out leg
                // (Pattern A run 083149: T1 out leg 36f vs T5's 22f same lane).
                // Cannot flip-flop: while the pair is still closing the
                // separating counter never increments, and re-begin requires
                // closing.
                if (runtimeWorker.CollisionAvoidedUnitTag != 0
                    && selfPosition != null
                    && snapshot?.SelfUnits != null
                    && snapshot.SelfUnits.TryGetValue(runtimeWorker.CollisionAvoidedUnitTag, out var avoidedSelf)
                    && avoidedSelf?.Position != null)
                {
                    var pairDist = (float)Math.Sqrt(DistanceSquared(selfPosition, avoidedSelf.Position));
                    if (runtimeWorker.CollisionDetourPrevPairDist >= 0f
                        && pairDist > runtimeWorker.CollisionDetourPrevPairDist + ClosingEpsilon)
                    {
                        runtimeWorker.CollisionDetourSeparatingFrames++;
                    }
                    else
                    {
                        runtimeWorker.CollisionDetourSeparatingFrames = 0;
                    }
                    runtimeWorker.CollisionDetourPrevPairDist = pairDist;

                    if (runtimeWorker.CollisionDetourSeparatingFrames >= 2
                        && pairDist > WorkerPassThroughRange)
                    {
                        endReason = "crossed-separating";
                    }
                }

                if (endReason == null)
                {
                    // Engagement latch: the pair has now actually come together within
                    // contact range at least once during this detour. Until this is
                    // true, "pair-clear" must NOT end the detour — a detour started at
                    // the 2.0u oncoming range would otherwise be killed the very next
                    // frame (the pair is still ~1.9u apart, far beyond the 0.35u
                    // contact range) and the walk would flip-flop on/off every frame.
                    if (!runtimeWorker.CollisionDetourEngaged
                        && IsNearCarryingWorker(runtimeWorker.UnitTag, selfPosition, allAssignedWorkers, snapshot, WorkerPassThroughRange))
                    {
                        runtimeWorker.CollisionDetourEngaged = true;
                    }

                    if (runtimeWorker.CollisionDetourEngaged
                        && !IsNearCarryingWorker(runtimeWorker.UnitTag, selfPosition, allAssignedWorkers, snapshot, WorkerPassThroughRange))
                    {
                        endReason = "pair-clear";
                    }
                }
            }

            if (endReason == null)
            {
                return WorkerCollisionIntervention.ContinueDetour;
            }

            runtimeWorker.CollisionDetourActive = false;
            runtimeWorker.CollisionDetourStartFrame = -1;
            runtimeWorker.CollisionAvoidedUnitTag = 0;
            runtimeWorker.CollisionDetourStartOffsetX = 0;
            runtimeWorker.CollisionDetourStartOffsetY = 0;
            runtimeWorker.CollisionDetourEngaged = false;
            runtimeWorker.CollisionDetourPrevPairDist = -1f;
            runtimeWorker.CollisionDetourSeparatingFrames = 0;
            Console.WriteLine($"[WORKER COLLISION] frame={currentFrame} worker={runtimeWorker.UnitTag} command=END-DETOUR reason={endReason} listIndex={runtimeWorker.CurInstrIdx}/{(runtimeWorker.Instructions?.Count ?? 0)}");
            return WorkerCollisionIntervention.EndDetour;
        }

        // Owner spec 2026-10-14 ("use distance to the Hatchery for when to stop
        // mineral walking"): the detour ends when the RETURNING (avoided) worker
        // is 0.2u NEARER to the hatchery than the detouring walker — i.e. their
        // centers have crossed on the hatchery axis with a 0.2u margin. The old
        // 2D offset dot product fired too early: whenever the pair slid even
        // slightly sideways during the overlap, the offset rotated past 90° and
        // the dot went negative while the returning worker was still coming
        // towards the walker. The offset test is kept only as a fallback for
        // frames where no hatchery position is observable.
        private const float HatcheryCrossMargin = 0.2f;

        private static bool HasPassedAvoidedWorker(
            RuntimeWorkerState runtimeWorker,
            AssignedWorkerDto assignedWorker,
            ObservationSnapshotDto snapshot,
            Vector2Dto selfPosition,
            out string mode)
        {
            mode = null;
            var avoidedTag = runtimeWorker.CollisionAvoidedUnitTag;
            if (avoidedTag == 0 || selfPosition == null)
            {
                return false;
            }

            Vector2Dto otherPosition = null;
            if (snapshot?.SelfUnits != null
                && snapshot.SelfUnits.TryGetValue(avoidedTag, out var other)
                && other?.Position != null)
            {
                otherPosition = other.Position;
            }

            if (otherPosition == null)
            {
                return false;
            }

            var hatcheryPosition = ResolveHatcheryPosition(assignedWorker, snapshot);
            if (hatcheryPosition != null)
            {
                var selfDist = (float)Math.Sqrt(DistanceSquared(selfPosition, hatcheryPosition));
                var otherDist = (float)Math.Sqrt(DistanceSquared(otherPosition, hatcheryPosition));
                if (otherDist + HatcheryCrossMargin <= selfDist)
                {
                    mode = "hatchery";
                    return true;
                }

                return false;
            }

            var startX = runtimeWorker.CollisionDetourStartOffsetX;
            var startY = runtimeWorker.CollisionDetourStartOffsetY;
            if (startX == 0f && startY == 0f)
            {
                return false;
            }

            var currentX = selfPosition.X - otherPosition.X;
            var currentY = selfPosition.Y - otherPosition.Y;
            if (currentX * startX + currentY * startY < 0f)
            {
                mode = "offset";
                return true;
            }

            return false;
        }

        // The walker's hatchery: prefer the town hall recorded on its own mining
        // targets; fall back to the first observed town hall (the 5x5 experiment
        // runs a single hatchery per team). Null when nothing is observable.
        private static Vector2Dto ResolveHatcheryPosition(
            AssignedWorkerDto assignedWorker,
            ObservationSnapshotDto snapshot)
        {
            if (snapshot?.CurrentTownHalls == null || snapshot.CurrentTownHalls.Count == 0)
            {
                return null;
            }

            var townHallUnitId = assignedWorker?.MiningTargets
                ?.Select(target => target?.TownHallUnitId ?? 0)
                .FirstOrDefault(id => id != 0)
                ?? 0;
            if (townHallUnitId != 0
                && snapshot.CurrentTownHalls.TryGetValue(townHallUnitId, out var townHall)
                && townHall?.Position != null)
            {
                return townHall.Position;
            }

            foreach (var candidate in snapshot.CurrentTownHalls.Values)
            {
                if (candidate?.Position != null)
                {
                    return candidate.Position;
                }
            }

            return null;
        }

        private bool IsClosing(ulong selfTag, ulong otherTag, Vector2Dto selfNow, Vector2Dto otherNow)
        {
            if (!_previousPositions.TryGetValue(selfTag, out var selfPrevious) || selfPrevious == null)
            {
                return false;
            }

            if (!_previousPositions.TryGetValue(otherTag, out var otherPrevious) || otherPrevious == null)
            {
                return false;
            }

            return DistanceSquared(selfNow, otherNow) < DistanceSquared(selfPrevious, otherPrevious) - ClosingEpsilon;
        }

        // Closest-point-of-approach corridor test (owner spec 2026-11-08
        // 20:22). Both workers' headings come from their last frame's
        // displacement; the relative-motion CPA gives the pair's closest
        // distance and the frames until it. "Paths intersect" means CPA lies
        // inside the contact corridor (two worker radii, ~0.75u) and arrives
        // within the intervention window. When either side's heading is
        // unknown or near-zero (parked, first observation) the test cannot
        // prove the paths diverge, so it defers to the legacy trigger.
        private const float CollisionCorridorRadius = 0.8f;
        private const float MaxApproachFrames = 8f;
        private const float MinHeadingSpeedSquared = 0.0001f; // ~0.01 u/frame

        private bool PathsIntersect(
            ulong selfTag,
            ulong otherTag,
            Vector2Dto selfNow,
            Vector2Dto otherNow,
            out float closestApproach,
            out float framesToApproach)
        {
            closestApproach = 0f;
            framesToApproach = 0f;
            if (!_previousPositions.TryGetValue(selfTag, out var selfPrevious) || selfPrevious == null
                || !_previousPositions.TryGetValue(otherTag, out var otherPrevious) || otherPrevious == null)
            {
                return true;
            }

            var selfVelocityX = selfNow.X - selfPrevious.X;
            var selfVelocityY = selfNow.Y - selfPrevious.Y;
            var otherVelocityX = otherNow.X - otherPrevious.X;
            var otherVelocityY = otherNow.Y - otherPrevious.Y;
            var selfSpeedSquared = selfVelocityX * selfVelocityX + selfVelocityY * selfVelocityY;
            var otherSpeedSquared = otherVelocityX * otherVelocityX + otherVelocityY * otherVelocityY;
            if (selfSpeedSquared < MinHeadingSpeedSquared
                || otherSpeedSquared < MinHeadingSpeedSquared)
            {
                // Near-stationary side (owner spec 2026-11-09 00:08, doctrine
                // D3): velocity CPA cannot say where a parked/frozen worker
                // will go, but geometry CAN say whether the MOVING worker's
                // course even reaches it. S2 f120: Y4 had just picked up BA
                // (post-pickup freeze, speed ~0) parked 1.38u off S2's BB
                // line — no contact possible, yet the legacy branch detoured
                // S2 into a center-honing Smart that cost the 3 frames the
                // S2×Y3 bump had just won. A carrier parked OFF the walker's
                // ray can never be hit: skip the detour.
                if (otherSpeedSquared < MinHeadingSpeedSquared && selfSpeedSquared >= MinHeadingSpeedSquared)
                {
                    closestApproach = DistancePointToRay(otherNow, selfNow, selfVelocityX, selfVelocityY);
                    framesToApproach = float.MaxValue;
                    return closestApproach <= CollisionCorridorRadius;
                }
                if (selfSpeedSquared < MinHeadingSpeedSquared && otherSpeedSquared >= MinHeadingSpeedSquared)
                {
                    closestApproach = DistancePointToRay(selfNow, otherNow, otherVelocityX, otherVelocityY);
                    framesToApproach = float.MaxValue;
                    return closestApproach <= CollisionCorridorRadius;
                }
                return true; // both parked — defer to the legacy closing trigger
            }

            // Relative motion of the other worker in this worker's frame.
            var relativeX = otherNow.X - selfNow.X;
            var relativeY = otherNow.Y - selfNow.Y;
            var relativeVelocityX = otherVelocityX - selfVelocityX;
            var relativeVelocityY = otherVelocityY - selfVelocityY;
            var relativeSpeedSquared = relativeVelocityX * relativeVelocityX + relativeVelocityY * relativeVelocityY;
            if (relativeSpeedSquared < ClosingEpsilon)
            {
                // Identical courses — the pair never gains on itself.
                closestApproach = (float)Math.Sqrt(relativeX * relativeX + relativeY * relativeY);
                framesToApproach = float.MaxValue;
                return false;
            }

            var tStar = -(relativeX * relativeVelocityX + relativeY * relativeVelocityY) / relativeSpeedSquared;
            if (tStar < 0f)
            {
                // Closest approach is behind them — diverging.
                closestApproach = (float)Math.Sqrt(relativeX * relativeX + relativeY * relativeY);
                framesToApproach = tStar;
                return false;
            }

            framesToApproach = tStar;
            var closestX = relativeX + relativeVelocityX * tStar;
            var closestY = relativeY + relativeVelocityY * tStar;
            closestApproach = (float)Math.Sqrt(closestX * closestX + closestY * closestY);
            return tStar <= MaxApproachFrames
                && closestApproach <= CollisionCorridorRadius;
        }

        // Perpendicular distance from a point to the moving worker's forward
        // ray (position + t·velocity, t ≥ 0). Behind the worker counts as
        // the distance to its current position — already passed, no contact.
        private static float DistancePointToRay(Vector2Dto point, Vector2Dto rayStart, float velocityX, float velocityY)
        {
            var relX = point.X - rayStart.X;
            var relY = point.Y - rayStart.Y;
            var speedSquared = velocityX * velocityX + velocityY * velocityY;
            if (speedSquared < 0.0000001f)
            {
                return (float)Math.Sqrt(relX * relX + relY * relY);
            }
            var t = (relX * velocityX + relY * velocityY) / speedSquared;
            if (t < 0f)
            {
                return (float)Math.Sqrt(relX * relX + relY * relY);
            }
            var closestX = relX - velocityX * t;
            var closestY = relY - velocityY * t;
            return (float)Math.Sqrt(closestX * closestX + closestY * closestY);
        }

        private void RememberPosition(ulong workerTag, Vector2Dto position)
        {
            if (workerTag == 0 || position == null)
            {
                return;
            }

            _previousPositions[workerTag] = new Vector2Dto(position.X, position.Y, position.Z);
        }

        // A stationary blocker within this distance of the walker's own mineral is
        // parked AT that mineral (stand point is ~0.83-1.25u from center) — handled
        // by the arrival gate instead of a detour (see skip above).
        private const float BlockerAtMineralRange = 2.0f;
        private const float StationaryEpsilon = 0.01f;

        // True when the worker's frame-over-frame displacement is under
        // StationaryEpsilon — a seated harvester or a post-pickup-frozen carrier.
        // Unknown history (never observed) counts as NOT stationary, so the
        // detour still begins when we cannot prove the blocker is parked.
        private bool IsStationary(ulong workerTag, Vector2Dto position)
        {
            if (workerTag == 0 || position == null
                || !_previousPositions.TryGetValue(workerTag, out var prev) || prev == null)
            {
                return false;
            }
            return DistanceSquared(position, prev) < StationaryEpsilon * StationaryEpsilon;
        }

        private readonly struct CarryingWorkerCandidate
        {
            public CarryingWorkerCandidate(ulong unitTag, Vector2Dto position, float distanceSquared)
            {
                UnitTag = unitTag;
                Position = position;
                DistanceSquared = distanceSquared;
            }

            public ulong UnitTag { get; }
            public Vector2Dto Position { get; }
            public float DistanceSquared { get; }
        }

        private static CarryingWorkerCandidate FindNearestCarryingWorker(
            ulong workerTag,
            Vector2Dto selfPosition,
            IReadOnlyList<AssignedWorkerDto> allAssignedWorkers,
            ObservationSnapshotDto snapshot,
            float range)
        {
            var nearest = new CarryingWorkerCandidate(0, null, float.MaxValue);
            if (allAssignedWorkers == null || snapshot?.SelfUnits == null || selfPosition == null)
            {
                return nearest;
            }

            var rangeSquared = range * range;
            foreach (var other in allAssignedWorkers)
            {
                if (other == null || other.UnitID == 0 || other.UnitID == workerTag || other.CurrentXY == null)
                {
                    continue;
                }

                if (!snapshot.SelfUnits.TryGetValue(other.UnitID, out var observedOther)
                    || observedOther == null
                    || !observedOther.IsCarrying)
                {
                    continue;
                }

                var distanceSquared = DistanceSquared(selfPosition, other.CurrentXY);
                if (distanceSquared <= rangeSquared && distanceSquared < nearest.DistanceSquared)
                {
                    nearest = new CarryingWorkerCandidate(other.UnitID, other.CurrentXY, distanceSquared);
                }
            }

            return nearest;
        }

        private static bool IsNearCarryingWorker(
            ulong workerTag,
            Vector2Dto selfPosition,
            IReadOnlyList<AssignedWorkerDto> allAssignedWorkers,
            ObservationSnapshotDto snapshot,
            float range)
        {
            return FindNearestCarryingWorker(workerTag, selfPosition, allAssignedWorkers, snapshot, range).UnitTag != 0;
        }

        // Resolves the worker's current destination mineral for the detour
        // Smart. The book's current row is authoritative (Pattern B switchers
        // loop on a mineral Mti no longer names); rows without a mineral tag
        // (waits, jumps) scan ahead — the walk direction — then behind.
        private static ulong ResolveOwnMineralTag(
            RuntimeWorkerState runtimeWorker,
            AssignedWorkerDto assignedWorker,
            ObservationSnapshotDto snapshot)
        {
            var rows = runtimeWorker?.Instructions;
            if (rows != null
                && runtimeWorker.CurInstrIdx >= 0
                && runtimeWorker.CurInstrIdx < rows.Count
                && snapshot?.Minerals != null)
            {
                for (var offset = 0; offset <= 4; offset++)
                {
                    var ahead = runtimeWorker.CurInstrIdx + offset;
                    if (ahead < rows.Count && TryGetRowMineralTag(rows[ahead], snapshot, out var aheadTag))
                    {
                        return aheadTag;
                    }

                    var behind = runtimeWorker.CurInstrIdx - offset;
                    if (offset > 0 && behind >= 0 && TryGetRowMineralTag(rows[behind], snapshot, out var behindTag))
                    {
                        return behindTag;
                    }
                }
            }

            return assignedWorker?.MiningTargets?.ElementAtOrDefault(assignedWorker.Mti)?.ResourceUnitId ?? 0;
        }

        private static bool TryGetRowMineralTag(WorkerInstruction row, ObservationSnapshotDto snapshot, out ulong tag)
        {
            tag = 0;
            if (row == null || row.TargetId == 0)
            {
                return false;
            }

            if (!snapshot.Minerals.TryGetValue(row.TargetId, out var mineral) || mineral == null)
            {
                return false;
            }

            tag = row.TargetId;
            return true;
        }

        private static SC2APIProtocol.Action CreateMineralWalkSmart(ulong workerTag, ulong mineralTag)
        {
            var command = new ActionRawUnitCommand
            {
                AbilityId = (int)Abilities.SMART,
                TargetUnitTag = mineralTag,
                QueueCommand = false
            };
            command.UnitTags.Add(workerTag);
            return new SC2APIProtocol.Action
            {
                ActionRaw = new ActionRaw { UnitCommand = command }
            };
        }

        private static float DistanceSquared(Vector2Dto first, Vector2Dto second)
        {
            if (first == null || second == null)
            {
                return float.MaxValue;
            }

            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }
    }
}
