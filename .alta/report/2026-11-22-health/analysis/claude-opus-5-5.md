# Health analysis: claude-opus-5-5

Scope read: the signals and CI runs; `src/CodeAlta/frontend/src/main.tsx` (structure of `App`, its state and its fix commits), `demo-api.ts` and the `*.neoastra.mount.ts` fixtures, `src/CodeAlta/Desktop/Rpc/BootRpc.cs`, the commits that added one RPC service; `src/CodeAlta.Orchestration/Runtime/SessionRuntimeService*.cs` (fields, actor writes, fix subjects), `SessionToolGuards.cs`, `SessionRuntimeService.ToolHooks.cs` and the guard calls in `src/CodeAlta.LiveTool`; `src/CodeAlta.Catalog/SessionJournalSqliteCache.cs` and `ApplicationDatabase.MigrateAsync`; the Fleet budget and sign-in commits; the Mobile plugin, its contract and fixture server; `CodeModeRunRegistry.cs`, `CodeModeRunStoreTests.cs`, `ReviewServiceTests.cs`, the browser tests' sleeps; the plugin-runtime skill, `site/docs/plugins/developers.md`, `doc/site-screenshots.md`, and the paths cited in `doc/`, `AGENTS.md` and `readme.md`.
Not read: the providers (`CodeAlta.Agent.*`), the TUI beyond its size, `CodeAltaConfigStore.cs`, `style.css`, the server and node beyond the commits named, the user guide beyond the plugin page. No build or test was run.

## Findings

### Run the column migration of the session list on every open, from one list of columns
- **Lens / area / action:** complexity in a fix-dense file · `CodeAlta.Catalog` session projection · simplify
- **Cost:** three times in six days a column added to the session list broke listing sessions on every existing database ("no such column"), because its migration only runs when `SchemaVersion` changes and the version was not raised.
- **Evidence:** `src/CodeAlta.Catalog/ApplicationDatabase.cs:345` returns before the migration when the stored version equals the requested one; `src/CodeAlta.Catalog/SessionJournalSqliteCache.cs:15` (`SchemaVersion = 11`); nine hand-written `Add…ColumnAsync` methods called from `src/CodeAlta.Catalog/SessionJournalSqliteCache.cs:1599-1607`, each a `pragma_table_info` check then `ALTER TABLE` (`src/CodeAlta.Catalog/SessionJournalSqliteCache.cs:1618-1785`). Commits `77e0346bd` (2026-10-26, sandbox column, "listing or creating a session failed with no such column: c.local_sandbox"), `c3a5461df` (2026-10-27, options column), `8f15ae2f5` (2026-10-31, cloud column): each fix only raised the version. `src/CodeAlta.Catalog.Tests/SessionTableLayoutTests.cs:82` pins the version (`Assert.AreEqual(11, …)`) and was edited by each fix. The development guide makes every new session fact a column (`doc/development-guide.md:7`), so the next column meets the same trap.
- **Or else:** the three were one-off slips that a careful author avoids. Check: the next column added; or `git log -S"ADD COLUMN" -- src/CodeAlta.Catalog/SessionJournalSqliteCache.cs` shows how many column additions needed a later version fix.
- **Change:** declare the columns once (name, type, whether the rows must be read again from the journals) and, at each open, read `pragma_table_info` (a read, no write lock) and add only the missing ones; keep `SchemaVersion` for changes a column list cannot express (the move of large values, a dropped column).
- **Removes / adds:** removes nine near-identical methods (about 170 lines) and the rule "raise the version when you add a column"; adds one column table and one loop of about 30 lines.
- **Confidence:** verified
- **First step:** a regression test that creates the session tables without one declared column, at the current version, then lists sessions: it fails today. Then the loop over the declared list, the nine methods removed.
- **Verify:** that test passes; `dotnet test` of `CodeAlta.Catalog.Tests` passes; adding a column to the list without touching the version passes the test.
- **Risk:** the two methods that also clear the rows (`local_permission_mode`, `space_id`) must keep doing so; the check runs at each open and must stay a read when nothing is missing (`ApplicationDatabase.cs:337` says why).

