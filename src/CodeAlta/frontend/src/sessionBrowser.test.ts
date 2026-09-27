import test from "node:test";
import assert from "node:assert/strict";
import type { WorkspaceSnapshot } from "#neoastra";
import { browseSessions, browserActivation } from "./sessionBrowser";
import { resolveShortcut } from "./shortcuts";
import { paletteAvailable } from "./paletteActions";

const snapshot: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  projects: [{ id: "p", name: "Project", path: "/p", archived: false }], sessions: ["one", "two", "global"].map(id => ({ id,
    title: id, fullTitle: `Saved ${id}`, fullTitleTruncated: false, createdAt: null, updatedAt: "saved-time",
    parentSessionId: null, lineageIssue: null, providerKey: null, scopeKind: id === "global" ? "global" : "project",
    projectId: id === "global" ? null : "p", workspacePath: id === "global" ? null : "/p" })) };
const tab = { sessionId: "one", projectId: "p", path: "/p" };

test("saved browsing filters title/ID in scope without selection and reports bounded omissions", () => {
  assert.equal(browseSessions(snapshot, "p", "SAVED ONE").rows[0].row.id, "one");
  assert.equal(browseSessions(snapshot, null, "global").rows.length, 1);
  assert.equal(browseSessions(snapshot, "p", "global").rows.length, 0);
  const many = { ...snapshot, sessionsTruncated: true, sessions: Array.from({ length: 205 }, (_, i) => ({ ...snapshot.sessions[0], id: `s${i}` })) };
  assert.deepEqual([browseSessions(many, "p", "").rows.length, browseSessions(many, "p", "").hidden, browseSessions(many, "p", "").incomplete], [200, 5, true]);
});

test("activation requires current exact unique identity and revision; archives remain browsable", () => {
  assert.equal(browserActivation(snapshot, tab, 1, 1), true);
  assert.equal(browserActivation(snapshot, tab, 1, 3), false);
  assert.equal(browserActivation(undefined, tab, 1, 1), false);
  for (const changed of [{ ...snapshot, sessions: [] }, { ...snapshot, sessions: [...snapshot.sessions, snapshot.sessions[0]] },
    { ...snapshot, projects: [...snapshot.projects, snapshot.projects[0]] }, { ...snapshot, projects: [{ ...snapshot.projects[0], path: "/moved" }] },
    { ...snapshot, sessions: [{ ...snapshot.sessions[0], workspacePath: "/moved" }] }])
    assert.equal(browserActivation(changed, tab, 1, 1), false);
  assert.equal(browserActivation({ ...snapshot, projects: [{ ...snapshot.projects[0], archived: true }] }, tab, 1, 1), true);
});

test("browser shortcut excludes text, IME, repeats and modifiers; palette capture refuses ABA", () => {
  const key = { key: "b", ctrlKey: true, altKey: true };
  assert.equal(resolveShortcut(key, false, false).action, "browseSessions");
  assert.equal(resolveShortcut(key, false, true).handled, false);
  for (const event of [{ ...key, repeat: true }, { ...key, isComposing: true }, { ...key, shiftKey: true }, { ...key, metaKey: true }])
    assert.equal(resolveShortcut(event, false, false).action, null);
  assert.equal(resolveShortcut({ key: "e", ctrlKey: true }, false, false).handled, false);
  const context = { workspace: true, selection: null, epoch: null, infoReady: false, promptReady: true, searchReady: true, browserScope: "scope-1" };
  assert.equal(paletteAvailable("browseSessions", context, context), true);
  assert.equal(paletteAvailable("browseSessions", context, { ...context, browserScope: "scope-3" }), false);
});
