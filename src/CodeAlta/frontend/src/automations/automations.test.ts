import assert from "node:assert/strict";
import test from "node:test";
import type { AutomationItem, AutomationRunItem, AutomationTriggerItem } from "#neoastra";
import { locales, translate, type Locale, type MessageKey } from "../localization";
import { defaultSessionSettings } from "../sessionSettings/sessionSettings";
import { automationTemplates, canReply, dayName, denseTimeline, describeTrigger, describeTriggers, emptyForm, filterAutomations, formOf, formProblem, formSettings, inputOf, integrationTypes, isHooked, isIntegration, isSchedule, maximumNameLength, newAutonomy, describeAutonomy, toolNames,
  maximumPromptLength, needsProject, newTrigger, openableRun, placementLabel, placementProblem, placementWait, scriptArgumentsTemplate, runStatusLabel, runTone, runTriggerLabel, runsWithFacts, secretName, secretReference, sessionOrigin, sessionSettingsOf, timelinePosition, timelineRows, triggerIcon, triggerLabel,
  triggerTone, triggerTypes, type AutomationForm } from "./automations";

const english = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate("en", key, parameters);
const trigger = (type: string, change: Partial<AutomationTriggerItem> = {}): AutomationTriggerItem =>
  ({ type, minute: 0, every: 1, at: [], days: [], expression: null, event: "opened", authors: "trusted", command: null, folder: null, integration: null, ...change });
const item = (id: string, change: Partial<AutomationItem> = {}): AutomationItem => ({
  id, name: `Automation ${id}`, enabled: true, prompt: "Do the thing.", projectId: null, projectName: null, projectFolder: null, storeProjectId: null, file: "/home/config.toml",
  provider: null, model: null, effort: null, agent: null, permissionMode: null, reviewBeforeCommit: null, sandbox: null, catchUp: false, triggers: [], problem: null, nextRunAt: null, running: false, lastRun: null, repository: null, watchProblem: null, allowed: true, script: null, scriptArguments: null, options: null, runsOn: null, publishTo: null, placement: null, autonomy: null, ...change,
});
const run = (id: string, sessionId: string | null): AutomationRunItem =>
  ({ id, automationId: "a", name: "A", sessionId, projectId: null, startedAt: "2026-10-06T12:00:00Z", endedAt: null, trigger: "manual", detail: null, status: "running", message: null, machineId: null, reply: null, output: null, calls: null });

test("a trigger reads as a sentence, and several as one line", () => {
  const read = (value: AutomationTriggerItem, locale: Locale = "en") => describeTrigger(value, (key, parameters) => translate(locale, key, parameters), locale);
  assert.equal(read(trigger("hourly", { minute: 5 })), "Every hour at :05");
  assert.equal(read(trigger("hourly", { minute: 30, every: 6 })), "Every 6 hours at :30");
  assert.equal(read(trigger("daily", { at: ["09:00", "17:30"] })), "Daily at 09:00, 17:30");
  // The days are read in the order of the week, whatever the order they were chosen in.
  assert.equal(read(trigger("weekly", { days: ["thu", "mon"], at: ["08:30"] })), "Mon, Thu at 08:30");
  assert.equal(read(trigger("cron", { expression: "0 9 * * 1-5" })), "Cron 0 9 * * 1-5");
  assert.equal(read(trigger("issue")), "When an issue is opened");
  assert.equal(read(trigger("pull_request")), "When a pull request is opened");
  assert.equal(read(trigger("pull_request", { event: "updated" })), "When a pull request is updated");
  assert.equal(read(trigger("command", { command: " gh run watch 123 --exit-status ", folder: "tools" })), "When gh run watch 123 --exit-status succeeds");
  // A long command is named by its start; where an automation is read before it is allowed, it is whole, with its folder.
  assert.equal(read(trigger("command", { command: "x".repeat(200) })), `When ${"x".repeat(59)}… succeeds`);
  assert.equal(describeTrigger(trigger("command", { command: "x".repeat(200), folder: " tools " }), english, "en", true), `When ${"x".repeat(200)} succeeds (in tools)`);
  assert.equal(describeTrigger(trigger("command", { command: "wait-for-it" }), english, "en", true), "When wait-for-it succeeds");
  assert.equal(describeTrigger(trigger("daily", { at: ["09:00"] }), english, "en", true), "Daily at 09:00");
  for (const locale of locales) assert.ok(read(trigger("command", { command: "wait-for-it" }), locale).includes("wait-for-it"), locale);
  assert.equal(read(trigger("later")), "later", "A kind this page does not know is shown as it is named.");
  assert.equal(describeTriggers([], english, "en"), "Manual");
  assert.equal(describeTriggers([trigger("daily", { at: ["09:00"] }), trigger("issue")], english, "en"), "Daily at 09:00 · When an issue is opened");
  for (const locale of locales) {
    assert.ok(read(trigger("weekly", { days: ["mon"], at: ["08:30"] }), locale).includes(dayName("mon", locale)), locale);
    assert.ok(read(trigger("daily", { at: ["09:00"] }), locale).includes("09:00"), locale);
  }
  assert.equal(dayName("someday", "en"), "someday");
});

