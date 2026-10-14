---
title: Share Claude Code sessions with VS Code and the Claude CLI
status: in-progress
created: 2026-10-13
summary: Detect a Claude Code conversation open in another Claude process before resuming it and ask the user; catch up turns added outside CodeAlta; list and import sessions started in VS Code or a terminal; open a CodeAlta session in VS Code or the CLI in one click.
---

# Share Claude Code sessions with VS Code and the Claude CLI

## Goal

One Claude Code conversation can be continued from CodeAlta, VS Code or a terminal, without a manual
import/export and without silent copies:

1. Before CodeAlta resumes a CLI conversation, it checks whether another Claude process holds it, and if so
   asks: **Wait until it is free**, **Continue as a copy**, **Open here anyway**.
2. Turns added to the CLI transcript outside CodeAlta are appended to the CodeAlta journal (catch-up), so
   the timeline shows them and the resume counts stay right.
3. A project lists the Claude Code sessions started in VS Code or a terminal; opening one imports it.
4. A Claude Code session of CodeAlta has one-click **Open in VS Code**, **Copy `claude --resume <id>`** and
   **Run in terminal**.

User decisions (2026-10-13): CodeAlta's journal stays the source of truth (the CLI transcript is a mirror,
synced at known moments); CodeAlta sessions are **not** forced into VS Code's session list (no honest way:
see "Facts"); one-click open in VS Code / CLI instead. Desktop only (AGENTS.md: no TUI feature), except
the held-elsewhere question, which the TUI gets too so that this change does not turn its resume into a
failed run. Plan approved 2026-10-14 with the choices below. Implemented on `feat/share-claude-sessions`
from `origin/main` 4a34f104 (which has the permission-mode change #119), in the worktree
`C:\dev2\CodeAlta-share-claude`. `feat/child-permission-on-parent` (in progress elsewhere) also changes
`SessionRuntimeService.cs`, `main.tsx`, `OwnedSessionPanel.tsx`, `localization.ts`, `doc/desktop.md` and
`site/docs/workspace.md`: phases 0 and 3 come first; the branch that lands second is rebased.

## Non-goals

- Listing CodeAlta sessions in VS Code's session list. It needs CodeAlta to claim another entrypoint
  (`CLAUDE_CODE_ENTRYPOINT=cli`) or to rewrite the CLI's transcript: both misreport CodeAlta to Anthropic.
  A feature request to Anthropic (a setting for programmatic sessions) is the user's call, outside this plan.
- Keeping CodeAlta's CLI process alive while a session is open (so VS Code always sees it as held).
- Other providers: only `claude-code` keeps a conversation outside the journal (`doc/providers.md:112`).
- The TUI beyond the held-elsewhere question (it must keep building and passing its tests).

## Facts (research of 2026-10-12/13, CLI and VS Code extension 2.1.296)

**CLI and VS Code behavior**
- `claude -p --resume <id>` on a conversation held by another live CLI neither refuses nor copies: both write
  the same transcript (experiment). Interactive `claude --resume` (by id, by title, the picker, `/resume`)
  does the same (from the code, phase 0): its only check, `$9`, finds a holder only when it is not
  `interactive` (a background session). No CLI path asks "held by another process": only VS Code does.
  So CodeAlta's check is the only protection on its side. (Idle auto-compaction of the CLI refuses with
  `held_elsewhere` when another live process holds the session.)
- Live registry: `~/.claude/sessions/<pid>.json` (root `CLAUDE_CONFIG_DIR ?? ~/.claude`), written also by a
  print-mode stream-json CLI (as CodeAlta runs it). Example fields: `pid`, `sessionId`, `cwd`, `startedAt`
  (ms), `procStart` (Windows: FILETIME of the process start, e.g. `134362904649601721`), `version`,
  `kind` (`interactive` even for print mode), `entrypoint` (`cli`, `claude-vscode`, `sdk-cli`, ...),
  `status` (`busy`, `shell`, `idle`, `waiting`), `kind` (`interactive`, `bg`, `daemon`, `daemon-worker`),
  `pidDomain` (`win32:<hostname lower>`; Linux: machine-id + pid namespace), `messagingSocketPath`, `name`,
  `updatedAt`, optional `procStartFt`. Removed on exit; a killed process leaves its entry behind (seen); the
  CLI deletes stale entries of its own `pidDomain` when it lists sessions. The entry follows a session
  switch: `--fork-session` registers the **new** id from its first write, the old id never appears
  (experiment, phase 0); `--fork-session` skips the holder check; a fork that receives no message writes
  no transcript.
