# Alta Live Tool Specification

Status: **Draft**  
Last updated: **2026-05-09**  
Audience: implementers of the CodeAlta executable, headless/live command gateway, orchestration services, plugin runtime, agent prompt/tool integration, and global `~/.alta/` agent instructions.

Related specs:

- `doc/specs/filesystem_metadata_catalog_spec.md`
- `doc/specs/agent_api_specs.md`
- `doc/specs/agent_instruction_templates_spec.md`
- `doc/specs/skills_specs.md`
- `doc/specs/plugins_runtime_specs.md`
- `doc/development-guide.md`

## 1. Goal

Define a new live tool named `alta` that lets a running agent inspect and control CodeAlta through a command-line-shaped model implemented in-process with `XenoAtom.CommandLine`:

```text
alta --help
alta project --help
alta project list
alta session --help
alta session list --project CodeAlta
alta session model <thread-id>
alta model list --provider codex
alta session create --project CodeAlta --same-model-as <thread-id> --reasoning low
alta session tail <thread-id> --last 10
alta session send <thread-id> --stdin
alta skill activate <skill-name> --session <thread-id>
```

The tool should be:

- **self-describing**: every command level exposes `--help` through `XenoAtom.CommandLine`, with command descriptions, examples, and subcommand lists generated from the command tree;
- **capture-friendly**: non-help command output is deterministic JSONL by default, has stable exit codes, and avoids terminal-only UI for headless commands;
- **live**: when a CodeAlta host is running, commands can query and control that host's in-memory runtime, not only files on disk;
- **agent-safe**: inter-agent messages are clearly attributed as coming from another CodeAlta agent/session and cannot impersonate the user or host;
- **progressively extensible**: core commands are small and stable, while plugins can add command groups and live tool capabilities without changing the gateway tool schema.

## 2. Terminology

- **`alta` CLI**: the command-line-shaped surface implemented with `XenoAtom.CommandLine`. In v1 it is hosted in-process by CodeAlta command surfaces and the live tool; it is not a separate client process for controlling another CodeAlta instance.
- **`alta` live tool**: an agent tool exposed inside CodeAlta-managed sessions. It accepts command-line-like arguments and dispatches them through the in-process command registry.
- **Live host**: the currently running CodeAlta process that owns active UI state, orchestration runtime, plugin runtime, active sessions, and the in-process `alta` command registry.
- **Controller session**: the session invoking `alta`.
- **Target session**: the session being inspected, sent to, steered, aborted, compacted, or summarized.
- **Thread id**: CodeAlta durable work-thread id. It may be derived from backend/session ids today.
- **Backend session id**: provider-owned session id from Codex, Copilot, or a CodeAlta local runtime provider.
- **Model selection**: the provider/model/reasoning tuple used to create or resume a session. It is represented by `providerKey`, `modelId`, and optional `reasoningEffort`.
- **Model ref**: a compact string form of a model selection, `providerKey:modelId[@reasoning]`, for command-line use and agent-to-agent handoff.
- **Session lineage**: durable parent/child relationship recorded when one session creates another session in the same project scope.
- **Provenance**: durable attribution for who created a session, submitted/queued a prompt, steered a run, or sent an inter-agent message.
- **Plugin caller**: a trusted CodeAlta plugin invoking the in-process `alta` dispatcher to query runtime state or perform actions such as creating sessions.
- **Coordinator instructions file**: the managed `~/.alta/AGENTS.md` file that defines the global coordinator role and compact `alta` usage guidance.

## 3. Current implementation facts this design should reuse

The first implementation should stay close to APIs that already exist or are already planned in the active architecture:

- `ProjectCatalog` can load, resolve, and upsert project descriptors.
- `WorkThreadRuntimeService` can list recoverable threads, create project/global threads, send prompts, steer active runs, abort runs, activate skills, stream sanitized runtime events, and persist local thread state.
- `IWorkThreadOrchestrator` already defines headless command/query/event contracts for draft creation, launch, submit, steer, abort, compact, skill activation, queueing, snapshots, and event streams.
- `AgentHub`/`IAgentBackend` can list sessions, list provider models, resume sessions, send/steer/abort, and retrieve backend-normalized history where supported.
- `SkillCatalog` supports progressive discovery/activation for CodeAlta-managed skills.
- `PluginBase.GetCommandLineContributions()` and plugin agent-tool contributions already create a plugin seam that can be extended for `alta` command/live tool contributions.
- The existing executable already builds a `XenoAtom.CommandLine.CommandApp` named `alta`; this work should extend the same executable rather than introduce a second user-facing command name.

## 4. Non-goals for v1

The first implementation should not attempt to provide:

- cross-machine, remote-network, or cross-process control;
- a plugin sandbox or new trust model beyond the existing trusted plugin model;
- direct access to hidden/private chain-of-thought. Commands may expose assistant messages, tool events, and backend-provided reasoning summaries that are already visible/sanitized, but must not invent or expose private reasoning;
- arbitrary mutation outside CodeAlta's own runtime contracts;
- a replacement for backend-native CLIs;
- a full multi-user permissions system.

## 5. Product shape

### 5.1 One gateway command and one gateway live tool

The live agent tool should have one stable name: `alta`.

The tool payload should be intentionally small so plugins can add subcommands without changing the model-visible tool schema:

```json
{
  "args": ["session", "list"],
  "stdin": null,
  "cwd": null,
  "timeoutMs": 30000
}
```

The implemented agent-tool schema exposes `args`, nullable `stdin`, nullable `cwd`, `maxOutputRecords`, `maxOutputBytes`, and `timeoutMs` (milliseconds). The handler passes `cwd` through to the shared dispatcher so project-relative commands such as `project resolve` use the caller-supplied working directory while still falling back to the session working directory when omitted.

Model-visible tool result shape:

```jsonl
{"type":"alta.result","version":1,"exitCode":0,"correlationId":"01HX...","truncated":false}
{"type":"alta.session.summary","version":1,"correlationId":"01HX...","count":1,"truncated":false}
```

The live tool should not return a JSON object with `stdout`/`stderr` string properties containing escaped JSONL. That nested form is unnecessarily verbose for model context. Instead, the model-visible response for non-help commands is a flat finite JSONL transcript: one compact `alta.result` header followed by the command's normal JSONL records and any diagnostic records. Empty streams have no section marker.

The CLI-shaped surface and live tool must share the same parser and command handlers:

- an agent calls the `alta` live tool with `args = ["session", "list"]`;
- an in-process CodeAlta command surface can invoke the same command registry for diagnostics, tests, or future UI affordances;
- a user or agent progressively discovers commands with `alta --help`, `alta session --help`, and `alta session tail --help`;
- execution and help requests resolve through the same `AltaCommandRegistry` entry and `AltaCommandContext`.

### 5.2 In-process only

The v1 live `alta` gateway is intentionally in-process only. It runs inside the same CodeAlta process that owns the sessions it inspects or controls.

Implications:

- do not implement a client process, local IPC, named pipes, Unix sockets, endpoint discovery files, or host registry;
- do not make an external `alta ...` shell process control a running CodeAlta instance;
- commands that need live runtime state receive it from the current dependency-injection/service context;
- read-only catalog commands may still be reusable in tests or startup diagnostics, but the live session-control feature assumes an in-process host context;
- plugin command contributions are merged into the in-process command catalog/tree before sessions receive the live tool;
- trusted plugins may also invoke the same in-process dispatcher programmatically instead of shelling out or duplicating orchestration APIs.

### 5.3 Non-blocking command model

No `alta` command should block waiting for future session activity. A command may perform bounded asynchronous work needed to serve the request, such as connecting to the live host, reading current state, submitting a prompt, or writing a queued item, but it must then return a result.

Implications:

