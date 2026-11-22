# Health review of CodeAlta

CodeAlta · at 2a3fd8388 · 2026-11-22 · whole repository at `feature50`: .NET libraries and executables, desktop frontend, tests, CI, docs and agent instructions

The core rules hold: one writer per session, clean low layers, guardrail tests that catch regressions. The cost is in how state is saved and decisions repeated: stores whose saves their readers refuse, provider edits that bypass the store's lock, decisions kept in two places, and one 6,700-line component every feature edits. Most cures delete or merge code.

- **11 of 16** strong findings (found or confirmed by two analysts, checked in the code)
- **14** writers of the provider list that skip the store's locked update (in the desktop, the node and the TUI; 2 of them write defaults into `config.toml`)
- **134** `useState` in the `App` component (6,741 lines; `main.tsx` changed in 144 of 1,702 commits in four weeks)
- **3 in 6 days** session lists broken by a column without a version bump (2026-10-26 to 2026-10-31, each an existing database)
- **8,958** words an agent loads with the plugin skill (about 12,000 tokens per activation; the next built-in skill has 2,096)

## Order of work

**Now: stop the silent losses**: Small fixes and a deletion, each with its test first.
1. F01 Keep the automation state within the size its own reader accepts
2. F04 Delete the TUI's copy of a provider definition
3. F05 Add the missing columns of the session list at each open, from one list
4. F02 Grant an automation occurrence only once its claim is written
5. F09 Delete the raw skill-scanning code that only its tests call (Pure deletion, 1,878 lines.)

**Next: make each decision exist once**
1. F03 Edit the providers of `config.toml` through the store's locked update, on the raw definitions (Model-server and node paths first.)
2. F06 Give Settings the inherited project scope that sessions use
3. F12 Give every test deadline the CI factor and replace the fixed sleeps of the browser tool tests (Before the frontend moves: they are judged by the suite.)
4. F15 Cut the plugin-runtime skill to a workflow and an index
5. F13 Say why a review snapshot failed instead of returning null

**Then: the structural moves, in steps**
1. F11 Move the state of features out of the `App` component (One surface per commit, renders measured.)
2. F10 Delete the desktop start without a host (List its users first.)
3. F07 Let a desktop RPC service declare its JSON types and fixtures in its own file (After F10: one registration left to move.)
4. F08 Check each service of the built-in `alta` commands once, for its commands and its policies
5. F16 Move the desktop-window guide out of `AGENTS.md`

**When this code is next changed**
1. F14 Run the search kernel test on the macOS and Windows runners

## Findings

### Saves that lose what they keep

A save that succeeds and loses state costs more than one that fails: these come first.

#### F01 Keep the automation state within the size its own reader accepts

`fix` · strong · impact high · effort S · risk low · `automations` · opus confirmed, gemini confirmed, astra found

The automation state can be saved larger than the 8 MiB its reader accepts; the next start then loads empty state with every automation paused, no run history and no trigger watermarks.

Evidence:
- `src/CodeAlta.Automations/AutomationService.Autonomy.cs:132` A try records each call it does not make, as a line of up to 2,000 characters.
- `src/CodeAlta.Automations/AutomationState.cs:51` Up to 50 such calls per run.
- `src/CodeAlta.Automations/AutomationState.cs:190-195` 100 runs kept per automation, 2,000 in all, against a reader limit of 8 MiB: one automation alone can reach 10 MB.
- `src/CodeAlta.Automations/AutomationState.cs:313` Retention counts runs, not bytes.
- `src/CodeAlta.Automations/AutomationState.cs:444` The reader returns nothing above the limit and marks the file unreadable; the store then starts empty and paused.
- `src/CodeAlta.Automations/AutomationState.cs:219` The paused, empty state that replaces it.
- `src/CodeAlta.Automations/AutomationState.cs:467` `Save` writes the whole state whatever its size.

Change: Make the read limit a rule of the save: when the serialized state is over budget, drop the oldest completed runs, their calls first, until it fits. Never drop runs in progress, the pause choice, approvals or trigger watermarks; if those alone do not fit, fail the save and keep the last readable file.

Removes: the outcome where a save succeeds and the next start loses the state of every automation  
Adds: a size check on the bytes already serialized; one round-trip test

First step: A test that fills a state just under 8 MiB, adds one run that crosses the limit and reopens the store: today it starts paused and empty. Then the size check in `Save`.

