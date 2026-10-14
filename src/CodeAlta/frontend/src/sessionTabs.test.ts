import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { backgroundSessionTabs, closeDraftTab, closePreviewTab, closeSessionTab, draftTabLimit, draftTabOf, emptySessionTabs, hasDraftTab, isPreviewTab, openDraftTab, openSessionTab, persistSessionTabs, pinSessionTab, reconcileSessionTabs, resolveDraftTab, resolveSessionTab, restoreSessionTabs, selectedTab, sessionPaneKey, sessionTabLimit, singleSessionTab } from "./sessionTabs";
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
].map(row => ({ ...row, messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, spaceId: null, title: row.id, fullTitle: row.id, fullTitleTruncated: false, createdAt: null,
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

test("the pane of a session is another one after a rewind, another host or another session", () => {
  const key = sessionPaneKey("epoch", tab("one"), "2026-10-12T10:00:00Z", 0);
  assert.equal(sessionPaneKey("epoch", tab("one"), "2026-10-12T10:00:00Z", 0), key);
  // A rewind writes the journal again: what the pane read of it, and where it was scrolled, is not kept.
  assert.notEqual(sessionPaneKey("epoch", tab("one"), "2026-10-12T10:00:00Z", 1), key);
  assert.notEqual(sessionPaneKey("epoch", tab("one"), "2026-10-12T10:00:00Z", 2), sessionPaneKey("epoch", tab("one"), "2026-10-12T10:00:00Z", 1));
  assert.notEqual(sessionPaneKey("other", tab("one"), "2026-10-12T10:00:00Z", 0), key);
  assert.notEqual(sessionPaneKey("epoch", tab("two"), "2026-10-12T10:00:00Z", 0), key);
  assert.notEqual(sessionPaneKey("epoch", tab("one", null), "2026-10-12T10:00:00Z", 0), key);
  assert.notEqual(sessionPaneKey("epoch", tab("one"), null, 0), key);
  assert.equal(sessionPaneKey(undefined, tab("one"), null, 0), sessionPaneKey(null, tab("one"), null, 0));
});

test("a session that is looked at takes the transient tab, in the place of the one that was there", () => {
  const first = openSessionTab(emptySessionTabs(), tab("one"), "preview");
  assert.deepEqual(first, { open: [tab("one")], active: tab("one"), closed: [], preview: tab("one") });
  assert.ok(isPreviewTab(first, tab("one")) && !isPreviewTab(first, tab("two")) && !isPreviewTab(emptySessionTabs(), tab("one")));

  // A kept session beside it; the next session looked at replaces the transient one where it was, and leaves no closed tab.
  const beside = openSessionTab(openSessionTab(emptySessionTabs(), tab("one"), "preview"), tab("global", null));
  assert.deepEqual(beside.open, [tab("one"), tab("global", null)]);
  assert.ok(isPreviewTab(beside, tab("one")) && !isPreviewTab(beside, tab("global", null)));
  const next = openSessionTab(beside, tab("two"), "preview");
  assert.deepEqual(next, { open: [tab("two"), tab("global", null)], active: tab("two"), closed: [], preview: tab("two") });

  // Looking at a session that has a tab shows it and changes nothing else: a kept tab is not made transient again.
  const shown = openSessionTab(next, tab("global", null), "preview");
  assert.deepEqual(shown, { ...next, active: tab("global", null) });
  assert.equal(openSessionTab(shown, tab("global", null), "preview"), shown);
  // With no transient tab, the session looked at is added as the transient one.
  const none = openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"), "preview");
  assert.deepEqual(none.open, [tab("one"), tab("two")]);
  assert.ok(isPreviewTab(none, tab("two")));
});

test("the transient tab is kept by opening its session to keep it, or by pinning it; closing it leaves none", () => {
  const preview = openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"), "preview");
  const kept = openSessionTab(preview, tab("two"));
  assert.deepEqual(kept, { open: [tab("one"), tab("two")], active: tab("two"), closed: [], preview: null });
  assert.deepEqual(pinSessionTab(preview, tab("two")), { ...preview, preview: null });
  assert.equal(pinSessionTab(preview, tab("one")), preview, "a tab that is not the transient one is as it was");
  assert.equal(pinSessionTab(kept, tab("two")), kept);
  // A session opened to be kept while another is looked at: the transient tab stays.
  const both = openSessionTab(preview, tab("global", null));
  assert.ok(isPreviewTab(both, tab("two")) && both.open.length === 3);

  const closed = closeSessionTab(preview, tab("two"));
  assert.deepEqual(closed, { open: [tab("one")], active: tab("one"), closed: [tab("two")], preview: null });
  assert.ok(isPreviewTab(closeSessionTab(preview, tab("one")), tab("two")), "closing another tab leaves the transient one");

  // What takes the place of the transient tab takes its session away without listing it as closed.
  assert.deepEqual(closePreviewTab(preview), { open: [tab("one")], active: null, closed: [], preview: null });
  assert.deepEqual(closePreviewTab({ ...preview, active: tab("one") }), { open: [tab("one")], active: tab("one"), closed: [], preview: null });
  assert.equal(closePreviewTab(kept).open.length, 2);
  const plain = openSessionTab(emptySessionTabs(), tab("one"));
  assert.equal(closePreviewTab(plain), plain);
});

test("the oldest inactive tab that leaves a full strip can be the transient one", () => {
  let state = openSessionTab(emptySessionTabs(), tab("one"), "preview");
  for (let index = 0; index < sessionTabLimit; index++) state = openSessionTab(state, tab(`s${index}`));
  assert.equal(state.open.length, sessionTabLimit);
  assert.equal(state.open.some(value => value.sessionId === "one"), false);
  assert.equal(state.preview, null);
});

test("the new-session prompt of a scope can have a tab of its own, as many scopes can", () => {
  const project = draftTabOf(catalog, "p"), chats = draftTabOf(catalog, null);
  assert.deepEqual([project, chats], [{ projectId: "p", path: "/p" }, { projectId: null, path: null }]);
  assert.equal(draftTabOf(catalog, "missing"), null);
  assert.equal(draftTabOf({ ...catalog, projects: [...catalog.projects, catalog.projects[0]] }, "p"), null, "a project listed twice names no scope");
  assert.ok(resolveDraftTab(catalog, project!) && resolveDraftTab(catalog, chats!));
  assert.equal(resolveDraftTab(catalog, { projectId: "p", path: "/moved" }), false);

  const state = openSessionTab(emptySessionTabs(), tab("one"));
  assert.equal(hasDraftTab(state, project), false);
  const one = openDraftTab(state, project!);
  assert.deepEqual(one.drafts, [project]);
  assert.equal(openDraftTab(one, { projectId: "p", path: "/p" }), one, "a scope has one tab");
  const two = openDraftTab(one, chats!);
  assert.deepEqual(two.drafts, [project, chats]);
  assert.ok(hasDraftTab(two, project) && hasDraftTab(two, chats) && !hasDraftTab(two, null));
  assert.deepEqual(two.open, state.open, "the tabs of sessions are as they were");
  assert.deepEqual(closeDraftTab(two, project!).drafts, [chats]);
  assert.equal(closeDraftTab(one, chats!), one);
  // More scopes than tabs are kept for: the oldest leaves.
  let many = emptySessionTabs();
  for (let index = 0; index <= draftTabLimit; index++) many = openDraftTab(many, { projectId: `p${index}`, path: `/p${index}` });
  assert.equal(many.drafts!.length, draftTabLimit);
  assert.equal(many.drafts![0].projectId, "p1");
});

test("the transient tab and the tabs of prompts follow the catalog, and are kept for the next start", () => {
  const state = openDraftTab(openDraftTab(openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"), "preview"),
    { projectId: "p", path: "/p" }), { projectId: null, path: null });
  assert.equal(reconcileSessionTabs(state, catalog), state);
  // The session of the transient tab is gone, and so is the project of a prompt: neither keeps a tab.
  const without = { ...catalog, projects: [], sessions: catalog.sessions.filter(row => row.id === "global") };
  assert.deepEqual(reconcileSessionTabs(state, without), { open: [], active: null, closed: [], preview: null, drafts: [{ projectId: null, path: null }] });
  const plain = openSessionTab(emptySessionTabs(), tab("one"));
  assert.deepEqual(Object.keys(reconcileSessionTabs({ ...plain, open: [...plain.open, tab("gone")] }, catalog)), ["open", "active", "closed"], "a state without them keeps its shape");

  let stored = "";
  assert.ok(persistSessionTabs(value => { stored = value; }, state));
  assert.deepEqual(JSON.parse(stored), { version: 1, open: [tab("one"), tab("two")], active: tab("two"), preview: tab("two"),
    drafts: [{ projectId: "p", path: "/p" }, { projectId: null, path: null }] });
  assert.deepEqual(restoreSessionTabs(() => stored), { open: state.open, active: state.active, closed: [], preview: tab("two"), drafts: state.drafts });
  // What was stored before there was a transient tab is restored as it was, and a state without one is stored as before.
  persistSessionTabs(value => { stored = value; }, plain);
  assert.deepEqual(JSON.parse(stored), { version: 1, open: [tab("one")], active: tab("one") });
  assert.deepEqual(restoreSessionTabs(() => stored), { open: [tab("one")], active: tab("one"), closed: [] });
  // A transient tab that is not one of the open tabs, and a prompt that is not understood, are left out: the tabs are restored.
  const odd = JSON.stringify({ version: 1, open: [tab("one")], active: tab("one"), preview: tab("two"),
    drafts: [{ projectId: "p", path: "/p" }, { projectId: 7, path: "/x" }, "text", { projectId: "p", path: "/p" }] });
  assert.deepEqual(restoreSessionTabs(() => odd), { open: [tab("one")], active: tab("one"), closed: [], drafts: [{ projectId: "p", path: "/p" }] });
});

test("with a single tab the session that is opened takes the place of the one that was there", () => {
  const first = openSessionTab(emptySessionTabs(), tab("one"), "single");
  assert.deepEqual(first, { open: [tab("one")], active: tab("one"), closed: [] });
  assert.equal(openSessionTab(first, tab("one"), "single"), first, "the session that is shown, opened again, changes nothing");
  // The session that leaves is not a closed tab: it was replaced, as the one of a transient tab is.
  const second = openSessionTab(first, tab("two"), "single");
  assert.deepEqual(second, { open: [tab("two")], active: tab("two"), closed: [] });
  assert.equal(isPreviewTab(second, tab("two")), false, "nothing is transient");
  // Closed, it leaves no tab, and is the tab that can be opened again.
  const closed = closeSessionTab(second, tab("two"));
  assert.deepEqual(closed, { open: [], active: null, closed: [tab("two")] });
  assert.deepEqual(openSessionTab(closed, closed.closed.at(-1)!, "single"), second);
  // What is kept for the next start is what a window with one kept tab keeps.
  let stored = "";
  assert.ok(persistSessionTabs(value => { stored = value; }, second));
  assert.deepEqual(JSON.parse(stored), { version: 1, open: [tab("two")], active: tab("two") });
});

test("a single tab keeps the session shown of the tabs that were open, and neither a transient tab nor the tab of a prompt", () => {
  const project = { projectId: "p", path: "/p" };
  // A kept session, a kept chat that is the one shown, a session that is looked at, and the prompt of the project.
  const several = { ...openDraftTab(openSessionTab(openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("global", null)), tab("two"), "preview"), project),
    active: tab("global", null) };
  const single = singleSessionTab(several);
  assert.deepEqual(single, { open: [tab("global", null)], active: tab("global", null), closed: [], preview: null, drafts: [] });
  assert.equal(singleSessionTab(single), single, "the same state once it has a single tab");
  assert.equal(openSessionTab(single, tab("global", null), "single"), single);
  // Opening a session among several tabs leaves that one.
  assert.deepEqual(openSessionTab(several, tab("one"), "single"), { ...single, open: [tab("one")], active: tab("one") });
  // The prompt of a new session is the one shown: no session has a tab.
  assert.deepEqual(singleSessionTab({ ...several, active: null }), { ...single, open: [], active: null });
  // A state that had neither a transient tab nor a prompt keeps its shape, and so does what is stored of it.
  const plain = openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"));
  assert.deepEqual(singleSessionTab(plain), { open: [tab("two")], active: tab("two"), closed: [] });
  const one = openSessionTab(emptySessionTabs(), tab("one"));
  assert.equal(singleSessionTab(one), one);
  assert.equal(singleSessionTab(closeSessionTab(one, tab("one"))).open.length, 0);
  let stored = "";
  assert.ok(persistSessionTabs(value => { stored = value; }, single));
  assert.deepEqual(JSON.parse(stored), { version: 1, open: [tab("global", null)], active: tab("global", null) });

  // Several tabs again: the tab that was the single one is a kept tab, and the next session looked at is beside it.
  const again = openSessionTab(single, tab("two"), "preview");
  assert.deepEqual(again.open, [tab("global", null), tab("two")]);
  assert.ok(isPreviewTab(again, tab("two")) && !isPreviewTab(again, tab("global", null)));
});

test("the sessions that keep a pane without a tab are the named ones the catalog lists, but the one shown", () => {
  assert.deepEqual(backgroundSessionTabs(catalog, ["one", "global", "one", "missing"], "two"), [tab("one"), tab("global", null)]);
  assert.deepEqual(backgroundSessionTabs(catalog, ["one", "two"], "one"), [tab("two")]);
  assert.deepEqual(backgroundSessionTabs(catalog, [], null), []);
  // A session listed twice, or under a project the catalog does not have, is not one the window can show.
  assert.deepEqual(backgroundSessionTabs({ ...catalog, sessions: [...catalog.sessions, catalog.sessions[0]] }, ["one", "two"], null), [tab("two")]);
  assert.deepEqual(backgroundSessionTabs({ ...catalog, projects: [] }, ["one", "global"], null), [tab("global", null)]);
});