- do not add `watch`, `follow`, `wait`, or interactive prompt modes in v1;
- commands that inspect messages/events return a finite snapshot, selected by `--last`, `--since`, `--limit`, or equivalent filters;
- commands that start work, such as `session send`, return submission metadata (`threadId`, `runId`, queue status) rather than waiting for the target agent to finish;
- v1 commands must not open interactive confirmation prompts; disruptive commands either run under existing trusted policy or fail with a policy-denied JSONL error;
- live-tool calls should have bounded execution time and output size so agents can safely capture the result.

### 5.4 JSONL output contract

Non-help `alta` commands should always produce newline-delimited JSON (`application/x-ndjson`). Do not add `--json`, `--jsonl`, table, color, or quiet output variants in v1. A command that returns one result writes one JSON object followed by a newline; a command that returns multiple results writes one object per line.

For process-style command surfaces, normal result records are written to stdout and diagnostics may be written to stderr. For the agent live tool, optimize for token consumption: do **not** embed those streams as escaped strings inside a larger JSON object. The live tool's model-visible content is a flat JSONL transcript:

1. an `alta.result` header record with `exitCode`, `correlationId`, truncation metadata, and optional counts;
2. zero or more normal command result records;
3. zero or more `alta.warning` / `alta.error` diagnostic records.

Because v1 commands are finite and non-streaming, the host can emit the `alta.result` header after the command completes but place it first in the returned transcript. This avoids a trailing status record that agents might miss and avoids repeatedly annotating every data record with envelope fields.

`--help` is the only v1 exception: it uses `XenoAtom.CommandLine`'s normal help renderer because it is for progressive human/model discovery rather than data exchange.

JSONL records must:

- use camelCase property names;
- include `type` and `version` fields;
- include `correlationId` when available;
- be complete standalone objects; consumers should not need surrounding arrays;
- include `truncated = true` and enough cursor/count metadata when output is limited.

Errors and warnings should also be JSONL records with types such as `alta.error` or `alta.warning`; the process/tool exit code remains the primary success/failure signal. In the flat live-tool transcript, diagnostics appear as records rather than inside a `stderr` string or a separate section.

Example successful live-tool transcript:

```jsonl
{"type":"alta.result","version":1,"exitCode":0,"correlationId":"01HX...","truncated":false,"recordCount":2,"diagnosticCount":0}
{"type":"alta.session.item","version":1,"correlationId":"01HX...","threadId":"codex:abc","projectName":"CodeAlta","state":"running"}
{"type":"alta.session.summary","version":1,"correlationId":"01HX...","count":1,"truncated":false}
```

Invalid command usage is still a non-help command result and should therefore use the same JSONL error contract for the live tool and other machine-captured command surfaces. The implementation should rely on `XenoAtom.CommandLine` for parsing, strict unknown-option detection, validation, command resolution, and usage/help hints, then normalize those diagnostics into finite `alta.error` records with exit code 2. The JSONL error should preserve the useful library-generated message/hint rather than replacing it with a vague wrapper.

Recommended parse/usage error shape:

```jsonl
{"type":"alta.result","version":1,"correlationId":"01HX...","exitCode":2,"truncated":false,"recordCount":0,"diagnosticCount":1}
{"type":"alta.error","version":1,"correlationId":"01HX...","code":"usage.invalidOption","exitCode":2,"commandPath":"alta session list","message":"Unknown option: --stat","usageHint":"Use `alta session list --help` for usage.","suggestions":["--state"]}
```

Implementation guidance:

- keep `CommandConfig.StrictOptionParsing = true` so misspelled `-`/`--` options fail instead of becoming positional arguments;
- prefer built-in `XenoAtom.CommandLine` validators and constraints, such as `Validate.OneOf`, `Validate.Range`, `Validate.NonEmpty`, mutually exclusive constraints, and required argument cardinality, so invalid usage is caught before command handlers mutate state;
- when custom validation is needed while parsing options, throw the current library exception type `CommandOptionException` (older notes may call this `OptionException`) or another appropriate `CommandException` so the command path, unknown-token suggestions, inactive-option notes, and `Use ... --help` guidance remain consistent;
- for errors detected after parsing but before mutation, return an `alta.error` record with the stable exit code that best matches the condition; use exit code 2 only for command-line usage/parse/argument validation errors;
- configure `XenoAtom.CommandLine` output through `ICommandOutput` or an equivalent adapter so `--help` continues to use normal help output while errors, unknown-token diagnostics, version output, and command results remain JSONL for non-help invocations;
- do not leak the library default parse-error exit code if it differs from this spec; the `alta` dispatcher should map parse/usage failures to exit code 2 consistently.

Example `alta session list` output:

```jsonl
{"type":"alta.session.item","version":1,"correlationId":"01HX...","threadId":"codex:abc","backendId":"codex","projectId":"01HX...","projectName":"CodeAlta","title":"Alta live tool spec","state":"running","parentThreadId":"codex:parent","createdBy":{"kind":"agent","sourceThreadId":"codex:parent","sourceAgentId":"agent:parent"},"modelSelection":{"providerKey":"codex","modelId":"gpt-5-codex","reasoningEffort":"high","modelRef":"codex:gpt-5-codex@high"},"lastActiveAt":"2026-05-09T10:15:00Z","isRunning":true,"queuedPromptCount":0,"messageCount":42}
{"type":"alta.session.summary","version":1,"correlationId":"01HX...","count":1,"truncated":false}
```

For commands that naturally return a compact scalar, such as `session model`, still emit a JSONL object:

```jsonl
{"type":"alta.model.selection","version":1,"threadId":"codex:abc","providerKey":"codex","modelId":"gpt-5-codex","reasoningEffort":"high","modelRef":"codex:gpt-5-codex@high"}
```

### 5.5 Model selection references

Commands that create or configure sessions should accept both explicit fields and a compact model ref.

Compact syntax:

```text
<provider-key>:<model-id>[@<reasoning-effort>]
```

Examples:

```text
codex:gpt-5-codex@high
openai:gpt-4.1@low
anthropic:claude-sonnet-4-5
```

The parser splits the provider key at the first `:` and the reasoning effort at the last `@`. Reasoning values are the CodeAlta `AgentReasoningEffort` names in lowercase: `minimal`, `low`, `medium`, `high`, `xhigh`, and `none`. If a model id itself contains `@`, callers should use long options instead of the compact form.

Every JSONL session/model record that includes a model selection should expose both forms:

```jsonl
{"type":"alta.model.selection","version":1,"providerKey":"codex","modelId":"gpt-5-codex","reasoningEffort":"high","modelRef":"codex:gpt-5-codex@high"}
```

Resolution precedence for session creation and model-sensitive commands:

1. explicit `--model-ref`;
2. explicit `--same-model-as <thread-id>` plus any `--provider`, `--model`, or `--reasoning` overrides;
3. explicit `--provider`, `--model`, and/or `--reasoning`, merged with the caller-session default where possible;
4. the controller session's provider/model/reasoning when the live tool caller has `AltaCallerIdentity.SourceThreadId`;
5. the host's current/default provider preference for external CLI calls without a source session;
6. the provider/backend default.

The implemented resolver treats `--model-ref` as an atomic highest-precedence selection: when it is present, long-form provider/model/reasoning options do not override its parsed values. Use long-form options with `--same-model-as` or without `--model-ref` when field-level overrides are needed.

This means an agent creating another session normally inherits its own provider/model/reasoning without specifying any flags. To use the same model as another session but lower reasoning, it can use `--same-model-as <thread-id> --reasoning low`, for example:

```text
alta session create --project CodeAlta --same-model-as <thread-id> --reasoning low
```

### 5.6 Exit codes

Use stable exit codes for automation:

