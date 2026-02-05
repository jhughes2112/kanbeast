---
name: Finish Branch
description: High throughput git workflow with feature branches, squash then rebase onto main, and fast forward merges. Linear history. No PRs or reviews.
dependencies: git
---

## Overview

This skill defines a strict OneFlow style workflow optimized for autonomous agents. All work happens on short lived feature branches. Before integration, feature branch commits are squashed into a single logical commit, rebased onto the latest main, tested, then fast forward merged to maintain a strictly linear history.

## When to Use

* Starting work on a new ticket
* Completing work on a ticket
* Syncing a feature branch with main

## Branch Naming

```
feature/<ticket-id>-<short-description>
```

Example: `feature/TKT-123-add-user-api`

## Workflow Commands

### 1. Start Feature Branch

Create a feature branch from the latest main:

```bash
git fetch origin
git checkout main
git pull origin main
git checkout -b feature/<ticket-id>-<short-description>
```

### 2. Commit Changes

Commits may be granular during development. They will be squashed before integration.

```bash
git add <files>
git commit -m "<ticket-id>: <brief description>"
```

Commit message rules:

* Start with ticket ID
* Present tense
* Subject line only, <= 50 chars

Example:
`TKT-123: Add GetUserById to UserRepository`

### 3. Squash Feature Branch

Before rebasing, squash all feature branch commits into a single commit.

```bash
git fetch origin
git rebase -i origin/main
```

In the interactive rebase:

* Keep the first commit as pick
* Mark all subsequent commits as squash
* Edit the final commit message to a single clean ticket level message

Result must be exactly one commit on the feature branch.

### 4. Rebase onto Main

After squashing, rebase the feature branch onto the latest main:

```bash
git rebase origin/main
```

If conflicts occur:

* Resolve conflicts
* `git add <resolved-files>`
* `git rebase --continue`
* If blocked, `git rebase --abort` and stop

### 5. Re-test After Rebase

Tests are mandatory after the squash and rebase.

```bash
dotnet build
dotnet test
```

If tests fail:

* Fix issues
* Amend the single commit
* Re-run tests
* Do not merge until green

### 6. Fast Forward Merge to Main

Merge using fast forward only:

```bash
git checkout main
git pull origin main
git merge --ff-only feature/<ticket-id>-<short-description>
git push origin main
```

If fast forward fails, main moved:

* Checkout feature branch
* Rebase again
* Re-test
* Retry merge

### 7. Cleanup

```bash
git branch -d feature/<ticket-id>-<short-description>
git push origin --delete feature/<ticket-id>-<short-description>
```

## Complete Merge Sequence

```bash
git checkout feature/<ticket-id>-<short-description>

git fetch origin
git rebase -i origin/main
git rebase origin/main

dotnet build
dotnet test

git checkout main
git pull origin main
git merge --ff-only feature/<ticket-id>-<short-description>
git push origin main

git branch -d feature/<ticket-id>-<short-description>
```

## Error Handling

### Rebase Conflicts

```
Conflicting Files:
- <file>

Resolution:
<what changed>

Status: Resolved | Blocked
```

### Merge Not Fast Forward

Main advanced. Repeat squash, rebase, test.

### Push Rejected

```bash
git pull origin main --rebase
git push origin main
```

## Rules

* Exactly one commit per ticket on main
* Squash before rebasing, always
* Never force push to main
* Never merge without rebasing
* Never merge without passing tests
* Feature branches are disposable
* One ticket, one branch
