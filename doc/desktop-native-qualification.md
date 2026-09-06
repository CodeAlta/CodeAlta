# Desktop native qualification

The production entrypoint is now [`src/CodeAlta`](../src/CodeAlta/README.md): package `CodeAlta`,
assembly/command `alta`, ordinary `Exe`, published centrally pinned NeoAstra **0.1.0**. It is an
**in-development boot surface**, not an agent backend. The TUI remains `CodeAlta.Tui` / `altatui`.
Desktop declares six non-musl RIDs; TUI retains eight. No profile/provider/plugin startup is enabled.

## Preserved M0 evidence

The throwaway source location `src/prototypes/CodeAlta.Desktop.Probe` was retired in M1b by moving
tracked files only to [`CodeAlta.Desktop.Tests/NativeSmoke`](../src/CodeAlta.Desktop.Tests/NativeSmoke/README.md).
Ignored original prototype `node_modules`, `bin`, `obj` and local evidence were left untouched;
only a preservation `.gitignore` remains tracked at the retired location. The fixture
retains the historical `CodeAlta.Desktop.Probe` package/command to keep reproduction and evidence
comparable. It is outside the release solution and is never included in the production package.

The [historical evidence ledger](../src/CodeAlta.Desktop.Tests/NativeSmoke/evidence/README.md) records
actual Windows x64 native/tool execution on 2026-09-05: Windows **10.0.26200**, .NET **10.0.11**,
WebView2 **152.0.4191.62** / Chromium **152.0.7977.76**. Both Exe and WinExe test packages preserved
Common Controls v6 manifests and redirected CLI output; only Exe is the current desktop default.
The fixture validated generated RPC, post-open channel abort and iterator-break disposal (2/2),
relative lazy JS/CSS/SVG, CM styles/edit transaction, Radix portal/focus, strict Mermaid rendering,
native owned TaskDialog Cancel/Accept, asynchronous close cancellation and joined cleanup.
It found 100 hash-verified assets (3,823,561 bytes) and six native libraries. No other RID was run.

## M1b qualification boundary

See [test commands](../src/CodeAlta.Desktop.Tests/README.md) for managed and explicit native checks.
Native scripts create unique task-owned temporary roots, install only to a temporary local tool
path, launch outside the checkout, remove `NEOASTRA_*` / `WEBVIEW2_*` environment overrides and
restrict child PATH to Windows system directories (no Node or dev server). Evidence is retained.

Production boot and fixture qualification are distinct: the fixture's fake hello/stream/visual/dialog
operations are not production features. Managed lifecycle source guards are not fault-injected
native teardown tests, and native-library packaging is not evidence that other architectures run.
Shared-root ownership, production admission/shutdown, backend projections, renderer recovery and
feature parity belong to later milestones. Current automation requires a new explicitly injected
data root and must never launch against the active/default profile.

Remaining release gates include five native RID hosts, interactive console/Explorer behavior,
accessibility/IME/performance, shared-host lifecycle and complete third-party distribution notices.
The preserved dependency/license review is point-in-time evidence, not a release notice inventory.

### Packaging details discovered in M1b

- On SDK 10.0.303, `RuntimeIdentifiers` also triggers RID-specific tool-package fan-out. Desktop
  uses the supported `PackAsToolShimRuntimeIdentifiers` six-RID list instead: the SDK derives its
  restore RIDs from it, and packs one framework-dependent `CodeAlta` tool with six standard shims.
  NeoAstra supplies the six native binaries. TUI's existing eight-RID packaging is unchanged.
- NeoAstra's consumer targets normally copy generated TypeScript as a runtime artifact. Desktop
  removes only that build-only source item after collection and excludes publish symbols; generated
  JSON manifest/schema and manifest-backed assets remain packaged. Package inspection checks both
  absence of sources/maps/terminal/host/fixture assemblies and presence of runtime contract metadata.
- Desktop output/intermediate paths are now `bin/desktop` / `obj/desktop`, with explicit exclusion
  of all `bin`/`obj` source globs. Initial M1b verification used the old default `bin/Release` and
  `obj/Release` paths before this isolation correction; those ordinary SDK builds may have updated
  overlapping pre-rename output files. No ignored directories were manually removed or restored.
- The production native script verifies a real window/library/profile and successful native close,
  not the rendered boot DOM. Actual DOM/RPC/CM/Radix/Mermaid/dialog assertions remain in the isolated
  M0 fixture. Managed tests separately cover the production boot projection and generated JSON.

### Final M1b recovery verification — 2026-09-05

All commands below completed on the recovered filesystem, without replaying the failed prior
session. Local evidence is retained under
`%TEMP%/codealta-m1b-recovery-1697d64f1c964aa9b70645afc91d90db/` (not a portable CI artifact).
Build/test/script output was redirected to files and only bounded summaries inspected.

| Check | Actual result | Evidence relative to that root |
| --- | --- | --- |
| SDK item/output evaluation | `bin/desktop`, `obj/desktop`; 4 compile items, no old output resources/items. Excluding `frontend/node_modules/**` and `frontend/dist/**` reduced default `None` items from 10,719 to 13, and evaluation output from 11,314,627 to 14,827 bytes. | `evaluation.json`, `evaluation-after.json` (inspect counts, not full dumps) |
| Glob regression | New structural regression failed before the fix, passed afterward. | `glob-regression-before.log`, `desktop-tests-final.log` |
| Release solution build | Passed, 0 warnings/errors. | `solution-build.log` |
| Release solution tests | 1,821 passed, 2 skipped: explicit desktop-native opt-in and existing Catalog incremental-search skip. Desktop has 22 managed passes. **Unfiltered and not profile-isolated; see the safety correction below.** | `solution-tests.log` |
| Frontend tests | Production 1 passed; migrated fixture 3 passed. Package builds also perform strict TS/Vite builds. | `frontend-tests.log`, `fixture-frontend-tests.log`, `package/pack.log`, `package/fixture/pack.log` |
| `Verify-DesktopPackage.ps1 -EvidenceRoot <root>/package` | Passed restore, fresh isolated pack, local-only tool install, standalone and installed native boot/CLI/close, then the same migrated M0 fixture qualification. | `package-qualification.log`, `package/` |
| Lunet site build | Passed. | `site-build.log` |
| Tracked diff whitespace check | Passed. | `diff-check.log` |

#### Full-suite safety correction

The exact full-suite invocation from the repository root was:

```powershell
dotnet test src/CodeAlta.slnx -c Release --no-build --no-restore *> "$root/solution-tests.log"
```

Here `$root` was the evidence directory above, **not a redirected user profile**. No test filter,
OS-level profile isolation, `SpecialFolder.UserProfile` redirection, or injected global roots were
applied to this invocation. Output redirection does not isolate test filesystem access. The passing
result establishes test outcomes only; it must not be cited as a profile-safe full-suite run.

Source-only follow-up confirmed these exposure paths without inspecting user contents:

- `CodeAltaShellControllerTests.OpenFolderAsync_TildePath_ExpandsHomeDirectoryBeforeUpsert`
  (`src/CodeAlta.Tests/CodeAltaShellControllerTests.cs:618-647`) resolves the real
  `SpecialFolder.UserProfile`, creates `codealta-open-tests/<guid>` beneath it, and deletes the GUID
  directory in `finally`. This test was not excluded or skipped, so the run performed that real
  profile write/cleanup. The parent `codealta-open-tests` directory may remain; its prior existence
  and current contents were not inspected, and no follow-up cleanup was attempted.
- `SystemPromptBuilder_CodeFormatsPathsInGeneratedMarkdown`
  (`src/CodeAlta.Orchestration.Tests/SystemPromptInfrastructureTests.cs:211-249`) omits explicit
  user prompt roots. The run therefore retained the path that can discover/read `~/.alta/prompts`.