| Code | Meaning |
|---:|---|
| 0 | Success. |
| 1 | Command failed because of runtime, validation, or backend error. |
| 2 | Command-line parse or usage error. |
| 3 | Target project/session/skill/command not found. |
| 4 | Permission or policy denied. |
| 5 | Required in-process service/context unavailable. |
| 6 | Timeout or cancellation. |
| 7 | Unsupported capability in the current backend/runtime. |

## 6. Core command groups

The list below is intentionally limited to use cases feasible with the current catalog/orchestration/backend abstractions.

Canonical command groups should use singular resource nouns (`project`, `session`, `skill`, `model`, `provider`, `plugin`, `tool`). This is the most predictable shape for subcommands that operate on either one item or a collection (`session list`, `session show`, `session create`) and matches common command palettes/CLIs where the group names a resource domain. Plural aliases should be avoided by default and added only for compatibility with existing instructions or user-visible tools, such as `skills activate` / `skills_activate` during migration.

### 6.1 Help-driven self-description

```text
alta --help
alta <group> --help
alta <group> <command> --help
alta version
alta tool capability list
```

Responsibilities:

- use `XenoAtom.CommandLine.HelpOption` at the root and at every command/group level that has subcommands or options;
- rely on `XenoAtom.CommandLine` help generation to list visible subcommands, options, arguments, usage, and command descriptions;
- make each command description concise and agent-readable because it becomes the progressive discovery text shown by `--help`;
- include examples in command help text where the syntax is not obvious;
- expose runtime/live-tool capabilities with `tool capability list` under the canonical `tool` group, not as command-tree discovery.

No separate `alta describe`, `alta commands`, or `alta schema` commands are required for v1. They would duplicate `XenoAtom.CommandLine`'s built-in help behavior and should be added only later if a concrete machine-readable discovery gap remains after the CLI/live tool exists.

The live tool prompt guidance should tell agents to progressively inspect `alta --help`, then narrower command levels such as `alta session --help` or `alta session tail --help`, instead of embedding every subcommand in the initial prompt.

### 6.2 Project commands

```text
alta project list [--include-archived]
alta project show <project-id|slug|path>
alta project resolve [--path <path>]
alta project upsert <path>
```

Implementation source:

- `ProjectCatalog.LoadAsync`
- `ProjectCatalog.GetByIdAsync`
- `ProjectCatalog.GetBySlugAsync`
- `ProjectCatalog.GetByPathAsync`
- `ProjectCatalog.UpsertFromPathAsync`
- `ProjectResolver` where current-directory/project-scope behavior is needed

Returned fields should include:

- `projectId`, `slug`, `name`, `displayName`, `projectPath`;
- `defaultBranch`, `archived`, `sourcePath`;
- optional `lastActiveAt`/thread counts when cheap to compute.

### 6.3 Session/thread discovery commands

```text
alta session list [--project <id|slug|path>] [--state running|idle|inactive|archived|all] [--backend <id>] [--limit <n>]
alta session show <thread-id>
alta session status <thread-id>
alta session children <thread-id> [--recursive]
alta session model <thread-id>
alta session tail <thread-id> [--last <n>] [--include user,assistant,reasoning,tool,host,event]
alta session events <thread-id> [--since <sequence>] [--limit <n>] [--include ...]
```

State semantics:

- `running`: target thread has an active in-flight run in the live host.
- `idle`: target thread is known to the live host but has no active run.
- `inactive`: recoverable/durable/backend session exists, but it is not active in the live host.
- `archived`: local state marks it archived/hidden.
- `all`: include all of the above where available.

Implementation source:

- live host runtime registry for active entries and queue counts;
- `WorkThreadRuntimeService.ListRecoverableThreadsAsync` for recoverable user-facing sessions;
- `WorkThreadCatalog.LoadInternalAsync` for legacy host-owned internal thread metadata;
- `IWorkThreadOrchestrator.GetThreadSnapshotAsync` for a live snapshot when a thread id is known;
- thread view state, execution options, and backend/session metadata for `session model` and model selection fields;
- CodeAlta event projections or normalized stored events for `tail` and `events` when available;
- backend `GetHistoryAsync` only as a fallback when CodeAlta projections are unavailable, such as during early bootstrap or migration.

`session list`, `show`, and `status` should include `providerKey`, `modelId`, `reasoningEffort`, `modelRef`, `parentThreadId`, and `createdBy` when known. `session show` should also include direct child counts/ids when cheap to compute. `session children` should emit child `alta.session.item` records. `session model` should emit one `alta.model.selection` JSONL record so another command can reuse its `modelRef` field easily.

`tail` and `events` return finite snapshots only; they must not wait for future messages. Reads should prefer CodeAlta-owned event projections so they do not race with backend files that another active session may be writing. Backend-history fallback must be best-effort and tolerant of locked/partially written files; it should return available data plus an `alta.warning` record rather than fail the whole command when a transient read conflict occurs. The current implementation reads CodeAlta local-runtime normalized event journals first via `WorkThreadRuntimeService.TryReadStoredHistoryAsync`; transient/corrupt local journal reads emit `session.historyStoreUnavailable`, then the command falls back to the active runtime session history when available. They should expose only sanitized visible content:

- user/delegated-agent messages;
- assistant messages;
- tool calls/results when visible to the session;
- host lifecycle/runtime events;
- backend reasoning summaries or visible reasoning messages, never hidden chain-of-thought.

### 6.4 Session creation and control commands

```text
alta session create --project <id|slug|path> [--title <title>] [--model-ref <ref>] [--same-model-as <thread-id>] [--provider <id>] [--model <id>] [--reasoning <effort>] [--parent <thread-id>|--no-parent]
alta session create --global [--title <title>] [--model-ref <ref>] [--same-model-as <thread-id>] [--provider <id>] [--model <id>] [--reasoning <effort>] [--parent <thread-id>|--no-parent]
alta session send <thread-id> (--message <text> | --stdin) [--queue-if-busy]
alta session steer <thread-id> (--message <text> | --stdin)
alta session queue <thread-id> (--message <text> | --stdin)
alta session abort <thread-id> [--reason <text>]
alta session compact <thread-id> [--submit]
alta session join <thread-id>
```

Implementation source:

- `WorkThreadRuntimeService.CreateProjectThreadAsync`
- `WorkThreadRuntimeService.CreateGlobalThreadAsync`
- `IWorkThreadOrchestrator.LaunchThreadAsync`
- `IWorkThreadOrchestrator.SubmitPromptAsync`
- `IWorkThreadOrchestrator.SteerAsync`
- `IWorkThreadOrchestrator.QueuePromptAsync`
- `IWorkThreadOrchestrator.AbortAsync`
- `IWorkThreadOrchestrator.CompactAsync`

The current live tool dispatch path uses `WorkThreadRuntimeService` directly for create/send/steer/abort/compact and for durable queue persistence. The UI-facing `RuntimeWorkThreadOrchestratorAdapter.QueuePromptAsync` delegates to the same runtime queue service so the orchestrator contract and live tool use a shared durable queue record shape.

`session create` uses the model selection resolution rules in section 5.5. Its JSONL result record must include the resolved model selection and compact `modelRef`.

The current implementation persists per-thread model/reasoning preferences in `WorkThreadViewState.ThreadPreferences` when `session create` resolves a model selection so later `session model`, caller-session inheritance, and `--same-model-as` reuse the same `modelRef` after restart.

When `session create` is invoked by an agent session and the target session belongs to the same project as the caller, CodeAlta should default `parentThreadId` to the caller's source thread id. If the target session is created in another project, CodeAlta should still record `createdBy` provenance but should not create a sidebar parent/child link by default. `--parent <thread-id>` can request an explicit same-project parent; `--no-parent` suppresses the hierarchy link while keeping provenance. The current implementation validates explicit `--parent` before creating the session: the parent must exist, be visible to the caller, and be in the same target project scope or the same global scope.

