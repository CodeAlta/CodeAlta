# Candidates

Every finding of every analysis, merged. Findings about the same code and cost are one candidate; when the cures differ, each cure is listed. No author, nothing dropped. Citations are `path:line` at `2a3fd8388`.

### C01: Keep automation state small enough for its own reader
- **Cost:** a valid automation history can grow past the size the reader accepts, and the next start then loads empty, paused state: all automations paused, no run history, no trigger watermarks.
- **Evidence:** dry runs keep up to 50 call descriptions of 2,000 characters per run (`src/CodeAlta.Automations/AutomationState.cs:51`, `src/CodeAlta.Automations/AutomationService.Autonomy.cs:132`); retention counts runs, not bytes (`src/CodeAlta.Automations/AutomationState.cs:313`); `Save` writes everything (`src/CodeAlta.Automations/AutomationState.cs:467`); the reader rejects more than 8 MiB and substitutes paused state (`src/CodeAlta.Automations/AutomationState.cs:195`, `src/CodeAlta.Automations/AutomationState.cs:219`).
- **Change:** a save never writes what the reader refuses: over budget, drop the oldest completed history first; never active runs, the pause choice or trigger cursors.
- **Removes / adds:** removes the write-succeeds-read-empties outcome; adds a size check and a round-trip test.

### C02: Shorten the plugin-runtime skill that each activation loads whole
- **Cost:** `SKILL.md` is 8,958 words (about 12,000 tokens per activation), four times the next built-in skill; it defines a canvas twice and restates the plugin developer guide, with which it shares 10 of its 19 commits.
- **Evidence:** `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:274` and `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:436` (two definitions of a canvas); contribution points, triggers, credential providers and tool hooks told in both `SKILL.md:116-149`, `SKILL.md:509-627` and `site/docs/plugins/developers.md:161-395`; the skill already has `references/` read on demand.
- **Change, cure A:** `SKILL.md` keeps the work loop and an index of contribution points; the detail of each point moves to its own reference file; the two canvas sections merge. **Cure B:** delete the prose that `alta plugin api` and `site/docs/plugins/developers.md` already give, and keep a short workflow guide with pointers to `alta plugin api <type>` and `samples/`.
- **Removes / adds:** removes about 7,000 words per activation; A adds about ten reference files (moved text), B adds pointers.

### C03: Delete the desktop window that runs without a host
- **Cost:** every desktop RPC service is registered twice in `DesktopApplication.cs`, once for a mode without a host that only test-harness flags reach, and the two lists have drifted (81 against 55 registrations).
- **Evidence:** `src/CodeAlta/Desktop/DesktopApplication.cs:129` takes the path without a host only when `options.Owned` is null; normal starts set it (`src/CodeAlta/Desktop/DesktopCommandLine.cs:476`, `src/CodeAlta/Desktop/DesktopCommandLine.cs:499`); null only for `--data-root` without `--allow-owned-host` (`src/CodeAlta/Desktop/DesktopCommandLine.cs:306-334`); its registrations at `src/CodeAlta/Desktop/DesktopApplication.cs:1858-1913`; tests use it (`src/CodeAlta.Desktop.Tests/DesktopWorkspaceTests.cs:121`).
- **Change:** remove `RunAsync`, the "in-development" boot state, the parsing branch without a host and the stub constructors that only it uses; move its tests and the screenshot recipe to `--profile` or `--allow-owned-host`.
- **Removes / adds:** removes about 135 lines and one way to start; adds nothing.

### C04: Run the column migration of the session list on every open, from one list of columns
- **Cost:** three times in six days a column added to the session list broke listing sessions on existing databases ("no such column"), because the migration runs only when `SchemaVersion` changes and the version was not raised.
- **Evidence:** `src/CodeAlta.Catalog/ApplicationDatabase.cs:345` skips the migration at the same version; nine `Add…ColumnAsync` methods (`src/CodeAlta.Catalog/SessionJournalSqliteCache.cs:1599-1607`, bodies `:1618-1785`); fixes `77e0346bd`, `c3a5461df`, `8f15ae2f5` only raised the version; each new session fact is a column (`doc/development-guide.md:7`).
- **Change:** declare the columns once; at each open read `pragma_table_info` and add the missing ones; keep the version for changes a column list cannot express.
- **Removes / adds:** removes nine near-identical methods (about 170 lines) and the rule "raise the version for a column"; adds a column table and a loop.

