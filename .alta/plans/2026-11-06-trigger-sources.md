---
title: Trigger sources - Project and Automation triggers, plugin triggers, scripts without a model
status: done
created: 2026-11-06
summary: Open the fixed list of automation triggers to trigger sources, built in or contributed by plugins (polled with a cursor or pushed by a listener), add the Project trigger of issue #354 and the Automation trigger for chaining, let a script automation run without a model, and let a plugin ship a trigger together with the script that handles it.
---

# Trigger sources

Issue [#354](https://github.com/CodeAlta/CodeAlta/issues/354) asks for a trigger that fires when a project is added to the catalog. Today the triggers are a fixed list: the 13 kinds of `AutomationTriggerKind` (`src/CodeAlta.Automations/AutomationModel.cs`), each hard-coded in the configuration, the keys and fingerprint, the look loop and the editor. A plugin can only feed events to the built-in `jira` kind (`IIssueEventSource`); it cannot add a trigger of its own.

This plan adds **trigger sources**: a trigger type that describes its events and fields, and gives its events to the host. CodeAlta's own triggers and plugins use the same contract. The first two built-in sources are **Project** and **Automation**. Separately, a script automation gets a mode that runs the script without a model turn.

## Decisions

| Decision | Choice |
| --- | --- |
| Scope of this round | The infrastructure, the Project trigger, the Automation trigger, and scripts without a model |
| How a source gives its events | Both: polled with a cursor the host keeps (plus a signal to look now), and a long-running push listener |
| `event` of a script | A JSON object for the new triggers; the existing triggers keep their text |
| How a project was added | Reported when the add happened in this process; `unknown` otherwise |
| Where a Project-triggered run goes | As today: a chat or the automation's fixed project; the project is in the event |
| Project events | `added`, `archived`, `removed` |
| Landing | A worktree branch, one commit per step, no push |
| Existing triggers | Unchanged: no refactor of the 13 kinds |
| Script without a model | Runs with no session, its rules declared on the automation: `alta` read-only and plugin commands, nothing that starts an agent; `tools.*` by `script_access` (read or write), in the sandbox of the automation; write without a sandbox only in the user's configuration |
| Scripts of a plugin | A plugin contributes scripts to the library, and a trigger can come with its script as a ready-made automation; the user still saves and enables it |

## What changes

### The contract (`CodeAlta.Plugins.Abstractions`, new `PluginAutomationTriggers.cs`)

- `PluginBase.GetAutomationTriggers()` returns `PluginAutomationTriggerContribution`s, registered by `PluginContributionRegistry` with conflict keys, like the agent tools.
- A contribution describes its trigger:
  - a type: `<plugin-id>/<name>` for a plugin; a bare name for a built-in source (`project`, `automation`);
  - a display name and a description;
  - its events (id and label, the default first); an automation can choose several;
  - its fields: text, `${secret:NAME}` reference, choice, several choices, boolean, space, project, automation; required or not;
  - whether it needs a project, needs none, or takes either.
- An `IAutomationTriggerSource` validates a trigger's settings and gives its events in one or both ways:
  - **Polled:** `ReadAsync(query)`. The query has the chosen events, the settings (secrets resolved by the host), the project path, and the cursor the host saved; null on the first look, which answers a starting cursor and no events. The answer has the events, the next cursor and a problem.
  - **Pushed:** `ListenAsync(context, sink)`. The host runs one listener per armed trigger while it is armed, restarts it with a growing pause when it fails, and cancels it on disable, pause or exit, as the Slack listener is run today.
  - A polled source can ask for a look now through a signal of `IPluginServices`.
- An event has an id (unique within its trigger), when it happened, its event name, a title, fields that are someone else's text, and a JSON payload for the script's `event`.

### The host (`CodeAlta.Automations`)

- **Model and configuration:** one new kind, `Source`, holds the type, the chosen events and the settings (`PluginTriggerSettings`, shared with plugins). A plugin's type is `<prefix>/<name>` with a prefix the plugin chooses (its runtime key, `Assembly:Type`, is not fit for a file); two plugins that give the same type are a conflict of the registry. It reads and writes `{ type = "project", on = ["added"], space = "work" }` or `{ type = "acme.deploys/deploy", on = ["succeeded"], environment = "prod" }`. The settings are part of `Key` (except credentials) and of `FingerprintKey`, so an automation that comes with a project asks for **Allow** again when they change.
- **Registry:** the built-in sources plus the contributions of the active plugins. When the source of a trigger is not active, the automation shows why ("The trigger acme.deploys/deploy comes from the plugin acme.deploys, which is not active"), and its configuration is written back unchanged.
- **Look loop:** polled sources are looked at with the other event triggers, every five minutes, or within 30 s after a signal. The cursor is kept in the trigger's watermark. Catch-up works as today: without **Catch up**, the first look after a start moves the cursor without running.
- **Runs:** the event id is the claim (`event:<trigger key>#<event id>`), so an event runs once. The host writes the "This automation was started by…" section from the event's title and fields, marked as someone else's text. A script that declares `event` gets the JSON payload (`CodeModeScriptRunPrompt` takes a JSON value besides text).
- **Where they are evaluated:** on the machine that runs the automation. The Project and Automation triggers see that machine's catalog and runs. A plugin trigger needs its plugin active there, which the node reports otherwise. Evaluation at a fleet server is out of scope.

### The Project trigger

- Events `added`, `archived`, `removed`, and an optional space. On each look it compares the catalog with the ids and names it saw, kept in its watermark. So it sees projects added by this instance, the TUI, another instance or a hand-made file, and catches up those added while CodeAlta was closed. A catalog change in this process signals a look at once.
- Event: `on`, `projectId`, `slug`, `name`, `path`, `spaces`, `archived`, `description`, `repository` and `repoUrl` (from the remotes of its folder), and `addedBy` (`window`, `alta project add`, `alta project upsert`, `session`, `fleet`, or `unknown`). The add paths in this process pass their origin to the catalog, whose new `Changed` event tells the source; a reconciled event says `unknown`. A removed project is described by what was last seen of it.
- With a space, the trigger watches the projects of that space: `added` when a project joins it (so `alta project add --space work`, which adds then assigns, starts it), `removed` when one leaves it. A project brought back from the archive is `added` again.

### The Automation trigger

- Events `completed`, `failed` (failed or interrupted) and `waiting` (waits for an approval), for chosen automations, by id, or for any other automation.
- Never for its own runs. A run started by this trigger records the run it follows (`Cause`); a run that would follow a chain of 5 is recorded as skipped, so that two automations cannot start each other forever.
- It is polled, and signaled whenever the runs of this machine change; it sees the runs of this machine only. Its cursor keeps the runs that waited, so that one approval is one event.
- Event: `on`, `automationId`, `automationName`, `runId`, `status`, `message`, `sessionId`, `startedAt`, `endedAt`, and the `detail` of what started that run.

### Scripts without a model or a session

What it is for: an event that should only call back into a plugin, behind the scenes: send an email, write a line to a log, update another tool. No model turn, and no session in the sidebar for each run.

- A script automation gets `script_mode = "direct"` (in the editor, **Run without a model**). The host runs the script itself, in Code Mode, with no session and no model. Its rules are declared on the automation, since nobody is there to approve a call.
- **`alta`:** the commands that change nothing, and the commands plugins contribute (`GetAltaCommands`): that is how it reaches a plugin. Nothing that starts or drives an agent: no `session create/send/steer`, `job`, `reminder`, `automation run`; no `agent()` nor `session()`. A command it does not get is not in its API.
- **`tools.*`:** the built-in tools, in the folder of the project (the home folder for a chat). `script_access = "read"`, the default, gives those that read; `"write"` also gives the edits and `shell_command`. They run in the sandbox of the automation (`sandbox`, else what the settings give), and as they are with `sandbox = "off"`. A command that asks to run outside the sandbox is refused: nobody can allow it.
- **Without a sandbox:** `script_access = "write"` without a sandbox is read only in the configuration of the user, which may say so for a plugin too; an automation of a project that writes names a sandbox profile, or is refused.
- **The run's record:** the run stays in the Automations tab with its status, its return value and what it showed with `present()` (both bounded), or the error of a script that fails. The return value is the answer of the run, also for **Write the answer back**. No session is created.
- **Allow:** an automation from a project waits for **Allow** of the script, and asks again when the script, its mode or its access changes.
- **Not in such a run:** `agent()`, `session()`, MCP tools and approvals. A script that needs a model stays a script automation of the usual mode, with its one model turn.
### Scripts of a plugin, and a trigger that comes with its script

A plugin can give a trigger together with the code that handles it, so that "when X happens, do Y" ships built into the plugin, with no script for the user to write.

- **Scripts of a plugin in the library.** `PluginBase.GetScripts()` contributes Code Mode scripts, with a `meta` like any script of the library. The library gets a fourth scope, `Plugin`, beside `BuiltIn`, `Global` and `Project` (`CodeModeScriptLibrary`). Their names are prefixed with the id of the plugin (`<plugin-id>/<name>`), so that they never take the name of a script of the user or of a project. They exist while the plugin is active, in the spaces the plugin is in, and are read-only: `alta script save` doesn't overwrite them.
- **A trigger names its script.** A trigger contribution can name a script of its plugin, with default arguments and a mode (`direct` by default). The editor then offers the trigger as a ready-made automation: the trigger, its script and the script's arguments are filled in, and the user changes what the plugin lets them change, typically the fields of the trigger and the arguments.
- **Turned on by the user.** A plugin creates and enables no automation by itself: it offers the template, and the user saves and enables it, as with every automation that runs by itself. A new version of the plugin whose script changed asks for **Allow** again, as a script that came with a `git pull` does.
- **Other uses.** The scripts of a plugin are also there for **Run…** in the Workflows tab, for `workflow("<plugin-id>/<name>")` in other scripts, and for agents, as any script of the library.

### Editor, RPC and CLI

- An RPC lists the trigger types with their descriptors. One generic component draws the fields of a source trigger; the events are a multi-select. The script form gets **Run without a model**.
- `alta automation create --trigger project@added,archived`, with settings in the table of the trigger; `--script-mode direct`, `--script-access write`, `--sandbox`. An automation an agent creates stays disabled.

## Steps

- [x] 1. Plan (this file); create the worktree branch `add/trigger-sources`.
- [x] 2. Model and configuration of the `Source` kind: read, write, validate, `Key`, `FingerprintKey`; tests in `CodeAlta.Desktop.Tests` (`AutomationConfigTests`).
- [x] 3. The public contract and its registration: `PluginAutomationTriggers.cs`, `PluginBase.GetAutomationTriggers()`, `PluginContributionRegistry`, the look-now signal; wiring in `DesktopApplication` and `NodeAutomations`; tests in `Plugins.Abstractions.Tests` and `Plugins.Tests`.
- [x] 4. Engine: polled sources with cursors, push listeners, claims, the prompt section, the JSON `event`; a missing source reported; tests with a fake source (`AutomationServiceTests`).
- [x] 5. Project trigger, with the origin reported by the in-process add paths; tests.
- [x] 6. Automation trigger, with the self and chain-depth guards; tests.
- [x] 7. Scripts without a model: `script_mode` and `script_access` (read, validate, fingerprint; write without a sandbox only in the user's configuration); the tools of a run without a session, in the sandbox of the automation (the sandbox router of the runtime takes the sandbox it is given); the unattended runner in `CodeAlta.LiveTool` (`alta` read-only and plugin commands, `tools.*` by access, discovery, `present()`); the runner of the automation that uses it; tests.
- [x] 8. Scripts of a plugin: `PluginBase.GetScripts()`, the `Plugin` scope of the library and its names, read-only; a trigger contribution that names its script, arguments and mode; tests.
- [x] 9. RPC and editor: trigger descriptors, the generic fields, the events multi-select, **Run without a model**, a plugin's trigger offered with its script as a ready-made automation; frontend tests; checked in the developer instance, both themes and a narrow window.
- [x] 10. `alta automation`: `--trigger <type>@<events>`, `--script-mode`, `--script-access`, `--sandbox`; tests. Deviation: no `--trigger-setting`; the settings of a trigger are written in its table, `--trigger '{ type = "project", on = ["added"], space = "work" }'`, which `alta` already read.
- [x] 11. Documentation:
  - `site/docs/automations.md`: the two triggers, a "CodeAlta events" section, scripts without a model, the automations a plugin offers;
  - the plugin runtime skill (`src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime`): how to write a trigger and its script, and an `automation-trigger` sample (a local git branch that moves, with a script that writes a line through the plugin's `alta` command);
  - `doc/plugins.md`: the new extension points; `doc/codemode.md`: the `Plugin` scope and runs without a session; `doc/fleet/orchestration.md`: where source triggers are evaluated.
- [x] 12. Full validation: Release build (0 warnings), `dotnet test`, `npm test`, `lunet build`. It found two problems of this branch, fixed: the fake hub of the Automations browser test had no `triggerTypes`, and the desktop read a `CodeModeSource` (the guard keeps `CodeAlta.CodeMode` out of the desktop assembly; the library script now gives its `Content`). `RemoteHost_ListsReadsAndDrivesTheSessionsOfTheNode` failed only while both suites ran, and passed alone.

## How it is verified

- [x] Configuration round trip of source triggers, with an unknown type kept as written (`AutomationSourceConfigTests`).
- [x] A fake polled source: first look starts nothing; a new event starts one run, once; catch-up; the cursor kept; a signal reads again soon; a failing source is shown and read again (`AutomationSourceTests`).
- [x] A fake push source: listens while armed, events run once; a listener that fails is shown and started again after a pause.
- [x] Project trigger: added, archived, removed; space filter; a catalog change signals a look; an automation runs with the project as the `event` of its script (`ProjectTriggerSourceTests`, `ProjectCatalogChangeTests`).
- [x] Automation trigger: chosen events; never its own runs; a chain stops at five (`AutomationRunTriggerTests`).
- [x] Script without a model (as decided at step 7, which replaced "`tools.*` is absent; `agent()` starts a session"): no session; read-only `alta` and plugin commands, nothing that starts an agent; `tools.*` that read, and write with write access; `present()` kept; a script that cannot run says why; a plugin's script runs with its event (`UnattendedScriptRunnerTests`, `CodeModePluginScriptsTests`, `AutomationConfigTests`).
- [x] Scripts of a plugin: in the library under their prefixed name, never saved over (`CodeModePluginScriptsTests`).
- [x] A plugin's trigger with its script: the editor fills the script and its mode (checked in the developer instance at step 9; no automated test of that menu). A changed script asks for **Allow** through the script hash every library script has; not tested for the plugin scope on its own.
- [x] Editor: the fields of a source trigger drawn from its descriptor; frontend tests pass.

## Risks

- **Scripts without a session:** today every script run belongs to a session, through its `exec` tool (`doc/codemode.md`, "Hosts"). Running one without a session means a Code Mode host with only the `alta` binding, and guards for `alta` commands that have no calling session. If that needs a larger change of the runtime than expected, step 7 stops and reports what it found before going further.
- **Loops:** an automation that edits the catalog could trigger itself through the Project trigger. The claims run each event once, and the automation does not start twice at once, but it could react to its own change. The guide says so.
- **The constructor of `AutomationService`** grows again; the sources come in as one registry rather than one more parameter each.

## Out of scope

- Session, Plan, Task and Review triggers (later rounds, on the same contract).
- Evaluating triggers at a fleet server, and fleet events (machine offline, budget).
- Moving the 13 existing kinds to the contract.
- Putting `alta` on the PATH of a command trigger (mentioned in #354).
