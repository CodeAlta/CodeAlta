# SQLite-backed session listing cache

- Status: Implemented
- Plan file: `.alta/plans/2026-06-18-session-sqlite-cache.md`
- Created: 2026-06-18
- Task: Add a fast local SQLite cache for recoverable session listing while keeping JSONL session journals as the durable source of truth.
- Git: `.alta/plans/` is not ignored; commit this plan with the related implementation work.

## Objective
- Make startup/sidebar session listing fast when `~/.alta/sessions` contains many large journals by loading a SQLite session index first.
- Keep `~/.alta/sessions/**/*.jsonl` as the source of truth for history and recoverable session state; the SQLite DB is an implementation cache only.
- Keep behavior invisible to UI/users except for faster loading and a startup error when the cache DB is locked.
- Maintain the cache on CodeAlta create/update/delete paths, prune DB rows whose journal file has been deleted, and rebuild/reconcile from journals when the DB is missing or recoverably corrupt.
- Non-goals: move session history out of JSONL, add user-facing cache controls, change provider/session ownership, or introduce a broad storage abstraction beyond the small local-cache DB foundation needed here.

## Context and evidence
- `FileSystemAgentSessionStore.ListSessionProjectionsAsync` scans `*.jsonl` files and projects each file for metadata (`src/CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs:162`), using head/tail probes (`ProjectSessionMetadataFileUncachedAsync`, same file around `:527`). This is the primary slow startup path.
- `AgentSessionCatalog.LoadSnapshotAsync` materializes the whole store listing before callers receive sessions (`src/CodeAlta.Agent/AgentSessionCatalog.cs:108`), so a cache must make the store listing itself fast.
- `SessionRuntimeService.ListRecoverableSessionsAsync` maps `AgentSessionMetadata` to `SessionViewDescriptor` and then reads per-session CodeAlta local state from the journal (`src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs:184`, `:218`). The cache must cover both agent metadata and session-view/local-state overlay to avoid tail-reading every journal.
- `SessionViewJournalStore.AppendStateAsync` writes `codealta.sessionState` snapshots (`src/CodeAlta.Catalog/SessionViewJournalStore.cs:86`); `FileSystemAgentSessionStore.UpsertSessionAsync`/`UpsertStateAsync` write `local.sessionSummary` and `local.sessionState` (`src/CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs:63`, `:281`). These are the right write-through hooks.
- Session create/resume goes through `SessionRuntimeService.EnsureCoordinatorSessionCoreAsync`, which calls `UpsertSessionMetadataAsync`, `UpdateSessionLocalStateAsync`, then notifies the catalog (`src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs:689-706`).
- Session delete goes through `SessionRuntimeService.DeleteSessionAsync` and `AgentSessionCatalog.DeleteSessionAsync` (`src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs:299`, `src/CodeAlta.Agent/AgentSessionCatalog.cs:76`).
- Startup session loading currently imports known projects from `IAgentSessionCatalog`, loads projects, then progressively applies recoverable sessions (`src/CodeAlta/App/CodeAltaShellController.cs:482`; `src/CodeAlta/App/SessionLoadCoordinator.cs:26`). Current catch blocks log session-load errors (`CodeAltaShellController.cs:493`) and `KnownProjectImporter` swallows catalog import failures (`src/CodeAlta/App/KnownProjectImporter.cs:42`), so locked-cache exceptions need explicit rethrow/surfacing.
- `CatalogOptions.CacheRoot` already identifies `~/.alta/cache` for machine-local caches, while `SessionsRoot` identifies `~/.alta/sessions` (`src/CodeAlta.Catalog/CatalogOptions.cs:45`, `:50`).
- Package versions are centrally managed in `src/Directory.Packages.props`; `Microsoft.Data.Sqlite` is not currently referenced.
- Existing docs describe `~/.alta/cache`, session journals, and session listing ownership in `doc/catalog-and-config.md`, `doc/runtime.md`, and `doc/development-guide.md`.