### C05: Do not grant an automation occurrence that the node could not record
- **Cost:** when the claim write fails, two successive instances of a node can each grant the same scheduled occurrence, and both can start real work.
- **Evidence:** `TryClaim` saves and returns true whatever the save did (`src/CodeAlta.Automations/AutomationClaims.cs:153`); `Save` swallows I/O errors (`src/CodeAlta.Automations/AutomationClaims.cs:253`); an unreadable ledger reads as empty (`src/CodeAlta.Automations/AutomationClaims.cs:135`, `:239`); the grant reaches `BeginAsync` (`src/CodeAlta.Automations/AutomationService.cs:797`); `09ee9a3ef` fixed the same assumption for the hourly counter.
- **Change:** grant only after the claim is durable; tell "storage unavailable" from "already claimed"; skip or defer with the real reason.
- **Removes / adds:** removes a silent grant; adds a claim outcome and an error-path test.

### C06: Move the state of features out of the `App` component of `main.tsx`
- **Cost:** `App` is one 6,741-line component (`main.tsx:550` to `:7291`) holding the state of dozens of features; `main.tsx` had 144 commits in four weeks (38 fixes); four text fields re-render the whole window on each keystroke, against `doc/development-guide.md:31`; a tab switch renders all of `App` (task `wtvm`).
- **Evidence:** `src/CodeAlta/frontend/src/main.tsx:550`; keystroke state `main.tsx:6514`, `:6533`, `:6689`, `:6980`; dialog state at `main.tsx:1099-1160`, `:6199-6204`; 61 props into `SessionWorkspace` (`main.tsx:7547`); `.alta/tasks/2026-11-19-render-the-session-panes-once-for-a-tab-switch-wtvm.md`.
- **Change, cure A:** start with the smallest surfaces: each dialog keeps its own draft and busy state (`RenamePopover` first), then the delete, archive and move dialogs. **Cure B:** start with the largest: a `SettingsHost` owning the Settings overlay state, then other overlays, with narrow external stores (`useSyncExternalStore`) for the workspace and the active tab.
- **Removes / adds:** removes dozens of `useState` and effects from `App` and whole-window renders; adds components or hooks in the existing feature folders.