Verify: The reopened store keeps the newest run and a watermark set before, and stays unpaused.; The automation tests of `CodeAlta.Desktop.Tests` pass; the test of a malformed file still pauses.

Risks: The limits on runs become upper bounds: 100 very large runs may not all be kept.

If we leave it: A user who runs long tries of many automations finds them all paused one morning, with their history and trigger state gone.

#### F02 Grant an automation occurrence only once its claim is written

`fix` · strong · impact medium · effort S · risk low · `automations` · opus confirmed, gemini confirmed, astra found

When the claim file cannot be written, `TryClaim` still grants the occurrence; after a restart the claim is gone and the same occurrence can be granted and run a second time.

Evidence:
- `src/CodeAlta.Automations/AutomationClaims.cs:153` `TryClaim` saves, then returns true whatever the save did.
- `src/CodeAlta.Automations/AutomationClaims.cs:253` `Save` swallows I/O and access errors.
- `src/CodeAlta.Automations/AutomationClaims.cs:239` An unreadable claim file reads as no claims at all.
- `src/CodeAlta.Automations/AutomationService.cs:797` The grant reaches the start of the run.
- `src/CodeAlta.Automations/AutomationState.cs:420` Commit `09ee9a3ef` fixed the same assumption for the hourly call counter, whose store now returns the result of its save.

Change: `Save` returns whether it wrote, and `TryClaim` grants only then. An unreadable ledger means "storage unavailable", not "no claims": the scheduler skips or defers the occurrence with that reason.

Removes: a grant that no file records; the reading of an I/O error as an absence  
Adds: a claim outcome; an error-path test

First step: In the placement fixture, make the claim file unwritable and check that the start is refused; keep the restart test that passes today.

Verify: Zero runner starts on a failed claim write.; After the storage recovers, a reopened store refuses the same occurrence.

Risks: A missed or deferred occurrence is preferred to a duplicate run: a pooled event must not be marked seen when its claim failed.

If we leave it: Rare, but when it happens the side effects of an automation run twice.

#### F03 Edit the providers of `config.toml` through the store's locked update, on the raw definitions

`merge` · strong · impact high · effort M · risk medium · `provider settings` · opus confirmed, gemini confirmed, astra found

Fourteen call sites in the desktop, the node and the TUI read and rewrite the whole provider list of `config.toml` by themselves: none holds the file lock from the read to the write, and two read the definitions with their defaults filled in, so adding a model server writes defaults into every other provider.

Evidence:
- `src/CodeAlta.Catalog/CodeAltaConfigStore.cs:275` The store already has a read-and-save under the lock of the file, which nine settings writers use and no provider edit does.
- The callers of `UpdateGlobal`: settings only. (`git grep -n '.UpdateGlobal(' -- 'src/*.cs' ':!src/*Tests*'`)
- The 14 callers of the whole-list save, outside the store. (`git grep -n 'SaveGlobalProviderDefinitions(' -- 'src/*.cs' ':!src/*Tests*'`)
- `src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:124` The text editor holds the file lock across its revision check and its save.
- `src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:943` The structured form checks the revision under an in-process lock only; the store takes the file lock at the save.
- `src/CodeAlta/Desktop/DesktopApplication.cs:909-917` Adding a model server reads the completed definitions and saves them all back.
- `src/CodeAlta.Catalog/CodeAltaConfigStore.cs:1879` That read fills in the defaults of every definition.
- `src/CodeAlta.Node/NodeScopeProviders.cs:143` The node does the same for the providers of a scope.
- `src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:976` The form avoids it on purpose: "no defaults are filled in, so saving them back adds none".

Change: Give the store one provider edit: under `ConfigFileLock`, read the raw definitions, apply the caller's change, check the revision when the caller has one, save. Move the 14 call sites to it, the model-server and node paths first. Complete the definitions only to apply them to the runtime, after the write.

Removes: 14 hand-written read-modify-write loops; defaults written into untouched providers; edits lost between two writers (the developer instance shares `config.toml`)  
Adds: one store method of about 20 lines; a two-writer test and a raw round-trip test

First step: Add the store method and move the model-server path to it, with a test: a config with a minimal Codex provider keeps no `api_url`, `max_concurrent_requests` or `model_discovery` after a model server is added.

Then: Move the structured form and check two writers on one revision: one `ok`, one `conflict`.; Move the node paths (`NodeScopeProviders`, `NodeProviders`).; Move the TUI coordinator; the whole-list save remains for the store only.

Verify: No caller outside the store reads completed definitions to save them.; The two-writer test returns exactly one `ok`, and the winner's value is on disk.

