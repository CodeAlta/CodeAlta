# Catalog, configuration and state

`CodeAlta.Catalog` owns the durable state of the user: configuration, projects, spaces, the session-view records of
journals, the application database and the editable text files. It has no UI and no provider code. Paths come from
`CatalogOptions`; nothing derives them elsewhere.

## Global root and instance state root

Two roots, because two instances may run on one profile:

- The **global root** (`CatalogOptions.GlobalRoot`, `~/.alta` by default) holds what the user configures and every
  instance shares: settings, credentials, projects, spaces, prompts, skills, plugins, caches.
- The **state root** (`CatalogOptions.StateRoot`) holds what one running instance alone writes: sessions, the
  application database, view state, drafts, scratchpads. It is the global root for the normal instance and `~/.alta/dev/`
  for the developer instance (`--dev`, `CodeAltaInstanceProfile` in `CodeAlta.Hosting`).

Why: two processes must never write the same journal or database, but a developer instance must see the user's real
providers, prompts and plugins. Each instance holds the lock of its state root (`alta.lock`), so one instance of each
kind runs at a time. A host with a separate state root only creates the coordinator
`AGENTS.md` of the global root when it is missing, and on its first run seeds its view state from the normal instance's
per-project provider preferences.

| Under the global root | Owner |
| --- | --- |
| `config.toml` | `CodeAltaConfigStore`: settings, providers, plugins, automations of the user. |
| `mcp.json` | MCP plugin: server connections ([MCP](mcp.md)). |
| `auth/` | Provider auth managers: subscription credentials, token caches. |
| `projects/`, `spaces/` | `ProjectCatalog`, `SpaceCatalog`: one Markdown file per project or space. |
| `prompts/`, `skills/`, `plugins/`, `plugin-data/` | Prompt resources, skills, source plugins, what plugins store with `Services.State`. |
| `cache/` | Rebuildable data: model metadata, plugin builds, downloaded tools and models (`speech/`, `search/`, `runtime/`). |
| `worktrees/`, `color-schemes/` | Session worktrees (by default), desktop color schemes. |

