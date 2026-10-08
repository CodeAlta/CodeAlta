---
title: Agent Prompts
---

# Agent Prompts

Agent prompts are selectable workflow profiles for CodeAlta sessions. They tell the agent how to work: implement directly, plan first, review only, coordinate child sessions, keep visible notes, or follow a project-specific release workflow.

CodeAlta separates prompt-related instructions into a few layers:

{.table}
| Layer | Files | Use it for |
|---|---|---|
| **Agent prompt** | `prompts/agents/<id>.prompt.md` | Session workflow, mode behavior, delegation style, review rules, and when to ask the user. |
| **System prompt** | `prompts/system/<id>.system-prompt.md` | Stable host/agent invariants shared by one or more agent prompts. |

> [!TIP]
> Most customization belongs in an agent prompt. Change a system prompt only when you really need to change stable host-level instructions across workflows.

The active agent prompt is included when a session starts or resumes. Its optional `system` property chooses which system prompt id to use; when omitted, CodeAlta uses `default`. Optional composition fields in the agent prompt frontmatter can also turn generated context sections on or off for that workflow.

## Prompt composition at a glance

The selected agent prompt is the entry point for composition: its body becomes developer instructions, and its frontmatter chooses the system prompt plus the generated context sections CodeAlta adds around it.

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

In the model request, the system prompt is sent as the system message. The agent prompt body and enabled generated sections are combined as developer instructions, while the user's actual request and attachments remain the user message.

Trusted local plugins can also append prompt parts and, through the dedicated instruction-processor extension point, inspect or replace the final system/developer instructions after composition and before provider submission, hashing, and prompt journal metadata. This hook is for trusted in-process customization and audit metadata; it is not a security boundary.

## Built-in modes

CodeAlta ships two built-in agent prompts:

{.table}
| Mode | Prompt id | Best for | What it does |
|---|---|---|---|
| **Default** | `default` | Normal implementation/build work | Inspects relevant files, edits when implementation is feasible, verifies, self-reviews diffs, reports concrete outcomes, executes approved plan files, and proposes [follow-up tasks](work-items.md) for what it found beside the request. |
| **Plan** | `plan` | Larger or riskier tasks where you want a plan before edits | Researches read-only, writes a readable Markdown plan under `.alta/plans/` and asks for review. Once you approve it, you choose where it is carried out. |

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-plan-mode.png" alt="CodeAlta Plan mode session showing a saved plan and review workflow" loading="lazy">
  <figcaption class="small text-secondary mt-2">Plan mode is an agent prompt profile: it researches, writes a plan file and asks for your review. The work starts only when you say where.</figcaption>
</figure>

> [!IMPORTANT]
> Plan mode is prompt-enforced behavior, not a hard filesystem sandbox. The built-in Plan prompt tells the agent not to mutate project files except for the plan under `.alta/plans/`, but normal host permissions still exist. Use normal review practices for sensitive work.

Typical prompts:

```text
Use Plan mode for this task. Inspect the relevant docs and code,
then write an implementation-ready plan before any source edits.
```

```text
Execute the approved plan at .alta/plans/2026-06-04-workflow-docs.md.
```

A plan starts with what changes and why, shows examples and diagrams where they help, then lists the steps. In CodeAlta Desktop an approved plan is shown as a card of the session, where you start it in a new worktree, in the same session or in a new session, or keep it for later. See [Work items](work-items.md).

If git is active and `.alta/plans/` is not ignored, the built-in prompts treat plan files as repository artifacts: Plan keeps the saved plan current, and Default keeps it synchronized with implementation progress.

## Source locations and precedence

Prompt resources are layered in this order:

1. Built-in resources shipped with CodeAlta.
2. User-global resources under `~/.alta/prompts/`.
3. Project-local resources under `<project>/.alta/prompts/`.