- `procStart` is, per platform: Windows the FILETIME of the process creation (`GetProcessTimes`; .NET
  `Process.StartTime.ToFileTimeUtc()` gives the same value; a fallback writes .NET ticks, hence
  `procStartFt`); Linux field 22 of `/proc/<pid>/stat`; macOS `ps -o lstart=` with `LC_ALL=C TZ=UTC`.
  The CLI's liveness: token = `procStartFt ?? procStart`, live when the pid runs and its start time read
  now equals the token; no token → pid exists. The VS Code extension does the same but, on Windows, reads
  no token (pid exists only, fooled by pid reuse), and counts an unreadable entry whose pid runs and whose
  file is under 30 s old as held.
- Print-mode `--resume` (without `--fork-session`) refuses with `session_held_by_background` only when the
  holder is a background session (`kind` `bg`/daemon); interactive holders (VS Code, terminal) never block
  it. Today CodeAlta would take that refusal for a missing transcript.
- No official API: no `claude sessions` subcommand (only background-session commands), no control request
  to list sessions or read messages, no registry API. The TS SDK's `listSessions` and `getSessionMessages`
  (which resolves the main chain but does not merge lines by `message.id`, and skips what precedes the last
  compaction in large files) run in Node only. Decision: CodeAlta parses the files in .NET, porting the
  SDK's chain rules, rather than shipping a Node helper.
- VS Code (extension) checks that registry before it starts a session, and shows "This session is open in
  another Claude process on this computer ..." with "wait until released" / "open here anyway".
- VS Code's session list drops a transcript when the first `entrypoint` of its head (else the last of its
  tail) is `sdk-cli`/`sdk-ts`/`sdk-py`, or its `sessionKind` is `daemon`/`daemon-worker`
  (`includeProgrammatic:false` in its panels; no setting). Every CodeAlta transcript is `sdk-cli`. A
  session started in VS Code therefore stays listed there after CodeAlta continued it. The list also drops
  a file whose first line is a sidechain, or whose `continued-in` record points to an existing transcript.
- VS Code opens a session from `vscode://anthropic.claude-code/open?session=<uuid>` (`registerUriHandler`,
  path `/open`, params `session`, `prompt`). Whether VS Code must already be open on the folder: **not
  verified**.

