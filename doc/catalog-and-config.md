# Catalog, configuration, and state

CodeAlta keeps user-owned durable state under a global root and project-local `.alta` directories. The default global root is `~/.alta`, resolved from the current user's profile directory by `CodeAltaOwnedServices` and `CodeAltaHost` unless a host overrides it.

## Global root layout

`CatalogOptions` defines the current global layout:

| Path under `~/.alta` | Owner | Purpose |
| --- | --- | --- |
| `config.toml` | `CodeAltaConfigStore` | Global chat defaults, providers, and plugins. |
| `mcp.json` | MCP plugin | Global MCP server connection definitions. |
| `projects/` | `ProjectCatalog` | Markdown project descriptors keyed by project slug. |
| `checkouts/` | `ProjectCatalog` helpers | Default checkout root used by catalog planning APIs. |
| `machines/` | Catalog model | Machine-specific override profile root. |
| `agents/` | Catalog model | File-backed agent-definition root used by host-owned coordinator setup. |
| `cache/` | Process/runtime services | Machine-local cache root, including `cache.sqlite3` session-listing projections, refreshed model metadata, and plugin build cache. |
| `sessions/` | Agent session runtime and session catalog | Date-sharded session journals and optional protocol traces. |
| `saved_prompts/` | Catalog `PromptDraftStore` | Unsent session and global/project new-session text drafts. |
| `ui-state.yaml` | Frontend view-state service | Open/selected tabs, session/model preferences, theme, and shell view state. |
| `plugins/` | Plugin runtime | User-scoped source plugin packages. |
| `skills/` | Skill catalog | User-scoped CodeAlta skill roots. |
| `sessions/internal/` | Work-session catalog | Internal session linkage descriptors still read by the catalog. |

The runtime creates directories as needed. Provider auth managers also write under `~/.alta/auth/`, for example subscription credentials and direct-provider token caches. Protocol traces, session journals, auth files, and provider caches can contain prompts, tool arguments, model output, file paths, command output, or credentials; treat them as private user data.

## Project-local state

Project-local CodeAlta state lives under `<project>/.alta/`:

| Path | Purpose |
| --- | --- |
| `<project>/.alta/config.toml` | Project-local config overrides. |
| `<project>/.alta/mcp.json` | Project-local MCP server connection definitions. |
| `<project>/.alta/plugins/<package-id>/plugin.cs` | Project-scoped trusted source plugin packages. |
| `<project>/.alta/skills/<skill-name>/SKILL.md` | Project-scoped skills. |

The skill catalog also reads `<project>/.agents/skills/` and `~/.agents/skills/` as common `SKILL.md` roots. Use `.alta` roots when the content depends on CodeAlta-specific behavior.

## Configuration model

The top-level TOML document has these active sections:

```toml
[chat]
default_provider = "my_provider"

[providers.my_provider]
type = "openai-responses"
enabled = true
model = "model-id"
api_key_env = "MY_PROVIDER_API_KEY"

[plugins.statistics]
enabled = true

[skills]
disabled = ["ilspy-decompile"]
```

`CodeAltaConfigDocument` maps these sections to:

- `chat`: chat-level defaults, currently the default provider key;
- `providers`: configured model-provider documents keyed by provider key;
- `skills`: skill enablement settings, currently normalized disabled skill names;
- `plugins`: plugin enablement keyed by built-in id or source package id, plus plugin-owned policy such as `[plugins.mcp]`.

Legacy `[acp]` and `[acp.*]` blocks are no longer active configuration. `CodeAltaConfigStore` ignores them while preserving their TOML text when saving normalized config so existing user data is not deleted.

Global config is loaded from `~/.alta/config.toml`. Project config is loaded from `<project>/.alta/config.toml` when a project scope is active. The provider-management UI edits the same global file and validates TOML before saving. During startup, `CodeAltaConfigStore` creates the bundled default template only when the global config file is missing; existing user config files are not reconciled with newly bundled defaults.