Risks: `ConfigFileLock` is a thread-affine mutex: the edit must not span an `await`.; Disabled providers and providers of an unknown type stay as they are.

If we leave it: Each new caller copies one of the 14 loops, and an edit made in the developer instance while the normal one saves is lost without a word.

#### F04 Delete the TUI's copy of a provider definition

`delete` · strong · impact medium · effort S · risk low · `provider settings` · opus confirmed, gemini confirmed, astra found

Saving one provider in the TUI rewrites every provider through a private copier that lacks seven properties the store's copy keeps, so it removes them from untouched providers in `config.toml`.

Evidence:
- `src/CodeAlta.Tui/ViewModels/ModelProviderEditorItemViewModel.cs:377` The TUI copier: no `ContextSize`, `Sandbox`, `CodeMode`, `Server`, `Request`, `ModelRequest` or `Headers`.
- `src/CodeAlta.Catalog/CodeAltaConfigStore.cs:3920` The store's copy, which has them.
- `src/CodeAlta.Tui/Views/ModelProvidersDialog.cs:1459` The dialog rebuilds every provider to save.
- `src/CodeAlta.Catalog/CodeAltaConfigStore.cs:2222` The merge removes the modeled keys a definition no longer has.
- `src/CodeAlta.Catalog/CodeAltaConfigDocumentCopy.cs:153` Commit `77da0870b` had to add the model visibility rules to both copies; a third copier exists for documents.

Change: Use `CodeAltaConfigStore.CloneProviderDefinition` in both paths of the TUI editor and delete its copier.

Removes: a 64-line copier and its own list of fields  
Adds: one preservation test

First step: Extend the round trip of `ProviderFrontendCoordinatorTests` with an untouched provider that has a `server` and a `context_size`: it fails today. Then replace the copier.

Verify: The untouched provider keeps both settings when the config is reopened.; The tests of omitted defaults still pass.

Risks: The store's copy deep-copies nested tables: compare values, not references.

If we leave it: A TUI user who renames one provider loses the model server or the sandbox of another.

### One decision kept in two places

Each was fixed in one place and missed in the other; the cure makes it exist once.

#### F05 Add the missing columns of the session list at each open, from one list

`simplify` · strong · impact high · effort S · risk low · `session list` · opus found, gemini confirmed, astra unreviewed

Three times in six days a column added to the session list broke listing sessions on every existing database ("no such column"), because its migration only runs when `SchemaVersion` changes and the version was not raised.

Evidence:
- `src/CodeAlta.Catalog/ApplicationDatabase.cs:345` The migration is skipped when the stored version is the one asked for.
- `src/CodeAlta.Catalog/SessionJournalSqliteCache.cs:15` The version of the session list.
- `src/CodeAlta.Catalog/SessionJournalSqliteCache.cs:1599-1785` Nine hand-written `Add…ColumnAsync` methods, each a `pragma_table_info` check then an `ALTER TABLE`.
- Three fixes that only raised the version: sandbox column ("no such column: c.local_sandbox"), options column, cloud column. (`git show --stat 77e0346bd c3a5461df 8f15ae2f5`)
- `doc/development-guide.md:7` Every new fact about sessions is a column or a table of the database.

Change: Declare each column once: name, type, and whether adding it invalidates the rows. At each open, read `pragma_table_info` (a read) and add what is missing in one write. Keep `SchemaVersion` for what a list of columns cannot express: moving large values, dropping a column.

Removes: nine near-identical methods, about 170 lines; the rule "raise the version when you add a column"  
Adds: a column table and a loop of about 30 lines

First step: A test that creates the session tables without one declared column, at the current version, then lists sessions: it fails today. Then the loop; the nine methods go.

Verify: That test passes, also for a column added to the list without changing the version.; `CodeAlta.Catalog.Tests` pass.

Risks: The permission-mode and space columns also clear the rows: keep that flag per column.; An up-to-date database must stay a read at open (`ApplicationDatabase.cs:337`).

If we leave it: The next column of a session breaks the session list of every user who already has a database.

#### F06 Give Settings the inherited project scope that sessions use

`merge` · strong · impact medium · effort M · risk medium · `settings of a sub-project` · opus confirmed, gemini confirmed, astra found

For a sub-project, Settings and the session disagree on skills, prompts and plugins: a repository skill offered to the session is missing from Settings, a skill the parent turned off shows as on, and the Prompts and Plugins pages show the sub-project's own entries only, against the user guide.