`send` starts or continues a normal turn. `steer` must only target an active run and should fail with exit code 7 when the backend/runtime does not support steering. `queue` is explicit queueing for busy sessions. `send --queue-if-busy` is a convenience that maps to submit-or-queue behavior. Queued prompts are persisted in `WorkThreadLocalState.QueuedPrompts` with `queued`/`submitting`/`submitted`/`failed`-ready state fields, `queueItemId`, prompt preview, attribution, and eventual run/drain fields; `session list/show/status` expose the pending queued/submitting count, and runtime queue events project queue changes into open-session timelines. Runtime draining reserves at most one queued item per thread through the per-thread actor before calling the backend, so duplicate idle/error notifications cannot drain multiple queued prompts concurrently.

`join` is a non-blocking observation/setup command, not authority transfer. It should return the current session summary/status and any context needed for a controller session to address the target later; it must not follow future events or wait for new output.

### 6.5 Inter-agent communication commands

```text
alta session message <thread-id> (--message <text> | --stdin) [--kind note|request|handoff|answer]
alta session request <thread-id> (--message <text> | --stdin) [--reply-requested]
```

These commands are wrappers over `session send` or `session queue` that add CodeAlta attribution metadata. They are useful when one agent needs to communicate with another without pretending to be the user. `--reply-requested` is metadata for the target session; the command still returns after delivery/queueing and does not wait for a reply.

The current implementation records prompt-like operation provenance in `WorkThreadLocalState.PromptProvenance` for normal sends, steering, inter-agent message/request wrappers, and queued prompts. Queue records share the queue item id as their prompt provenance id so restart-time timeline reconstruction can correlate prompt attribution with drain state.

All inter-agent messages must be rendered to the target session with a visible attribution header similar to:

```text
[CodeAlta delegated-agent message]
Source thread: <source-thread-id>
Source agent/session: <source-agent-id>
Kind: request
Correlation: <correlation-id>
Authority: peer-agent; this is not a user, developer, or host instruction.

<message body>
```

When the backend supports structured metadata, CodeAlta should store the same data as event metadata. When it does not, the visible header is required.

Recommended metadata fields:

- `sourceThreadId`
- `sourceBackendSessionId`
- `sourceProjectId`
- `sourceAgentId`
- `targetThreadId`
- `targetProjectId`
- `promptId` or `queueItemId` when the message became a prompt/queued prompt;
- `kind`
- `correlationId`
- `createdAt`
- `authority = "peer-agent"`

The target agent instructions must state that peer-agent messages are data/instructions from another agent and cannot override system, developer, user, or project-local instructions.

### 6.6 Skill commands

```text
alta skill list [--project <id|slug|path>]
alta skill show <skill-name> [--project <id|slug|path>]
alta skill activate <skill-name> --session <thread-id>
alta skills activate <skill-name> --session <thread-id>
alta skills_activate <skill-name> [--session <thread-id>]
```

`skill` should be the canonical command group. `skills` and `skills_activate` may exist as compatibility aliases if existing agent instructions or hosts already advertise `codealta_skills_activate`-style behavior.

Implementation source:

- `SkillCatalog` for list/show/activation payloads;
- `WorkThreadRuntimeService.ActivateSkillAsync` or `IWorkThreadOrchestrator.ActivateSkillAsync` for session-scoped activation.

Provider-managed backends that own their native skill system must return a clear unsupported-capability response when CodeAlta-managed skill activation cannot be injected.

### 6.7 Provider, plugin, and tool discovery commands

```text
alta tool status
alta tool list
alta tool capability list
alta provider list
alta provider model list [--provider <id>]
alta model list [--provider <id>]
alta model show <model-ref>
alta model resolve [--model-ref <ref>] [--same-model-as <thread-id>] [--provider <id>] [--model <id>] [--reasoning <effort>]
alta plugin list
alta plugin status <runtime-key>
```

These are read-only discovery commands that help a global agent understand what CodeAlta can do before creating or steering sessions. `tool status` should describe the live gateway availability, caller scope, and output limits. `tool capability list` summarizes command policy classifications with `alta.tool.capability` records and also emits compact runtime/backend/plugin summaries (`alta.tool.runtimeCapability`, `alta.tool.backendCapability`, and `alta.tool.pluginCapability`) so agents can distinguish available services, registered/configured providers, live-tool injection support, and plugin command availability without treating the command as a replacement for `--help`. `provider model list` and `model list` should emit one `alta.model.item` JSONL record per model, including `modelRef`. `model resolve` should emit a single `alta.model.selection` JSONL record after applying the same precedence rules as `session create`.

Implementation source:

- live host composition root for loaded provider/backend ids and current/default provider preferences;
- `AgentHub.ListRegisteredBackends` and backend `ListModelsAsync`;
- `AgentModelInfo.DefaultReasoningEffort` and `SupportedReasoningEfforts` for model/ref validation;
- `PluginRuntimeManager` descriptors/diagnostics;
- merged tool definitions from orchestration/plugin bridges.

## 7. Architecture

### 7.1 Proposed projects and responsibilities

Use small shared services rather than placing runtime orchestration in the TUI project.

```text
src/CodeAlta.LiveTool/                # in-process command registry, DTOs, command handlers, output contracts
src/CodeAlta/                         # executable composition, Terminal/TUI, process entry point, live tool registration
src/CodeAlta.Orchestration/           # existing session/runtime operations and any narrow query additions
src/CodeAlta.Plugins.Abstractions/    # plugin contribution interfaces for alta live commands/tools
src/CodeAlta.Plugins/                 # adapts plugin contributions into the live command registry
```

`CodeAlta.LiveTool` should not depend on terminal UI controls. It can depend on catalog/orchestration abstractions and `XenoAtom.CommandLine`.

For 1.0 packaging, the only intended public package/API surface is `CodeAlta.Plugins.Abstractions`. `CodeAlta.LiveTool` types may remain `public` where useful for internal assembly boundaries and tests, but they are bundled with the CodeAlta executable and are not promised as separately consumable end-user APIs until a later decision.

No separate hosting/transport project is required for v1 because the command gateway is in-process only.

### 7.2 Command registry and execution context

Candidate abstractions:

```csharp
public sealed record AltaCommandPolicy
{
    public required string Path { get; init; }
    public bool RequiresInProcessRuntime { get; init; }
    public bool IsMutating { get; init; }
    public bool IsDisruptive { get; init; }
    public bool SupportsCatalogOnlyContext { get; init; }
}

public sealed record AltaCommandContext
{
    public required AltaCallerIdentity Caller { get; init; }
    public required IServiceProvider Services { get; init; }
    public required TextReader Stdin { get; init; }
    public required TextWriter Stdout { get; init; }
    public required TextWriter Stderr { get; init; }
    public int? MaxOutputRecords { get; init; }
    public int? MaxOutputBytes { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record AltaModelSelection
{
    public required string ProviderKey { get; init; }
    public string? ModelId { get; init; }
    public AgentReasoningEffort? ReasoningEffort { get; init; }
    public required string ModelRef { get; init; }
}

public interface IAltaCommandContributor
{
    IEnumerable<CommandNode> CreateCommandLineNodes(AltaCommandContributionContext context);
    IEnumerable<AltaCommandPolicy> GetCommandPolicies(AltaCommandContributionContext context);
}
```

The command registry should generate a `CommandApp` tree for in-process live-tool argument parsing and any future in-process UI/diagnostic command surfaces. Help is not a separate metadata service: every root command, command group, and leaf command that accepts options should include `HelpOption` so `XenoAtom.CommandLine` can render `alta --help`, `alta session --help`, `alta session tail --help`, and plugin command help automatically.