### Move the state of dialogs and fields out of `App` into the components that own them
- **Lens / area / action:** boundaries · desktop frontend `main.tsx` · move
- **Cost:** `App` is one component of 6,741 lines (lines 550 to 7291) with 134 `useState`, 95 `useEffect`, 94 `useRef` and 48 `useMemo`; 144 commits changed `main.tsx` in four weeks (22.7 files per commit on average), 38 of them fixes, and four text fields re-render the whole window on each keystroke, against `doc/development-guide.md:31`.
- **Evidence:** `src/CodeAlta/frontend/src/main.tsx:550` (`function App`); the state of whole features held there: session rename (`main.tsx:1154-1160`), session delete (`main.tsx:1136-1145`), project rename (`main.tsx:1099-1108`), archive, move, project delete (`main.tsx:6199-6204`). Keystroke state in `App`: `onChange={setSidebarText}` (`main.tsx:6514`), `onChange={setProjectRenameName}` (`main.tsx:6533`), `onChange={setRenamingTitle}` (`main.tsx:6689`), `onQuery: setSettingsQuery` (`main.tsx:6980`); a minute timer in `App` (`main.tsx:1265`). Most fix commits change `main.tsx` by 1 to 15 lines (`git show --numstat` of the 38): it is the wiring point every feature passes through. Perf work on whole-window renders: #404, #469.
- **Or else:** `App` re-renders are cheap because children are memoized, and the commits only add wiring. Check: React profiler or a render counter on `App` while typing in the rename popover.
- **Change:** each dialog keeps its draft and busy state itself (`RenamePopover` holds the title it edits and calls back on submit; the delete, archive and move dialogs become components of their feature folders with their own hooks); `App` keeps what the window shares: the workspace, the tabs, the selection.
- **Removes / adds:** removes about 40 `useState` and their effects from `App`, and keystroke renders of the window; adds a hook or a component per feature, in the folders that already exist (`sessionArchive/`, `sessionChildren/`, …).
- **Confidence:** verified (counts, keystroke state); likely for the render cost (missing check: a render counter while typing)
- **First step:** `RenamePopover` keeps its own draft; `renamingTitle` leaves `App`. A browser test counts the renders of `App` while typing a title.
- **Verify:** that count is 0 per keystroke; `npm test`; the number of `useState` in `App`, measured as above, goes down step by step.
- **Risk:** state that two features read (selection, workspace snapshot) must stay shared; StrictMode double effects in moved hooks.

### Register a desktop RPC service once, instead of in five shared files
- **Lens / area / action:** duplication · desktop host and frontend fixtures · simplify
- **Cost:** a new RPC service edits its own file and five shared ones; `demo-api.ts` changed in 45 commits in four weeks, 38 of them shared with `BootRpc.cs` (109 commits), and a forgotten fixture export broke the browser test bundles once (`e66f2a618`).
- **Evidence:** commit `c708f677b` (the labels service): `src/CodeAlta/Desktop/DesktopApplication.cs:1391` and `src/CodeAlta/Desktop/DesktopApplication.cs:1903` (registered twice, with and without a host), `src/CodeAlta/Desktop/Rpc/BootRpc.cs` (1,021 `[JsonSerializable]` lines from line 151), `src/CodeAlta/frontend/src/demo-api.ts:281` (a stub of `unavailable` methods), the export list of `src/CodeAlta/frontend/src/projectFocus.neoastra.mount.ts:7`; commit `3e1ce7ef6` (network service) touches the same five plus `main.tsx`.
- **Or else:** each list is checked (the build fails on a missing type, the bundle on a missing export), so the cost is edits, not defects. Check: count the commits whose only change to these files is a registration line.
- **Change:** (1) the fixtures re-export `demo-api` with `export *` and override what they fake, instead of naming 60 services; (2) `demo-api` answers `unavailable` for any service it does not fake, through one helper, so a new service needs no stub; (3) each RPC file declares the JSON types of its own records (its own `JsonSerializerContext`, combined with the others when the host starts), so `BootRpc.cs` stops being a list of every type.
- **Removes / adds:** removes the 60-name export list, most stubs of `demo-api.ts` and the 1,021-line attribute list; adds one helper and one combine call.
- **Confidence:** likely (missing check: that NeoAstra accepts a combined resolver; that ES star re-exports resolve the fixtures as today)
- **First step:** replace the export list of `projectFocus.neoastra.mount.ts:7` by `export *` with local overrides; run the browser tests that use it.
- **Verify:** adding a service in a scratch branch touches only its own files and `DesktopApplication.cs`; `npm test`.
- **Risk:** a star re-export of the same name from two modules is excluded silently in ES modules: an ambiguous name must stay explicit.