Evidence:
- `src/CodeAlta.Catalog/Skills/SkillCatalogQuery.cs:160-175` A session takes the inherited roots, the effective folders and the disabled names of every ancestor.
- `src/CodeAlta.Catalog/Skills/SkillManagementService.cs:92` The Settings listing reads the sub-project's disabled names only, with one root.
- `src/CodeAlta.Catalog/Skills/SkillManagementService.cs:149` Its query: one project root.
- `src/CodeAlta/Desktop/Rpc/SkillsRpc.cs:517` The page passes `SkillFolders`, not `EffectiveSkillFolders`.
- `src/CodeAlta/Desktop/Rpc/McpServersRpc.cs:688` The scope of a Settings page is resolved to a root only, for every page.
- `src/CodeAlta/Desktop/Rpc/AgentPromptsRpc.cs:69-74` The Prompts page queries that root alone.
- `site/docs/monorepos.md:95-99` What the user guide says a sub-project inherits.

Change: `SettingsProjectScope.ResolveAsync` returns the project, and the Skills, Prompts and Plugins pages read through the computation of effective roots and disabled names that sessions use (`ProjectResourceRoots.Inherited`). Settings keeps its diagnostics; creating, deleting and configuring still write to the sub-project's own files.

Removes: a second definition of what a sub-project inherits, in three pages  
Adds: a sub-project fixture per page

First step: Skills: carry the parent's disabled names into the listing of a sub-project, with a test where a user skill is turned off by the parent only.

Then: Skills: the inherited roots and `EffectiveSkillFolders`.; Prompts, then Plugins, through the same resolved project.

Verify: The sub-project lists that skill as unavailable; a skill of the parent is listed and offered.; Prompts and Plugins of a sub-project list the repository's entries, read-only.

Risks: An inherited entry stays read-only from the sub-project: a repository-level off must not become a setting of the sub-project.

If we leave it: In a monorepo, Settings keeps showing a list the sessions do not use.

#### F07 Let a desktop RPC service declare its JSON types and fixtures in its own file

`simplify` · worth-exploring · impact medium · effort M · risk low · `desktop RPC` · opus found, gemini found, astra unreviewed

A new RPC service edits its own file and five shared ones; `BootRpc.cs` changed in 109 commits in four weeks, all in its list of 1,021 `[JsonSerializable]` lines, and `demo-api.ts` in 45, 38 of them with `BootRpc.cs`.

Evidence:
- The labels service: its file, `DesktopApplication.cs` (twice), `BootRpc.cs`, `demo-api.ts` and a fixture. (`git show --stat c708f677b`)
- `src/CodeAlta/Desktop/Rpc/BootRpc.cs:151-1173` One context for every RPC type of the desktop.
- `src/CodeAlta/Desktop/Rpc/BootRpc.cs:5` NeoAstra is given that one context type.
- `src/CodeAlta/frontend/src/demo-api.ts:281` A stub of `unavailable` methods per service.
- `src/CodeAlta/frontend/src/projectFocus.neoastra.mount.ts:7` A fixture that names 60 services one by one; a missing name broke the test bundles once (`e66f2a618`).

Change: Each `*Rpc.cs` declares `partial class DesktopJsonContext` with the attributes of its own types, if the JSON source generator accepts them spread over partial declarations: the first step tells. The fixtures re-export `demo-api` with `export *` and override what they fake; `demo-api` builds its `unavailable` stubs through one helper. With F10, the second registration in `DesktopApplication.cs` goes too.

Removes: the central list of 1,021 attributes; the 60-name export list of the fixture; most stub methods of `demo-api.ts`  
Adds: one partial declaration per RPC file; one stub helper

First step: Move the attributes of one RPC file (`ReleaseNotesRpc.cs`) to a partial declaration in that file; build and run its RPC tests.

Verify: `BootRpc.cs` holds the boot types only.; A new service edits its own file, `DesktopApplication.cs` and `main.tsx`, nothing else.

Risks: A type shared by two services is declared once.; Two star re-exports of one name are dropped silently in ES modules: an ambiguous name stays explicit.

Disagreement: One analyst proposed one JSON context per RPC file, combined at start: `[assembly: NeoRpcJsonContext(typeof(DesktopJsonContext))]` names a single context for NeoAstra, so the partial declarations are the cure kept.

#### F08 Check each service of the built-in `alta` commands once, for its commands and its policies

