# Statistics

The Statistics plugin (`src/CodeAlta.Plugin.Statistics`) turns session journals into numbers: how much was spent, in
time, tokens, cost and tool calls, by provider, model, project, space and session, over any period. The timeline
cards of a turn work in both applications; the store, the history and the canvas run in CodeAlta Desktop only.

| Piece | Where | Role |
| --- | --- | --- |
| `ISessionJournalCatalog` | `CodeAlta.Agent` (`FileSystemSessionJournalCatalog`) | Lists every journal of the session store and opens one for reading from an offset |
| `JournalScanner` | `Journal/` | Streams a journal from an offset to its last complete line, parsing only what statistics need |
| `SessionFactsReducer`, `FactBatch` | `Facts/` | Turns records into additive facts, with a small state between calls |
| `SessionCatchUp` | `Facts/` | Scanner + reducer from a cursor to a new cursor: the one operation of the history and of the flow |
| `StatisticsStore` | `Store/` | The plugin's tables in the application database: facts, roll-ups, runs, sessions, cursors |
| `StatisticsEngine` (`IStatisticsService`) | `History/` | The history job, the flow, the status and controls |
| `StatisticsQueries` | `Query/` | Every question, in plain C#, independent of any transport |
| `alta statistics` | `StatisticsCommands.cs` | The same questions as JSONL commands |
| Canvas | `Canvas/`, `src/CodeAlta/frontend/src/statistics/` | The calls of the tab and the React canvas |
| `StatisticsArchiveStore` | `CodeAlta.Catalog` | Durable saved statistics of deleted sessions |

## Facts: the model

A fact is an addition to a measure, keyed by **session, quarter hour (UTC) and the key of its family** (activity,
usage, cost, tools, content, details, histograms, extremes, scripts), plus one row per run, per run of a script and per
session. Everything follows from three choices:

- **Facts are additive.** Every measure is a sum or a count; only largest values (fullest context, longest call,
  extremes) merge with `MAX`. Merging is addition, so reading a session in two halves gives exactly the facts of
  reading it at once. Averages and rates are never stored: they are computed from sums when asked. Costs are kept in
  millionths of their unit, so every stored number is an integer and every sum is exact. Units never mix: dollars and
  AI credits are separate.
- **Quarter hours in UTC.** Every time-zone offset in use is a whole number of quarter hours, so a local day is an
  exact sum of quarter hours in every zone and across daylight-saving changes. Roll-ups by local day, month and year
  are a cache, written in the same transaction as the facts, rebuilt from the facts alone when the time zone or the
  roll-up version changes. A journey costs a rebuild, never a reread.
- **Names, never content.** The store keeps counts, the names of providers, models, tools, skills and MCP servers,
  the program of a shell command, the first two words of an `alta` command, file extensions, a cleaned session
  title, and a hash of the folder of a session. No prompt, answer, tool argument, result or path is copied. A name taken out of a command is kept only when
  it is written as a name, because the rest of a command may hold a secret (`PGPASSWORD=… psql` counts `psql`; a quoted
  value, a token-like word or an ambiguous shell construct counts nothing). The same holds for scripts: the name of a
  library script, the id of a step and the name of an error (`TypeError`) are kept only as identifiers, never a message.

Definitions the reducer guarantees:

- **A run** goes from the first record that carries its id, in file order, to its `Idle` or `error`. Its active time
  is accounted record by record and cut at quarter hours; a gap of more than seven days between two records is not
  time of the run (a damaged line or a wrong clock). A run with no end when the next starts was interrupted. A session
  quiet for an hour with an open run is settled as interrupted; a later record of that run opens it again with negative
  additions, since the engine cannot tell a dead session from one waiting for the user.
- **Corrections are negative additions.** The provenance of a prompt (who sent it, how) may be written after the
  prompt; when it changes what the prompt was counted for, the reducer subtracts and adds again.
- **A token is counted once**, with `AgentInputTokenUsage.From`, the one rule of the application. The model is the one
  the provider reports, not the alias chosen.
