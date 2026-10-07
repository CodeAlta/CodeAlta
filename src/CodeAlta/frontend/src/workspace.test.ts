import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { loadWorkspace, sessionListSignature, sessionsForProject, workspaceNotice, type WorkspaceState } from "./workspace";

const empty: WorkspaceSnapshot = {
  configured: true, projects: [], sessions: [], projectsTruncated: false,
  sessionsTruncated: false, displayTextTruncated: false,
};
const signal = { aborted: false } as AbortSignal;

test("workspace loading transitions to populated snapshot", async () => {
  const states: WorkspaceState[] = [];
  const snapshot: WorkspaceSnapshot = { ...empty, projects: [{ id: "p", name: "Project", path: "/p", archived: false }] };
  await loadWorkspace(async (request, options) => {
    assert.deepEqual(request, {});
    assert.equal(options.signal, signal);
    return snapshot;
  }, signal, state => states.push(state));
  assert.deepEqual(states, [{ kind: "loading" }, { kind: "ready", snapshot }]);
});

test("workspace distinguishes unconfigured from empty", async () => {
  const states: WorkspaceState[] = [];
  await loadWorkspace(async () => ({ ...empty, configured: false }), signal, state => states.push(state));
  assert.deepEqual(states.at(-1), { kind: "unconfigured" });
  await loadWorkspace(async () => empty, signal, state => states.push(state));
  assert.deepEqual(states.at(-1), { kind: "ready", snapshot: empty });
});

test("workspace reports read failure without exposing exception details", async () => {
  const states: WorkspaceState[] = [];
  await loadWorkspace(async () => { throw new Error("private path / credentials / locked cache"); }, signal, state => states.push(state));
  const state = states.at(-1);
  assert.equal(state?.kind, "error");
  if (state?.kind === "error") {
    assert.match(state.message, /close.*relaunch/i);
    assert.doesNotMatch(state.message, /private|credentials/);
  }
});

test("workspace ignores completion after abort", async () => {
  const states: WorkspaceState[] = [];
  let aborted = false;
  const controlled = { get aborted() { return aborted; } } as AbortSignal;
  const original = loadWorkspace(async () => empty, controlled, state => states.push(state));
  aborted = true;
  await original;
  assert.deepEqual(states, [{ kind: "loading" }]);
  await loadWorkspace(async () => { assert.fail("must not start after abort"); }, controlled, state => states.push(state));
  assert.equal(states.length, 1);
});

test("workspace ignores failure after abort", async () => {
  const states: WorkspaceState[] = [];
  let aborted = false;
  const controlled = { get aborted() { return aborted; } } as AbortSignal;
  const original = loadWorkspace(async () => { throw new Error("late failure"); }, controlled, state => states.push(state));
  aborted = true;
  await original;
  assert.deepEqual(states, [{ kind: "loading" }]);
});

test("workspace keeps persisted global/project scope and unverified or truncated unmatched sessions visible", () => {
  const session = { messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, createdAt: null, id: "s", title: "Saved title", fullTitle: "Saved title", fullTitleTruncated: false, parentSessionId: null,
    scopeKind: "project", projectId: "p", lineageIssue: null, workspacePath: "/p", providerKey: null, updatedAt: "2026-01-01T00:00:00Z" };
  const snapshot: WorkspaceSnapshot = {
    ...empty, projects: [{ id: "p", name: "Project", path: "/p", archived: false }],
    sessions: [session, { ...session, id: "global", scopeKind: "global", projectId: null },
      { ...session, id: "global-in-project", scopeKind: "global", projectId: null },
      { ...session, id: "unmatched", scopeKind: null, projectId: null, workspacePath: "/other" },
      { ...session, id: "unverified", scopeKind: null, projectId: null },
      { ...session, id: "omitted", projectId: "omitted", workspacePath: "/omitted" }],
  };
  assert.deepEqual(sessionsForProject(snapshot, "p").map(value => value.id), ["s", "unverified"]);
  assert.deepEqual(sessionsForProject(snapshot, null).map(value => value.id), ["global", "global-in-project", "unmatched", "omitted"]);
});

test("workspace shows truncation without implying paging", () => {
  assert.equal(workspaceNotice(empty), null);
  assert.match(workspaceNotice({ ...empty, projectsTruncated: true })!, /not paging/i);
  assert.match(workspaceNotice({ ...empty, sessionsTruncated: true })!, /whole catalog/i);
  assert.match(workspaceNotice({ ...empty, displayTextTruncated: true })!, /shortened/i);
});

test("session list signature changes with the sessions shown, not with their activity", () => {
  const session = { id: "a", title: "A", parentSessionId: null, scopeKind: "project", projectId: "p", workspacePath: "/p", updatedAt: "2026-01-01T00:00:00Z" };
  const snapshot = (...sessions: object[]) => ({ ...empty, sessions }) as unknown as WorkspaceSnapshot;
  const shown = sessionListSignature(snapshot(session));

  assert.equal(sessionListSignature(snapshot({ ...session, updatedAt: "2026-01-02T00:00:00Z" })), shown);
  assert.notEqual(sessionListSignature(snapshot(session, { ...session, id: "b", parentSessionId: "a" })), shown, "a sub-session appeared");
  assert.notEqual(sessionListSignature(snapshot({ ...session, title: "Renamed" })), shown);
  assert.equal(sessionListSignature(snapshot({ ...session, id: "b" }, session)), sessionListSignature(snapshot(session, { ...session, id: "b" })));
});