`simplify` · worth-exploring · impact low · effort M · risk low · `alta live tool` · opus confirmed, gemini found, astra unreviewed

The built-in commands test about 20 optional services twice, once to build the commands and once to list their policies, and the two lists have drifted: the early return of the policies does not test the credential broker.

Evidence:
- `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:157-316` The commands, built service by service.
- `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:324-379` The policies, the same services tested again.
- `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:346` The early return lists 20 services but not the credential broker, whose policies come later.
- `src/CodeAlta.LiveTool/AltaCommandRegistry.cs:13-20` The registry already takes several contributors.

Change: Each self-contained partial (credentials first) becomes its own `IAltaCommandContributor` that tests its service once and returns its commands and its policies together; the router of 22 flags goes.

Removes: the second test of every service; the drift between commands and policies  
Adds: small contributor classes on the existing interface

First step: A test with only the credential broker registered asserts that the `credential` policies are listed: it fails today. Then the credentials contributor.

Verify: That test passes; the help and policy tests of `AltaLiveToolTests` pass with the same order.

Risks: The order of help and policies follows the order of registration: keep it.

If we leave it: No host found today registers the broker alone: the drift stays latent until one does.

### Code that only its tests reach

#### F09 Delete the raw skill-scanning code that only its tests call

`delete` · strong · impact medium · effort S · risk low · `skills` · opus confirmed, gemini confirmed, astra found

Five files (1,074 lines) and their five test files (804 lines, 25 tests) scan, read and parse skills with no product caller, and answer differently from the live catalog: they reject the `triggers` and `version` fields it accepts.

Evidence:
- `src/CodeAlta.Catalog/Skills/RawSkillCandidateReader.cs:65` "never used by ordinary skill discovery or activation".
- The five types are named only in their own files and their tests. (`git grep -l -E 'RawSkillCandidateReader|SkillCandidateMetadataReader|SkillMetadataParser|SkillIgnoreSourceReader|SkillIgnoreSourceParser' -- .`)
- `src/CodeAlta.Catalog/Skills/SkillCatalog.cs:42` The live catalog accepts `triggers` and `version`.
- `src/CodeAlta.Catalog/Skills/SkillMetadataParser.cs:212` The raw parser rejects them.
- No change to the five files since 2026-09-26. (`git log -1 --format=%ad -- src/CodeAlta.Catalog/Skills/SkillMetadataParser.cs`)

Change: Delete the ignore reader and parser with their tests, then the candidate reader and metadata parser with theirs. Keep `SkillFrontmatterReader` and the shared frontmatter types.

Removes: 10 files; 1,878 lines; 25 tests of code nothing runs  
Adds: nothing

First step: Delete `SkillIgnoreSourceReader`, `SkillIgnoreSourceParser` and their two test files; build and run `CodeAlta.Catalog.Tests`.

Verify: The search of the five names finds nothing.; The build and the Catalog tests pass.

Risks: The types are public in a project that is not packed: no consumer outside the repository was found.

If we leave it: A maintainer who finds these files gets a second, wrong answer to "what is a valid skill".

#### F10 Delete the desktop start without a host

`delete` · worth-exploring · impact medium · effort S · risk medium · `desktop host` · opus confirmed, gemini found, astra unreviewed

Every desktop RPC service is registered a second time for a start without a host that only test-harness flags reach (`--data-root` without `--allow-owned-host`), and the two lists have drifted to 81 and 55 registrations.

Evidence:
- `src/CodeAlta/Desktop/DesktopApplication.cs:129` The start without a host is taken only when `Owned` is null.
- `src/CodeAlta/Desktop/DesktopCommandLine.cs:476` A normal start sets it.
- `src/CodeAlta/Desktop/DesktopCommandLine.cs:306-334` It is null only for explicit roots without `--allow-owned-host`.
- `src/CodeAlta/Desktop/DesktopApplication.cs:1858-1913` Its 55 registrations.
- `src/CodeAlta.Desktop.Tests/DesktopWorkspaceTests.cs:121` Tests that start it.
- `src/CodeAlta/README.md:101` The explicit roots are documented for tests and for running on a copy of a profile.

Change: Remove `RunAsync`, the boot state of a start without a host, its parsing branch and the stub constructors only it uses; move its tests and every procedure that uses it to `--profile` or `--allow-owned-host`.

Removes: about 135 lines and a second way to start; the second registration of each RPC service  
Adds: nothing

First step: List the procedures that start the window without `--allow-owned-host` (the screenshot recipe, the release skill, the tests) and move them to `--profile`.