test("each kind of trigger has its label, icon and color; a run by hand has its own", () => {
  assert.deepEqual(triggerTypes.map(type => english(triggerLabel(type))), ["Hourly", "Daily", "Weekly", "Cron", "Issue", "Pull request", "Jira issue", "Command",
    "Jira version", "Jira query", "Slack", "incident.io", "Webhook"]);
  assert.equal(new Set(triggerTypes.map(triggerIcon)).size, triggerTypes.length);
  // A color per kind of the application's own triggers; those of other tools have a color each too, from fewer tones.
  assert.equal(new Set(triggerTypes.filter(type => !isIntegration(type)).map(triggerTone)).size, triggerTypes.length - integrationTypes.length);
  assert.ok(integrationTypes.every(type => triggerTone(type) !== "muted"));
  assert.deepEqual([triggerIcon(undefined), triggerTone(undefined), english(triggerLabel("unknown"))], ["hand", "muted", "Manual"]);
  assert.deepEqual(triggerTypes.filter(type => !isSchedule({ type })), ["issue", "pull_request", "jira", "command", ...integrationTypes]);
  // A command watches no repository: it runs in a chat too.
  assert.deepEqual(triggerTypes.filter(type => needsProject({ type })), ["issue", "pull_request", "jira"]);
  assert.deepEqual([runTone("running"), runTone("completed"), runTone("failed"), runTone("skipped"), runTone("interrupted")], ["running", "ok", "failed", "muted", "muted"]);
  assert.deepEqual(["running", "completed", "failed", "cancelled", "interrupted", "skipped", "later"].map(status => english(runStatusLabel(status))),
    ["Running", "Completed", "Failed", "Cancelled", "Interrupted", "Skipped", "Unknown"]);
  // A run a budget of its machine holds says what it waits for.
  assert.deepEqual([runTone("held"), english(runStatusLabel("held"))], ["held", "Waits for Continue anyway"]);
  assert.deepEqual(["manual", "daily", "issue", "pull_request", "command", "try", "slack"].map(name => english(runTriggerLabel(name))),
    ["Manual", "Daily", "Issue", "Pull request", "Command", "Try", "Slack"]);
});

test("a trigger of another tool reads as a sentence, starts with its default event, and names its secrets by reference", () => {
  const read = (value: AutomationTriggerItem) => describeTrigger(value, english, "en");
  const version = newTrigger("jira_version");
  assert.equal(read({ ...version, integration: { ...version.integration!, jiraProject: "PLAT" } }), "When a version of PLAT is released");
  assert.equal(read(newTrigger("jira_query")), "When an issue matches a Jira query");
  assert.equal(read({ ...newTrigger("jira_query"), event: "updated" }), "When an issue changes while it matches a Jira query");
  assert.equal(read(newTrigger("slack")), "When the app is mentioned in Slack");
  assert.equal(read({ ...newTrigger("slack"), event: "message" }), "When a message is posted in Slack");
  assert.equal(read(newTrigger("incident_io")), "When incident.io declares an incident");
  assert.equal(read(newTrigger("webhook")), "When a signed webhook arrives");
  assert.deepEqual(integrationTypes.map(type => newTrigger(type).event), ["released", "matches", "mention", "declared", "received"]);
  // A new trigger only reads: writing back where the event came from is turned on by the person.
  assert.ok(integrationTypes.every(type => newTrigger(type).integration!.reply === false));
  assert.equal(newTrigger("webhook").integration!.signature, "standard");
  assert.deepEqual([needsProject(newTrigger("jira_query")), canReply("webhook"), isHooked("incident_io"), isHooked("slack")], [false, false, true, false]);
  assert.equal(secretReference(" JIRA_TOKEN "), "${secret:JIRA_TOKEN}");
  assert.equal(secretReference("  "), null);
  assert.deepEqual([secretName("${secret:JIRA_TOKEN}"), secretName(null), secretName("plain")], ["JIRA_TOKEN", "", "plain"]);
});