### C07: Delete the TUI's lossy copy of a provider definition
- **Cost:** saving a change to one provider in the TUI can remove settings of another, untouched provider from the shared `config.toml`: the TUI copier omits six properties the Catalog copy keeps.
- **Evidence:** the TUI copier (`src/CodeAlta.Tui/ViewModels/ModelProviderEditorItemViewModel.cs:377`) against `src/CodeAlta.Catalog/CodeAltaConfigStore.cs:3920` lacks `ContextSize`, `Sandbox`, `CodeMode`, `Server`, `Request`, `ModelRequest`; the dialog rebuilds every provider (`src/CodeAlta.Tui/Views/ModelProvidersDialog.cs:1459`); the merge removes dropped modeled keys (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:2222`); `77da0870b` had to update both copies.
- **Change:** use `CodeAltaConfigStore.CloneProviderDefinition` in both editor paths.
- **Removes / adds:** removes a 64-line copier; adds one preservation test.

### C08: Split `BuiltInAltaCommandContributor` into one contributor per domain
- **Cost:** 39 partial files (about 25,000 lines; 63 commits and 15 fixes in four weeks) form one class whose command creation and policy list each repeat a check of 20 optional services, and the two have drifted: the early return of the policy list omits the credential broker.
- **Evidence:** `src/CodeAlta.LiveTool/AltaCommandRegistry.cs:13-20` already takes several `IAltaCommandContributor`; `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:157-316` (commands) and `:324-379` (policies); the early return at `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:346` does not test `CredentialBroker`, added at `:375`.
- **Change:** each self-contained partial becomes an `IAltaCommandContributor` that checks its service once for its commands and its policies; first the credentials one.
- **Removes / adds:** removes the repeated 22-flag router and the drift; adds small contributor classes on the existing interface.

### C09: Time in tests: unscaled deadlines and fixed sleeps
- **Cost:** 33 test deadlines written `UtcNow.AddSeconds(…)` escape `TestTime` and its 4× CI factor (some of 2 s), and three browser test files sleep 61 times, 14.2 s per run, each "act, sleep, assert once".
- **Evidence:** the guard matches `UtcNow + TimeSpan.From…` but not `UtcNow.Add…(` (`src/CodeAlta.Tests/TestTimeGuardTests.cs:118`); `src/CodeAlta.Tests/AltaLiveToolTests.cs:7408`, `src/CodeAlta.Browser.Tests/BrowserPluginTests.cs:413`; sleeps `src/CodeAlta.Browser.Tests/BrowserDrawingTests.cs:175`, `src/CodeAlta.Browser.Tests/BrowserAnnotationsTests.cs:426`, `:615` (2 s); `doc/development-guide.md:39-40`.
- **Change, cure A:** convert the 33 deadlines to `TestTime` and extend the guard's pattern to `UtcNow.Add…(`. **Cure B:** convert them without extending the guard. **Both:** a helper that polls the page state until it holds replaces the sleeps; checks of absence wait for the next message the page sends.
- **Removes / adds:** removes 33 unscaled limits and about 14 s per run; adds a helper (and, with A, one pattern of the guard).

### C10: Hold the configuration file lock while a structured edit checks its revision
- **Cost:** two cooperating writers of one config can both succeed from the same revision, the second whole-provider-set save erasing the other's edit.
- **Evidence:** the text editor holds `LockGlobalConfig()` across check and save (`src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:124`); the structured path only holds the service's `_gate` (`src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:927`, `:943`) and writes its captured list later (`:684`); the store takes the named lock only at save (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:1902`).
- **Change:** hold the existing file mutex from the revision check to the saved revision; release it before provider preparation.
- **Removes / adds:** removes the weaker of two implementations of one guarantee; adds a two-writer test.

### C11: Register a desktop RPC service in one place instead of five
- **Cost:** a new RPC service edits its own file and five shared ones: `DesktopApplication.cs` (twice), the 1,021 `[JsonSerializable]` lines of `BootRpc.cs`, `main.tsx`, a stub in `demo-api.ts` and a 60-name export list of a test fixture; `BootRpc.cs` (109 commits in four weeks, all in its attribute list) and `demo-api.ts` (45, 38 of them with `BootRpc.cs`) are hotspots for that reason.
- **Evidence:** commits `c708f677b` and `3e1ce7ef6`; `src/CodeAlta/Desktop/Rpc/BootRpc.cs:151` to `:1173`; `src/CodeAlta/Desktop/DesktopApplication.cs:1391`, `:1903`; `src/CodeAlta/frontend/src/demo-api.ts:281`; `src/CodeAlta/frontend/src/projectFocus.neoastra.mount.ts:7`; a missed fixture export broke the test bundles (`e66f2a618`).
- **Change, cure A:** each `*Rpc.cs` declares `partial class DesktopJsonContext` with the attributes of its own types. **Cure B:** each RPC file has its own `JsonSerializerContext`, combined at start. **Also:** fixtures re-export `demo-api` with `export *` and override; `demo-api` answers `unavailable` for any service it does not fake through one helper.
- **Removes / adds:** removes the central attribute list, the export list and most stubs; adds one footer per RPC file or one combine call, and one helper.

### C12: Mask secrets at the single publish and journal boundary of `AgentSession`
- **Cost:** secret masking needed ten leak fixes in nine days (2026-11-03 to 11-11), each a new output path that forgot to mask; `AgentSession` still masks at eight call sites.
- **Evidence:** commits `5aafcb4d9`, `874115cd6`, `4ee5b1442`, `7c7f38abb`, `e1bd24e5e`, `48589b22a`, `6dc574ed0`, `017a28fc7`, `cf4f147eb`; call sites `src/CodeAlta.Agent/Runtime/AgentSession.cs:423`, `:627`, `:655`, `:664`, `:1249`, `:1475`, `:1525`, `:3477`.
- **Change:** `Publish` and the journal append mask every event as their last step; streaming deltas keep `MaskStreams`, tool results keep `MaskResult`; the caller-side `Masked(…)` calls go.
- **Removes / adds:** removes four ad-hoc call sites and the "forgot to mask" class; adds one pass in the sink.

### C13: Stop saving runtime provider defaults when adding a model server
- **Cost:** "Use in sessions" of a model server on this computer turns the absent settings of every other provider into explicit values in `config.toml`, even with one writer.
- **Evidence:** `src/CodeAlta/Desktop/ModelServers/DesktopModelServers.cs:263` calls `SaveLocalProvider`, which reads the completed definitions and writes them back (`src/CodeAlta/Desktop/DesktopApplication.cs:909`, `:917`); that read fills defaults (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:1879`, `:3026`); the form avoids this on purpose (`src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:976`).
- **Change:** apply the server-provider edit to the raw definitions through the store's update operation; complete definitions only for the runtime.
- **Removes / adds:** removes a completed-model-to-file round trip; adds one fixture.

### C14: Group the host options and take the RPC registration out of `RunOwnedAsync`
- **Cost:** `CodeAltaHostOptions` has 63 `init` properties set separately by four executables, and defaults drift between hosts (`f411ce863` fixed three such defaults at once); `DesktopApplication.RunOwnedAsync` is one 1,388-line method; `CodeAltaHost.cs` had 26 fixes and `DesktopApplication.cs` 24 in four weeks.
- **Evidence:** `src/CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs:96-115` (flags that imply others); callers `src/CodeAlta/Desktop/DesktopApplication.cs:561`, `src/CodeAlta.Node/NodeHost.cs:178`, `src/CodeAlta.Cli/CliHost.cs:162`, `src/CodeAlta.Tui/App/CodeAltaOwnedServices.cs:191`; `src/CodeAlta/Desktop/DesktopApplication.cs:324-1711`.
- **Change:** group the options into a few records with safe defaults per kind of host; one permission policy value instead of five interacting flags; the 81 registrations move to a `DesktopRpcCatalog.Register`.
- **Removes / adds:** removes about 30 per-host lines and conflicting flag combinations; adds four or five option records and one helper.

### C15: Give `CodeModeRunRegistry` the clock of its store
- **Cost:** `ARunCodeAltaClosesWhileItSleeps_IsInterrupted_AndResumesByItselfOnceItWakes_Once` failed on Ubuntu CI (run 43323247389, 2026-11-12) and passed on rerun; the test depends on a 500 ms wall-clock window.
- **Evidence:** wake time 500 ms ahead (`src/CodeAlta.Orchestration.Tests/CodeModeRunStoreTests.cs:219`), failed assertion `:232`; the store takes a `TimeProvider` (`src/CodeAlta.Orchestration/Runtime/Scripts/CodeModeRunStore.cs:130`), the registry uses `DateTimeOffset.UtcNow` (`src/CodeAlta.Orchestration/Runtime/Scripts/CodeModeRunRegistry.cs:319`).
- **Change:** the registry takes the store's `TimeProvider`; the test advances a fake clock.
- **Removes / adds:** removes a wall-clock window; adds one parameter.

### C16: Give Skills settings the same inherited project scope as a session
- **Cost:** for a sub-project, Settings and the session disagree: a repository skill offered to the session is missing from Settings, and a skill the parent disabled shows enabled.
- **Evidence:** session discovery takes inherited roots and the union of disabled names (`src/CodeAlta.Catalog/Skills/SkillCatalogQuery.cs:168`); the Settings listing takes the child's disabled names and one root (`src/CodeAlta.Catalog/Skills/SkillManagementService.cs:92`, `:149`); the RPC passes `SkillFolders`, not `EffectiveSkillFolders` (`src/CodeAlta/Desktop/Rpc/SkillsRpc.cs:517`, `src/CodeAlta.Catalog/ProjectDescriptor.cs:201`); `site/docs/monorepos.md:95` documents inheritance.
- **Change:** share the computation of the effective read roots and disabled names; keep Settings' diagnostics and child-only writes.
- **Removes / adds:** removes a second definition of a project's discovery scope; adds a sub-project fixture.

### C17: Select the platform tests of CI by something other than a grep of class names
- **Cost:** the Windows and macOS jobs select test classes by grepping files that mention `OperatingSystem.Is…()`: 13 helper class names are pulled in, which match unrelated test classes and send them to the slow runners, while a hardware-dependent test (`SearchVectorKernelTests`) is never selected there.
- **Evidence:** `.github/workflows/ci.yml:68-71`; helper names such as `Recorder`, `ManualTimer`, `SamplePlugin` matched through `FullyQualifiedName~.<Name>.`; `src/CodeAlta.Tests/Search/SearchVectorTests.cs:20`, `:37` selects kernels by CPU flags; `src/CodeAlta.Search/SearchVectorKernels.cs:96`.
- **Change, cure A:** extract only classes marked `[TestClass]`, or a `[TestCategory("PlatformSpecific")]`. **Cure B:** add the search kernel test to the filter by name, and nothing more.
- **Removes / adds:** A removes the helper matches from every platform run; B adds one name.

### C18: Check what an agent does through one path, not at each command that can act
- **Cost:** in three weeks five fixes closed a path by which a session acted without the checks of its tool calls: `alta mcp tool call`, plugin commands, Code Mode calls of `alta`, exec scripts under Claude Code's Auto mode, children and later turns of a run.
- **Evidence:** commits `f234ae8a4`, `beda233d8`, `874115cd6`, `db09b80bd`, `d63063583`; guard calls at `src/CodeAlta.LiveTool/AltaCommandRegistry.cs:62`, `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:5699`, `src/CodeAlta.LiveTool/PluginAltaCommandContributor.cs:159`, `src/CodeAlta.Plugin.Mcp/McpCommandFactory.cs:489`, beside the interceptor (`src/CodeAlta.Orchestration/Runtime/SessionToolGuards.cs:172`).
- **Change:** a command an agent runs is a call of its session's tool, checked once by the interceptor, as `d63063583` did for Code Mode; the per-command guard calls go.
- **Removes / adds:** removes the guard calls spread over the commands; adds nothing if the interceptor sees the command line.

### C19: Retire the raw skill-scanning branch that only its tests call
- **Cost:** five production files (1,074 lines) and five test files (804 lines, 25 tests) scan, read metadata and parse ignore rules with no product caller, and give a different answer than the live catalog to "what is a valid skill" (they reject `triggers` and `version`).
- **Evidence:** `src/CodeAlta.Catalog/Skills/RawSkillCandidateReader.cs:65` says discovery does not use it; references to `RawSkillCandidateReader`, `SkillCandidateMetadataReader`, `SkillMetadataParser`, `SkillIgnoreSourceReader`, `SkillIgnoreSourceParser` only in themselves and their tests; the live catalog parses on its own (`src/CodeAlta.Catalog/Skills/SkillCatalog.cs:408`, `:537`, `:42`) while `src/CodeAlta.Catalog/Skills/SkillMetadataParser.cs:212` rejects its fields; unchanged since 2026-09-26.
- **Change:** confirm no outside consumer; delete the ignore reader and parser with their tests, then the candidate scan and metadata pair with theirs.
- **Removes / adds:** removes 10 files and 1,878 lines; adds nothing.

### C20: Say why `GitSnapshot.WriteTreeAsync` failed instead of returning null
- **Cost:** a review snapshot that fails returns null with no reason: `ASnapshot_WhoseIndexGitCannotReplace_IsWrittenThroughAnIndexGitCreates` failed on Windows CI (run 43251505041) on its first, unconstrained snapshot, and the log says nothing of why.
- **Evidence:** `src/CodeAlta.Catalog.Tests/ReviewServiceTests.cs:378` (`Assert.IsNotNull(expected)`); `src/CodeAlta.Catalog/Reviews/ReviewTextSource.cs:207-237` discards the git error and I/O exceptions; `File.Copy(seed, index)` at `src/CodeAlta.Catalog/Reviews/ReviewTextSource.cs:217`.
- **Change:** carry the git exit code, stderr or exception to the caller and the test; fall back to an index without seed when the copy fails.
- **Removes / adds:** removes a silent null; adds a reason and a fallback.

### C21: Trim `AGENTS.md` and complete the projects of `doc/architecture.md`
- **Cost:** `AGENTS.md` loads 3,041 words in every session, 1,605 of them on driving the desktop window, and repeats ten rules of `doc/development-guide.md`; `doc/architecture.md` lists none of the Fleet, Server, Node, Pseudonymization and Codex App Server projects and names three executables of five.
- **Evidence:** `AGENTS.md:73-133`; repeated rules such as `AGENTS.md:146` and `doc/development-guide.md:9`; the project table `doc/architecture.md:38-60` and `doc/architecture.md:3`; the fleet projects are described in `doc/fleet/architecture.md`.
- **Change:** move the desktop-window section to `doc/desktop.md` behind a three-line pointer; link the repeated rules instead of restating them; add the missing projects or a link to `doc/fleet/architecture.md`.
- **Removes / adds:** removes about 1,800 words loaded per session; adds a few table rows.
