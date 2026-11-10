import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { MachineView } from "#neoastra";
import { locales, translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { emptyForm, formProblem, inputOf, newTrigger, poolTags, poolTakes, type AutomationForm } from "./automations";
import type { AutomationsHub } from "./automationsHub";
import { poolChoice, RunsOnField } from "./RunsOnField";

const never = () => assert.fail("rendering must not act");
const hub = { poolMachines: never } as unknown as AutomationsHub;
const machine = { id: "dgx", name: "dgx-spark", state: "online" } as unknown as MachineView;

function field(form: AutomationForm, reachable: readonly MachineView[] = [], locale: typeof locales[number] = "en") {
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(RunsOnField, { hub, form, reachable, disabled: false, edit: never })));
}

const named = (form: Partial<AutomationForm>): AutomationForm => ({ ...emptyForm(null), name: "Nightly", prompt: "Review.", ...form });

test("Runs on is always shown: alone with Add a machine… while no machine is reachable", () => {
  const html = field(named({}));
  assert.match(html, /<select id="automation-machine"/);
  assert.match(html, /<option value="" selected="">This computer<\/option>/);
  assert.match(html, /Add a machine…/);
  assert.doesNotMatch(html, /Machines with tags…/, "a pool needs machines");
  for (const locale of locales) assert.ok(field(named({}), [], locale).includes(translate(locale, "Add a machine…")), locale);
});

test("an automation kept in its project shows Runs on disabled, and why", () => {
  const html = field(named({ projectId: "p", storeInProject: true }), [machine]);
  assert.match(html, /<select disabled="" id="automation-machine"/);
  assert.match(html, /An automation of the project runs on the machines that have the project\./);
  assert.doesNotMatch(html, /Add a machine…/);
});

test("Machines with tags… shows the tags, and the triggers a pool does not take", () => {
  const offered = field(named({}), [machine]);
  assert.match(offered, /<option value="dgx">dgx-spark<\/option>/);
  assert.match(offered, new RegExp(`<option value="${poolChoice}">Machines with tags…</option>`));
  const pooled = field(named({ runsOnTags: ["gpu", "linux"], triggers: [newTrigger("daily"), newTrigger("slack"), newTrigger("command")] }), [machine]);
  assert.match(pooled, new RegExp(`<option value="${poolChoice}" selected="">Machines with tags…</option>`));
  assert.match(pooled, /id="automation-pool-tags"/);
  assert.match(pooled, />gpu</);
  assert.match(pooled, />linux</);
  assert.match(pooled, /The machines that have all these tags run it: each run goes to one of them\./);
  assert.match(pooled, /Machines with tags do not take these triggers: Slack, Command\./);
  for (const locale of locales)
    assert.ok(field(named({ runsOnTags: ["gpu"] }), [machine], locale).includes(translate(locale, "Machines with tags…")), locale);
});

test("a pool is saved with its tags and the runs it lets work at once, and refuses what it does not take", () => {
  assert.deepEqual(["daily", "cron", "issue", "pull_request", "jira", "jira_query"].map(poolTakes), [true, true, true, true, true, true]);
  assert.deepEqual(["command", "slack", "webhook", "incident_io", "project"].map(poolTakes), [false, false, false, false, false]);
  assert.equal(formProblem(named({ runsOnTags: [] })), "Choose the tags of the machines it runs on.");
  assert.equal(formProblem(named({ runsOnTags: ["gpu"], triggers: [newTrigger("slack")] })),
    "Machines with tags take schedules, cron, issues, pull requests and Jira: remove the other triggers, or run it on one machine.");
  assert.equal(formProblem(named({ runsOnTags: ["gpu"], triggers: [newTrigger("hourly")] })), null);
  const input = inputOf(named({ runsOn: "dgx", runsOnTags: ["gpu"], maxRuns: 3 }));
  assert.deepEqual([input.runsOn, input.runsOnTags, input.maxRuns], [null, ["gpu"], 3], "the tags take the place of a machine");
  assert.deepEqual([inputOf(named({ maxRuns: 1 })).maxRuns, inputOf(named({ runsOnTags: ["gpu"], projectId: "p", storeInProject: true })).runsOnTags], [null, null]);
});

test("the tags typed are kept once each whatever their case, as the machines compare them", () => {
  assert.deepEqual(poolTags(["gpu", " GPU ", "linux", "two words", "Linux", "os:linux"]), ["gpu", "linux", "os:linux"]);
  assert.equal(poolTags(Array.from({ length: 12 }, (_, index) => `t${index}`)).length, 8);
});
