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
failed run. Plan approved 2026-10-14 with the choices below. Implemented on `feat/share-claude-sessions`,
first in a separate worktree, then (2026-10-14, after phase 2a) rebased onto `origin/main` 81d29945 (with
#120, the sub-session waiting on its parent) and continued in `C:\dev2\CodeAlta`.

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
- [x] `IClaudeCodeTransport.ProcessId`; instance-owned set of CodeAlta's CLI pids in the runtime.
      Done: `ClaudeCodeOwnProcesses` (pid + start read at registration) owned by `ClaudeCodeTurnExecutor`,
      fed by `ClaudeCodeSession` when a connection starts, released when it closes. Opt-in live test
      `Registry_NamesTheProcessCodeAltaStarted` (`CODEALTA_TEST_CLAUDE_CLI=auto`, no model call): the entry of
      the native claude.exe CodeAlta starts has its pid, `procStart` = `StartTime.ToFileTimeUtc()`, and
      `pidDomain` = `win32:` + `Dns.GetHostName()` in lower case. The model probes are not tracked (their
      conversations are random ids nobody resumes).
- [x] `ClaudeCodeLiveSessions` reader + liveness rule; tests with fixture registry files (live, stale pid,
      reused pid, own pid, other `pidDomain`, ticks instead of FILETIME, unreadable fresh entry, unknown
      shape, unreadable folder). Done (`ClaudeCodeLiveSessionsTests`, 30 tests, 39 cases); reachable from the session
      (`ClaudeCodeSession.LiveSessions`) for phase 2. Own domain as the extension builds it: Windows
      `win32:<host>`, Linux `linux:<machine-id>:<readlink /proc/self/ns/pid>`, macOS `darwin` (no host);
      unknown own domain + an entry with a domain → "unknown". macOS token: `Process.StartTime` in UTC formatted
      as C's `%c` (no `ps`): not verified on a Mac. Precedence: a described holder (latest `updatedAt`) >
      unreadable fresh entry > unknown > none.
- [x] Check that the transcript exists before `--resume` (reuse `ClaudeCodeTranscriptRelocation.FindSources`
      rules); fall back to a new conversation only when it is missing, not on a slow start nor on a
      `session_held_by_background` refusal (that one is "held elsewhere", phase 2). Regression tests first
      (a slow start and that refusal must not drop the conversation). Done: `LocateTranscript` (Present /
      Missing / Unknown → tried). Facts found: the CLI (2.1.296) refuses before it reads its input with
      `Error: That session is running in the background (<job>). Run \`claude attach <job>\` ...` on stderr and
      exit 1; it writes a zeroed `error_during_execution` result with `startup_failure_reason:
      "session_held_by_background"` only when `CLAUDE_CODE_STARTUP_FAILURE_RESULTS` is set, which CodeAlta now
      sets. Both are recognized. Both failures fail the run and leave the conversation bound to the recorded
      state (the next turn resumes it). Deviation: a CLI that exits for another reason while the transcript is
      found (e.g. "No conversation found", a profile with `CLAUDE_CODE_PROJECT_DIR_NAME`) still starts a new
      conversation with the preamble, as before.

### Phase 2: held-elsewhere prompt (commit)
- [x] `AgentConversationHeldRequest` on `AgentProviderRunContext`; hosts without a handler fail the run
      with the explanatory message. Done: `CodeAlta.Agent/AgentConversationHeldRequest.cs` (request, holder with
      origin `VsCode`/`Terminal`/`OtherApplication`/`BackgroundSession`/`Unknown`, choices `Wait`/`ContinueAsCopy`/
      `OpenAnyway`/`Cancel`, delegate `AgentConversationHeldHandler`); `OnConversationHeld` on
      `AgentSessionCreateOptions` (session default) and `AgentSendOptions` (per send), passed to the run context.
- [x] `ClaudeCodeSession`: check in `EnsureConnectionAsync`; Wait (poll, cancellable), Copy (fork path),
      Anyway; no check for forks and CLI-started turns. Tests with `ClaudeCodeFakeCli` + fixture registry.
      Done: `ClaudeCodeSession.Held.cs`; `ClaudeCodeHeldConversationTests` (13). Also not checked while the process
      of the session runs. The wait is shown with transient `Info` session updates whose details carry
      `conversationHeld: {state: waiting|released, ...}` (`AgentConversationHeldUpdates`); Stop ends it. Cancel, a
      run with nobody to ask, and a cancelled wait end the run (Cancel as a stopped run: `OperationCanceledException`)
      and rebind the conversation from the recorded state, so the next turn sends the prompt of that run again with
      its own. The background refusal asks Wait/Copy (Wait retries the start every 2 s without asking again); a `bg`/
      daemon holder already in the registry gets the same two choices. Compaction and Remote Control have no run to
      ask: they fail with the message. Deviation: the fake CLI now reads a per-test registry folder
      (`CLAUDE_CONFIG_DIR`), not the real `~/.claude`.
- [x] Orchestration: pending table + RPC (list/resolve) modelled on `SessionPermissionService`. Done:
      `SessionConversationHeldService` (`SessionRuntimeService.ConversationHolds`, on with
      `AskWhenConversationHeld` / `CodeAltaHostOptions.AskWhenConversationHeld`, set by the desktop): its own mailbox,
      handle = runtime + attachment ordinal + session + run + request + fresh attempt; a question ends with its run
      and with its attachment; counted in the waiting sessions of `spaces.activity`. RPC `sessionConversationHeld`
      (`ConversationHeldService`: `list`, `resolve`; typed client `conversationHeld`). Tests:
      `SessionConversationHeldServiceTests` (8), `ConversationHeldRpcTests` (4).
- [x] Desktop card in the timeline notices (3 buttons, then the waiting state with Stop); strings in 5
      languages; frontend test + browser mount test. Done: `frontend/src/conversationHeld/` (reviewer
      `createConversationHeldReviewer`, `ConversationHeldPanel`, css, messages, `conversationHeld.test.ts`,
      `conversationHeld.mount.tsx` + `.browser.test.ts`), rendered by `OwnedSessionPanel` in the timeline notices
      portal; only the offered choices, Cancel quiet and last; an answer is sent once and the list is read again
      whatever the reply (rejected/uncertain show a line, stale epoch asks for a reload); `conversationHeld` in the demo
      API. Deviation: the Info updates of the wait are not in the timeline (it skips Info rows), so the live display
      carries them: `RuntimeDisplaySession.ConversationWait` (set by a `conversationHeld` waiting update, cleared by
      released and by the end of the run) → `SessionDisplayView.conversationWait {originText ≤ 64}`; tests in
      `RuntimeDisplayProjectionTests` and the display wire budget. Checked live in the developer instance (a
      throwaway Claude Code session; holder: a print-mode `claude --resume` with stdin open, shown as "another
      application (sdk-cli)" since print mode ignores `CLAUDE_CODE_ENTRYPOINT`): Wait (the line, then the run went on
      by itself once the holder exited), Open here anyway (resumed while held), Cancel (run ended as stopped), Stop
      on the waiting line, Continue as a copy (new CLI conversation id); both themes and 900×650. Note: after a
      Cancel or a Stop the next turn sends the prompts of the stopped runs again with its own (as designed above).
- [x] TUI: a dialog with the same three choices, through its permission-request flow
      (`SessionPermissionRequestCoordinator`); test. Done: the TUI host turns `AskWhenConversationHeld` on;
      `SessionConversationHeldCoordinator` shows `ConversationHeldDialog` (offered choices as buttons, Escape =
      Cancel) and answers with `ConversationHolds.ResolveAsync`; the dialog closes when the question ends without it.
      The questions reach it through a new `SessionConversationHeldService.Observe(onAsked)` (callback per question
      with a token cancelled when it ends; a failing observer changes nothing): the TUI's permission flow is a
      per-send handler, the held handler is the runtime's. The wait shows in the status line (its Info message), as
      before. Tests: `SessionConversationHeldCoordinatorTests` (4), observer test in
      `SessionConversationHeldServiceTests`; strings in `SR.yml`.

### Phase 3: transcript reader (commit)
- [x] `ClaudeCodeTranscriptReader` with synthetic fixture transcripts (no real content): repeated
      `message.id`, parallel tool calls (sibling results), a rewind branch (newest leaf wins), tool results
      in `tool-results/`, image, thinking, compaction with a preserved segment, a subagent call, unknown
      entry and block types, a truncated last line, incremental read from an offset, leaf not an ancestor.
      Done: `ClaudeCodeTranscriptReader` (`Read`, `ReadAfter`, `ReadHeader`) produces `AgentConversationMessage`s
      directly; 67 tests; opt-in smoke test (`CODEALTA_TEST_CLAUDE_TRANSCRIPTS=1`) over 1,399 local files:
      21,021 turns, 0 failures, 0 differences between incremental and full reads (713 of 1,028 stayed
      incremental). Reviewed independently. Deviations from this plan:
      - **Unwritten parents.** The CLI often names a `parentUuid` it never wrote (146 of 300 recent files,
        mostly after `tool_use` lines in VS Code files): such a line is bridged to the last line of the
        same writer (same side, same `entrypoint`), unless that line already has a child written after it.
        Without it the chain loses the first prompt (4,683 turns found instead of 21,021). Two writers with
        the same entrypoint (two VS Code windows) cannot be told apart.
      - **Incremental position** = offset, length, mtime, first-line hash, leaf, `Tail` (the next prompt
        hangs from the last system/attachment line, not from the leaf), the leaf's `message.id`, and the
        last line of each writer. The read falls back to a full read when it cannot be exact.
      - **Statuses** `Read`, `Unchanged`, `CannotFollow` (recorded leaf not an ancestor), `LeafNotFound`
        (the recorded leaf was never written and nothing names it: the reducer records tool-result uuids
        as the leaf, which the CLI often does not write), `Missing`.
      - **Not user prompts:** `isMeta`, `isCompactSummary`, tool-result-only lines, and turns the CLI
        started (`origin.kind` other than `human`, task notifications: no user message, `TriggerText`).
      - **Commands** map as CodeAlta records them: `/name args` a user message, the CLI's own command output
        an assistant message, `!cmd` and its output one user message.
      - **Counts** of a turn are those of the messages the reader returns; they must never be recomputed
        from the transcript for CodeAlta's own turns (they differ in rare cases: steered messages, the
        empty closing assistant message, an unknown block).
      - **Cost** (`cost-state`) is often absent: catch-up needs a fallback. Images are `Data` parts: saving
        them as attachment files is left to the import/catch-up. Persisted outputs are resolved by file name
        only under `<id>/tool-results/`, up to 4 MB.