### Give `CodeModeRunRegistry` the clock of its store, and the test a clock it controls
- **Lens / area / action:** flaky tests · Code Mode runs · fix
- **Cost:** `ARunCodeAltaClosesWhileItSleeps_IsInterrupted_AndResumesByItselfOnceItWakes_Once` failed on Ubuntu CI and passed on rerun (run 43323247389, 2026-11-12), one of the 10 runs of `main` started again in two weeks.
- **Evidence:** the test sets the wake time 500 ms ahead on the wall clock (`src/CodeAlta.Orchestration.Tests/CodeModeRunStoreTests.cs:219`) and asserts the run still reads "interrupted" with that wake time after a new registry loaded the store (`src/CodeAlta.Orchestration.Tests/CodeModeRunStoreTests.cs:232`, the assertion that failed). `CodeModeRunStore` takes a `TimeProvider` (`src/CodeAlta.Orchestration/Runtime/Scripts/CodeModeRunStore.cs:130`), but `CodeModeRunRegistry` computes the delay with `DateTimeOffset.UtcNow` (`src/CodeAlta.Orchestration/Runtime/Scripts/CodeModeRunRegistry.cs:319`) and stamps events with it (`CodeModeRunRegistry.cs:298`): the test cannot control time.
- **Or else:** the failure comes from the order of the journal callback and the resume task, not from time. Check: run the test 40 times with the wake time an hour ahead; if it still fails, the cause is the order.
- **Change:** the registry reads the clock of its store (or takes a `TimeProvider`), waits with `Task.Delay(left, time, token)`; the test uses a fake time provider and advances it instead of waiting 500 ms.
- **Removes / adds:** removes a wall-clock window from a test and two `UtcNow` reads; adds one parameter.
- **Confidence:** likely (missing check: a reproduction under load, `--count 40` on one core)
- **First step:** the registry uses `store`'s `TimeProvider`; the test moves to a fake one.
- **Verify:** 0 failures in 40 runs of the test alone under a one-core affinity; before the change, the same loop with a 50 ms window should fail.
- **Risk:** other registry callers that construct a store with the system clock keep the system clock: no change of behavior.

### Give the plugin-runtime skill one screen of instructions and its detail in references
- **Lens / area / action:** docs, agent instructions · `BuiltinSkills/codealta-plugin-runtime` · rewrite
- **Cost:** each activation of the skill, one of the most used of this project, loads 8,958 words (about 12,000 tokens) into the session, and it states one fact in two places.
- **Evidence:** `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md` has 730 lines and 26 `##` sections, one per contribution point; a canvas is defined twice, `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:274` ("A canvas is a tab that a plugin provides") and `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:436` ("A canvas is a tab of CodeAlta Desktop that the plugin provides"); the skill already has `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/references/script-api.md` and `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/references/script-libraries.md`, read on demand; 19 commits in four weeks, 10 of them with `site/docs/plugins/developers.md`.
- **Or else:** agents need all of it in most plugin tasks, so loading it once is cheaper than several reads. Check: in the sessions that activated the skill, which sections the work used (a reader test with four tasks: a command, a canvas, a tool hook, a setting).
- **Change:** `SKILL.md` keeps the work loop (create, build, reload, look at the window), the shape of a plugin and an index of the contribution points, one line each with its reference file; the 20 sections of contribution points move to `references/<point>.md`; the two canvas sections become one.
- **Removes / adds:** removes about 7,000 words from each activation and the duplicated canvas definition; adds about ten reference files (moved text, no new text).
- **Confidence:** verified (size, duplication); likely for the gain (missing check: the reader test)
- **First step:** merge the two canvas sections into one new reference file, `canvases.md` beside the two existing ones, and link it from the index.
- **Verify:** `SKILL.md` under 2,000 words; the reader test answers the four tasks from the index and one reference each.
- **Risk:** the skill tests (`SkillCatalogTests`) that check the files it names must follow the move.

### Wait for a condition in the browser tool tests, not for a fixed time
- **Lens / area / action:** tests (cost, flakiness) · `CodeAlta.Browser.Tests` · fix
- **Cost:** three test files wait 61 fixed delays, 14.2 s in all, on every run, each "act, sleep, assert once": too short on a loaded machine, wasted on a fast one.
- **Evidence:** `src/CodeAlta.Browser.Tests/BrowserDrawingTests.cs:175` (click, `Task.Delay(100)`, then one check of `drawState().shapes`); `src/CodeAlta.Browser.Tests/BrowserAnnotationsTests.cs:426` (sleep 300 ms then assert that no message came); `src/CodeAlta.Browser.Tests/BrowserAnnotationsTests.cs:615` (2,000 ms). The development guide asks tests to start concurrent sides on a signal (`doc/development-guide.md:39`).
- **Or else:** none of these failed in the 300 CI runs read, so the flake is a risk, not a measured rate. Check: run the three files ten times with two parallel test processes.
- **Change:** one helper that polls a toolbar state until it holds, under `TestTime`; the checks of absence wait for the next message the page must send (a ready or a state change) rather than a duration.
- **Removes / adds:** removes 61 sleeps and about 14 s per run; adds one helper.
- **Confidence:** verified (count, sum); likely for flakiness
- **First step:** the helper and `BrowserDrawingTests.cs` moved to it.
- **Verify:** the test time of the three files drops by about 14 s; ten runs under load pass.
- **Risk:** an absence check needs an event that marks "nothing more comes"; where the page has none, keep a short settle time with `// not a limit`.

