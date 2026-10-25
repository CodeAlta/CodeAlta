---
title: Agent Prompts
---

# Agent Prompts

An agent prompt is a workflow you choose for a session: implement directly, plan first, review only, coordinate child sessions, or follow the release checklist of a project. CodeAlta ships **Default** and **Plan**; you can add your own, for all projects or for one.

{.table}
| Layer | Files | Use it for |
|---|---|---|
| **Agent prompt** | `prompts/agents/<id>.prompt.md` | How a session works: its mode, delegation, review rules, when to ask you. |
| **System prompt** | `prompts/system/<id>.system-prompt.md` | Stable rules shared by several agent prompts. |

> [!TIP]
> Most customization belongs in an agent prompt. Change a system prompt only for rules that apply across workflows.

The agent prompt of a session is included when it starts or resumes. Its `system` field chooses the system prompt, `default` when omitted.

## Prompt composition at a glance

The selected agent prompt drives the composition: its body becomes developer instructions, and its frontmatter chooses the system prompt and the generated sections CodeAlta adds around it.

<figure class="my-4">
  <svg class="img-fluid rounded-4 shadow" viewBox="0 0 960 430" role="img" aria-labelledby="prompt-composition-title prompt-composition-desc" xmlns="http://www.w3.org/2000/svg">
    <title id="prompt-composition-title">Prompt composition flow</title>
    <desc id="prompt-composition-desc">The selected agent prompt references a system prompt and controls generated context such as runtime context, tool guidance, skills, and project context before CodeAlta sends system, developer, and user messages to the model.</desc>
    <defs>
      <marker id="composition-arrow" markerWidth="10" markerHeight="10" refX="9" refY="3" orient="auto" markerUnits="strokeWidth">
        <path d="M0,0 L0,6 L9,3 z" fill="#8ab4f8" />
      </marker>
      <style>
        .ca-bg { fill: #101820; }
        .ca-card { fill: #17212b; stroke: #51606f; stroke-width: 1.5; }
        .ca-agent { fill: #19324a; stroke: #8ab4f8; stroke-width: 2; }
        .ca-output { fill: #1d2d22; stroke: #89d185; stroke-width: 2; }
        .ca-muted { fill: #21303d; stroke: #51606f; stroke-width: 1.5; }
        .ca-arrow { stroke: #8ab4f8; stroke-width: 2.4; fill: none; marker-end: url(#composition-arrow); }
        .ca-text { fill: #eef3f8; font: 600 17px system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
        .ca-small { fill: #c7d1dc; font: 13px system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
        .ca-label { fill: #9fb3c8; font: 12px system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; text-transform: uppercase; letter-spacing: .08em; }
        .ca-pill { fill: #233447; stroke: #5b7188; stroke-width: 1; }
      </style>
    </defs>
    <rect class="ca-bg" width="960" height="430" rx="24" />
    <text class="ca-label" x="40" y="45">Prompt resources</text>
    <rect class="ca-muted" x="40" y="62" width="190" height="92" rx="16" />
    <text class="ca-text" x="62" y="96">Resource roots</text>
    <text class="ca-small" x="62" y="122">built-in → global → project</text>
    <rect class="ca-agent" x="290" y="54" width="270" height="130" rx="18" />
    <text class="ca-text" x="315" y="88">Selected agent prompt</text>
    <text class="ca-small" x="315" y="116">agents/&lt;id&gt;.prompt.md</text>
    <rect class="ca-pill" x="315" y="134" width="100" height="28" rx="14" />
    <text class="ca-small" x="334" y="153">body</text>
    <rect class="ca-pill" x="426" y="134" width="106" height="28" rx="14" />
    <text class="ca-small" x="445" y="153">frontmatter</text>
    <rect class="ca-card" x="640" y="62" width="230" height="92" rx="16" />
    <text class="ca-text" x="665" y="96">System prompt</text>
    <text class="ca-small" x="665" y="122">chosen by system, else default</text>
    <text class="ca-label" x="40" y="222">Generated context toggles</text>
    <rect class="ca-card" x="40" y="240" width="190" height="110" rx="16" />
    <text class="ca-text" x="62" y="271">Runtime context</text>
    <text class="ca-small" x="62" y="297">date, platform, cwd,</text>
    <text class="ca-small" x="62" y="317">project root, session kind</text>
    <rect class="ca-card" x="260" y="240" width="190" height="110" rx="16" />
    <text class="ca-text" x="282" y="271">Tool guidance</text>
    <text class="ca-small" x="282" y="297">host tools and</text>
    <text class="ca-small" x="282" y="317">agent-prompt discovery</text>
    <rect class="ca-card" x="480" y="240" width="190" height="110" rx="16" />
    <text class="ca-text" x="502" y="271">Skills</text>
    <text class="ca-small" x="502" y="297">available and active</text>
    <text class="ca-small" x="502" y="317">skill guidance</text>
    <rect class="ca-card" x="700" y="240" width="190" height="110" rx="16" />
    <text class="ca-text" x="722" y="271">Project context</text>
    <text class="ca-small" x="722" y="297">AGENTS.md and other</text>
    <text class="ca-small" x="722" y="317">repository instructions</text>
    <rect class="ca-output" x="315" y="380" width="330" height="34" rx="17" />
    <text class="ca-small" x="340" y="402">Model request: system + developer + user messages</text>
    <path class="ca-arrow" d="M230 108 H286" />
    <path class="ca-arrow" d="M560 108 H636" />
    <path class="ca-arrow" d="M425 184 V218" />
    <path class="ca-arrow" d="M135 350 C135 385 230 397 310 397" />
    <path class="ca-arrow" d="M355 350 C365 372 395 382 435 382" />
    <path class="ca-arrow" d="M575 350 C565 372 535 382 505 382" />
    <path class="ca-arrow" d="M795 350 C795 385 710 397 650 397" />
    <path class="ca-arrow" d="M755 154 C755 236 650 330 565 378" />
  </svg>
  <figcaption class="small text-secondary mt-2">Composition is file-backed and deterministic. Project-local prompt files can replace or append to global and built-in files; the selected agent prompt then decides which system prompt and generated sections are included for that session.</figcaption>
</figure>

The model receives the system prompt as the system message, the agent prompt body and the generated sections as developer instructions, and your request with its attachments as the user message.

Trusted plugins can add prompt parts, and read or replace the final instructions before they are sent. This is for in-process customization and audit, not a security boundary.

## Built-in modes

{.table}
| Mode | Prompt id | Best for | What it does |
|---|---|---|---|
| **Default** | `default` | Implementation work | Inspects the relevant files, edits, verifies, reviews its diff, reports the outcome, executes approved plan files, and proposes [follow-up tasks](work-items.md) for what it found beside the request. |
| **Plan** | `plan` | Larger or riskier tasks | Researches read-only, writes a Markdown plan under `.alta/plans/` and asks for your review. Once you approve it, you choose where it is carried out. |

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-plan-mode.png" alt="CodeAlta Plan mode session showing a saved plan and review workflow" loading="lazy">
  <figcaption class="small text-secondary mt-2">Plan mode is an agent prompt profile: it researches, writes a plan file and asks for your review. The work starts only when you say where.</figcaption>
</figure>

> [!IMPORTANT]
> Plan mode is enforced by its prompt, not by a sandbox. The Plan prompt tells the agent to change no file but its plan, but the usual permissions still apply.

Typical prompts:

```text
Use Plan mode for this task. Inspect the relevant docs and code,
then write an implementation-ready plan before any source edits.
```

```text
Execute the approved plan at .alta/plans/2026-06-04-workflow-docs.md.
```

A plan starts with what changes and why, shows examples and diagrams where they help, then lists the steps. In CodeAlta Desktop an approved plan is a card of the session: start it in a new worktree, in the same session or in a new one, or keep it for later. See [Work items](work-items.md).

When git is active and `.alta/plans/` is not ignored, plan files are part of the repository: Plan keeps the plan current, and Default checks its steps off as it works.

## Where prompts live

Prompts come from three places, each one taking precedence over the one before:

1. the built-in prompts of CodeAlta;
2. your global prompts, under `~/.alta/prompts/`;
3. the prompts of a project, under `<project>/.alta/prompts/`.

Each place has the same layout:

```text
prompts/
  system/
    default.system-prompt.md
    my-custom-system.system-prompt.md
  agents/
    default.prompt.md
    plan.prompt.md
    reviewer.prompt.md
```

The id of a prompt is its file name: `reviewer.prompt.md` is the agent prompt `reviewer`, and `team-release.system-prompt.md` the system prompt `team-release`.

- **Replace**: a file with the same id replaces the one before it. A global `reviewer` prompt can have a stricter `reviewer.prompt.md` in one repository.
- **Append**: with `mode: append` in its frontmatter, the file's body is added after the prompt it would replace. Several appended files stack in order.

The custom agents of GitHub Copilot (`.github/agents`, `~/.copilot/agents`) are agent prompts too; a CodeAlta prompt with the same id comes first. See [GitHub Copilot layout](copilot-layout.md#custom-agents).

## Create a prompt

Make a prompt of a workflow you repeat often.

1. In CodeAlta Desktop, open **Settings > Agent prompts** with `Ctrl+G Ctrl+H` or `/prompt`. In CodeAlta TUI, the same keys open the prompt manager: choose **Agent Prompts**.
2. Create a **Global** prompt for all projects, or a **Project** prompt for the current one.
3. Pick a short lowercase id, such as `reviewer`, `triage` or `release`.
4. Fill in its name, description, system prompt (optional) and Markdown body.
5. Select it with the **Agent** chip below the prompt (the agent selector in CodeAlta TUI), or cycle prompts with `Ctrl+T` / `/next_prompt`.

A minimal prompt file:

```markdown
---
name: Reviewer
description: Review-only workflow that reports risks before code changes.
---
You are the active CodeAlta review agent for this session.

Review the requested change without editing files. Inspect relevant code,
tests, and docs. Report findings first, ordered by severity, with file and
line references when possible. If no issues are found, say so and mention any
verification gaps.
```

Agents see the descriptions of the prompts, to choose one or delegate to it. Keep a description short and specific, such as "Plan-first release workflow that writes a checklist and asks before publishing", and put the detailed behavior in the body.

## Frontmatter fields

{.table}
| Field | Required? | Use it for |
|---|---:|---|
| `name` | Yes, unless appended | Name in the prompt selector. |
| `description` | No | Short summary shown to agents and users. |
| `system` | No | Id of the system prompt in `prompts/system/<id>.system-prompt.md`; `default` when omitted. |
| `mode` | No | `append` adds the body to the prompt with the same id before it; `replace`, the default, replaces it. |
| `append` | No | `true` is the same as `mode: append`; prefer `mode`. |
| `skills` | No | Include the guidance of available and active skills. |
| `project_context` | No | Include repository instructions such as `AGENTS.md`. |
| `runtime_context` | No | Include the date, platform, working folder, project root, how to link a file, and the session kind. |
| `tool_guidance` | No | Include the guidance of the host tools and the list of agent prompts. |
| `inherit` | No | Agent prompts this one is made of, such as `[default, maui]`. See [Inherit other prompts](#inherit-other-prompts). |
| `disabled_tools` | No | Built-in tools a session of this prompt does without, such as `[shell_command, webget]`. In CodeAlta Desktop, the prompt's **Tools turned off**. See [Tools turned off](workspace/settings.md#tools-turned-off). |

The generated sections (`skills`, `project_context`, `runtime_context`, `tool_guidance`) are all included by default. Set one to `false` only when a workflow needs less; without tool guidance, an agent discovers fewer of CodeAlta's features.

An appended prompt keeps the `name`, `description`, `system` and section settings of the prompt it adds to, unless it sets them. A project can add rules to the built-in `default` prompt without copying it:

```markdown
---
mode: append
---
For this repository, run `dotnet test -c Release` before reporting success
when C# runtime code changes.
```

A narrow review prompt can keep the usual system prompt and leave out the skills and tool guidance:

```markdown
---
name: Minimal Reviewer
description: Review-only workflow with a smaller generated context.
skills: false
tool_guidance: false
---
Review the requested change without editing files. Report findings first,
ordered by severity, with file and line references when possible.
```

## Inherit other prompts

With `inherit`, an agent prompt is made of other agent prompts. Keep common rules in a prompt of their own and compose them, instead of copying text. With a `maui.prompt.md` that holds the rules of a .NET MAUI project:

```markdown
---
name: MAUI Default
description: Default workflow with the MAUI rules of this team.
inherit:
  - default
  - maui
---
Prefer the MVVM Community Toolkit for new view models.
```

and a `maui-plan.prompt.md` that inherits `[plan, maui]`.

- The text of each inherited prompt comes first, in the order of the list, then the prompt's own text.
- An inherited prompt is taken as it is in effect: with the files that replace or append to it, and with the prompts it inherits itself.
- A prompt reached twice comes once, at its first place.
- The last `system` and section fields win, and the prompt's own fields come last. Tools turned off by an inherited prompt are turned off too.
- An id that names no agent prompt, or a loop back to the prompt itself, is left out.

`inherit` takes a list in brackets (`inherit: [default, maui]`), `- id` lines as above, or ids separated by commas. Only agent prompts inherit.

In CodeAlta Desktop, the editor of an agent prompt shows **Inherits from**: the inherited prompts in order, ending with the prompt itself.

- **Inherit a prompt** adds one; the arrow of a prompt moves it one place earlier; its name opens it.
- The line below gives the size of the composed prompt, and **Preview the composed prompt** shows the text the agent receives, unsaved changes included.
- The system prompt and the tools turned off that come from an inherited prompt are shown in their fields. In the list, a prompt that inherits others has a tag with their number.

## Write a prompt body

A good body shapes behavior and stays short:

- the role and scope of the workflow;
- when the agent inspects, implements, verifies or stops;
- when it asks for your approval;
- whether it may delegate to child sessions;
- what notes it keeps on a long task;
- the smallest verification that counts;
- what is out of scope, and the safety limits.

A body can name what agents can do through the `alta` tool, such as delegation, notes, reminders or questions. You still prompt for the outcome; the agent runs the commands. See [Advanced Agent Workflows](sessions/delegation.md).

## Choose and edit prompts

The **Agent** chip below the prompt (the agent selector in CodeAlta TUI) chooses the prompt of the session: built-in prompts first, then global and project prompts.

- It lists the prompts of the [space](spaces.md#what-a-space-has) of the session. A new session starts with the agent prompt of its space (**New sessions** on the page of the space), else with `default`.
- On the **Agent prompts** page of Settings, the chip of a prompt shows and changes its spaces.
- The prompt manager lists built-in, global and project prompts, shows which ones are replaced, and creates, edits and deletes global and project files. Built-in files are read-only: create a file with the same id to replace or append to one.

Agents can manage prompts for you. Ask, for example:

```text
List the available agent prompts for this project and recommend which
one fits a read-only security review.
```

```text
Create a project agent prompt named release-checklist that plans release
steps, keeps notes visible, and asks before publishing.
```

```text
Switch this session to the reviewer prompt for the next turn.
```

## Project prompts

A project prompt specializes a workflow for one repository:

- `plan.prompt.md` with the sections its plans need;
- `reviewer.prompt.md` with its test commands and review gates;
- `release.prompt.md` with its packaging and publishing checklist;
- `triage.prompt.md` that has child sessions read logs, issues and recent sessions.

A `default.prompt.md` that replaces the built-in one, without `mode: append`, should keep its **Review by the user** section, or say the same in its own words. That section tells the agent to wait for your approval before it commits when [Review before commit](reviews.md#review-before-commit) is on, and how to answer your review. Without it, the agent learns only when its commit is refused. An appended prompt keeps the section.

A prompt that has its agent ask for your review of a file, such as a plan, must use `alta review request file <path> --stdin`, with the questions on stdin; for the changes, `alta review request changes`. `alta ask` given a file refuses with `ask.fileMoved` and names that command. See [Reviews](reviews.md#agents-and-alta-review).

> [!WARNING]
> Project prompts are part of the repository. Review a prompt change like a code change: it changes how agents decide, ask, delegate and edit files.

## System prompts

A system prompt holds stable host rules; keep it short. It takes `mode: append` too, so a global or project file can add to `default.system-prompt.md` without copying it:

```markdown
---
mode: append
---
Always mention this team's release freeze window before proposing publish steps.
```

A new system prompt is used only by the agent prompts that name it:

1. Create the system prompt, such as `prompts/system/team-release.system-prompt.md`.
2. Name its id in the **System Prompt** field of an agent prompt (`system: team-release`), not its path.
3. Select that agent prompt for the session.

```markdown
---
name: Release Coordinator
description: Release workflow using the team release system prompt.
system: team-release
---
Coordinate the release checklist, verify packaging steps, and ask before
publishing.
```

For the built-in **Default** or **Plan** modes to use it, create a global or project `default.prompt.md` or `plan.prompt.md`, appended or replacing, with the `system` field. That changes every session of that mode in its scope.
