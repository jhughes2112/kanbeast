---
name: Finish Branch
description: This is how we finish branches. Rebasing squashed branches as fast-forward merges only. Linear history. No PRs or reviews.
dependencies: git
---

## Agent Skill: Linear Integration (OneFlow)

### Phase 1: Preparation & Local Sync

Before integration, you must ensure the local feature branch is clean and the environment is synchronized.

1. **Checkout Feature Branch:** `git checkout feature/<ticket-id>`
2. **Initial Squash:** Use `git reset --soft origin/main && git commit -m "TKT-123: Description"` to collapse all work into a single commit.
3. **Push Prep:** `git push origin feature/<ticket-id> --force-with-lease` (to ensure the remote reflects the squashed state).

### Phase 2: The Sync & Rebase Loop

4. **Sync Main:**
* `git checkout main`
* `git pull origin main`

5. **Rebase Feature:**
* `git checkout feature/<ticket-id>`
* `git rebase main`

6. **Conflict Handling:**
* **If Conflicts Exist:** You must attempt resolution, understand what the changes are intending to do, resolving conflicts such that your changes and their changes both still function properly. Run build steps to be sure.
* **If Unresolvable:** `git rebase --abort`, push the current clean (but unmerged) state, and **Abort Skill**.
* **If Resolved:** `git add .` and `git rebase --continue`.

7. **Post-Rebase Validation:**
* Run `dotnet build` and `dotnet test`.
* **If Tests Fail:** Fix, `git add .`, `git commit --amend --no-edit`, and **Restart Phase 2** (to ensure `main` hasn't moved again).

### Phase 3: Fast-Forward Integration

Once tests pass on a rebased branch, the agent performs the final merge.

8. **Final Push of Feature:** `git push origin feature/<ticket-id> --force-with-lease`
9. **The FF-Merge:**
* `git checkout main`
* `git merge --ff-only feature/<ticket-id>`

10. **Push Main:** * `git push origin main`
* **If Rejected (Main moved again):** **Restart Phase 2**.

### Restrictions
* No using stashes, cherry picking, or merge commits.  They ruin the repository history and lead to ruin in inept hands.

### Implementation Note

To make this truly "OneFlow," ensure Git configuration has `pull.rebase true` to avoid accidental merge commits during the `git pull` phase.
