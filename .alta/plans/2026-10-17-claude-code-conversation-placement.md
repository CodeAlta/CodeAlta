---
title: Place every Claude Code conversation in one project, find it from anywhere, drag it to another
status: in-progress
created: 2026-10-17
summary: Replace the per-folder listing of the Claude Code conversations started in VS Code or a terminal with one index of the whole profile. Each conversation is listed under one project at most, chosen from the folder it started in and its git repository. The others go in a "no project" group at the end of the sidebar. Ctrl+P and a new sidebar filter find any of them. Dragging a row onto a project imports it there. The preview of a conversation with no project asks where its first message continues it.
---

# Place every Claude Code conversation in one project, find it from anywhere, drag it to another

## Goal

The **From Claude Code (N)** group of a project lists the conversations started in VS Code or a terminal in the
project folder (branch `feat/share-claude-sessions`, plan `2026-10-13-share-claude-code-sessions`). It reads one
folder of `~/.claude/projects` and keeps the conversations whose first `cwd` is the project folder. So:

- a conversation started in a subfolder of a project (`src`) is listed nowhere;
- a conversation that moved into a Claude Code worktree (EnterWorktree) is listed nowhere (see "Facts");
- a conversation of another checkout of the repository (`C:\dev2\stride-gal`, a CodeAlta worktree opened in VS Code)
  is listed only if that checkout is a project;
- a conversation of a removed worktree, of the home folder or of a repository not in CodeAlta can't be found at all.

What we want:

1. **Each conversation is listed under one project at most, never twice.** The rules below choose that project.
2. **Every conversation can be found.** The ones no rule places are in a **From Claude Code, no project** group at
   the end of the sidebar. `Ctrl+P` and a new filter at the top of the sidebar find any of them by title.
3. **Search and placement stay separate.** Search finds and shows. The sidebar is where a conversation is placed and
   reparented: drag its row onto a project to import it there.
4. **Typing never imports into a place you couldn't see first.** The preview banner names the project the first
   message continues the conversation in, with a picker.

The PR is a draft: what this replaces in the work already done on the branch is simplified, not kept beside it.

## Non-goals

- Searching the content of conversations or sessions. The filter and `Ctrl+P` match titles. Content can come later
  in the same places.
- The TUI. Nothing of this is added to it (AGENTS.md); it keeps building and its tests keep passing.
- Moving or copying transcripts. Neither an import nor a reparenting touches the files of `~/.claude`.
- Placing a conversation in a chat. Chats can't be moved to a project afterwards (`site/docs/sessions.md`).

## Facts (checked on 2026-10-16/17, Claude Code 2.1.296)

- **Where the CLI files a transcript.** In the folder of the working directory it starts in
  (`ClaudeCodeProjectFolders.GetFolderName`). A `cd` during the session doesn't move the file, and later records
  carry the new `cwd`. This was seen in real transcripts, for example `C--Users-xen/01d10db1…` going to a temp
  scratchpad, then to `Documents\English deck`.
- **`claude -w <name>`** creates `<repo>\.claude\worktrees\<name>` (a git worktree, branch `worktree-<name>`,
  locked) and starts there. The transcript is in that worktree's folder, with the worktree as its only `cwd`.
- **EnterWorktree during a session** moves the whole transcript to the worktree's folder. Nothing is left in the main
  folder, and its first `cwd` is still the main checkout. Today it is listed by no project, and adding the worktree
  as a project wouldn't list it either, because its first `cwd` is the main checkout.
- **Resume finds a single copy anywhere.** The CLI looks in the folder of its working directory, then accepts one
  copy in another folder (`ClaudeCodeTranscriptRelocation` remarks). An imported session can therefore work in a
  folder other than the one holding the file, with no move.
- **The size of a profile.** This profile has 28 folders and 482 transcripts. Headers are already cached per file
  while its length and last write time don't change.
- **Repositories.** A git worktree's `.git` is a file `gitdir: <common>/.git/worktrees/<name>`. The main checkout's
  `.git` is a folder. Both name the common directory without running git. A removed checkout has no `.git` anymore.
- **This machine's setup.** `stride`, `stride2`, `stride3` and `stride4` are projects and git worktrees of
  `C:\dev\stride`, which has about 45 checkouts. Their Claude folders are junctions to one folder
  (`c--dev-stride`), and `~/.claude` is a symlink.
- **Drag and drop today.** Session rows can be dragged onto a project (Move to project). The **From Claude Code** rows
  and the `Ctrl+P` results (`search/GlobalSearch.tsx`) can't be dragged.
- **The desktop is the only frontend that lists external conversations** (`WorkspaceClaudeCodeSessionsRpc`,
  `WorkspaceClaudeCodePreviewRpc`, `WorkspaceCreateSessionRpc`).

## Design

### Placement

The placement uses the **starting folder** (first `cwd`) of a conversation, and the git checkout that contains it:
walk up from the folder to the first `.git`. A conversation a CodeAlta session continues is hidden everywhere, as
today.