Verify: No `"in-development"` boot state is left under `src/CodeAlta`.; The desktop tests pass.

Risks: The README offers the explicit roots to run on a copy of a profile: check that `--profile` covers that use.

### The state of the desktop window in one component

#### F11 Move the state of features out of the `App` component

`move` · strong · impact high · effort L · risk medium · `desktop frontend` · opus found, gemini found, astra unreviewed

`App` in `main.tsx` is one component of 6,741 lines with 134 `useState`, 95 `useEffect` and 94 `useRef`; 144 commits changed `main.tsx` in four weeks (22.7 files each on average), and four text fields re-render the whole window at each keystroke, against the development guide.

Evidence:
- `src/CodeAlta/frontend/src/main.tsx:550` The component, from line 550 to line 7291.
- `src/CodeAlta/frontend/src/main.tsx:6514` Keystrokes of the sidebar filter set state of `App`.
- `src/CodeAlta/frontend/src/main.tsx:6533` The project rename field.
- `src/CodeAlta/frontend/src/main.tsx:6689` The session rename field.
- `src/CodeAlta/frontend/src/main.tsx:6980` The Settings search.
- `src/CodeAlta/frontend/src/main.tsx:1099-1160` The state of the rename, delete and move of sessions and projects, held by `App`.
- `doc/development-guide.md:31` "Keystrokes, tokens and timers never re-render the whole app".
- `.alta/tasks/2026-11-19-render-the-session-panes-once-for-a-tab-switch-wtvm.md` A tab switch renders all of `App`: "a gain needs the whole of `App` not to render for a switch".
- 144 commits in four weeks. (`git log --since='4 weeks ago' --no-merges --oneline -- src/CodeAlta/frontend/src/main.tsx`)

Change: Each surface owns its state in its feature folder: the rename popover keeps its draft, the sidebar filter its text, the delete, archive and move dialogs their busy and message state, then a `SettingsHost` the Settings overlay with its search. `App` keeps what the window shares: the workspace, the tabs, the selection.

Removes: about 40 `useState` and their effects from `App` in the first steps; whole-window renders while typing  
Adds: a component or a hook per surface, in folders that exist

First step: `RenamePopover` keeps the title it edits and calls back on submit; `renamingTitle` leaves `App`. A browser test counts the renders of `App` while typing a title.

Then: The sidebar filter, then the project rename.; The delete, archive and move dialogs, one per commit.; `SettingsHost`, with the Settings search.; Measure the tab switch again (task `wtvm`) before choosing an external store.

Verify: No render of `App` per keystroke in the rename popover, the sidebar filter and the Settings search.; `npm test` passes; the count of `useState` in `App` falls at each step.

Risks: State two surfaces read (selection, workspace) stays shared.; Moved effects stay idempotent under StrictMode.

Not part of this: No state library.

Disagreement: Two analysts differ on the order: the smallest surfaces first, or Settings first. Settled: the rename popover first (one field, one test), Settings once the pattern holds; the analyst who proposed Settings first agreed in review.

### Tests and CI that say what failed

Every other change of this review is judged by the suite.

#### F12 Give every test deadline the CI factor and replace the fixed sleeps of the browser tool tests

`fix` · strong · impact medium · effort S · risk low · `tests` · opus found, gemini found, astra unreviewed

33 test deadlines written `UtcNow.AddSeconds(…)` escape `TestTime` and its four-fold CI factor, some of 2 s, and three browser test files sleep 61 times, 14.2 s per run, each time acting, sleeping and asserting once.

Evidence:
- `src/CodeAlta.Tests/TestTimeGuardTests.cs:118` The guard catches `UtcNow + TimeSpan.From…` but not `UtcNow.AddSeconds(…)`.
- `src/CodeAlta.Tests/AltaLiveToolTests.cs:7408` A wait helper with a 2-second deadline, unscaled on CI.
- `src/CodeAlta.Browser.Tests/BrowserPluginTests.cs:413` A 30-second deadline written the same way.
- `src/CodeAlta.Browser.Tests/BrowserDrawingTests.cs:175` Click, sleep 100 ms, check once.
- `src/CodeAlta.Browser.Tests/BrowserAnnotationsTests.cs:615` Sleep 2 s, then assert that nothing came.
- `doc/development-guide.md:39-40` Every limit of a test comes from `TestTime`.

Change: Write the 33 deadlines with `TestTime`. Replace the sleeps by one helper that polls the page state until it holds, under `TestTime`; a check of absence waits for the next message the page sends.

