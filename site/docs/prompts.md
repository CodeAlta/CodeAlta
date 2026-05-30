---
title: Prompts and Instructions
---

# Prompts and Instructions

CodeAlta separates the instructions that shape an agent session into two file-backed layers:

- **System prompts** are the invariant host/agent rules. They are stored as `<id>.system-prompt.md` files under an `instructions/system` folder.
- **User prompts** are selectable session profiles. They are stored as `<id>.prompt.md` files under an `instructions/prompts` folder and are shown in the footer **Prompt:** selector.

The active user prompt is included when a session starts or resumes. Its optional `system` property chooses which system prompt id to use; when omitted, CodeAlta uses `default`.

## Source locations and precedence

Prompt resources are layered in this order:

1. Built-in resources shipped with CodeAlta.
2. User-global resources under `~/.alta/instructions/`.
3. Project-local resources under `<project>/.alta/instructions/`.

Each root has the same layout:

```text
instructions/
  system/
    default.system-prompt.md
    my-custom-system.system-prompt.md
  prompts/
    default.prompt.md
    reviewer.prompt.md
  template.yml        # optional advanced defaults
```

If multiple roots contain the same prompt or system id, the later source overrides the earlier one: project overrides global, and global overrides built-in. The selector displays effective prompts in built-in, global, then project order.

## User prompt frontmatter

A user prompt must define `name`; CodeAlta uses it as the display label. `description` is optional. `system` is optional and defaults to `default`.

```markdown
---
name: AnotherPrompt
system: my-custom-system
description: Instructions for a specialized project session.
---
You are the active CodeAlta project agent for this session.

Handle the user's scoped task directly. Keep changes focused and report concrete outcomes, evidence, and blockers.
```

The file name supplies the prompt id. For example, `reviewer.prompt.md` creates or overrides the prompt id `reviewer`.

## Selecting and editing prompts

Use the **Prompt:** selector below the prompt editor to choose the prompt for a draft or session. CodeAlta stores draft prompt preferences per global/project scope and session prompt selections in session-local state, alongside provider/model/reasoning preferences.

Open the prompt manager with `Ctrl+G Ctrl+H` or `/prompt`. The default **Prompt** tab lists built-in, global, and project user prompts; shows shadowed overrides; and lets you create, edit, save, or delete global/project prompt files. The **System Prompt** tab uses the same source precedence for system prompt files and lets you inspect built-ins while editing only global/project override files. Built-in prompt and system prompt files are visible for inspection but read-only. Creating a global or project prompt/system prompt with the same id as a lower-precedence resource overrides it.

The live tool exposes the same resources for automation. Use `alta prompt list --scope all` to discover prompt ids, add `--system` to target system prompts, and add `--verbose` to include full prompt content. Use `alta prompt show <prompt-id>` for one prompt, `alta prompt create <prompt-id> --scope global|project --name <name> --stdin` to create a complete prompt file, and `alta prompt edit <prompt-id> --scope global|project` to get or update an editable file path. For one-off sends, `alta session send <session-id> --prompt-id <prompt-id> --stdin` runs the session with that user prompt; omit `--prompt-id` to keep the default/current selection.

## System prompts and templates

System prompt files carry host-level behavior and should be short, stable, and explicit. User prompts are better for workflow-specific session behavior.

Advanced users can add `template.yml` under `~/.alta/instructions/` or `<project>/.alta/instructions/` to choose default ids and generated context parts:

```yaml
version: 1
system: default
prompt: default
skills: true
project_context: true
runtime_context: true
tool_guidance: true
```

Use templates sparingly. Most day-to-day customization should be done with user prompts and selected from the footer.
