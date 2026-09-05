# Dual-head desktop parity — in development

> **Status: M0 Windows package/native feasibility accepted, 2026-09-05.** This is an implementation acceptance ledger, not documentation of a shipped desktop. Desktop feature parity and full platform qualification remain **pending**; the isolated M0 package/native proof below has passed. The existing `CodeAlta` / `alta` remains the terminal application at this checkpoint. A successful managed build or existing unit test does not qualify desktop support.

Source of acceptance criteria: the approved [dual-head desktop plan](../.alta/plans/2026-09-05-dual-head-desktop.md), especially its feature-parity matrix, design §§6.2–6.6, and M0/M7 gates. This document records only the bounded M0 baseline/parity-document step; it does **not** mark all of M0 complete. The coordinator owns the plan checklist and subsequent implementation.

## Scope and evidence rules

- Target identities are desktop `CodeAlta` → `alta` and terminal `CodeAlta.Tui` → `altatui`, sharing .NET application/runtime services and durable state. These are targets, not completed renames.
- Desktop uses the approved NeoAstra 0.1.0 NuGet integration, React/TypeScript/Vite and packaged local assets. No HTTP UI server, browser launch, SSR or installed Node runtime; explicit backend OAuth loopback callbacks are a separate preserved feature.
- One application owns the shared data root across both heads. Tab closure/reload must not stop an accepted run; explicit Stop and confirmed application quit have separate lifetimes. JSONL remains authoritative and SQLite remains a rebuildable cache.
- Use normal trusted-library integration. Document/model/plugin payload text remains data: raw Markdown HTML disabled, Mermaid strict defaults, controlled links/attachments and backend tool permissions. No auxiliary rendering view, custom sandbox or nonce infrastructure is part of this acceptance scope.
- **Pending** means implementation and/or acceptance evidence is outstanding. **Pass** requires a recorded scenario, revision, test/fixture and result; native scenarios also require actual OS/RID/engine evidence. **Fail** records the observed defect. **Skipped** is not a pass and requires a reason. Only explicitly terminal-specific diagnostics may be desktop **N/A**, with justification.
- Every parity row requires automated coverage and a manual scenario where UI/native behavior matters. Existing source references describe the current TUI baseline, not proof that a desktop counterpart exists. No placeholder page, mock-browser test, source fallback alone, or successful compilation establishes parity.
- Future evidence should record command/scenario, date, revision, fixture/data root, OS/RID/engine version, duration, pass/fail/skip, artifact location and remaining limitations. Keep tokens, user journals/configuration and other private data out of evidence.

## Baseline recorded on 2026-09-05

### Checkout, configuration and safety

- Branch: `feature/dual-head-desktop`; baseline revision: `7ef14f72e128798e4134036d462eff16158d1f7e`. Tracked files were clean before baseline execution. The approved plan and the pre-existing local configuration/artifact paths remained untracked; the configuration/artifact files were not read, staged, deleted or otherwise changed.
- Inspected root and website `AGENTS.md`, `doc/development-guide.md`, `src/CodeAlta.slnx`, project files, `src/Directory.Build.props`, `src/Directory.Packages.props`, `src/global.json` and `site/config.scriban`. Project files contain no custom `Exec` build targets launching the application. No repository `packages.lock.json` or test runsettings was found in the tracked-file inventory.
- The SDK policy requests .NET 10.0.100 with `latestMinor` roll-forward, no prereleases. Actual runner: Windows 10.0.26200, `win-x64`, SDK **10.0.303**, MSBuild **18.6.14**. Tests use centrally pinned MSTest **4.3.3**, target `net10.0`; warnings are errors. Existing project-specific warning exclusions were not changed.
- Used the already installed `dotnet` and `lunet` commands; no missing tools or new application dependencies were installed. Ordinary established dependency restore was allowed. Lunet fetched its configured `lunet-io/templates` extension during the site build.
- Build/test/site processes used a fresh task-owned temporary directory, with process-local `HOME`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `DOTNET_CLI_HOME`, `TEMP` and `TMP` pointing to its subdirectories. CLI telemetry/first-time-experience flags were disabled. Provider/token/secret environment-variable names and `ALTA_`/`CODEALTA_` overrides were removed from that shell without printing values. These changes did not change the parent host environment.
- `NUGET_PACKAGES` explicitly reused the established `.nuget/packages` dependency cache; the installed Lunet tool was resolved before changing the environment. This is dependency/tool reuse, not an isolated OS account or sandbox. Environment overrides alone do **not** prove Windows `SpecialFolder.UserProfile` or all runtime stores are redirected, so safety relied on the bounded audited test selection below. No CodeAlta executable, real provider, user source plugin or billing-capable session was launched.
- The audited project is `CodeAlta.Plugins.Abstractions.Tests`: its sole test file uses in-memory contracts, `NoopPluginServices`, fake event data and temporary-path descriptors. `NoopPluginStateStore` performs no filesystem reads/writes. This suite does not create the application host or load the checkout's project configuration.

