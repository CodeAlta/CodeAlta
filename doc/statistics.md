# Statistics: the store, the history, the flow and the questions

This page documents what the Statistics plugin (`src/CodeAlta.Plugin.Statistics`) keeps of the sessions and how it keeps it up to date: the tables in the application database, the job that reads the history, the flow that follows the sessions that write, the questions the pages and `alta statistics` ask, and the `alta statistics` commands. How a journal is read and what a fact is are in `doc/statistics-facts.md`; the page that shows the numbers is built on this one.

| Piece | Where | Role |
| --- | --- | --- |
| `StatisticsStore` | `Store/` | The tables of the plugin in the application database: facts, roll-ups, runs, sessions and where the reading of each journal stopped. Saving one catch-up is one write transaction |
| `StatisticsEngine` (`IStatisticsService`) | `History/` | The history job and the flow, with the state of the reading (`StatisticsStatus`) and its events |
| `StatisticsQueries` | `Query/` | The questions: summary, series, rankings, tables, distributions, records, health, one session. Pure C#, independent of any transport |
| `alta statistics` | `StatisticsCommands.cs` | The same questions as commands that write JSONL records |
| `StatisticsPlugin` | `StatisticsPlugin*.cs` | Starts the engine (CodeAlta Desktop only), signals it from the events of the sessions, and contributes the commands. The cards of the timeline are unchanged |

## The store

The tables live in the SQLite database of the instance (`<state root>/data/alta.sqlite3`; the developer instance has its own), through the `IPluginDatabase` service of the host (`doc/plugins.md`, "Plugin database"): prefix `statistics_`, owner `plugin:statistics`, one writer at a time, write-ahead log. The tables are created by `MigrateAsync(1)`. Nothing in them is text of a session: only counts and sums, the names of providers, models, tools and MCP servers, the program of a shell command (`git`) and the first two words of an `alta` command, the extension of a changed file, and the id, the title and the project of a session.

| Table | One row per | Holds |
| --- | --- | --- |
| `journal` | Session file | Where the reading stopped: the offset, the length and the stamp of the file when the reading began (zero while the reading did not reach the end), the mark of its first line, the state of the facts (JSON, with its version), the version of the facts the rows were computed with, the first quarter hour that was kept (the floor), the number of runs that had no end, whether the file is gone |
| `session` | Session | The identity (project reference, kind, parent, creator, automation, title, provider), the first and last record, whether the journal was deleted since |
| `project` | Project | The name the project had when it was last resolved, by `alta project list` |
| `run` | Run | Start, end, outcome, who sent the prompt and how, the counts of the run (requests, tool calls, tokens, compactions, answer size), the cost per unit in millionths, the provider, model, effort and permission mode at its start. Replaced each time the run changes |
| `activity_q`, `usage_q`, `cost_q`, `tool_q`, `content_q`, `detail_q`, `histogram_q` | Session, quarter hour (UTC) and the key of the family | The facts of `doc/statistics-facts.md`, one table per family. A cost total is kept in millionths of its unit, so that every number of the store is an integer and every sum is exact |
| `extreme_q` | Session, quarter hour, measure, subject | The largest value, and the run and the time it comes from |
| the same names with `_day`, `_month`, `_year` | Local day, month or year, and the same keys without the session | The roll-ups: the same measures, added up (or, for the largest values, the larger). The period is `yyyymmdd`, `yyyymm` or `yyyy`; `extreme_*` keeps the session and run of the record |
| `meta` | Key | The versions, the time zone of the roll-ups, what the user chose, where the history is complete from |

A merge is an addition, except for the three measures that are a largest value (the fullest context window, the longest tool call, and the extremes): `UPDATE ... SET x = x + excluded.x` and `MAX`. A batch row whose measures are all zero is not written.

### Roll-ups

