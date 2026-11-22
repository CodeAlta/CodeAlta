# Health analysis: gpt-6.2-astra

Scope read: at `2a3fd8388`, I followed automation persistence from the runner to the node and its restart tests; provider settings from the TUI, desktop form and model-server page into the TOML writer; and skill discovery from Catalog into the session and Settings. I also checked the CI selector, the search kernel oracle, and the Code Mode restart and Git snapshot tests named in the CI signals. Not read: most provider transports, React components, fleet protocol operations and plugins. I followed individual decisions through their consumers rather than inferring findings from file sizes. This was a source and history review; no build, test or application was run.

## Findings

### Keep automation state small enough for its own reader
- **Lens / area / action:** persistence · automations · fix
- **Cost:** One automation can stay below its 100-run history limit yet produce a file that restarts with **all** automations paused, no run history and no trigger watermarks.
- **Evidence:** A dry run records call descriptions (`src/CodeAlta.Automations/AutomationRunGuard.cs:208`); each run can retain 50 of them, up to 2,000 characters each (`src/CodeAlta.Automations/AutomationState.cs:51`, `src/CodeAlta.Automations/AutomationService.Autonomy.cs:132`). Thus 90 completed runs can carry 9,000,000 bytes of ASCII call descriptions alone. Retention counts runs, not bytes (`src/CodeAlta.Automations/AutomationState.cs:313`); `Save` writes the entire serialization (`src/CodeAlta.Automations/AutomationState.cs:467`). The reader rejects anything over 8,388,608 bytes and the constructor substitutes empty, paused state (`src/CodeAlta.Automations/AutomationState.cs:195`, `src/CodeAlta.Automations/AutomationState.cs:444`, `src/CodeAlta.Automations/AutomationState.cs:219`).
- **Or else:** Rejecting oversized, hand-edited files is sensible. It does not explain rejecting a file this writer just successfully produced. The retention test adds short records and checks their count, not the serialized size (`src/CodeAlta.Desktop.Tests/AutomationServiceTests.cs:308`).
- **Change:** Make the read limit an invariant of a successful save. When over budget, discard the oldest completed history first. Never evict active runs, the pause choice or trigger cursors to make history fit; if essential state alone cannot fit, report a failed save and preserve the last readable file.
- **Removes / adds:** Removes the write-success/read-empty outcome; adds a size check on the bytes already serialized, pressure-only trimming and a round-trip test. No file format change.
- **Confidence:** verified
- **First step:** Seed a valid state just below the limit, apply one legitimate run update that crosses it, and fix the write path in the same change.
- **Verify:** Reopen after the update: the file is at most 8 MiB, the newest run and a pre-existing watermark survive, and an unpaused store remains unpaused. The existing malformed-file test should still pause.
- **Risk:** The count limits become upper bounds, not a guarantee that 100 unusually large runs will survive. This does not by itself recover oversized files from older versions.