### Commands and results

Durations below are measured command wall time, including command startup and restore/build work, not application performance benchmarks. `<EvidenceRoot>` denotes the task-owned temporary directory described below.

| Working directory | Command | Status / exit | Wall time | Evidence and classification |
| --- | --- | --- | --- | --- |
| `src` | `dotnet build -c Release` | **Pass**, 0 | 17.538 s | Full existing solution built; **0 warnings, 0 errors**. MSBuild reported 17.29 s. No pre-existing compilation failure observed. |
| `src` | `dotnet test CodeAlta.Plugins.Abstractions.Tests/CodeAlta.Plugins.Abstractions.Tests.csproj -c Release --logger "trx;LogFileName=plugin-abstractions.trx" --results-directory <EvidenceRoot>/results` | **Pass**, 0 | 2.140 s | **13 passed, 0 failed, 0 skipped** in this project only; runner test duration 70 ms. Contract regression baseline, not full-solution or desktop coverage. |
| `src` | `dotnet test -c Release` | **Skipped — safety**, not launched | Not measured | Unfiltered runtime/profile isolation was not established; see exclusions below. No full-suite success or failure is claimed. |
| `site` | `lunet build` | **Pass**, 0 | 3.152 s | Production site build: **111 files**, **1,011,576 bytes** written; Lunet reported 2,095.4111 ms. No pre-existing website failure observed. |

Local evidence was retained outside the repository in the original temporary directory under `codealta-m0-baseline-a2726f6b424147a98bf5336411b567c1`: `dotnet-info.log`, `build.log`, `tests.log`, `site.log`, `summary.json` and `results/plugin-abstractions.trx`. These are local, temporary execution artifacts, not committed evidence or a portable CI artifact. Standard ignored build/site outputs were generated; no application, dependency or website source was edited.

### Safety exclusions and baseline limits

- `CodeAlta.Tests/CodeAltaShellControllerTests.cs`, `OpenFolderAsync_TildePath_ExpandsHomeDirectoryBeforeUpsert`, resolves `Environment.SpecialFolder.UserProfile` and creates/deletes a test directory beneath that real home path. It was **not run**. The existing single-instance default-path test also resolves that special folder, although its default-path assertion alone is not a write. The broader suite must not be treated as profile-isolated merely because environment variables were redirected.
- All projects other than `CodeAlta.Plugins.Abstractions.Tests` were excluded from test execution in this bounded baseline: `CodeAlta.Tests`, `CodeAlta.Catalog.Tests`, `CodeAlta.Orchestration.Tests` and `CodeAlta.Plugins.Tests`. Their sources include runtime/host, filesystem, subprocess, provider and source-plugin integration paths. This is a conservative scope/safety omission, **not** a claim that every test in these projects is unsafe. They compiled in the solution build but their runtime results remain unknown.
- Source-plugin tests tagged `RequiresDotNet10FileBuild`, the catalog's existing ignored timing-sensitive traversal test and platform-specific branches were not evaluated by this selection. They must not be reported as passing or as runner-skipped tests within the 13-test result.
- No real-provider login/model request, default-root application smoke, multi-process lock test, cross-head handoff, desktop prototype, NuGet tool pack/install, GUI launch, benchmark or native accessibility test was attempted. There is no failing baseline to fix from the commands executed here; the unfiltered-suite omission is the regression-baseline gap.

### Coordinator regression follow-up (2026-09-05)