A roll-up is a cache that can always be added up again from the facts. It is maintained in the **same transaction** as the facts: the rows a catch-up adds are added to their quarter hour, their local day, their month and their year, in memory first so that a hundred quarter hours of one day make one statement. When a session is **replaced** (a rewritten file, a new version of the facts, more history) its rows are removed, the new ones are written, and the roll-ups of every day the old or the new rows touch are added up again from the quarter hours of all sessions, because a largest value cannot be taken away; the months and years of those days follow.

The **local day** of a quarter hour is the date of its start in the time zone of the user (`TimeZoneInfo.Local`), with the rules the zone had on that date. Every offset in use is a whole number of quarter hours, so a quarter hour never spans two local days, wherever the user is and whenever the clocks change; `LocalDays` computes it from the zone, never from a fixed offset, and the tests run the roll-ups in nine zones (whole hours, half hours, quarter hours, daylight saving in both hemispheres, `Pacific/Kiritimati` at +14). `meta` records `rollup.version` and `rollup.timezone`: when either differs from the running one, `StatisticsStore.InitializeAsync` **rebuilds every roll-up from the facts alone**, a block of 30 days per transaction, with the old roll-ups readable meanwhile. The facts do not move: a journey or a change of zone costs the rebuild only.

| Measured | Value |
| --- | --- |
| Rebuild of the roll-ups of the author's profile (915 sessions, 6 months, 239,000 fact rows) | 0.3 s |
| Rebuild of a synthetic store of 190,000 fact rows | 0.8 s |
| Saving 220,000 rows of facts with their roll-ups (one transaction per session of 3,600 rows) | 5.4 s |
| Size of the database for the author's profile | 48 MB, of which the facts per quarter hour of the distributions are 60% |

### Deleted sessions

When the journal of a session is gone, its `journal` and `session` rows are marked deleted and **its facts are kept**. `alta statistics history forget-deleted` (and `IStatisticsService.ForgetDeletedAsync`) removes the facts of the deleted sessions and adds the roll-ups of their days up again.

## One operation: catch a session up

The history and the flow do the same thing to a session (`SessionCatchUp`, `doc/statistics-facts.md`): open its journal at the saved offset, read to the last complete line, turn the records into facts, and **save the facts, the roll-ups and the new cursor in one transaction** (`StatisticsStore.ApplyAsync`). A crash between two catch-ups loses nothing and counts nothing twice: the next run starts from the last saved cursor.

| Case | What happens |
| --- | --- |
| A journal read in several chunks (64 MiB each) | One transaction per chunk: the writes stay short, whatever the size of the file. The largest journal of the author's profile is 1.5 GB |
| The file is not the one the cursor belongs to (a header prepended, a file replaced, a shorter file, a state of another version of the facts) | `CatchUpResult.Restarted`: the session is read again from the start, the chunks are kept in memory (facts are small), and **one** transaction replaces the rows of the session, so the old numbers stay until the new ones are complete |
| The read is canceled (pause, stop, application closing) | It stops at a line end; what was read is saved with its cursor. The `journal` row then holds a length and a stamp of zero, so that it is never taken for a complete reading |
| A session that has been quiet for an hour (`DeadAfter`) with a run that has no end | `InterruptOpenRuns` closes the run as interrupted, once: a session that is not running writes nothing more |
| A session whose journal is not readable | It is skipped and counted (`StatisticsStatus.SkippedCount` and the first fifty with their reason); the job goes on. It is tried again when the journal changes, not at every look |

## The history job

The job runs only in **CodeAlta Desktop** (`PluginHostInfo.Frontend == Desktop`), when the plugin was made with the journals of the instance (`StatisticsPlugin.CreateForDesktop(ISessionJournalCatalog)`, which `DesktopPlugins.StatisticsDefinition` does) and the host has a database. CodeAlta TUI runs no job; the desktop catches up what the terminal wrote at its next start. The job is one tracked plugin task (`Services.Tasks.Run`, `LongRunning`) started a few seconds after the plugin is activated, and it stops, at a line end, when the plugin is deactivated.