### Do not grant an automation occurrence that the node could not record
- **Lens / area / action:** failure handling · automation claims · fix
- **Cost:** Two successive instances of a node can each grant the same scheduled occurrence when the first instance's claim write failed. The scheduler can start real work on the first grant.
- **Evidence:** `TryClaim` inserts into memory, calls `Save`, then unconditionally returns true (`src/CodeAlta.Automations/AutomationClaims.cs:153`). `Save` swallows I/O and access errors (`src/CodeAlta.Automations/AutomationClaims.cs:253`); on restart an unreadable claim file and a missing one both mean “no claims” (`src/CodeAlta.Automations/AutomationClaims.cs:135`, `src/CodeAlta.Automations/AutomationClaims.cs:239`). A grant reaches `BeginAsync` (`src/CodeAlta.Automations/AutomationService.cs:797`, `src/CodeAlta.Automations/AutomationService.cs:810`). The restart regression checks only a **successful** write (`src/CodeAlta.Desktop.Tests/AutomationPlacementTests.cs:59`). Commit `09ee9a3ef` fixed this same failed-write assumption for the hourly call counter; its store returns the result of the save (`src/CodeAlta.Automations/AutomationState.cs:420`).
- **Or else:** A server grant can protect a pooled run. A node with no server assignment has no second arbiter, and a single-machine placement is also allowed to proceed when its server is away (`src/CodeAlta.Node/NodeAutomations.Server.cs:45`, `src/CodeAlta.Node/NodeAutomations.Server.cs:75`). In-memory ownership is enough only until the node exits.
- **Change:** Grant a new occurrence only after its claim is durable. Distinguish storage unavailable from “another run took it,” including on opening an existing unreadable ledger. Preserve the ledger; let the existing scheduling paths skip with the actual reason or defer where they already support it.
- **Removes / adds:** Removes a silent grant and the assumption that a read error proves absence; adds a local claim outcome and an error-path test, not another journal or a fleet protocol change.
- **Confidence:** verified
- **First step:** In the placement fixture, make the claim file unwritable while leaving automation state writable. Refuse the start on that path; keep the successful restart test.
- **Verify:** Zero runner starts on the failed claim, no “already claimed” diagnosis for an I/O failure, and after storage recovers one durable grant that a reopened store refuses to duplicate.
- **Risk:** This deliberately prefers a missed or deferred occurrence to duplicate side effects. A pooled event must not be marked seen on storage failure.

### Delete the TUI's lossy copy of a provider definition
- **Lens / area / action:** duplication · Catalog and TUI provider editing · delete
- **Cost:** Saving a change to one provider can remove settings from an untouched provider in the shared `config.toml`. The TUI's copy omits **six** properties that the Catalog copy preserves.
- **Evidence:** Both opening and saving an editor item go through its private copier (`src/CodeAlta.Tui/ViewModels/ModelProviderEditorItemViewModel.cs:228`, `src/CodeAlta.Tui/ViewModels/ModelProviderEditorItemViewModel.cs:243`). Comparing the initializer at `src/CodeAlta.Tui/ViewModels/ModelProviderEditorItemViewModel.cs:377` with `src/CodeAlta.Catalog/CodeAltaConfigStore.cs:3920` gives `ContextSize`, `Sandbox`, `CodeMode`, `Server`, `Request` and `ModelRequest` only in the Catalog copy. The dialog rebuilds **every** provider (`src/CodeAlta.Tui/Views/ModelProvidersDialog.cs:1459`); the coordinator saves the set (`src/CodeAlta.Tui/App/ProviderFrontendCoordinator.cs:97`). The TOML merge removes dropped *modeled* keys (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:2222`, `src/CodeAlta.Catalog/CodeAltaConfigStore.cs:2240`). Commit `77da0870b` already had to update both copies to retain model visibility rules.
- **Or else:** Unknown keys and unsupported provider types are specially preserved. These six are known settings; the omission happens before the writer. For example, a `llama-cpp` definition with no explicit URL changes its endpoint selection when it loses `Server` (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:2974`).
- **Change:** Use `CodeAltaConfigStore.CloneProviderDefinition` in both editor paths. Keep the TUI's deliberate edits and normalizations; it needs no controls for desktop-only settings.
- **Removes / adds:** Deletes the 64-line TUI copier and its independent field inventory; adds one preservation test, no new API.
- **Confidence:** verified
- **First step:** Extend the real read/editor/save round trip at `src/CodeAlta.Tests/ProviderFrontendCoordinatorTests.cs:67` with an untouched server-backed provider and a nondefault modeled field, then replace the copier.
- **Verify:** Editing another provider leaves the server reference and nondefault field present when the config is reopened; the existing tests for *omitted* defaults still pass.
- **Risk:** The Catalog copy deep-copies nested settings rather than sharing the TUI's references. Verify nested request values, not object identity.