## Assumptions and open decisions
- Assumption: store the shared local cache DB at `~/.alta/cache/cache.sqlite3` with namespaced tables, not inside `~/.alta/sessions`, because the DB may later host other runtime caches.
- Assumption: a healthy-cache startup may show DB rows immediately after checking that each row's journal path still exists; external journal additions/changes are reconciled asynchronously right after the initial sidebar projection. Missing/corrupt DB rebuilds still require a one-time journal scan.
- Assumption: `SQLITE_BUSY`/`SQLITE_LOCKED` during startup, migration, hot load, or rebuild is a hard startup error and must not fall back to slow JSONL scanning or delete the DB.
- Assumption: non-locked corrupt/unreadable cache DBs can be moved aside or deleted and rebuilt because the JSONL journals are authoritative.
- Resolved decision: use `~/.alta/cache/cache.sqlite3` for the shared local cache DB filename.
- Resolved decision: keep the planned two-phase behavior: fast initial DB load, then asynchronous reconciliation for external journal additions/changes.

## Design notes
- Add a small local-cache layer shared by the session journal components:
  - Define minimal cache contracts/DTOs in `CodeAlta.Agent` so `FileSystemAgentSessionStore` can be cache-aware without depending on SQLite.
  - Implement the SQLite-backed cache in `CodeAlta.Catalog`, where `SessionViewJournalStore` can update both agent-session projection data and `SessionViewLocalState` overlay data and can pass the cache facet into each created `FileSystemAgentSessionStore`.
  - Add `Microsoft.Data.Sqlite` to `CodeAlta.Catalog` rather than making every `CodeAlta.Agent` consumer depend directly on SQLite. If the contract proves too awkward, use the smaller fallback of putting the implementation in `CodeAlta.Agent` and exposing typed update hooks to `SessionViewJournalStore`, but prefer the no-SQLite-in-Agent split first.
- Use one general-purpose DB file with a small metadata/migration table or `PRAGMA user_version`, and a sessions table namespaced for this feature. Do not assume this DB only contains session tables; drop/recreate only session tables for session-schema incompatibility, and recreate the whole DB only when SQLite reports the file itself as corrupt/not-a-database.
- Recommended `sessions` table columns:
  - Identity/source: `session_id` primary key with case-insensitive collation, `journal_path`, `journal_last_write_utc_ticks`, `journal_length`, `cache_updated_utc_ticks`, `projection_version`.
  - Agent metadata: `created_at_utc_ticks`, `updated_at_utc_ticks`, `protocol_family`, `provider_id`, `provider_key`, `working_directory`, `title`, `summary`, `model_id`, `reasoning_effort`, `agent_prompt_id`, `parent_session_id`, `created_by_session_id`, `created_by_run_id`, `provider_session_id`.
  - Session-view overlay: `kind`, `project_ref` when known, `local_provider_key`, `local_model_id`, `local_reasoning_effort`, `local_agent_prompt_id`, `archived`, `message_count`, `local_parent_session_id`, `created_by_json`.
  - Indexes: `updated_at_utc_ticks DESC`, `working_directory`, `project_ref`, and `journal_path` unique.
- Add `AgentReasoningEffort? ReasoningEffort` to `AgentSessionMetadata` and propagate it from `AgentSessionSummary` so recovered sessions can display/resume the last reasoning effort without requiring journal-tail local-state reads.
- Fast load path:
  - Open/migrate DB asynchronously.
  - Query cached session rows ordered by last active/update time.
  - For each row, check `File.Exists(journal_path)`; delete stale rows and skip display when missing.
  - Convert rows directly to `AgentSessionMetadata` and then `SessionViewDescriptor`, applying cached local-state overlay without reading JSONL.
- Reconciliation path:
  - If DB is missing/empty after schema creation, synchronously rebuild from all journals using existing projection code; this is the unavoidable first-run/deleted-cache slow path.
  - For a healthy DB, start an async reconciliation pass after initial sidebar projection: enumerate `~/.alta/sessions/**/*.jsonl`, compare path + stamp against cache rows, parse/upsert only new or changed files, and prune rows not found on disk.
  - When reconciliation changes visible data, invalidate `AgentSessionCatalog` and reapply a coalesced session snapshot so externally added/changed journals appear without restarting.