The `alta` executable dispatches recognized live-tool root commands directly to `AltaCommandRegistry` before starting the terminal UI. Root help is rendered through the live registry and appends the process-level TUI/plugin bootstrap options; command-group and leaf help are rendered by the same registry. Catalog-only `project` commands use `CatalogOptions`/`ProjectCatalog` services without creating a headless runtime, while runtime-backed commands compose a headless `CodeAltaHost` with the real catalog, orchestration, plugin, provider, model, and skill services. Unknown non-option roots are treated as potential plugin command roots and therefore compose the headless runtime before parsing, allowing external invocations such as `alta statistics estimate ...` to discover trusted built-in/plugin commands.

`XenoAtom.CommandLine` command graphs are intended for one invocation at a time: parsing mutates per-run option state, and concurrent `RunAsync`/`Parse` calls on the same command tree are not supported. The `alta` registry must therefore either create a fresh command tree with fresh, unattached `CommandNode` instances for each concurrent invocation, or serialize invocations through a single built tree. Prefer fresh per-invocation trees if multiple agents/plugins may call `alta` concurrently; if v1 starts with serialized dispatch, document the serialization and keep handlers non-blocking so the critical section stays short.

Plugin and core command contributors must not cache and reuse `CommandNode` instances across multiple trees after they have been attached to a parent. Contribution APIs should either return fresh nodes for each registry build or provide factories/descriptors that the registry uses to create fresh nodes.

`AltaCommandPolicy` is for in-process host enforcement and live-tool safety checks. It should not be treated as a replacement for help text or as a requirement to implement `alta describe` in v1.

Model selection parsing/resolution should live behind a small reusable service, for example `AltaModelSelectionResolver`, so `session create`, `session model`, `model resolve`, and plugin commands all apply the same defaults, compact ref formatting, reasoning validation, and caller-session inheritance.

### 7.3 Durable lineage and provenance model

The existing `WorkThreadDescriptor.ParentThreadId` should be generalized from legacy internal-thread metadata to same-project child-session lineage. New durable metadata should be added without making the TUI the source of truth:

```csharp
public sealed record AltaActorProvenance
{
    public required string Kind { get; init; } // user, agent, host, plugin
    public string? SourceThreadId { get; init; }
    public string? SourceBackendSessionId { get; init; }
    public string? SourceProjectId { get; init; }
    public string? SourceAgentId { get; init; }
    public string? PluginRuntimeKey { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record AltaThreadLineage
{
    public string? ParentThreadId { get; init; }
    public AltaActorProvenance? CreatedBy { get; init; }
}

public sealed record AltaPromptProvenance
{
    public required string PromptId { get; init; }
    public required AltaActorProvenance SubmittedBy { get; init; }
    public string? ParentPromptId { get; init; }
    public string? QueueItemId { get; init; }
    public string? RunId { get; init; }
}
```

Implementation can choose whether these are embedded directly in catalog descriptors/view state or persisted as sidecar metadata, but they must be durable enough to restore sidebar hierarchy and timeline attribution after restart. Backend transcripts are not sufficient because provider history may not preserve CodeAlta-specific provenance.

The current implementation stores `ParentThreadId` and `CreatedBy` on `WorkThreadDescriptor` YAML and mirrors both fields in `WorkThreadLocalState` for recoverable/live thread view-state restoration. Sidebar projections rebuild same-project parent/child trees from those durable fields, tolerate missing/cross-project parents by rendering the affected thread as a project/global root, and keep expanded/collapsed UI state separate from lineage metadata.

### 7.4 In-process dispatch

The live tool handler should dispatch directly to `AltaCommandRegistry` with an `AltaCommandContext`. It should not spawn a process, open a local socket, write endpoint discovery files, or serialize requests through IPC.

For tests and future UI command surfaces, callers can construct the same context with in-memory `TextReader`/`TextWriter` instances. The handler writes JSONL records, returns after the current command result is produced, and never keeps a request open to wait for future session events. Agent live-tool presentation should flatten captured output/diagnostics into the compact transcript defined in section 5.4 rather than nesting stdout/stderr JSONL in escaped strings.

The in-process command context should carry:

- service provider or explicit service ports for catalog, runtime, providers, plugins, and model selection;
- caller identity for agents, host code, UI surfaces, and plugins;
- stdin/stdout/stderr writers;
- JSONL truncation limits;
- cancellation token for bounded current work.

Frontend/TUI integrations, such as future command-palette actions or sidebar affordances, should call the same in-process dispatcher through application services. They should not reimplement `alta` parsing in UI controls or move reusable orchestration into the frontend project. Run bounded command dispatch outside UI event handlers where needed, then marshal any view-state updates back through existing frontend coordinators/projections in line with `doc/development-guide.md`.

### 7.5 Agent tool integration

For CodeAlta-managed backends, the runtime should add an `alta` tool definition to sessions when enabled by policy. The handler dispatches in-process directly to the command registry.

The implemented v1 policy is explicit allow-list based: `IAltaSessionToolBackendPolicy` enables the tool for CodeAlta-managed OpenAI chat/responses transports, including configured `openai-chat`, `openai-responses`, and `openai-codex-subscription` providers. It does not inject the tool into Codex app-server, generic ACP, Anthropic, Google GenAI, or other backends unless they are later added to that policy after adapter support exists.

Provider-managed backends may not accept host-injected tools. For those sessions, the model-visible `alta` live tool is only guaranteed when the host controls tool injection. V1 should not advertise an external shell command as an equivalent live-control path.

The `AgentToolResult` returned to the backend should contain one `AgentToolResultItem.Text` item: either the normal help text for `--help`, or the compact flat JSONL transcript for non-help commands. `AgentToolResult.Success` should be `true` only when the `alta.result.exitCode` is `0`; for non-zero exit codes, set `Error` to a short summary from the first `alta.error` record when available. Do not use tool progress callbacks for normal command output in v1 because `alta` commands are finite snapshots, not streams.

The tool should set caller identity:

```csharp
public sealed record AltaCallerIdentity
{
    public required string Kind { get; init; } // cli, agent, host, plugin
    public string? SourceThreadId { get; init; }
    public string? SourceBackendSessionId { get; init; }
    public string? SourceAgentId { get; init; }
    public string? SourceProjectId { get; init; }
    public string? PluginRuntimeKey { get; init; }
}
```

Caller identity is mandatory for mutating session commands so attribution can be persisted and rendered. Plugin calls should set `Kind = "plugin"` and `PluginRuntimeKey`; if the plugin invocation is associated with a thread/run, it should also set the source thread/session fields.

### 7.6 Plugin invocation service

Plugins should be able to call the in-process `alta` dispatcher as a host service, not only contribute new `alta` commands. This lets a plugin launch sessions, send prompts, query models, inspect status, or coordinate work using the same contracts as agents.

Candidate service shape exposed through plugin services:

```csharp
public interface IPluginAltaService
{
    ValueTask<PluginAltaCommandResult> InvokeAsync(
        IReadOnlyList<string> args,
        string? stdin = null,
        PluginAltaInvocationOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record PluginAltaCommandResult
{
    public required int ExitCode { get; init; }
    public required string TranscriptJsonl { get; init; }
    public bool Truncated { get; init; }
    public string? Error { get; init; }
}

public sealed record PluginAltaInvocationOptions
{
    public string? SourceThreadId { get; init; }
    public string? SourceBackendSessionId { get; init; }
    public string? SourceProjectId { get; init; }
    public string? SourceAgentId { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? MaxOutputRecords { get; init; }
    public int? MaxOutputBytes { get; init; }
    public TimeSpan? Timeout { get; init; }
}
```

