# Statistics: the journal reader and the facts of a session

This page documents the foundation of the Statistics history in `src/CodeAlta.Plugin.Statistics`: a streaming reader of session journals and a reducer that turns the records of a session into additive facts, plus the host catalog that lists the journals. It stores nothing and runs no job: the store, the history job, the flow and the `alta statistics` commands, described in `doc/statistics.md`, build on it.

| Piece | Where | Role |
| --- | --- | --- |
| `ISessionJournalCatalog` | `CodeAlta.Agent` (`CodeAltaHost.SessionJournals`) | Lists **every** journal of the session store, and opens one for reading from an offset |
| `JournalScanner` | `CodeAlta.Plugin.Statistics/Journal` | Reads a journal from an offset to its last complete line; hands the records statistics need to a sink |
| `SessionFactsReducer` | `CodeAlta.Plugin.Statistics/Facts` | Counts what happened in the records it is given as additions to the facts; keeps a small state between calls |
| `SessionCatchUp` | `CodeAlta.Plugin.Statistics/Facts` | The one operation of the history and of the flow: scanner + reducer, from a cursor to a new cursor |

## The catalog of journals

`ISessionJournalCatalog` is in `CodeAlta.Agent` because both the file-system store and the plugin abstractions reach it, and the Statistics project needs nothing of the orchestration. `FileSystemSessionJournalCatalog` lists `sessions/yyyy/MM/dd/<session id>.jsonl` under the sessions folder of the store (the `traces` folder apart), most recently changed first. It is **not** the list the Explorer shows: a session of a project that was removed is listed too, because the catalog knows nothing of projects.

`OpenAsync(sessionId, offset)` opens the file with `FileAccess.Read` and `FileShare.ReadWrite | FileShare.Delete`, without any buffer of its own, and takes no in-process lock of the session. The session writes the file with `FileShare.Read` and replaces it by writing a temporary file, so a reader that asks for these flags never blocks the session and never keeps a file from being replaced or deleted. It returns `null` when the journal is gone. `CodeAltaHost.SessionJournals` is the instance of the host; `SessionViewJournalStore.CreateJournalCatalog()` builds one over the sessions folder of any catalog options (the developer instance reads `~/.alta/dev/sessions`, the normal one `~/.alta/sessions`).

## The reader

### Full-source verification of a returning journal