- **Work is counted where it was done.** A fork or the session that keeps what a rewind removed starts with a copy of
  its source's records; its header says from when its records are its own (`forked_from.counted_from`), and the
  inherited ones only carry the choices in force. Turns made outside CodeAlta (runs whose id starts with `external:`)
  count nothing. So the totals of a session and its forks are what was spent: nothing twice, nothing lost.
- **A run of a script** ([Code Mode](codemode.md)) is read from its records, which belong to no turn. It is counted
  when it starts, under the model in force and the sender of the turn it started in; what it did (its outcome,
  duration, calls, steps, sessions, time waiting for the user) is counted when it ends, in the quarter of its end,
  because a script may sleep for days. Its name is the first library script it loads; a run that loads none is
  `(inline)`, and one recorded before the loads were recorded `(unknown)`. A session a script started records the run
  (`workflow:<run>`), which gives the tokens and the cost of the sessions of each script at query time. The calls a
  script makes are tool calls of the session marked as the script's, and `exec` and `wait` are tools of their own kind.

`SessionFactsState.CurrentVersion` (9) versions the definitions. A new version rereads every session that still has
its journal, one at a time, keeping the old numbers until each session's new rows are saved.

## Reading journals

The journal is the source of the numbers; an agent event is only a signal to read it. An event that was dropped, or
arrived before its record was written, therefore costs nothing.

- **Never in the way of the session.** The reader opens journals with `FileShare.ReadWrite | FileShare.Delete` and takes
  no lock of the session, so it never blocks a write and never keeps a journal from being replaced or deleted.
- **Streaming and bounded.** Records are written with their discriminators first and their envelope (time, run,
  provider) last. The scanner classifies a line by its first 160 bytes, passes over the large records it does not need
  (state snapshots, tool outputs, message copies) keeping only their last 512 bytes for the envelope, and parses the
  others forward-only, stopping where the envelope begins. A record above 4 MiB is parsed from its start and its end
  only. A half-written last line is left for the next read.
- **Rewrites are detected.** A cursor remembers the fingerprint of the first line; a file that was replaced, rewound
  or shortened is read again from the start (`CatchUpResult.Restarted`).
- **One transaction per catch-up.** The facts, the roll-ups and the new cursor are saved together
  (`StatisticsStore.ApplyAsync`): a crash between two catch-ups loses nothing and counts nothing twice. A large
  journal is read in 64 MiB chunks, one transaction each; a session read again from its start replaces its rows in one
  transaction, so the old numbers stay until the new ones are complete.

## The history and the flow

The engine runs where the plugin has the journals of the instance and a database: CodeAlta Desktop
(`StatisticsPlugin.CreateForDesktop`). The TUI runs none; the desktop catches up what the terminal wrote at its next
start.

- **Nothing is read before the user chooses** how much history to read (everything, the last N days, or from today).
  The choice, its floor, a pause and a stop survive restarts. Reading more history later replaces the sessions read
  with a higher floor.
- **The history job** is one tracked plugin task, newest journals first so the present is right within seconds and
  the charts fill toward the past. It is gentle: one thread below normal priority, one session at a time, the flow
  served between sessions and chunks. Every five minutes it compares the store of journals with its cursors (sessions
  of another application, deleted journals).
- **The key of the folder of a session** (`SessionFolders`) is kept with the session: a session read now gets it from
  its header as it is read. A session read before the store kept it gets it once, after the history has nothing left to
  read, from the first line of its journal only (`StatisticsEngine.BackfillFolderKeysAsync`): a small batch per step,
  stopped by a pause, never a new reading of its facts. A journal that is gone, or whose first line is no header,
  leaves the session without a key.
- **The flow** catches a session up one second after its first signal, coalescing a streaming session to once a
  second. `OnAgentEventAsync` only enqueues the session id: a slow handler would hold the event pipeline of its
  session, and callbacks beyond the plugin's budget are dropped. The plugin opts out of streaming deltas, which the
  journal does not record.
- **A deleted journal keeps its facts**, marked deleted; `history forget-deleted` removes unsaved ones.
- **Saved statistics.** When the runtime deletes an expired session, it asks the plugin first
  (`ISessionStatisticsRetirementArchive`): a session read to the end of its current file has its facts saved in a
  separate database (`saved_statistics_after_deleted_sessions/archives.sqlite3`), outside the recoverable application
  database. That archive fails closed: integrity and digests are checked on every read, and a missing or damaged
  archive stops preservation instead of being recreated empty, since that would silently lose history. Preparation is
  best effort and never keeps a session from being deleted. A returning journal replaces saved facts only after a
  strict full-source reread. A saved copy is read with the tables of the version it was saved with; the metrics it did
  not record are named as unavailable, never shown as zero.