The custom agents of GitHub Copilot (`.github/agents`, `~/.copilot/agents`) are agent prompts too. A resource of CodeAlta with the same id comes first. See [GitHub Copilot layout](copilot-layout.md#custom-agents).

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
```

If multiple roots contain the same prompt or system id, the later source replaces the earlier one by default: project replaces global, and global replaces built-in. This lets you keep a global `reviewer` prompt while giving one repository a stricter project-local `reviewer.prompt.md`.

When you only want to add local instructions to an existing prompt, set `mode: append` (or `append: true`) in frontmatter. CodeAlta then appends that file's body after the nearest lower-precedence replacement chain instead of replacing it. Multiple append files stack in precedence order.

> [!NOTE]
> Prompt ids come from file names. For example, `reviewer.prompt.md` creates, replaces, or appends to the agent prompt id `reviewer`, and `team-release.system-prompt.md` creates, replaces, or appends to the system prompt id `team-release`.

## Create a custom agent prompt

Use a custom prompt when you repeat the same workflow often enough that it deserves a named mode.

1. Open the prompt manager with `Ctrl+G Ctrl+H` or `/prompt`.
2. Choose **Agent Prompts**.
3. Create a **Global** prompt for all projects or a **Project** prompt for the current project.
4. Pick a short lowercase id such as `reviewer`, `triage`, or `release`.
5. Fill in a concise name, description, optional system prompt id, optional composition overrides, and Markdown body.
6. Select the agent prompt from the agent selector below the prompt, or cycle prompts with `Ctrl+T` / `/next_prompt`.

Creating a system prompt does not activate it by itself. In normal use, a system prompt becomes active only when the selected agent prompt references its id in the **System Prompt** field (the `system` frontmatter property).

A minimal prompt file looks like this:

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

`description` is optional but should be decision-useful: CodeAlta surfaces effective agent prompt descriptions in generated model context so agents can discover available workflows and switch or delegate appropriately.

> [!TIP]
> Keep descriptions short and specific, for example “Plan-first release workflow that writes a checklist and asks before publishing.” Avoid long policy text in descriptions; put detailed behavior in the body.

## Agent prompt frontmatter and composition

Agent prompt frontmatter is the user-facing entry point for prompt composition. The file name selects the agent prompt id, the prompt's `system` field selects the system prompt id, `mode` controls replacement versus append behavior, and optional boolean fields override which generated context sections CodeAlta includes. In other words, you normally activate a custom system prompt by editing or creating an agent prompt that points at it, then selecting that agent prompt for the session.

{.table}
| Field | Required? | Use it for |
|---|---:|---|
| `name` | Yes for replace; inherited for append | Display name in the prompt selector. |
| `description` | No | Short decision-useful summary shown to agents and users. |
| `system` | No | System prompt id from `prompts/system/<id>.system-prompt.md`; defaults to `default` when omitted. |
| `mode` | No | `append` to add this prompt body to the lower-precedence prompt with the same id; omit or use `replace` for the default replacement behavior. |
| `append` | No | Boolean alias for `mode: append`; prefer `mode` for new prompt files. |
| `skills` | No | Include available/active skill guidance. |
| `project_context` | No | Include repository instruction files such as `AGENTS.md`. |
| `runtime_context` | No | Include current date, platform, working directory, project root, how to write a link to a file, and session kind. |
| `tool_guidance` | No | Include generated host-tool guidance and available agent-prompt discovery. |

Do not restate defaults in every prompt. When generated-section boolean fields are omitted, CodeAlta uses its normal defaults, which currently include all generated sections when content is available. Add one of those booleans only when a workflow intentionally needs a different composition.

For example, a project can add repository-specific rules to the built-in `default` agent prompt without copying the whole file:

```markdown
---
mode: append
---
For this repository, run `dotnet test -c Release` before reporting success
when C# runtime code changes.
```

Appended agent prompts inherit lower-precedence metadata such as `name`, `description`, `system`, and generated-section settings unless the appended file sets those fields. For example, an appended prompt can still set `tool_guidance: false` if the local addition intentionally changes composition.

For example, if you create `prompts/system/team-release.system-prompt.md`, reference it from an agent prompt with `system: team-release`:

```markdown
---
name: Release Coordinator
description: Release workflow using the team release system prompt.
system: team-release
---
Coordinate the release checklist, verify packaging steps, and ask before
publishing.
```

Selecting **Release Coordinator** as the active agent prompt is what activates `team-release` for that session. If the `system` field is omitted, CodeAlta uses `default`.

For example, a narrow review prompt can keep the normal system prompt while disabling skill and tool-discovery guidance:

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

## What to put in a prompt body

Good agent prompt bodies are concrete enough to shape behavior but small enough to stay understandable:

- state the role and scope of the workflow;
- define when the agent should inspect, implement, verify, or stop;
- say when it should ask for structured approval;
- explain whether it may delegate to child sessions;
- describe progress-note expectations when the task is long-running;
- name the smallest meaningful verification for the workflow;
- call out non-goals and safety boundaries.

For advanced workflows, the body can mention CodeAlta live-tool capabilities the model may use, such as session delegation, sticky notes, reminders, or structured asks. Users still prompt for the outcome; the model invokes live-tool commands internally when the selected provider/session supports them. See [Advanced Agent Workflows](advanced-agent-workflows.md) for recipes and command-group coverage.

## Selecting, switching, and editing prompts

Use the **Agent:** selector below the prompt editor to choose the prompt for the current draft/session. Built-in prompts appear first, followed by global prompts and project prompts.

The prompt manager lists built-in, global, and project prompts, shows shadowed replacements, and lets you create, edit, save, or delete global/project prompt files. Built-in prompt and system prompt files are visible for inspection but read-only; create a global or project file with the same id to replace or append to one.

Agents can also use the `prompt` and `session` live-tool command groups internally when you ask for prompt automation. For example, you can ask:

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

Those are user prompts, not terminal commands. CodeAlta-managed agents translate them into the appropriate prompt/session operations when available.

## Default and project-specific overrides or extensions

A project prompt can specialize a global workflow without changing other repositories. Common examples:

- `plan.prompt.md` that adds repository-specific plan-file sections;
- `reviewer.prompt.md` that includes local test commands and review gates;
- `release.prompt.md` that knows the repository's packaging and publishing checklist;
- `triage.prompt.md` that asks child sessions to inspect logs, issues, and recent session history.

> [!WARNING]
> Project-local prompts are part of the repository workspace. Review prompt changes like code changes: a prompt can change how agents decide, ask, delegate, and mutate files.

## System prompts and composition

System prompt files carry host-level behavior and should be short, stable, and explicit. Agent prompts are better for workflow-specific session behavior.

System prompts support the same `mode: append` / `append: true` frontmatter as agent prompts. This is useful for global or project-local additions to `default.system-prompt.md` without copying CodeAlta's full built-in system prompt:

```markdown
---
mode: append
---
Always mention this team's release freeze window before proposing publish steps.
```

Use the agent prompt `system` frontmatter field when a workflow needs a custom system prompt. The value is the system prompt id, not the full path: for `prompts/system/team-release.system-prompt.md`, write `system: team-release` in the agent prompt frontmatter, then select that agent prompt for the session.

The activation flow is:

1. Create, replace, or append a system prompt, for example `prompts/system/team-release.system-prompt.md`.
2. Create or edit an agent prompt and set its **System Prompt** field (the `system` frontmatter property) to `team-release`.
3. Select that agent prompt from the **Agent:** selector before sending the prompt.

Keep composition overrides close to the agent prompt that needs them. Disabling generated context such as tool guidance can make advanced prompt workflows less discoverable to the agent, so prefer the default generated sections unless a specific workflow has a clear reason to remove them.

If you want a custom system prompt to affect the built-in **Default** or **Plan** modes, create a global or project agent prompt with the same agent prompt id (`default.prompt.md` or `plan.prompt.md`) and add the `system` field there. Review these replacements or append extensions carefully because they change every session that uses that mode in the matching scope.
