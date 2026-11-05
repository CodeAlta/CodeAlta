import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { IssueRow, IssueSource, WorkspaceProject } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { allPartsLabel, issueFilters, issueIcon, issueReference, issueStateLabel, issueTone, kindLabel, labelColors, sameList, shownPart, signInCommand, sourceKinds, voteIcon, voteLabel } from "./issues";
import { AssigneeNames, IssuesPanel, StateTag, type IssuesApi } from "./IssuesPanel";

test("a pull request can be merged, an issue cannot: each kind has its own lists", () => {
  assert.deepEqual(issueFilters("issue"), ["open", "closed", "all"]);
  assert.deepEqual(issueFilters("pull_request"), ["open", "merged", "closed", "all"]);
});

test("a tracker shows the kinds it has, named as its users name them", () => {
  const source = (kinds: string[]): IssueSource => ({ service: "jira", name: "Jira", location: "ALTA", url: null, kinds, parts: [] });
  assert.deepEqual(sourceKinds(source(["pull_request", "issue", "epic"])), ["issue", "pull_request"]);
  assert.deepEqual(sourceKinds(source(["issue"])), ["issue"]);
  assert.deepEqual(sourceKinds(null), []);
  assert.equal(kindLabel("pull_request", "gitlab", true), "Merge requests");
  assert.equal(kindLabel("pull_request", "github", false), "Pull request");
  assert.equal(kindLabel("issue", "azure_devops", true), "Work items");
  assert.equal(kindLabel("issue", "jira", true), "Issues");
});

test("an item is named by its number or by its key, and drawn by what it is and where it stands", () => {
  assert.equal(issueReference({ id: "128" }), "#128");
  assert.equal(issueReference({ id: "ALTA-12" }), "ALTA-12");
  assert.equal(issueIcon({ kind: "issue", state: "open" }), "issueOpen");
  assert.equal(issueIcon({ kind: "issue", state: "closed" }), "issueClosed");
  assert.equal(issueIcon({ kind: "pull_request", state: "merged" }), "pullRequest");
  assert.deepEqual(["Bug", "User Story", "Product Backlog Item", "Task", "Feature", "Epic", "incident"].map(type => issueIcon({ kind: "issue", state: "open", type })),
    ["issueBug", "issueStory", "issueStory", "issueTask", "issueFeature", "issueEpic", "issueBug"], "a work item is drawn by its type, its state by its color");
  assert.equal(issueIcon({ kind: "issue", state: "closed", type: "Test Case" }), "issueClosed", "a type without a picture of its own is drawn by its state");
  assert.deepEqual((["open", "draft", "merged", "closed"] as const).map(state => issueTone({ kind: "pull_request", state })), ["open", "draft", "done", "closed"]);
  assert.equal(issueTone({ kind: "issue", state: "closed" }), "done", "an issue that is closed is done; a pull request that is closed was not merged");
  assert.deepEqual(["open", "draft", "merged", "closed"].map(state => issueStateLabel({ state })), ["Open", "Draft", "Merged", "Closed"]);
});

test("the people an issue is assigned to are named in its row, and the tooltip says they are its assignees", () => {
  const render = (names: string[], locale: "en" | "de") => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: () => undefined } },
    createElement(AssigneeNames, { names })));
  const one = render(["Ana Lopez"], "en");
  assert.match(one, /title="Assigned to Ana Lopez"/);
  assert.match(one, />Ana Lopez</);
  const three = render(["Ana", "Bo", "Cy"], "de");
  assert.match(three, /title="Zugewiesen an Ana, Bo, Cy"/, "the tooltip names them all");
  assert.match(three, />Ana, Bo<\/span><span>\+1</, "the row names two and counts the others");
  assert.equal(render([], "en"), "", "an issue assigned to nobody shows nothing");
});

test("a state is written in the color its tracker gives it, and otherwise in the color of its tone", () => {
  const row = { kind: "issue", state: "open", stateText: "Ready" } as IssueRow;
  const render = (stateColor: string | null) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: () => undefined } },
    createElement(StateTag, { item: { ...row, stateColor } })));
  const colored = render("86cdde");
  assert.match(colored, /--issue-state-color:#86cdde/);
  assert.match(colored, /data-colored="true"/);
  assert.doesNotMatch(colored, /background/, "the tag tints its background from the color of its text");
  for (const color of [null, "red", "86cdde;x"]) assert.doesNotMatch(render(color), /--issue-state-color|data-colored/, `${color} is not a color of a tracker`);
});