### The choice, and what is read

Nothing is read until the user chose. The state is then `needsChoice`, and `alta statistics status` says so.

| Choice | `history read` | The floor | What happens |
| --- | --- | --- | --- |
| Everything | `--all` | none | Every journal is read, the most recently changed first |
| The last N days | `--days N` (30 and 90 are usual) | The first local day of the N days, today included | The journals changed since the floor are read. Only the facts from the floor are kept: the statistics start there |
| Nothing of the past | `--from-today` | Today | Nothing is read now. The flow keeps today, and a session of today that began earlier keeps only what happened since the start of the day |

The choice, the floor, a pause and a stop are kept in `meta`, so they survive a restart. **Reading more** is the same call with a choice that goes further back (a larger N, or `--all`); one that does not go further back changes nothing. The sessions already read with a higher floor are read again from the start and replaced (`journal.floor_q`).

### What is left to do, and in which order

At the start, after a choice, and every five minutes (sessions of another application), the engine lists the journals of the session store (`ISessionJournalCatalog`, the **whole** store, not the sessions the Explorer shows) and compares them with the `journal` table:

| Journal | To do |
| --- | --- |
| Not read, changed since the floor | Read it |
| Read, but its length or its stamp differ | Catch it up from its cursor |
| Read with another version of the facts, or with a higher floor | Read it again and replace it |
| Read, with a run that has no end, and quiet for an hour | Settle it |
| In the table, gone from the store | Mark it deleted |

The list is ordered by the **last change of the file, the most recent first**, so that the present is right within seconds and the charts fill towards the past. A new version of the facts (`SessionFactsState.CurrentVersion`) reads every session again, one at a time, while the old numbers stay until each session's new rows are saved; the reason is `facts-improved`.

### Progress, pause, stop

`StatisticsStatus` (`IStatisticsService.Status`, `StatusChanged`) is what the bar of the page and `alta statistics status` show.

| Property (JSON name) | Meaning |
| --- | --- |
| `state` | `starting`, `needsChoice`, `reading`, `paused`, `stoppedHere`, `done`, `failed` |
| `reason` | While reading: `first-read`, `extended`, `facts-improved`, `catch-up` |
| `choice`, `floorDay` | What the user chose (`all`, `days:30`, `from-today`) and the first day kept (`yyyymmdd`; absent for everything) |
| `sessionsTotal`, `sessionsDone`, `bytesTotal`, `bytesDone` | The progress of the list that is being read |
| `oldestDateReached` | The oldest local day of a session read so far |
| `completeFromDay` | The numbers are complete from this local day (`yyyymmdd`): before it, sessions are still to read. Absent when everything is read. Only the first reading fills in from the present: reading more history keeps the old floor until it is done, and a catch-up leaves the floor alone |
| `bytesPerSecond`, `etaSeconds` | The speed since the reading began (pauses do not count), and the time left |
| `currentSessionId` | The session being read |
| `skippedCount`, `skipped` | The sessions that could not be read, and why |
| `pendingFlow` | The sessions the flow still has to catch up |
| `revision` | Grows each time a catch-up is saved |
| `error` | Why the engine could not start |
| `isComplete` | `done` with nothing before `completeFromDay` |

| Control | `IStatisticsService` | Command | Effect |
| --- | --- | --- | --- |
| Choose or read more | `ChooseHistoryAsync(HistoryChoice)` | `history read` | See above |
| Pause | `PauseAsync()` | `history pause` | The reading stops at a line end and keeps its cursor; the flow goes on; kept across restarts |
| Resume | `ResumeAsync()` | `history resume` | |
| Stop here | `StopHereAsync()` | `history stop` | The floor becomes `completeFromDay`: the statistics start at the date reached, and "read more" goes further back |
| Forget deleted | `ForgetDeletedAsync()` | `history forget-deleted` | Removes the facts of the sessions whose journal is gone |

