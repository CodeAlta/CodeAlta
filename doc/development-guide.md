# Development Guide

Rules for every change. The strict rules come first; each section below adds what you need to apply them in one area. Details, examples and the reasons behind them are in the area docs linked from each section, and the long contracts of specific components are in [development contracts](development-contracts.md): read the section that covers the code you change. Add a rule here only when it must hold across the repository; no plans or one-off design notes.

## Strict rules

1. **Never read session journals in normal operation.** Listing sessions, their relations, state, notes and turn positions read the application database, which every session write keeps in sync. See [Session database](#session-database-sqlite).
2. **No static mutable data.** Use instance-owned state, DI services, immutable or frozen static data. No process-wide lock maps or static caches for runtime or session state.
3. **One writer per session state.** Per-session mailbox actors serialize mutable runtime state and event order; no process-wide run, event-writer or provider lock. No Akka.NET or other actor framework without a recorded decision.
4. **UI thread:** plain `await` in UI flow; no `ConfigureAwait(false)` where the continuation touches bindable/UI/frontend state; marshal back to the dispatcher before updating UI-owned state.
5. **Layers:** Orchestration, Orchestration.Hosting, Plugins and Catalog never reference a frontend or terminal controls; reusable orchestration never lives in a frontend; Desktop never references the TUI; `CodeAlta.Hosting` is composition only.
6. **New features are for CodeAlta Desktop only.** The TUI gets no new screen, dialog, command or setting unless asked, but it must build and its tests pass after a shared-layer change.
7. **Instance state goes through `CatalogOptions.StateRoot`** (`CodeAltaInstanceProfile`), never `GlobalRoot` or a recomputed `~/.alta`; shared files stay under `GlobalRoot`, and a host with a separate state root does not rewrite them at startup.
8. **Tests:** new or changed behavior has tests; a bug fix starts with a failing regression test. Tests bound work by counts, never by clocks, and pass on a loaded machine. All .NET tests, `npm test` and `lunet build` pass before submitting.
9. **No page call to the host is given up on a timer**; a call ends with its answer, its `AbortSignal` or a closed connection.
10. **Persist through the owning store only** (config, journals, drafts, plugin state, UI state). Records that belong together are one journal append.
11. **A tool, command or file location that starts programs or runs code declares it** where the existing ones do, so that a session without `shell_command` cannot run programs.
12. **Several windows:** no "the page"/"the window" singleton in host or page; route per window.
13. **Animations in window regions never move a box** (only opacity, color or paint).
14. **Native engines run in a worker process**, never in the host.
15. **Logs, journals, prompts, outputs, provider payloads and plugin diagnostics are sensitive data**; credentials never go in logs, errors or diagnostics.
16. **Public APIs have XML docs** (with thrown exceptions); validate inputs early with specific exceptions (`ArgumentNullException.ThrowIfNull`); respect nullability; no warning suppressed without a justification comment.
17. **Build and test with the configuration `AGENTS.md` prescribes** (Debug/Release by what runs); one build at a time; never exit the user's CodeAlta.
18. **Docs follow behavior:** `readme.md`, `site/` and `doc/` are updated with the change; internal docs describe the current implementation.

## Session database (SQLite)

The application database is `<state root>/data/alta.sqlite3`. The session projection (`session_projection_cache`) and the other tables of session facts are kept in sync with the sessions, so that normal operation **never reads a session journal, or another client's transcript,** to:

- list sessions or resolve them (`alta session list`, the Explorer, the workspace snapshot, bookmarks, search, sub-session reads);
- find their relations: parent, children, fork origin, project, space;
- know their state: provider, model, effort, agent prompt, title, archived, message count, queued prompts;
- read their notes or turn positions.

Journals stay authoritative. They are read only to:

1. rebuild the database: first run, deleted or damaged tables (rare);
2. show the history of the session the user has open: bounded pages, scrolling back;
3. feed the statistics and search followers, from their saved cursors, new bytes only;
4. build the provider conversation a send needs: none for a CLI that owns its history; from the last compaction for API providers.

Rules:

- A feature that needs a fact about sessions stores it in the database when the session is written (a column of the projection, such as `journal_header_json`, or a table of its own). Never add a per-session journal read to get it.
- Each such read path has a regression test that holds every journal open without sharing and must keep passing: `DesktopSessionHierarchyTests.WorkspaceSnapshot_ReadsTheSessionProjection_NeverAJournal`, `AltaLiveToolTests.SessionList_ReadsTheSessionProjection_NeverAJournal`, `ClaudeCodeExternalConversationsTests.ListAsync_AfterARestart_TakesTheHeadersOfUnchangedTranscriptsFromTheStore_WithoutReadingThem`. Add one for each new path.
- The listing makes one status read per row to check its journal is the one projected; a changed journal is projected again and written back; a row of an older cache gets its header read once. A state appended to a journal (`SessionViewJournalStore.AppendStateAsync`) is written to its row and refreshes the in-memory catalog.
- Importers of other clients keep the headers they read in `IAgentExternalConversationHeaderStore` (`external_conversation_header`) under their own provider name, and read again only files whose length, write time or format version changed.
- `IAgentSessionStore`/`IAgentSessionCatalog` own sessions; listing, metadata, history and deletion are provider independent. Listing is streaming only (`IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync`); materialize at the edge.
- Missing or damaged projection tables rebuild from journals; a write that fails on a lock surfaces its error, never a slow-scan fallback.
- The database **file is not a cache**: plugins keep data there that cannot be rebuilt; nothing deletes it.
- Everything goes through `ApplicationDatabase`: reads on their own connections, writes through its queue, one owner name and version per set of tables. No second connection from feature code; every table name starts with its owner's prefix (`IPluginDatabase.TablePrefix` for a plugin).
- A query that runs after each write (search index sync and counts) reads pages in proportion to what changed, never a table, and has a test that counts the pages it reads (`SQLITE_DBSTATUS_CACHE_MISS`, e.g. `TheChangesOfOneChunk_AreReadFromAFewPages_NotFromEveryVectorOfTheSet`).
- Saved Statistics are the one durable store outside it: `StatisticsArchiveStore` owns `<StateRoot>/saved_statistics_after_deleted_sessions/archives.sqlite3`; it never restores an older backup or recreates itself empty, and startup imports a generation once. See [Statistics](statistics.md#durable-saved-statistics).
- Code that reads journals over time (statistics) lists them with `ISessionJournalCatalog`, opens them with full sharing and no session lock, and reads with `JournalScanner` from an offset to the last complete line, never loading a journal or a whole line; facts are additive (`FactBatch`). See [statistics facts](statistics-facts.md).

Coverage to keep: `SessionJournalSqliteCacheTests`, `AgentSessionCatalogTests`, `ApplicationDatabaseTests`, `PluginDatabaseTests`.

### Session-database rules being added

Rules for each new session fact kept in the database (queued prompts, relations, notes, turn positions...) go here, one bullet each, with the test that locks the journals.

- **Relations come from the session rows.** Parent, children, descendants, the sessions of a project, space or worktree (delete, archive or move of a tree, the Explorer, `alta session` commands, retention) are read from `session_projection_cache` through `FileSystemAgentSessionStore.ListSessionsAsync`/`GetSessionAsync` or the runtime's catalog over them; never by enumerating or reading journals. Destructive operations still delete the files they found. Test: `WorkspaceSessionChildrenTests.DeletingATreeOrAProjectAfterARestart_FindsTheSessionsInTheSessionRows_NeverInAJournal`.
- **A session's state comes from its row.** Model, effort, provider, modes, tier, archive, queue and lineage are read from `local_state_json` of `session_projection_cache` (`SessionViewJournalStore.ReadLatestStateAsync`), never from the journal. A writer appends the journal first, then writes the row in the same call with the stamp the journal has just after the record; a row that did not match the journal before keeps its stamp and no state, and is projected again. No in-memory cache of a session's state beside the row. Tests: `SessionStateRowTests`, `SessionRuntimeArchiveTests` (fresh runtime after a restart).

## Threading and async

- `ConfigureAwait(false)` is for background/infrastructure code (libraries, SDKs, transport, persistence, I/O, workers, startup code that marshals back). Keep the boundary from UI flow narrow and explicit; no workaround abstractions for wrong `ConfigureAwait` use.
- Desktop RPC handlers (`NeoRpcDispatchMode.Background`) run on the thread pool concurrently: shared service state is locked or thread safe, the window/view is touched only through the dispatcher, and one file has one writer at a time (`config.toml` through `CodeAltaConfigStore`). Page calls that set host-kept state go through `createOrderedCalls`; a call that must follow others waits for them.
- The window's thread carries every page call: no handler dispatched there does file I/O or waits.
- Watcher notifications (file editor) are attachment scoped: post to the captured app, ignore while unmounted, reconcile on attach, invalidate on detach. Watcher threads never mutate bindable state.
- Async completions, provider callbacks and plugin results become runtime commands/events before they touch mailbox-owned state. Event streams are bounded or have a documented overflow policy.
- A provider callback posts its event to the session mailbox inline (`OwnedProviderEventForwarding.Forward`); never move that post to the thread pool. `OrchestrationMailboxActor` admits commands first come, first served; do not replace it with a semaphore.
- Per-delta work never grows with the message, the journal or the number of sessions (`StreamedAssistantText`, `PluginBase.ObservesContentDeltas`, `AppendLinesTrackedAsync`). Measure with `src/CodeAlta.Benchmarks` ([runtime](runtime.md), "Measuring the runtime").
- Public APIs expose ids, request DTOs, snapshots, handles and events, never actor references or mailbox channels.

## Architecture boundaries

- `CodeAlta.Tui` owns terminal controls, view models, projections, dialogs and action adapters; `CodeAlta` owns the native desktop lifecycle and RPC/React presentation. Lower layers reference neither.
- `CodeAlta.Hosting` is composition only: provider registration/defaults, bounded inspection and the configured auth orchestrations (Copilot, xAI, Codex). Concrete auth/protocol code stays in provider packages, persistence in Catalog, presentation in the frontends. No new application owner. Details: [contracts › Hosting](development-contracts.md#hosting-and-provider-authentication).
- Plugins: orchestration hooks are headless; plugin prompt/notification contracts are minimal data, never terminal-UI APIs; plugin-derived session events are transient projections, never transcript events; plugin commands go through the shared command registry with no plugin-specific frontend wiring; plugin startup passes the single-start gate and retention admission before publishing services or its database.
- A plugin's provider type goes through `PluginModelProviderContribution`, never a Hosting/Catalog reference to the plugin. Providers that run their own agent build on `CodeAlta.Agent.Harness`; do not copy it.
- Keep the `alta` tool name, product identity, neutral `CodeAlta.*` namespaces and shared `.alta` paths stable across frontend renames.
- Prefer named ports, request/response DTOs, immutable snapshots and event streams over callback aggregates.
- Naming: `ModelProvider` for selectable runtimes (never new `Backend` names; removed `AgentBackendId`, `BackendId`, `IAgentBackend`, `AgentBackendFactory` stay removed); `Session`/`SessionView` for conversations (`Thread` only for real threads or legacy readers); no `LocalRuntime`/`LocalAgent*`/broad `CodeAlta*` prefixes in `CodeAlta.Agent`.
- Compatibility covers user data only: old journals and view state load with tolerant reads of legacy fields; `config.toml`/`[providers]` semantics stay stable unless a migrator is added.
- A state the agent must learn during a session reaches it as an `AgentInputItem.HostContext` note of the next prompt, never through the system prompt (which stays byte-stable for the prompt cache). A command refused in every permission mode is an `AgentCommandGuard`.
- Reviews live in `ReviewService` (state folder, never the checkout); the host anchors threads; comment layers go through `frontend/src/review/` ([review](review.md)).

## Runtime, sessions and providers

- `AgentHub` owns active sessions, per-session runs, abort/steer/compact and provider resolution, not discovery or probing. `SessionRuntimeService` maps UI/live-tool actions to runtime requests, queues, skills, history and `SessionRuntimeEvent` projections.
- Model providers own provider identity, protocol adaptation, readiness, capabilities and model catalogs only. Their initialization starts eagerly, concurrently with listing and restore; a failing provider is a provider state, never a startup failure. `IModelProviderRegistry`/`IModelProviderInitializationService` never gate listing, history or import.
- File-change snapshots and diffs are bounded in the shared agent runtime before serialization, with explicit omission evidence; these limits never change file mutation or permissions.
- The statistics have one engine (`StatisticsEngine`, Desktop only): agent events only enqueue a session id (at most 64 outstanding), numbers always come from the journal, reading runs below normal priority and commits per session or chunk, and pages and `alta statistics` read through `StatisticsQueries` only ([contracts › Statistics](development-contracts.md#statistics)).
- Parent/child sessions share lineage only: no shared run gate, cancellation, provider state or journal writer.
- A session's space is fixed at creation and passed explicitly (`spaceId` of the create methods: the window's space, the calling session's for agents, else the project's only space or Default); copies keep it. Read it with `SpaceCatalog.ResolveSessionSpace`, check resources with `SpaceScope.IsAvailable` ([catalog](catalog-and-config.md)).
- Shell turned off: new `alta` commands, plugin tools, provider tools or files that start programs declare it in `BuiltInAltaCommandContributor` (`ReviewsCommands`, `ShellTurnedOffAsync`, `ResolveSourceCommandRefusalAsync`), `PluginToolActivationPolicy.StartsPrograms`, `AgentWriteGuard` or the Claude Code restricted launch; session-creating commands keep `RefuseWiderToolsAsync`/`RefuseSettingsChangeAsync` ("Tools turned off" in [runtime](runtime.md)).
- Tools made available mid-turn (an activated MCP server) register through `AgentToolInvocation.RunTools` and are offered on the next model request; never end the turn to get them.
- Running tool output is transient (`RuntimeToolOutputProjection`, `toolCalls.observe`/`toolCalls.read`); a new tool view belongs in `ToolCallDialog`/`ToolCallViews`.
- Titles carry their source (`AgentSessionSummary.TitleSource`): `user` titles are never replaced; only the live `AgentSession` writes automatic titles (`UpdateSummaryAsync`); listings show the saved title; `<session_title>` tags are stripped where text leaves the session ([new-session admission](new-session-provider.md)).
- Fork and rewind are store journal operations committed by one rename (`CommitForkAsync`/`CommitRewindAsync`), at turn starts named by run id. A fork is `ForkedFrom`, never a child; it sends byte-identical history (cache key `AgentTurnRequest.PromptCacheKey`, no "forked from" message). Rewind needs an idle session and rewrites the header (`rewind_count`). See "Fork and rewind" in [runtime](runtime.md).
- CLI agents that run their own tools (`claude-code`) are turn executors: no own `IAgentSession`, journal or events, no second execution of their tools, no credential handling, tolerant parsing of CLI output; qualify with `ClaudeCodeFakeCli` ([providers](providers.md)).
- Conversations shared with other programs (`SharesConversations`): the CodeAlta journal stays the source of truth; external turns are added on the session actor under `external:` runs; other programs' files are read only and never rewritten; a conversation held by another live process is never resumed without asking.
- Execution options are assembled by `SessionExecutionPolicy` from explicit captured descriptors, never from selected tabs; trusted backend inputs are not renderer grants.
- Owned session commands (send, queue, abort, steer, compact, permissions, asks, reminders, notes) bind to exact epoch/session/attachment/run identities, never retarget or replay, and join original work before releasing it. Details: [contracts › Owned session commands](development-contracts.md#owned-session-commands).

## Host and frontend lifetime

- Disposal and creation rollback use one instance-owned `Lazy<Task>` per owner, attempt every stage after a failure, rethrow a lone exception through EDI and aggregate several in order without flattening; no retries, timeouts, scheduling or generic lifetime framework.
- Join the original task before releasing what it uses; cancellation, a timeout or a lost reply is not completion.
- Lifetime tests use only the static seams with inert tasks, retained and joined with bounded observations; no concrete owners, timers, sleeps or profile shortcuts.
- Startup scratchpad cleanup, retention admission, model preparation pins and storage scans follow [contracts › Host lifetime](development-contracts.md#host-lifetime) and [retention runtime](session-retention-runtime.md).
- Desktop startup failures keep full exception chains in log and stderr and drain owned diagnostics before returning.

## Desktop frontend

Sources live in `src/CodeAlta/frontend/src`, one folder per feature (folder map and rules: "Frontend sources" in [desktop](desktop.md#frontend-sources)).

- New feature, new folder; move files with `git mv`. Tests sit beside their module. Logic that needs no DOM is in plain tested functions. Feature stylesheets are imported from `main.tsx`.
- Host calls: no timers that give up a call; a refused `too_many_requests` call is resent; pollers never overlap (`createCoalescedRead`); hidden panes stop polling (`PaneObservingContext`); shared reads go through `HostReadsContext`. A failed call never holds the window; only the host's own answer latches a capability; a reload-only state shows **Reload**.
- Renders: keystrokes, tokens and timers never render `App`; components follow one fact of a store; context values and memoized props keep identity; no layout reads in effects that run every render; regions never move a box (`regionChurn.browser.test.ts`).
- A UI performance change is measured in the developer instance before and after (CPU of `alta`, `regionChurnScript`); a fix without a bad "before" is not a fix.
- Several windows go through `DesktopWindows` and `DesktopPageWatchers<T>.Publish(value, label)`; main-window-only work checks `windowsHub.secondary`; methods needing the caller's window take `NeoRpcContext`; tabs are named by `tabRefs`.
- Drags are pointer events (WebView2 on Windows never delivers HTML5 drops between page elements); handles prevent the pointer-press default (`startSplitterDrag`); test drags with trusted input.
- Markdown and foreign HTML are security boundaries: Markdown goes only through the instance-owned `markdownBoundary` (nothing unsanitized added after sanitization); HTML that runs scripts (MCP Apps) runs only on the `mcpapp` scheme in a sandboxed frame, never on `app://codealta` ([mcp](mcp.md), "MCP Apps").
- Charts use the components of `frontend/src/charts/` (ECharts): no other chart library ([desktop › Charts](desktop.md#charts)).
- Component contracts (composer, timeline, splitter, session tabs, Session info, disclosures, copy feedback): [desktop › Component contracts](desktop.md#component-contracts).
- Startup layout: apply tab-strip geometry when the strip mounts; never suppress `ResizeObserver` errors (`startupLayout.browser.test.ts`).
- `Cmd+W` on macOS goes through the page's tab-close path; only `Cmd+Q` exits.

## Plugins in the page

- Lent libraries (`react`, `react-dom`, Blueprint, `flexlayout-react`, `lucide-react`, `codealta`) are the application's own instances, one entry each in `src/lent/`. Adding one changes the import-map hash in `neoastra.json` (the failing test prints it); `lentVersions` and the version table of [plugins](plugins.md) follow `package.json`; `codealta` stays stable.
- Application modules (built-in plugin UI from the app's build) are listed in `src/lent/appModules.ts` and accepted only for `builtin:` plugins. Plugin modules are served by `DesktopPluginModules`: only published files of web types.
- Details of both: [plugins › Lent libraries](plugins.md#lent-libraries).

### Plugin RPC carrier

`alta.rpc` of a canvas has its own run-time NeoAstra host per instance, carried by the single `canvases.watch` channel: the page has a **budget of 35 channels** (`RpcChannelBudgetTests`); add none for calls. Handlers run on the thread pool. Details: [plugins › The carrier of `alta.rpc`](plugins.md#the-carrier-of-altarpc).

## Catalog, configuration and stores

- Files read while another store, instance or process writes them are read through `SharedTextFile` (`ReadWrite | Delete` sharing), never `File.ReadAllText`, and replaced with `StagedFileReplacement`, never `File.Move` over them. Test such races on Windows.
- Journal appends are one write call (`AgentSessionJournalFile.AppendLinesCoreAsync`).
- Session notes are journal authoritative, written through `RuntimeAltaNotesService`/`SessionRuntimeService` and validated strictly before an append; reading them follows [Session database](#session-database-sqlite).
- Config recovery, prompts and skills go through their Catalog stores (`ConfigRecoveryService`, `PromptResourceStore`, `SkillManagementService`) with the shared `TextFileCodec`; revisions come from the loaded snapshot; failures never report success. Bounded readers (skills, ignores, directory completion) are opt-in prerequisites, not inventory or authority. Details: [contracts › Catalog stores](development-contracts.md#catalog-stores-and-bounded-readers), [catalog and config](catalog-and-config.md).
- Startup scratchpad cleanup is host-owned (`ScratchpadStartupCleanup`) and joined before services start; scratchpad paths use `SystemPromptBuildRequest.InstanceStateRoot`.

## Native engines and worker processes

- The engine runs in a worker (`alta --speech-worker`); only the worker names the wrapper types (`SherpaSpeechEngine` behind `ISpeechEngine`).
- Native libraries are not packaged: they download on first use into `~/.alta/cache/runtime/...` (`ExcludeAssets="native"`), pinned by hash, through `PinnedDownloader`; workers use `WorkerFrames`/`WorkerProcess`. Never copy them.
- Wrapper and native versions move together (`SpeechModelCatalog.EngineVersion`, a test checks); a managed binding is never newer than its native library (`EmbeddingTests.TheManagedBinding_IsNotNewerThanTheNativeRuntime`) and loads it by full path.
- Background workers run below normal priority, serve the user first, pause during dictation and exit when idle.
- Version bumps and the full rules: [contracts › Native engines](development-contracts.md#native-engines-and-worker-processes); the speech worker: "Speech to prompt" in [desktop](desktop.md).

## Providers

- Request defaults and compatibility profiles live in `ProviderDefaults/provider_defaults.toml` unless they need code. No script-like transforms in config; complex rewriting stays in code behind named flags.
- `config.toml` stays backward compatible (`extra_body`, `profile`, `compaction`, `model_overrides` win over defaults). Provider forms edit stored overrides, keeping omitted settings null.
- A provider failure is diagnosable from the session: put provider details on the exception with `AgentErrorData` keys; never read errors through an SDK model that drops fields; check `LogManager.IsInitialized` before logging on failure paths. Retries and fallbacks are durable journal records, bounded and redacted.
- ChatGPT subscription auth uses the public dynamic-client flow, validates ID tokens, never uses email/`sub` as workspace, serializes refreshes, fetches signing keys before consuming a rotating refresh token. Codex fast routing is opt-in. Details: [providers](providers.md).
- The pinned Claude Code CLI (`ClaudeCodePinnedCli`) is bumped on its own commit as a release step ([providers](providers.md)). ACP types are regenerated on their own commit ([ACP](acp.md)).

## MCP

- CodeAlta writes MCP servers only to `~/.alta/mcp.json` and `<project>/.alta/mcp.json` (root `mcpServers`, preserving existing flavors). Other tools' files are read only (`McpConfigSnapshot.ExternalSources`).
- Overlay: first definition wins; project before user; CodeAlta's file before others; the rest are shadowed.
- TOML `[plugins.mcp]` is policy only; connection fields stay in JSON. Effective enablement needs global `plugins.mcp.enabled` and no server-local `false`.
- Discovery and tool enumeration are non-interactive; OAuth only through explicit actions, tokens in CodeAlta state. Do not document unimplemented MCP features as shipped. See [MCP](mcp.md).
- Reusable MCP config, policy, runtime and management code stays in the MCP plugin/runtime; TUI dialogs and status rendering stay in the frontend.

## Tests

### Tests that hold on a loaded machine

- Bound work by a count, never a clock; start concurrent sides on a signal; spinning threads get `LongRunning`.
- Start listening before causing the event (enumerate a watch on the calling thread, not `Task.Run`).
- Deadlines tell a hang from a slow machine (`TestDeadlines.Hang`); tens of seconds when a process or host starts.
- Order async paths only as the product does.
- Reproduce load failures by pinning processors or delaying a step, not by loading the machine. A failure that a larger minimum thread pool (`DOTNET_ThreadPool_ForceMinWorkerThreads`) removes was thread starvation, not a hang.
- On Windows, filters can block renames: replace files with `StagedFileReplacement`; temp folders wait for release (`TestTempDirectory`).
- A test that fails only while `dotnet test` and `npm test` run together is rerun alone before it is called a regression.

### Browser and frontend tests

- Output is failure-focused (`scripts/failures-reporter.mjs`; `npm run test:verbose` for every name); at most two test files at once; Vite builds log warnings only.
- Fixtures use `browserTarget.ts` (system Edge or `CODEALTA_TEST_BROWSER`, which fails if missing; guest flags; `--accept-lang=en-US`) and `waitForBrowserPort` (five-second deadline, piped stderr, no blind relaunch or skip). End browsers with `endBrowser`, never a one-system command.
- Assert what the product promises, not one font's margins; use the platform's modifier; name the system where a gesture differs.
- Press through `pressElement`; never sleep before a click.
- Component fixtures render under StrictMode.

### Other

- SQLite lock tests hold a real SQLite writer transaction (a `FileShare.None` stream does not lock on Unix) and keep their assertions on every platform.
- The GitHub .NET summary omits passed/skipped details through the final `--logger` argument in `src/dotnet-releaser.toml`; a `.runsettings` logger is replaced by dotnet-releaser.
- Desktop windows are driven through the `codealta-dev` MCP server per `AGENTS.md`; screenshots for the site are 2400×1500 (1600×1000 CSS at 150%); destructive checks use synthetic roots.

## TUI shell shape

The TUI is organized around state, commands, events and projections (diagram: [contracts › TUI shell shape](development-contracts.md#tui-shell-shape)):

- `CodeAltaApp` is the composition root and compatibility facade, not a callback target: new behavior goes into named command services, coordinators, events, projection controllers or adapters. `ShellFrontendHost` is only the run/tick/dispose wrapper.
- Views get view models and narrow interfaces, not callback lists. State changes publish a typed event or return a typed result.
- Commands are single immutable `ShellCommand` objects added through `BuiltinShellCommands.Enumerate()`, activated without arguments; no slash-tail parsers or command-id switches. `ShellCommandViewFactory` projects them everywhere.
- Logical tabs are inspectable through one shell tab state model.

## Documentation

- Keep `AGENTS.md` aligned with this guide, and `doc/readme.md` as the navigation entry.
- Move completed plans and superseded drafts out of tracked docs.
- Shipped `site/docs` pages are listed in their `menu.yml` and use only markup `ShippedDocumentationMarkup` reads (`ShippedUserGuideTests`).
- The homepage works without JavaScript and in small-screen, reduced-motion and print layouts; check it with `lunet build` in a browser.
- Prompt images: validation and persistence stay in shared orchestration; the renderer supplies encoded copies, never file authority ([prompt images](prompt-images.md)).