test("a label keeps its color with a text that can be read on it", () => {
  assert.deepEqual(labelColors("D73A4A"), { background: "#D73A4A", color: "#ffffff" });
  assert.deepEqual(labelColors("fef2c0"), { background: "#fef2c0", color: "#1b1f24" });
  assert.equal(labelColors("red"), null);
  assert.equal(labelColors(null), null);
});

test("signing in is a command for the services that have one", () => {
  assert.equal(signInCommand("github"), "gh auth login");
  assert.equal(signInCommand("gitlab"), "glab auth login");
  assert.equal(signInCommand("azure_devops"), "az login");
  assert.equal(signInCommand("bitbucket"), null);
});

test("an answer belongs to the list that asked for it", () => {
  const key = { projectId: "p", service: "github", kind: "issue", filter: "open", query: "", part: null } as const;
  assert.equal(sameList(key, { ...key }), true);
  assert.equal(sameList(key, { ...key, query: "crash" }), false);
  assert.equal(sameList(key, { ...key, part: "PLAT" }), false, "the list of one Jira project is not the list of all of them");
  assert.equal(sameList(key, { ...key, kind: "pull_request" }), false);
  assert.equal(sameList(null, key), false);
});

test("one part of a tracker is listed only while the tracker has it", () => {
  const jira = { parts: ["ALTA", "PLAT"] };
  assert.equal(shownPart(jira, "PLAT"), "PLAT");
  assert.equal(shownPart(jira, null), null);
  assert.equal(shownPart(jira, "GONE"), null, "a project that left the settings is not asked");
  assert.equal(shownPart({ parts: [] }, "PLAT"), null, "a choice made for Jira says nothing of GitHub");
  assert.equal(shownPart(null, "PLAT"), null);
  assert.equal(allPartsLabel("jira"), "All projects");
  assert.equal(allPartsLabel("another"), "All");
});

test("the tab says what it waits for, and that a window without a host has no issues", () => {
  const never = () => assert.fail("rendering must not ask the host");
  const api = { sources: never, list: never, read: never, start: never, openLink: never } as unknown as IssuesApi;
  const project = { id: "p", name: "Alpha", path: "C:\\code\\alpha", archived: false } as WorkspaceProject;
  const render = (epoch: string | null) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(IssuesPanel, { api, epoch, projects: [project], projectId: "p", visible: false, preferredStart: "worktree", onActivate: never, onOpenSession: never, onNotice: never })));
  assert.ok(render(null).includes("Issues are unavailable in this window."));
  const html = render("epoch");
  assert.match(html, /<h2>Issues<\/h2>/);
  assert.match(html, /<option value="p" selected="">Alpha<\/option>/);
  assert.ok(html.includes("activity-spinner") && !html.includes("No tracker for this project"), "nothing is said to be missing before the host answered");
});

test("a window without projects shows an empty state instead of waiting for sources", () => {
  const never = () => assert.fail("rendering must not ask the host");
  const api = { sources: never, list: never, read: never, start: never, openLink: never } as unknown as IssuesApi;
  const render = (projectId: string | null, visible: boolean, epoch: string | null) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
    { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(IssuesPanel, { api, epoch, projects: [], projectId, visible, preferredStart: "worktree", onActivate: never, onOpenSession: never, onNotice: never })));
  for (const projectId of [null, "removed"]) {
    for (const visible of [true, false]) {
      const html = render(projectId, visible, "epoch");
      assert.ok(html.includes("No projects in this snapshot."));
      assert.ok(!html.includes("activity-spinner"), "there is no project whose sources can load");
      assert.ok(!html.includes("No tracker for this project"), "a missing project is not a project without a tracker");
      assert.ok(render(projectId, visible, null).includes("Issues are unavailable in this window."));
    }
  }
});

test("a vote is said and drawn as the reviewer gave it", () => {
  assert.deepEqual(["none", "approved", "suggestions", "waiting", "rejected", "other"].map(vote => [voteLabel(vote), voteIcon(vote)]),
    [["No vote yet", "gap"], ["Approved", "check"], ["Approved with suggestions", "check"], ["Waiting for author", "later"], ["Rejected", "close"], ["No vote yet", "gap"]]);
});
