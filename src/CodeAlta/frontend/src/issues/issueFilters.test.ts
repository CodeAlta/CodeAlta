import assert from "node:assert/strict";
import test from "node:test";
import type { IssueFilterField } from "#neoastra";
import { choosePick, forgetPick, joinValues, movePicks, pickKey, readPicks, splitValues, filterValueText, listedFilters, sameList, trackerText, writePicks } from "./issues";
import { addableFilters, changeEntry, configuredText, configuredValue, fieldTypeLabel, entriesOf, formSelections, freeViewName, isActive, sameEntries, savedEntries, switchEntry, valueOf, valuesOf } from "./issueFilterSettings";

const team: IssueFilterField = { id: "team", label: "Team", values: [{ id: "t1", label: "Platform", detail: "Your team" }, { id: "t2", label: "Mobile", detail: null }], dependsOn: [], problem: null,
  trackerLabel: "Team", kind: "choice", fieldType: null, multiple: false, allLabel: null };
const sprint: IssueFilterField = { id: "sprint", label: "Sprint", values: [{ id: "@current", label: "Current sprint", detail: "S12" }, { id: "s1", label: "S1", detail: null }],
  dependsOn: ["team"], problem: null, trackerLabel: "Sprint", kind: "choice", fieldType: null, multiple: false, allLabel: null };

test("a listing is asked with the values that say something, as a text equal for equal choices", () => {
  const listed = listedFilters({ sprint: "@current", team: "t1", type: "" });
  assert.deepEqual(listed.values, { sprint: "@current", team: "t1" });
  assert.equal(listed.text, listedFilters({ team: "t1", sprint: "@current" }).text, "the order of the choices does not matter");
  const key = { projectId: "p", service: "azure_devops", kind: "issue", filter: "open", query: "", part: null } as const;
  assert.equal(sameList({ ...key, filters: listed.text }, { ...key, filters: listed.text }), true);
  assert.equal(sameList({ ...key, filters: listed.text }, { ...key, filters: "" }), false, "the list of a sprint is not the list of every sprint");
  assert.equal(sameList(key, { ...key, filters: "" }), true);
});

test("the words of filters are translated, the data of a tracker is not", () => {
  const t = (key: string) => `«${key}»`;
  assert.equal(trackerText("Current sprint", t), "«Current sprint»");
  assert.equal(trackerText("Task", t), "Task", "a type of work item is the name the tracker gives it");
  assert.equal(trackerText("Platform", t), "Platform");
  assert.equal(filterValueText({ label: "Current sprint", detail: "S12 · 2026-10-19 – 2026-10-30" }), "Current sprint (S12 · 2026-10-19 – 2026-10-30)");
  assert.equal(filterValueText({ label: "Bug", detail: null }), "Bug");
});

test("a view gives its filters as they are, and a new view of the user takes a free name", () => {
  assert.deepEqual(entriesOf({ filters: [{ id: "team", mode: "off", value: "Platform", label: null, pick: null },
    { id: "field:System.State", mode: "strange", value: "Ready", label: "State", pick: true }] }), [
    { id: "team", mode: "off", value: "Platform", label: null, pick: false },
    { id: "field:System.State", mode: "on", value: "Ready", label: "State", pick: true },
  ], "only off turns a filter off");
  assert.deepEqual(entriesOf(null), []);
  assert.equal(freeViewName([], "Mine"), "Mine");
  assert.equal(freeViewName([{ name: "Mine" }, { name: "Mine 2" }], "Mine"), "Mine 3");
  const off = { id: "a", mode: "off", value: null, label: null, pick: false } as const;
  assert.equal(sameEntries([off], [{ ...off }]), true);
  assert.equal(sameEntries([off], [{ ...off, pick: true }]), false, "whether the tab may change it is part of the view");
  assert.equal(sameEntries([off], []), false);
});

