speedmining_init.pseudo 

function InitializeSpeedMining(workers, mineralPatches, frame):
    closePatches = GetCloseMineralPatches(mineralPatches)
    
    if frame == 0:
        // Step 1: Assign 8 primary workers to close patches
        primaryWorkers = SelectBestPrimaries(workers, count = 8, closePatches)
        AssignPrimariesToPatches(primaryWorkers, closePatches)

        // Step 2: Assign 4 push workers to move toward same patches
        pushWorkers = workers - primaryWorkers
        for w in pushWorkers:
            targetPatch = ChoosePushLane(w, closePatches)
            IssueOrder(w, MOVE, targetPatch.approachPoint)

        // Step 3: Angle optimization for all workers
        for w in workers:
            patch = GetAssignedPatch(w)
            entryAngle = ComputeBestEntryAngle(w, patch)
            if NeedsAngleAdjustment(w, entryAngle):
                IssueOrder(w, MOVE, entryAngle.position)
                QueueOrder(w, GATHER, patch)