Plugin-facing service and contribution DTOs belong in `CodeAlta.Plugins.Abstractions`, because that is the only intended public 1.0 API surface. The internal `CodeAlta.LiveTool` registry can use richer internal types, but it must adapt them to stable plugin-facing DTOs rather than requiring plugins to reference a future non-public `CodeAlta.LiveTool` package.

The service should:

- dispatch through `AltaCommandRegistry` with `AltaCallerIdentity.Kind = "plugin"`;
- set `PluginRuntimeKey` from the calling plugin context, not from plugin-supplied text;
- for project-scoped plugins, derive the effective source project id from the runtime-owned plugin scope before invoking `alta`;
- return the same flat JSONL transcript and exit-code metadata used by the agent live tool, without escaped nested stdout/stderr JSONL;
- apply the same safety policy, mutating/disruptive checks, output truncation, and non-blocking rules as agent calls;
- record plugin provenance for created sessions, prompts, queued prompts, steering prompts, and inter-agent messages;
- avoid recursive command contribution mutation while commands are executing.

The current implementation enforces runtime-owned plugin identity and scope derivation for both plugin runtime services and plugin-contributed `alta` commands: project-scoped plugin calls override any plugin-supplied `SourceProjectId` with the runtime-owned scope project before dispatch.

A plugin-created session in the same project as an explicit source thread may use that source thread as `parentThreadId` under the same rules as agent-created sessions. If there is no source thread, the session is plugin-created but not a sidebar child.

## 8. Plugin extension model

Plugins should be able to interact with the live `alta` tool in three complementary ways:

1. **Contribute command-line nodes**: contribute factories/descriptors that create fresh `XenoAtom.CommandLine.CommandNode` entries appearing in `alta --help` and invokable by agents, host code, UI surfaces, or other plugins.
2. **Contribute live command policies/handlers**: contribute safety/capability metadata and handlers that can run in a headless command context.
3. **Invoke existing commands**: call `IPluginAltaService.InvokeAsync(...)` to use built-in or plugin-contributed commands such as `session create`, `session send`, `model resolve`, or `session status`.

Do not require a plugin to change the `alta` tool schema. A plugin command should be addressable as normal arguments, for example:

```text
alta statistics session summarize <thread-id>
```

Recommended additions to plugin abstractions:

```csharp
public virtual IEnumerable<PluginAltaCommandContribution> GetAltaCommands() => [];

public sealed record PluginAltaCommandContribution
{
    public required string Path { get; init; }
    public string? Description { get; init; }
    public PluginAltaCommandPolicy Policy { get; init; } = new();
    public required PluginAltaCommandNodeFactory CreateCommandNode { get; init; }
    public int Order { get; init; }
}

public delegate CommandNode PluginAltaCommandNodeFactory(PluginAltaCommandContext context);

public sealed record PluginAltaCommandContext
{
    public required PluginDescriptor Plugin { get; init; }
    public required IPluginServices Services { get; init; }
    public PluginScope Scope { get; init; }
    public string? ScopeProjectId { get; init; }
    public string? ScopeProjectPath { get; init; }
    public required string CorrelationId { get; init; }
    public string? WorkingDirectory { get; init; }
    public required TextReader Stdin { get; init; }
    public required TextWriter Stdout { get; init; }
    public required TextWriter Stderr { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
```

The implemented v1 runtime rejects plugin command contributions whose top-level command name collides with a core command root (`project`, `session`, `skill`, `model`, `provider`, `plugin`, `tool`, `version`, or compatibility aliases) or with an earlier plugin root. Plugins that need multiple subcommands under one root should contribute one fresh root `Command` node containing those subcommands.

Plugin commands must declare whether they are read-only, mutating, disruptive, require the in-process runtime, or can run with catalog-only services. These flags are enforced by the host and may be summarized by `alta tool capability list`; normal command discovery remains `--help`.

Plugins invoking `alta` must not get a privileged bypass by default. A plugin can be trusted code, but `alta` command policy should still classify and audit the operation. Future plugin-management UI can add per-plugin policy controls; v1 should at least record `pluginRuntimeKey` provenance for mutating commands.

## 9. Safety and policy

### 9.1 Read-only vs mutating operations

Commands must be classified. Initial v1 classifications:

- read-only: `--help`, `version`, `project list/show/resolve`, `session list/show/status/children/model/tail/events/join`, `skill list/show`, `provider list`, `provider model list`, `model list/show/resolve`, `plugin list/status`, `tool status`, `tool list`, `tool capability list`;
- mutating: `project upsert`, `session create`, `session send`, `session steer`, `session queue`, `session compact`, `session message`, `session request`, `skill activate`;
- disruptive: `session abort`, future delete/archive operations.

The live tool handler should enforce command classification for audit/policy, but v1 does not require user-facing confirmation prompts for disruptive commands. CodeAlta sessions and plugins are configured as trusted/full-access actors in this phase. Future confirmation/pre-approval flows can be added without changing command names.

### 9.2 Attribution, lineage, and authority

Any message sent from one agent/session to another must be marked as peer-agent content. It must never be presented as:

- a user message without attribution;
- a developer instruction;
- a system/host instruction;
- project-local guidance.

Session creation and prompt submission must also record provenance. A restored thread should preserve whether it was user-created, host-created, plugin-created, or agent-created, including the plugin runtime key and source thread/session when available.

Minimum durable provenance shape:

```json
{
  "kind": "agent",
  "sourceThreadId": "codex:parent",
  "sourceBackendSessionId": "abc",
  "sourceProjectId": "01HX...",
  "sourceAgentId": "agent:parent",
  "pluginRuntimeKey": null,
  "correlationId": "01HX...",
  "createdAt": "2026-05-09T10:15:00Z"
}
```

For same-project child sessions, the durable thread descriptor should include both `parentThreadId` and `createdBy`. For cross-project child creation, the descriptor should include `createdBy` but leave `parentThreadId` empty unless a future cross-project UI explicitly supports such links.

Prompt submissions, queued prompts, steering prompts, and inter-agent messages should include equivalent `createdBy` / `submittedBy` metadata so the timeline can distinguish user prompts from agent-created and plugin-created prompts after restart.

### 9.3 Visibility policy

`tail`, `events`, and `show` can expose session content to another session. Visibility must be enforced by the host/runtime, not left to model instructions.

Default v1 policy:

- The global `~/.alta` agent is the coordinator and has full local visibility across all known projects, sessions, and subsessions.
- A project-scoped session can see its own session, same-project sessions, and same-project subsessions/children allowed by project policy.
- A project-scoped session can receive and respond to messages from the global coordinator even when the coordinator originated outside the project scope.
- Project-to-project visibility is not enabled by default. Future cross-project collaboration can be added through explicit host policy and provenance rules.
- Plugin invocations inherit the plugin scope plus any explicit source thread/project supplied by the host plugin context; they do not bypass visibility policy by fabricating source ids.

Commands denied by visibility policy should return exit code 4 and an `alta.error` JSONL record.

The current implementation treats callers without a project scope, including the global coordinator agent, as having full local visibility. Project-scoped agents can inspect/mutate only their own session and same-project sessions by default; explicit `session list --project`, `session create --project`, and `model resolve --same-model-as` targets are denied outside that scope, and project-scoped callers cannot create new global sessions. Project catalog listing/show/resolve is filtered to the caller's project, and project-scoped `project upsert` can only update that already-known project entry. Project-scoped `skill list/show` defaults to the caller's project roots and denies explicit `--project` references outside that scope. Project-scoped agents cannot inspect global coordinator transcripts, but `session message` and `session request` may target global coordinator threads so project sessions can reply with peer-agent attribution.

### 9.4 Coordinator `~/.alta/AGENTS.md`