- `AgentInstructionTemplateProvider.CreateDiscoveryContext`
  (`src/CodeAlta.Orchestration/Runtime/AgentInstructionTemplateProvider.cs:179-192`) explicitly uses
  the real `SpecialFolder.UserProfile` even with a supplied `GlobalRoot`, retaining user-common
  `~/.agents/skills` discovery. Actual user-file reads and their extent were not instrumented;
  absence of such reads cannot be claimed.

This was an execution safety gap. The full suite should have remained deferred pending audited
exclusions or genuine profile/discovery isolation. It was not rerun after the parent raised the
issue. Do not repeat it on the active profile. The explicit task-owned roots and sanitized child
environment in the separate native qualification scripts do **not** retroactively isolate this
managed full-suite invocation.

The production package contains **3 hash-verified assets (211,086 bytes)**, **six SDK shims**,
**six native binaries**, runtime JSON contracts, and no build-only TS/maps/PDBs, TUI/host or fixture
assemblies. Package SHA-256:
`5862FBF0E02A4A0109D3EB4E24CA1F2C4C9CD5A81AABED2D126FAE21B89763E9`.
See `package/package-inspection.log` and `package/{standalone,installed}/verification.log`.
Both Exe apphost and installed shim retain subsystem 3 and the Common Controls manifest.
The installed boot loaded the native library from its own temporary `.store`, showed
`CodeAlta - in development`, and returned 0 after native WM_CLOSE with empty stderr.

The migrated fixture again verified **100 assets (3,823,561 bytes)**, generated RPC, channel
enumerator disposal **2/2**, local lazy assets/CM/Radix/Mermaid, native TaskDialog Cancel/Accept,
asynchronous canceled close, approved close and drained cleanup. See
`package/fixture/{standalone,installed}/smoke.stdout.log` and `package/fixture/package-inspection.log`.
Its installed native library also came from its own temporary `.store`.

The prior session's reported 346,222,239-byte serialization failure was **not reproduced or
diagnosed**. Excessive SDK default frontend item enumeration was confirmed and corrected, but
is not proof of that failure's cause. No output-path/build/pack blocker remains in these runs.
Direct recovery inspection/editing did not access the original repository `.alta/config.toml`,
`.alta/mcp.json`, gzip artifact, old desktop output residue or retired prototype artifacts. This
does not establish that the unrestricted full suite avoided user-profile reads/writes; see the
safety correction above. The fixture migration preserves 22 files exactly;
only its README changed to describe test ownership. Plan/parity integration and commits remain
the driving parent's responsibility. Native evidence remains Windows x64 only, with the boot DOM
and other release gates explicitly unqualified as described above.

## M2 source-only test-isolation audit (2026-09-05)

**The three historical cases above are not an exhaustive exclusion list.** A read-only child audit,
with parent spot-checks of MCP discovery, both ancestor walkers and live-tool skill queries,
identified additional reachable profile paths. This audit ran no tests, providers, application
startup or profile inspection. It establishes source behavior, not which user files were accessed
during the historical full-suite run. Constructing a host/catalog/request alone does not prove discovery.

| Reachable path | Source evidence and actual fixture invocation | Qualification consequence |
| --- | --- | --- |
| MCP global discovery and writability probes | `src/CodeAlta.Plugin.Mcp/McpConfigDiscovery.cs:7-21,34-44,53-96,127-164` always includes global configuration. `McpRuntimeServiceTests.SearchDescribeAndCall_UseStdioAndApplyToolPolicy` calls `SearchToolsAsync` without `UserHomeDirectory`; `McpManagementServiceTests.SetServerEnabledAsync_WritesPolicyAndRefreshesSnapshot` and `AddOrUpdateServerAsync_WritesJsonConfigAndRefreshesSnapshot` refresh discovery; the two `McpConfigTests.StatusLabel_*` cases call `RefreshSnapshot` without an injected home. | Existing global config is read and opened with read/write access (not proof of content modification). If absent, discovery conditionally creates/deletes `.codealta-write-test-<guid>` in `.alta` or its parent. Which branch occurred historically is unknown. Project scope does not suppress global discovery. |
| MCP plugin/runtime helper fallbacks | `McpPlugin.cs:90,99,286,315,338,634` creates requests without an injected home. `McpRuntimeService.cs:404-412,663` uses the same home in global policy/token paths; `SearchToolsAsync:139-151` can obtain runtime state for effective servers. Paths are under `src/CodeAlta.Plugin.Mcp/`. | Fixtures need explicit home propagation through plugin helpers, not just outer management requests. Actual historical token access, subprocess/network activity and configured servers remain unknown; no credentials were inspected. |
| Explicit real-home skill forwarding | `src/CodeAlta.Orchestration/Runtime/AgentInstructionTemplateProvider.cs:97-110,179-192`, `SessionRuntimeService.cs:1258-1262`, `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs:3660-3675`, and `src/CodeAlta.Tui/App/SkillsManagementService.cs:43-54` supply the real profile. `src/CodeAlta.Catalog/Skills/BuiltInSkillRootProviders.cs:97-115` maps it to `.agents/skills`; `SkillCatalog.cs:303-410` performs discovery. | Temporary `GlobalRoot` does not isolate these consumers. Both `AltaLiveToolTests.SkillVisibility_*` cases actually invoke list/show through that path. Headless fixtures that submit a prompt can reach it, unlike construction-only tests. An unset `UserProfileRoot` is skipped by the user-common root provider; it does not itself fall back to home. |
| Ancestor instruction traversal | `src/CodeAlta.Agent/Runtime/AgentInstructionComposer.cs:28-32,101-153` and `src/CodeAlta.Orchestration/Runtime/SystemPrompts/SystemPromptBuilder.cs:624-640,663-718` walk to filesystem root, select and read `AGENTS.md`, `CLAUDE.md` or `.github/copilot-instructions.md`. `AgentSessionTests.AgentInstructionComposer_ComposesDeveloperInstructionsAndLargestContextFilesPerDirectory` directly calls `Compose`; the known formatting fixture calls `Build` with project context. | Normal Windows profile-local temporary projects can still probe/read profile ancestors after global-root injection. Traversal is confirmed; any particular ancestor-file read depends on its existence and was not observed. |

The direct prompt-file seam is already available: supplying nonblank temporary `UserCodeAltaRoot`
to `SystemPromptBuildRequest` prevents real `~/.alta/prompts` lookup through
`SystemPromptContentLocator.cs:96-110`. Merely resolving a profile string does not prove file reads.
`BuiltInPromptContentTests` and `PromptManagementDialogTests` were not confirmed prompt-profile leaks;
`HeadlessHost_ComposesPluginSkillRoots` explicitly queries temporary roots with no user-common root.

### Bounded repairs before expanding verification

