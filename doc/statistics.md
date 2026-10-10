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

The tables live in the SQLite database of the instance (`<state root>/data/alta.sqlite3`; the developer instance has its own), through the `IPluginDatabase` service of the host (`doc/plugins.md`, "Plugin database"): prefix `statistics_`, owner `plugin:statistics`, one writer at a time, write-ahead log. The tables are created by `MigrateAsync(2)` (`StatisticsStore.SchemaVersion`; version 2 adds `run.skipped_ms`). They hold counts and sums, the names of providers, models, tools and MCP servers, the program of a shell command (`git`) and the first two words of an `alta` command, the extension of a changed file, and the id, the title and the project of a session. The **title** is the only text of a session that is kept: the application makes it from the start of the first prompt (its first sentence, at most 80 characters), so it is kept in a form of its own (the table below), in the `session` row and in the state of the `journal` row. No prompt, answer, tool argument or tool result is copied.

A name that is taken out of a command is kept only when it is written as a name, because the rest of a command may hold a secret:

| Name | What is kept | What is not |
| --- | --- | --- |
| The program of a shell command | The first word, without its folder and `.exe`, in lower case, when it is made of letters, digits, `.`, `_`, `-` and `+`, holds a letter and is at most 32 long. A **quoted** first word is one only when it is called (`& "C:\Program Files\x.exe"`) or written as a path (`C:\…`, `/…`, `./…`, `~/…`) and followed by a space and something that is not an operator or the end | A quoted text that starts a command is a value in PowerShell (`"…" \| docker login --password-stdin`): piped, redirected, joined, left alone or not a path, it is never kept. Nor is a number, a word longer than 32 (a token), or a word that goes on in a quoted part. A variable set before the program is stepped over with its value (`PGPASSWORD=… psql` counts `psql`, `$env:TOKEN='…'; gh` counts `gh`); a command whose value has no end that can be told has no program, since the word that follows could be a word of the value: a command substitution, a quote that does not close, an escape of the shell (a backslash, the backtick of PowerShell, a doubled quote), text that goes on after the closing quote, an operator of the shell inside the value, a `${…}` or a `$(…)` in a double-quoted value (a quote inside it is not the end), an empty value followed by a space, a value beyond the first 160 bytes. The command is read as the shell reads it: the escapes of JSON are undone first (the journals write a quote and an ampersand as `\u0027` and `\u0026`). An expression or a variable (`$x`, `[IO.File]::…`) is not a program |
| The command of `alta` | The first two words written as commands are (a lower-case letter, then lower-case letters, `-`, `_`; at most 24) | Options and everything after them, and any other word: a sentence, a path, a name with a capital, a word with a digit (an identifier, a password) |
| The name of a skill | The `skillName` of a call that activates one, when it is made of letters, digits, `.`, `_`, `-` and `:` (at most 64) | Any other text given in its place |
| The extension of a changed file | What follows the last dot of the decoded name, in lower case, when it is letters, digits, `_`, `-`, `+` (at most 12); JSON escapes such as the `+` in `.c++` are decoded first | The path and the name |
| The title of a session (`SessionTitles.Clean`) | Its words separated by one space, at most 80 characters (cut with `…`) | A word that holds a `/` or a `\` (a path, an address) is replaced by `…`; a title made of such words only is not kept, and the session is named by its id |

| Table | One row per | Holds |
| --- | --- | --- |
| `journal` | Session file | Where the reading stopped: the offset, the length and the stamp of the file when the reading began (zero while the reading did not reach the end), the mark of its first line, the state of the facts (JSON, with its version), the version of the facts the rows were computed with, the first quarter hour that was kept (the floor), the number of runs that had no end, whether the file is gone |
| `session` | Session | The identity (project reference, kind, parent, creator, automation, title, provider), the first and last record, whether the journal was deleted since |
| `project` | Project | The name the project had when it was last resolved, by `alta project list` |
| `run` | Run | Start, end, the milliseconds between the two that are not time of the run (`skipped_ms`: a record whose time is more than seven days from the others), outcome, who sent the prompt and how, the counts of the run (requests, tool calls, tokens, compactions, answer size), the cost per unit in millionths, the provider, model, effort and permission mode at its start. Replaced each time the run changes |
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
| The read is canceled (pause, stop, application closing) | It stops at a line end; what was read is saved with its cursor. The `journal` row then holds a length and a stamp of zero, so that it is never taken for a complete reading. A session that is read **again from its start** (the row above) saves nothing when it is canceled before its end: a part of the session does not take the place of all of it, the old numbers stay, and the next reading starts from the start again |
| A session that has been quiet for an hour (`DeadAfter`) with a run that has no end | `InterruptOpenRuns` closes the run as interrupted, once: a session that is not running writes nothing more. The engine cannot tell a dead session from a run that waits for the user, so a later record of that run opens it again and takes the interruption back (`doc/statistics-facts.md`, "Interrupted") |
| A session whose journal is not readable | It is skipped and counted (`StatisticsStatus.SkippedCount` and the first fifty with their reason); the job goes on. It is tried again when the journal changes, not at every look, and it stays counted at the following looks. `ResumeAsync` on a history that is done or stopped with skipped sessions ("Try again") tries them again as they are |
| The file is replaced between two chunks of a session that is read again | The facts kept of the first file are dropped: the batch of a restarted reading holds the session from its start |

## The history job

The job runs only in **CodeAlta Desktop** (`PluginHostInfo.Frontend == Desktop`), when the plugin was made with the journals of the instance (`StatisticsPlugin.CreateForDesktop(ISessionJournalCatalog)`, which `DesktopPlugins.StatisticsDefinition` does) and the host has a database. CodeAlta TUI runs no job; the desktop catches up what the terminal wrote at its next start. The job is one tracked plugin task (`Services.Tasks.Run`, `LongRunning`) started a few seconds after the plugin is activated, and it stops, at a line end, when the plugin is deactivated.

### The choice, and what is read

Nothing is read until the user chose. The state is then `needsChoice`, and `alta statistics status` says so.

| Choice | `history read` | The floor | What happens |
| --- | --- | --- | --- |
| Everything | `--all` | none | Every journal is read, the most recently changed first |
| The last N days | `--days N` (30 and 90 are usual) | The first local day of the N days, today included | The journals changed since the floor are read. Only the facts from the floor are kept: the statistics start there |
| Nothing of the past | `--from-today` | Today | Nothing is read now. The flow keeps today, and a session of today that began earlier keeps only what happened since the start of the day |

The choice, the floor, a pause and a stop are kept in `meta`, so they survive a restart. **Reading more** is the same call with a choice that goes further back (a larger N, or `--all`); one that does not go further back changes nothing. The sessions already read with a higher floor are read again from the start and replaced (`journal.floor_q`). Until then a session that writes is caught up by the flow with the floor its rows have: the floor of a row is lowered only by the reading that replaces it, so a session cannot lose its older days to a signal that came first.

### What is left to do, and in which order

At the start, after a choice, and every five minutes (sessions of another application), whether the history is done or was stopped by the user, the engine lists the journals of the session store (`ISessionJournalCatalog`, the **whole** store, not the sessions the Explorer shows) and compares them with the `journal` table:

| Journal | To do |
| --- | --- |
| Not read, changed since the floor | Read it |
| Read, but its length or its stamp differ | Catch it up from its cursor |
| Read with another version of the facts, or with a higher floor | Read it again and replace it |
| Read, with a run that has no end, and quiet for an hour | Settle it |
| In the table, gone from the store | Mark it deleted |

The list is ordered by the **last change of the file, the most recent first**, so that the present is right within seconds and the charts fill towards the past. A new version of the facts (`SessionFactsState.CurrentVersion`) reads every session again, one at a time, while the old numbers stay until each session's new rows are saved; the reason is `facts-improved`. Only a session whose journal still exists can be read again: the rows of a session that was **deleted** since stay as the version that read it made them. What an earlier version kept and a later one no longer keeps (before version 4: a word of the value of a variable, or a quoted text at the start of a command, taken for the program of a shell command; a title with the paths it named; any text given as the name of a skill) is therefore removed for those sessions by `ForgetDeletedAsync` (`history forget-deleted`, "Forget deleted sessions"), and by nothing else short of a reset.

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
| `error` | Why the engine could not start (`failed`). The engine is started only when every step of its preparation worked, so a start that failed at any step can be tried again. The job does not end: it waits for a start that works, which `ResumeAsync` ("Try again") tries, and which any question that prepares the tables is too. The engine does not try by itself |
| `isComplete` | `done` with nothing before `completeFromDay` |

| Control | `IStatisticsService` | Command | Effect |
| --- | --- | --- | --- |
| Choose or read more | `ChooseHistoryAsync(HistoryChoice)` | `history read` | See above |
| Pause | `PauseAsync()` | `history pause` | The reading stops at a line end and keeps its cursor; the flow goes on; kept across restarts |
| Resume | `ResumeAsync()` | `history resume` | A paused reading goes on. On a history that is done or stopped with sessions that could not be read, they are tried again. On an engine that could not start (`failed`), the start is tried again, and nothing else: a reading that was paused stays paused |
| Stop here | `StopHereAsync()` | `history stop` | The floor becomes `completeFromDay`: the statistics start at the date reached, and "read more" goes further back. Stopped before a journal was listed, the statistics start today. A stopped history still catches up what changes from its floor (the state is `reading` with the reason `catch-up` meanwhile, and `stoppedHere` again after). Only the reading the user chose (`first-read`, `extended`) is stopped that way: stopping a catch-up, or the new reading of a new version of the facts, changes neither the floor nor the choice. It ends where it is, the history is `done` or `stoppedHere` as it was before, and the next look at the journals reads what is left |
| Forget deleted | `ForgetDeletedAsync()` | `history forget-deleted` | Removes the facts of the sessions whose journal is gone |

The reading is **gentle**: one thread of its own with a priority below normal for the reading itself (the CPU and disk work), one session at a time, and the flow first, between two sessions and between two chunks of a large journal. Between two chunks the flow catches up the **other** sessions: the session whose journal is being read stays in the flow until its last chunk is saved, because a catch-up from the saved cursor would read bytes the history is about to read, and they would be counted twice.

## The flow

`StatisticsPlugin.OnAgentEventAsync` is only a **signal**: it enqueues the session id (`IStatisticsService.Signal`) and returns at once, because at most 64 callbacks may be outstanding per plugin before events are dropped, and a slow handler holds the pipeline of its session. A session is caught up **one second after its first signal** (coalescing: a session that streams is caught up once a second, never starved), from its journal and its cursor: **the event is a signal, the numbers always come from the file**, so an event that was dropped, or that arrived before its record was written, costs nothing. A session that was never seen appears with its first event. A signal received before the user chose is ignored. Whatever the state of the history, even paused or stopped, the flow keeps the present up to date.

`IStatisticsService.DataChanged` (`StatisticsDataChange`: the new `revision`, the first and last local day that changed, the sessions) tells a page which days to ask again.

## The questions

`StatisticsQueries` (`IStatisticsService.Queries`, or built over the store for a process that reads nothing) answers every question of the pages; `alta statistics` and the canvas call the same methods. Every method takes a `StatisticsRequest`:

| Property | Choices |
| --- | --- |
| `period` | `today`, `yesterday`, `Nd` (the last N days, today included), `week`, `month`, `last-month`, `year`, `all`, or `yyyy-MM-dd..yyyy-MM-dd` (an end left out is today, a start left out is the first day with data). Local days |
| `frequency` | `auto`, `hour`, `day`, `week` (from `weekStart`), `month`, `year`. `auto`: hours for one day, days up to 90 days, weeks up to a year, months beyond. At most 5,000 buckets |
| `comparison` | `none`, `previousPeriod` (the period of the same length before), `samePeriodLastYear` |
| `filter` | `space` (the projects it has **today**, and the chats: the sessions of no project, which the Explorer shows in every space), `project`, `provider`, `model`, `effort`, `origin` (`you`, `agent`, `automation`, `reminder`), `toolKind`, `session` (one session, by its identifier or by the start of it when one session matches) with `withChildren` (also the sessions it created, at any depth: its sub-agents) |
| `weekStart`, `limit` | The first day of the week; the most rows or lines (default 50, 20 for series lines, 10 for rankings; at most 500). A request that names no first day gets `StatisticsQueries.DefaultWeekStart`, the one of the regional settings of the computer. `alta statistics` uses it unless `--week-start` is given, and the canvas reads it with `statistics.context` and names it in every request and in its calendars, so a command and the page cut the same weeks |

Counts and sums are read from the roll-ups (days, and months or years when the period is made of whole months or years); a question that needs the session (a project, a space, a session) or the hour reads the quarter hours. A filter the facts cannot honor for a question (the origin of a prompt on the tokens of a quarter hour) is **not applied** and the result names it in `query.ignoredFilters`: provider and model apply to activity, requests and cost (not to tools or prompts), effort to activity and requests, tool kind to tools, origin to prompts, project and space to everything.

**A session is a limit that is always applied.** `ResolveAsync` turns `filter.session` into the list of sessions the numbers are read for (`ResolvedQuery.SessionIds`: the session, then its sub-agents with `withChildren`, found by a recursive walk of `parent_session_id` whose `UNION` keeps a session once, so parents that name each other end the walk), and every question reads the quarter hours of those sessions: the aggregates, the sessions started and active, the runs, the records. The other filters narrow it (a project, a space with its chats, a provider), none widens it. A session the statistics do not know (it is not read yet, or the identifier is wrong) gives numbers of **nothing**, with the note `session-not-found`, and never the numbers of every session; a session that is named and empty, or the start of several sessions, is a request that is not valid. With a session, the period `all` is the life of those sessions, from the day of their first record to the day of their last (and no later than today), so a session of one afternoon is cut in hours and not in the weeks of the whole history.

Every result starts with `query` (`QueryHeader`): the period as asked, `from` and `to`, the **frequency that was used**, the time zone, the compared period, `coverage` and `notes`. `coverage` (`complete`, `historyState`, `completeFrom`) is what the page hatches: `complete` is false while the user has not chosen, or while the period starts before the day the numbers are complete from. A space filter adds the note `space-membership-is-current`: the sessions do not record the spaces of their time. A session filter that names a session the statistics do not know adds `session-not-found`.

| Method | Result (JSON) | What |
| --- | --- | --- |
| `SummaryAsync` | `SummaryResult`: `buckets`, `tiles[]`, `costs[]` | The tiles of the Overview: sessions, runs, active time, prompts you sent, requests, tokens (all, input, output, fresh, cache read, cache write, reasoning), tool calls and failures, lines added and removed, files changed, errors, compactions; each has `value`, `previous`, `change` (a ratio) and `spark` (one value per bucket). The cost has a tile for **each unit**: dollars and AI credits never add up |
| `SeriesAsync(metric, group)` | `SeriesResult`: `metric`, `unit`, `group`, `buckets[]`, `series[]` (`key`, `label`, `values[]`, `previous[]`, `total`, `previousTotal`) | One metric per bucket. The `key` of a line is the value a filter takes and its `label` the name: the same for a provider, a model or a tool, the id and the name for a project. A group the facts keep as a number (`kind`, `origin`, `prompt-kind`, `content-kind`, `purpose`) is keyed by its name too (`shell`, `you`), never by the number, so a page can name a line and filter on it by its key. `StatisticsQueries.MetricNames`: runs (and by outcome), active time, errors, compactions, requests, tokens of each kind, tool calls, failures, canceled calls, time, bytes, files and lines, prompts (and yours, characters, words, attachments), answers (and their characters and words), the reasonings a model showed (`reasonings`, `reasoning-chars`), the instructions of the sessions (`instructions`, `instruction-chars`, `instruction-tokens`), cost, sessions active and started, and three that are computed when they are asked (the table below): `sessions-at-once`, `context-fill` and its `context-samples`. `GroupNames`: provider, model, effort, project, delegated (your sessions or sub-agents), tool, kind, origin, purpose, unit, prompt-kind. The runs cut by origin, or filtered by it, are read from the table of runs, which knows who sent the prompt of each. The lines beyond the limit are added up as `other`. `sessions-active` counts **different** sessions in the facts, never a sum of buckets. The `total` of a line is the sum of its values, except for `sessions-at-once` (the most of the period) and `context-fill` (the average of the period) |
| `TopAsync(kind, by)` | `TopResult`: `rows[]` (`key`, `label`, `detail`, `value`, `share`, `tokens`, `timeMs`, `calls`, `requests`, `failures`, `spark[]`), `totalRows`, `truncated` | Tools, models, projects or sessions by tokens, time or calls. A model is the model of a provider, as in the table of the models: its `key` is `provider/model`, its `label` the model and its `detail` the provider, and the same name under two providers is two rows |
| `ToolsAsync`, `ModelsAsync`, `ProjectsAsync`, `SessionsAsync(sort)` | `ToolsResult`, `ModelsResult`, `ProjectsResult`, `SessionsResult` | The tables of the pages: tools with failure rate, time, median and 90th percentile and a line; models with each kind of token, cache share, time, context fill, costs by unit and a line, and models by effort; projects with sessions, runs, time, tokens, calls, costs and a line; sessions with their numbers (a deleted session is listed with `deleted` and its numbers) |
| `DistributionAsync(measure, subject)` | `DistributionResult`: `steps[]` (`lower`, `upper`, `count`), `count`, `p50`, `p90` | A distribution in the fixed steps (run duration, tool duration, request tokens, prompt size, run cost, tool calls of a run). Percentiles are interpolated inside the step they fall in, exact within about 19% |
| `CalendarAsync`, `WeekHourAsync` | `CalendarResult` (`days[]`), `WeekHourResult` (`weekdays[]`, `activeMs[7][24]`, `runs[7][24]`) | A year of days; the day of the week by hour |
| `DetailsAsync(list)` | `DetailsResult`: `rows[]` (`name`, `count`, `share`), `total`, `totalRows` | The names the facts count, ranked: `shell-program` (the program of a shell command: `git`, `dotnet`), `alta-command` (its first two words), `changed-file-extension`, `skill`, `permission-mode`, `compaction-trigger`, `run-origin`, `session-origin`. `sub-agent-depth` is computed from the parents of the sessions (the table below): its names are depths, in that order |
| `RunsAsync(sort)` | `RunsResult`: `runs[]` (`runId`, `start`, `durationMs`, `outcome`, `origin`, `promptKind`, `promptChars`, `promptWords`, `requests`, `toolCalls`, `inputTokens`, `outputTokens`, `answerWords`...), `totalRows` | The runs of a period, one row each, sorted by `recent`, `longest`, `tokens` or `tools`: what a prompt brought back |
| `RecordsAsync` | `RecordsResult`: `records[]` | Longest run, most tool calls in a run, longest tool calls, fullest contexts, largest requests, busiest day, longest streak of days, largest prompt (the lower bound of its step) |
| `HealthAsync` | `HealthResult` | Errors and interrupted runs per bucket, the error rate per run, the tools that fail, compactions by trigger with tokens before and after, the fill of the context by model |
| `SessionAsync(id, withChildren)` | `SessionDetailResult` | One session over its life (the id may be the start of it): totals, its latest runs, models and tools; with its sub-agents when asked |
| `CoverageAsync` | `StatisticsCoverage` | Only the coverage of a period |

### Numbers computed from the rows

Three numbers are not facts of their own: they are computed, when they are asked, from rows the store already keeps (`StatisticsQueries.Derived.cs`). No journal is read for them, and the layout of the store does not change.

| Number | From | What it is, and what it is not |
| --- | --- | --- |
| `series sessions-at-once` | The `run` rows: session, first and last time | The most **different sessions with a run going at the same moment**, in each bucket; the total of the line is the most of the period. A run is going from its first record to its last one, as the journal dates them: this is what was **observed**, not a measure of work. It includes the time a run waits for an answer or a permission (a session writes nothing meanwhile), a run that is still open ends at its last record, and a run that stopped without an end ends where it was seen last. A run that ends at the moment another starts was not going with it, and two runs of one session are one session. A run whose records are more than seven days apart (`skipped_ms > 0`) has a first and a last time that are not one stretch of time, and nothing says where it was going between them: it is **left out**, and the result carries the note `runs-of-unknown-time-left-out`. The filters of a run apply (provider, model, effort, origin, project, space); the kind of tool is ignored; there is no group. Before `coverage.completeFrom` the sessions are not all read, so the number is a least number there |
| `series context-fill` | `usage`: `context_fill_ppm_sum` and `context_samples` | The **average fill of the context window**, from 0 to 1: the sum of the fills of the requests that reported a window over their number, in each bucket, and over the period for the total. It is never an average of averages: a quarter hour with one request does not weigh as much as one with a hundred. It can be cut like the tokens (model, provider, effort, project, purpose); the lines are the groups that reported a window, the most samples first, and the rest is one line computed the same way. A bucket **without a sample is 0**: `series context-samples`, with the same group and limit, says which, and the page draws a hole there (`maskSeries`). A request whose provider gives no window is not a sample |
| `details sub-agent-depth` | `session.parent_session_id` | For the sessions with a parent **that started in the period** (the project and the space apply; the other filters are ignored): how many parents are above each, `1` for a sub-agent of a session of its own, `2` for a sub-agent of a sub-agent, as rows in the order of the depth with their count and share. The parents are followed through every session the store knows, whatever the period. A parent that is not known (it is older than the history that was read, or its numbers were forgotten) is the top of what is known, and so is a parent that was already met (journals that name each other): the depth is then **at least** what is given, and the result carries the note `some-parents-unknown` |

A session filter limits the three of them as it limits the facts: the sessions at once are the session and its sub-agents with a run going, the fill is of their requests, and the depth counts the sub-agents of the tree of the session. The **depth of each is still from the top of the whole tree**, not from the session of the filter: a sub-agent of a sub-agent is at 2 on the page of its parent as on the page of every session, and the parents above the session are followed though they are not counted.

`context-fill` and `context-samples` exclude groups without a sample in either period before applying the limit. Both rank by current samples, then by ordinal key, including previous-only groups; their named lines and weighted `other` therefore stay paired. The Models block uses the sample counts to decide whether it is empty: measured 0% is still data. Activity shows `runs-of-unknown-time-left-out` as a caption on Sessions at once. Agents shows `some-parents-unknown` on the depth block and labels its rows as lower bounds, not verified relationships to your sessions.

`StatisticsJson.Serialize<T>` writes any result as one line of JSON with camelCase names, enums as text and nulls left out; the names are stable. The results are bounded: at most 5,000 buckets, 21 lines of a series, 500 rows of a table, 50 runs of a session.

### The arithmetic is tested against a naive recomputation

The tests compute a series from the facts themselves, with plain `TimeZoneInfo` arithmetic and none of the code under test, and compare: every metric of the families (active time, runs, tokens, tool calls, prompts, cost per unit) at every frequency (day, week, month, year) in six zones; the hours of a day on which the clocks change; a month as the sum of its days read from the roll-ups and from the quarter hours; a series cut by a group equals the series without it; a project and a space filter equal the sum of the sessions of those projects; sessions active are counted in the facts; the rankings, the tables, the records, the calendar and the week by hour. `StatisticsSessionFilterTests` does the same for a session filter: every question of every page equals the sum of the facts of the session and of its sub-agents at two levels, alone and beside a project, a space or a provider; a session that is not known gives nothing; sessions that name each other as parents are walked once. `StatisticsDerivedMetricsTests` has the session filter on the three computed numbers, each against the same store with the sessions of someone else in it. Resume, split and restart invariants are in `StatisticsStoreTests` and `StatisticsEngineTests`: reading in halves equals reading at once, a crash between two commits resumes to the same totals, a rewritten session replaces its facts, a deleted journal keeps them, and the same range is never counted twice.

## The canvas

The Statistics canvas (`src/CodeAlta/frontend/src/statistics/`) draws the questions above. It is one React component, `StatisticsCanvas({ api, context })`, that knows no transport: it asks a `StatisticsApi` and draws what comes back, and the application binds that interface to the plugin. `createFixtureApi` is a second implementation over generated data (six months, several providers, models and projects, a history in each of its states), used by the browser tests and for demos.

| Piece | Role |
| --- | --- |
| `api.ts` | `StatisticsApi`: one method for each question (`summary`, `series`, `top`, `tools`, `models`, `projects`, `sessions`, `session`, `distribution`, `calendar`, `weekHour`, `records`, `health`, `details`, `runs`), the controls of the history (`status`, `chooseHistory`, `pause`, `resume`, `stopHere`, `forgetDeleted`, `resetStatistics?`), `costEstimate?` and `subscribe` for the status and the days that changed. Every question takes an `AbortSignal`. `StatisticsContext` says where the canvas is: its instance id, whether it is visible, the space and project it opened for, the spaces, `openSession` |
| `types.ts` | The JSON of every result, as TypeScript. `src/CodeAlta.Plugin.Statistics.Tests/Golden/results.json` holds one sample of each as the plugin writes it: a C# test keeps the file equal to the serializer, `golden.test.ts` checks the types against it |
| `frame.ts` | The bar every page shares: the period, the frequency, the comparison, the filters, the page. A reducer changes it; `encodeFrame` and `decodeFrame` write it as a short query string kept per canvas instance, so a reload keeps it; `requestOf` turns it into the request of a page |
| `runtime.tsx`, `queryStore.ts`, `useQuery.ts` | The state of a canvas (one provider per canvas): the frame, the status of the history, the results read, the controls |
| `options.ts`, `steps.ts` | The ECharts options of the pages and the arithmetic of the steps of a distribution, as pure functions |
| `FrameBar.tsx`, `HistoryBar.tsx`, `pages/` | The bar, the history bar and the first-time card, and one component for each page |

### Pages

| Page | Blocks |
| --- | --- |
| Overview | Tiles (sessions, runs, active time, prompts you sent, tokens and a tile for each unit of cost), activity over time stacked by provider, project or model in tokens, requests or time with a brush that sets the period, the year as a calendar, the top projects, models and tools, the records |
| Activity | Sessions started and active, runs by outcome, active time, the week by hour, the most sessions at once, how long a run takes, runs and active time per session |
| Models | Tokens by model, what tokens are made of, the share of input read from the cache, the size of a request, reasoning effort, the average fill of the context by model, the table of models |
| Cost | For each unit that has a cost: the cost over time, by model, by project and per run. An estimate from public prices, marked as such, only when the binding offers `costEstimate` |
| Tools | Calls by kind, the table of tools, where the time goes, the duration of the most called tools, the shell programs, the `alta` commands, MCP servers |
| Prompts | Prompts by sender or by kind, the size of your prompts, attachments, what comes back for a prompt of yours |
| Agents | Sessions started by you and by agents, the share of tokens and time in sub-agents, the depth of the sub-agents, the largest trees of sessions, runs by who started them |
| Code | Files changed, lines added and removed, by project and by kind of file |
| Projects | The table of projects, the spaces, the time by space and project |
| Sessions | The sessions of the period as a table that sorts on any number; a row opens its session |
| Health | Errors, interrupted runs, the tools that fail, compactions, how full the context gets |

A chart is never alone: each has "Show as table" (the same numbers), a name for a screen reader, a legend of buttons and a tooltip. A click on a bar, a legend entry or a row adds the filter (a provider, a model, a project, a kind of tool); a click on a day of the calendar sets the period to that day; a session row opens the session.

### The bar

| Control | Choices |
| --- | --- |
| Period | Today, the last 7, 30 or 90 days, this month, last month, this year, all time, or two dates |
| Frequency | Auto (as the plugin picks it), hour, day, week, month, year. A frequency that does not fit the period (hours over a year) is not offered |
| Compare | No comparison, the previous period, the same period last year: tiles show their change with an arrow and a sign, time charts a dashed line |
| Filters | Space, project, provider, model, reasoning effort, who started the work, kind of tool, as chips. A chip of a filter that a page cannot honor (`query.ignoredFilters`) is dashed |
| Reset | Back to the frame the canvas opened with: the shown space or project, 30 days; all time for the canvas of a session |

The canvas opens filtered on the space the window shows (`context.spaceId`) or on a project (`context.projectId`), and removing the chip shows everything.

**The canvas of a session** (`context.sessionId`, from the key `session:<id>`) is limited to that session and its sub-agents. The session is the subject of the canvas, not a filter of its bar: `sessionScoped` (`frame.ts`) adds `filter.session` and `filter.withChildren` to every request in `request()` of the runtime, after what the frame and the page put in it, so no chip removes it, "Reset" keeps it, the stored frame does not hold it, and a page that empties the filter to list the models to choose among still asks inside the session. The bar shows it as a first chip, **Session: <title>**, which has no cross and opens the session (the title is the one of the row of the session, asked with `sessions`; the start of the identifier until the session is read). Such a canvas opens with no filter of a space or of a project (the sub-agents of a session may be of another project) and on **All time**, which the plugin gives as the life of the session. The Sessions page lists the session and its sub-agents, each one click from its chat. In a narrow canvas (the canvas answers to its own width) the dates of the period go first, so the menu stays on the first row, and the row of pages scrolls: it fades on the side that has more and brings the page in front into view.

### The history in the canvas

| State | What the canvas shows |
| --- | --- |
| `needsChoice` | A card instead of the pages: all the history (with the number of sessions and the date from the status, when the plugin gives them), the last 90 days, from today |
| `reading` | A bar under the bar: sessions read of the total, the date reached, the time left, **Pause**. The canvas is in use; the part of a chart before `coverage.completeFrom` is hatched and says "Not read yet". When it ends, one notification |
| `paused` | The date reached, the sessions left, **Resume** and **Stop here** |
| `stoppedHere` | "The charts start on…"; "Read more history…" in the menu of the canvas |
| `done` or `stoppedHere` with skipped sessions | "3 sessions could not be read", with their reasons and **Try again**; on a stopped history it follows the sentence of the stop |
| `failed` | The reason, and **Try again**, which starts the engine again |

The menu of the canvas also has "Forget deleted sessions" and, when the binding offers `resetStatistics`, "Reset statistics…", which asks first ("Reset the statistics?", a small popover under the menu with Cancel and Reset). "Read more history…" lists the choices that go further back than the one made.

### Lifecycle

- Only the page that is shown is mounted, and only it asks. A question is held under its key (the method, the request, its arguments) in a store of the canvas that keeps 96 results; coming back to a page shows them at once.
- A question starts a moment after its key settles and is canceled when the key changes or the block goes away, so the effects that run twice under React StrictMode ask once.
- While `context.visible` is false nothing is asked and the charts draw nothing. The status and the changes that arrive meanwhile are kept and applied when the canvas is shown again.
- `DataChanged` events are gathered for a second; then the results whose period (or compared period) holds a changed day are marked stale, and the page that is shown asks for those again, the others not. A change that arrives while a question is in flight leaves its answer stale when it comes back, and the block asks again at once.
- Several canvases stay mounted in one window (the one of the application, the one of a project): the ids of the tabs and of the first-time card come from `useId`, so no two elements share one. What the history bar tells a screen reader (`role="status"`) is the sentence of its state, not the bar: the buttons are not read with it, and the numbers of the progress are left to the progress bar.
- A chart is drawn with the SVG renderer (canvas above 1,500 buckets). The hatch of what is not read yet is CSS over the plot box the option fixes, so it is not a legend entry or a row of the table.
- Numbers and dates are written with the locale of the window (`Intl`): `1.2M`, `1 h 05`, `$1,511`; the sentences are in `statistics/messages.ts`, in six languages. The marks of a logarithmic axis of time are short and in one unit (`100 ms`, `10 s`, `17 min`, `2.8 h`, `fmt.durationMark`), and one that would run into its neighbor is left out.
- A number is filed under a key and read under a name. A tool is keyed `<kind of activity>:<name>` (`ToolCall:read_file`, `Skill:alta`), every shell tool `shell`, and a tool of an MCP server keeps its long name (`ToolCall:mcp__github__issue_read`): `toolParts` and `toolName` (`pages/shared.ts`) write `read_file`, and `issue_read (github)` with the server, wherever a tool is shown. A provider is keyed as its sessions recorded it (`claude-code`): `providerName` of the runtime writes the name the window shows the provider under (`context.providers`, from `statistics.context`), and the key for a provider the window no longer has. A filter and a color always use the key, and the chip of a provider filter is written from the key each time, so it follows the names when they arrive after it. One tool can come under two keys (the same name recorded as two kinds of activity): `mergeToolRows` adds such rows up for the table, the lists and the charts of the Tools page, and the box of the tool is drawn from the durations of all its keys (`mergeSteps`). A row of the models is a model of a provider: where a model is named alone and two providers have it, `modelNames` adds the provider (`gpt-5.6-sol (Codex)`), and the filter offers the model once.
- A linear axis of time steps by a round time (`timeAxis`: 1, 2, 5, 10, 15 or 30 seconds or minutes, 1, 2, 3, 6, 12 or 24 hours, then days) in at most six parts, and its marks are written whole, from `0` (`15 min`, `1 h 30`, `12 h`).

### Tests

`statistics.test.ts` (the pure parts: periods and frequencies, the frame and its query string, the request, the formatters, the options, the steps, the store, the history states, the filters, the fixture), `golden.test.ts` (the JSON shapes), and `statistics.browser.test.ts`, which mounts the canvas in headless Edge under the production policy and React StrictMode over a recording fixture API: every page in both themes, the requests the frame produces, the first time, the pause, a hidden tab, a burst of changes, an error and its retry, an empty period, 220 page switches, the keyboard, the languages, narrow and zoomed, and a change that arrives while a question is in flight. `canvas.browser.test.ts` mounts the module of the plugin (`canvas.tsx`) the same way, over an `alta` object whose calls cross the wire as JSON. `sessionScope.test.ts` and `sessionScope.browser.test.ts` are the canvas of a session (every page asks inside it; a filter, Reset and a reload keep it; a session that is not read shows nothing of the others) and the button of the card of a turn in the real window of its details (`turnCard.mount.tsx`).

## The plugin side of the canvas

In CodeAlta Desktop the plugin declares the canvas, its button and its commands (`StatisticsPlugin.Canvas.cs`); `src/CodeAlta.Plugin.Statistics/Canvas/` holds the calls and the events. None is declared when the plugin reads nothing (CodeAlta TUI, no database, no journals).

| What | How |
| --- | --- |
| Canvas | `statistics`, scope Application, icon `chart-column`, `Script = PluginScript.App("statistics")`, a one-line skeleton as fragment. The calls are registered in `Open`, before the view is returned. The key `project:<project id>` opens it for one project (its own tab, titled `Statistics: <name>`) and survives a restart, as an input does not. The key `session:<session id>` opens it for one session and its sub-agents (its own tab, titled `Statistics: <title of the session>`, cut to a line; the start of the identifier while the session is not read) |
| Button | `PluginUi.Button(TitleBar, "statistics", "chart-column", "Statistics")` with `Canvas = "statistics"`. Its state is a ring (`PluginButtonBadge.Busy`) while `State` is `reading`, a dot while it is `needsChoice`, nothing otherwise, with a tooltip for each. `InvalidateButtons()` is called when that kind changes, not at each step of the reading. A second button, `ProjectMenu`, runs the command `statistics-project` for the project of the row, and a third, `SessionMenu` (**Statistics of this session**), runs `statistics-session` for the session of the row: the host gives a line of a menu the row that was clicked, whatever is selected |
| Commands | `statistics` (palette name `/statistics`, binding `Ctrl+G` then `C`), and `statistics-project` and `statistics-session` (`StatisticsPlugin.SessionScope.cs`), which are not in the palette: they are what the line of a project menu, the line of a session menu and the button of the card of a turn run. Each opens the canvas with the key of the project or of the session of its context, and says so when it has none: never the canvas of everything |
| Card of a turn | The details of the card (`PluginDerivedSessionEventDetailSection`) keep their Markdown, which CodeAlta TUI shows and Copy takes, and have an HTML form for CodeAlta Desktop (`LinkedDetailsHtml`): the same Markdown in an `alta-markdown` block, which the window draws, then a **Session statistics** button (`PluginHtml.CommandButton`) that names `statistics-session`. The fragment has no script. The window runs the command for the session whose timeline shows the card (`PluginPaneContext`, `doc/plugins.md`) and closes the details. Details longer than 12,000 characters stay Markdown, so a fragment is never cut before its button |
| Module | `src/statistics/canvas.tsx` is the entry listed in `lent/appModules.ts`. It reads `statistics.context`, makes a `StatisticsApi` over `alta.rpc` (`rpcApi.ts`), maps `alta.context` to a `StatisticsContext` (`canvasContext.ts`: the space of the tab, nothing for the space that holds every project, the project of the key or the input, the session of the key) and mounts `StatisticsCanvas`. `statistics.css` is part of the page's own stylesheet: a module brings none |

### The calls

Each call is a record the script sends (`StatisticsCall`: `request`, `metric`, `group`, `kind`, `by`, `sort`, `measure`, `subject`, `list`, `id`, `withChildren`, `days`) and answers with the JSON `StatisticsJson` writes, exactly as `alta statistics` does: the result of a question is `StatisticsJson.Serialize` parsed once, so the golden file of the tests is the contract of the wire too. A name is lowercase, as the transport requires.

| Calls | Input | Answer |
| --- | --- | --- |
| `statistics.summary`, `.tools`, `.models`, `.projects`, `.calendar`, `.week-hour`, `.records`, `.health` | `{ request }` | the result of the question of that name |
| `statistics.series` | `{ request, metric, group }` | `SeriesResult` |
| `statistics.top` | `{ request, kind, by }` | `TopResult` |
| `statistics.sessions`, `.runs` | `{ request, sort }` | `SessionsResult`, `RunsResult` |
| `statistics.session` | `{ id, withChildren }` | `SessionDetailResult`; `not_found` when no session matches |
| `statistics.distribution` | `{ request, measure, subject }` | `DistributionResult` |
| `statistics.details` | `{ request, list }` | `DetailsResult` |
| `statistics.status` | | the `StatisticsStatus`; while the first choice waits it carries the numbers of the card (below) |
| `statistics.choose-history` | `{ kind: "all" \| "fromToday" \| "days", days }` | the status after the choice |
| `statistics.pause`, `.resume`, `.stop-here`, `.reset` | | the status after the call |
| `statistics.forget-deleted` | | `{ count }` |
| `statistics.context` | | `{ weekStart, spaces: [{ id, name, isDefault, projectIds }], projects: [{ id, name }], providers: [{ key, name }] }`: the first day of the week of the questions (`Monday`), the spaces and projects read through `alta space list` and `alta project list` (the default space has every project), and the providers with the name the host shows each under, read through `alta provider list --detailed` (the key when it has no name). The module asks for it when the tab is first shown, not while it is hidden |

A question waits for the tables to exist (`InitializeAsync`), then for one of four places (`MaximumConcurrentQuestions`, shared by all the canvases), so a page that asks twelve things at once does not hold twelve readers of the database. They run while the history is read: reading uses its own connection and the database is in write-ahead mode. A result of more than 3 MiB (`StatisticsCanvasRpc.MaximumResultBytes`, under the 4 MiB the transport carries) is refused with `result_too_large`: choose a shorter period or add a filter; the queries cap their own rows (`StatisticsQueries.MaxLimit`) well below that.

Errors have stable codes and a sentence made to be shown: `invalid_request` (a period, a metric, a sort, a space that does not exist, a request that is not an object, a missing argument), `not_found` (a session), `unavailable` (retryable: the plugin is not running) and `result_too_large`. Any other failure is not translated: the host reports `internal_error` and the text stays in the log of the plugin. Cancellation reaches the query.

### The events

One event, `statistics.events`, carries `{ kind: "status", status }` and `{ kind: "data", change: { revision, fromDay, toDay, sessionIds } }`. `StatisticsEventPump` (one per open canvas) gathers what the engine says in a burst for a quarter of a second and sends the last status and the union of the days that changed. It sends nothing while the tab is hidden: the latest goes when the tab is shown. An event is not kept, so the module reads the status with a call after it listens, and again when the connection to the plugin is made again (`alta.rpc.generation`), together with a change that says every day is stale.

### The first-time card

While the choice waits, the engine lists the journals (no journal is opened) and publishes, in `sessionsTotal`, `bytesTotal` and `oldestDateReached` (the day of the oldest journal), what the card says: "906 sessions since 20 April 2026 can be read". The listing is made once the engine is ready, kept, and read again by `statistics.status` when it is more than half a minute old. It is dropped when the choice is made. `ResetAsync` (the menu of the canvas, `statistics.reset`) cancels the reading, empties every table of the plugin and comes back to this state, with a data change that says every day changed.

## `alta statistics`

The commands are the same questions, for agents and for the user. Each writes **one JSONL record** (`type`, `version`, `correlationId`, then the result) and works without a window, on any host that has the database. `0` is success, `2` a usage error (a period, a metric or a filter that is not valid), `1` another failure; an error is an `alta.error` record on stderr.

| Command | Record |
| --- | --- |
| `summary [--period] [--by] [--compare previous\|year] [--project] [--space] [--session [--with-children]] [--provider] [--model] [--effort] [--origin] [--tool-kind] [--week-start]` | `alta.statistics.summary` |
| `series <metric> [--group] [--period] [--by] [--limit] [filters]` | `alta.statistics.series` |
| `top <tools\|models\|projects\|sessions> [--by tokens\|time\|calls] [--limit] [--period] [filters]` | `alta.statistics.top` |
| `details <list> [--limit] [--period] [--project] [--space]` | `alta.statistics.details` |
| `session <session> [--with-children]` | `alta.statistics.session` |
| `status` | `alta.statistics.status`: the `StatisticsStatus` and `running`, whether this application reads the sessions. A process that reads nothing reads it from the store |
| `history read (--days N \| --all \| --from-today)`, `history pause`, `history resume`, `history stop`, `history forget-deleted` | `alta.statistics.history` (the status after the action, and `action`), `alta.statistics.forgotten` (`sessions`). Only where the engine runs (CodeAlta Desktop): elsewhere `statistics.notRunning` |
| `estimate <text>` | `alta.statistics.estimate`, as before |

A frequency (`--by`), a comparison and a first day of the week (`--week-start`) are names (`week`, `previous`, `monday`): a number, or several names, is a usage error. The error record is `StatisticsCommandError` (`type`, `version`, `correlationId`, `code`, `exitCode`, `message`), written with a source-generated serializer as every result is. Where the engine runs, a command asked before the engine prepared its tables (it waits a few seconds after the start of the application) prepares them first, as a question of the canvas does, so `status` never answers `starting` for a state that is known.

`--session <id>` (and `--with-children`) is the session filter of the questions, on `summary`, `series`, `top` and `details`: the numbers of one session, and of its sub-agents, over a period, where `session <session>` gives its totals over its whole life. `--session` without an identifier and `--with-children` without `--session` are usage errors.

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
- The spaces of a project are the ones it has today. A space has the chats too (a chat belongs to no project, and the Explorer shows it in every space); a project filter leaves them out.
- The origin of a prompt filters prompts and runs; time and tokens of a quarter hour have no sender, and a result says so in `query.ignoredFilters`.
- The sub-agents of a session are the sessions whose records name it as their parent. A session the statistics have not read yet (a history that starts later, a session created a second ago) is not in the tree until it is read.
- A run that is still going when the application is closed is closed as interrupted by the next start once its session has been quiet for an hour. So is a run that waits more than an hour for the user, until it goes on: it is then opened again.
- The statistics of the developer instance are its own (`<state root>/data/alta.sqlite3`, its own sessions).