test("a filter is turned on or off in a view, offered in the tab, and the view names only its own", () => {
  const priority = "field:Microsoft.VSTS.Common.Priority";
  const entries = changeEntry([], priority, { mode: "on" });
  assert.deepEqual(entries, [{ id: priority, mode: "on", value: null, label: null, pick: false }]);
  assert.equal(isActive(entries, priority), true);
  assert.deepEqual(changeEntry([], "team", { label: "Squad" }), [{ id: "team", mode: "off", value: null, label: "Squad", pick: false }], "a name alone does not turn a filter on");
  assert.deepEqual(switchEntry([{ id: "team", mode: "on", value: "Platform", label: null, pick: true }], "team", false), [{ id: "team", mode: "off", value: "Platform", label: null, pick: true }],
    "off, a filter stays in the view with its value");
  assert.equal(isActive([{ id: "team", mode: "off", value: "Platform", label: null, pick: false }], "team"), false);
  assert.deepEqual(entriesOf({ filters: [{ id: "team", mode: null, value: "Platform", label: null, pick: null }] }), [{ id: "team", mode: "on", value: "Platform", label: null, pick: false }],
    "a filter a view names is on");
  assert.deepEqual(savedEntries([{ id: "sprint", mode: "on", value: "@current", label: null, pick: true }, { id: "team", mode: "on", value: null, label: null, pick: false }]), [
    { id: "sprint", mode: "on", value: "@current", label: null, pick: true },
    { id: "team", mode: "on", value: null, label: null, pick: null },
  ]);
  const field: IssueFilterField = { ...team, id: priority, values: [], fieldType: "number", kind: "text" };
  assert.deepEqual(addableFilters([team, sprint, field], [{ id: priority }, { id: "field:Custom.Market" }], [{ id: "team", mode: "on", value: null, label: null, pick: false },
    { id: "field:Custom.Market", mode: "on", value: null, label: null, pick: false }]), { own: ["sprint"], fields: [priority] },
    "the tracker's own filters and the fields the view does not name yet");
  assert.equal(fieldTypeLabel("person"), "Person");
  assert.equal(fieldTypeLabel(null), "Built in");
});

test("a value chosen in the tab is kept for its view, and one chosen for a team forgets the sprint of another", () => {
  const values = new Map<string, string>();
  const storage = { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => void values.set(key, value) };
  const key = pickKey("p1", "azure_devops", { name: "Mine", source: "personal" });
  writePicks(key, { sprint: "s1" }, storage);
  writePicks(pickKey("p1", "azure_devops", { name: "Board", source: "team" }), { sprint: "s9" }, storage);
  assert.deepEqual(readPicks(key, storage), { sprint: "s1" });
  const renamed = pickKey("p1", "azure_devops", { name: "Sprint", source: "personal" });
  movePicks(key, renamed, storage);
  assert.deepEqual([readPicks(key, storage), readPicks(renamed, storage)], [{}, { sprint: "s1" }], "a renamed view keeps the values chosen for it");
  writePicks(renamed, {}, storage);
  assert.deepEqual(readPicks(renamed, storage), {});
  values.set("codealta.desktop.issue-picks.v1", "{broken");
  assert.deepEqual(readPicks(key, storage), {}, "what cannot be read is no choice");
  assert.deepEqual(choosePick({ team: "t1", sprint: "s1", type: "Bug" }, [team, sprint, { id: "type", dependsOn: [] }], "team", "t2"), { team: "t2", type: "Bug" });
  assert.deepEqual(forgetPick({ team: "t2", sprint: "s-t2", type: "Bug" }, [team, sprint, { id: "type", dependsOn: [] }], "team"), { type: "Bug" },
    "↺ on the team forgets the sprint chosen for it too");
  assert.deepEqual(listedFilters({ sprint: "", team: "t1", type: "" }, ["sprint"]).values, { sprint: "", team: "t1" }, "a filter the tab may change is asked for every item");
});

test("a value is kept in the configuration by a name a person reads", () => {
  assert.equal(valueOf(team, "platform")?.id, "t1", "by its label, without case");
  assert.equal(valueOf(team, "t2")?.id, "t2", "by its id");
  assert.equal(valueOf(team, "gone"), null);
  assert.equal(configuredText(team.values[0]), "Platform");
  assert.equal(configuredText(sprint.values[0]), "@current", "a symbolic value by its id, which stays true as sprints pass");
  assert.deepEqual(formSelections([team, sprint], [{ id: "team", mode: "on", value: "Mobile", label: null, pick: false }, { id: "sprint", mode: "on", value: "unknown", label: null, pick: false }]), { team: "t2" });
});

test("a filter of several values keeps them joined, and the configuration keeps them by their names", () => {
  const state: IssueFilterField = { ...team, id: "field:System.State", label: "State", values: [{ id: "New", label: "New", detail: null }, { id: "Ready", label: "Ready", detail: null },
    { id: "On Hold", label: "On Hold", detail: null }], fieldType: "list", multiple: true };
  const chosen = joinValues(["New", "On Hold"]);
  assert.deepEqual(splitValues(chosen), ["New", "On Hold"]);
  assert.deepEqual(splitValues(""), []);
  assert.deepEqual(valuesOf(state, joinValues(["ready", "gone", "New"])).map(value => value.id), ["Ready", "New"], "by id or label, without case; what is gone is left out");
  assert.equal(configuredValue(team, joinValues(["t1", "t2"])), joinValues(["Platform", "Mobile"]));
  assert.equal(configuredValue(team, ""), "");
  assert.deepEqual(formSelections([{ ...state, multiple: true }], [{ id: "field:System.State", mode: "on", value: "New", label: null, pick: false }]), {}, "only a filter of one value is asked with");
});