### Check what an agent does through one path, not at each command that can act
- **Lens / area / action:** boundaries · tool calls of sessions, `alta` commands · merge
- **Cost:** in three weeks five fixes closed a path by which a session acted without the checks of its tool calls (autonomy guard, tool hooks, permission mode): `alta mcp tool call`, plugin commands, Code Mode calls of `alta`, exec scripts under Claude Code's Auto mode, children and later turns of a run.
- **Evidence:** commits `f234ae8a4` (2026-11-04), `beda233d8` (2026-11-04), `874115cd6` (2026-11-04), `db09b80bd` (2026-11-01), `d63063583` (2026-11-18, "A call of alta.* is now a call of the session's own alta tool"); guard calls in `src/CodeAlta.LiveTool/AltaCommandRegistry.cs:62`, `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:5699` (three callers), `src/CodeAlta.LiveTool/PluginAltaCommandContributor.cs:159`, `src/CodeAlta.Plugin.Mcp/McpCommandFactory.cs:489`, beside the interceptor of tool calls (`src/CodeAlta.Orchestration/Runtime/SessionToolGuards.cs:172`).
- **Or else:** the paths are now all closed, and the next one is rare. Check: list the `alta` commands that change state and do not reach `SessionToolGuards`.
- **Change:** generalize what `d63063583` did for Code Mode: a command an agent runs is a call of its session's tool, checked once by the interceptor, and the per-command guard calls go.
- **Removes / adds:** removes the guard calls spread over the commands; adds nothing if the interceptor sees the command line.
- **Confidence:** hunch (borders on security, out of scope of the brief; the list of remaining paths was not made)

## Healthy: keep as it is
- **The actor discipline of session state.** The pending model and prompt of a session are written inside `actor.QueryAsync` (`src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs:1442-1457`), as `doc/runtime.md` says; the size of `SessionRuntimeService` does not come with writes outside the actor.
- **One source of test time.** `TestTime` and `testLimit`, with `WithinAsync` naming what was awaited: the flaky tests found on CI are not timeouts.
- **Citations of the docs.** 109 paths cited in backticks in `doc/`, `AGENTS.md` and `readme.md`: one is stale (`doc/site-screenshots.md:125` names `src/mobile/…` without `CodeAlta/frontend`), the others are generated or example files.
- **The mobile server and its client change together** (11 shared commits of 12): code with its client and tests (`MobileServerTests` call the API 141 times), with two fixes in eight weeks.
- **The Fleet sign-in chain** (protocol, server, client, host, RPC, page) changes together because a sign-in crosses processes; the RPC layer validates and maps errors, it does not only pass calls.

## Tempting, and wrong here
- **Split `SessionRuntimeService` (16,037 lines in 44 files) or `BuiltInAltaCommandContributor` (about 25,000 lines) into many services because of their size.** The partial files already group by topic, and the run-lifecycle fixes (aborts, queued prompts, steers) are about ordering inside one session, which one owner keeps right. A split would put that order across services. Extract only a cluster that owns its fields: the four sandbox partials (1,405 lines, eight fields of their own) are the candidate, if a later change there needs it.
- **Treat the Fleet budget gate as a broken design** because 20 of its 21 commits are fixes: 30 fixes landed on 2026-11-04 and 05 from a review just after the feature, two since.

## Observations
- Three `main` runs were started again because `Tests (macos-latest, platform-specific)` was cancelled after 15 minutes without a failed step (runs 44574308664, 44327876124, 43453313598): a runner or queue cause, not read further.
- `ASnapshot_WhoseIndexGitCannotReplace_IsWrittenThroughAnIndexGitCreates` failed on Windows CI on `Assert.IsNotNull(expected)` (`src/CodeAlta.Catalog.Tests/ReviewServiceTests.cs:378`): the snapshot without any held file returned null, which the product may also meet. Cause not found.
- The 476 "workaround" markers of the signals are mostly variables named `temporary`: the marker overstates the workarounds.
- `BootRpc.cs` and `localization.ts` are registries; their commit counts measure features, not their own complexity.
- `doc/site-screenshots.md` (8,228 words, 28 commits) is a procedure in a folder the development guide keeps for architecture.