CodeAlta should bootstrap a compact coordinator instruction file at `~/.alta/AGENTS.md`. The global `~/.alta` project uses this file to define the coordinator role, especially:

- it helps across all user projects and sessions;
- it can inspect projects/sessions through `alta` under the global visibility policy;
- it can create, send to, steer, queue, abort, and summarize project sessions when appropriate;
- it should preserve provenance and use peer-agent authority when communicating with project sessions;
- it should use `alta --help` and narrower help commands for progressive command discovery;
- it should inspect and activate skills through the canonical `alta skill` commands when the live tool is available, keeping `skills activate`/`skills_activate` only as compatibility aliases;
- it should prefer JSONL parsing and bounded/non-blocking `alta` commands.

The shipped template should live in CodeAlta content, for example:

```text
src/CodeAlta.Orchestration/content/coordinator/AGENTS.md
```

The file should be compact and packed with high-signal details rather than a full manual. More detailed `alta` reference can stay in docs or be discoverable through `alta --help`.

Recommended on-disk shape:

```md
<!-- CodeAlta:coordinator-managed:begin version="2026-05-09" checksum="..." -->
# CodeAlta Global Coordinator

<managed compact role/tool instructions>
<!-- CodeAlta:coordinator-managed:end -->

<!-- CodeAlta:local-instructions:begin -->
### Local instructions

<User-editable coordinator preferences go here. CodeAlta preserves this section.>
<!-- CodeAlta:local-instructions:end -->
```

Bootstrap/update policy:

1. On first launch, create `~/.alta/AGENTS.md` from the shipped template.
2. On later launches, update only the managed block when the shipped template version/checksum is newer.
3. Preserve the local instructions block byte-for-byte whenever possible.
4. If a user already has an unmarked `~/.alta/AGENTS.md`, migrate safely: create a timestamped backup, write the managed block, and place the previous file content inside the local instructions block.
5. If the markers are damaged or duplicated, do not overwrite silently; emit diagnostics and leave the file unchanged unless the user explicitly repairs/resets it.
6. The local section always comes last so user coordinator preferences can refine the managed role without editing generated text.

The managed block is generated content owned by CodeAlta. The local instructions block is user-owned content. CodeAlta must not remove or rewrite local instructions while refreshing the managed coordinator template.

### 9.5 Backpressure and truncation

The live tool must have output limits to avoid flooding context:

- default `session tail --last` should be small, for example 20 events;
- command results should include `truncated = true` when output is cut;
- event/message snapshot commands should use `--limit`/`--last` bounds and return immediately with currently available data;
- JSONL output is for finite result framing only and should not emit heartbeat/status frames for long-lived streams.

## 10. Structured event, timeline, and sidebar model

Avoid relying only on tags embedded in assistant text. Prefer structured runtime events and metadata wherever possible.

### 10.1 Sidebar lineage

The sidebar should be able to render same-project child sessions underneath the parent session that created them:

```text
Project: CodeAlta
  Parent session
    Child session created by parent agent
    Another child session
  Independent user-created session
```

Rules:

- only same-project `parentThreadId` relationships are rendered as nested sessions in v1;
- cross-project session creation remains traceable through `createdBy` metadata but is not rendered as a child under the source session;
- restored sessions must rebuild the hierarchy from durable thread metadata, not from only in-memory runtime state;
- collapsed/expanded state belongs to the UI view state, while lineage/provenance belongs to durable thread metadata;
- cycles or missing parents should be tolerated by rendering the affected session at the project root with a diagnostic/provenance marker.

Implementation note: the sidebar projection expands thread nodes that have children, orders root sessions by the most recent activity in their subtree, and renders threads with missing, cross-scope, or cyclic parent lineage at the project/global root with a warning provenance marker and tooltip explaining why the durable parent link was not nested.

### 10.2 Timeline provenance

Timeline items should visually distinguish at least:

- user-submitted prompts;
- agent-created prompts/messages from another session;
- plugin-created prompts/messages;
- queued prompts created by an agent or plugin;
- steering prompts created by an agent or plugin;
- session-created events that link to the child session;
- session-created events in another project, shown as trace/provenance rather than nested sidebar children.

A parent session timeline should receive a host event when it creates a same-project child session, containing the child `threadId`, title, project id, model selection, and correlation id. A child session timeline should receive a host event showing its creator. These host events are durable projections or reconstructable from durable metadata; they should be visible after restart.

### 10.3 Event records

Minimum event record shape for `session tail` JSONL output:

```json
{
  "type": "alta.session.event",
  "version": 1,
  "threadId": "...",
  "sequenceNumber": 123,
  "timestamp": "2026-05-09T10:15:00Z",
  "kind": "assistant.message",
  "role": "assistant",
  "source": {
    "kind": "backend",
    "backendId": "codex",
    "backendSessionId": "..."
  },
  "createdBy": {
    "kind": "agent",
    "sourceThreadId": "codex:parent",
    "sourceAgentId": "agent:parent"
  },
  "content": [
    { "type": "text", "text": "..." }
  ],
  "metadata": {}
}
```

For inter-agent messages, include source/target/correlation metadata as described in section 6.5. For prompts and session lifecycle events, include provenance fields from section 9.2.

If a backend cannot store metadata, CodeAlta should render a visible header and emit best-effort metadata in host-side projections. Durable CodeAlta metadata should still preserve provenance even when the backend transcript cannot.

## 11. Implementation phases

### Phase 0: Finalize narrow contracts

- [x] Confirm command names, flat JSONL transcript/result-header contracts, parse/usage error diagnostics, and exit codes.
- [x] Decide whether `skill` or `skills` is canonical; keep compatibility aliases as needed.
- [x] Define policy defaults for cross-session inspection and mutation.
- [x] Add the `alta` spec to agent/global instructions after commands exist.
- [x] Define the shipped compact coordinator `AGENTS.md` template, managed markers, version/checksum metadata, and migration behavior.

### Phase 1: Shared command registry and catalog-only commands

- [x] Create `CodeAlta.LiveTool`.
- [x] Build an `AltaCommandRegistry` around `XenoAtom.CommandLine`.
- [x] Move/extend existing top-level CLI parsing so `alta --help` can show new command groups.
- [x] Implement `version` and ensure `--help` works at the root, command-group, and leaf-command levels.
- [x] Choose and test the command-graph concurrency strategy: fresh per-invocation trees.
- [x] Implement catalog-only `project list/show/resolve/upsert` using `ProjectCatalog`.
- [x] Add tests for parsing, help availability, JSONL output, invalid usage diagnostics, and exit codes.
- [x] Add focused tests for plugin/custom `CommandOptionException`/validator failures.

### Phase 2: Coordinator instruction bootstrap

- [x] Add the shipped compact coordinator `AGENTS.md` template to CodeAlta content.
- [x] Implement `~/.alta/AGENTS.md` first-run creation.
- [x] Implement managed-block refresh when the shipped version/checksum is newer.
- [x] Preserve the local instructions block exactly and migrate unmarked existing files safely with a backup.
- [x] Add tests for first-run creation, managed-block update, local-section preservation, unmarked-file migration, and damaged-marker diagnostics.

### Phase 3: In-process live tool wiring

- [x] Register the `AltaCommandRegistry` and built-in command contributors in the CodeAlta composition root.
- [x] Build/freeze the final command catalog or contributor set after safe-mode/plugin bootstrap so plugin commands are available before sessions receive the live tool.
- [x] Expose an in-process dispatcher service that session/tool composition, UI code, and tests can call.
- [x] Add tests for in-process dispatch, compact flat live-tool transcript formatting, output capture, cancellation, truncation, and missing-service diagnostics.

### Phase 4: Session discovery and status