| # | Starting folder | Listed under | Row tag |
| --- | --- | --- | --- |
| 1 | inside one or more project folders of the same repository, or of no repository: the root, a subfolder, `.claude\worktrees\x` | the deepest of those projects | the subfolder or worktree, relative to the project |
| 2 | in another checkout of a project's repository, also a subfolder of it | the project whose sessions use that checkout (`WorktreePath`), else the project at the repository's main checkout, else the first project of the repository by path | the checkout's folder name |
| 3 | anything else | **no project** | the starting folder |

- **Rule 1 stays in the repository.** When `C:\dev2` is a project and the conversation started in
  `C:\dev2\stride-gal\sources`, `C:\dev2` is used only if no project of the stride repository exists. A project
  folder that contains the starting folder but sits outside its checkout is the last choice of rule 1.
- **A checkout that is itself a project** falls under rule 1 (stride2 → stride2).
- **Rule 3 covers:** removed worktrees, the home folder, WSL paths, repositories not in CodeAlta. With junctions,
  nothing is listed twice.

### Where an import works

- **A checkout of the project's repository:** the session works in the checkout where the conversation ended (its
  last `cwd`, walked up to its checkout), if that checkout still exists. It records it as its `WorktreePath`, like a
  CodeAlta worktree session.
- **Otherwise:** the session works in the project folder.
- **The transcript is never moved,** since resume finds a single copy anywhere.

### Sidebar

- **Per-project groups.** **From Claude Code (N)** at the end of a project lists what the rules place there, as
  today, with a row tag when the rule gives one.
- **The no-project group.** **From Claude Code, no project (N)**, after the projects. It is hidden when empty and
  closed at first (the window remembers), with the same paging as the project groups.
- **Dragging.** Every Claude Code row can be dragged onto a project row: the conversation is imported into that
  project, and the new session is selected. This is how a conversation is reparented. Dropping it on the project it
  is listed under imports it there.
- **The filter box.** It sits at the top of the sidebar and narrows the tree in place by title: sessions, chats,
  every project's Claude Code rows and the no-project group. Groups with a match open while it is used, projects
  without a match are hidden, and `Esc` clears it. The paged groups match on every conversation they hold, not only
  the rows loaded.

### Search (`Ctrl+P`)

- **Its sessions also include Claude Code conversations** not yet in CodeAlta, from every folder. Each has the Claude
  Code logo and "From Claude Code · `<starting folder>` · `<project>` or not in a project". The space filter applies:
  the conversations of the space's projects, plus those of no project.
- **`Enter` opens the preview.** A **Show in sidebar** action opens the row's group, loads its pages up to the row,
  selects it and scrolls to it, ready to drag.
- **Search never chooses a project.**

### Preview and menus

- **A conversation listed under a project.** The banner says "Your first message continues it in `<project>`", with
  **Change** to pick another project.
- **A conversation with no project.** The banner says "Claude Code conversation started in `<folder>`, not in a
  project. Your first message continues it in: [project ▾]". The project of the tab you came from is preselected.
  With no project at all, sending is disabled and the banner says to add a project.
- **Sending** imports the conversation into the project shown, then continues it there. A prompt not sent comes back,
  as today.
- **The row menu and the banner** have **Continue in CodeAlta** (the project listed) and **Continue in project…**
  (the picker). That is the keyboard way to do what dragging does.

### Simplifications of the branch

- **One index for the whole profile** (`ClaudeCodeExternalConversations`). It scans every folder of
  `~/.claude/projects` with the header cache, de-duplicates by session id (the newest file wins, which also folds
  junction folders), and places each conversation. `FindTranscripts(workingDirectory)`, the folder-name match and
  `IsImportable`'s same-folder check go away.
- **Describe, Read and Preview take a conversation id,** not a working directory. They find the file in the index.
  The RPC requests lose `ProjectPath`; the import request takes the target project.
- **The listing takes a placement key** (a project id, or "no project") and returns that group's page. The project
  catalog and the checkouts the sessions use are given by orchestration (`SessionRuntimeService.Import`), not looked
  up by the provider.

## Steps

### Phase 1: the index and the placement (commit, with the orchestration part of phase 2)
- [x] Checkouts from `.git` files and folders: reused `GitWorktreeService.FindCheckoutRoot`/`FindCommonDirectory`
      of the catalog instead of a new provider class (no git process). The placement lives in orchestration
      (`ExternalConversationPlacement`), neutral of the provider, and keeps what it found per listing.
- [x] Index of the profile: every project folder, header cache (8,192 entries, 5,000 transcripts scanned), newest file
      per session id, first and last `cwd` (`ClaudeCodeTranscriptHeader.LastCwd`); Git Bash `/c/...` folders read as
      `C:\...`. Tests: the EnterWorktree layout, the same id in two folders, read/preview/describe by id.
