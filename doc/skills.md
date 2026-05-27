# Skills

CodeAlta skills are filesystem directories that contain a required `SKILL.md` file and optional helper files such as `scripts/`, `references/`, and `assets/`. Discovery reads metadata and files; activation never executes scripts automatically.

## Roots and precedence

CodeAlta discovers skills from these roots, highest precedence first:

1. `<project>/.alta/skills/`
2. `<project>/.agents/skills/`
3. `~/.alta/skills/`
4. `~/.agents/skills/`
5. built-in skills bundled with CodeAlta

Plugin-contributed skill roots are included through plugin resource contributions with runtime-assigned scope and precedence. Project/user skills with the same `name` shadow lower-precedence entries. Shadowed skills remain inspectable in the UI when requested, but only the winning valid skill is advertised for activation.

Use `.alta/skills/` when a skill depends on CodeAlta behavior. The `.agents/skills/` roots are common `SKILL.md` roots that CodeAlta can read without making them CodeAlta-owned.

CodeAlta ships a built-in `codealta-plugin-runtime` skill with plugin authoring and troubleshooting guidance plus sample plugin folders.

## `SKILL.md` format

Each skill directory must be named after the skill and contain YAML frontmatter:

```markdown
---
name: dotnet-test-fix
description: Diagnose and fix failing .NET tests with minimal churn.
---

# Dotnet test fix

Use this skill when a task is primarily about failing .NET tests.
```

Rules enforced by `SkillCatalog`:

- the directory must contain `SKILL.md`;
- `name` is required and must match the containing directory name;
- names are lowercase alphanumeric text plus hyphens, with no leading/trailing hyphen or consecutive hyphens;
- `description` is required;
- frontmatter fields are validated against the allowed top-level fields;
- resource paths must be relative and must not escape the skill root.

## Discovery and validation

`SkillCatalog` resolves root providers, walks skill roots with gitignore-aware file walking, parses frontmatter, validates descriptors, applies shadowing, and returns descriptors with provenance and diagnostics.

Resource listing excludes VCS directories and respects ignore rules. Activation payloads include the canonical `SKILL.md` body and a bounded list of related files. Related files are made available for inspection; they are not run.

## Runtime behavior

For agent-runtime sessions, CodeAlta advertises compact skill metadata in instructions. The full skill body is loaded only when the user, UI, plugin, or agent activates a skill through the host-owned runtime path.

Activation:

1. resolves the valid unshadowed skill for the current project/user scope;
2. builds a canonical payload and related-file list;
3. records activation in the session journal;
4. injects skill context into the selected agent-runtime session;
5. emits visible activity/session updates.

Provider-managed native skill sessions return an unsupported-capability diagnostic when CodeAlta cannot inject a CodeAlta-managed skill payload into that session.

Activated agent-runtime skills are replayed from the session journal after resume. Before compaction, activated payloads remain ordinary replayed context. After compaction, CodeAlta can rehydrate activated skills into composed instructions so compacted sessions retain skill guidance without duplicating current context. If a compacted skill payload references a missing on-disk skill, CodeAlta preserves history and reports the missing path.

## UI

Open the skills browser with `/skills`, `/skill`, the command palette entry, or `Ctrl+G Ctrl+K`.

The browser can show combined, current-project, or user/global scopes. It displays source kind, validation state, model visibility, shadowing, provenance paths, diagnostics, and a refresh action.

Available actions:

- **Activate** loads a valid unshadowed skill into the selected CodeAlta session through `SessionRuntimeService`.
- **Open SKILL.md** opens the selected skill document in the editor.
- **Open related** opens selected files under `scripts/`, `references/`, or `assets/` for inspection/editing.
- **New skill** scaffolds a skill under `<project>/.alta/skills/<name>/` when a project is selected, otherwise under `~/.alta/skills/<name>/`.

The scaffold creates `SKILL.md` plus empty `scripts/`, `references/`, and `assets/` directories, then opens `SKILL.md` in the editor.

## Live-tool commands

Managed sessions with the `alta` live tool use the singular `skill` command group:

```text
alta skill list [--project <id|slug|path>]
alta skill show <skill-name> [--project <id|slug|path>]
alta skill activate <skill-name> --session <session-id>
```

`alta skills activate` and `alta skills_activate` remain aliases for existing prompt text; prefer `alta skill activate` in new guidance. Activation uses the same runtime path as the UI and records the activation in the session journal.

## Plugin skill roots

Plugins can expose skill roots with resource contributions:

```csharp
public override IEnumerable<PluginResourceContribution> GetResources()
{
    yield return Resources.SkillRoot("skills");
}
```

Plugin skill roots are trusted local resource roots. Project-scoped plugin roots are visible only for the matching project scope.
