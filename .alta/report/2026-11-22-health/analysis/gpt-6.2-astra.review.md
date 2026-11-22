# Adversarial review: gpt-6.2-astra

Reviewed the assigned candidates against `2a3fd8388` using source, git history and static counts. No build, tests or application launch.

### C02: confirm

Take **B**. I counted 8,958 words; activation appends the entire body but only the *names* of companion files (`src/CodeAlta.Catalog/Skills/SkillCatalog.cs:1013`). The canvas is introduced twice (`src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:274`, `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md:436`), and the developer guide already indexes contributions (`site/docs/plugins/developers.md:161`). Keep the workflow and point to the C# API lookup, the existing JavaScript references and the shipped samples. Ten more reference files would create ten more things to maintain.

### C03: reject

The command-line help promises two different operations: browse a catalog copy, with writes allowed to its cache, or explicitly opt in to running sessions on it (`src/CodeAlta/Desktop/DesktopCommandLine.cs:186`). A profile start supplies an owned host (`src/CodeAlta/Desktop/DesktopCommandLine.cs:499`); it is not a substitute for the first operation. The smaller registration set deliberately supplies a workspace over the copy (`src/CodeAlta/Desktop/DesktopApplication.cs:1859`), and the admission test even accepts browser-only startup (`src/CodeAlta.Desktop.Tests/DesktopWorkspaceTests.cs:94`). Unequal service counts do not establish drift. Deletion would retire a documented capability.

### C04: simplify

The three missed version bumps are real. Keep the read-only current-version path (`src/CodeAlta.Catalog/ApplicationDatabase.cs:337`); permission mode invalidates cached rows, whereas sandbox preserves them (`src/CodeAlta.Catalog/SessionJournalSqliteCache.cs:1632`, `src/CodeAlta.Catalog/SessionJournalSqliteCache.cs:1657`). Run the real session listing against a fixture of the latest shipped schema **with its actual owner version**; refresh the fixture when a new schema ships. Unlike starting with today's schema and dropping one known column (`src/CodeAlta.Tests/SessionJournalSqliteCacheTests.cs:1197`), that catches a missing bump from the shipped version without inspecting columns at every open.

### C06: simplify

Take the draft-local part of **A**. Both rename inputs call `App` setters on every edit (`src/CodeAlta/frontend/src/main.tsx:6533`, `src/CodeAlta/frontend/src/main.tsx:6689`). Let the existing `RenamePopover` own a draft keyed by target and pass its value on submit (`src/CodeAlta/frontend/src/RenamePopover.tsx:24`). Leave busy/conflict state with the mutation: the sidebar also consumes busy at `src/CodeAlta/frontend/src/main.tsx:6531`. This removes those keystrokes from `App`; neither a Settings host nor external stores are needed. The tab-switch trace measures a different interaction, not rename latency.

### C08: simplify

Delete the all-services-absent early return. A broker alone enables the command at `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:180`, but the return at `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:346` skips its policy at `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:375`. Cover that one service combination; the trade-off is a list copy in the no-services case. Splitting contributors is not free: command and policy allowlists recognize the **concrete built-in type** (`src/CodeAlta.LiveTool/AltaCommandRegistry.cs:49`, `src/CodeAlta.LiveTool/AltaCommandRegistry.cs:141`).

### C09: simplify

Take **A** for polling deadlines, not for every `UtcNow.Add…` expression. The two-second bound at `src/CodeAlta.Tests/AltaLiveToolTests.cs:7409` escapes the guard at `src/CodeAlta.Tests/TestTimeGuardTests.cs:118`; the wake time at `src/CodeAlta.CodeMode.Tests/CodeModeSleepTests.cs:100` is input data and must not scale. Match deadline assignments and loop bounds. For sleeps, `src/CodeAlta.Browser.Tests/BrowserDrawingTests.cs:260` already polls, so its delay is not a fixed cost per run. Replace blind waits with the state or completion of the operation being asserted: an unrelated next message cannot establish that the cancelled image save at `src/CodeAlta.Browser.Tests/BrowserAnnotationsTests.cs:615` finished without emitting anything.

### C11: simplify

Take **A** for serialization: move a service's attributes beside its DTOs on the same partial context. There are 1,021 attributes, one assembly-declared context (`src/CodeAlta/Desktop/Rpc/BootRpc.cs:5`, `src/CodeAlta/Desktop/Rpc/BootRpc.cs:151`), and typed consumers (`src/CodeAlta/Desktop/Rpc/CanvasesRpc.cs:180`); B adds resolver composition. Keep the explicit demo and fixture behavior: `labels.list` returns an empty collection while writes throw (`src/CodeAlta/frontend/src/demo-api.ts:281`); the project fixture explicitly selects the demo `reminder` instead of the shell fixture's implementation (`src/CodeAlta/frontend/src/projectFocus.neoastra.mount.ts:7`, `src/CodeAlta/frontend/src/settingsShell.neoastra.mount.ts:263`). Two wildcard exports do not make that choice. `e66f2a618` fixed a runtime client import, not a missing fixture export.

### C12: reject

There is no single downstream sink. Canonical events are already masked **before** entering memory history, durable storage or publication (`src/CodeAlta.Agent/Runtime/AgentSession.cs:3479`, `src/CodeAlta.Agent/Runtime/AgentSession.cs:3488`). The prompt suggestion reuses its masked value for a separate summary write (`src/CodeAlta.Agent/Runtime/AgentSession.cs:627`), and a permission request goes directly to a callback (`src/CodeAlta.Agent/Runtime/AgentSession.cs:665`). Masking again in `Publish` and append cannot replace either. One cited fix, `4ee5b1442`, was in node telemetry, outside both sinks.

