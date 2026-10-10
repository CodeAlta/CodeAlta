# Statistics: the journal reader and the facts of a session

This page documents the foundation of the Statistics history in `src/CodeAlta.Plugin.Statistics`: a streaming reader of session journals and a reducer that turns the records of a session into additive facts, plus the host catalog that lists the journals. It stores nothing and runs no job: the store, the job, the `alta statistics` commands and the canvas build on it.

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

Per file, a scan reports: the offset it started at and the one to resume from, the length of the file, the lines handled, the records parsed and passed over (with their bytes), the oversize records, the malformed and unknown lines, whether the end was reached, and the **fingerprint of the first line** (its length and a hash of its first 4 KiB). A cursor that remembers the fingerprint detects the two rewrites of a journal (a header added to an old file; the file replaced when the provider changes while the session is idle) and a file that became shorter than the offset: the scan then reads nothing and says `RewriteDetected`, and `SessionCatchUp` starts again from 0 and tells the store that the batch replaces the facts of the session (`CatchUpResult.Restarted`).

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
| `Details` | quarter, list, name | counts of shell programs, `alta` commands, extensions of changed files, permission modes, compaction triggers, run origins, skills, session origins |
| `Histograms` | quarter, measure, subject, step | counts per fixed step |
| `Extremes` | quarter, measure, subject | the largest value and the session, run and time it comes from |
| `Runs` | run | one row per run that changed: start, end, outcome, sender and kind of its first prompt, its counts, its provider, model, effort and permission mode at the start; replaced each time the run changes |
| `Session` | session | the identity (project, kind, parent, creator, automation, title, working directory, provider), the permission mode, the first and last record |

The quarter hour is `QuarterHour.Of(time)`: whole 15-minute steps since 1970 in UTC, so a local day is an exact sum of quarters in every time zone. Histogram steps are fixed (`HistogramSteps`): about 19% wider each, from a millisecond to several days (109 steps; the 99th reaches a day), computed with integers so that a stored step means the same range on every machine. Averages and rates are never stored; sums and counts are.

### Definitions

| Metric | Definition |
| --- | --- |
| Run | From the first record of the file that carries its `runId` (any record, parsed or not) to its `Idle` or `error`. The start is the first record **in the order of the file**, not the earliest timestamp, so that reading in halves is exact; the two differ by milliseconds |
| Active time | The time of the run, accounted record by record from the latest time seen, cut at quarter-hour limits, and given to the provider, model and effort in force **before** the record. Sub-millisecond remainders are carried in the state. It includes the time a run waits for the user |
| Counted where | A run is counted in the quarter where it starts, its outcome in the quarter where it ends, its duration and tool-call distributions in the quarter where it starts |
| Interrupted | A run that has no end when another run starts, ended at its last record. On the author's profile 48 runs, and no record of a run ever comes after the next one has started. A run that is still open at the end of a read stays open (`Outcome = Running`): the caller closes the open runs of a session it knows is dead with `InterruptOpenRuns()` |
| Requests | The `UsageUpdated` records that carry tokens; never `Idle`, `CompactionCompleted` or the snapshots. Requests with `initiator: compaction` are kept in their own purpose and are not requests of the run |
| Input tokens | `AgentInputTokenUsage.From`, the one rule of the application: total, fresh, cache read and cache write. Output includes reasoning, which is also counted on its own |
| Model | The model the provider reports (`lastOperation.model`), not the alias chosen; the alias is mapped to it from the first request that names it, so that the time of a session that chose `opus` is given to `claude-opus-5-5`. Until that request, the alias holds the time. The provider is the `backendId` of the record, with `codex_cli` folded into `codex` and `copilot_cli` into `copilot` |
| Cost | The cost of a request, by unit (`usd` when the provider names none, `AI credits`). A provider that repeats the cost and the duration of a turn (Claude Code) repeats the same pair on later records of the run: the same pair twice in a run counts once |
| Tool call | Counted when it starts (an end without a start counts too), measured when it ends: duration, failure, bytes of the result, files, lines. The bucket is the rule of the cards of the timeline (`shell`, or `<ActivityKind>:<name>`); its kind is one of the fixed list files, search, shell, web, `alta`, MCP, skill, other |
| Lines added and removed | Lines of the diff of a completed call that start with `+` or `-`, the `+++` and `---` headers apart (the rule of the terminal) |
| Prompt | A user content. Its sender is who the provenance names (`submitted_by.Kind`: user, agent, reminder, automation, other for MCP, plugin, job, host), or an agent when the content carries `source_session_id`. Its kind is an answer when it carries `ask_id`, a steer when it comes into a run that already had a prompt or its provenance says `steer`, queued when its provenance says so, a new turn otherwise |
| Provenance written later | The state record that describes a prompt is written before or after it (a reminder, an automation or an MCP send usually after). The reducer remembers the last eight prompts and the entries that wait for one, matches by run or by time (within 5 s), and when an entry changes what a prompt was counted for it **corrects** it with negative additions. 1,358 of 2,955 entries did on the author's profile |
| Words | Runs of characters that are not ASCII white space; characters are UTF-16 code units, as `string.Length` |

The prompts counted "of yours" (sizes, words) are the prompts whose sender is you. The histograms of a prompt that is later corrected are corrected with it. The row of a run that already ended is not updated by a correction that comes later (its facts are).

### The state, and the cursor

`SessionFactsState` is what a catch-up carries to the next: the model, effort, agent prompt and permission mode in force, the runs that are open (with the time accounted), the last closed run identifiers (so that a late record does not open a run again), the tool calls that have not ended, the last eight prompts, the provenance entries that wait for a prompt, the hashes of the prompts already taken, and the identity of the session. It serializes to JSON (`ToUtf8Json`, `FromUtf8Json`; the largest on the author's profile is 28 KB) and has a `Version`: a state of another version is not used, and the session is read again. `JournalCursor` is the offset, the fingerprint of the first line and the state: the store saves it **in the same transaction** as the batch, and hands it back to `SessionCatchUp.CatchUp`, which works on a copy of the state (a catch-up that is not saved leaves the saved state as it was).

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

`RealProfileHarness` (the reader), `RealProfileFactsHarness` (totals and diagnostics of the reducer), `RealProfileCrossCheck` (the facts against a second, naive reading with a JSON DOM; requests, every kind of token, prompts, answers, reasonings, errors, compactions and instructions must be equal) `RealProfileSplitHarness` (recorded journals cut at record boundaries: 32 sessions, 2,009 cuts, all equal), `RealProfileCoverageHarness` (parsed records whose fields came out empty, to find shapes the parser does not read) and `RealSessionSummaryHarness`.

## Limits

- The facts count what is in the journals. What a session does not record (the time a run waits for the user, retries, the application, a cost for most providers) cannot be counted.
- A run is only known to be interrupted when the next one starts, or when the caller says the session is dead.
- The kind of an MCP tool that CodeAlta exposes under its bare name (`take_snapshot`) is `other`; only `mcp__<server>__<tool>` names are MCP.