test("a new trigger starts with values that can be saved; a command waits to be written", () => {
  for (const type of triggerTypes) {
    const form: AutomationForm = { ...emptyForm("p"), name: "n", prompt: "p", triggers: [newTrigger(type)] };
    assert.equal(formProblem(form), type === "command" ? "Write the command of the trigger." : null, type);
  }
  assert.deepEqual(newTrigger("weekly"), { type: "weekly", minute: 0, every: 1, at: ["09:00"], days: ["mon"], expression: null, event: "opened", authors: "trusted", command: null, folder: null, integration: null });
  assert.equal(newTrigger("cron").expression, "0 9 * * 1-5");
  assert.deepEqual([newTrigger("command").command, newTrigger("command").folder, newTrigger("daily").command], ["", null, null]);
});

test("a form says what it misses before it is sent", () => {
  const form: AutomationForm = { ...emptyForm("p"), name: "Nightly", prompt: "Review." };
  assert.equal(formProblem(form), null);
  assert.equal(formProblem({ ...form, name: "  " }), "Give the automation a name.");
  assert.equal(formProblem({ ...form, name: "x".repeat(maximumNameLength + 1) }), "The name is too long.");
  assert.equal(formProblem({ ...form, prompt: " \n" }), "Write the prompt the automation sends.");
  assert.equal(formProblem({ ...form, prompt: "x".repeat(maximumPromptLength + 1) }), "The prompt is too long.");
  assert.equal(formProblem({ ...form, triggers: [trigger("daily")] }), "Add a time to the trigger.");
  assert.equal(formProblem({ ...form, triggers: [trigger("weekly", { at: ["09:00"] })] }), "Choose a day for the weekly trigger.");
  assert.equal(formProblem({ ...form, triggers: [trigger("cron", { expression: "  " })] }), "Write the cron expression.");
  // An event is one of the repository of a project.
  assert.equal(formProblem({ ...form, projectId: null, triggers: [trigger("issue")] }), "A trigger on issues or pull requests needs a project.");
  assert.equal(formProblem({ ...form, projectId: null, triggers: [trigger("daily", { at: ["09:00"] })] }), null);
  // A command is written, and runs in a chat as well as in a project.
  assert.equal(formProblem({ ...form, triggers: [trigger("command", { command: "  " })] }), "Write the command of the trigger.");
  assert.equal(formProblem({ ...form, projectId: null, triggers: [trigger("command", { command: "wait-for-it" })] }), null);
  for (const locale of locales) assert.ok(translate(locale, "Write the command of the trigger.").length > 0);
  for (const locale of locales) assert.ok(translate(locale, "A trigger on issues or pull requests needs a project.").length > 0);
});