The ordinary incremental reader below is intentionally tolerant. Retention never reads a journal: it saves the facts the tables already hold for a session read to the end of its current file (see [Statistics](statistics.md#durable-saved-statistics)). The strict scan, `JournalScanOptions.RetirementSessionId`, is used only when a journal whose facts were saved comes back and must replace them: SHA-256 over every source byte, strict JSON and typed event/header/state validation, matching session identity, supported discriminators and the serializer's envelope ordering. A record above the bounded 4 MiB parse limit is uncertain and keeps the saved facts, even when the normal reader could skip it. EOF with pending bytes, a malformed/unknown record, an open run, cancellation or a source change also keeps them.

`StatisticsArchiveSource` (Catalog) resolves the catalog path of that returning journal, rejects linked ancestors and hashes the held source again and the current pathname. Its streams use `FileShare.ReadWrite | FileShare.Delete`, so they never block a writer or a deletion. The dedicated archive described in [Statistics](statistics.md#durable-saved-statistics) stores the complete base facts, never this incremental checkpoint. Facts version is **6**; archive version **1** and application projection schema **4** are separate contracts.

A journal is UTF-8, one JSON object per line, with no byte order mark. Every record is written with its discriminators first (`$type`, then `kind`, `phase`, or `backendEventType` for a raw record) and its **envelope last** (`backendId`, `sessionId`, `timestamp`, `runId`). The reader depends on both facts:

| Step | What |
| --- | --- |
| Classify | The first 160 bytes of a line give its kind (`JournalPrefixClassifier`). A line that does not start the way the serializer writes it is counted as unknown and skipped |
| Pass over | State snapshots (`local.sessionState`, `local.sessionSummary`), tool outputs, copies of messages (`local.*Message`), `DiffUpdated`, deltas, `Idle` and `error` are not parsed and not kept: the bytes are passed over in chunks, and only the last 512 bytes of the line are kept, to read the envelope |
| Read the envelope | `timestamp`, `runId` and `backendId` come from those 512 bytes, for **every** record, parsed or not. The time of a record is therefore always known, and so is the run it belongs to |
| Parse | For the other kinds (header, state, tool call start and end, prompt, answer and reasoning, usage, model change, compaction, system prompt) a forward-only `Utf8JsonReader` reads the payload and stops where the envelope begins. Only the fields statistics use are kept |
| Bound | A record above the bound (4 MiB by default, `JournalScanOptions.MaxParsedRecordBytes`) is not buffered whole: its first 4 MiB are parsed as far as they go (a tool call keeps its arguments, its files and the start of its diff), its last 512 bytes give the envelope, and the size of the line stands for the size of its text or of its result |
| Stop | The scan ends at the last complete line (`\n`); a half-written last line is left for the next scan, which resumes from `JournalScanResult.EndOffset`. A scan can also stop after a number of bytes (`maxBytes`) or when its token is canceled, always at a line end |

State records repeat the whole list of their prompt provenance each time (up to 1.6 MB). The reader does not walk it token by token: it looks for the entries (`{"prompt_id":"`), hashes the identifier of each, and parses only the entries the sink has not seen (`IJournalRecordSink.IsPromptSeen`).

Per file, a scan reports: the offset it started at and the one to resume from, the length of the file, the lines handled, the records parsed and passed over (with their bytes), the oversize records, the malformed and unknown lines, whether the end was reached, and the **fingerprint of the first line** (its length and a hash of its first 4 KiB). A cursor that remembers the fingerprint detects the rewrites of a journal (a header added to an old file; the file replaced when the provider changes while the session is idle; a session that is rewound, whose header is written again with the next `rewind_count`, so that a journal that grows past the saved offset afterwards is still not taken for the file that was read) and a file that became shorter than the offset: the scan then reads nothing and says `RewriteDetected`, and `SessionCatchUp` starts again from 0 and tells the store that the batch replaces the facts of the session (`CatchUpResult.Restarted`).

The reader tolerates the legacy `codealta.threadHeader` and `codealta.threadState`, `Turn`, `Started` and `Shutdown` activities, malformed lines, a byte order mark, CRLF line ends, timestamps with 0 to 7 fraction digits, and timestamps that go backwards (277,914 records of the author's profile are stamped earlier than the line before them): the file order is the order of the facts, the timestamps are only read.

### Measured on the author's profile

911 files, 10.4 GiB, 1.64 million lines, 42 lines above the bound, none malformed, none unknown; files in the cache of the system, Windows 11, NVMe; four runs of the test harness (the machine was also building other projects):

| | Reader alone | Reader and reducer |
| --- | --- | --- |
| Time | 4.2 to 7.3 s | 4.4 to 6.9 s |
| Throughput | 1.4 to 2.5 GiB/s | |
| Peak working set of the test process | 92 MiB | 101 to 109 MiB |
| Managed allocation for a line of 64 MiB that is passed over | under 8 MiB (checked by a test) | |

A cold disk was not measured. Lines per kind (records read): headers 709, states 15,584, tool call phases 290,142, prompts 5,637, answers, reasonings and summaries 103,060, requests 131,896, model changes 3,862, run ends 4,600, compactions 878, instructions 1,018; 1,084,503 records are only touched.

## The facts

A batch (`FactBatch`) holds the additions one catch-up makes, in one dictionary per family. Keys have no session (a batch is one session's); the store adds it. Every measure is a sum, except a few that are marked and merge with the larger value. `FactBatch.Merge` is a plain addition, so reading a session in two halves gives the same facts as reading it at once (`SplitInvarianceTests` cut synthetic journals at every record boundary, and the harness does the same on recorded ones).

| Family | Key | Measures |
| --- | --- | --- |
| `Activity` | quarter hour (UTC), provider, model, effort | active ms; runs started, completed, failed, interrupted; errors; compactions with tokens before and after |
| `Usage` | quarter, provider, model, effort, agent prompt, purpose (turn or compaction) | requests; input in total, fresh, cache read, cache write; output; reasoning; provider duration; context samples with the sum of tokens, of limits and of fill (ppm) and the largest fill |
| `Cost` | quarter, provider, model, unit (`usd` or `AI credits`) | total and number of costs |
| `Tools` | quarter, provider, kind, tool bucket | calls, failures, canceled; duration count, total and longest; bytes in and out; files read and changed; lines added and removed |
| `Content` | quarter, kind (prompt, answer, reasoning, reasoning summary, instructions), sender, prompt kind | count, characters, words, approximate tokens (instructions), attachments by type |
| `Details` | quarter, list, name | counts of shell programs, `alta` commands, extensions of changed files, permission modes, compaction triggers, run origins, session origins, prompt provenances, skills (activated by an agent, attached to a prompt, read as `SKILL.md`) and MCP tools |
| `Histograms` | quarter, measure, subject, step | counts per fixed step |
| `Extremes` | quarter, measure, subject | the largest value and the session, run and time it comes from |
| `Runs` | run | one row per run that changed: start, end, outcome, sender and kind of its first prompt, its counts, its provider, model, effort and permission mode at the start; replaced each time the run changes |
| `Session` | session | the identity (project, kind, parent, creator, automation, title, provider), the permission mode, the first and last record. The working directory of the header is not read: no path of a session is kept, in the facts or in the state |

The quarter hour is `QuarterHour.Of(time)`: whole 15-minute steps since 1970 in UTC, so a local day is an exact sum of quarters in every time zone. Histogram steps are fixed (`HistogramSteps`): about 19% wider each, from a millisecond to several days (109 steps; the 99th reaches a day), computed with integers so that a stored step means the same range on every machine. Averages and rates are never stored; sums and counts are.

### Definitions

| Metric | Definition |
| --- | --- |
| Run | From the first record of the file that carries its `runId` (any record, parsed or not) to its `Idle` or `error`. The start is the first record **in the order of the file**, not the earliest timestamp, so that reading in halves is exact; the two differ by milliseconds |
| Active time | The time of the run, accounted record by record from the latest time seen, cut at quarter-hour limits, and given to the provider, model and effort in force **before** the record. Sub-millisecond remainders are carried in the state. It includes the time a run waits for the user. More than seven days between two records of a run (`SessionFactsReducer.MaxRunGap`) is not time of the run: it is the time of a damaged line or of a clock that was wrong, and nothing is added for it. The run remembers how much was left out (`OpenRunState.SkippedTicks`, `RunRow.SkippedMs`): its duration, in the distribution of the durations, in the longest run and in the list of the runs, is its end minus its start minus that, while the row keeps the first and the last time as the journal gives them |
| Counted where | A run is counted in the quarter where it starts, its outcome in the quarter where it ends, its duration and tool-call distributions in the quarter where it starts |
| Interrupted | A run that has no end when another run starts, ended at its last record. On the author's profile 48 runs, and no record of a run ever comes after the next one has started. A run that is still open at the end of a read stays open (`Outcome = Running`): the caller closes the open runs of a session it takes for dead with `InterruptOpenRuns()`. The caller can only guess (a run that waits for an answer of the user writes nothing either), so those runs are remembered as they were (`SettledRuns`, at most four, until another run starts): a later record of one **opens it again**, with negative additions for the interruption and for its place in the distributions of the runs. A largest value it gave stays, and its end can only give a larger one. The time between the last record before the quiet and the record that opens the run again is counted as time of the run (up to the seven days above): it is a run that waited, for a permission, an answer or a long command, in an application that stayed open. A run that died with its process is never opened again that way: the identifier of a run is made when a prompt is sent (`AgentSession`, a new GUID) and lives in memory only, so nothing the application writes after a restart carries the identifier of an earlier run |
| Requests | The `UsageUpdated` records that carry tokens; never `Idle`, `CompactionCompleted` or the snapshots. Requests with `initiator: compaction` are kept in their own purpose and are not requests of the run |
| Input tokens | `AgentInputTokenUsage.From`, the one rule of the application: total, fresh, cache read and cache write. Output includes reasoning, which is also counted on its own |
| Model | The model the provider reports (`lastOperation.model`), not the alias chosen; the alias is mapped to it from the first request that names it, so that the time of a session that chose `opus` is given to `claude-opus-5-5`. Until that request, the alias holds the time. The provider is the `backendId` of the record, with `codex_cli` folded into `codex` and `copilot_cli` into `copilot` |
| Cost | The cost of a request, by unit (`usd` when the provider names none, `AI credits`). A provider that repeats the cost and the duration of a turn (Claude Code) repeats the same pair on later records of the run: the same pair twice in a run counts once. A cost without a duration is the cost of its request (the credits of Copilot) and always counts: two requests may cost the same |
| Tool call | Counted when it starts (an end without a start counts too), measured when it ends: duration, failure, bytes of the result, files, lines. The bucket is the rule of the cards of the timeline (`shell`, or `<ActivityKind>:<name>`); its kind is one of the fixed list files, search, shell, web, `alta`, MCP, skill, other |
| Use of a skill | An **activation** by an agent, counted when the call ends without failing (`skill`): `alta skill activate <name>` (and its aliases `skills activate`, `skills_activate`) under any name of the `alta` tool, the `Skill` tool of Claude Code, the old `codealta_skills_activate`. The name seen when the call starts is kept in the state until it ends. A skill **attached** to a prompt, once per prompt (`skill-attached`). A `SKILL.md` among the files a completed call read, named by its folder (`skill-read`). The UI's own activity record of an attachment is not counted again. Each is named `<source>/<name>` (`SkillSources`): the `source_kind` of the payload in the result of an activation (its quotes are escaped once or twice in the journal), `claude-code` for the `Skill` tool, the folder of the path of an attachment or of a read; the name alone when nothing says |
| Call of an MCP tool | `server/tool`, counted with the call (`mcp-tool`): the parts of an `mcp__<server>__<tool>` name, as the name writes them (the MCP plugin turns a character other than a letter or a digit of a key into `_`), or the `--server` and `--tool` of `alta mcp tool call`, as given |
| Lines added and removed | Lines of the diff of a completed call that start with `+` or `-`, the `+++` and `---` headers apart (the rule of the terminal) |
| Prompt | A user content. Its sender is what the provenance recorded with it says (`provenance` of the user message: `agent-session` an agent, `reminder`, `automation`, `user-answer` and `user-review` you, any other other), else who the provenance entry of the state names (`submitted_by.Kind`: user, agent, reminder, automation, other for MCP, plugin, job, host), or an agent when the content carries `source_session_id`. A recorded provenance is also counted under its own name (`prompt-provenance`), a new identifier included; one that is not lowercase words separated by hyphens is not. Its kind is an answer when it carries `ask_id`, a steer when it comes into a run that already had a prompt or its provenance says `steer`, queued when its provenance says so, a new turn otherwise |
| Provenance written later | The state record that describes a prompt is written before or after it (a reminder, an automation or an MCP send usually after). The reducer remembers the last eight prompts and the entries that wait for one, matches by run or by time (within 5 s), and when an entry changes what a prompt was counted for it **corrects** it with negative additions. 1,358 of 2,955 entries did on the author's profile |
| Words | Runs of characters that are not ASCII white space; characters are UTF-16 code units, as `string.Length` |

The prompts counted "of yours" (sizes, words) are the prompts whose sender is you. The histograms of a prompt that is later corrected are corrected with it. The row of a run that already ended is not updated by a correction that comes later (its facts are).

### Forked and rewound sessions

A session that starts with the turns of another one holds a **copy** of their records, under its own identifier and with their times and their runs: a fork (the turns before a point), and the session that keeps what a rewind removed (every turn of the session before the rewind). Those records were counted in the session they were written in. The header of the new session says from when its records are its own, `forked_from.counted_from` (`HeaderRecord.CountedFrom`): the creation of a fork, or the start of the first removed turn for the session that keeps a rewind.

| Record of a session whose header has `counted_from` | What the reducer does |
| --- | --- |
| Older than `counted_from` (**inherited**) | No fact, no run, no row, and not a record of the session: its first and last record are those of its own records. Only the choices in force are followed through it, so that the records after it count under the names they had in the source: the provider (`backendId`), a `ModelChanged` (model, effort, agent prompt), the model a request reports for an alias, the agent prompt of a `system_prompt` |
| At or after `counted_from` (**its own**) | Counted as in any session |
| The header | Always its own: the header of the source is not copied. The fork is one session more (`SessionOrigin`) |

A run is not cut in two by `counted_from`: it belongs whole to the part its **first record in the order of the file** belongs to, because the time of a record may be earlier than the one of the record before it. The last 32 runs of the source are remembered for that (`InheritedRecordsState.Runs`): a late record of one of them opens no run in the fork, and a record of a run of the fork that is stamped before `counted_from` is counted.

So the totals of a session and of its forks are what was spent: nothing twice, nothing lost (`SessionForkFactsTests`, and `SessionForkRewindIntegrationTests` with the journals the runtime writes). A session that is **rewound** keeps its identifier: its journal is replaced, its first line changes, and it is read again from its start (see the fingerprint above), which removes the facts of the turns it lost; they are in the session that keeps them, or nowhere when the rewind discarded them. A header without `forked_from`, which is every journal written before forks existed, changes nothing: no record is inherited.

### Turns made outside CodeAlta

A session of a provider that shares its conversation with other programs (Claude Code, continued in VS Code or in a terminal) can have turns that were made there and added to its journal afterwards, by a catch-up or by an import (see "Turns made outside CodeAlta" in [runtime](runtime.md)). Each is written under a run whose id starts with `external:` (`AgentSessionTurn.ExternalRunPrefix`), with an `Idle` that carries no usage. The reducer skips every record of such a run: no run, prompt, tool call, usage or cost is counted, since none of it was work of CodeAlta (`SessionFactsReducerTests.TurnMadeOutsideCodeAlta_IsNotCounted`, `SessionImportedFromClaudeCode_CountsNoRunOfCodeAlta`). A compaction made there is a notice without a run, and still counts as a compaction.

### The state, and the cursor

`SessionFactsState` is what a catch-up carries to the next: the model, effort, agent prompt and permission mode in force, the runs that are open (with the time accounted), the last closed run identifiers (so that a late record does not open a run again), the runs that were closed for a session that went quiet (so that one that goes on is opened again), the tool calls that have not ended, the last eight prompts, the provenance entries that wait for a prompt, the hashes of the prompts already taken, the identity of the session, and, for a fork only, `counted_from` with the last runs of its source (`Inherited`, absent from the state of any other session). It serializes to JSON (`ToUtf8Json`, `FromUtf8Json`; the largest on the author's profile is 28 KB) and has a `Version` (`SessionFactsState.CurrentVersion`, 7): a state of another version is not used, and the session is read again. Version 2 is the first that never keeps a variable of a shell command as its program, a word of an `alta` command that is not a command, or the working directory in the state; it also counts every cost that has no duration and adds no time for a gap of more than seven days. Version 6 names every use of a skill and the MCP tool of every MCP call, and counts an activation when it ends. Version 7 takes the sender of a prompt from the provenance recorded with it, and counts the prompts by that provenance. `JournalCursor` is the offset, the fingerprint of the first line and the state: the store saves it **in the same transaction** as the batch, and hands it back to `SessionCatchUp.CatchUp`, which works on a copy of the state (a catch-up that is not saved leaves the saved state as it was).

## Differences with `alta session metrics` and with the cards

The new facts follow the specification, not the older numbers. Measured on sessions of three providers of the author's profile (`--scope session`), the counts the commands share are equal: the runs, the prompts and answers, the tool calls, the requests, the input, output, cached and reasoning tokens, the cache writes and the cost.

| | `alta session metrics` | Facts |
| --- | --- | --- |
| Duration | First prompt to final answer of the session, idle time between turns included (12,405 s for a session whose runs take 360 s) | The time of the runs (first record to `Idle`), cut at quarter hours |
| Tool calls | Count the `Requested` records | Count the `Started` records (the same number in the journals read) and add the ends that have no start |
| Operations | Drops an operation equal in every value to the one counted before | Counts every `UsageUpdated`. 336 of 131,994 requests (0.25%) are exact repeats of the one before |
| Scope | The last turn or the session | Any period and any filter |
| Cost | The sum of the operations | The same, per unit; a repeated turn cost counts once |

The cards of the timeline (`StatisticsPlugin`) are unchanged; they share the rule of the tool bucket (`StatisticsToolBuckets.Bucket`) with the facts.

## Running the harness on a real profile

The tests of `CodeAlta.Plugin.Statistics.Tests` that read a real profile are skipped unless an environment variable names the folder. They only read, and print counts, sizes and timings, never a text of a session:

```powershell
$env:CODEALTA_STATS_SESSIONS = "$HOME\.alta\sessions"
dotnet test src -c Release --filter "FullyQualifiedName~RealProfile" --logger "console;verbosity=detailed"
$env:CODEALTA_STATS_SESSION_IDS = "<id>,<id>"   # RealSessionSummaryHarness: the totals of some sessions
```

`RealProfileHarness` (the reader), `RealProfileFactsHarness` (totals and diagnostics of the reducer), `RealProfileCrossCheck` (the facts against a second, naive reading with a JSON DOM; requests, every kind of token, prompts, answers, reasonings, errors, compactions and instructions must be equal) `RealProfileSplitHarness` (recorded journals cut at record boundaries: 32 sessions, 2,009 cuts, all equal), `RealProfileCoverageHarness` (parsed records whose fields came out empty, to find shapes the parser does not read), `RealProfileRunsHarness` (what the runs say about the sessions at once: on the author's profile 4,792 runs of 941 sessions, none with records more than seven days apart, 21 longer than an hour and none longer than a day, at most 9 sessions with a run going at the same moment; 223 sessions have a parent, and every parent has its journal) and `RealSessionSummaryHarness`.

## Limits

- The facts count what is in the journals. What a session does not record (the time a run waits for the user, retries, the application, a cost for most providers) cannot be counted.
- The sessions at once are counted from the first and the last time of each run (`doc/statistics.md`, "Numbers computed from the rows"): a run that waits is going, and nothing in the store says when, inside a run, the session worked.
- A run is only known to be interrupted when the next one starts. When the caller says the session is dead, the run is counted as interrupted until one of its records says otherwise.
- The kind of an MCP tool that CodeAlta exposes under its bare name (`take_snapshot`) is `other`; only `mcp__<server>__<tool>` names are MCP.
- The session that keeps what a rewind removed holds the records of the removed turns, not the state records that were written with them (a fork copies no snapshot). Their runs have no permission mode, and their prompts no provenance: a prompt a reminder or an automation sent is counted as yours there, and a queued one as a new turn. The tokens, costs, requests, tool calls and times are exact.
- A record that carries no run is told apart by its time alone. One written before the first removed turn of a rewind and stamped after its start would be counted in both sessions; none is known.
- A session that was forked and read by a version of the statistics that did not know `counted_from` keeps the numbers of that reading, the inherited turns included, until it is read again from its start (a reset, a new version of the facts, a rewind).