Removes: 33 limits without the CI factor; 61 sleeps, 14 s of every run  
Adds: one polling helper

First step: Convert the two 2-second deadlines of `AltaLiveToolTests.cs`, and add the polling helper to `BrowserDrawingTests.cs`.

Verify: The search of `UtcNow.AddSeconds(`, `AddMilliseconds(` and `AddMinutes(` in the tests lists only times marked `// not a limit`.; The three browser files run about 14 s faster and pass ten times with two test processes.

Risks: Some `UtcNow.Add…` of the tests are data, not limits: they stay, marked `// not a limit`.

Disagreement: One analyst also wants the guard's pattern extended to `UtcNow.Add…(`, so that the mistake cannot come back. Not decided here: extending a source-text guard is the owner's call.

#### F13 Say why a review snapshot failed instead of returning null

`fix` · worth-exploring · impact low · effort S · risk low · `review snapshots` · opus confirmed, gemini found, astra unreviewed

`GitSnapshot.WriteTreeAsync` drops git's error and I/O exceptions and returns null; a Windows CI run failed on its first, unconstrained snapshot (run 43251505041), and its log says nothing of why.

Evidence:
- `src/CodeAlta.Catalog.Tests/ReviewServiceTests.cs:378` The assertion that failed on CI: the snapshot before anything is held.
- `src/CodeAlta.Catalog/Reviews/ReviewTextSource.cs:207-237` The git error and the I/O exceptions are discarded.
- `.alta/report/2026-11-22-health/signals/ci-runs.md` The first attempts of the runs of `main` that passed later.

Change: Carry git's exit code and error, or the exception, to the caller and into the failure message of the test. Choose a fallback once the reason is known.

Removes: a failure without a reason  
Adds: a reason on the result

First step: Return the reason with the null result, and print it in the assertion of `ReviewServiceTests.cs:378`.

Verify: The next failure of this test on CI names git's error or the exception.

Risks: The reason may hold paths: keep it out of what the user sees.

#### F14 Run the search kernel test on the macOS and Windows runners

`fix` · strong · impact low · effort S · risk low · `CI` · opus confirmed, gemini confirmed, astra found

The platform jobs run only the test files that mention `OperatingSystem.Is…()`; the test that checks the CPU-specific search kernels against the scalar one is in none of them, so the ARM kernel is never checked on CI.

Evidence:
- `.github/workflows/ci.yml:68-71` The selection of the platform tests.
- `src/CodeAlta.Tests/Search/SearchVectorTests.cs:20` The test picks its kernels by CPU flags, not by system.
- `src/CodeAlta.Search/SearchVectorKernels.cs:96` The ARM path uses another instruction.
- The selection run locally does not list `SearchVectorTests.cs`. (`grep -rlE 'OperatingSystem\.Is(Windows|MacOS|Linux)\(\)' --include='*.cs' src/CodeAlta*.Tests | grep -i search`)

Change: Add `SearchVectorKernelTests` to the filter of the platform jobs by name.

Removes: one hole in the CI selection  
Adds: one name in the filter

First step: Add the class to the filter of `ci.yml` and read its output on the next macOS run.

Verify: The macOS job runs `SearchVectorKernelTests`; its output names `arm64-sdot` when the runner has it.

Risks: Running the test does not prove the runner's CPU has the instruction.

Disagreement: Another analyst also proposed selecting by `[TestClass]` or a test category, because 13 helper class names enter the filter. They match no test: the filter is `FullyQualifiedName~.<Name>.`, and no test name has such a segment. Dropped.

### Text agents load in every session

#### F15 Cut the plugin-runtime skill to a workflow and an index

`rewrite` · strong · impact medium · effort M · risk low · `agent instructions` · opus found, gemini found, astra unreviewed

Each activation of `codealta-plugin-runtime` loads 8,958 words (about 12,000 tokens), four times the next built-in skill; it defines a canvas twice and restates the plugin developer guide, with which it shares 10 of its 19 commits in four weeks.

Evidence:
- `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:274` "A canvas is a tab that a plugin provides".
- `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:436` "A canvas is a tab of CodeAlta Desktop that the plugin provides".
- `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:509-627` Tool hooks, `alta` commands, automation triggers and credential providers, told here…
- `site/docs/plugins/developers.md:222-395` …and in the developer guide.
- `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:103-115` The skill already sends agents to `alta plugin api` for the C# types.