test("what is sent for a form is trimmed, and names a model only with its provider", () => {
  const form: AutomationForm = { ...emptyForm("p"), id: "id", name: "  Nightly ", prompt: "Review.\n", provider: " codex ", model: " gpt ", effort: "High", agent: " reviewer ", catchUp: true,
    permissionMode: " acceptEdits ", reviewBeforeCommit: false, sandbox: "off",
    storeInProject: true, triggers: [trigger("daily", { at: ["17:30", "09:00"], expression: "left over" }), trigger("cron", { expression: " 0 9 * * * " })] };
  assert.deepEqual(inputOf(form), { id: "id", name: "Nightly", enabled: true, prompt: "Review.\n", script: null, scriptArguments: null, projectId: "p", provider: "codex", model: "gpt", effort: "High",
    agent: "reviewer", catchUp: true, permissionMode: "acceptEdits", reviewBeforeCommit: false, sandbox: "off", options: null, runsOn: null, publishTo: null, autonomy: null,
    triggers:[trigger("daily", { at: ["09:00", "17:30"] }), trigger("cron", { expression: "0 9 * * *" })] });
  assert.deepEqual([inputOf(emptyForm(null)).permissionMode, inputOf(emptyForm(null)).reviewBeforeCommit, inputOf(emptyForm(null)).sandbox], [null, null, null], "The defaults are sent as nothing.");
  assert.deepEqual([inputOf({ ...form, provider: "" }).model, inputOf({ ...form, provider: "" }).effort, inputOf({ ...form, model: "" }).effort, inputOf({ ...form, agent: " " }).agent], [null, null, null, null]);
  assert.equal(inputOf({ ...form, triggers: [] }).catchUp, false, "An automation that is run by hand misses nothing.");
  assert.equal(inputOf({ ...form, triggers: [trigger("issue")] }).catchUp, true);
  // A command is sent trimmed, with its folder when it names one; another kind sends neither.
  assert.deepEqual(inputOf({ ...form, triggers: [trigger("command", { command: " wait-for-it --once ", folder: " tools " }), trigger("command", { command: "other", folder: "  " }),
    trigger("daily", { at: ["09:00"], command: "left over", folder: "left over" })] }).triggers.map(value => [value.command, value.folder]),
    [["wait-for-it --once", "tools"], ["other", null], [null, null]]);
});

test("an automation of yours can run on a paired machine; one of a project, and a command trigger, stay here", () => {
  const form: AutomationForm = { ...emptyForm("p"), name: "Nightly", prompt: "Review.", runsOn: "mini", publishTo: null };
  assert.equal(inputOf(form).runsOn, "mini");
  assert.equal(inputOf({ ...form, storeInProject: true }).runsOn, null, "The configuration of a project runs on the machines that have it.");
  assert.equal(formProblem({ ...form, triggers: [trigger("command", { command: "wait-for-it" })] }), "A command trigger runs on this computer only.");
  assert.equal(formProblem({ ...form, storeInProject: true, triggers: [trigger("command", { command: "wait-for-it" })] }), null);
  assert.equal(formOf(item("a", { runsOn: "mini", publishTo: null })).runsOn, "mini");
  assert.equal(placementLabel(item("a"), english), null);
  assert.equal(placementLabel(item("a", { runsOn: "mini", publishTo: null, placement: { machineId: "mini", machineName: "Mac mini", state: "copied", message: null, copiedAt: null } }), english), "On Mac mini");
  assert.equal(placementLabel(item("a", { runsOn: "gone", publishTo: null, placement: { machineId: "gone", machineName: null, state: "failed", message: "x", copiedAt: null } }), english), "On Unavailable machine");
  assert.equal(placementProblem(item("a", { runsOn: "mini", publishTo: null, placement: { machineId: "mini", machineName: "Mac mini", state: "failed", message: "Refused.", copiedAt: null } })), "Refused.");
  assert.equal(placementProblem(item("a", { runsOn: "mini", publishTo: null, placement: { machineId: "mini", machineName: "Mac mini", state: "pending", message: "Offline.", copiedAt: null } })), null,
    "A machine that is away is not a problem: it takes the automation when it answers.");
  const waiting = item("a", { runsOn: "mini", publishTo: null, placement: { machineId: "mini", machineName: "Mac mini", state: "waiting", message: "It waits until Laptop takes away its copy.", copiedAt: null } });
  assert.equal(placementWait(waiting), "It waits until Laptop takes away its copy.");
  assert.equal(placementProblem(waiting), null, "A machine that waits for the one it left is no problem of its own.");
  assert.equal(placementWait(item("a", { runsOn: "mini", publishTo: null, placement: { machineId: "mini", machineName: "Mac mini", state: "copied", message: null, copiedAt: null } })), null);
  for (const locale of locales) assert.ok(translate(locale, "On {machine}", { machine: "M" }).includes("M"), locale);
});