- Write-through consistency:
  - On `FileSystemAgentSessionStore.UpsertSessionAsync`, append the summary to JSONL first, then upsert the DB row from the resulting projection/stamp.
  - On `FileSystemAgentSessionStore.UpsertStateAsync`, append state first, then update provider-session/runtime columns.
  - On `SessionViewJournalStore.AppendStateAsync` and `EnsureHeaderAsync`, update local-state/header columns after the JSONL write succeeds.
  - On `FileSystemAgentSessionStore.DeleteSessionAsync`, remove the DB row after file deletion; if the file is already gone, still remove any DB row for that session id.
  - On cache write failure that is not locked, attempt one DB recreate/rebuild or table repair and retry the row update; on locked failure, surface an error rather than silently losing synchronization.
- Async/concurrency:
  - Use per-instance `SemaphoreSlim`/single-writer gates for DB schema/rebuild/write operations; do not add static mutable state or process-wide lock maps.
  - Use short-lived SQLite connections per operation or an explicitly owned connection factory; use async ADO methods with cancellation where available and `ConfigureAwait(false)` off the UI thread.
  - Keep journal writes authoritative: DB updates happen after successful file writes, never before, and failed DB updates must not corrupt or roll back journal files.
- Startup/error behavior:
  - Introduce a specific cache exception for locked DBs (or a helper that classifies `SqliteException` error codes `SQLITE_BUSY`/`SQLITE_LOCKED`).
  - Update `KnownProjectImporter` and `CodeAltaShellController.LoadStartupSessionsAsync` to rethrow locked-cache exceptions so CLI startup fails visibly instead of logging and continuing.
  - Non-locked cache corruption should be logged with a concise message, the DB recreated, and loading continued from journals.
- UI/performance follow-through:
  - Avoid one UI invocation per cached session during startup; update `SessionLoadCoordinator` to apply cached/reconciled snapshots in batches or one final snapshot while preserving progressive behavior for slow first rebuilds.
  - Keep provider initialization independent; session cache load must not instantiate or probe providers.

## Risks and challenges
- SQLite native dependency/AOT behavior may affect packaging; verify Release build and publish-relevant warnings after adding `Microsoft.Data.Sqlite`.
- The cache contract split between `CodeAlta.Agent` and `CodeAlta.Catalog` may need careful DTO design to avoid circular references while still storing `SessionViewLocalState` overlay data.
- If the hot path only checks file existence, externally modified journals may briefly display stale metadata until reconciliation completes; this is intentional for speed but should be documented in code comments/tests.
- Rebuilding a missing/corrupt DB still performs the old expensive scan; this is acceptable as a one-time recovery path but should report status/logging clearly.
- Windows file locking and SQLite `-wal`/`-shm` sidecar files need careful corrupt-recreate tests so locked DBs are not accidentally deleted.
- Existing `AgentSessionCatalog` snapshot caching can hide background reconciliation results unless implementation explicitly invalidates and reapplies the catalog.
- `SessionLoadCoordinator` can become the next startup bottleneck if it keeps applying every cached session individually.

