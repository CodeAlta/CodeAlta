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

### Reminder-firing verification follow-up (2026-09-06)

Parent independently passed clean Release build, 8 focused reminder tests, 507 audited TUI tests, 132 Catalog tests plus one Unix-only skip, and site111 (configured theme download). The prior 42-operand TUI filter adds only `FullyQualifiedName~CodeAlta.Tests.AltaReminderServiceTests.`; Catalog's nine operands remain unchanged. New tests use manually advanced timers and a custom-contributor-only dispatcher without runtime/catalog/default contributor or profile-discovery startup. The existing test assembly logging hook remains active; cleanup does not establish runner joining or whole-app shutdown. Parent inspected actual old-route runtime-red and final fixture isolation. Exact filters/results: `%TEMP%/codealta-parent-reminder-firings-{tui,catalog}.log`; full evidence/limits are in the parity ledger. No broader test/native/interactive qualification is implied.

### Ask-response settlement verification follow-up (2026-09-06)

The parent independently verified the bounded claim/three-way-settlement slice with a clean Release solution build, 47 focused tests, 499 audited TUI tests, 132 Catalog tests plus one Unix-only skip, and the website build (111 files; configured theme download). The existing 40-operand TUI filter adds only the source-audited in-memory `AltaAskResponseTests` and `AskResponsePresentationPolicyTests`; Catalog's nine operands are unchanged. These exercise controlled delegates/TCS, immutable DTOs and the actual presentation-policy seam, not history loading, provider/plugin execution or app startup. The static plugin replacement regression and source wiring guardrails likewise do not qualify full runtime admission or interactive TUI behavior. Exact filters and results are in `%TEMP%/codealta-parent-response-{tui,catalog}.log`; scope, red/green evidence and architectural limits are recorded in the parity ledger. No new full-suite, native, cross-RID, accessibility or execution-lifetime qualification is claimed.