**Transcript format** (`~/.claude/projects/<folder>/<id>.jsonl`, ~1,300 local files)
- Only `<uuid>.jsonl` files are sessions (a sync tool left `<uuid>_<HOST>_<date>_Conflict.jsonl` here).
  Folder name: `ClaudeCodeProjectFolders.GetFolderName` (the CLI's rule); folders differ by drive-letter
  case (match case-insensitively on Windows) and may be junctions: de-duplicate by session id, and trust
  the `cwd` of the first node line over the folder name.
- Tree nodes: `user`, `assistant`, `system`, `attachment`, `progress` (`uuid`, `parentUuid`, `isSidechain`,
  `sessionId`, `cwd`, `version`, `entrypoint`, `timestamp`, ...). Everything else is metadata keyed by
  session, appended many times, last one wins: `queue-operation`, `last-prompt` (`leafUuid`, a hint),
  `ai-title`, `custom-title`, `mode`, `cost-state` (`totalCostUSD`), `bridge-session`, `pr-link`,
  `file-history-*`, `agent-name`, `continued-in`, ... Unknown types appear between versions; partial or
  bad lines occur (the last line may be half-written while a session runs).
- Content blocks: user `text`, `image` (base64), `tool_result` (string or array), rare `document`;
  assistant `text`, `thinking`, `tool_use`. `toolUseResult` holds the tool's own structured result. User
  flags: `isMeta` (hidden/injected), `isCompactSummary`, `origin.kind` (e.g. `task-notification`).
- One model message is written as **several** `assistant` lines (one per content block, chained) with the
  same `message.id`: merge by `message.id` in file order. Parallel tool calls give sibling branches (each
  result a child of its `tool_use` line, `sourceToolAssistantUUID`): pair calls and results by id.
- Compaction: `system/compact_boundary` with `parentUuid: null` (a new root), `logicalParentUuid` to the
  history before, `compactMetadata.preservedSegment`/`preservedMessages` that re-parent kept messages,
  then a `user` line with `isCompactSummary`.
- The current leaf (the SDK's rule): among nodes that are not sidechain/`progress`/`fork_briefing`
  attachment and have no kept child, the newest by file position, walked up to its first user or
  assistant node; the conversation is the `parentUuid` chain from it, with the tool-result siblings
  reattached. `--resume-session-at` leaves the old branch in the file; the newest leaf wins.
- Large tool results are replaced by `<persisted-output>... saved to: <path>\<id>\tool-results\x.txt`.
  Subagents are in `<id>/subagents/agent-<agentId>.jsonl` (+ `.meta.json`), linked by
  `toolUseResult.agentId`. Titles: last `customTitle`, `<id>/custom-title.json`, `aiTitle`, `lastPrompt`,
  first prompt.
- `--fork-session` writes a new `<newId>.jsonl` with fresh uuids (from the code and the files).

**CodeAlta today**
- A plain continuation passes only `--resume=<id>` (`ClaudeCodeLauncher.cs:113-135`,
  `ClaudeCodeSession.BindConversation` :276-368): turns added elsewhere are **not** cut off; the model sees
  them, only the journal lacks them. `--resume-session-at`/`--fork-session` are used only while a
  `ProviderBranch` exists (fork/rewind before the CLI's first answer).
- `ClaudeCodeProviderState(SessionId, Users, Assistants, Instructions, Cost, Leaf)`
  (`ClaudeCodeSession.cs:1003-1061`), persisted in `AgentSessionState.ProviderState` by every answer.
  Resume rule: `persisted.Assistants == assistants && persisted.Users <= priorUsers` (:303-328); otherwise a
  **new** CLI conversation with the history as a `<codealta_previous_conversation>` preamble. In a bound
  session, extra user messages in the journal are **re-sent** to the CLI (:349-367).
  `Assistants` counts model messages (tool steps included), `Users` user messages sent.
- `Cost` is the CLI's running `total_cost_usd`: turns made elsewhere are billed to CodeAlta's next turn.
- The transcript's existence is never checked: a start failure or a 60 s `initialize` timeout
  (`ClaudeCodeSession.cs:423-452`) is taken for a missing transcript and silently starts a new conversation.
- CodeAlta's CLI process lives up to 10 min after a turn (`IdleTimeout`), longer with background tasks or
  Remote Control. CodeAlta does not keep its CLI pids (`ClaudeCodeProcessTransport` keeps `Process` private).
- Hook point for the held check: `ClaudeCodeSession.EnsureConnectionAsync`, before the resume branch (:416).
- A run can ask through `AgentProviderRunContext.OnUserInputRequest`, but the desktop only passes it when
  owned user input is enabled (opt-in): a held prompt needs its own request type.
- Journal: an import needs header (`WorkingDirectory == project.ProjectPath`, `ProjectRef`), per turn a
  fresh `runId` with `local.userMessage` + `contentCompleted{User}`, `local.assistantMessage` +
  `contentCompleted`/`activity{ToolCall}`, `local.toolMessage` + `activity{Completed}` + `ToolOutput`,
  `sessionUpdate{Idle}`; then `local.sessionSummary` (else not listed) and `local.sessionState` with the
  claude-code `ProviderState`. Model: `FileSystemAgentSessionStore.CommitForkAsync` (temp file + rename,
  then `UpsertCacheFromFileAsync`, `AgentSessionCatalog.NotifySessionCreatedAsync`).
- Idle mutations follow provider selection (`SessionRuntimeService.SelectOwnedProviderAsync`): inside the
  session actor, refuse while busy, commit, then **retire the attachment** so the next send resumes from the
  journal.
- Desktop: row menus (`main.tsx` ~3419, `explorer/ExplorerSessions.tsx:140-152`, gated like Remote Control
  by `remoteControl.ts:42-44`), palette (`commandRegistry.ts`), RPC pattern `SessionOperationsRpc.cs`
  `setRemoteControl`. Links open only through the host (`DesktopLinks.Open`), http/https only. Built-in
  terminal: `TerminalsRpc` `create` + `input`. The session list has no groups today
  (`explorer/sessionTree.ts`). Strings need the 5 translations of `localization.ts`.

## Design

**Held elsewhere (feature 1)**
- `ClaudeCodeLiveSessions` (provider project) reads `<configRoot>/sessions/<digits>.json` (≤ 256 KB)
  defensively. An entry holds the conversation when its `sessionId` matches, its `pidDomain` is absent or
  ours, its pid runs and is not one of CodeAlta's own CLI processes (instance-owned set kept by
  `ClaudeCodeModelProviderRuntime`, fed by a new `ProcessId` on the transport; no static state), and its
  start time matches `procStartFt ?? procStart`: Windows `Process.StartTime.ToFileTimeUtc()` (a value
  ≥ 6e17 is .NET ticks: compare `StartTime.Ticks`), Linux `/proc/<pid>/stat` field 22, macOS `ps -o lstart=`
  (`LC_ALL=C TZ=UTC`); no token → pid exists. An unreadable entry whose pid runs and whose file is under
  30 s old counts as held. CodeAlta never deletes entries (the CLI's job). It reports where:
  `claude-vscode` → VS Code, `cli` → a terminal, other → "another application" (with `entrypoint`). An
  unreadable folder or unknown shape counts as "unknown": the session is resumed and a warning is logged
  (never a block on a format we do not understand).
- Checked in `EnsureConnectionAsync` before a `--resume`. Not checked for a fork (`--fork-session` writes a
  new conversation) nor in a turn the CLI started by itself.
- A new run-context request `OnConversationHeld(AgentConversationHeldRequest) → choice` (Wait / Copy /
  Anyway / Cancel). The desktop renders it as a card in the timeline notices with three buttons and waits
  like `SessionPermissionService` (pending table + `TaskCompletionSource`); the TUI shows a dialog with the
  same choices. A host that passes no handler (runs with nobody to ask) fails the run with a message
  naming the other process and the three ways out; it never resumes silently.
- **Wait**: the run polls the registry (2 s) under its cancellation token; the card turns into "Waiting for
  VS Code to release the session", with Stop. When free, it continues (catch-up first, see below).
- **Continue as a copy**: the existing fork path (`_branchPending`, `_branchFork`, `_branchSource = id`,
  `_branchAt = null`): the CLI writes a new conversation; the CodeAlta session follows the copy from now on.
- **Open here anyway**: resume; the card said both sides will write the same conversation.

**Catch-up (feature 2)**
- `ClaudeCodeTranscriptReader` (provider project), in .NET (no official API exists): reads line by line,
  tolerates bad and partial lines, ignores unknown types and fields; builds the tree of node entries,
  applies the compaction re-parenting, picks the leaf with the SDK's rule ("Facts") and walks the chain
  (leaf → root, tool-result siblings reattached). Merges assistant lines by `message.id`, pairs tool calls
  and results by id, maps blocks (text, thinking → reasoning, tool_use/tool_result → tool call/result with
  `<persisted-output>` resolved from `<id>/tool-results` when present, image → attachment, unknown block →
  short placeholder). Shows user lines that are not `isMeta`, not `isCompactSummary` and not only tool
  results; skips attachments (except queued commands) and metadata; a compaction becomes one marker and the
  turns before it stay shown; a subagent shows as its Agent tool call and result only.
  It returns the turns after a given entry `uuid` (the recorded `Leaf`), the new leaf, the last
  `cost-state` total, the title, and the entrypoint/`origin` of each turn. It reads incrementally: the
  provider state also records the byte offset of the last full line, the file size and mtime; a shrunk
  file or a changed first line means a full read. If the recorded leaf is not an ancestor of the new leaf
  (rewound or resumed at an earlier point elsewhere), it returns "cannot follow".
- An optional executor interface (like `IAgentProviderInitiatedTurns`), `ReadExternalTurnsAsync(state)`,
  so the orchestration stays provider-neutral.
- `SessionRuntimeService.CatchUpExternalTurnsAsync(sessionId)`, inside the session actor, idle only (same
  guards as provider selection): appends each turn with a fresh `runId` and an Idle record, marked as made
  outside CodeAlta (`origin: claude-code`, `entrypoint`), writes a new `local.sessionState` with
  `Users`/`Assistants` increased by exactly what was written, `Leaf`, and `Cost` set to the transcript's
  total (so the external turns are not billed to the next CodeAlta turn), then retires the attachment.
- Runs (a) before a prompt is admitted on an idle session (send path, before
  `ResolveCoordinatorEntryAsync`), (b) when a session is opened in the desktop, (c) after "Wait" ends.
  Skipped while a run, a queue drain or a `ProviderBranch` exists. "Cannot follow": a timeline notice
  ("This conversation was changed outside CodeAlta in a way it cannot follow; the model has the full
  conversation") and no append.
- Statistics: external turns carry no provider usage events: they are not counted as CodeAlta usage.

**Discovery and import (feature 3)**
- Discovery (`workspace.claudeCodeSessions(projectId)`, lazy): the transcripts of
  `GetProjectFolder(project.ProjectPath)` (case-insensitive on Windows, `<uuid>.jsonl` only, `cwd` of the
  first node line equal to the project path) that VS Code would list itself (the filter of "Facts": not
  `sdk-*`, not a daemon session, not a sidechain, not `continued-in` an existing transcript, has a title
  or a prompt), not
  referenced by any CodeAlta session (index of CLI session ids: `Details.ProviderSessionId` of the catalog's
  claude-code sessions, from the projection cache), not empty. Discovery reads only the first and last 64 KB of a file,
  as the SDK does. Each entry: id, title (the SDK's priority), last change, origin (VS Code / terminal),
  held now or not.
- Import (`workspace.importClaudeCodeSession(projectId, cliSessionId)`): reads the transcript with the
  reader, writes the journal through a new store method modelled on `CommitForkAsync` (header, turns,
  summary, state with `SessionId`, counts of what was written, `Leaf`, `Cost`, `Instructions = null` so the
  first prompt sends the full CodeAlta instructions), notifies the catalog, returns the new session id.
  Importing only reads the transcript; a held session can be imported (its first prompt meets feature 1).
- The instructions update note gets a variant for "you were never given these" (today it says it replaces
  a `# CodeAlta` part the imported conversation never had).
- Desktop: a collapsible "From Claude Code (N)" entry at the end of a project's session list (new entry
  kind in `sessionTree.ts`), shown only when a claude-code provider is configured; a click imports and
  selects the session.

**Open elsewhere (feature 4)**
- Host RPC `sessions.claudeCodeConversation(sessionId)` → `{cliSessionId, resumeCommand, workingDirectory}`
  and `sessions.openInVsCode(sessionId)`: the host builds `vscode://anthropic.claude-code/open?session=<id>`
  itself (no general widening of `DesktopLinks` to other schemes).
- Before opening elsewhere, CodeAlta **releases** the conversation: it closes its idle CLI process for that
  session (not while a run, background tasks or Remote Control keep it; then the action says why).
  Otherwise VS Code would see CodeAlta as the holder for up to 10 minutes.
- Session row menus, palette and session header overflow: **Open in VS Code**, **Copy resume command**
  (`claude --resume <id>`), **Run in terminal** (built-in terminal in the session's working directory +
  `claude --resume <id>`). Only for claude-code sessions that have a CLI conversation.
- Session Info: a "Claude Code conversation" row with the id and Copy.

## Steps

### Phase 0: verify what is not yet known
- [x] Interactive `claude --resume <id>` in a terminal while another CLI holds the session: writes the
      same transcript (from the code). Recorded in "Facts".
- [x] The registry entry of a `--fork-session` process holds the new id from its first write (experiment).
- [x] The projection cache keeps the provider state: `session_projection_cache.provider_session_id` and
      `state_json` (`SessionJournalSqliteCache.cs:767-804`, upsert :1092). Index of known CLI ids =
      `AgentSessionCatalog.ListSessionsAsync` filtered on project + `claude-code`, taking
      `Details.ProviderSessionId`: no journal read, no schema change. Only the last CLI id of a session is
      kept (an earlier id after a broken resume or a provider switch is not): accepted, such a transcript
      is then listed for import.

### Phase 1: provider groundwork (commit)
- [ ] `IClaudeCodeTransport.ProcessId`; instance-owned set of CodeAlta's CLI pids in the runtime.
- [ ] `ClaudeCodeLiveSessions` reader + liveness rule; tests with fixture registry files (live, stale pid,
      reused pid, own pid, other `pidDomain`, ticks instead of FILETIME, unreadable fresh entry, unknown
      shape, unreadable folder).
- [ ] Check that the transcript exists before `--resume` (reuse `ClaudeCodeTranscriptRelocation.FindSources`
      rules); fall back to a new conversation only when it is missing, not on a slow start nor on a
      `session_held_by_background` refusal (that one is "held elsewhere", phase 2). Regression tests first
      (a slow start and that refusal must not drop the conversation).

### Phase 2: held-elsewhere prompt (commit)
- [ ] `AgentConversationHeldRequest` on `AgentProviderRunContext`; hosts without a handler fail the run
      with the explanatory message.
- [ ] `ClaudeCodeSession`: check in `EnsureConnectionAsync`; Wait (poll, cancellable), Copy (fork path),
      Anyway; no check for forks and CLI-started turns. Tests with `ClaudeCodeFakeCli` + fixture registry.
- [ ] Orchestration: pending table + RPC (list/resolve) modelled on `SessionPermissionService`.
- [ ] Desktop card in the timeline notices (3 buttons, then the waiting state with Stop); strings in 5
      languages; frontend test + browser mount test.
- [ ] TUI: a dialog with the same three choices, through its permission-request flow
      (`SessionPermissionRequestCoordinator`); test.

### Phase 3: transcript reader (commit)
- [ ] `ClaudeCodeTranscriptReader` with synthetic fixture transcripts (no real content): repeated
      `message.id`, parallel tool calls (sibling results), a rewind branch (newest leaf wins), tool results
      in `tool-results/`, image, thinking, compaction with a preserved segment, a subagent call, unknown
      entry and block types, a truncated last line, incremental read from an offset, leaf not an ancestor.

### Phase 4: catch-up (commit)
- [ ] Executor interface `ReadExternalTurnsAsync`; `SessionRuntimeService.CatchUpExternalTurnsAsync`
      (actor, idle guards, append, state with counts/leaf/cost, retire attachment).
- [ ] Call it before an idle prompt, on open in the desktop, after Wait.
- [ ] Timeline: mark turns made outside CodeAlta ("in VS Code" / "in a terminal").
- [ ] Tests: counts keep the next turn on `--resume` (no preamble, no re-sent prompts); cost not billed
      twice; "cannot follow" notice; statistics do not count external turns.

### Phase 5: discovery and import (commit)
- [ ] Discovery RPC + index of known CLI ids; import store method (temp file + rename) + RPC.
- [ ] Instructions note variant for an imported conversation.
- [ ] Desktop "From Claude Code (N)" group; import on click and select.
- [ ] Tests: imported session lists in the right project, renders, first prompt resumes with `--resume`
      and sends the full instructions; `sdk-*` and already-known transcripts are not listed.

### Phase 6: open in VS Code / CLI (commit)
- [ ] `vscode://anthropic.claude-code/open?session=<id>` with VS Code closed, open on another folder, open
      on the session's folder (moved from phase 0: it opens VS Code windows on the user's screen, so warn
      first). If the folder must be open, open it first (`vscode://file/<folder>` or `code <folder>`),
      then the session URI; record the rule.
- [ ] Host RPCs; release of the idle CLI process; the VS Code URI built on the host only.
- [ ] Row menus, palette, header overflow, Session Info row; gating to claude-code sessions with a CLI id.
- [ ] Tests: host refuses unknown ids and non-claude sessions; frontend menu and palette tests.

### Phase 7: docs and full verification (commit)
- [ ] `doc/providers.md` (Claude Code section: registry, held prompt, catch-up, import, open elsewhere,
      transcript check), `doc/runtime.md`, `doc/session-info.md`, `doc/desktop.md`.
- [ ] Site: `site/docs/sessions.md`, `workspace.md`, `model-providers.md`, `troubleshooting.md`.
- [ ] `dotnet build`/`dotnet test` (configuration per AGENTS.md), `npm test`, `lunet build`; check in the
      developer instance (`alta --dev`): a session held by a terminal CLI, catch-up after VS Code turns,
      import, Open in VS Code; both themes and a narrow window.

## Risks

- Undocumented formats (registry, transcript, VS Code URI) change between CLI versions: every reader skips
  what it does not understand and degrades to a notice, never to a silent new conversation or a block.
- A wrong `Users`/`Assistants` count drops the CLI conversation (new conversation + preamble) or re-sends
  prompts: the counts are always derived from what was written, and tested.
- Two writers still possible from a terminal `claude --resume` that does not check (phase 0 decides how
  much): catch-up shows what happened afterwards; "cannot follow" covers a rewritten chain.
- Transcripts can be large (tens of MB): catch-up reads from the recorded offset, discovery only the
  first and last 64 KB.
- On Windows, VS Code's own check is pid-only: a reused pid can make VS Code see a stale CodeAlta entry
  as held. Nothing to do on CodeAlta's side; the user picks "open here anyway" in VS Code.