### Hold the configuration file lock while checking a structured edit's revision
- **Lens / area / action:** concurrency · desktop provider settings · fix
- **Cost:** Two cooperating writers of the same config can both succeed on one starting revision, with the second whole-provider-set save erasing the other's edit.
- **Evidence:** The text editor holds `LockGlobalConfig()` across its revision check and save (`src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:124`). The structured path promises the same rule but uses only the service instance's `_gate` around the check and the read of raw definitions (`src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:29`, `src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:927`, `src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:943`). A form callback subsequently writes its captured list (`src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:684`). Catalog takes the named lock at save time, then replaces the provider set with that list (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:1902`, `src/CodeAlta.Catalog/CodeAltaConfigStore.cs:1932`). It cannot merge a provider edit made after the form's read.
- **Or else:** `_gate` does serialize calls to one service. It does not serialize a second CodeAlta instance sharing the file; the existing named mutex was expressly made for this case (`src/CodeAlta.Catalog/ConfigFileLock.cs:7`). Task `4ctn` is about registry preparation **after** a save, not this lost file update (`.alta/tasks/2026-11-20-apply-provider-settings-while-another-provider-reads-its-mod-4ctn.md:11`).
- **Change:** Hold the existing file mutex across revision check, raw read, edit, save and determination of the saved revision. Release it before provider preparation, change notifications or other work that does not own the file.
- **Removes / adds:** Removes the weaker of two implementations of the revision guarantee; adds one bounded lock scope and a two-writer regression, no new lock.
- **Confidence:** verified
- **First step:** Fix the synchronous `Mutate` path used by the structured form. Exercise two service instances on one temporary config, using barriers rather than a timing sleep to order the writes.
- **Verify:** For two revision-checked edits based on the same version, exactly one returns `ok`, the other returns `conflict`, and the winner's value remains on disk.
- **Risk:** The mutex is thread-affine and reentrant (`src/CodeAlta.Catalog/ConfigFileLock.cs:12`); do not span an `await`. This guarantee is for cooperating CodeAlta writers, not arbitrary external editors.

