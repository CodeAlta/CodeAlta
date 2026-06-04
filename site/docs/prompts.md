---
title: Prompts and Instructions
---

# Prompts and Instructions

CodeAlta separates the instructions that shape an agent session into two file-backed layers:

- **System prompts** are the invariant host/agent rules. They are stored as `<id>.system-prompt.md` files under a `prompts/system` folder.
- **Agent prompts** are selectable session profiles. They are stored as `<id>.prompt.md` files under a `prompts/agents` folder and are shown in the footer **Agent:** selector.

The active agent prompt is included when a session starts or resumes. Its optional `system` property chooses which system prompt id to use; when omitted, CodeAlta uses `default`.

## Source locations and precedence

Prompt resources are layered in this order:

1. Built-in resources shipped with CodeAlta.
2. User-global resources under `~/.alta/prompts/`.
3. Project-local resources under `<project>/.alta/prompts/`.

Each root has the same layout:

```text
prompts/
  system/
    default.system-prompt.md
    my-custom-system.system-prompt.md
  agents/
    default.prompt.md
    plan.prompt.md
    reviewer.prompt.md
  template.yml        # optional advanced defaults
```

If multiple roots contain the same prompt or system id, the later source overrides the earlier one: project overrides global, and global overrides built-in. The selector displays effective prompts in built-in, global, then project order.

## Built-in agent prompts

CodeAlta ships two built-in agent prompts:

- **Default** (`default`) is the normal implementation/build agent. It can execute approved work, including plan files, while following the project safety and verification rules.
- **Plan** (`plan`) is a prompt-enforced planning workflow. It works through understanding, focused exploration, design/validation, final-plan, and review/handoff phases; writes a first-class plan under `<project>/.alta/plans/yyyy-mm-dd-{plan-name}.md`; asks the user to review the plan with `alta ask`; and can hand off to `default` for execution.

Plan mode is an instruction profile, not a hard filesystem sandbox. The prompt tells the agent to avoid mutating project files except for the plan file, but normal host permissions still apply. If git is active and `.alta/plans/` is not ignored, the built-in prompts treat plan files as repository artifacts: Plan mode keeps the saved plan current, and Default keeps it synchronized with implementation progress and includes it in related commits.

When built-in prompts delegate child sessions, they default to the driving session's model and reasoning effort with `--same-model-as`. Simple or single-phase work stays in the driving session; read-only research children may run in parallel, while implementation children that write files run sequentially with parent inspection and verification between steps. If the user requests a specific sub-session agent, provider, model, or reasoning effort, the prompts instruct agents to honor it when available and state limitations otherwise.

## Agent prompt frontmatter

An agent prompt must define `name`; CodeAlta uses it as the display label. `description` is optional, but it should be concise and decision-useful because CodeAlta surfaces effective prompt descriptions in generated model context for prompt/mode discoverability. `system` is optional and defaults to `default`.

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

Use the **Agent:** selector below the prompt editor to choose the agent prompt for a draft or session, or press `Ctrl+T` / run `/next_prompt` to cycle to the next available prompt and wrap back to the first. CodeAlta stores draft prompt preferences per global/project scope and session prompt selections in session-local state, alongside provider/model/reasoning preferences.

Open the prompt manager with `Ctrl+G Ctrl+H` or `/prompt`. The default **Agent Prompts** view lists built-in, global, and project agent prompts; shows shadowed overrides; and lets you create, edit, save, or delete global/project prompt files. The **System Prompt** tab uses the same source precedence for system prompt files and lets you inspect built-ins while editing only global/project override files. Built-in prompt and system prompt files are visible for inspection but read-only. Creating a global or project prompt/system prompt with the same id as a lower-precedence resource overrides it.

The live tool exposes the same resources for automation. Use `alta prompt list --scope all` to discover prompt ids, add `--system` to target system prompts, and add `--verbose` to include full prompt content. Use `alta prompt show <prompt-id>` for one prompt, `alta prompt create <prompt-id> --scope global|project --name <name> --stdin` to create a complete prompt file, and `alta prompt edit <prompt-id> --scope global|project` to get or update an editable file path. Use `alta session set_agent <session-id> --prompt-id <prompt-id>` to change a session's agent prompt, or `alta session send <session-id> --prompt-id <prompt-id> --stdin` for a one-off send/session switch. Omit `--prompt-id` to keep the default/current selection.

## System prompts and templates

System prompt files carry host-level behavior and should be short, stable, and explicit. Agent prompts are better for workflow-specific session behavior.

Advanced users can add `template.yml` under `~/.alta/prompts/` or `<project>/.alta/prompts/` to choose default ids and generated context parts:

```yaml
version: 1
system: default
agent: default
skills: true
project_context: true
runtime_context: true
tool_guidance: true
```

Use templates sparingly. Most day-to-day customization should be done with agent prompts and selected from the footer.