test("an automation that runs on a machine is published to a team or the organization; on this computer or in a project it stays the person's", () => {
  const form: AutomationForm = { ...emptyForm(null), name: "Deploy", prompt: "Deploy.", runsOn: "box", publishTo: "team:infra" };
  assert.equal(inputOf(form).publishTo, "team:infra");
  assert.equal(formOf(item("a", { runsOn: "box", publishTo: "organization" })).publishTo, "organization");
  assert.equal(inputOf({ ...form, runsOn: null }).publishTo, null);
  assert.equal(inputOf({ ...form, storeInProject: true }).publishTo, null);
});

test("an automation that runs a script sends its script and arguments, and no prompt", () => {
  const form: AutomationForm = { ...emptyForm("p"), name: "Review", prompt: "Left over.", script: " review-pr ", scriptArguments: ' { "pr": 7 } ' };
  assert.equal(formProblem(form), null);
  assert.equal(formProblem({ ...form, scriptArguments: "[1]" }), "The arguments of the script are a JSON object.");
  assert.equal(formProblem({ ...form, scriptArguments: "{ pr: 7" }), "The arguments of the script are a JSON object.");
  assert.equal(formProblem({ ...form, scriptArguments: "" }), null);
  const input = inputOf(form);
  assert.deepEqual([input.prompt, input.script, input.scriptArguments], ["", "review-pr", '{ "pr": 7 }']);
  assert.deepEqual([inputOf({ ...form, script: "" }).prompt, inputOf({ ...form, script: "" }).scriptArguments], ["Left over.", null], "Without a script, the prompt is sent.");
  assert.deepEqual(formOf(item("a", { script: "triage", scriptArguments: '{"days":1}', prompt: "" })).script, "triage");
  assert.equal(filterAutomations([item("a", { script: "triage", prompt: "" })], "triage", "all").length, 1, "A search finds the script.");
  assert.equal(scriptArgumentsTemplate([{ name: "pr", type: "integer", required: true }, { name: "depth", type: "string", required: false }]), '{\n  "pr": 0\n}');
  assert.equal(scriptArgumentsTemplate([{ name: "depth", type: "string", required: false }]), "");
});

test("the form of an automation holds what it is, and where it is written", () => {
  const stored = item("a", { projectId: "p", storeProjectId: "p", provider: "codex", model: "gpt", effort: "Low", agent: "reviewer", catchUp: true, triggers: [trigger("issue")], enabled: false,
    permissionMode: "plan", reviewBeforeCommit: true, sandbox: "strict", options: { subSessions: false } });
  assert.deepEqual(formOf(stored), { id: "a", name: "Automation a", enabled: false, prompt: "Do the thing.", script: "", scriptArguments: "", projectId: "p", storeInProject: true, provider: "codex", model: "gpt", effort: "Low",
    agent: "reviewer", catchUp: true, permissionMode: "plan", reviewBeforeCommit: true, sandbox: "strict", options: { subSessions: false }, runsOn: null, publishTo: null, triggers: [trigger("issue")], autonomy: null });
  assert.deepEqual(formOf(item("b")), { ...emptyForm(null), id: "b", name: "Automation b", prompt: "Do the thing." });
  // The host says the defaults too: the form holds them as nothing.
  assert.equal(formOf(item("c", { options: { subSessions: true } })).options, null);
  // What a form sends back is what it was read from.
  assert.deepEqual(inputOf(formOf(stored)), { id: "a", name: "Automation a", enabled: false, prompt: "Do the thing.", script: null, scriptArguments: null, projectId: "p", provider: "codex", model: "gpt", effort: "Low", agent: "reviewer",
    catchUp: true, permissionMode: "plan", reviewBeforeCommit: true, sandbox: "strict", options: { subSessions: false }, runsOn: null, publishTo: null, triggers: [trigger("issue")], autonomy: null });
});

