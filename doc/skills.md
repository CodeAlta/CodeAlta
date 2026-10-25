# Skills

A skill is a folder with a `SKILL.md` (YAML frontmatter, then Markdown instructions) and optional helper files
(`scripts/`, `references/`, `assets/`). The prompt advertises skills by name and description; the body enters a session
only when the user, the window, a plugin or the agent activates the skill. Activation never runs a script: helper files
are offered for reading. CodeAlta reads the Agent Skills format, so skills written for other agents work unchanged.

| Part | Where |
| --- | --- |
| Discovery, validation, shadowing, enablement, cache | `src/CodeAlta.Catalog/Skills/` (`SkillCatalog`, `SkillCatalogQuery`, `SkillFrontmatterReader`) |
| Built-in skills | `src/CodeAlta.Catalog/BuiltinSkills/` (`codealta-plugin-runtime`, `document-conversion`) |
| The prompt's skill list | `AgentInstructionTemplateProvider` (`src/CodeAlta.Orchestration/Runtime`) |
| Activation | `SessionRuntimeService`, `alta skill` (`src/CodeAlta.LiveTool`) |
| Preferred skills | `DesktopSkillUsage` (`src/CodeAlta/Desktop`), from the Statistics plugin |

## Roots and precedence

Highest first:

1. `<project>/.alta/skills/`, then `<project>/.agents/skills/` and `<project>/.github/skills/`
2. `~/.alta/skills/`, then `~/.agents/skills/` and `~/.copilot/skills/`
3. Skill roots of plugins (resource contributions), with the scope of their plugin
4. Built-in skills

A skill with the name of a higher one is shadowed: it stays inspectable, and only the winner can be activated. Use
`.alta/skills/` for skills that depend on CodeAlta; the `.agents` and GitHub Copilot folders are shared with other
agents and are read as those agents write them (the Copilot folders accept any top-level field, the others refuse an
unknown one). Skills that MCP servers publish are a separate origin, named by server and URI, that never shadows these
(see [MCP](mcp.md)).

## Validation

- `name` is required, matches the folder, and is lowercase letters, digits and single hyphens.
- `description` is required.
- Frontmatter fields are checked against the allowed set. The fields other skill tools add (`argument-hint`,
  `user-invocable`, `disable-model-invocation`, `context`) are accepted; `disable-model-invocation: true` hides the
  skill from the model while the user can still activate it.
- Resource paths are relative and stay inside the skill folder; walking respects `.gitignore` and skips VCS folders.

## Enablement and spaces

Skills are enabled by default. `[skills] disabled = [...]` in the global `config.toml` disables names everywhere; a
project's `config.toml` adds to it and cannot re-enable. `[skills.spaces]` in the global file assigns names to spaces;
a skill outside the session's space is disabled whatever folder it comes from. `SkillCatalogQuery.ForSession` builds
the one query a session uses for activation, instructions and `alta skill`, so what an agent sees is what it can
activate.

## The prompt

The instructions are rebuilt at each send and must stay byte-identical while nothing changes, or the provider's
prompt cache is lost. So:

- **The catalog is cached.** `SkillCatalog` keeps each parsed `SKILL.md` until its length or write time changes, and
  each root's walk until a folder, a `.gitignore` or the repository it read changes. A walk is not kept when something
  it read changed in the last five seconds, since that change could be missing from it. Shadowing, enablement and
  spaces are applied again on every call.
- **The list is sorted and does not depend on the turn.** With many skills it is shortened
  (`[search] prompt_catalog`): the whole list up to 12 skills, one line per skill above, counts per scope above 60,
  with a hint to `alta search`. The TUI and a desktop without search keep the whole list.
- **Built-in skills come first**, with their whole description: they say how CodeAlta itself works.
- **Preferred skills** (desktop) are the skills used most recently, from Statistics: an exponential moving average of
  uses over 28 days, full weight in the session's project, a quarter elsewhere in its space. The ranking is read once
  per local day from uses before today, and a session keeps its first ranking for the life of the process, so the
  prompt changes as seldom as possible.

## Activation

`alta skill activate <name>` (or the window, or a plugin) resolves the winning enabled skill for the session's
project and space, builds the payload (the `SKILL.md` body and a bounded list of its files), journals the activation,
and injects it into the session with a visible activity. A resumed session replays its activations from the journal.
After a compaction, active skills are restated in the composed instructions instead of their payloads being carried as
history; a skill whose folder is gone keeps its history and is reported missing.

`alta skill list|show|activate` is the agent's interface; `list` stays within the `alta` tool's output caps and points
to `alta search --kind skill` where the desktop keeps a search index.