A bounded read-only audit identified additional temporary-root/in-memory fixtures. The coordinator ran these against the baseline Release outputs with `dotnet test <project>/<project>.csproj -c Release --no-build --no-restore --filter <filter>`, with TRX output in task-owned temporary `codealta-m0-followup-16cdec454c204bd8bd8ce3ae0e4d6355`. No executable startup, real provider, or default-root lock acquisition was used. The following results supplement, rather than replace, the initial baseline exclusions above:

| Project | Filter (`FullyQualifiedName` unless noted) | Result | Wall time |
| --- | --- | --- | --- |
| `CodeAlta.Tests` | `~CodeAlta.Tests.CodeAltaSingleInstanceGuardTests` | 6 passed | 1.247 s |
| `CodeAlta.Tests` | `=CodeAlta.Tests.CodeAltaHostTests.CreateAsync_HeadlessWithoutPlugins_ConstructsAndDisposesRuntimeServices` OR `=CodeAlta.Tests.CodeAltaHostTests.CreateAsync_ArchivedCurrentProjectIsVisibleInMemoryWithoutSaving` | 2 passed | 1.139 s |
| `CodeAlta.Catalog.Tests` | `~ProjectFilePromptReferenceParserTests` OR `~ProjectFileSearchServiceTests` OR `~SkillCatalogTests` | 15 passed, 1 existing ignored test | 0.869 s |
| `CodeAlta.Orchestration.Tests` | `~BoundedRuntimeEventStreamTests` OR `~OrchestrationMailboxActorTests` OR `~SessionActorRegistryTests` OR `~SessionEventSequencerTests` OR `~SessionPromptDispatchPlannerTests` | 22 passed | 0.874 s |

For command reproduction, expand each operand with `FullyQualifiedName` and join OR operands with literal `|`. All commands exited 0. The ignored catalog case is `ProjectFileSearchSession_PublishesIncrementalUpdatesAndIgnoresStaleRefreshes`. This brings executed baseline coverage to **58 passed, 1 ignored**, not a full-suite result.

The audit narrowed the remaining isolation gaps: `SystemPromptInfrastructureTests.SystemPromptBuilder_CodeFormatsPathsInGeneratedMarkdown` omits explicit user roots and can read global prompts; `AgentInstructionTemplateProvider` passes the real user profile into common-skill discovery even when the host's `GlobalRoot` is temporary. These need explicit root injection before full runtime coverage. The tilde-path test writes only its GUID test directory under `~/codealta-open-tests`, not `~/.alta`; the selected lock tests use temporary locks, and their default-path assertion only compares strings. Fake provider construction, task-owned filesystem fixtures and local-loopback tests are not inherently live-profile or billable operations. No blanket unsafe classification or full-suite isolation claim is made.

## Feature-parity acceptance matrix

All **14** approved areas are retained. The two evidence columns are deliberately separate: neither implementation intent nor existing TUI coverage establishes desktop acceptance. Paths/symbols below refer to the pre-rename implementation under `src/CodeAlta` unless otherwise noted.

