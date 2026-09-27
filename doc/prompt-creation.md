# Desktop create-only agent prompt authoring

In **Settings → Agent prompts**, select **New agent prompt** for an exact saved owned session. Choose project or user-global publication explicitly, enter a canonical ID, name, optional description, body and composition mode, acknowledge cross-scope composition, then review and confirm. Nothing is copied from the truncated effective catalog preview.

IDs are 1–64 lowercase ASCII letters/digits/hyphens, start with a letter and cannot be Windows device names. Name, description and body limits are respectively 128, 512 and 16,384 UTF-16 units. Invalid Unicode, unsupported controls, unsupported scopes and missing required fields are refused before publication. Only Agent sources are writable; system metadata is not an authoring field. Shared serialization trims outer metadata/body whitespace; the runtime parser normalizes source newlines and composition uses its normal separator.

Precedence is **built-in → user-global → project**. Replace starts a new composition chain; append adds to lower-precedence bodies. Supplied name/description override lower metadata in that chain. Without system metadata, append may inherit the lower system ID; otherwise it defaults to `default`. Creating a user-global source does not defeat an existing project override. This surface discloses possible same-ID shadowing rather than scanning all roots or claiming an isolated effective prompt.

## Publication and ownership

- Host epoch, exact saved-session header/creation time and bounded catalog ownership/archive checks resolve roots. The request carries identity assertions, never an arbitrary output path. Root naming comes from `AgentPromptCatalog.ResolveRoots`; serialization and observed ancestor/reparse policy come from `PromptResourceStore`.
- Existing revision-returning `CreateAsync` reads/hashes collision files. The new shared `TryCreateAsync` route deliberately does not read collision bytes or manufacture a revision. It shares `TextFileCodec`'s owner gate, staging policy and the **same non-overwriting publication helper used by Missing-revision saves**. Existing destinations, Windows ordinary case aliases and racing creators cannot be overwritten. No force, suffix renaming or replace fallback exists.
- The Desktop host owns one original task through canceled RPC waits. Its existing catalog-writer gate excludes concurrent host import/archive/rename/session-delete work during validation/publication. Closing admission drains the original before host disposal. There is no provider probe, session activation, config/default change or prompt application.
- The App owns draft fields and captured target across dismissal/navigation. Discard of unsaved fields requires an explicit second action. Review captures current catalog, host capability and navigation lifetime; same-value ABA invalidates that confirmation. Original pending/uncertain requests cannot be edited, discarded, rebased or retried. Only correlated validated late evidence reaches the originating capability; observer faults cannot replace the outcome.
- Outcomes distinguish created, conflict, refused and uncertain. Created is **not** proof active/ready. Catalog refresh (reopening the catalog) and choosing a next-Send prompt are separate explicit actions. No creation result refreshes the workspace/catalog or changes a retained Send.
- Host identity evidence is separate from publication proof. A fully correlated changed-host response invalidates only the originating capability, but its claimed created/conflict/refused/busy/closed outcome cannot settle the original operation: it remains uncertain with no new draft, retry or discard. An explicit correlated `stale_epoch` response is a definite pre-admission refusal. Conversely, a response from the **original** host can confirm its original publication after navigation, host/capability replacement or ABA, even if the originating capability is now invalid; replacement authority is never modified by that response.
- After a settled created/conflict/refused outcome, an explicit new blank draft can capture a new selection while retaining the earlier original. Eight originals maximum per App lifetime; no eviction. Uncertainty blocks successors. Retention is in memory, not reload persistence, durable receipts or automatic reconciliation.

## Limits

Observed reparse checks are not a filesystem sandbox against external ancestor/path swaps. The no-overwrite publication is not edit CAS or an atomic catalog/filesystem snapshot. External catalog writers remain outside the host gate. OS calls may block, and shutdown retains the original rather than claiming cancellation stopped publication. Cross-process races are qualified only for new destination publication, not arbitrary external path mutation. Windows case-sensitive-directory policy, native WebView lifecycle and real-provider behavior are not qualified by the disposable-root/fake-bridge tests.

Editing/deleting existing prompts, system-prompt management, defaults and broad prompt CRUD remain open.

## Static presentation language

The create-only form, scope/mode labels, precedence help, acknowledgment and
review/discard/confirmation controls use the six supported [WebView languages](webview-localization.md).
Prompt IDs, names, descriptions, bodies, mode/scope protocol values and retained
original JSON remain literal; locale adds no trimming or newline transformation.
Dirty drafts, review and an open discard choice survive language changes without
new requests or owner remounts. Controller outcome/diagnostic messages remain
literal; only known earlier phase labels translate. Pending/uncertain publication
keeps its original request and the same permanent retry fence in every language.