- [x] Placement: rules 1–3 against the projects (archived ones skipped) and the worktrees their sessions use. Tests
      for every row, nested projects, a parent-folder project, a checkout that is a project, a used checkout, a
      removed Claude worktree, folders of another system.
- [x] `List`/`Describe`/`Read`/`Preview` take an id; holders are looked up for the page only
      (`FindExternalConversationHoldersAsync`); the agent interface and its tests updated.

### Phase 2: orchestration and RPC (commit)
- [x] `SessionRuntimeService.Import`: projects and checkouts from the catalog; list by project or `null` (no
      project); import into a given project, in the worktree the conversation ended in (`KeptWorktree`) or in the
      project folder; describe runs the terminal there. Integration test `Listing_GivesEachConversationToOneProject…`.
- [x] Desktop RPCs: list, describe, open in VS Code and preview take a project or none (both `ProjectId` and
      `ProjectPath` null); rows carry `Tag` and `StartDirectory`; import keeps its target project (any project).
      Test: the no-project scope in `WorkspaceClaudeCodeSessionsTests`. The search RPC moves to phase 5.

### Phase 3: sidebar (commit, with phases 2 and 4)
- [x] The no-project group at the end of the project list (an item of `#project-list`, so it scrolls with the
      projects), hidden when empty, closed at first, remembered (`unplacedClaudeCodeKey`), paged.
- [x] Row tags (subfolder, worktree, checkout, starting folder), the start folder in the row tooltip.
- [x] Drag a Claude Code row onto a project → import there and open the session (`ClaudeCodeConversationDrag` in
      `sessionDrag.ts`; any non-archived project, also the one that lists it).
- [x] **Continue in project…** in the row menu, with `MoveSessionDialog` in `continuing` mode (every project).
      **Continue in CodeAlta** is not offered for a conversation of no project; Run in terminal is disabled there.
- [x] Frontend unit and browser tests; checked in the developer instance on this profile (stride 82, no project 51,
      import by the dialog and by a drag onto stride4).

### Phase 4: preview banner (commit, with phase 3)
- [x] The banner names the project the first message continues it in, with a picker (one control instead of a
      "Change" link): the one chosen, else the one that lists it, else the project the window shows
      (`previewTarget`). Without a project, Continue and Send are disabled and the banner says to add one.
- [x] Tests (banner states, picker, no target).

### Phase 5: search (commit)
- [x] `Ctrl+P`: Claude Code conversations among the sessions, once something is typed (title, project, folder),
      of the space's projects or of none (`claudeCodeConversations` RPC, `ListAllExternalConversationsAsync`, at most
      2,000). Choosing one opens its preview and shows its row: the project and its group open, and the group reads
      on until the row is listed, then scrolls to it (one action instead of a separate "Show in sidebar").
- [x] Sidebar filter box (above the chats): narrows the sessions, the chats, the projects and the Claude Code rows
      to those whose title has every word (`sidebarFilterView`, `filterSessionRows`). Scopes with a match open while
      it is used, without changing the tree; every match is listed (no counts, sub-agents found under their
      session); the Claude Code groups show their matches from the same list the search reads; projects without a
      match are hidden; Escape or the clear button empties it; "Nothing matches." when nothing does. Checked live in
      both themes and a 900×650 window.

### Phase 6: docs and verification
- [x] `site/docs/workspace.md` (the sidebar, the search, the section of the conversations: placement table, the
      no-project group, dragging, Continue in project…, the preview's project, the worktree of an import),
      `site/docs/model-providers.md`, and the internal `doc/providers.md`, `doc/runtime.md`, `doc/desktop.md` (the
      group, the search, the filter of the sidebar). No rule of the development guide changed.
- [x] `dotnet test -c Release` (CodeAlta runs from the installed tool): failures only in tests with open tasks
      (symlink privilege, the shell tool test). `npm test`: failures only in the motion browser tests of the open
      reduced-motion task; `projectFocus.browser.test.ts` updated, since the Explorer now has its filter. `lunet build`
      passes.
- [x] Checked in the developer instance on this profile: stride 82 (was 68), no project 51; import by Continue in
      project… and by a drag onto stride4; the preview's picker; `Ctrl+P` opening a conversation two months old and
      showing its row; the filter; both themes and a 900×650 window. This profile has no conversation started in a
      sibling checkout (`stride-gal`…): that rule is covered by `ExternalConversationPlacementTests` and
      `ClaudeCodeImportTests` only.

## Risks

- **A scan of the whole profile on every listing.** The header cache keeps it to file metadata after the first read.
  Measure on this profile, and bound it with `MaxTranscriptsScanned` across the profile if needed.
- **The placement depends on every project.** Adding or removing a project moves conversations between groups. The
  groups read again when the catalog changes.
- **Checkouts resolved from `.git` files** miss a repository moved since. That conversation falls to rule 3, where
  search and dragging still reach it.
