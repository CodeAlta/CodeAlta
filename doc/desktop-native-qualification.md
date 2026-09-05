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