The reading is **gentle**: one thread of its own with a priority below normal for the reading itself (the CPU and disk work), one session at a time, and the flow first, between two sessions and between two chunks of a large journal.

## The flow

`StatisticsPlugin.OnAgentEventAsync` is only a **signal**: it enqueues the session id (`IStatisticsService.Signal`) and returns at once, because at most 64 callbacks may be outstanding per plugin before events are dropped, and a slow handler holds the pipeline of its session. A session is caught up **one second after its first signal** (coalescing: a session that streams is caught up once a second, never starved), from its journal and its cursor: **the event is a signal, the numbers always come from the file**, so an event that was dropped, or that arrived before its record was written, costs nothing. A session that was never seen appears with its first event. A signal received before the user chose is ignored. Whatever the state of the history, even paused or stopped, the flow keeps the present up to date.

`IStatisticsService.DataChanged` (`StatisticsDataChange`: the new `revision`, the first and last local day that changed, the sessions) tells a page which days to ask again.

## The questions

`StatisticsQueries` (`IStatisticsService.Queries`, or built over the store for a process that reads nothing) answers every question of the pages; `alta statistics` and the canvas call the same methods. Every method takes a `StatisticsRequest`:

| Property | Choices |
| --- | --- |
| `period` | `today`, `yesterday`, `Nd` (the last N days, today included), `week`, `month`, `last-month`, `year`, `all`, or `yyyy-MM-dd..yyyy-MM-dd` (an end left out is today, a start left out is the first day with data). Local days |
| `frequency` | `auto`, `hour`, `day`, `week` (from `weekStart`, or the culture), `month`, `year`. `auto`: hours for one day, days up to 90 days, weeks up to a year, months beyond. At most 5,000 buckets |
| `comparison` | `none`, `previousPeriod` (the period of the same length before), `samePeriodLastYear` |
| `filter` | `space` (the projects it has **today**), `project`, `provider`, `model`, `effort`, `origin` (`you`, `agent`, `automation`, `reminder`), `toolKind` |
| `weekStart`, `limit` | The first day of the week; the most rows or lines (default 50, 20 for series lines, 10 for rankings; at most 500) |

Counts and sums are read from the roll-ups (days, and months or years when the period is made of whole months or years); a question that needs the session (a project, a space, a session) or the hour reads the quarter hours. A filter the facts cannot honor for a question (the origin of a prompt on the tokens of a quarter hour) is **not applied** and the result names it in `query.ignoredFilters`: provider and model apply to activity, requests and cost (not to tools or prompts), effort to activity and requests, tool kind to tools, origin to prompts, project and space to everything.

Every result starts with `query` (`QueryHeader`): the period as asked, `from` and `to`, the **frequency that was used**, the time zone, the compared period, `coverage` and `notes`. `coverage` (`complete`, `historyState`, `completeFrom`) is what the page hatches: `complete` is false while the user has not chosen, or while the period starts before the day the numbers are complete from. A space filter adds the note `space-membership-is-current`: the sessions do not record the spaces of their time.