### Phase 4: catch-up (commit)
- [x] Executor interface `ReadExternalTurnsAsync`; `SessionRuntimeService.CatchUpExternalTurnsAsync`
      (actor, idle guards, append, state with counts/leaf/cost, retire attachment).
      Done: `CodeAlta.Agent/Runtime/AgentExternalTurns.cs` (`IAgentProviderExternalTurns.ReadExternalTurnsAsync(state,
      workingDirectory)` → `AgentExternalTurns(Status None/Unchanged/Read/CannotFollow/Missing, Turns, ProviderStateAfter(turns,
      users, assistants))`), implemented by `ClaudeCodeTurnExecutor` with `ClaudeCodeExternalTurns.Read` (transcript only where
      `--resume` from the working directory looks: `GetProjectFolder(root, wd)/<id>.jsonl`). Reached by
      `AgentHub.ReadExternalTurnsAsync` on the runtime the registry keeps (nothing started; works for a session that is not
      attached). Journal shapes in `AgentExternalTurnJournal.PrepareAsync` (the private serializers of `AgentSession` made
      `internal`): per turn a run `external:<guid>`, `local.userMessage` + `contentCompleted{User, contentId
      external:<vscode|terminal|other|unknown>:<guid>}`, `local.assistantMessage` + content/`ToolCall Requested`,
      `ToolCall Started`, `local.toolMessage` + `Completed`/`Failed` + `ToolOutput`, `Idle` without usage, then a
      `local.sessionState` (counts so far, leaf = last entry of the turn: a fork/rewind at a turn is exact); a turn the CLI
      started gets a `provider:` prompt from its trigger text; images of prompts are saved with `PromptImageAttachmentStore`
      (as pasted images), those of tool results with `AgentSessionToolImages`; a compaction is a `CompactionCompleted`
      notice ("The conversation was compacted in VS Code."). `SessionRuntimeService.CatchUp.cs`: observe on the actor (guards
      of provider selection + background tasks + held questions; `ProviderBranch` → nothing), read off the actor, commit on
      the actor only if the state and the journal length are unchanged (`stale` otherwise); one catch-up per session at a
      time (a send joins one in progress). Outcomes `appended`/`cannot_follow`/`unchanged`/`none`/`busy`/`stale`.
      Provider state: `ClaudeCodeProviderState` gains `Position` (`transcript` object: offset, length, mtime ticks, first-line
      hash, leaf, tail, leaf message id, last lines) and `Anchor` (last assistant entry). **Counts** = recorded + exactly what
      was written. **Cost** = the last `cost-state` read; absent → the recorded cost is kept (a CLI that resumes counts from
      the last `cost-state` of the transcript, which these turns did not change). Deviations:
      - The interface is reached through the provider runtime of the registry, like `IAgentSessionRelocationProvider`,
        not through a live session: the desktop opens sessions that are not attached.
      - **Position** is recorded only by a catch-up (an answer cannot compute it without reading the file) and then kept by
        every answer (`ClaudeCodeSession` carries it; dropped for a new or forked conversation). A read that found only
        CodeAlta's own lines writes the new position only when the session has no attachment (an attached one would write
        its state over it) and never retires an attachment.
      - **CannotFollow** writes the notice (a `Warning` update) **and** a state whose leaf/position are the transcript's
        current leaf (counts unchanged), and retires the attachment: otherwise the notice would be written at every open,
        and the idle CLI process holds the conversation as it was.
      - **LeafNotFound**: the reader cannot know where an unwritten leaf hung, so the session now records `Anchor` (the last
        assistant entry, which the CLI writes); the turns are read after it and the leading tool results of the anchor's
        calls (which the journal has) are dropped. Without an anchor, or when that read cannot follow either, it is
        `CannotFollow`. (In practice the leaf is a tool result only when a run ended right after one.)
      - Turns of an attached idle session are written and the attachment retired; with background tasks running the
        catch-up is `busy` (retiring would end them).
