# Contributing

## Purpose
This repository contains custom code plus a copied upstream framework. Files under `Sharky/` are treated as a read-only upstream and must not be modified by default. Any change to `Sharky/` requires explicit approval.

## Protected Paths
- `Sharky/` and all subdirectories are protected.
- Modifications under `Sharky/` require explicit owner approval and must be done in a dedicated branch with a clear justification.

## Automation / Assistant Policy
- Automated tools and the assistant must not propose, create, or modify files inside `Sharky/` unless a file-by-file authorization is granted.
- If a suggested fix appears to require changes under `Sharky/`, the assistant will ask for explicit approval and list the exact files.

## Recommended Local Safeguards
1. Work in a custom branch:
   - `git checkout -b my/customizations`

2. Optionally mark tracked `Sharky/` files as skip-worktree:
   - `git ls-files -z "Sharky" | xargs -0 -n1 git update-index --skip-worktree`
   - To undo: `git ls-files -z "Sharky" | xargs -0 -n1 git update-index --no-skip-worktree`

3. Exclude new local files under `Sharky/`:
   - `echo "/Sharky/" >> .git/info/exclude`

4. Add a pre-commit hook (local) to block accidental changes:
   - Create `.githooks/pre-commit`:
     ```bash
     #!/bin/sh
     if git diff --cached --name-only | grep -qE '^Sharky/' ; then
       echo "ERROR: Changes to 'Sharky/' are protected. Revert or move your changes and try again."
       exit 1
     fi
     exit 0
     ```
   - Enable hooks locally:
     - `git config core.hooksPath .githooks`
     - `chmod +x .githooks/pre-commit`

5. CI / PR checks
   - Add CI step that fails PRs touching `Sharky/` unless labeled/approved.

### Install local hooks (one-time)
To activate the local pre-commit hook on your machine run one of the following in the repository root:

- Manual:
- One-line installer:

Add this step to your local setup notes so the pre-commit validation blocks accidental edits to protected paths.

## Intentional Changes Workflow
- Create branch `sharky/<purpose>`, include justification in PR, request owner review, and tag PR with `sharky-mod`. Document tests and rationale.

## Maw directory contribution guidelines

### Purpose
- `Maw/` contains project-specific customizations. Changes in `Maw/` are allowed and encouraged.
- `Sharky/` is an upstream read-only copy. Avoid modifying any files under `Sharky/` unless there is an approved, documented exception.

### What to document in every Maw change
- Short description of the change.
- Why the change belongs in `Maw/` and not in `Sharky/`.
- Test plan and verification steps (how you validated behavior locally).
- If the change touches behavior that depends on the Sharky framework, list the specific Sharky files and explain why modification would otherwise be required.

### Before you commit
- Run the local validation script: `scripts/validate-protected-paths.sh` (fails if any staged change touches `Sharky/`).
- Ensure unit/integration tests pass and verify minimal manual scenario (e.g., run the bot for 30s on the test map).

### Branching & PR policy
- Use feature branches: `maw/<short-description>` or `fix/<short-description>`.
- PR description must include:
  - Motivation and summary.
  - Files changed in `Maw/` and an explicit statement that `Sharky/` is untouched.
  - Test steps and expected result.
- At least one review from a repo owner is required before merge.
- Tag PR with `maw` label.

### Protected-path exceptions
- Any change that modifies files under `Sharky/` must:
  1. Be opened in a dedicated branch named `sharky/<reason>`.
  2. Contain a clear justification in the PR description and link to an issue documenting the need.
  3. Include test evidence and target reviewers (owner approval required).
  4. Be clearly labeled `sharky-mod`.
- CI will block merges that touch `Sharky/` unless the PR includes `sharky-mod` and owner approval.

### Local safeguards (recommended)
- Create a feature branch: `git checkout -b maw/<desc>`
- Prevent accidental commits to tracked Sharky files (local):  
  `git ls-files -z "Sharky" | xargs -0 -n1 git update-index --skip-worktree`  
  To undo: `git ls-files -z "Sharky" | xargs -0 -n1 git update-index --no-skip-worktree`
- Add local exclude for new files: `echo "/Sharky/" >> .git/info/exclude`

### Verification & release notes
- Add a short changelog entry for user-visible changes in `Maw/CHANGES.md`.
- For behavior affecting game startup, include the exact steps used to reproduce.

### Contact / escalation
- If you think a change truly requires a Sharky modification, open an issue describing the problem and the proposed Maw-only alternative. Tag repository owners and request guidance.