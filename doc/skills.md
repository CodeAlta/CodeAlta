# CodeAlta skills

CodeAlta can discover filesystem skills that follow the Agent Skills `SKILL.md` layout. A skill is a directory containing a required `SKILL.md` file and optional helper files such as `scripts/`, `references/`, and `assets/`.

## Where to put skills

CodeAlta discovers skills from these roots, in precedence order:

1. Project-specific CodeAlta skills: `<project>/.alta/skills/`
2. Project portable Agent Skills: `<project>/.agents/skills/`
3. User CodeAlta skills: `~/.alta/skills/`
4. User portable Agent Skills: `~/.agents/skills/`
5. CodeAlta built-in skills bundled with the application

Use `.agents/skills/` for portable skills that should work across Agent Skills-compatible clients. Use `.alta/skills/` for CodeAlta-specific variants or workflows.

CodeAlta includes a built-in `codealta-plugin-runtime` skill with plugin authoring and troubleshooting guidance plus real sample plugin folders. User/project skills with the same name shadow built-ins by normal precedence rules.

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

Skill names must be lowercase alphanumeric text plus hyphens, with no leading/trailing hyphen or consecutive hyphens, and must match the containing directory name.

## Runtime behavior

For local/raw backends, CodeAlta advertises only compact skill metadata in instructions and exposes `codealta.skills.activate` so the full skill body is loaded only when needed. Activation reads content; it never executes scripts automatically.

Agent role profiles can list associated skills with `skills:` or `codealta.skills:` frontmatter. CodeAlta treats those entries as ranking hints in the advertised catalog, not as automatic full prompt preloads.

Codex and Copilot backends manage their own native skills. CodeAlta therefore does not inject CodeAlta-managed skill advertisements or the `codealta.skills.activate` tool into Codex/Copilot sessions.

Activated local-runtime skills are recorded in the session journal so thread info can show loaded skills after resume. Before compaction, the activated payload remains ordinary replayed conversation context; after compaction, CodeAlta can rehydrate activated skills into composed instructions so compacted sessions keep skill guidance without duplicating current context. If a compacted prompt-integrated skill is missing on disk, CodeAlta preserves the history and reports the missing path.

## Browsing skills in the UI

Use `/skills` or `/skill`, the command palette entry, or `Ctrl+G Ctrl+K` to open the skills browser. The browser can show combined, current-project, or user/global discovery scopes; it includes source kind, validation state, model visibility, shadowing, provenance paths, diagnostics, and a refresh action.

Use **Activate** on a valid, unshadowed skill to load it into the selected local/raw backend thread through the host-owned runtime path. CodeAlta records the activation in the local session journal and injects the canonical skill payload as session context; it does not paste raw skill text into the prompt draft before compaction. Codex and Copilot threads are excluded because those providers manage their own native skills.

Use **Open SKILL.md** to open the selected skill in the existing editor. Because `SKILL.md` is a Markdown file, it uses the editor's normal Markdown/TextMate highlighting path.

The browser also lists related authoring files under the selected skill's `scripts/`, `references/`, and `assets/` folders. Select a related file and use **Open related** to edit it in the same editor flow. CodeAlta only opens these files for inspection/editing; activation still does not execute scripts automatically. Related-file and activation file lists use CodeAlta's `XenoAtom.Glob`-based walker, so project `.gitignore` rules are respected.

Use **New skill** to scaffold a new Agent Skills-compatible skill. When a project is selected, CodeAlta creates it under the project CodeAlta-specific root (`<project>/.alta/skills/<name>/`); otherwise it falls back to the user CodeAlta root (`~/.alta/skills/<name>/`). The scaffold includes `SKILL.md` plus empty `scripts/`, `references/`, and `assets/` folders, then opens `SKILL.md` in the editor.

## Live tool commands

When a CodeAlta-managed session has the `alta` live tool, agents and plugins should use the singular `skill` command group for discovery and activation:

```text
alta skill list [--project <id|slug|path>]
alta skill show <skill-name> [--project <id|slug|path>]
alta skill activate <skill-name> --session <thread-id>
```

`alta skills activate` and `alta skills_activate` are compatibility aliases for older instructions; prefer `alta skill activate` in new guidance. Activation uses the same host-owned runtime path as the UI and records the activation in the local session journal. If the target provider owns a native skill system that CodeAlta cannot inject into, the command returns an unsupported-capability JSONL diagnostic instead of silently succeeding.

## Validation and collisions

Discovery validates frontmatter, naming, required descriptions, resource paths, and duplicate names. Higher-precedence skills win; lower-precedence duplicates are kept inspectable as shadowed skills but are not advertised to the model.

Skill resource reads must use relative paths and cannot escape the skill root.
