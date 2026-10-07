import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { IssueSource, WorkspaceProject } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { issueFilters, issueIcon, issueReference, issueStateLabel, issueTone, kindLabel, labelColors, sameList, signInCommand, sourceKinds } from "./issues";
import { IssuesPanel, type IssuesApi } from "./IssuesPanel";

test("a pull request can be merged, an issue cannot: each kind has its own lists", () => {
  assert.deepEqual(issueFilters("issue"), ["open", "closed", "all"]);
  assert.deepEqual(issueFilters("pull_request"), ["open", "merged", "closed", "all"]);
});

test("a tracker shows the kinds it has, named as its users name them", () => {
  const source = (kinds: string[]): IssueSource => ({ service: "jira", name: "Jira", location: "ALTA", url: null, kinds });
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
  assert.deepEqual((["open", "draft", "merged", "closed"] as const).map(state => issueTone({ kind: "pull_request", state })), ["open", "draft", "done", "closed"]);
  assert.equal(issueTone({ kind: "issue", state: "closed" }), "done", "an issue that is closed is done; a pull request that is closed was not merged");
  assert.deepEqual(["open", "draft", "merged", "closed"].map(state => issueStateLabel({ state })), ["Open", "Draft", "Merged", "Closed"]);
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
  const key = { projectId: "p", service: "github", kind: "issue", filter: "open", query: "" } as const;
  assert.equal(sameList(key, { ...key }), true);
  assert.equal(sameList(key, { ...key, query: "crash" }), false);
  assert.equal(sameList(key, { ...key, kind: "pull_request" }), false);
  assert.equal(sameList(null, key), false);
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