test("the autonomy of an automation goes to the form and back, its tools trimmed", () => {
  const autonomy = { allowedTools: ["k8s_restart_pod"], maxCallsPerHour: 3, approvalTimeout: 30, onTimeout: "wait", approvers: "owner" };
  const form = formOf(item("a", { autonomy }));
  assert.deepEqual(form.autonomy, autonomy);
  assert.deepEqual(inputOf({ ...form, autonomy: { ...autonomy, allowedTools: [" k8s_restart_pod ", "  "] } }).autonomy, autonomy);
  // Turned on, it is read only: no tool allowed, approvals to the owner within an hour, then the run stops.
  assert.deepEqual(newAutonomy(), { allowedTools: [], maxCallsPerHour: null, approvalTimeout: 60, onTimeout: "stop", approvers: "owner" });
  const en = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate("en", key, parameters);
  assert.equal(describeAutonomy(newAutonomy(), en), "Read only: it asks you before anything else than reading.");
  assert.equal(describeAutonomy({ ...newAutonomy(), allowedTools: ["jira_comment", "slack_post"] }, en), "It calls jira_comment, slack_post by itself; it asks you for anything else.");
  assert.deepEqual(toolNames(" jira_comment,slack_post\n jira_comment ,, mcp__github__create_issue "), ["jira_comment", "slack_post", "mcp__github__create_issue"]);
  // A run that waits for a person is in progress, and says so in every language.
  assert.equal(runTone("waiting"), "running");
  for (const locale of locales) assert.notEqual(translate(locale, runStatusLabel("waiting")), translate(locale, "Unknown"), locale);
});

test("the Agent chip of an automation shows what it runs with, and what it chooses goes back to the form", () => {
  const form = formOf(item("a", { provider: "codex", model: "gpt", effort: "low", agent: "reviewer", permissionMode: "plan", reviewBeforeCommit: false, sandbox: "off",
    options: { subSessions: false } }));
  const settings = sessionSettingsOf(form);
  assert.deepEqual(settings, { agentPromptId: "reviewer", providerId: "codex", modelId: "gpt", reasoningEffort: "low", permissionMode: "plan", reviewBeforeCommit: false, sandbox: "off",
    options: { subSessions: false } });
  assert.deepEqual({ ...form, ...formSettings(settings) }, form, "the round trip changes nothing");
  // The defaults are the empty fields of the form.
  assert.deepEqual(sessionSettingsOf(emptyForm(null)), defaultSessionSettings);
  assert.deepEqual(formSettings(defaultSessionSettings), { agent: "", provider: "", model: "", effort: "", permissionMode: "", reviewBeforeCommit: null, sandbox: "", options: null });
});

test("the details say what the sessions run with besides the defaults, the permission mode and the sandbox included", () => {
  assert.deepEqual(runsWithFacts(item("a", { provider: "codex", model: "gpt", agent: "plan", permissionMode: "acceptEdits", reviewBeforeCommit: false, sandbox: "off" }), english),
    ["codex", "gpt", "agent plan", "Permissions: Accept edits", "Review before commit: Off", "Sandbox: Off"]);
  assert.deepEqual(runsWithFacts(item("b", { permissionMode: "custom", sandbox: "strict" }), english), ["Permissions: custom", "Sandbox: strict"]);
  assert.deepEqual(runsWithFacts(item("c", { options: { subSessions: false } }), english), ["Sub-sessions: Off"]);
  assert.deepEqual(runsWithFacts(item("d", { options: { subSessions: true } }), english), []);
  assert.deepEqual(runsWithFacts(item("c"), english), [], "the defaults say nothing");
  // Its provider as the machine it runs on names it, or as this computer does.
  assert.deepEqual(runsWithFacts(item("e", { provider: "codex", runsOn: "mac", publishTo: null }), english, "Codex (Mac mini)"), ["Codex (Mac mini)"]);
});

test("the list is narrowed by words and by where the automations run", () => {
  const items = [item("a", { name: "Issue triage", projectId: "p", projectName: "Alpha" }), item("b", { name: "Daily brief", prompt: "Summarize my projects." }),
    item("c", { name: "Lost", projectFolder: "/gone", problem: "not a project" })];
  const ids = (query: string, scope: "all" | "chats" | "projects") => filterAutomations(items, query, scope).map(value => value.id).join("");
  assert.equal(ids("", "all"), "abc");
  assert.equal(ids("", "chats"), "b");
  assert.equal(ids("", "projects"), "ac", "An automation whose project is missing is still one of a project.");
  assert.equal(ids("ALPHA", "all"), "a");
  assert.equal(ids("summarize projects", "all"), "b", "Every word is looked for, in the name, the project and the prompt.");
  assert.equal(ids("triage", "chats"), "");
  assert.equal(ids("  ", "all"), "abc");
});