## Questions

`StatisticsQueries` answers every question of the canvas and of `alta statistics` with the same methods, from a
`StatisticsRequest` (period in local days, frequency, comparison, filters, limit):

- Counts and sums read the roll-ups; a question that needs the session, the project or the hour reads quarter hours.
- **A session counts under the project CodeAlta lists it under** (`ProjectCatalog.ResolveSessionProject`): the project
  its header records when the catalog has it, otherwise the project of the catalog in its folder (a session `altacli`
  wrote where no project was), never another project of the catalog. The rule is applied with each question from the
  catalog of now (`SessionProjects`), so adding a project moves the numbers of its sessions without reading a journal.
- **A session filter is always applied.** It resolves to the session and, with `withChildren`, its sub-agents; other
  filters only narrow it, and an unknown session gives the numbers of nothing (`session-not-found`), never the numbers
  of everything.
- A filter the facts cannot honor for a question (the sender of a prompt on tokens) is not applied and is named in
  `query.ignoredFilters`. `coverage` says whether the period is completely read, which the canvas hatches.
- A few numbers are computed from stored rows when asked, not stored as facts: sessions at once (from run spans, an
  observed upper bound), average context fill (sum over samples, never an average of averages), sub-agent depth.
- Results are bounded (5,000 buckets, 500 rows) and serialized by `StatisticsJson` with stable camelCase names. The
  golden file `CodeAlta.Plugin.Statistics.Tests/Golden/results.json` is the contract of the JSON for `alta statistics`,
  the canvas calls and the frontend types alike.

`alta statistics` writes one JSONL record per command and works on any host with the database; the history controls
exist only where the engine runs. They change what CodeAlta keeps, so an agent runs them only when the user asks.

## The canvas

The canvas (`frontend/src/statistics/`) is one React component over a `StatisticsApi` interface, with no knowledge of
the transport: `rpcApi.ts` binds it to the plugin's canvas calls (`statistics.*`, answered with `StatisticsJson`), and
`createFixtureApi` to generated data for browser tests and demos. The plugin declares the canvas (application scope,
keys `project:<id>` and `session:<id>` for a project or a session tab), a title-bar button that shows reading progress,
project and session menu lines, and a landing card (see [plugins](plugins.md)).

- Only the page shown asks, a question is cancelled when its key changes or its block goes away, and results are kept
  per key, so React's double effects and page switches cost nothing.
- Data changes (`StatisticsEventPump`, a quarter-second burst) mark only results whose days changed as stale, on a
  display interval the user sets (10 minutes by default), and nothing is asked while the tab is hidden.
- At most four questions run at once for all canvases (`MaximumConcurrentQuestions`), on readers that never wait for
  the history's writes.
- A session canvas is limited to that session and its sub-agents by the runtime of the canvas, not by a filter chip,
  so no reset or filter can show the numbers of others.

## Invariants guarded by tests

- Split invariance: journals cut at every record boundary give the same facts (`SplitInvarianceTests`).
- The arithmetic of every metric, frequency and filter, in several time zones, against a naive recomputation from the
  facts; the session filter against the sum of the session and its sub-agents.
- Resume, crash and rewrite: reading in halves, a crash between commits, a replaced or deleted journal
  (`StatisticsStoreTests`, `StatisticsEngineTests`).
- Real profiles: read-only harnesses (`RealProfile*`, enabled by `CODEALTA_STATS_SESSIONS`) print counts and timings,
  never text of a session.

## Limits

- Only what journals record is counted: a session deleted before it was read cannot be counted.
- The spaces of a project are the ones it has today; a chat counts in the space its header records.
- Saved statistics of a deleted session keep no folder: they count under the project its header records.
- Sessions at once count a run from its first to its last record, including time spent waiting for the user.
- The developer instance has its own statistics, as it has its own sessions and database.