## Implementation checklist
- [x] Add `Microsoft.Data.Sqlite` to `src/Directory.Packages.props` and reference it from the project that owns the SQLite implementation, preferably `src/CodeAlta.Catalog/CodeAlta.Catalog.csproj`.
- [x] Add a cache DB path to `CatalogOptions` or a small resolver, targeting `Path.Combine(CacheRoot, "cache.sqlite3")` with XML docs.
- [x] Add cache exception/classification helpers for corrupt vs locked SQLite failures, including `SQLITE_BUSY` and `SQLITE_LOCKED` handling.
- [x] Add a small cache contract/DTO layer in `CodeAlta.Agent.Runtime` for session projection cache operations used by `FileSystemAgentSessionStore` without referencing SQLite.
- [x] Implement `SessionJournalSqliteCache` (or equivalent) in `CodeAlta.Catalog` with schema creation/migration, session-row mapping, stale-row deletion, corrupt DB recreate, and locked DB surfacing.
- [x] Update `SessionViewJournalStore` to own/share the cache instance, pass the agent cache facet to `CreateSessionStore()`, and update local-state/header cache columns from `EnsureHeaderAsync` and `AppendStateAsync` after successful journal writes.
- [x] Update `FileSystemAgentSessionStore` to use cached rows for `ListSessionsAsync`, `GetSessionAsync`, and metadata-only reads when cache is healthy, while preserving the existing file-projection path for cache misses/rebuilds/no-cache tests.
- [x] Update `FileSystemAgentSessionStore` write/delete paths to write through to the cache after successful JSONL mutations and to remove cache rows for deleted/missing sessions.
- [x] Add or expose a cache reconciliation operation that enumerates session journals, reprojects new/changed files by comparing file stamps, prunes missing rows, and returns whether visible metadata changed.
- [x] Add `ReasoningEffort` to `AgentSessionMetadata`, update JSON/source-generation as needed, and propagate model/reasoning from `AgentSessionSummary` through `FileSystemAgentSessionStore.ToMetadata` and `SessionRuntimeService.TryCreateRecoverableSession`.
- [x] Update `SessionRuntimeService.ListRecoverableSessionsAsync` to apply cached local-state overlay without per-session JSONL tail reads on the hot path, falling back to `ReadLatestStateAsync` for cache misses/rebuild compatibility.
- [x] Wire startup to load from the cache first, apply the initial sidebar snapshot, then run reconciliation asynchronously and reapply/invalidate when it changes sessions or known projects.
- [x] Update `KnownProjectImporter` and `CodeAltaShellController.LoadStartupSessionsAsync` so locked-cache exceptions escape startup instead of being swallowed by broad logging catches.
- [x] Update `SessionLoadCoordinator` to batch/coalesce UI catalog snapshots during fast cached loads and reconciliation while preserving cancellation and existing progressive behavior for slow rebuilds.
- [x] Update create/resume/delete flows in `SessionRuntimeService`, `AgentSessionCatalog`, and controller/deleter paths as needed so CodeAlta-created sessions are present in both JSONL and DB, and CodeAlta-deleted sessions are absent from both.
- [x] Add focused logging/status messages for cache rebuild/recreate/reconcile failures without exposing prompts, journal contents, or credentials.
- [x] Update docs in `doc/catalog-and-config.md`, `doc/runtime.md`, and `doc/development-guide.md`; update `readme.md`/site docs only if the user-visible `~/.alta` layout summary needs the new cache DB mentioned.

## Verification checklist
- [x] Add unit tests for cache schema creation and hot listing from an existing DB without parsing full journal contents.
- [x] Add tests for missing DB rebuild from existing JSONL journals and for corrupt/non-database DB recreate from journals.
- [x] Add tests for locked DB behavior that assert startup/listing surfaces an error and does not fall back to file scanning or delete the DB.
- [x] Add tests for stale DB row pruning when the associated journal file has been deleted externally.
- [x] Add tests for external journal addition/change reconciliation using file stamps, including catalog invalidation/reapplied sidebar projection behavior.
- [x] Add tests for create/resume/update/delete write-through: summary, provider session id, model, reasoning, agent prompt, title/summary, archived state, message count, parent/created-by lineage, and local-state overrides.
- [x] Add regression tests that corrupt JSONL journals are still skipped/tolerated as today while the DB remains usable.
- [x] Add tests for `KnownProjectImporter`/startup locked-cache exception propagation through broad catch blocks.
- [x] Add tests or assertions that provider initialization is not required for cached/recovered session listing.
- [x] Run `cd src; dotnet build -c Release`.
- [x] Run `cd src; dotnet test -c Release`.
- [x] If readme/site docs change, run `cd site; lunet build`; otherwise note why the Lunet build was not required.
- [x] Self-review the diff for focused scope, no static mutable cache state, async/cancellation correctness, no prompt/credential data in logs, and no unrelated formatting churn.

## Handoff notes
- Preserve current untracked local files `.alta/config.toml` and `.alta/mcp.json`; they pre-existed planning and are user-owned.
- Prefer the cache-contract-in-Agent / SQLite-implementation-in-Catalog split first; fall back to a direct Agent implementation only if the contract introduces more complexity than it removes.
- Keep JSONL as the authoritative recovery path and ensure every optimization has a no-cache/rebuild fallback except for explicitly locked DB errors.
- The first implementation milestone should make listing fast from a healthy DB and safe under missing/corrupt/locked DB conditions before optimizing reconciliation batching further.

## Execution notes
- Implemented in CodeAlta.Agent/CodeAlta.Catalog/CodeAlta.Orchestration/App layers with SQLite DB at `~/.alta/cache/cache.sqlite3` and JSONL journals remaining authoritative.
- Verified with `dotnet build -c Release`, `dotnet test -c Release`, and `lunet build` on 2026-06-18.
