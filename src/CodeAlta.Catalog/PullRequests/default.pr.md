---
name: Default
description: Puts the work of the session on a branch, pushes it and opens a pull request.
---
Create a pull request for the work of this session.

1. Look first: `git status`, the current branch, the default branch of the remote, and what this session changed. If there is nothing to propose, say so and stop.
2. Put the work on a branch of its own. On the default branch, create one named for the work (for example `fix/short-name`). In a worktree or on a feature branch, keep the branch you are on.
3. Commit what belongs to the work, and only that, with the commit conventions of the repository (its `AGENTS.md`, its recent history). Leave unrelated changes uncommitted and say which they are.
4. Push the branch and open the pull request with the tool of the hosting service: `gh pr create`, `glab mr create` or `az repos pr create`. Target the default branch, unless the branch was started from another one.
5. Write it for a reviewer. The title follows the conventions of the repository. The body says what changes and why, how it was verified, and what is left to do. Use the pull request template of the repository when it has one. Link the issue the work answers (`Fixes #123`) when there is one.
6. Answer with the link of the pull request.

Do not merge it and do not force-push. If something stands in the way (no remote, not signed in, a check that fails and that you cannot fix), say what it is and stop.