### C14: reject

`CodeAltaHostOptions` is public (`src/CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs:15`); full trust, live policy and fixed fallbacks already have one precedence rule (`src/CodeAlta.Orchestration/Hosting/CodeAltaHost.cs:693`). CLI deliberately chooses full trust, while Node takes a live fleet policy (`src/CodeAlta.Cli/CliHost.cs:173`, `src/CodeAlta.Node/NodeHost.cs:260`). The cited fix already derives review availability from the command allowlist. Moving RPC setup would still require the host, current space and epoch (`src/CodeAlta/Desktop/DesktopApplication.cs:1391`). Neither a remaining wrong default nor a dependency removed by the proposed catalog justifies the bundled redesign.

### C15: reject

The November 12 failure has a diagnosed cause and a November 13 fix: `f535964b9` drained queued writes on shutdown. Disposal now flushes the writer (`src/CodeAlta.Orchestration/Runtime/Scripts/CodeModeRunRegistry.cs:829`); a regression queues a slow write and checks that no call is left in flight (`src/CodeAlta.Orchestration.Tests/CodeModeRunStoreTests.cs:194`). The card's wake time is recorded *before* the timer starts (`src/CodeAlta.Orchestration/Runtime/Scripts/CodeModeRunRegistry.cs:283`); passing the 500 ms mark skips the delay, not the card's wake time. A fake clock would not have fixed the lost write.

### C17: confirm

The missing hardware check was **my finding**. Take **B**, explicitly selecting `Dot_EveryKernelOfThisMachine_EqualsTheScalarReference`. I applied the workflow predicate to tracked test sources: **0 of 157** clauses match that method (`.github/workflows/ci.yml:68`, `src/CodeAlta.Tests/Search/SearchVectorTests.cs:17`). It compares the kernels the actual CPU supports to the scalar result (`src/CodeAlta.Tests/Search/SearchVectorTests.cs:37`); filtering for `[TestClass]` alone would still miss its file. The other analyst's helper-name observation did not change my mind: `Recorder`, `ManualTimer` and `SamplePlugin` add filter text, but I found no matching test class or namespace segment to substantiate extra runner time.

### C18: reject

The checks consume different facts. The session interceptor sees the outer tool call; the MCP command obtains the **resolved tool specification** before asking the same session guard (`src/CodeAlta.Plugin.Mcp/McpCommandFactory.cs:988`, `src/CodeAlta.Plugin.Mcp/McpCommandFactory.cs:999`, `src/CodeAlta.LiveTool/PluginAltaCommandContributor.cs:158`). Handing off work checks the receiving session's lineage (`src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:5706`), not just the caller's arguments. `d63063583` routed script calls through the session tool; it did not make those downstream facts available at entry. Keep the checks where their inputs exist.

### C20: simplify

Take the diagnostic, not the new retry. The failing assertion precedes the deliberately held index (`src/CodeAlta.Catalog.Tests/ReviewServiceTests.cs:377`, `src/CodeAlta.Catalog.Tests/ReviewServiceTests.cs:385`). Failed `add`, failed `write-tree` and an I/O exception all lose their cause (`src/CodeAlta.Catalog/Reviews/ReviewTextSource.cs:220`, `src/CodeAlta.Catalog/Reviews/ReviewTextSource.cs:234`). Have the internal attempt retain the failed stage and bounded diagnostic for the assertion/log; keep the public nullable return. There is no evidence that `File.Copy` failed in that run.

### C21: simplify

I counted 1,605 of 3,041 root words in the window procedure starting at `AGENTS.md:73`. Keep the live-build warning at `AGENTS.md:48`; move the procedure to the existing developer-instance section (`src/CodeAlta/README.md:57`), not an architecture document. Link rather than repeat rules such as the static-state prohibition (`AGENTS.md:146`, `doc/development-guide.md:9`). Correct “everything else is shared, headless libraries” (`doc/architecture.md:3`); link the fleet roles and Codex entry already written (`doc/fleet/architecture.md:34`, `doc/providers.md:16`), and name the pseudonymization library at the provider boundary (`doc/architecture.md:68`).

## What the list missed: the snapshot retry can change what is reviewed

C20 discusses a snapshot that fails; the existing retry can **succeed with a different tree**. The first attempt copies the real index; the retry starts empty and runs `git add -A` (`src/CodeAlta.Catalog/Reviews/ReviewTextSource.cs:199`, `src/CodeAlta.Catalog/Reviews/ReviewTextSource.cs:215`). A tracked path under an ignore rule, like `bin/tool.sh` in the repository's own fixture (`src/CodeAlta.Catalog.Tests/CheckoutUsageTests.cs:345`), stays tracked in the first index but is omitted from the empty one. Two retry snapshots can therefore miss an edit when approval compares their IDs (`src/CodeAlta.Catalog/Reviews/Reviews.cs:245`). Preserve the tracked set on retry; extend the existing equality check at `src/CodeAlta.Catalog.Tests/ReviewServiceTests.cs:387` with that fixture case.

**First:** C04 (catch the next broken upgrade), C02 (remove repeated activation text), C17 (cover CPU-specific arithmetic with one filter entry). **Stop:** C18 (the proposed single check lacks the facts the downstream checks use).