- [x] Add a narrow query service that merges live runtime snapshots, recoverable threads, local thread metadata, and backend session metadata.
- [x] Implement `session list/show/status/children/model` with provider/model/reasoning, `parentThreadId`, and provenance fields when known.
- [x] Distinguish `running`, `idle`, `inactive`, and `archived` states.
- [x] Add same-project parent/child hierarchy reconstruction for durable session metadata.
- [x] Add JSONL record contract tests and same-project filtering tests for session discovery/status commands.
- [x] Add hierarchy reconstruction tests for durable sidebar/session metadata.

### Phase 5: Session content inspection

- [x] Implement `session tail` from CodeAlta event projections / normalized stored events first, with backend history only as a best-effort fallback.
- [x] Implement `session events` as a finite snapshot over currently buffered/stored events, with `--since` and `--limit` filters.
- [x] Enforce sanitized visible content only.
- [x] Add tests for locked/partially written backend-history fallback returning warnings instead of failing the command.
- [x] Add truncation, `--last`, `--since`, `--limit`, `--include`, JSONL record, and timeout tests.

### Phase 6: Session creation and control

- [x] Implement provider/model/reasoning discovery and model-ref resolution (`provider model list`, `model list`, `model show`, `model resolve`).
- [x] Implement `session create`, `send`, `steer`, `abort`, `compact`, and non-blocking `join` through runtime/orchestration services where possible.
- [x] Implement durable/headless `session queue` through `IWorkThreadOrchestrator` or an equivalent real queue service.
- [x] Add durable caller/plugin attribution and same-project parent-thread assignment for `session create`.
- [x] Add JSONL caller/plugin attribution for prompt submission, steering, abort, compact, and inter-agent message command results.
- [x] Persist prompt/steering/inter-agent message provenance durably enough for restart-time timeline reconstruction.
- [x] Persist queued prompt provenance and drain state durably enough for restart-time timeline reconstruction.
- [x] Handle unsupported backend capabilities with exit code 7 and clear messages.
- [x] Add regression tests for model-ref parsing, caller-session model inheritance, `--same-model-as` with reasoning override, child-session provenance, agent-created prompt provenance, inter-agent prompt provenance, visibility denial, and steering unsupported cases.
- [x] Add regression tests for plugin-created prompt provenance, plugin-created session provenance, and busy-session queueing.

### Phase 7: Agent tool exposure

- [x] Add an `alta` agent tool definition for CodeAlta-managed/local-runtime sessions.
- [x] Dispatch in-process to the command registry with `AltaCallerIdentity.Kind = agent`.
- [x] Include compact prompt guidance that tells agents to run `alta --help` and then narrower help commands such as `alta session --help` for progressive discovery.
- [x] Add tool output truncation and command timeout controls.
- [x] Add tests for live tool invocation without spawning a process.

### Phase 8: Skills integration

- [x] Implement `skill list/show/activate` and compatibility aliases.
- [x] Route session activation through `IWorkThreadOrchestrator.ActivateSkillAsync` or `WorkThreadRuntimeService.ActivateSkillAsync`.
- [x] Return provider-managed-skill unsupported diagnostics where applicable.
- [x] Update skill docs/global instructions after behavior exists.

### Phase 9: Plugin extension support

- [x] Add plugin `alta` command contribution abstractions with stable DTOs in `CodeAlta.Plugins.Abstractions` and fresh command-node factories/descriptors.
- [x] Add `IPluginAltaService` (or equivalent) to plugin services so plugins can invoke built-in and plugin-contributed `alta` commands without referencing `CodeAlta.LiveTool` directly.
- [x] Merge core and plugin command-node factories/descriptors so plugin commands appear in the relevant `--help` output.
- [x] Enforce v1 collision rules and safety classifications.
- [x] Add tests with a sample plugin/fake plugin catalog contributing a read-only command, command discovery/help, collision handling, and runtime-owned plugin service identity/scope.
- [x] Add tests with a mutating plugin command and plugin invocation of `alta session create` through the plugin service.

### Phase 10: Inter-agent communication and policies

- [x] Implement `session message` and `session request` wrappers.
- [x] Persist/render peer-agent attribution metadata and prompt provenance.
- [x] Add enforced coordinator/project visibility policy checks.
- [x] Add sidebar/timeline projection updates for agent-created same-project child sessions and agent-created prompts.
- [x] Add tests ensuring peer-agent messages cannot be rendered as user/developer/system instructions.

## 12. Documentation and testing requirements

Before considering the feature complete:

- update `readme.md` and `doc/` user-facing pages with examples;
- update global/project agent instruction templates to advertise `alta` only when the live tool is available;
- test and document coordinator `~/.alta/AGENTS.md` managed/local section behavior;
- add command help snapshots for root, command-group, leaf-command, and plugin-contributed help output;
- add invalid-usage diagnostic snapshots for unknown options, bad enum values, missing required arguments, mutually exclusive options, and plugin-contributed command validation;
- add JSONL record contract tests for major command outputs and flat live-tool transcript/result-header formatting;
- add in-process command dispatch tests that do not require the terminal UI, including the chosen command-graph concurrency behavior;
- add visibility-policy tests for global coordinator access, project-scoped access, coordinator replies, and denied cross-project access;
- add orchestration tests for session list/send/steer/abort behavior and same-project child-session provenance;
- add plugin tests for in-process command contribution discovery, collision handling, plugin invocation of built-in commands, and plugin provenance;
- add sidebar/timeline projection tests for parent/child sessions and agent-created prompts;
- document unsupported backend behavior explicitly.

Current completion evidence: user-facing examples are documented in `readme.md`, `doc/readme.md#alta-live-tool`, `doc/skills.md#live-tool-commands`, and `doc/plugins.md#alta-live-tool-integration`; coordinator/skill instruction templates advertise `alta` only when it is available; regression coverage lives in `AltaLiveToolTests`, `ThreadRuntimeEventCoordinatorTests`, `ArchitectureGuardrailTests`, and catalog/plugin infrastructure tests for the command registry, JSONL transcripts, invalid usage diagnostics (unknown options, bad values, missing arguments, mutually exclusive flags, plugin validation), visibility (global coordinator access, same-project access, scoped project catalog/skill access, scoped plugin alta invocation, coordinator reply path, denied cross-project access), capability discovery, provenance, explicit parent validation, queue draining, plugin contributions/invocation, and timeline/sidebar projections.

## 13. Resolved v1 decisions

- Canonical command groups use singular nouns (`project`, `session`, `skill`, `model`, `provider`, `plugin`, `tool`). Plural forms are compatibility aliases only when needed.
- The global `~/.alta` agent is the coordinator and has full local visibility across projects/sessions. Its role and compact `alta` guidance are bootstrapped into a managed `~/.alta/AGENTS.md` with a preserved local instructions section.
- V1 has no user-facing confirmation prompts for disruptive commands. Agents/plugins run as trusted full-access actors; command classification remains for audit and future policy.
- `alta session tail` and `session events` read CodeAlta event projections / normalized stored events first. Backend history is fallback only and must tolerate transient file-write conflicts.
- For 1.0, `CodeAlta.Plugins.Abstractions` is the only intended public package/API. `CodeAlta.LiveTool` may expose public types internally but ships bundled with the executable, not as a separate stable API.
- Agent live-tool results use a compact flat JSONL transcript headed by `alta.result`; they must not wrap escaped stdout/stderr JSONL inside another JSON object.
- Runtime and capability discovery belongs under the canonical `tool` group (`tool status`, `tool list`, `tool capability list`); v1 should not add separate `runtime` or `capability` command groups.
- Because `XenoAtom.CommandLine` command graphs are not safe for concurrent invocations, the registry must use fresh command trees per concurrent call or serialize dispatch over a shared tree.