| Method | Result (JSON) | What |
| --- | --- | --- |
| `SummaryAsync` | `SummaryResult`: `buckets`, `tiles[]`, `costs[]` | The tiles of the Overview: sessions, runs, active time, prompts you sent, requests, tokens (all, input, output, fresh, cache read, cache write, reasoning), tool calls and failures, lines added and removed, files changed, errors, compactions; each has `value`, `previous`, `change` (a ratio) and `spark` (one value per bucket). The cost has a tile for **each unit**: dollars and AI credits never add up |
| `SeriesAsync(metric, group)` | `SeriesResult`: `metric`, `unit`, `group`, `buckets[]`, `series[]` (`key`, `label`, `values[]`, `previous[]`, `total`, `previousTotal`) | One metric per bucket. `StatisticsQueries.MetricNames`: runs (and by outcome), active time, errors, compactions, requests, tokens of each kind, tool calls, failures, time, bytes, files and lines, prompts (and yours, characters, words, attachments), answers, cost, sessions active and started. `GroupNames`: provider, model, effort, project, delegated (your sessions or sub-agents), tool, kind, origin, purpose, unit, prompt-kind. The runs cut by origin, or filtered by it, are read from the table of runs, which knows who sent the prompt of each. The lines beyond the limit are added up as `other`. `sessions-active` counts **different** sessions in the facts, never a sum of buckets |
| `TopAsync(kind, by)` | `TopResult`: `rows[]` (`key`, `label`, `detail`, `value`, `share`, `tokens`, `timeMs`, `calls`, `requests`, `failures`, `spark[]`), `totalRows`, `truncated` | Tools, models, projects or sessions by tokens, time or calls |
| `ToolsAsync`, `ModelsAsync`, `ProjectsAsync`, `SessionsAsync(sort)` | `ToolsResult`, `ModelsResult`, `ProjectsResult`, `SessionsResult` | The tables of the pages: tools with failure rate, time, median and 90th percentile and a line; models with each kind of token, cache share, time, context fill, costs by unit and a line, and models by effort; projects with sessions, runs, time, tokens, calls, costs and a line; sessions with their numbers (a deleted session is listed with `deleted` and its numbers) |
| `DistributionAsync(measure, subject)` | `DistributionResult`: `steps[]` (`lower`, `upper`, `count`), `count`, `p50`, `p90` | A distribution in the fixed steps (run duration, tool duration, request tokens, prompt size, run cost, tool calls of a run). Percentiles are interpolated inside the step they fall in, exact within about 19% |
| `CalendarAsync`, `WeekHourAsync` | `CalendarResult` (`days[]`), `WeekHourResult` (`weekdays[]`, `activeMs[7][24]`, `runs[7][24]`) | A year of days; the day of the week by hour |
| `DetailsAsync(list)` | `DetailsResult`: `rows[]` (`name`, `count`, `share`), `total`, `totalRows` | The names the facts count, ranked: `shell-program` (the program of a shell command: `git`, `dotnet`), `alta-command` (its first two words), `changed-file-extension`, `skill`, `permission-mode`, `compaction-trigger`, `run-origin`, `session-origin` |
| `RunsAsync(sort)` | `RunsResult`: `runs[]` (`runId`, `start`, `durationMs`, `outcome`, `origin`, `promptKind`, `promptChars`, `promptWords`, `requests`, `toolCalls`, `inputTokens`, `outputTokens`, `answerWords`...), `totalRows` | The runs of a period, one row each, sorted by `recent`, `longest`, `tokens` or `tools`: what a prompt brought back |
| `RecordsAsync` | `RecordsResult`: `records[]` | Longest run, most tool calls in a run, longest tool calls, fullest contexts, largest requests, busiest day, longest streak of days, largest prompt (the lower bound of its step) |
| `HealthAsync` | `HealthResult` | Errors and interrupted runs per bucket, the error rate per run, the tools that fail, compactions by trigger with tokens before and after, the fill of the context by model |
| `SessionAsync(id, withChildren)` | `SessionDetailResult` | One session over its life (the id may be the start of it): totals, its latest runs, models and tools; with its sub-agents when asked |
| `CoverageAsync` | `StatisticsCoverage` | Only the coverage of a period |

`StatisticsJson.Serialize<T>` writes any result as one line of JSON with camelCase names, enums as text and nulls left out; the names are stable. The results are bounded: at most 5,000 buckets, 21 lines of a series, 500 rows of a table, 50 runs of a session.

### The arithmetic is tested against a naive recomputation

