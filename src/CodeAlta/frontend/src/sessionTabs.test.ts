import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { closeSessionTab, emptySessionTabs, openSessionTab, persistSessionTabs, reconcileSessionTabs, resolveSessionTab, restoreSessionTabs, selectedTab, sessionTabLimit } from "./sessionTabs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { SessionTabLabel } from "./SessionTabStrip";
import { ShellLanguageContext } from "./shellLanguage";
import { locales, translate } from "./localization";

const tab = (id: string, projectId: string | null = "p") => ({ sessionId: id, projectId, path: projectId === null ? null : "/p" });
const catalog: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  projects: [{ id: "p", name: "Project", path: "/p", archived: false }], sessions: [
  { id: "one", workspacePath: "/p", scopeKind: "project", projectId: "p" },
  { id: "two", workspacePath: "/p", scopeKind: "project", projectId: "p" },
  { id: "global", workspacePath: null, scopeKind: "global", projectId: null },
].map(row => ({ ...row, messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, title: row.id, fullTitle: row.id, fullTitleTruncated: false, createdAt: null,
  updatedAt: "2026-09-26T00:00:00Z", parentSessionId: null, lineageIssue: null, providerKey: null })) };

test("six-locale Flex tab labels keep English-like user titles and identities literal without dispatch", () => {
  const snapshot = { ...catalog, sessions: catalog.sessions.map(row => ({ ...row, title: "Prompt draft <literal>" })) };
  const state = openSessionTab(emptySessionTabs(), tab("one"));
  const forbidden = () => { assert.fail("rendering localized tabs must not dispatch"); };
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
      { value: { locale, choice: locale, setLanguage: forbidden } }, createElement(SessionTabLabel,
        { label: `${snapshot.sessions[0].title} - Project`, path: state.open[0].path, dirty: true })));
    const diagnostic = `locale=${locale}; html=${html}`;
    assert.ok(html.includes("Prompt draft &lt;literal&gt; - Project"), diagnostic);
    assert.ok(html.includes(translate(locale, "Draft edited in this window")), diagnostic);
    assert.ok(html.includes('/p'), diagnostic);
    assert.equal(state.open[0].sessionId, "one");
  }
});

test("tabs open uniquely, close inactive without changing selection, close active to first remaining, close last and reopen", () => {
  let state = openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"));
  assert.equal(openSessionTab(state, tab("two")), state);
  const inactive = closeSessionTab(state, tab("one"));
  assert.deepEqual(inactive.active, tab("two"));
  state = closeSessionTab(state, tab("two"));
  assert.deepEqual(state.active, tab("one"));
  state = closeSessionTab(state, tab("one"));
  assert.equal(state.active, null); assert.equal(state.open.length, 0);
  state = openSessionTab(state, state.closed.at(-1)!);
  assert.deepEqual(state.active, tab("one")); assert.equal(state.closed.length, 1);
});

test("catalog validation refuses missing, duplicate, path/scope changed identities; archives remain read-only navigation", () => {
  assert.ok(resolveSessionTab(catalog, tab("one")));
  assert.ok(resolveSessionTab(catalog, tab("global", null)));
  assert.equal(resolveSessionTab({ ...catalog, sessions: [{ ...catalog.sessions[2], projectId: "p" }] }, tab("global", null)), undefined);
  assert.equal(resolveSessionTab(catalog, tab("one", null)), undefined);
  assert.equal(resolveSessionTab(catalog, { ...tab("one"), path: "/changed" }), undefined);
  assert.equal(selectedTab(catalog, "p", "missing"), null);
  assert.equal(resolveSessionTab({ ...catalog, sessions: [...catalog.sessions, catalog.sessions[0]] }, tab("one")), undefined);
  assert.equal(resolveSessionTab({ ...catalog, projects: [...catalog.projects, catalog.projects[0]] }, tab("one")), undefined);
  assert.ok(resolveSessionTab({ ...catalog, projects: [{ ...catalog.projects[0], archived: true }] }, tab("one")));
  const state = openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"));
  assert.deepEqual(reconcileSessionTabs(state, { ...catalog, sessions: [catalog.sessions[0]] }).active, tab("one"));
  assert.equal(reconcileSessionTabs(state, { ...catalog, sessions: [] }).active, null);
});

test("versioned bounded persistence validates all fields, active membership, duplicates and storage failures", () => {
  const state = openSessionTab(emptySessionTabs(), tab("one"));
  let saved = "";
  assert.equal(persistSessionTabs(value => { saved = value; }, state), true);
  assert.deepEqual(restoreSessionTabs(() => saved), state);
  assert.deepEqual(restoreSessionTabs(() => JSON.stringify({ version: 1, open: [], active: null })), emptySessionTabs());
  for (const raw of ["bad", "null", "x".repeat(65537), JSON.stringify({ version: 2, open: [], active: null }),
    JSON.stringify({ version: 1, open: [tab("one"), tab("one")], active: tab("one") }),
    JSON.stringify({ version: 1, open: [tab("one")], active: tab("two") }),
    JSON.stringify({ version: 1, open: [{ ...tab("one"), path: 123 }], active: null })])
    assert.equal(restoreSessionTabs(() => raw), null);
  assert.equal(restoreSessionTabs(() => { throw Error("denied"); }), null);
  assert.equal(persistSessionTabs(() => { throw Error("denied"); }, state), false);
  let many = emptySessionTabs();
  for (let i = 0; i < 80; i++) many = openSessionTab(many, tab(String(i)));
  assert.equal(many.open.length, sessionTabLimit); assert.equal(many.active?.sessionId, "79");
  for (const item of [...many.open]) many = closeSessionTab(many, item);
  assert.equal(many.closed.length, sessionTabLimit);
});