| Under the state root | Owner |
| --- | --- |
| `sessions/yyyy/MM/dd/<id>.jsonl`, `<id>.attachments/` | Session journals and their files ([runtime](runtime.md#journals-are-the-source-of-truth)). |
| `data/alta.sqlite3`, `data/backups/`, `data/search.sqlite3` | `ApplicationDatabase` and its copies; the desktop search index (derived). |
| `saved_statistics_after_deleted_sessions/` | `StatisticsArchiveStore`: statistics of retired sessions (see below). |
| `ui-state.yaml`, `saved_prompts/` | `SessionViewCatalog` view state, `PromptDraftStore` unsent drafts. |
| `scratchpad/` | Files where agents write down long instructions; named in the runtime context, cleared at startup. |
| `automations.json`, `work_items.json` | Desktop automation state; links of tasks and plans to sessions. |
| `alta.lock`, `mcp_url.txt` | The instance lock, the address of the desktop MCP server while it runs. |

Protocol traces stay under the global root (`sessions/traces/`). Journals, traces, auth files and caches hold prompts,
tool output, paths and credentials: they are private user data.

## Project-local state

| Under `<project>/.alta/` | Content |
| --- | --- |
| `config.toml` | Project overrides, CLI profiles of its sessions (`[identity.cli.*]`), project automations, `[search] enabled`. |
| `issues.toml`, `local/issues.toml` | Issue views and the Jira link of the team; the user's own on top (`local/` is git-ignored). |
| `mcp.json`, `plugins/`, `plugin-data/`, `skills/`, `prompts/` | Project MCP servers, plugins, plugin data, skills, prompts. |
| `plans/`, `tasks/` | Plans and follow-up tasks, Markdown with a front matter. |
| `worktrees/` | Worktrees kept inside the repository when the user chose so (git-ignored). |

Skills are also read from `.agents/skills/` of the project and of the home folder.

## Configuration

`config.toml` of the user is the one settings file; a project's `.alta/config.toml` overlays what it may override
(providers, plugins, skills disablement, which is additive: a project cannot re-enable a globally disabled skill).
`CodeAltaConfigDocument` maps its tables, each read by a typed method of `CodeAltaConfigStore`:

| Table | Holds |
| --- | --- |
| `[chat]`, `[providers.<key>]` | Default provider; provider definitions (`type`, `enabled`, `model`, credentials by environment variable name). |
| `[plugins.<id>]` | Enablement, plus plugin-owned policy (`[plugins.mcp]`, `[plugins.mcp.servers.*]`) the store does not model. |
| `[skills]`, `[tools]` | Disabled skills; tools every session does without. |
| `[sessions]`, `[review]`, `[commits]`, `[work_items]` | Title mode and prompt suggestions; review before commit; co-author trailer; tasks and plans. |
| `[worktrees]`, `[storage]`, `[speech]`, `[search]` | Where worktrees go; scratchpad and session retention; desktop speech and search. |
| `[models]` | Model tiers and fallback (below). |
| `[automations.<guid>]` | Desktop automations, read and written by the desktop, kept by every typed save. |
| `spaces = [...]` on a resource, `[skills.spaces]`, `[prompts.spaces]` | The spaces of providers, MCP servers, plugins, skills and prompts. |

- Defaults are not written, and a table left empty is removed, so the file holds only what the user chose.
- The bundled template (`src/CodeAlta.Catalog/DefaultConfig/config.toml`, every provider disabled) is written only when
  the file is missing; an existing file is never reconciled with new defaults. A provider entry without `enabled` is
  enabled; one with missing credentials is skipped at startup and stays in the file.
- Session-specific choices (provider, model, effort, tier, mode) are not configuration: they live in the local state of
  the journal, so a session keeps them when defaults change.
- MCP connections are JSON (`mcp.json`) so they stay compatible with other clients; TOML only overlays policy.
- **Overlays.** `CatalogOptions.Config` (`CodeAltaConfigOptions`) tells every store built from those options how to read
  the user's configuration: the file, or none (`IgnoreUserConfig`), then each `CodeAltaConfigOverlay` in order (the
  `CODEALTA_CONFIG_CONTENT` variable, then the `-c key=value` of a command line), tables merged key by key and the later
  layer winning on values and arrays; a strict reading that rejects settings no part of CodeAlta reads; and a read-only
  store whose saves fail. Overlays are never written, errors name the layer at fault, and a project file still overlays
  the result. Why options and not a mode of the process: a headless run reads the user's settings without changing
  them while Desktop runs on the same file.

### Shared-file rules

Several stores, both instances and the user's editor write the same files. The rules:

- **One writer at a time.** Every save reads, changes and writes under `ConfigFileLock`, a named system mutex derived
  from the path, shared across processes. Writers outside the store take the same lock
  (`CodeAltaConfigStore.EditConfigFile`, `LockConfigFile`).
- **Saves keep what they do not model.** Typed saves edit the table model of the existing file, so unknown keys,
  plugin policy, key order, line endings and end-of-line comments stay. `ConfigTableSyntax` edits one value in the text
  when comments and blank lines must stay too (space assignments, MCP policy, issue views).
- **Readers take no lock**, so a write publishes with `StagedFileReplacement` (a rename with POSIX semantics):
  a reader finds the old file or the new one, never none, which it would read as an empty configuration. `File.Replace`
  is not used for these files because it moves the target away first.
- **Editable documents** (`TextFileCodec`) save against the raw-byte revision they loaded: a stale save conflicts
  instead of overwriting, encoding, BOM and line endings are preserved, and invalid bytes are refused rather than
  replaced. This is cooperative: a writer outside the codec can still race the final check, so a revision is not an
  authorization token.
- Project and space files are edited over the bytes that were read (`ProjectCatalog.UpdateSpacesAsync` writes only the
  `spaces` line); a file changed meanwhile is read again.

### Reads and the cache of the parsed files

Configuration is read on paths that run with every prompt (title mode, disabled tools, tiers, space assignments).
`CodeAltaConfigStore` and `ProjectCatalog` therefore keep what each file parses to (`ParsedFileCache`, per store
instance) and parse again only when the file's stamp (existence, length, last write time) changed. A file written in
the last three seconds is compared by content at each read, since two writes within a clock step can leave the same
stamp. A file that does not read or parse is never kept, so every read reports the error again. `LoadGlobal()` and
`LoadProject()` return deep copies the caller owns; the narrow reads return values. Every save reads its file again
under its lock and forgets what the store kept.

### Model tiers and fallback

`[models.tiers]` lists, for `complex`, `standard` and `quick` work, ordered targets
`<provider>:<model>@<effort>,<effort>`: the provider is a configured key, which names an account, and efforts are in
order of preference. `[models.fallback]` (`enabled`, off by default; `on = ["quota", "unavailable"]`) says whether a
failed run continues on the next target ([runtime](runtime.md#provider-fallback)).

```toml
[models.tiers]
complex  = ["claude-code:opus@high,xhigh", "codex:gpt-6.1@xhigh"]
standard = ["claude-code:opus@medium", "codex:gpt-6.1@medium"]
quick    = ["claude-code:haiku@low"]

[models.fallback]
enabled = true
```

- `ModelTierResolver.Resolve` gives the first target whose provider is enabled, ready and lists the model, with the
  first effort the model supports. A missing or unready provider is skipped, never a validation error.
- `ModelTierScope` is what a session uses: the tiers of its space when the space sets its own (`new_sessions.tiers`,
  with its own `new_sessions.fallback`), else the global ones, filtered to providers of the space.
- The tier of a session is part of its local state, not configuration.

## Projects

`ProjectCatalog` keeps one `<slug>.md` per project under `projects/` (legacy `<slug>/readme.md` is still read): a stable
id, slug, name, path, archive state and the ids of its spaces besides the default one. The current directory at launch
is offered as an in-memory project until a session is created for it. A filesystem root is a valid project.

The working directory of a session ties it to its project; a session in a git worktree records the worktree beside it
(`worktreeDirectory` of its summary).

## Spaces

A space is a named group of projects and a setup for their sessions. `SpaceCatalog` keeps one Markdown file per space
under `spaces/`: the file name is the id (derived from the first name, kept on rename), the front matter has `name`,
`icon`, `color`, `order` and optional settings, the body is the description. The default space (`default`) holds every
project, comes first, cannot be deleted, and has a file only when it is customized. A file that cannot be read as a
space is left out, never hiding the others; an invalid value written by hand is dropped with the rest kept.

Space settings (`SpaceSettings`): `new_sessions` (agent prompt, tiers, fallback), `identity` (Git author and signing,
SSH key, GitHub CLI profile, Jira, CLI profiles), and `tools.disabled`. Only paths and names of environment variables
are stored, never a secret.

- **The space of a session** is fixed at creation and recorded in the first line of its journal (`space_id`): the
  calling session's space for an agent, the shown space for the window, the project's only space for an automation. A
  session without one (older, or from the TUI) is in its project's space when the project has exactly one, else in the
  default space. `SpaceCatalog.ResolveSessionSpace` applies these rules and never throws for what a journal says.
  Why fixed: a session must never gain, mid-life, providers, tools or an identity its space did not give it.
- **Resources per space.** Providers, MCP servers, plugins, skills and prompts carry an optional `spaces` list in
  `config.toml`; absent means every space (`SpaceScope.IsAvailable`). `SpaceFilter` is what a session's space has,
  applied before global and project enablement, so a project cannot bring into a space what the space does not have.
  Only the window writes these lists.
- **Identity.** In the desktop (`CodeAltaHostOptions.AppliesSpaceIdentities`), `SessionIdentityResolver` turns the
  space identity, with the CLI profiles of the project, into one environment (`AgentProcessEnvironment`) kept by the
  attachment and applied to every process the session starts (shell, Claude Code CLI, jobs, Git plugin tools, stdio MCP
  servers, terminals). A profile separates accounts; it is not a sandbox.

Which space a window shows, and its tabs, belong to the page's local storage, not the catalog.

## The application database

One SQLite file per instance, `<state root>/data/alta.sqlite3`, owned by `ApplicationDatabase` (one per host, shared
with the session catalog and the plugin runtime). It holds the session projections, bookmarks, provider caches of
external conversation headers, and the tables of plugins.

It is **not a cache that may be deleted**: plugin tables hold data that cannot be rebuilt (statistics of deleted
sessions), and bookmarks point to the sessions of one instance. Nothing deletes the file.

- **Concurrency.** WAL mode, so readers never wait for the writer. Writes go one at a time through a FIFO queue, each a
  `BEGIN IMMEDIATE` transaction; a write that awaits another write is refused, since it would wait for the queue it
  holds.
- **Owners and versions.** Each user (`session_cache`, `plugin:<id>`) records its schema version in `app_meta` and
  migrates under its table prefix; objects outside the prefix are refused. A version from a newer build is an error,
  not a downgrade; owners whose data is derived (the session list) drop and rebuild their tables instead.
- **Damage.** Malformed or missing files are never deleted: derived tables are rebuilt first; otherwise the file is moved
  aside and replaced by the newest valid copy, as a new `Generation`, and plugins migrate again. Replacement waits for
  the service's connections to close on every platform, because an old connection would remove the new file's WAL/SHM.
- **Copies.** `data/backups/`, made with the SQLite backup API about daily and checked; a copy records its copy number,
  so a restored file is recognized after a restart.

**Session projections.** The session list (`SessionJournalSqliteCache`) and the session index
(`SessionJournalIndexStore`) let normal operation work without reading journals (see
[runtime](runtime.md#journals-are-the-source-of-truth)). A row is the projection of its journal only at the journal's
stamp: writers append to the journal first, then write the row with the stamp taken under the journal gate; a reader
that finds another stamp reprojects. A listing reads named short columns; per-session detail (provider state, last
state record) is a separate table read for one session. The journals stay authoritative: every table here is made again
from them when missing, damaged, replaced or from another layout.

**Statistics archive.** `StatisticsArchiveStore` keeps the statistics of retired sessions in its own file under the
state root, with an initialized marker. It is irreplaceable, so it never goes through the recovery of the application
database, and its loss requires explicit repair instead of a silent fresh start.

## Other stores

- `SessionViewCatalog` owns `ui-state.yaml`: open sessions, selection, project preferences, and frontend-owned trees
  (`frontend_layouts`, `logical_tabs`) that every frontend preserves even when it cannot render them. Malformed YAML is
  an error, never dropped data.
- `PromptDraftStore` owns unsent text; `PromptImageAttachmentStore` copies prompt images beside the journal with
  create-new semantics, never overwriting.
- `PromptResourceStore` owns agent and system prompt files: built-in prompts cannot be written, project scope never
  falls back to global, linked path components are refused.
- `SkillManagementService` creates skills and edits skill enablement; skill discovery is in [skills](skills.md).