| ID / area and current reference | Required desktop acceptance | Automated evidence | Manual/native evidence |
| --- | --- | --- | --- |
| P01 — Startup/config recovery: `Program`, `DeferredCodeAltaApp`, `ConfigRecoveryDialog` | Fast boot shell; offline sessions before providers; malformed TOML recovery; cache/provider/plugin errors visible; safe mode works. | **Pending** | **Pending** |
| P02 — Navigator/projects: `CodeAltaShellController`, `NavigatorActionCoordinator`, project dialogs | Current-folder project; add/open/import; rename/edit/archive/hide/delete as currently supported; sorting/recent limits; global and nested child sessions; refresh/search and complete session browsing. | **Pending** | **Pending** |
| P03 — Tabs and restore: shell/session tab coordinators, `SessionViewCatalog` | Draft/session/editor/plugin tabs, selected tab and preferences restored; close/reopen and keyboard movement; unsent drafts survive switching heads. Running sessions remain discoverable when tabs close. | **Pending** | **Pending** |
| P04 — Session commands: creation/command/queue/provider-switch coordinators | Create global/project/child sessions; send/queue/always-queue/steer/abort/compact; clear queue, inspect and perform existing queue edit/reorder actions; rename/delete; provider/model/reasoning/agent-prompt selection with actual runtime capabilities. | **Pending** | **Pending** |
| P05 — Composer: prompt/reference/image views and GitHub plugin | Multiline/full prompt; undo/redo, paste, IME; file `@` and GitHub `#` references; plugin attachments; clipboard/drop images; capability validation and persistent drafts. `?`/`/` shortcuts remain prompt UI, not a new slash-tail parser. | **Pending** | **Pending** |
| P06 — Timeline: runtime/timeline renderers, usage and session-info views | User/assistant/reasoning/notices/tools; streaming progress/errors/cancellations; code/diffs/images; expand/collapse and copy; history navigation/search; system-prompt details; context/tokens/cost/timing and provider/session metadata. No recomputing usage in JavaScript. | **Pending** | **Pending** |
| P07 — Interactions: permission/user-input coordinators, `AskModeCoordinator`, `AskQuestionFormView`, `AskFileReviewView` | Existing permission decisions and auto-approval policy; queued question/choice/freeform asks; attached-file review/edit/save; cancellation and exactly-once replies across document reload. | **Pending** | **Pending** |
| P08 — Coordination: `CodeAltaFrontendComposition`, LiveTool contributors, notes/reminders | All in-process `alta` actions, parent/child status/results, notes copy/clear, reminders create/list/delete and repeat/queue behavior, agent/profile switching. Independent of active tab selection. | **Pending** | **Pending** |
| P09 — Providers: provider coordinator/dialog/model catalog | Add/edit/delete/enable; advanced TOML; test/refresh/cancel; browser/device login/logout; accounts/models; readiness and diagnostics; model metadata and selection for draft/current session. | **Pending** | **Pending** |
| P10 — Prompts/skills: management dialogs/services and catalogs | Agent and system prompt create/edit/delete/inspect; scope/provenance/built-in read-only rules; skills discover/inspect/create/edit/enable/disable/activate and diagnostics. | **Pending** | **Pending** |
| P11 — Plugins/MCP/GitHub/statistics | Discovery, enable/disable/rebuild/reload/status/diagnostics/contributions; current MCP server/tool management, JSON/policy editing and OAuth; GitHub picker/attachments; statistics cards/details/live-tool commands. | **Pending** | **Pending** |
| P12 — File editing: `FileEditorWorkspaceCoordinator`, `FileEditorTab`, `TextFileCodec` | Scoped open/edit/save; encoding/BOM/newlines; syntax/search/undo/redo; conflict detection and dirty-close prompts. No unrestricted renderer filesystem access. | **Pending** | **Pending** |
| P13 — Preferences/help/diagnostics: built-in commands, navigator settings, logs/about | Scheme/language/permissions; navigator layout/density; help/palette; logs; version/update visibility; clipboard and screenshot/export equivalent. Terminal loop/terminal-cell debug commands stay TUI-only (see exception below). | **Pending** | **Pending** |
| P14 — Accessibility/native behavior | Keyboard-complete navigation, focus restore/trap, semantic labels/non-color statuses, contrast/reduced motion, text scale/high DPI, native window/menu/dialog behavior on every claimed engine. | **Pending** | **Pending** |

### Explicit terminal-only exception

Terminal loop/terminal-cell developer diagnostics have no native-window or browser-cell equivalent and may remain TUI-only, as the approved matrix permits. This does **not** exempt logs, status, help, clipboard, screenshot/export or ordinary diagnostics from desktop parity. The command-by-command inventory and proof that these diagnostic commands remain functional in TUI are **pending**; no other area is classified N/A.

## Additional approved v1 acceptance details

These rows retain the richer approved editor/visual/workspace requirements; they do not expand the plan. Each needs automated tests plus packaged native UI evidence where applicable.