MCP server connection definitions are intentionally separate JSON files: global `~/.alta/mcp.json` and project `<project>/.alta/mcp.json`. CodeAlta loads one file per scope, global first and then project overlay; a project server key shadows a global server key. New files use the default `mcpServers` format, while supported existing `mcpServers`/`servers` flavors are detected and preserved. String values in stdio `env` entries and HTTP/SSE `headers` may reference process environment variables with `${NAME}` placeholders, keeping secrets out of JSON when desired. TOML under `[plugins.mcp]` is a policy overlay only: per-server enablement, per-tool `disabled_tools`/`allowed_tools`, prompt caps, timeouts, and direct-exposure controls. Runtime availability is finite diagnostic state rather than durable config: failed or timed-out MCP servers are reported as unavailable for the current request/test, and diagnostics/results are redacted before display. Dynamic MCP tool exposure is progressive: configured servers appear compactly in the prompt, and `alta mcp activate <id>*` enables tools from selected servers for the current session; see [MCP support](mcp.md).

### Provider enablement

The bundled template is `src/CodeAlta.Catalog/DefaultConfig/config.toml`; all built-in provider entries are explicitly disabled there. For user-authored provider entries, an omitted `enabled` value normalizes to `true`.

Provider registrations are skipped at startup when required credentials or provider-specific authentication settings are missing. Skipped providers remain in config and can be corrected through the UI or TOML editor.

### Project overrides

Project-local config can override effective chat/provider behavior for that project. The frontend also persists session-specific provider/model/reasoning selections in `ui-state.yaml`, so an existing session view can keep its selected runtime after global defaults change.

Skill disablement is additive rather than an override: a skill named in global `[skills].disabled` is disabled everywhere, and a skill named in project `[skills].disabled` is disabled only for that project. A project config cannot re-enable a globally disabled skill.

## Project catalog

`ProjectCatalog` stores project descriptors under `~/.alta/projects`. Current saves use flat `<slug>.md` files; the loader still reads older `<slug>/readme.md` descriptor paths so existing user state can be opened.

