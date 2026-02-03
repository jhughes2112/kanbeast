---
name: Git OneFlow
description: High-throughput git workflow with feature branches, rebasing onto main, and fast-forward merges. No PRs or reviews required.
dependencies: git
---

## Overview
This skill implements a simplified OneFlow branching strategy optimized for autonomous agent work. Feature branches are rebased onto the latest main and fast-forward merged to maintain a linear history.

## When to Use
- Starting work on a new ticket (create feature branch)
- Completing work on a ticket (rebase, test, merge)
- Syncing a feature branch with latest main (rebase)

## Branch Naming
```
feature/<ticket-id>-<short-description>
```
Example: `feature/TKT-123-add-user-api`

## Workflow Commands

### 1. Start Feature Branch
Create a new feature branch from latest main:
```bash
git fetch origin
git checkout main
git pull origin main
git checkout -b feature/<ticket-id>-<short-description>
```

### 2. Commit Changes
Make atomic commits with descriptive messages:
```bash
git add <files>
git commit -m "<ticket-id>: <brief description of change>"
```

Commit message format:
- Start with ticket ID
- Present tense ("Add" not "Added")
- Brief but descriptive (50 char limit for subject)

Example: `TKT-123: Add GetUserById method to UserRepository`

### 3. Sync with Main (Rebase)
Before merging, rebase onto latest main:
```bash
git fetch origin
git rebase origin/main
```

If conflicts occur:
1. Resolve conflicts in each file
2. `git add <resolved-files>`
3. `git rebase --continue`
4. If unresolvable, `git rebase --abort` and report blocker

### 4. Re-test After Rebase
After rebasing, always run tests to ensure nothing broke:
```bash
dotnet build
dotnet test
```

If tests fail after rebase:
1. Fix the issues
2. Amend or add commits
3. Do NOT proceed to merge

### 5. Fast-Forward Merge to Main
Once tests pass, merge the feature branch:
```bash
git checkout main
git pull origin main
git merge --ff-only feature/<ticket-id>-<short-description>
git push origin main
```

If `--ff-only` fails (main has advanced):
1. Return to feature branch
2. Rebase again (step 3)
3. Re-test (step 4)
4. Retry merge

### 6. Cleanup
After successful merge:
```bash
git branch -d feature/<ticket-id>-<short-description>
git push origin --delete feature/<ticket-id>-<short-description>
```

## Complete Merge Sequence
One-shot command sequence for completing a ticket:
```bash
# Ensure we're on the feature branch
git checkout feature/<ticket-id>-<short-description>

# Rebase onto latest main
git fetch origin
git rebase origin/main

# Run tests
dotnet build
dotnet test

# If tests pass, merge
git checkout main
git pull origin main
git merge --ff-only feature/<ticket-id>-<short-description>
git push origin main

# Cleanup
git branch -d feature/<ticket-id>-<short-description>
```

## Error Handling

### Rebase Conflicts
```
## Conflict Report

**Conflicting Files:**
- `<file1>`
- `<file2>`

**Resolution:**
<Describe how conflicts were resolved>

**Status:** Resolved | Blocked (need human input)
```

### Merge Fails (Not Fast-Forward)
Main has diverged. Re-run the rebase sequence.

### Push Rejected
Another process pushed to main. Pull and retry:
```bash
git pull origin main --rebase
git push origin main
```

## Rules
- NEVER force push to main
- NEVER merge without rebasing first
- NEVER merge without passing tests
- Keep feature branches short-lived (merge within hours, not days)
- One ticket = one feature branch