test("the day to come is drawn from where each time falls; an automation that runs all along is one band", () => {
  const from = Date.parse("2026-10-06T12:00:00Z");
  const to = from + 24 * 60 * 60 * 1000;
  assert.equal(timelinePosition("2026-10-06T12:00:00Z", from, to), 0);
  assert.equal(timelinePosition("2026-10-07T00:00:00Z", from, to), 0.5);
  assert.equal(timelinePosition("2026-10-08T00:00:00Z", from, to), 1);
  assert.equal(timelinePosition("2026-10-05T00:00:00Z", from, to), 0);
  assert.equal(timelinePosition("not a date", from, to), 0);
  assert.equal(timelinePosition("2026-10-07T00:00:00Z", from, from), 0);

  const at = (hour: number) => new Date(from + hour * 60 * 60 * 1000).toISOString();
  const often = Array.from({ length: denseTimeline }, (_value, index) => ({ automationId: "often", at: at(index) }));
  const rows = timelineRows([{ automationId: "twice", at: at(1) }, ...often, { automationId: "twice", at: at(13) }]);
  assert.deepEqual(rows.map(row => [row.automationId, row.times.length, row.dense]), [["twice", 2, false], ["often", denseTimeline, true]]);
  assert.deepEqual(rows[0].times, [at(1), at(13)]);
  assert.equal(timelineRows(often.slice(1))[0].dense, false);
  assert.deepEqual(timelineRows([]), []);
});

test("a run opens its session only while the session exists", () => {
  const sessions = new Set(["s1"]);
  assert.equal(openableRun(run("r1", "s1"), sessions), true);
  assert.equal(openableRun(run("r2", "s2"), sessions), false);
  assert.equal(openableRun(run("r3", null), sessions), false);
});

test("a session started by an automation is told which one, and what started that run", () => {
  const items = [item("a", { name: "Nightly review", triggers: [trigger("daily", { at: ["23:00"] })] })];
  const runs = [{ ...run("r2", "s2"), trigger: "issue", detail: "#12 Crash" }, run("r1", "s1")];
  assert.deepEqual(sessionOrigin("s2", "a", items, runs, english), { id: "a", name: "Nightly review", summary: "Issue · #12 Crash" });
  assert.deepEqual(sessionOrigin("s1", "a", items, runs, english), { id: "a", name: "Nightly review", summary: "Manual" });
  // A run the page no longer lists: the automation is still named.
  assert.deepEqual(sessionOrigin("old", "a", items, runs, english), { id: "a", name: "Nightly review", summary: null });
  // An automation that was deleted: there is nothing to open.
  assert.deepEqual(sessionOrigin("s1", "gone", items, runs, english), { id: null, name: null, summary: null });
});

test("every template can be saved as it is, in a project or as a chat, and is named in every language", () => {
  assert.equal(new Set(automationTemplates.map(template => template.key)).size, automationTemplates.length);
  for (const template of automationTemplates) {
    const form: AutomationForm = { ...emptyForm(template.project ? "p" : null), name: english(template.name), prompt: template.prompt,
      script: template.script ?? "", scriptArguments: template.scriptArguments ?? "", triggers: template.triggers };
    assert.equal(formProblem(form), null, template.key);
    // A template runs a prompt, or a script of the library in place of one.
    assert.ok(template.script ? template.prompt === "" : template.prompt.length > 80 && template.prompt.length < 1000, template.key);
    // A template that watches a repository runs in a project.
    assert.ok(template.project || template.triggers.every(isSchedule), template.key);
    for (const locale of locales) assert.ok(translate(locale, template.name).length > 0 && translate(locale, template.description).length > 0, `${template.key} ${locale}`);
  }
  assert.ok(automationTemplates.some(template => !template.project), "One template needs no project.");
  assert.ok(automationTemplates.some(template => template.triggers.length === 0), "One template is run by hand.");
  const script = automationTemplates.find(template => template.script);
  assert.equal(script?.script, "review-pr", "One template runs a sample script on each pull request.");
  assert.deepEqual(inputOf({ ...emptyForm("p"), name: "Review", prompt: script!.prompt, script: script!.script!, triggers: script!.triggers }).script, "review-pr");
});