A project descriptor includes stable id, slug, display name, project path, archive/visibility state, and timestamps. At launch, the current directory is exposed as a selectable in-memory project when no persisted descriptor already exists for that path; it is not written to `~/.alta/projects` until a session is created for it. Opening a folder upserts a descriptor for that path, then the shell selects it in the sidebar. Filesystem roots are valid project paths: because they have no leaf folder name, the catalog stores a safe synthetic project `name` and uses the normalized root path (for example `D:\` or `/`) as the sidebar display name.

## Session and session-view storage

CodeAlta uses two related records for active work:

- **Session-view descriptors** are catalog/runtime metadata for global, project, and internal session views. They carry title, project reference, provider/model/reasoning preferences, parent/created-by attribution, and last-active timestamps. Some persisted readers and file names still use `SessionView`/`SessionId` for legacy compatibility.
- **Agent session journals** are CodeAlta-owned JSONL files under `~/.alta/sessions/yyyy/MM/dd/<session-id>.jsonl`. They contain replayable normalized `AgentEvent` records plus raw snapshot events for `local.sessionSummary`, `local.sessionState`, `codealta.sessionHeader`, and `codealta.sessionState`.

`SessionViewJournalStore` still reads and writes the legacy header/state event names in the same journal used by the agent runtime. This avoids maintaining separate provider-bound state files for the same session while preserving existing user data.

For fast startup/sidebar loading, CodeAlta also maintains a machine-local SQLite projection cache at `~/.alta/cache/cache.sqlite3`. The JSONL files under `~/.alta/sessions` remain authoritative: missing or corrupt cache databases are recreated from journals, stale rows whose journals were deleted are pruned, and externally added or changed journals are reconciled after the initial cached load. If the SQLite database is busy or locked during startup, CodeAlta reports a startup error instead of scanning journals or deleting the locked database.

Optional protocol traces are written to `~/.alta/sessions/traces/<session-id>.trace` only when a provider has tracing enabled. Credential headers are redacted, but trace files can still contain sensitive prompts, outputs, tool arguments, and streamed protocol updates.

## Prompt drafts and view state

`CodeAlta.Catalog.PromptDraftStore` owns unsent text under `CatalogOptions.PromptDraftsRoot` (`<GlobalRoot>/saved_prompts`). Files remain plain text named `saved_prompt_{sanitizedScopeKey}.md`: session IDs are used unchanged as keys, global new-session drafts use `__draft__:global`, and project new-session drafts use `__draft__:project:{trimmedProjectId}`. Project drafts still live in the **global** root. Filename sanitization replaces the current OS's invalid filename characters with `-`; colon handling therefore remains OS-dependent, preserving existing filenames rather than migrating them.

Legacy reads use the shared codec's strict BOM-aware Unicode decoding. Writes remain UTF-8 **without BOM** (including when replacing a BOM-bearing legacy file), retaining nonblank text, whitespace, Unicode and newlines literally. Null or whitespace-only text requests deletion. Missing and empty-file revisions differ. There is no new draft schema, and unsent image lists remain transient TUI state; this store does not persist images.

The TUI owns debounce, selection, events, bindings and image lists. Its persistence coordinator tracks one ordered work chain for saves **and deletes**. Canceling a debounce never abandons a started write: subsequent edits/clears and disposal join it, and only acknowledged commits advance the baseline revision. Conflicts (including same-mtime external edits/deletions) and I/O failures retain pending text/deletion intent; a flush returns failures rather than marking it saved. Retries use the original acknowledged revision, never the conflicting external revision. Storage errors before an initial baseline is read fail closed. Pending text is in memory, not a recovery journal: preserve it before exiting after an error.

The synchronous TUI projection/clear/delete seams join an awaitable flush on prompt selection synchronization, send-related clear actions and session deletion. Composer clear acknowledges deletion **before** changing text or images; failed pre-admission clear retains both the original composer and its pending text, not a tombstone. Send and queue paths complete that clear before admission, so a storage-failure retry cannot enqueue an already accepted prompt. New-session draft clear is deferred until provider/content checks and history loading succeed. Conflicts return no acknowledged snapshot, only the observed revision; successful deletion returns a missing snapshot. Deletion removes the final file entry, including a normal or dangling symbolic link, without deleting the linked target.

Coordinator disposal stops new edits and joins pending work, throwing on failed flush. `ShellFrontendHost` still disposes the application's owned services if frontend disposal fails, preserving the original error and aggregating a second owned-cleanup failure instead of losing either. This is not a complete shutdown confirmation/recovery UX. Successful flush means the file operation completed, not guaranteed survival of power loss. The TUI composition injects the **same** `TextFileCodec` instance into drafts, file editors and ask reviews, serializing cooperating saves/deletes even when an editor opens a draft's path. External writers/path-link changes retain the documented final-check/commit race, not cross-process atomic compare-and-swap.

The frontend stores view state in `~/.alta/ui-state.yaml`, including open/selected tabs, theme and navigator settings, and session-specific model preferences.

`ShellStateStore` is a UI-session projection of currently open shell state; it is not a replacement for the durable catalog, session journals, or runtime-owned session state.

### Prompt image attachment copies

`CodeAlta.Catalog.PromptImageAttachmentStore` owns encoded image persistence and the neutral `PromptImageAttachment` / `PromptImageAttachmentReference` records. The TUI retains localized title/factory helpers, clipboard/DIB/Skia decoding, composer state, `PromptSubmission` snapshots, and conversion to `AgentInputItem.LocalImage`. No terminal toolkit dependency is introduced into Catalog.

Images are copied into `<GlobalRoot>/sessions/<session CreatedAt UTC yyyy>/<MM>/<dd>/<sanitizedSessionId>.attachments/`; an unset creation date uses the current UTC date. Filenames remain `{currentUTC:yyyyMMddHHmmssfff}-{index:00}-{sanitizedTitle}-{first8ImageId}{extension}`, with titles capped at 48 characters and collision suffixes `-2`, `-3`, etc. Source image files are not moved or rewritten. Atomic `FileMode.CreateNew` creation never overwrites a preexisting entry; only native already-exists errors retry, bounded to 1000 candidates. Write/flush, permission and other I/O errors do not become collision retries.

The entire batch's path/payload fields are validated before writes: nonempty bytes and titles, an image MIME type without parameters, 1–128 ASCII letter/digit/hyphen/underscore IDs, and 1–16 ASCII alphanumeric extension characters (optional leading dot). Session IDs must sanitize to 1–200 characters. Sanitization neutralizes both platforms' separators, invalid filename characters and control characters; unusable session components are rejected. This preserves normal legacy names without promising compatibility for malformed directly constructed payloads. Validation does not decode or sniff image contents.

Queue and dispatch ownership are unchanged: submission/queue snapshots copy byte arrays before composer clear; saving happens on actual dispatch, **before** run augmentation and runtime submission. A failed/cancelled save attempts to remove only files that batch successfully created (including partial writes), never preexisting collision entries. Once save succeeds, its references remain valid across composer/queue clear, downstream augmentation cancellation or dispatch failure; retries may create another copy. Empty directories and files that cannot be removed due to filesystem errors may remain after rollback. This does not add image garbage collection, reference counting, recovery or restart durability for unsent images.

The store accepts a **trusted backend root**, not a renderer file grant or RPC authorization. Its lexical validation and create-new behavior are not a filesystem sandbox: externally replaced directories/symlinks/files can race persistence or rollback, and cleanup cannot prove file identity after external replacement. Successful writes do not guarantee survival of power loss. Renderer authorization and broader ownership/recovery remain separate work.

## Editable text files

`CodeAlta.Catalog.TextFileCodec` owns text-file reads, attached-file lookup, and conditional saves/deletes. The TUI composition shares one instance between prompt drafts, file-editor tabs and attached ask-file reviews. Editors, undo/selection state, file watchers, conflict dialogs, and ask comments remain TUI presentation; the store does not depend on terminal controls or LiveTool contracts.

Loads return the complete text, encoding/BOM information, an advisory timestamp, and a SHA-256 identity of the raw bytes. The missing-file revision differs from an empty file. Saves require the previously observed revision, including for creation: stale edits, external deletion, and same-timestamp content changes return a conflict without overwriting the target. File-editor Overwrite confirms the revision observed in the conflict, so another intervening edit conflicts again. Conflicts retain dirty editor text; ask-file saves return failure without marking the review saved or allowing the save-and-submit action to proceed.

UTF-8 with or without a BOM and BOM-bearing UTF-16/UTF-32 in either byte order are supported. Literal CRLF/LF/CR sequences, mixed line endings, Unicode, and final-newline presence are preserved; no newline conversion is applied. Invalid encoded bytes or invalid Unicode are rejected rather than decoded/encoded with replacement characters that could silently corrupt a saved document. BOM-less non-UTF-8 encodings are not inferred.

Saves write a uniquely created temporary file in the destination directory, recheck content identity, and replace the target only after staging succeeds and cancellation is checked. Read-only targets are rejected, Unix mode bits are retained on replacement, and file symbolic links are followed rather than replaced with regular files. Failed/cancelled pre-commit saves leave the original intact and attempt to remove only their own staging file; cancellation after commit does not turn an acknowledged save into a cancellation. This is not a crash-recovery journal or a durability guarantee across power loss.

Unix staging starts with user-only read/write mode. On Windows, staging for an existing file is created with a copy of the original's effective discretionary ACL (DACL), protected from additional directory inheritance, before any edited bytes are written. That DACL remains in place after the write handle closes for the revision check and replacement; directory readers do not gain additional DACL permissions through staging. Failure to read or apply the DACL aborts rather than falling back to directory permissions. Windows new-file saves have no original DACL and inherit directory permissions. This is DACL preservation, not a full security-descriptor/EFS copy or protection against privileged access; concurrent external security-metadata changes are not covered by content revisions.

The instance-owned save gate serializes cooperating editors only. Other processes/instances or noncooperating writers can change a file or path link between the final check and replacement; there is no portable cross-process atomic compare-and-swap guarantee. Content identity also does not distinguish a delete/recreate sequence that restores identical bytes. Atomic replacement changes file identity, so hard-linked aliases do not receive the replacement contents. Do not treat timestamps, revisions, or paths as authorization tokens.

Attached-file lookup retains the trusted TUI behavior: an absolute path is accepted; relative lookup tries the session working directory then project root, with the first supplied root/current directory as fallback. It is not a project containment or renderer authorization boundary. A future desktop adapter must validate project/session association and explicit external-file access before calling these APIs.

## Plugin and skill state

Source plugins are discovered from user and project roots and are enabled by default unless disabled in config or safe mode is active. CodeAlta owns generated plugin-root build files and plugin build manifests under its roots; plugin package directories should contain only package-owned source/content files.

Skills are plain directories containing `SKILL.md` plus optional helper files. Discovery validates metadata, applies precedence/shadowing and config enablement, and reads resources without executing scripts. Disabled skills remain visible in the management UI, but are not advertised to models and cannot be activated through UI, runtime, or live-tool paths.