- [x] Call it before an idle prompt, on open in the desktop, after Wait.
      Done: (a) `SendOwnedBodyAsync` before `ResolveCoordinatorEntryAsync` (`CatchUpBeforeSendAsync`, failures never stop the
      prompt; also the TUI's sends, which share the runtime). (b) RPC `sessions.catchUp({expectedEpoch, sessionId})` →
      `OwnedSessionCommandService.CatchUpExternalTurnsAsync` (busy while a command of the session is in flight), called by
      `OwnedSessionPanel` whenever a session becomes visible; the catalog event bumps the display revision, which refreshes
      the timeline. Unchanged costs a state read and a stat + first-line hash of the transcript. (c) Deviation: the run that
      waited does **not** catch up (its prompt is already in the journal: turns written after it would be out of order, and
      once it answered they could never be added). After the release, `RefuseTurnsMadeWhileWaiting` reads the transcript;
      when the holder added turns the run ends with "Turns were added to the Claude Code conversation <id> in VS Code while
      this run waited for it. CodeAlta adds them to this session before the next message: send the message again." The next
      send catches up (the turns follow the failed prompt) and sends only its own prompt.
- [x] Timeline: mark turns made outside CodeAlta ("in VS Code" / "in a terminal").
      Done: `timeline.ts` reads `external:<where>:` prompt ids (`externalOrigin`, `madeElsewhere`), subtitle "in VS Code" /
      "in a terminal" / "in another application" / "outside CodeAlta" (5 languages); the cannot-follow notice is a warning row.
- [x] Tests: counts keep the next turn on `--resume` (no preamble, no re-sent prompts); cost not billed
      twice; "cannot follow" notice; statistics do not count external turns.
      Done: `ClaudeCodeExternalTurnsTests` (8: end to end with the fake CLI and a written transcript — resumed, only "third"
      sent, cost 0.01 not 0.26, turn rendered as made in VS Code, position kept by the next answer; images/compaction; cost
      kept without `cost-state`; rewound → CannotFollow + state followed and not repeated; CannotFollow journal records;
      unwritten leaf followed from the anchor; Unchanged/Missing/None; state round trip),
      `ClaudeCodeHeldConversationTests.HeldConversation_Wait_EndsTheRun_WhenTheHolderAddedTurnsMeanwhile`,
      `SessionCatchUpTests` (4: appended + attachment retired + next prompt follows; send catches up first; busy while a run;
      cannot follow notice), `SessionFactsReducerTests.TurnMadeOutsideCodeAlta_IsNotCounted`, `timeline.test.ts`.
      Statistics: external runs carry no usage record; the reducer also skips every record of an `external:` run (no run,
      prompt or tool call is counted). A compaction made elsewhere (run-less notice) still counts as a compaction.
- [x] Fixes of the independent review of Phase 4 (2026-10-14), each with its regression test first:
      - CodeAlta's own lines read back as external: after a compaction of CodeAlta, and after a turn stopped or failed once
        its prompt was sent, `ClaudeCodeSession.FollowOwnWrites` reads the transcript (turn gate held, right after the CLI
        wrote) and moves leaf/anchor/position past the leading turns whose entrypoint is `sdk-cli` (or none);
        `IAgentProviderStateUpdates.TakeProviderState` gives that state to `AgentSession`, which records it after a turn
        that threw and after a provider compaction. No general `sdk-cli` net in the catch-up: `claude -p --resume` in a
        terminal writes `sdk-cli` too.
      - Re-reads of attached sessions: `ClaudeCodeTranscriptPositions` (owned by the executor) keeps the latest position
        of each transcript read; reads start from the latest of it and the recorded one, and every answer records it.
      - The summary is appended with the turns (one append), and the attachment is retired in a `finally` once the append
        began; images saved for turns that are not written are deleted. Journal appends are one write
        (`AgentSessionJournalFile.AppendLinesCoreAsync`): a process that ends leaves all the records of an append or none.
      - A send that joins a catch-up that ended `busy`/`stale`/failed runs once more.
      - `busy` also while a send or a compaction holds a work use of the attachment (`TryAcquireWorkUse`) and while Remote
        Control is connecting or connected.
      - `held` (`AgentExternalTurnsStatus.Held`): nothing is read while another live process holds the conversation and is
        not `idle`.
      - The hub asks only providers whose descriptor has `SharesConversations` (Claude Code): no runtime of another
        provider is created on open.

### Phase 5: discovery and import (commit)
- [x] Discovery RPC + index of known CLI ids; import store method (temp file + rename) + RPC.
      Done: provider-neutral `IAgentProviderExternalConversations` (`CodeAlta.Agent/Runtime/AgentExternalConversations.cs`:
      `ListExternalConversationsAsync(workingDirectory, isKnown, limit)` → `AgentExternalConversationList(Conversations, Total)`,
      `ReadExternalConversationAsync(workingDirectory, id)` → `AgentExternalConversationImport(Conversation, Turns)` or null), implemented by
      `ClaudeCodeTurnExecutor` with `ClaudeCodeExternalConversations` (instance-owned header cache keyed by path + length + mtime, max
      4,096 headers; the newest 1,000 `<uuid>.jsonl` of the project folders whose name matches case-insensitively on Windows, de-duplicated
      by id; `IsListedByVsCode` and `cwd` = project path; held = `LiveSessions.FindHolder`). Routed by `AgentHub.ListExternalConversationsAsync`
      / `ReadExternalConversationAsync` (providers with `SharesConversations` only, nothing started; the read also returns the runtime
      descriptor for the protocol family/key of the journal). `SessionRuntimeService.Import.cs`: index of known ids = every catalog session's
      `ProviderSessionId` (any project, archived too; the project's session preferred), import serialized by an instance gate, known → `existing`
      (idempotent), else full read → `AgentExternalTurnJournal.PrepareAsync` (origin marks, images saved beside the new journal) →
      `SessionViewJournalStore.ImportAsync` → `FileSystemAgentSessionStore.CommitImportAsync` (temp file + rename, then cache upsert) →
      `NotifySessionCreatedAsync`. Session: ProjectSession, `ProjectRef`, `WorkingDirectory == project.ProjectPath`, title of the transcript
      (`TitleSource = User`: CodeAlta does not retitle it), the provider's default model/effort/permission mode, `ProviderSessionId` = CLI id,
      claude-code state `SessionId`, counts written, `Leaf`/`Anchor` = leaf, `Position` of the full read, `Cost` = last `cost-state` or null
      (the first CodeAlta turn is then given no cost rather than the whole conversation's), `Instructions = null`, `imported: true`.
      Statistics: every record is under an `external:` run with an Idle without usage (the Phase 4 reducer rule). Desktop RPCs
      `workspace.claudeCodeSessions({expectedHostEpoch, projectId, projectPath})` → `{status, hostEpoch, projectId, projectPath, providerId,
      sessions[{cliSessionId, title, lastChangedAt, origin, held}], total}` (≤ 50) and `workspace.importClaudeCodeSession({expectedHostEpoch,
      projectId, projectPath, cliSessionId})` → `{status ok|existing|not_found|…, sessionId}`, exact catalog project, provider = the project's
      default when it shares conversations, else the first enabled one that does (`no_provider` otherwise); import admitted under the
      workspace session gate (`busy`, `closed`, drained on close, `import_unconfirmed` after admission). Deviation: no per-folder mtime cache
      (a folder's mtime does not change when a transcript grows); the per-file header cache does that job.
- [x] Instructions note variant for an imported conversation.
      Done: `ClaudeCodeProviderState.Imported` (`"imported": true`, written only while `Instructions` is null); the session keeps it
      until its first instructions note, which is `ClaudeCodePrompts.CreateInstructionsForImportedConversation` ("…You were not given the
      instructions of CodeAlta before: they are below…") instead of the "replaces the `# CodeAlta` part" wording.
- [x] Desktop "From Claude Code (N)" group; import on click and select.
      Done: entry kind `claude-code` (`explorer/sessionTree.ts`: `SessionTreeEntry` for what `sessionList` builds, `SessionListEntry` adds the
      group; `withClaudeCodeSessions`, `sessionEntries`), rendered after the disclosure of the selected scope (main.tsx) and of open scopes
      (`ExplorerSessions` `claudeCode` prop) when a `claude-code` provider is configured and the window is owned.
      `claudeCodeImport/ClaudeCodeSessionsGroup.tsx`: decision on N — one listing call when the project's list is shown (N and the first 50
      rows together: the host's header cache makes it cheap), again when the sessions of the window change (count) and when the group is
      opened; hidden while N = 0; rows show title, time, "VS Code"/"terminal"/"another program", "open elsewhere"; a click imports (one at a
      time) and the window refreshes its snapshot and reveals the session (toast when it cannot). Strings in 5 languages
      (`claudeCodeImport/messages.ts`); demo-api stand-ins.
- [x] Tests: imported session lists in the right project, renders, first prompt resumes with `--resume`
      and sends the full instructions; `sdk-*` and already-known transcripts are not listed.
      Done: `ClaudeCodeExternalConversationsTests` (5: filter — sdk-cli, daemon, sidechain, continued-in, known, other cwd, untitled, empty,
      non-uuid names; order, limit, total, re-read on change; held; read → turns + state; imported mark; note wording),
      `ClaudeCodeImportTests` (2: end to end with the host and the fake CLI — listed, imported in the project, turns made in VS Code, no usage,
      state, idempotent, first prompt `--resume` with the imported note and no preamble; non-listable → `not_found`, nothing written),
      `WorkspaceClaudeCodeSessionsTests` (2, desktop RPC validation/busy/closed/unconfirmed),
      `SessionFactsReducerTests.SessionImportedFromClaudeCode_CountsNoRunOfCodeAlta`, frontend `sessionTree.test.ts` (+1),
      `claudeCodeImport/claudeCodeSessions.test.ts` (4), `claudeCodeSessions.browser.test.ts` (mount: hidden until found, N, re-read on open,
      rows, held mark, one import at a time, error, German, both themes and 900×650).

### Phase 6: open in VS Code / CLI (commit)
- [ ] `vscode://anthropic.claude-code/open?session=<id>` with VS Code closed, open on another folder, open
      on the session's folder (moved from phase 0: it opens VS Code windows on the user's screen, so warn
      first). If the folder must be open, open it first (`vscode://file/<folder>` or `code <folder>`),
      then the session URI; record the rule.
      Not done yet (opens windows on the user's screen): the code path is testable through the opener seam of
      `SessionOperationsService` (`SharedConversationSeam.Open`; production: `DesktopLinks.Open`, shell execute).
- [x] Host RPCs; release of the idle CLI process; the VS Code URI built on the host only.
      Done: provider-neutral `IAgentProviderSharedConversation.DescribeSharedConversationAsync(state, workingDirectory)` →
      `AgentSharedConversation(ConversationId, ResumeCommand, WorkingDirectory, EditorUri, Holder)`
      (`CodeAlta.Agent/Runtime/AgentSharedConversation.cs`), implemented by `ClaudeCodeTurnExecutor`: null while a `ProviderBranch`
      exists or when the recorded id is not a GUID; command and URI from `ClaudeCodeConversationLinks` (GUID-checked);
      holder = `LiveSessions.FindHolder`. Routed by `AgentHub.DescribeSharedConversationAsync` (providers with `SharesConversations`
      only). `SessionRuntimeService.SharedConversation.cs`: `DescribeSharedConversationAsync` (`ok`/`unavailable`/`unsupported`/`none`,
      folder = summary `ExecutionDirectory`) and `ReleaseSharedConversationAsync`: on the actor, the catch-up guards with a reason each
      (`running`, `remote_control`, `background_tasks`, `interaction_pending`), then the idle attachment is retired as a provider
      selection retires it (`_forwarding.RetireAsync`, entry removed, transition until it ends): the AgentSession is disposed, which
      closes the CLI process; `released` also when nothing was attached; `release_unconfirmed` when the retirement failed.
      `OwnedSessionCommandService.GetSharedConversationAsync` / `ReleaseSharedConversationAsync` (public, `OwnedSharedConversation*`
      records; release describes first, `busy`/`running`/`interaction_pending` from the command locks). Desktop RPCs on `sessions`:
      `claudeCodeConversation({expectedEpoch, sessionId})` and `releaseClaudeCodeConversation(...)` →
      `{status, epoch, sessionId, cliSessionId, resumeCommand, workingDirectory, held}` (`held` = the `ConversationHeldHolder` of
      phase 2), `openInVsCode(...)` → `{status opened|open_failed|<refusal>, epoch, sessionId}`: describe, release, then open the
      host-built URI; the desktop projects only a GUID id whose URI and command are exactly the Claude Code ones (else
      `unsupported`); the URI never goes to the page (`[JsonIgnore]`). `WorkspaceSession.providerConversation` (optional, true when
      the catalog has a `ProviderSessionId`) gates the actions without listing the id.
      Deviations: (1) a third RPC, `releaseClaudeCodeConversation`, for **Run in terminal** (the terminal is the page's, through the
      terminals RPC). (2) **Fix of phase 1**: every attached session has a provider runtime of its own
      (`AgentHub.CreateStartedProviderRuntimeAsync` → `CreateRuntimeAsync`), while the registry, catch-up, discovery and this
      description read from the registry's runtime: its own-process set never had the attached sessions' CLI pids, so CodeAlta's
      own idle CLI was reported as a holder ("another application (sdk-cli)"). `ClaudeCodeOwnProcesses` now lives on the options
      instance (`ClaudeCodeModelProviderRuntimeOptions.OwnProcesses`, lazily created), shared by every runtime of that provider.
- [x] Row menus, palette, header overflow, Session Info row; gating to claude-code sessions with a CLI id.
      Done: `frontend/src/claudeCodeOpen/` (`claudeCodeOpen.ts`: gating `opensElsewhere(session, providerType)`, the three flows
      with an injectable API, refusal messages; `openElsewhereEntries.ts`; `hostApi.ts`; `ClaudeCodeConversationRow.tsx`;
      `messages.ts`, 5 languages). `SessionAction` + `sessionActionAccess`: `open-vs-code` and `run-in-terminal` with the authority of a
      change, `copy-resume-command` with the session's read authority. **Open in VS Code**, **Run in terminal**, **Copy resume command**
      in both row menus (after Remote Control) and in the search window (Session: `open_in_vscode`, `run_in_terminal`,
      `copy_resume_command`); outcomes as toasts. Run in terminal: release, `terminals.create` with the session (its folder), then
      `claude --resume <id>\r`. Session Info: a "Claude Code conversation" row (id, Copy → "Copied", "Open now in …" when held),
      read once when the window opens. Demo API stand-ins. Deviation: the session header has no overflow menu (its actions are
      icon buttons); none was added.
- [x] Tests: host refuses unknown ids and non-claude sessions; frontend menu and palette tests.
      Done: `ClaudeCodeOpenElsewhereTests` (4: named from the recorded state with own process not a holder, VS Code holder named,
      release closes the idle process and the next prompt resumes; refused while a run works; refused with Remote Control;
      unknown/no conversation/tampered id), `ClaudeCodeTurnExecutorTests.ProcessesOfTheSessions_AreCodeAltasOwn_ForEveryExecutorOfTheSameProvider`,
      `SessionCatchUpTests.SharedConversation_OfAProviderThatSharesNone_IsUnsupported_AndItsProcessIsKept`,
      `SessionClaudeCodeConversationRpcTests` (5: validation/stale epoch, projection + holder, open after release with the host URI,
      nothing opened when refused or forged, release), `DesktopWorkspaceTests.Projection_SaysWhichSessionsHaveAConversationOfTheirProvider_OnlyForThem`;
      frontend `claudeCodeOpen.test.ts` (8), `sessionRowActions.test.ts` (+1), explorer menu browser test (+1),
      `claudeCodeConversationRow.browser.test.ts` (2).

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