### Stop saving runtime provider defaults when adding a model server
- **Lens / area / action:** boundaries · desktop model-server setup · simplify
- **Cost:** “Use in sessions” on this computer can turn absent settings on **every** other provider into explicit overrides, even with only one writer and no concurrent edits.
- **Evidence:** The local route invokes `SaveLocalProvider` (`src/CodeAlta/Desktop/ModelServers/DesktopModelServers.cs:263`). Its implementation reads all definitions with `LoadGlobalProviderDefinitions(includeDisabled: true)` and writes that list back (`src/CodeAlta/Desktop/DesktopApplication.cs:909`, `src/CodeAlta/Desktop/DesktopApplication.cs:917`). That read completes every definition (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:1879`); for Codex it supplies, among other things, the API URL, concurrency and discovery defaults (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:3026`). The form already takes the opposite path: `RawDefinitions` specifically avoids materializing defaults when saving (`src/CodeAlta/Desktop/Rpc/GlobalConfigRpc.cs:976`).
- **Or else:** A runtime needs the completed values. An editor must preserve the difference between “take the default” and “pin today's value.” The existing editor test explicitly checks that `api_url` and `model_discovery` are absent after saving an untouched default (`src/CodeAlta.Tests/ProviderFrontendCoordinatorTests.cs:67`).
- **Change:** Apply the local server-provider edit to the raw definitions under the store's existing synchronous update operation. Complete definitions only when applying them to the runtime after the write.
- **Removes / adds:** Removes the completed-model-to-file round trip; reuses the raw-edit rule and `UpdateGlobal` (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:275`), with one fixture and no additional persistence model.
- **Confidence:** verified
- **First step:** Put the synchronous edit behind the existing local save callback, and test a config containing a minimal Codex provider plus the server being added.
- **Verify:** The new provider resolves to the model server; after re-reading the file the untouched provider still has no `api_url`, `max_concurrent_requests` or `model_discovery`.
- **Risk:** Keep disabled and unknown provider sections. This is distinct from the revision race: the single-writer round trip alone must pass.

### Give Skills settings the same inherited project scope as a session
- **Lens / area / action:** duplication · Catalog skill discovery and desktop settings · merge
- **Cost:** Select one sub-project and the two views disagree: a repository skill can be offered to the session but absent from Settings; a user skill disabled in the repository can show as enabled in Settings but not be offered.
- **Evidence:** Session discovery includes inherited roots, effective added folders and the union of their disabled names (`src/CodeAlta.Catalog/Skills/SkillCatalogQuery.cs:168`). The Settings listing reads only the child's disabled names and uses a one-element project root list (`src/CodeAlta.Catalog/Skills/SkillManagementService.cs:92`, `src/CodeAlta.Catalog/Skills/SkillManagementService.cs:149`). The RPC passes the raw child `SkillFolders`, not the existing `EffectiveSkillFolders` (`src/CodeAlta/Desktop/Rpc/SkillsRpc.cs:517`, `src/CodeAlta.Catalog/ProjectDescriptor.cs:201`), and returns the resulting enabled flag (`src/CodeAlta/Desktop/Rpc/SkillsRpc.cs:166`). This also contradicts the documented inheritance table (`site/docs/monorepos.md:95`). Completed plan `.alta/plans/2026-11-10-monorepo-sub-projects.md:199` covered inheritance; commit `c96bdf30e` fixed the composer by moving it off the Settings listing. It did not change the latter's discovery inputs.
- **Or else:** Management **should** show invalid, disabled and shadowed entries that a model never sees. That distinction doesn't explain omitting the root of an otherwise valid inherited skill, or saying a skill is enabled when its ancestor disabled it.
- **Change:** Share the computation of effective read roots and disabled names, not the final list. Retain Settings' broader inclusion flags and per-file diagnostics. Continue to resolve create, delete and configuration writes against the selected project's own roots only.
- **Removes / adds:** Removes the second definition of a project's discovery scope; adds effective read inputs to the management query and a sub-project fixture. No change to plugin discovery or the `alta skill` contract.
- **Confidence:** verified
- **First step:** Carry ancestor disablement into management listing/detail for a sub-project, with a user skill disabled only by the parent. Then reuse the effective roots for inherited packages without treating them as child-owned.
- **Verify:** The child listing marks the parent-disabled skill unavailable; a parent's valid package is visible in both listing and offered skills. Own-root editing and “unreadable configuration” diagnostics still work.
- **Risk:** A repository-level disable must not become a writable child override. Treat an inherited location as read-only from the child; do not simply swap in the session's filtered list.

### Retire the raw skill-scanning branch that only its fixtures call
- **Lens / area / action:** dead code · Catalog skills · delete
- **Cost:** Five production files (1,074 lines) and five fixture files (804 lines, 25 test methods) implement scanning, reading metadata and parsing ignore rules without a product caller. They give maintainers a second, different answer to “what is a valid skill?”
- **Evidence:** The scan explicitly disclaims use by discovery (`src/CodeAlta.Catalog/Skills/RawSkillCandidateReader.cs:65`). The candidate reader calls its own parser (`src/CodeAlta.Catalog/Skills/SkillCandidateMetadataReader.cs:128`); the ignore reader calls its own parser (`src/CodeAlta.Catalog/Skills/SkillIgnoreSourceReader.cs:135`). A repository search for `RawSkillCandidateReader|SkillCandidateMetadataReader|SkillMetadataParser|SkillIgnoreSourceReader|SkillIgnoreSourceParser` found references only within those components and their tests, including no configuration or registration naming them. The live catalog walks roots and parses ignore files independently (`src/CodeAlta.Catalog/Skills/SkillCatalog.cs:408`, `src/CodeAlta.Catalog/Skills/SkillCatalog.cs:537`). Its accepted `triggers` and `version` fields (`src/CodeAlta.Catalog/Skills/SkillCatalog.cs:42`) are rejected by the raw parser (`src/CodeAlta.Catalog/Skills/SkillMetadataParser.cs:212`). History shows the five components added September 25–26, with no subsequent change to their five source files.
- **Or else:** These might be an intentional opt-in API for an outside consumer. Catalog is not a package (`src/CodeAlta.Catalog/CodeAlta.Catalog.csproj:5`) and is not the documented plugin authoring contract (`doc/architecture.md:50`), but the types are public; an out-of-tree binary reference cannot be ruled out by grep.
- **Change:** Confirm the compatibility commitment first. Retire the raw ignore reader/parser and their two fixtures as one independent unit, then the raw candidate scan/metadata pair and their fixtures. If public compatibility is promised, deprecate first and remove at the permitted release boundary. Do not substitute these readers for live discovery merely to find them a caller.
- **Removes / adds:** Ultimately 10 files and 1,878 lines; no replacement scanner, dependency or tests. Keep the shared frontmatter types and the `SkillFrontmatterReader` used by discovery and MCP.
- **Confidence:** likely (missing check: whether the unshipped-as-a-package public API has a supported out-of-tree consumer).

### Include the hardware-dependent search oracle in platform CI
- **Lens / area / action:** test selection · CI and search · fix
- **Cost:** Neither filtered OS job selects the existing test that compares the actual machine's optimized search kernels with the scalar reference; changing which CPU those runners use cannot make the comparison run there.
- **Evidence:** Windows and macOS select files only by literal `OperatingSystem.IsWindows()`, `IsMacOS()` or `IsLinux()` (`.github/workflows/ci.yml:40`, `.github/workflows/ci.yml:68`, `.github/workflows/ci.yml:81`). The correct test selects kernels by `Dp.IsSupported` and the x86 intrinsic flags, then checks their values against the scalar loop (`src/CodeAlta.Tests/Search/SearchVectorTests.cs:20`, `src/CodeAlta.Tests/Search/SearchVectorTests.cs:37`). I applied the selection predicate to the tracked test files: `SearchVectorKernelTests` was not selected; there is no second declaration to select it. The ARM path really uses a different instruction (`src/CodeAlta.Search/SearchVectorKernels.cs:96`).
- **Or else:** The unfiltered Linux job does run the test, but only for the kernels its own CPU exposes. Tests with a fake `windows: true` argument often *are* sufficient on Linux; this test cannot fake `Dp.IsSupported`.
- **Change:** Include this existing hardware-dependent method in the platform filter explicitly. This evidence justifies one method, not bringing back the entire slow matrix or imposing categories on all tests.
- **Removes / adds:** Removes one hole in CI selection; adds one named test per filtered runner, no extra CI job or fixture.
- **Confidence:** verified
- **First step:** Add the method to the filter and keep the test's existing kernel list in its diagnostic output.
- **Verify:** The filter matches the method on both OS jobs; the next run on hardware supporting `Dp` reports `arm64-sdot` among the kernels checked. No application change is required.
- **Risk:** Selection does not prove runner hardware support. I did not measure runner CPU capabilities or claim a kernel defect.

## Healthy: keep as it is

- The composer now asks the runtime which skills a send will actually use (`src/CodeAlta/Desktop/Rpc/SkillsRpc.cs:136`, `src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs:4420`). Keep that fix; the remaining mismatch is in management's project inputs, not a reason to move filtering back into React.
- `ConfigFileLock` already provides a reentrant, path-specific interprocess lock (`src/CodeAlta.Catalog/ConfigFileLock.cs:12`). The text editor demonstrates the correct check-and-save scope; the registry refresh is deliberately outside it. No new coordinator is needed.

## Tempting, and wrong here

- Do not drop the TOML merge in favor of serializing typed documents wholesale. The merge preserves settings the current host does not model (`src/CodeAlta.Catalog/CodeAltaConfigStore.cs:2174`); it cannot preserve a known setting that a lossy editor has told it to remove.
- Do not treat every “Fix” prefix or every CI rerun as evidence of a recurring defect. In particular, none of the automation or provider scenarios above requires a conclusion about defect rates.

## Observations

- The Code Mode sleep test failed on November 12 (`.alta/report/2026-11-22-health/signals/ci-runs.md:24`). On November 13, `f535964b9` added a flush of stopped runs' writers; shutdown now waits for those writes (`src/CodeAlta.Orchestration/Runtime/Scripts/CodeModeRunRegistry.cs:829`). The old failure is not evidence that the current code still loses that write. The database recovery timing failure already has a concrete work item (`.alta/tasks/2026-11-14-make-the-database-recovery-test-independent-of-the-speed-of-6zpc.md:22`).