| ID / surface | Required acceptance | Implementation / evidence |
| --- | --- | --- |
| V01 — Editor/composer engine (P05/P12) | One CodeMirror 6 engine with composer, file/config and read-only comparison presets; preserve editor state/undo/selection through tab changes and resizing; IME/completion must prevent accidental sends. Line numbers, folding, matching/indentation, multiselection, find/replace with regex, go-to-line, wrapping/tab size, line/column and encoding/newline status; dirty/conflict/revision-checked .NET save and backend diagnostics with correct UTF-16 positions. | **Pending** |
| V02 — Languages and large files (P06/P12) | Markdown/fences, C#, JSON, TOML, YAML, JS/TS/JSX/TSX, HTML/CSS, XML/MSBuild, Bash/PowerShell, Python and diff grammars with plain-text fallback; bounded transport/long-line work and explicit large-file mode; never save a truncated document over its original. No semantic IDE/LSP claim. | **Pending** |
| V03 — Markdown/code (P06/P08/P10/P12) | Shared GFM renderer for all surfaces; tables/tasks/footnotes/callouts, heading anchors and exact-source code copy/wrap/open controls; no raw HTML, automatic remote images or executable task boxes. Lazy Shiki JavaScript-engine token rendering, bounded language imports, stable block revisions and readable incomplete-fence fallback. | **Pending** |
| V04 — Markdown editing and comparisons (P07/P12) | Source / Preview / Split, heading outline, source-position links, bounded anchored scroll sync, source-preserving toolbar edits; inline/side-by-side read-only CM merge, context folding/gutters/intraline emphasis/change navigation. Partial patches remain patches, not fabricated full files; editable saves use the ordinary backend conflict checks. | **Pending** |
| V05 — Mermaid (P06/P08/P12) | Direct main-document strict-mode rendering for flowchart, sequence, class, state and ER; `.mmd`/`.mermaid` and fenced Markdown share the component. Diagram / Source, accessible title/description/source, fit/zoom/reset/pan/expand, copy/save source and bounded PNG export. Test theme/fonts/Unicode, invalid/incomplete/oversized input, stale results and cleanup. Initial 32 KiB/200-edge limits are targets to measure, not qualified results; source fallback alone is not completion. | **Pending** |
| V06 — Tabs/splits/native drops (P03/P04/P05/P14) | Persistent reorderable tab strip with overflow/pin/reopen/badges/dirty guards, pointer and keyboard dnd-kit moves/cancellation/announcements, no editor remount or duplicate subscriptions. Accessible resizable panes with constraints/reset/collapse and committed-size persistence. Native file/image/folder drops use explicit grants, not browser path inference; queue reorder remains a backend command. | **Pending** |
| V07 — Settings/tables/themes/localization (P09/P10/P11/P13) | One settings sidebar/search/scope workflow; compact searchable/sortable accessible model/provider/plugin tables with truthful backend paging; shared named neutral color schemes and localization, local licensed fonts/icons, configurable typography/density, live-preview/revert, reduced motion and scaling. Exact library versions/lockfile and license review still pending. | **Pending** |
| V08 — Portable plugin panels (P11) | Head-neutral backend contributions, optional TUI renderers, rich desktop adapters and meaningful TUI fallback; builtin MCP/GitHub/statistics retain shared backend ownership. One typed project/workspace panel example; backend-only, dual-head, TUI-only, unavailable/failing/reloading plugin tests in installed packages. | **Pending** |

Remote hosting/network authentication, multi-user access, installers/automatic updates, arbitrary plugin JavaScript canvases, executable HTML/JS previews, editable graph canvases, WYSIWYG, embedded terminals, general docking/tear-offs and a full IDE remain outside this approved scope. They are not silently substituted for, or prerequisites of, the pending rows above.

## Package, runtime and release gates

