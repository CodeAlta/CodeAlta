---
title: Trigger sources - Project and Automation triggers, plugin triggers, scripts without a model
status: in-progress
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
| Script without a model | Runs with no session: `alta` commands (plugins' included) and `agent()`, no `tools.*`; it calls back into plugins behind the scenes |
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
- Event: `on`, `projectId`, `slug`, `name`, `path`, `spaces`, `repoUrl`, `archived`, and `addedBy` (`window`, `alta project add`, `alta project upsert`, `agent`, or `unknown`). The add paths in this process report their origin; a reconciled event says `unknown`. A removed project is described by what was last seen of it.

### The Automation trigger

- Events `completed`, `failed` (failed or interrupted) and `waiting` (waits for an approval), for chosen automations, by id, or for any other automation.
- Never for its own runs. A run started by this trigger carries its chain depth; past a depth of 5 it is recorded as skipped, so that two automations cannot start each other forever.
- It is signaled when a run of this machine changes; it sees the runs of this machine only.
- Event: `on`, `automationId`, `automationName`, `runId`, `status`, `message`, `sessionId`, `startedAt`, `endedAt`, and the `detail` of what started that run.

### Scripts without a model or a session

What it is for: an event that should only call back into a plugin, behind the scenes: send an email, write a line to a log, update another tool. No model turn, and no session in the sidebar for each run.

- A script automation gets `script_mode = "direct"` (in the editor, **Run without a model**). The host runs the script itself, in Code Mode, with no session and no model.
- **What the script can call:** the `alta` commands, which include those contributed by plugins (`GetAltaCommands`): that is how it reaches a plugin. These commands run with no calling session and with the guards of an automation's sessions; for example, a prompt goes only to a session of the same run. There is no `tools.*`: a shell command or a file edit needs the permissions and the sandbox of a session. A plugin writes its own files.
- **When a model is needed after all:** `agent()` starts a session of the automation, as a run with a prompt does, and the script gets its answer. So a script can decide on its own and call a model only when it has to.
- **The run's record:** the run stays in the Automations tab with its status, its return value and what it showed with `present()` (both bounded), or the error of a script that fails. The return value is the answer of the run, also for **Write the answer back**.
- **Autonomy and Allow:** an automation from a project waits for **Allow** of the script, as today. A plugin's `alta` command is subject to the automation's autonomy, as a plugin's tool is.
- **First task of the step:** check that `AltaScriptBinding` and `CodeModeRunRegistry` can run a script without a session, and how a plugin's `alta` command behaves with no session in its context. That decides the shape of this step.

### Scripts of a plugin, and a trigger that comes with its script

A plugin can give a trigger together with the code that handles it, so that "when X happens, do Y" ships built into the plugin, with no script for the user to write.

- **Scripts of a plugin in the library.** `PluginBase.GetScripts()` contributes Code Mode scripts, with a `meta` like any script of the library. The library gets a fourth scope, `Plugin`, beside `BuiltIn`, `Global` and `Project` (`CodeModeScriptLibrary`). Their names are prefixed with the id of the plugin (`<plugin-id>/<name>`), so that they never take the name of a script of the user or of a project. They exist while the plugin is active, in the spaces the plugin is in, and are read-only: `alta script save` doesn't overwrite them.
- **A trigger names its script.** A trigger contribution can name a script of its plugin, with default arguments and a mode (`direct` by default). The editor then offers the trigger as a ready-made automation: the trigger, its script and the script's arguments are filled in, and the user changes what the plugin lets them change, typically the fields of the trigger and the arguments.
- **Turned on by the user.** A plugin creates and enables no automation by itself: it offers the template, and the user saves and enables it, as with every automation that runs by itself. A new version of the plugin whose script changed asks for **Allow** again, as a script that came with a `git pull` does.
- **Other uses.** The scripts of a plugin are also there for **Run…** in the Workflows tab, for `workflow("<plugin-id>/<name>")` in other scripts, and for agents, as any script of the library.

### Editor, RPC and CLI

- An RPC lists the trigger types with their descriptors. One generic component draws the fields of a source trigger; the events are a multi-select. The script form gets **Run without a model**.
- `alta automation create|update --trigger project@added,archived` and `--trigger-setting space=work`; `--script-mode direct`. An automation an agent creates stays disabled.

## Steps

- [x] 1. Plan (this file); create the worktree branch `add/trigger-sources`.
- [x] 2. Model and configuration of the `Source` kind: read, write, validate, `Key`, `FingerprintKey`; tests in `CodeAlta.Desktop.Tests` (`AutomationConfigTests`).
- [ ] 3. The public contract and its registration: `PluginAutomationTriggers.cs`, `PluginBase.GetAutomationTriggers()`, `PluginContributionRegistry`, the look-now signal; wiring in `DesktopApplication` and `NodeAutomations`; tests in `Plugins.Abstractions.Tests` and `Plugins.Tests`.
- [ ] 4. Engine: polled sources with cursors, push listeners, claims, the prompt section, the JSON `event`; a missing source reported; tests with a fake source (`AutomationServiceTests`).
- [ ] 5. Project trigger, with the origin reported by the in-process add paths; tests.
- [ ] 6. Automation trigger, with the self and chain-depth guards; tests.
- [ ] 7. Scripts without a model or a session: the run in Code Mode without a session, the `alta` binding with the guards of an automation, `agent()` starting a session of the automation, the run's record; tests, including a fake plugin `alta` command that the script calls.
- [ ] 8. Scripts of a plugin: `PluginBase.GetScripts()`, the `Plugin` scope of the library and its names, read-only; a trigger contribution that names its script, arguments and mode; tests.
- [ ] 9. RPC and editor: trigger descriptors, the generic fields, the events multi-select, **Run without a model**, a plugin's trigger offered with its script as a ready-made automation; frontend tests; checked in the developer instance, both themes and a narrow window.
- [ ] 10. `alta automation`: `--trigger <type>@<events>`, `--trigger-setting`, `--script-mode`; tests.
- [ ] 11. Documentation:
  - `site/docs/automations.md`: the two triggers, a "CodeAlta events" section, scripts without a model, the automations a plugin offers;
  - the plugin runtime skill (`src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime`): how to write a trigger and its script, and an `automation-trigger` sample (a local git branch that moves, with a script that writes a line through the plugin's `alta` command);
  - `doc/plugins.md`: the new extension points; `doc/codemode.md`: the `Plugin` scope and runs without a session; `doc/fleet/orchestration.md`: where source triggers are evaluated.
- [ ] 12. Full validation: build, `dotnet test`, `npm test`, `lunet build`.

## How it is verified

- [ ] Configuration round trip of source triggers, with an unknown type kept as written.
- [ ] A fake polled source: first look starts nothing; a new event starts one run, once; catch-up; a cursor kept across a restart; a signal looks within 30 s.
- [ ] A fake push source: events run once; the listener ends on disable and pause and restarts after a failure.
- [ ] Project trigger: added, archived, removed; space filter; added while closed with and without **Catch up**; `addedBy`.
- [ ] Automation trigger: each event; never its own runs; a chain stops at depth 5.
- [ ] Script without a model: no session is created; it calls a plugin's `alta` command with the event; its return value is the answer; a failure fails the run; `tools.*` is absent; `agent()` starts one session of the automation; autonomy still applies.
- [ ] Scripts of a plugin: listed under their prefixed name while the plugin is active, gone when it is not, never overwritten by a save; a project script of the same short name does not hide them.
- [ ] A plugin's trigger with its script: the editor fills the trigger, script, arguments and mode; the automation is saved disabled until the user enables it; a changed script asks for **Allow** again.
- [ ] Editor: a plugin trigger's fields drawn from its descriptor; frontend tests pass.

## Risks

- **Scripts without a session:** today every script run belongs to a session, through its `exec` tool (`doc/codemode.md`, "Hosts"). Running one without a session means a Code Mode host with only the `alta` binding, and guards for `alta` commands that have no calling session. If that needs a larger change of the runtime than expected, step 7 stops and reports what it found before going further.
- **Loops:** an automation that edits the catalog could trigger itself through the Project trigger. The claims run each event once, and the automation does not start twice at once, but it could react to its own change. The guide says so.
- **The constructor of `AutomationService`** grows again; the sources come in as one registry rather than one more parameter each.

## Out of scope

- Session, Plan, Task and Review triggers (later rounds, on the same contract).
- Evaluating triggers at a fleet server, and fleet events (machine offline, budget).
- Moving the 13 existing kinds to the contract.
- Putting `alta` on the PATH of a command trigger (mentioned in #354).