Change: Keep in `SKILL.md` the work loop (create, build, reload, look at the window), the shape of a plugin, the traps (tool hooks that fail closed, the HTML sandbox) and an index of the contribution points, each with the type to read with `alta plugin api` and its folder of `samples/`. Delete the C# prose that `alta plugin api` and the developer guide already give; keep `references/` for the JavaScript API, which `alta plugin api` does not cover; merge the two canvas sections.

Removes: about 6,500 words per activation; the second definition of a canvas; the co-change with the developer guide  
Adds: pointers to `alta plugin api` and `samples/`

First step: Replace the sections on tool hooks, `alta` commands, automation triggers and credential providers (`SKILL.md:509-627`) by short rules and pointers.

Verify: `SKILL.md` under 2,500 words.; A fresh session with the skill alone does four tasks (a command, a canvas, a tool hook, a setting) with `alta plugin api` and one sample each.

Risks: Every `samples/` path the index names must exist: the skill tests check the files it names.

Disagreement: Two cures were proposed: move each section to its own reference file, or delete what `alta plugin api` and the developer guide already say. Deleting was kept: moving would keep two copies of the API to change together.

#### F16 Move the desktop-window guide out of `AGENTS.md`

`move` · worth-exploring · impact medium · effort S · risk medium · `agent instructions` · opus confirmed, gemini found, astra unreviewed

Every session of the project reads `AGENTS.md`; 1,605 of its 3,041 words are on driving the desktop window, which most sessions do not do, and several of its rules repeat `doc/development-guide.md`.

Evidence:
- `AGENTS.md:73-133` "Working on the desktop WebApp": 1,605 words.
- `AGENTS.md:146` A rule told twice: no static mutable data.
- `doc/development-guide.md:9` The same rule in the development guide.
- `doc/architecture.md:38-60` The project table names neither `CodeAlta.Pseudonymization` nor `CodeAlta.CodexAppServer`.

Change: Move the section on the desktop window to `doc/desktop.md`, behind a pointer that keeps its safety rules (always `--dev`, never exit the user's instance); link the rules of the development guide instead of restating them; add the two missing projects to `doc/architecture.md`.

Removes: about 1,600 words read by every session; rules told twice  
Adds: a pointer of a few lines; two rows of the project table

First step: Add `CodeAlta.Pseudonymization` and `CodeAlta.CodexAppServer` to the project table, with a link to `doc/fleet/architecture.md` for the fleet projects.

Verify: `AGENTS.md` under 1,600 words.; A session that works on the frontend still starts and exits the developer instance as the guide says (reader test).

Risks: A frontend session that skips the pointer drives the wrong window: the pointer keeps the rules that prevent it.

Disagreement: The fleet projects that one analyst found missing from `doc/architecture.md` are described in `doc/fleet/architecture.md`; only a link and two projects are missing.

## Considered and dropped

- Give `CodeModeRunRegistry` the clock of its store: The CI failure it explained was a race with the writes of a stopped run, fixed by `f535964b9` the next day; the clock cannot make the wake time null.
- Check what an agent does through one path instead of at each command: The checks at each command need what only the parsed command knows: the resolved MCP tool, the plugin's policy, the lineage of the target session. Security is also outside the scope of this review.
- Select the platform tests by `[TestClass]` or a test category because helper names enter the filter: The helper names match no test: the filter is `FullyQualifiedName~.<Name>.`, and no test name has such a segment.
- Mask secrets once, at the publish and journal boundary of `AgentSession`: Security is outside the scope of this review; the ten leak fixes came in one burst after the feature landed (2026-11-03 to 11-11), none since. Worth a security review of its own.
- Group the 63 host options into records with presets per host: `f411ce863` moved the drifted defaults into the host; grouping adds types without a defect that remains, and the desktop registration part is in F07 and F10.
- Split `SessionRuntimeService` (16,037 lines in 44 files) into services: The partial files already group by topic, and its fixes are about ordering inside one session, which one owner keeps right; a split would spread that order. Only the sandbox partials own fields of their own, if a later change needs them apart.

## Analyses

- Claude Opus 5.5 (`claude-code:opus@xhigh`, driver): `analysis/claude-opus-5-5.md`
- Gemini 4 Neon (`antigravity-acp:gemini-4-neon@high`, analyst): `analysis/gemini-4-neon.md`, `analysis/gemini-4-neon.review.md`
- GPT 6.2 Astra (`codex:gpt-6.2-astra@max`, analyst): `analysis/gpt-6.2-astra.md`