| Gate | Required evidence | Current status |
| --- | --- | --- |
| G01 — M0 NuGet consumer | Minimal consumer using published NeoAstra **0.1.0**, centrally pinned package and reviewed locked frontend dependencies; staged client and relative assets. | **Pass — isolated prototype**, not production |
| G02 — M0 serverless/native proof | Fake-data packaged Release launch, typed RPC round trip, channel cancellation, close/dialog behavior, dynamic asset loading and ordinary CM/Radix/direct Mermaid integration without Node/dev server/providers/plugins. | **Pass — Windows x64 prototype**, parent rerun |
| G03 — M0 isolated tool distribution | Local-feed pack/install outside checkout into task-owned tool path; native/assets/contracts/content inventory and command exit/output checks; Windows console/window subsystem behavior. No absolute local NeoAstra dependency or global tool replacement. | **Pass — Exe/WinExe prototype packages**; interactive console/Explorer UX and production plugin CLI pending |
| G04 — Shared ownership and compatibility | Both heads and both cross-head lock orders/races/stale/denied-access cases; admission before mutable startup; fake-provider sessions with closed tabs, reload, queue/stop and confirmed quit; legacy/current journal/config/UI-state/text/image draft handoff without unknown-field loss. | **Pending** |
| G05 — Recovery and permissions | One runtime reader; revisioned snapshot/subscribe, bounded nonblocking fan-out, loss/gap recovery with bounded history reads; exactly-once ask/permission resolution across reconnect/abort/quit and stale/wrong-session rejection. Preserve permission configuration including current AutoApprove default. | **Pending** |
| G06 — Content and package correctness | Controlled application navigation/links/attachments/native grants, default Markdown/Mermaid content handling, no secrets/source maps/remote assets/dev overrides in Release; explicit OAuth listeners cleaned up. No custom isolation framework. | **Pending** |
| G07 — Performance and accessibility | Hardware/fixture-recorded cold/warm startup, provider-independent catalog/history, ten active fake sessions and 100,000-event journal, bounded memory/payloads/DOM, input/scroll latency; keyboard/screen-reader/IME/contrast/reduced-motion/100–200% scale. Provisional <50 ms p95 input/frame targets are unmeasured. | **Pending — no performance budget qualified** |
| G08 — Full regression and release | Full safely isolated .NET suite, frontend typecheck/lint/tests/build, mock-transport UI tests distinguished from native tests; both final tool packages/content/plugin compatibility, all parity rows and public/internal installation/migration docs; no publishing or reduced support matrix without authorization. | **Pending — partial baseline only** |

### Native platform qualification

The [M0 prototype README](../src/prototypes/CodeAlta.Desktop.Probe/README.md) and its evidence ledger record actual Windows x64 execution on WebView2 **152.0.4191.62**, .NET **10.0.11**, Windows **10.0.26200**. Both ordinary Exe/WinExe local tool packages preserve native-dialog manifests and pass redirected CLI/native smoke. Parent independently reran the installed Exe smoke and all three frontend tests. Asset inspection verified 100 hashes (3,823,561 bytes) and six native binaries; no Node/runtime/source-map bundle. Only this bounded feasibility fixture is accepted; all production/native feature qualification remains open, including five other architectures/engines, interactive console behavior, accessibility/performance and complete release notices. Point-in-time npm/NuGet advisory checks reported no vulnerabilities.

| Intended desktop RID | Native engine | Actual engine/version evidence | Qualification |
| --- | --- | --- | --- |
| `win-x64` | WebView2 | 152.0.4191.62; M0 local-tool/native smoke passed | **M0 feasibility passed; full product qualification pending** |
| `win-arm64` | WebView2 | Not measured; no native run | **Pending / unqualified** |
| `osx-x64` | WKWebView | Not measured; no native run | **Pending / unqualified** |
| `osx-arm64` | WKWebView | Not measured; no native run | **Pending / unqualified** |
| `linux-x64` (glibc) | WebKitGTK | Not measured; no native run | **Pending / unqualified** |
| `linux-arm64` (glibc) | WebKitGTK | Not measured; no native run | **Pending / unqualified** |
| Desktop musl | No approved engine target | Explicitly unsupported by the plan | **Unsupported**, not a pending support claim |

The TUI retains its existing eight-RID intent, including `linux-musl-x64` and `linux-musl-arm64`; this Windows managed baseline is not new qualification of those packages. Each claimed desktop RID needs real engine/architecture runs covering local chunks/fonts, standard library styling/SVG, native menus/close/dialogs, clipboard/drop, focus/IME, resize/DPI and installed-package startup. Mock-browser or cross-compilation results cannot fill this table. A narrower initial public release requires user approval.

## Blockers and next step

1. The executed build, audited tests and website build have no observed failure. The full Release test baseline is **incomplete by safety choice**; establish explicit runtime-root injection or audit additional no-profile/fake-provider fixtures before broadening it. Do not use the active user's profile to turn a skip into a pass.
2. **M0 feasibility is accepted** after parent diff/evidence review and an independent installed-tool rerun. Next is the scoped M1 tracked TUI rename and reference/guardrail update, followed by promotion of proven desktop wiring. No upstream NeoAstra change or custom launcher was required.
3. Desktop feature, cross-head persistence/ownership, full native platform, performance and release gates remain pending. Prototype integration success is not parity, and the existing application has not yet been renamed.