1. Add explicit-home tilde normalization/controller overloads while preserving production defaults;
   move the tilde fixture entirely into a unique fake home. Cover `~`, `~/`, `~\`, unsupported `~name`
   and rooted paths. Set both explicit user roots in the known prompt-formatting fixture.
2. Add one discovery-home override to existing Catalog/host options and propagate it through
   instruction construction, runtime activation, live-tool queries and TUI skill management.
   Do not infer home from `GlobalRoot` or disable production common skills. Verify captured discovery
   requests before traversal, then synthetic home/global/project precedence.
3. Inject temporary `UserHomeDirectory` into relevant MCP requests and a narrow instance-owned
   plugin helper seam; verify global/project overlay, policy and token paths. Preserve actual
   discovery/writability behavior rather than bypassing it for tests. MCP home currently means the
   **parent of `.alta`**, not an arbitrary portable data root; shared-host integration must handle that distinction.
4. Bound both ancestor walkers explicitly for fixtures that test inheritance, or qualify them under
   an approved disposable OS identity/VM. Test boundaries using an outside sentinel within an owned
   outer directory, never the real profile. Non-discovery fixtures may use existing composed-instruction
   inputs when that preserves the behavior under test.

Keep executable admission/root propagation as the separate M2 shared-host step. In TUI `Program.cs`,
logging starts at lines 16-17 and plugin prestart at 27, before the guard at 79; plugin disposal in
the outer `finally` also occurs after that guard's scope. **Correction:** ordinary provider
composition is reached through `DeferredCodeAltaApp` after guard acquisition, not established as
pre-admission. Nevertheless, `CodeAltaOwnedServices.CreateAsync:95-166` and
`DeferredCodeAltaApp.cs:243-249` reconstruct default roots independently. Reuse explicit lock-path,
host/plugin root and provider state-root seams; acquire before mutable startup and release after disposal.

Source-audited expansion candidates, **not newly executed tests**, include
`OpenProjectRequestResolverTests`, `CodeAltaSingleInstanceGuardTests`, `BuiltInPromptContentTests`,
`PromptManagementDialogTests` and the current-project discovery selection in `SkillsManagementServiceTests`.
Do not add whole MCP/live-tool/headless classes until their individual requests and traversals are repaired.
SDK/NuGet credential providers and caches in `RequiresDotNet10FileBuild` tests, shell/MCP subprocesses,
native/browser/clipboard fixtures and arbitrary trusted plugin code need separate qualification.
`ModelProviderRuntimeTestExtensions.cs:40-58,72-82` can forward a null state root after failed lookup,
reaching `AgentRuntime.cs:42-46`'s real-home fallback; an offending invocation was not established.
Some provider fixtures also use nonunique temp/output roots; these are not proven profile leaks.

Environment-only `HOME`/`USERPROFILE` changes are not Windows `SpecialFolder` redirection, and
application path injection is not OS isolation. **No profile-safe full-suite result is claimed.**

### Skill-management isolation follow-up (2026-09-06)

The bounded skill extraction repaired its own TUI factory/fixtures, not all runtime discovery. Catalog `Skills.SkillManagementService` now takes an explicit optional profile and operation project root; TUI `SkillsManagementCoordinatorFactory` supplies the production profile default explicitly. The historical `SkillsManagementService.cs:43-54` forwarding reference above describes the pre-extraction code. Orchestration/live-tool/MCP and instruction ancestor repairs remain open.

An additional source-confirmed dependency path must be included in future fixture audits: XenoAtom.Glob `Git/RepositoryDiscovery.cs:31-61` walks ancestors for `.git`; `Internal/Git/GitConfigReader.cs:9-76` independently resolves `core.ignorecase` and `core.excludesFile`, falling through to XDG/home Git config and global ignores unless **both** resolve locally. A task-owned empty `.git` alone is insufficient. Catalog `Skills/SkillCatalog.cs` reaches this through scanning (`FindRepository`/repository discovery near lines 322, 362-364), related-file enumeration (209, 783-794), and activation (259). This is reachable behavior, not evidence of any particular historical home-file read; no home Git configuration was inspected.

Package correspondence: `Directory.Packages.props` pins XenoAtom.Glob 1.0.0, resolved by the Catalog assets file. Child inspection reports package nuspec repository commit `f281c870db2a1ae68b44185ddb66ec49abc0d2ec`, matching the local XenoAtom.Glob checkout HEAD with inspected source unchanged. Parent independently read the local discovery/config-reader source. This is package repository metadata/source correspondence, not independent binary decompilation.

Both new skill test fixtures create an owned `.git/config` containing `[core]`, `ignorecase = false`, and `excludesFile = excludes`, plus an owned empty `.git/excludes`. The relative excludes path is resolved within the owned Git directory; no `~/` expansion is used. Explicit providers and global/profile/project roots are supplied; synthetic built-in/plugin registrations also stay inside the fixture boundary. Tests are `CodeAlta.Tests.SkillsManagementServiceTests` (24 passing) and `CodeAlta.Catalog.Tests.SkillManagementServiceTests` (41 passing). Parent independently passed these and the recorded audited storage/TUI baselines (244 TUI; 95 Catalog with 1 Unix-only skip). This does not retroactively qualify other skill/Git fixtures or authorize a full suite.

The extra Unicode/CRLF preservation tests also exposed a pre-existing Tomlyn compatibility limit: valid literal U+FFFD in a TOML comment is rejected as an invalid UTF-16 surrogate sequence. A negative regression proves skill enablement reports failure without changing the original bytes; positive tests cover U+FFFD/supplementary scaffold descriptions and other Unicode/CRLF/provider-field preservation. No dependency or generic configuration parser repair was made.

### Editor-workspace cleanup verification follow-up

Against `452fad30`, parent audited the complete1,176-line six-case source gate before a clean targeted build and observed **two intended missing-adapter/core failures, four preservation passes**. The source gate is frozen. Sole-child Phase B wrote only the inert fixture first, exact workspace adapter/appended XML/core and three development-guide bullets, with no execution. Parent independently audited all801 behavioral/helper lines, production/docs, task/context containment, full workspace/guide inverses and frozen sources without corrections. Exact contract, source/read maps, hashes and filters are in the [parity checkpoint](dual-head-desktop-parity.md#m2-editor-workspace-cleanup-checkpoint).

Targeted and solution Release builds passed zero warnings/errors; **70 new source/inert cases**, **492 accepted lifetime regressions** and **four separately audited exact source-only architecture cases** passed. Only mandatory workspace and accepted Shell static seams execute, with inert IAsyncDisposable entries; no actual CTS/registrations, concrete editors/owners/search services/codecs/files/watchers/UI or version/logging/monitor initialization. All originals/producers/unselected/callback/caller/disposal/context work and observations are retained. Body and teardown start independent observations before each aggregate, finally release gates and observe two finite snapshots; every timeout remains permanently failing. The signaled inert dispatcher retains additional posts and dispatch-before-callback gating, restores context, and reports late-post/callback failures. Named source reads, architecture probes and existing writerless assembly logging remain nonzero I/O.

The change traverses held tab siblings after terminal picker/tab failures while preserving successful-path late snapshot timing, entry/reference order and plain-await context. It does not join picker/search refresh/ranking/publication, late initial loads, saves/reloads/dirty-dialog actions or queued/native watcher callbacks. Durable saves can complete after disposal; dirty-close return is not choice completion and file replacement is not UI acknowledgement. Hidden acquisitions, partial/late publication, disposed-before-fallible-cleanup flags, detached continuation dropping and fatal-reporting behavior remain unqualified. No default delayed acquisition, ordinary CTS-release failure, native callback join or installed UI-binary match is demonstrated. Noncompletion and earlier stages remain unbounded. M2–M7 and controller/Program/admission/other lower-owner gaps remain open. No native, installed-tool/archive, other-RID, interactive, accessibility, performance or full-parity evidence was added.

Parent `lunet build` passed (111 files /1,029,069 bytes; configured theme download/install observed, not network-free), `%TEMP%/codealta-parent-editor-workspace-site.log`. This adds no runtime/native qualification.

### Shell-controller initialization cleanup verification follow-up

Against `e044bdf7`, parent fully audited the frozen 1,445-line/four-case source gate before observing a clean build and **two intended missing-adapter/core failures, two preservation passes**. After sole-child Phase B, parent independently audited all 696 inert fixture/helper lines, exact 19-line adapter/appended XML/core and three development-guide bullets without corrections. Full inverse reconstruction, scheduling lines73/448, frozen Phase A/older fixtures and unchanged architecture guards passed raw-byte/ordinal preservation checks. Full contract, evidence, exact filters and logs are in the [parity checkpoint](dual-head-desktop-parity.md#m2-shell-controller-initialization-cleanup-checkpoint).

Targeted and solution Release builds passed zero warnings/errors; **60 new source/inert cases**, **432 accepted lifetime regressions** and **three separately audited exact source-only architecture cases** passed. Only mandatory controller and accepted Shell static seams execute; no concrete owner/controller/provider/history/UI/metadata/plugin/monitor/version initialization or broad suite. Three actual linked-CTS rows retain sources before registration. All originals, producers, unselected/callback work and frontend/controller/dispatch tasks are retained; body and teardown observations start independently before each aggregate, gates release in finally and two finite snapshots are observed. Any timeout permanently prevents actual registration/source release despite later completion. Source reads, architecture path probes and writerless assembly logging are nonzero I/O.

Containment preserves unconditional OCE suppression only around the supplied original join, direct error identities/order, initialization scheduling/policies, dispatcher behavior and fatal monitoring. The retained task is not complete startup ownership: failed/canceled interaction dispatch can skip the local-track join; direct initialization, cancellation-wrapped provider refresh/queued actions and independently scheduled restoration/history remain outside it. The provider reader's final cancellation may skip its join; None-token fallback and noncooperative/earlier stages remain unbounded. External cancellation traversals are separate, default monitoring may FailFast, and no current application-defined throwing registration or updater-style token-read race was demonstrated. Local dispatcher source is not a verified installed-binary match. No native, installed-tool/archive, other-RID, interactive, accessibility, performance or full-parity evidence was added. M2–M7, hidden acquisitions/partial publication, Program/early admission and other lower-owner lifetimes remain open.

Parent `lunet build` passed (111 files /1,028,833 bytes; configured theme download/install observed, not network-free), `%TEMP%/codealta-parent-controller-init-site.log`. Site success is not runtime/native qualification.

### Runtime-event-pump cleanup verification follow-up

Against `c10181ad`, parent audited the entire 566-line source fixture before a clean targeted build and actual **two-red/one-preservation-green** baseline. Sole-child implementation remained unexecuted until confirmed idle and complete parent audit of production and all 645 corrected behavioral/helper lines. Parent corrected arithmetic to **51 behavioral/54 new total cases**, not 61/64, and made context-body dispatch/frontend/pump observations concurrent before the aggregate. Exact adapter/core templates, full inverse pump reconstruction, Start line34, monitoring, four frozen source hashes and all older production/fixtures/architecture are preserved. A read-only Git-LF/checkout-CRLF comparison error was corrected without source changes; raw-Git round-trip and UTF-8/no BOM/per-file endings/final newline/whitespace checks passed. Full evidence, exact filters and logs are in the [parity checkpoint](dual-head-desktop-parity.md#m2-runtime-event-pump-cleanup-checkpoint).

Independent targeted and solution Release builds passed zero warnings/errors; **54 new source/inert cases**, separately selected **87 frontend**, **160 updater/Deferred**, **131 owner/Shell regressions** and **two separately audited exact architecture checks** passed. No Phase-B build/test failure. Runtime tests call only actual mandatory static pump/Shell operations; actual CTS/registrations occur only in three linked-callback rows, retained before registration with uncanceled parents and no independent external Cancel caller. All original/producer/callback/pump/frontend/dispatch tasks are retained, gates finally released, bounded observations concurrently launched before each aggregate, and a second finite snapshot taken. Persistent timeout history prohibits actual CTS/registration release even after later completion. No concrete owner/stream/controller/UI/provider/auth/plugin/metadata/version-info/monitor/reporter startup or broad suite. Named reads, architecture output-path ancestor/root probes and writerless assembly logging remain nonzero I/O.

The change contains terminal cancellation/join/release failures without changing the join-only unconditional OCE policy, Start/RunAsync, fatal monitoring or frontend ownership/context. The original successful disposal already joined before releasing sources; no updater-like token-read race was established. Joining the worker does not drain queued UI events/wakes, stop publishers/runtime/controller/plugins or join independent external cancellation callbacks. Default fatal reporting may still FailFast, and noncooperative operations or earlier stages can prevent later cleanup indefinitely. Hidden acquisitions, partial frontend/publication, Program/early admission and other lower owners remain unqualified. No native/installed-tool/archive/other-RID/interactive/accessibility/performance/full-parity evidence was added; M2–M7 remain open.

Parent `lunet build` passed (111 files /1,028,557 bytes; configured theme download/install observed, not network-free), `%TEMP%/codealta-parent-pump-site.log`. No runtime/native qualification follows from site success.

### Returned-app frontend cleanup verification follow-up

Against `fd2bf93b`, parent audited the complete 441-line source fixture before a clean targeted build and actual **two-red/one-preservation-green** baseline. Following sole-child implementation and confirmed idle handoff, parent reviewed all 559 behavioral lines/helpers, mandatory core and real adapter, and independently inverse-reconstructed App/Shell and the two bounded old-guard adaptations. The frozen source and replacement Deferred guard hashes, exact filters and logs are recorded in the [parity checkpoint](dual-head-desktop-parity.md#m2-returned-app-frontend-cleanup-checkpoint). Original per-file endings/UTF-8/no BOM/final newlines, App's 810 lines /46,998-byte size and unchanged architecture allowances were checked.

Independent targeted and solution Release builds passed zero warnings/errors; **87 new source/inert cases**, **291 accepted updater/Deferred/host/outer/Shell regressions** and **three exact source-only architecture checks** passed. No Phase-B build/test failure. New tests call only mandatory static Shell/Deferred operations with an inert app, retained original/unselected/callback tasks and finally-released gates. Every independent five-second observation starts before its aggregate, with a second finite snapshot and non-rehabilitated timeout errors; context dispatch is retained before aggregate teardown. No new CTS/registrations, concrete App/Shell/Deferred/UI/runtime/provider/auth/plugin/metadata/version-info initialization or broad suite. Source reads, existing architecture checkout enumeration/output-path ancestor lookup and writerless assembly logging remain nonzero I/O.

The seven frontend siblings are now independently attempted after terminal escaped failures, without changing persistence-result interpretation, successful ordering/frontend context, public App/Shell ownership, Deferred publication/selection or updater cleanup. This is not construction rollback or lower-owner completion: pending stages can still block later cleanup, and hidden acquisitions, partial surfaces/publication/Tick-after-failure, Program/early admission, reminders/metadata/lower owners and arbitrary UI/plugin/network termination remain unqualified. No new native/installed-tool/archive/other-RID/interactive/accessibility/performance/full-parity evidence; M2–M7 stay open.

Parent `lunet build` passed (111 files /1,028,283 bytes; configured theme download/install, not network-free), `%TEMP%/codealta-parent-frontend-cleanup-site.log`. No lifetime/native qualification is inferred from site success.

### Updater join verification follow-up

Against `093eca97`, parent independently audited the frozen three-method/534-line source fixture and observed all three intended red failures. Phase B remained unexecuted until parent confirmed the sole child idle and audited the full production/22-method/70-case fixture/helper changes. Exact inverse transformations preserved unrelated Deferred production and all three existing fixture adaptations; the strengthened old source guard and frozen updater guard hashes are recorded in the [parity checkpoint](dual-head-desktop-parity.md#m2-updater-cancellationjoin-checkpoint). Per-file encoding/newline and whitespace checks passed.

Independent targeted and solution Release builds passed with zero warnings/errors; the explicit four-fixture updater/Deferred filter passed **160 cases**, and unchanged accepted host/outer/shell regressions passed **131 cases**. No Phase-B build/test failure occurred. Exact filters/logs are in the parity checkpoint. No concrete updater/checker/About/app/host/services/provider/auth/plugin/metadata/version-info startup or broad suite ran. The new fixture uses mandatory static operations, retained inert tasks/resources/CTS, finally-released manual gates, concurrently started five-second observations, a second finite snapshot, explicit timeout rethrow and an explicitly retained context dispatch. Actual test-source release follows successful required observations and joins. Named source reads and writerless assembly logging remain nonzero I/O.

The production updater captures its token before scheduling, caches async-only disposal and joins its actual check before source release. Deferred awaits that complete stage without changing the prior app/startup → update → presenter → startup CTS order or frontend context. Earlier noncompleting cleanup can prevent update cancellation; the checker HTTP timeout does not bound total shutdown, and no network/transport or arbitrary callback termination is established. Existing check outcomes/UI generation and Program's pre-join result sampling remain. Hidden acquisitions, partial frontend/publication windows, interactive plugin startup, Program/early admission, reminder/metadata/lower-owner completeness remain unqualified. No new native/installed-tool/archive/other-RID/interactive/accessibility/performance or completed M2–M7 qualification. Historical follow-ups below retain their original scope.

Parent `lunet build` passed (111 files /1,028,049 bytes; configured theme download/install, not network-free); `%TEMP%/codealta-parent-update-site.log`. This does not expand runtime/native qualification.

### Deferred startup join verification follow-up

Against `10adb094`, parent audited the new two-method/423-line fixed-checkout source fixture before an exact source-red run; both missing-field assertions failed as expected. Its complete SHA256 stayed `1952CDBD26FF7EDEC65D531D2D0B7D77A0A4F5F1FCBDEECBCB3FB846775D0AFA`. Parent audited all 24 behavioral methods/85 cases and complete helpers before execution, required retention of timeout failures and concurrent context-teardown observations, and independently reconstructed all unrelated Deferred code under only approved substitutions. The initial targeted build encountered MSTEST0017 on a misleading expected-exception local name; parent renamed it without changing behavior, and the retry passed.

Independent final Release solution build: zero warnings/errors. Explicit new source/lifetime filter: **87 passed**. Preserved host/outer lifetime plus separately re-audited ShellFrontendHost filter: **131 passed**. Exact project/filters and log paths are recorded in the [parity checkpoint](dual-head-desktop-parity.md#m2-deferred-startup-cancellationjoin-checkpoint). No broad suite or concrete startup executed. Five actual production-used internal operations are exercised only with inert resources/callbacks and independently retained tasks; original faults/cancellations and all gated/caller/disposal tasks are observed, cleanup bounds start concurrently, and test CTS release requires actual joins. Named-source reads and writerless assembly logging are nonzero I/O.

The bounded change joins Deferred's original services-startup task and preserves exclusive app ownership, original terminal token and frontend-context cleanup. It does not prove UI-independent startup: the plugin feedback path calls `Terminal.Live`. Tagged .NET 10 CTS/registration source confirms only narrow parent-link cleanup semantics, not installed-runtime or arbitrary external-cancellation races. No native/installed-tool/archive/other-RID/interactive/accessibility/performance qualification was added. Hidden acquisitions, frontend partial construction/publication consequences, update/reminder joining, metadata/lower-owner completeness and Program/early-admission behavior remain open; no termination guarantee or completed M2 ownership/parity milestone is claimed.

### Acquired-object rollback verification follow-up

After explicit resume from the user-requested restart pause, parent independently audited both complete fixtures and creation/rollback wiring before executing tests. Both frozen Phase A source-method hashes, all 21 prior behavioral blocks/attributes and full helper suffixes match; successful creation bodies and all unrelated production reconstruct against `f3a67b9c` under the approved transforms. The two earlier missing-slot source failures were observed before implementation. Independent Release solution build passed with zero warnings/errors; **127 lifetime cases** and **four audited shell lifecycle regressions** passed. Exact filters are in the parity checkpoint; logs are `%TEMP%/codealta-parent-creation-rollback-after-{build,tests}.log` and `codealta-parent-creation-rollback-shell-tests.log`.

The 61 added rollback cases use only mandatory operation seams, instance-owned recordings and finally-observed independent tasks/manual gates with five-second bounds. No concrete owner, service, client, provider, coordinator, real startup, auth/protocol or native path runs. Named-source reads and writerless assembly logging remain. Returned-object cleanup and original/nested exception identity are covered; resources hidden by failing constructors, unpublished plugin activations, lower-owner termination, pending deferred-startup joining and early admission are not. No whole suite, new archive/install, native/other-RID, interactive, accessibility or performance qualification. M2 and all broader lifetime/release gates remain open.

Parent `lunet build` passed (**111 files /1,027,349 bytes**), including the configured theme download/install, not a network-free claim. The public plugin page now explains best-effort startup cleanup and retention of created files. Log: `%TEMP%/codealta-parent-creation-rollback-site.log`.

### Existing-owner disposal verification follow-up

Parent independently audited the final two lifetime fixtures and production ownership wiring before tests, reconstructed unchanged source against `c34cba33`, and verified both original source-method/attribute and full-helper hashes. After the two observed pre-fix source failures, the Release solution build passed with zero warnings/errors, **66 lifetime cases** passed, and **four separately re-audited `ShellFrontendHostTests` cases** passed. Exact filters and logs are in the parity checkpoint. No whole suite or additional legacy host/provider/auth/ModelsDev fixture was run.

Synthetic instance-owned operations test lazy single execution, pending/terminal task identity, best-effort ordering, borrowed operations, genuine cancellation, direct exception identity/aggregation and the late-host gate before metadata. Every independent operation is retained and bounded observations are started together during finally cleanup; gates are manually released. Source-only cases read named checkout files, not profiles; assembly hooks initialize writerless logging. These are not concrete resource/startup/shutdown tests or a zero-I/O claim. Program's prestarted plugin now survives host/metadata cleanup until its existing outer finally, but actual plugin/metadata lifetime remains unqualified. Same-owner reentrancy, synchronous blocking, failed/noncooperative lower owners, construction rollback, pending startup, frontend/refresh/reminder/update joins and early admission remain open. No new archive, install, native/other-RID, interactive, accessibility or performance qualification.

Parent `lunet build` also passed (**111 files /1,027,099 bytes**), with the configured theme download/install observed, not a network-free claim. The public plugin page now describes best-effort runtime cleanup and normal shutdown timing. Evidence: `%TEMP%/codealta-parent-host-lifetime-site.log`. Build/test/site success does not close M2 or the native/release gates.

### Codex authentication-test Hosting verification follow-up (2026-09-06)

Accepted after characterization baseline `731b94e3`: parent audited all original 16 methods/64 cases, both full helpers, five added public-null barriers and the updated tenth named-source guard before execution. Raw-UTF-8/ordinal reconstruction preserves the exact deferred factory/core/delegate, unrelated TUI, prior Hosting implementation (except approved overview/three ownership XML sentences), prior nine boundaries under API/caller substitutions and the tenth adapter/dialog/SourceRoot suffix. No BOM or encoding rewrite; original per-file line endings remain.

Independent Release build passed with zero warnings/errors. Explicit filtered results: **531 Hosting** (54 authentication-test, 58 browser, 49 device, 41 account, 42 deletion, ten boundaries, 86 xAI, 71 Copilot, 31 registration, 89 inspection), **646 TUI**, **two exact defaults**, **132 Catalog plus one Unix-only skip**. Focused TUI15 overlaps the broad audited selection. Logs: `%TEMP%/codealta-parent-codex-auth-{build,focused-hosting,focused-tui,hosting-regression,tui,defaults,catalog,site}.log`. Filters reuse the exact accepted browser-era operands, adding only the new auth fixture to TUI; no full provider/host/startup fixtures or full suite. Lunet passed with its configured theme download/install (111 files/1,026,739 bytes); neither site build nor restore is claimed network-free.

This qualifies inert forwarding/presentation and source wiring only, not real auth/import/refresh/save/delete, home discovery, provider protocol, application lifetime, installed packages, native/other-RID, interactive or accessibility/performance behavior. Required-null tests fault before production factories; invalid type/key/root alone is not isolation. Real authentication testing remains side-effectful, may succeed from fresh cached credentials, and retains manager/client non-disposal and arbitrary base-exception display. Existing localization output-directory reads and writerless logging remain. No failures or current bounded-step blockers; host rollback/join/disposal, shared admission and broader M2–M7 remain open. Prior eight-RID archives were not refreshed.

### Codex browser-login Hosting verification follow-up (2026-09-06)

Parent accepted the mechanical move from `425b5a2f` after comparing all 19 original complete test blocks (50 Hosting /28 TUI), both full corrected helper suffixes, exact factory/delegate/renamed core, remaining coordinator and prior Hosting source. Only the class overview and one explicitly corrected stale device XML ownership sentence differ outside the browser addition. All eight earlier boundaries reconstruct under approved browser/API/comment substitutions; the updated ninth preserves its entire adapter/dialog/opener/reader/SourceRoot suffix. Eight new public-null rows were individually audited: seven required objects plus all-null fault before construction, using throwing callbacks and no pending resources. A mismatching type is secondary only.

Independent Release build passed with zero warnings/errors. Audited results: **476 Hosting** (58 browser, 49 device, 41 account, 42 deletion, nine boundary, 86 xAI, 71 Copilot, 31 registration, 89 inspection), **631 TUI**, **two exact defaults**, **132 Catalog plus one Unix-only skip**. Logs: `%TEMP%/codealta-parent-codex-browser-{build,focused-hosting,hosting-regression,tui,defaults,catalog,site}.log`. TUI adds only the browser fixture to the exact previous 53 operands; three provider-coordinator methods remain individually selected. No full suite or concrete authentication/listener/store/host/browser execution was authorized. Existing localization output-directory reads and writerless logging remain; synthetic sequence events do not qualify provider behavior, storage, dispatcher rendering, native parity or lifetime.

The premature child response-stream termination was recovered in the same idle-confirmed session without another writer or reset; the temporarily absent TUI fixture was reconstructed and independently verified. Child and parent audit scripts each initially retained an extra separator newline; correcting the audit extraction passed without production changes. No build/test failure or current blocker remains. Original fake waits retain independent ownership, finally gate release/cancellation, five-second observations and nested route/wait joins before CTS disposal. Real unjoined-wait behavior and non-disposal remain intentionally unchanged. The approved browser-specific ID snapshot and plain-text response-after-save ordering are not new protocol or lifetime guarantees. Authentication testing and M2–M7 remain incomplete; historical archive/native evidence is not updated by this move.

### Codex browser-login characterization follow-up (2026-09-06)

Before a Hosting move, parent audited the full 802-line fixture, all 19 methods/78 inert cases and seven independently retained fake waits. Finally blocks release/cancel gates and observe both route and wait through bounded nested joins; CTS disposal follows cleanup. A parent correction removed a TUI helper call from the shared recording suffix so it can move mechanically; dedicated presentation cases still cover the production helper. All prior eight source boundaries reconstruct under only the approved browser expectation/comment changes, and a ninth guard checks the new TUI core/factory and protected routes. Raw UTF-8 Git-blob comparisons confirmed unrelated coordinator content and exact manager construction preserved. An initial console-decoded comparison mismatched Unicode; raw-byte decoding corrected the audit, not the production source.

Parent passed a clean Release build (zero warnings/errors), 78 browser cases and nine source-boundary cases using only explicit fixture filters. Logs: `%TEMP%/codealta-parent-codex-browser-before-{build,tests,boundary}.log`. No concrete provider/client/store/coordinator/host/listener/browser, credential/protocol/PKCE fixture, resolver or discovery executes. SR output-directory localization and writerless logging remain. Synthetic begin/wait events do not qualify the actual protocol: concrete ordering remains source evidence. Preserve the existing unjoined-wait failure path, ordinary opener exception suppression and dialog cancellation. The explicit browser-specific prefix-first one-read ID snapshot is not arbitrary-property/task identity/stack/settlement equivalence. Codex writes a plain-text callback response after persistence; response and cleanup failures can supersede earlier outcomes. No new full-suite/archive/install/native/other-RID or lifetime qualification; the Hosting move and broader authentication remain pending.

### Codex device-login verification follow-up (2026-09-06)

Parent audited the entire 667-line phase-1 fixture and accepted 60 inert cases plus eight source-boundary cases in `8a2c3f28`. After the Hosting move, independent comparisons preserved all 17 original methods (42 Hosting /18 TUI), both complete recording helpers, factory/core and unrelated TUI, including both display helpers/browser/root/account/deletion. Existing Hosting account/deletion content and adapter/dialog remained unchanged. The seven added public-null rows necessarily fail required-object guards before construction; nonmatching type is secondary only. The updated eighth boundary inspects API/nullability metadata and four named checkout sources without executing an operation; other guards and unchanged presentation/browser/adapter/dialog assertions remain intact.

Parent passed clean Release build (zero warnings/errors), **417 unique Hosting cases** (49 device, 41 account, 42 deletion, eight boundary, 86 xAI, 71 Copilot, 31 registration, 89 inspection), **603 audited TUI**, **two exact defaults**, **132 Catalog plus one Unix-only skip**, and site111 (1,026,191 bytes; configured theme download/install). Final filters/results are in `%TEMP%/codealta-parent-codex-device-{focused-hosting,hosting-regression,tui,defaults,catalog}.log`, with build/site and pre-move logs alongside them. TUI retains the exact prior 52 operands plus the device fixture. No phase-2 build/test failures or blockers occurred; the earlier phase-1 synthetic-key CS8601 correction remains recorded in the plan.

Fixtures use synthetic documents/inert primitive display values and mandatory recording factories only. All original pending-gate/finally/five-second join and CTS cleanup blocks were preserved. No credential/device-protocol/PKCE object, concrete manager/client/store/coordinator/host, resolver, profile/environment discovery or filesystem authentication executes. SR may read output-directory localization; writerless logging remains. Prefix-first once-only raw-ID projection is the explicit accepted deviation, not arbitrary-property/task identity/stack/settlement equivalence. Completion can fail after real provider persistence; reporting return does not prove UI rendering. These remain source observations, not protocol/storage/lifetime qualification. No full suite, new archive/install/native/other-RID or interactive qualification; historical archives predate this revision. Broader browser/authentication and M2–M7 stay open.

Final text-audit correction: the parent's initial no-BOM assertion falsely failed because culture-sensitive `string.StartsWith` treats U+FEFF as ignorable. Raw-byte inspection of all eleven committed blobs at `1be13656` confirms UTF-8 without BOM; the child's original report was correct. Use explicit byte checks or ordinal comparisons. Strict UTF-8 decoding, consistent line endings, final newlines and whitespace checks passed; no encoding rewrite was made. The earlier parent attribution to the child was incorrect.

### Codex account-metadata verification follow-up (2026-09-06)

Parent reviewed the full 493-line phase-1 fixture and accepted all 56 cases in `418801b3`. After extraction, independent comparisons preserved all 14 original methods (37 Hosting /19 TUI), both recording helpers, exact deferred factory/core, remaining TUI/formatter and existing Hosting deletion XML/API/core. Four added public-null cases were audited before execution: definition/root/callback/all-null fail required-object guards before concrete construction. There is no provider-type safety barrier for account lookup. The seventh boundary case reads four named checkout sources via the unchanged `CallerFilePath` root and inspects type/nullability metadata; all six prior methods/assertions were independently preserved.

Parent passed clean Release build (zero warnings/errors), **367 unique Hosting cases** (41 account, seven boundary, 42 deletion, 86 xAI, 71 Copilot, 31 registration, 89 inspection), **585 audited TUI**, **two exact defaults**, **132 Catalog plus one Unix-only skip**, and site111 (1,026,191 bytes; configured theme download/install). Exact filters/results are in `%TEMP%/codealta-parent-codex-account-{focused-hosting,hosting-regression,tui,defaults,catalog}.log`, with build/site and pre-move logs alongside them. Final TUI verification reran the exact prior 51 operands plus the account fixture. An initial reconstructed filter grouped the three existing pure cached-policy methods under their class; source review confirmed only those three methods and the final exact-filter run passed the same 585 cases. The first factory-comparison selector matched deletion; account-scoped comparison passed without code changes. Neither issue was an implementation/build/test failure.

All fixtures remain synthetic documents/inert strings/non-secret metadata and mandatory recording factories, with preserved manually released gates, five-second bounded joins and CTS cleanup. No concrete credential/protocol object, store/manager/client/coordinator/host, provider resolver, profile/environment discovery or filesystem authentication executes. SR may read output-directory localization; writerless logging remains. Once-only raw-label projection is the explicit accepted deviation; real storage/DPAPI/deserialization/JWT behavior is source-only evidence, not tested qualification. No full suite, new archive/install/native/other-RID, interactive or application-lifetime qualification was added. Historical registration archives predate this revision; broader Codex login/authentication and M2–M7 remain open.

### Codex credential-deletion verification follow-up (2026-09-06)

Parent reviewed all 382 lines of the pre-move fixture and passed 38 cases before accepting `8d08550c`. After the Hosting move, parent independently confirmed all original methods/helpers, the core/delegate/deferred construction expression and unrelated TUI content preserved. The four added public-null rows were audited before execution: definition/root/formatter/all-null faults occur before production construction, backed by a nonmatching synthetic type and throwing callbacks. No invalid-key/root-only shortcut was used. The sixth boundary test reads two named checkout source files through the unchanged `CallerFilePath` root and inspects API metadata, never credential paths. The five previous checks remain intact.

Parent passed clean Release build, **325 unique Hosting cases** (42 Codex deletion, six boundary, 86 xAI, 71 Copilot, 31 registration, 89 inspection), unchanged **566 audited TUI cases**, **two exact defaults cases**, **132 Catalog cases plus one Unix-only skip**, and site111 (1,026,191 bytes; configured theme download/install). The focused filter is `FullyQualifiedName~CodeAlta.Hosting.Tests.ConfiguredCodexCredentialDeletionTests.|FullyQualifiedName~CodeAlta.Hosting.Tests.HostingCompositionBoundaryTests.`; all existing regression filter operands remain unchanged. Exact filters/results: `%TEMP%/codealta-parent-codex-delete-{focused-hosting,hosting-regression,tui,defaults,catalog}.log`; build/site and pre-move evidence are recorded in the parity checkpoint.

Mandatory recording factories create only synthetic definitions, inert strings and token-only fake operations. Both manual-gate tests retain finally release, bounded joins and cancellation cleanup. No provider credential/protocol objects, concrete manager/client/store/coordinator/host, profile/environment discovery, filesystem authentication or actual deletion occurs. SR may read output-directory localization; writerless assembly logging remains. Fake lazy-root behavior is not proof of actual store validation: real root/store → HTTP/OAuth → key/manager construction order is source-only evidence. No direct-store shortcut or new resource disposal was introduced. Existing Codex auth/pipeline fixtures remain excluded; broader login, active authentication/refresh/import and account lookup are not qualified. No full suite, new archive/install/native/other-RID or application-lifetime qualification; historical registration archives predate this revision. M2 and remaining ownership/parity work stay open.

### xAI authentication verification follow-up (2026-09-06)

Parent audited the entire 812-line pre-move fixture and production wiring before passing 80 characterization cases, then independently confirmed all original methods, recording helpers, cores/options/delegates and unrelated TUI code preserved by the Hosting move. All 18 new public validation rows were reviewed before execution: each fails a required-object guard before the deferred production factory, with nonmatching non-null definitions as a second pre-factory barrier. Blank key/root values are only inert recording-factory inputs, never authentication shortcuts. The fifth boundary check reads two named checkout files through the existing `CallerFilePath` root; reflection checks inspect API metadata only. The four previous boundary tests remain intact.

Parent passed clean Release build, **282 unique Hosting cases** (86 xAI, five boundary, 71 Copilot, 31 registration, 89 inspection), **566 audited TUI cases** (including 12 xAI presentation cases), **two exact defaults cases**, **132 Catalog cases plus one Unix-only skip**, and site111 (1,026,191 bytes; configured theme download/install). New focused filter: `FullyQualifiedName~CodeAlta.Hosting.Tests.ConfiguredXaiAuthenticationTests.|FullyQualifiedName~CodeAlta.Hosting.Tests.HostingCompositionBoundaryTests.`. TUI adds only `FullyQualifiedName~CodeAlta.Tests.ConfiguredXaiAuthenticationTests.` to the previous audited baseline. Exact filters/results: `%TEMP%/codealta-parent-xai-{focused-hosting,focused-tui,hosting-regression,tui,defaults,catalog}.log`; build/site and pre-move evidence are recorded in the parity checkpoint.

The fixtures construct only synthetic definitions/inert public records and mandatory recording delegates, with manually released gates, finally cleanup and five-second bounds. No HTTP/OAuth/PKCE generation, listener, credential store/cache, environment/profile discovery, concrete coordinator/provider/host or browser/process execution occurs. SR may read output-directory localization and writerless test logging remains. Existing `XaiDirectProviderTests` manager/polling/credential fixtures remain excluded, including missing-cache status which still probes the filesystem. Real browser-PKCE/device exchanges, loopback/CORS, cancellation transformations, success-response/persistence ordering and teardown are source-only, not qualified by fakes. Nullable cached status does not reject expired credentials; no refresh/readiness policy was added. Existing manager/HttpClient non-disposal and application-lifetime/default-root blockers remain. No full suite, archive/install, native/other-RID or interactive qualification was added; historical registration archives do not establish this revision's payloads. M2 and broader parity remain open.

### Copilot authentication verification follow-up (2026-09-06)

Parent accepted the production-connected 68-case pre-move baseline, independently compared all original methods/fake helpers and the moved cores/options/delegates, and audited all 13 new public required-input cases before execution. Those calls fault before the deferred concrete factory; non-null definitions use a nonmatching type as an additional barrier, not invalid key/root values as an authentication shortcut. Successful operation cases supply recording delegates and inert public records only. No concrete manager, HTTP/OAuth client, credential store, coordinator, runtime or host is constructed; no environment/profile discovery or browser launch occurs in these fixtures. Manually released gates retain finally cleanup and bounded waits. Output-directory localization reads and writerless assembly logging remain, so this is not a blanket no-I/O/no-logging claim.

Parent independently passed clean Release build, **195 unique Hosting cases** (71 Copilot, four boundary, 31 registration, 89 inspection), **554 audited TUI cases** (including ten retained Copilot presentation cases), **two exact defaults cases**, **132 Catalog cases plus one Unix-only skip**, and site111 (1,026,191 bytes; configured theme download/install). Focused Hosting filter: `FullyQualifiedName~CodeAlta.Hosting.Tests.ConfiguredCopilotAuthenticationTests.|FullyQualifiedName~CodeAlta.Hosting.Tests.HostingCompositionBoundaryTests.`. Focused TUI/filter addition: `FullyQualifiedName~CodeAlta.Tests.ConfiguredCopilotAuthenticationTests.`. Other audited operands remain unchanged; exact filters/results are in `%TEMP%/codealta-parent-copilot-{focused-hosting,focused-tui,hosting-regression,tui,defaults,catalog}.log`, with build/site alongside them.

Boundary guards prove source/API wiring, not concrete authentication or storage behavior. Both UI login routes still use device flow; nullable cached status, option/root timing, callback/token/exception propagation, browser presentation and existing manager/HttpClient non-disposal are preserved. Real credential operations, browser-launch swallowing, full coordinator/provider startup and application shutdown remain unqualified. The unsafe legacy `CopilotDirectLoginManager_DeviceFlow_StoresCachedCredential` fixture was not run. No full suite, new archive, install, native/other-RID or interactive qualification was added; historical registration archives do not establish this revision's payloads. M2 and shared application-lifetime ownership remain incomplete.

### Provider inspection verification follow-up (2026-09-06)

Parent audited and ran the pre-move fixture: 74 new plus four existing checks passed before extraction. Parent then independently compared the preserved cores, factory/sorting and all original methods/fake helpers, and audited 26 added neutral-policy/input cases. Post-move results: clean Release build, 92 focused Hosting /15 focused TUI checks, 31 unchanged registration /two exact defaults cases, 544 audited TUI cases, 132 Catalog cases plus one Unix-only skip, and site111 with configured theme download/install. New Hosting filter: `FullyQualifiedName~CodeAlta.Hosting.Tests.ProviderInspectionWorkflowTests|FullyQualifiedName~CodeAlta.Hosting.Tests.HostingCompositionBoundaryTests`. The TUI baseline adds only `FullyQualifiedName~CodeAlta.Tests.ProviderInspectionWorkflowTests` and the three exact cached-helper methods previously audited in `ProviderFrontendCoordinatorTests`; the existing disposal guardrail remains included. Exact filters/results: `%TEMP%/codealta-parent-provider-inspection-{focused-hosting,focused-tui,registration,defaults,tui,catalog}.log`; the parity ledger records build/site and pre-move evidence.

All successful temporary probes execute explicitly supplied in-memory fake factories. The only public uncached calls in the new fixture are twelve required-input validation rows; parent checked they return before production factory forwarding. Other additions are pure cached-policy calls. No coordinator, registry, config store, metadata service, SDK, concrete runtime or host is constructed. Existing writerless test logging remains active. Both in-memory asynchronous gates are manually released in finally blocks and have bounded completion waits. Source guardrails are not full-coordinator/UI runtime proof. Borrowed mutable models, key-only cache reuse, probe-readiness quirks, formatting/disposal precedence, constructor rollback and broader lifetime remain unchanged. No archive pack, install, native startup, authentication or whole-suite qualification was added; previous archive checks do not establish contents of this revision.

### Configured-provider composition verification follow-up (2026-09-06)

Parent independently passed clean Release build, 34 Hosting cases, two exact shipped-defaults methods, the unchanged 530 audited TUI cases and 132 Catalog cases plus one Unix-only skip, and site111 (configured theme download/install). Hosting fixtures use synthetic documents, unique task-owned state/config paths, null metadata and credential-environment names, literal fake keys and uninvoked concrete factories. Only an explicitly fake runtime is retrieved, before any concrete registration; disposal coverage is fake-only. Writerless assembly logging uses `TestContext` ownership and shuts down only its own configuration. The three boundary tests inspect project/content/public API and TUI source wiring; they do not establish runtime startup. No blanket `ModelsDevCatalogTests`, OpenAI/provider class or host fixture was run. Exact new filters are `FullyQualifiedName~CodeAlta.Hosting.Tests.ConfiguredProviderRegistrationTests|FullyQualifiedName~CodeAlta.Hosting.Tests.HostingCompositionBoundaryTests` and `FullyQualifiedName=CodeAlta.Tests.OpenAIRawApiModelProviderRuntimeTests.RawApiProviderDefaultsCatalog_AppliesXiaomiOpenAIChatCompatibilityProfile|FullyQualifiedName=CodeAlta.Tests.OpenAIRawApiModelProviderRuntimeTests.RawApiProviderDefaultsCatalog_AppliesZaiReasoningReplayField`. Parent logs: `%TEMP%/codealta-parent-hosting-{build,focused,defaults,tui,catalog,site}.log`.

Parent independently checked the child-produced facade-plus-eight-RID archive set, version `0.19.9-alpha.0.24`; each RID payload includes Hosting/Agent/six providers/Hosting XML, exactly one identical defaults TOML and unchanged models JSON. See the parity checkpoint for the owned archive path. Initial child no-build pack failed with missing RID outputs (`MSB3030`); normal pack/build with `--no-restore` succeeded. Scoped restores were not proven network-free. These are archive-content checks only: no installed-tool, runtime loading, provider/auth, host lifetime, native or other-platform qualification. TUI still owns metadata refresh and provider workflows; existing host/default-root/discovery/disposal safety blockers remain. The new Hosting library does not repair broader transitive plugin/terminal coupling.

### Reminder observer-isolation verification follow-up (2026-09-06)

Parent independently passed clean Release build, 31 focused tests, 530 audited TUI tests, 132 Catalog tests plus one Unix-only skip, and site111 (configured theme download). TUI adds only the source-audited `AltaReminderProtocolTests.` and `ReminderPresentationFeedbackTests.` to the previous 43 operands; Catalog's nine operands are unchanged. Protocol list/delete use a nonadvancing manual clock and built-in-only preregistered services, not runtime/catalog/default plugin startup. Create's concrete runtime requirement remains unsafe for the legacy fixture: actual output-helper/service execution plus exact source wiring is the qualified coverage. Presentation coverage is the actual helper without visuals or posted-UI execution. Existing test-assembly logging runs; no whole-app startup/shutdown or reliable diagnostic delivery is qualified. Parent inspected five runtime-red failures against the unchanged service. Exact filters/results: `%TEMP%/codealta-parent-reminder-observers-{tui,catalog}.log`; parity ledger records full evidence and limits.

### Reminder-firing verification follow-up (2026-09-06)

Parent independently passed clean Release build, 8 focused reminder tests, 507 audited TUI tests, 132 Catalog tests plus one Unix-only skip, and site111 (configured theme download). The prior 42-operand TUI filter adds only `FullyQualifiedName~CodeAlta.Tests.AltaReminderServiceTests.`; Catalog's nine operands remain unchanged. New tests use manually advanced timers and a custom-contributor-only dispatcher without runtime/catalog/default contributor or profile-discovery startup. The existing test assembly logging hook remains active; cleanup does not establish runner joining or whole-app shutdown. Parent inspected actual old-route runtime-red and final fixture isolation. Exact filters/results: `%TEMP%/codealta-parent-reminder-firings-{tui,catalog}.log`; full evidence/limits are in the parity ledger. No broader test/native/interactive qualification is implied.

### Ask-response settlement verification follow-up (2026-09-06)

The parent independently verified the bounded claim/three-way-settlement slice with a clean Release solution build, 47 focused tests, 499 audited TUI tests, 132 Catalog tests plus one Unix-only skip, and the website build (111 files; configured theme download). The existing 40-operand TUI filter adds only the source-audited in-memory `AltaAskResponseTests` and `AskResponsePresentationPolicyTests`; Catalog's nine operands are unchanged. These exercise controlled delegates/TCS, immutable DTOs and the actual presentation-policy seam, not history loading, provider/plugin execution or app startup. The static plugin replacement regression and source wiring guardrails likewise do not qualify full runtime admission or interactive TUI behavior. Exact filters and results are in `%TEMP%/codealta-parent-response-{tui,catalog}.log`; scope, red/green evidence and architectural limits are recorded in the parity ledger. No new full-suite, native, cross-RID, accessibility or execution-lifetime qualification is claimed.