The tests compute a series from the facts themselves, with plain `TimeZoneInfo` arithmetic and none of the code under test, and compare: every metric of the families (active time, runs, tokens, tool calls, prompts, cost per unit) at every frequency (day, week, month, year) in six zones; the hours of a day on which the clocks change; a month as the sum of its days read from the roll-ups and from the quarter hours; a series cut by a group equals the series without it; a project and a space filter equal the sum of the sessions of those projects; sessions active are counted in the facts; the rankings, the tables, the records, the calendar and the week by hour. Resume, split and restart invariants are in `StatisticsStoreTests` and `StatisticsEngineTests`: reading in halves equals reading at once, a crash between two commits resumes to the same totals, a rewritten session replaces its facts, a deleted journal keeps them, and the same range is never counted twice.

## `alta statistics`

The commands are the same questions, for agents and for the user. Each writes **one JSONL record** (`type`, `version`, `correlationId`, then the result) and works without a window, on any host that has the database. `0` is success, `2` a usage error (a period, a metric or a filter that is not valid), `1` another failure; an error is an `alta.error` record on stderr.

| Command | Record |
| --- | --- |
| `summary [--period] [--by] [--compare previous\|year] [--project] [--space] [--provider] [--model] [--effort] [--origin] [--tool-kind] [--week-start]` | `alta.statistics.summary` |
| `series <metric> [--group] [--period] [--by] [--limit] [filters]` | `alta.statistics.series` |
| `top <tools\|models\|projects\|sessions> [--by tokens\|time\|calls] [--limit] [--period] [filters]` | `alta.statistics.top` |
| `details <list> [--limit] [--period] [--project] [--space]` | `alta.statistics.details` |
| `session <session> [--with-children]` | `alta.statistics.session` |
| `status` | `alta.statistics.status`: the `StatisticsStatus` and `running`, whether this application reads the sessions. A process that reads nothing reads it from the store |
| `history read (--days N \| --all \| --from-today)`, `history pause`, `history resume`, `history stop`, `history forget-deleted` | `alta.statistics.history` (the status after the action, and `action`), `alta.statistics.forgotten` (`sessions`). Only where the engine runs (CodeAlta Desktop): elsewhere `statistics.notRunning` |
| `estimate <text>` | `alta.statistics.estimate`, as before |

The history controls change what CodeAlta keeps, not a setting of the user; an agent runs them only when the user asks.

## Measured

On the author's profile (915 journals, 10.4 GiB, a real profile that was in use meanwhile), Windows 11, NVMe, files in the cache of the system, the plugin database in a temporary folder, `RealProfileHistoryHarness`:

| | |
| --- | --- |
| The whole history, end to end, with the writes to SQLite | 15.8 s and 24.3 s in two runs (430 to 660 MiB/s) |
| Peak working set of the process of the test | 108 to 111 MiB |
| Database after the reading | 48 MB; `histogram_q` 149,000 rows, `detail_q` 24,500, `extreme_q` 29,800, `tool_q` 16,500, `content_q` 11,700, `activity_q` 4,300, `usage_q` 3,900 |
| The check of the journals at the next start, nothing to read | 0.03 s |
| A question of a page (`all` periods) | 7 to 41 ms |
| The facts against a second reading of the same journals up to the same offsets | Equal: requests, runs, active time and tool calls |

A cold disk was not measured. `dotnet test src -c Release --filter RealProfileHistoryHarness` with `CODEALTA_STATS_SESSIONS` set runs it (`doc/statistics-facts.md`, "Running the harness on a real profile").

## Limits

- The facts count what is in the journals (`doc/statistics-facts.md`, "Limits"). A session that was deleted before it was read cannot be counted.
- The spaces of a project are the ones it has today.
- The origin of a prompt filters prompts and runs; time and tokens of a quarter hour have no sender, and a result says so in `query.ignoredFilters`.
- A run that is still going when the application is closed is closed as interrupted by the next start once its session has been quiet for an hour.
- The statistics of the developer instance are its own (`<state root>/data/alta.sqlite3`, its own sessions).
