import assert from "node:assert/strict";
import test from "node:test";
import type { AutomationItem, AutomationRunItem, AutomationTriggerItem } from "#neoastra";
import { locales, translate, type Locale, type MessageKey } from "../localization";
import { automationTemplates, dayName, denseTimeline, describeTrigger, describeTriggers, emptyForm, filterAutomations, formOf, formProblem, inputOf, isSchedule, maximumNameLength,
  maximumPromptLength, needsProject, newTrigger, openableRun, runStatusLabel, runTone, runTriggerLabel, sessionOrigin, timelinePosition, timelineRows, triggerIcon, triggerLabel, triggerTone, triggerTypes,
  type AutomationForm } from "./automations";

const english = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate("en", key, parameters);
const trigger = (type: string, change: Partial<AutomationTriggerItem> = {}): AutomationTriggerItem =>
  ({ type, minute: 0, every: 1, at: [], days: [], expression: null, event: "opened", authors: "trusted", command: null, folder: null, ...change });
const item = (id: string, change: Partial<AutomationItem> = {}): AutomationItem => ({
  id, name: `Automation ${id}`, enabled: true, prompt: "Do the thing.", projectId: null, projectName: null, projectFolder: null, storeProjectId: null, file: "/home/config.toml",
  provider: null, model: null, effort: null, agent: null, catchUp: false, triggers: [], problem: null, nextRunAt: null, running: false, lastRun: null, repository: null, watchProblem: null, allowed: true, ...change,
});
const run = (id: string, sessionId: string | null): AutomationRunItem =>
  ({ id, automationId: "a", name: "A", sessionId, projectId: null, startedAt: "2026-10-06T12:00:00Z", endedAt: null, trigger: "manual", detail: null, status: "running", message: null });

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
  assert.deepEqual(triggerTypes.map(type => english(triggerLabel(type))), ["Hourly", "Daily", "Weekly", "Cron", "Issue", "Pull request", "Jira issue", "Command"]);
  assert.equal(new Set(triggerTypes.map(triggerIcon)).size, triggerTypes.length);
  assert.equal(new Set(triggerTypes.map(triggerTone)).size, triggerTypes.length);
  assert.deepEqual([triggerIcon(undefined), triggerTone(undefined), english(triggerLabel("unknown"))], ["hand", "muted", "Manual"]);
  assert.deepEqual(triggerTypes.filter(type => !isSchedule({ type })), ["issue", "pull_request", "jira", "command"]);
  // A command watches no repository: it runs in a chat too.
  assert.deepEqual(triggerTypes.filter(type => needsProject({ type })), ["issue", "pull_request", "jira"]);
  assert.deepEqual([runTone("running"), runTone("completed"), runTone("failed"), runTone("skipped"), runTone("interrupted")], ["running", "ok", "failed", "muted", "muted"]);
  assert.deepEqual(["running", "completed", "failed", "cancelled", "interrupted", "skipped", "later"].map(status => english(runStatusLabel(status))),
    ["Running", "Completed", "Failed", "Cancelled", "Interrupted", "Skipped", "Unknown"]);
  assert.deepEqual(["manual", "daily", "issue", "pull_request", "command"].map(name => english(runTriggerLabel(name))), ["Manual", "Daily", "Issue", "Pull request", "Command"]);
});

test("a new trigger starts with values that can be saved; a command waits to be written", () => {
  for (const type of triggerTypes) {
    const form: AutomationForm = { ...emptyForm("p"), name: "n", prompt: "p", triggers: [newTrigger(type)] };
    assert.equal(formProblem(form), type === "command" ? "Write the command of the trigger." : null, type);
  }
  assert.deepEqual(newTrigger("weekly"), { type: "weekly", minute: 0, every: 1, at: ["09:00"], days: ["mon"], expression: null, event: "opened", authors: "trusted", command: null, folder: null });
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
    storeInProject: true, triggers: [trigger("daily", { at: ["17:30", "09:00"], expression: "left over" }), trigger("cron", { expression: " 0 9 * * * " })] };
  assert.deepEqual(inputOf(form), { id: "id", name: "Nightly", enabled: true, prompt: "Review.\n", projectId: "p", provider: "codex", model: "gpt", effort: "High", agent: "reviewer", catchUp: true,
    triggers: [trigger("daily", { at: ["09:00", "17:30"] }), trigger("cron", { expression: "0 9 * * *" })] });
  assert.deepEqual([inputOf({ ...form, provider: "" }).model, inputOf({ ...form, provider: "" }).effort, inputOf({ ...form, model: "" }).effort, inputOf({ ...form, agent: " " }).agent], [null, null, null, null]);
  assert.equal(inputOf({ ...form, triggers: [] }).catchUp, false, "An automation that is run by hand misses nothing.");
  assert.equal(inputOf({ ...form, triggers: [trigger("issue")] }).catchUp, true);
  // A command is sent trimmed, with its folder when it names one; another kind sends neither.
  assert.deepEqual(inputOf({ ...form, triggers: [trigger("command", { command: " wait-for-it --once ", folder: " tools " }), trigger("command", { command: "other", folder: "  " }),
    trigger("daily", { at: ["09:00"], command: "left over", folder: "left over" })] }).triggers.map(value => [value.command, value.folder]),
    [["wait-for-it --once", "tools"], ["other", null], [null, null]]);
});

test("the form of an automation holds what it is, and where it is written", () => {
  const stored = item("a", { projectId: "p", storeProjectId: "p", provider: "codex", model: "gpt", effort: "Low", agent: "reviewer", catchUp: true, triggers: [trigger("issue")], enabled: false });
  assert.deepEqual(formOf(stored), { id: "a", name: "Automation a", enabled: false, prompt: "Do the thing.", projectId: "p", storeInProject: true, provider: "codex", model: "gpt", effort: "Low",
    agent: "reviewer", catchUp: true, triggers: [trigger("issue")] });
  assert.deepEqual(formOf(item("b")), { ...emptyForm(null), id: "b", name: "Automation b", prompt: "Do the thing." });
  // What a form sends back is what it was read from.
  assert.deepEqual(inputOf(formOf(stored)), { id: "a", name: "Automation a", enabled: false, prompt: "Do the thing.", projectId: "p", provider: "codex", model: "gpt", effort: "Low", agent: "reviewer",
    catchUp: true, triggers: [trigger("issue")] });
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
    const form: AutomationForm = { ...emptyForm(template.project ? "p" : null), name: english(template.name), prompt: template.prompt, triggers: template.triggers };
    assert.equal(formProblem(form), null, template.key);
    assert.ok(template.prompt.length > 80 && template.prompt.length < 1000, template.key);
    // A template that watches a repository runs in a project.
    assert.ok(template.project || template.triggers.every(isSchedule), template.key);
    for (const locale of locales) assert.ok(translate(locale, template.name).length > 0 && translate(locale, template.description).length > 0, `${template.key} ${locale}`);
  }
  assert.ok(automationTemplates.some(template => !template.project), "One template needs no project.");
  assert.ok(automationTemplates.some(template => template.triggers.length === 0), "One template is run by hand.");
});
