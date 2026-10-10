import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceProject, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { landingProjects, landingSessions, landingStartupMilliseconds, opensLandingAtStartup } from "./landingWindow";

const project = (id: string, archived = false): WorkspaceProject => ({ id, name: id.toUpperCase(), path: `/code/${id}`, archived });
const session = (id: string, over: Partial<WorkspaceSession> = {}): WorkspaceSession => ({
  id, title: `Session ${id}`, fullTitle: `Session ${id}`, fullTitleTruncated: false, automationId: null, createdAt: null, lineageIssue: null, messageCount: null, parentSessionId: null,
  projectId: null, providerKey: null, scopeKind: "global", updatedAt: "2026-10-09T10:00:00Z", workspacePath: null, worktreeMissing: false, worktreeName: null, worktreePath: null, worktreeRoot: null, ...over,
});
const snapshot = (projects: WorkspaceProject[], sessions: WorkspaceSession[]): WorkspaceSnapshot => ({ configured: true, displayTextTruncated: false, projects, projectsTruncated: false, sessions, sessionsTruncated: false });

test("the page opens for the start once the window is ready and its canvas is declared", () => {
  const start = { ready: true, handled: false, openAtStartup: true, elapsedMilliseconds: 0, declared: true };
  assert.equal(opensLandingAtStartup(start), "open");
  assert.equal(opensLandingAtStartup({ ...start, ready: false }), "wait");
  assert.equal(opensLandingAtStartup({ ...start, ready: false, declared: false }), "wait");
  // The preference is not looked at before the window is ready: nothing is decided on a window that has no tabs yet.
  assert.equal(opensLandingAtStartup({ ...start, ready: false, openAtStartup: false }), "wait");
  assert.equal(opensLandingAtStartup({ ...start, declared: false }), "wait", "the plugins did not declare the canvas yet");
  assert.equal(opensLandingAtStartup({ ...start, declared: false, elapsedMilliseconds: landingStartupMilliseconds }), "wait");
  assert.equal(opensLandingAtStartup({ ...start, elapsedMilliseconds: landingStartupMilliseconds }), "open");
});

test("the start is over when the user does not want the page, when it took too long, and once it was handled", () => {
  const start = { ready: true, handled: false, openAtStartup: true, elapsedMilliseconds: 0, declared: true };
  assert.equal(opensLandingAtStartup({ ...start, openAtStartup: false }), "done");
  assert.equal(opensLandingAtStartup({ ...start, openAtStartup: false, declared: false }), "done", "it does not wait for a canvas it will not open");
  assert.equal(opensLandingAtStartup({ ...start, elapsedMilliseconds: landingStartupMilliseconds + 1 }), "done", "a plugin that comes late does not open the page");
  assert.equal(opensLandingAtStartup({ ...start, declared: false, elapsedMilliseconds: landingStartupMilliseconds + 1 }), "done");
  assert.equal(opensLandingAtStartup({ ...start, handled: true }), "done");
  assert.equal(opensLandingAtStartup({ ...start, handled: true, ready: false }), "done");

  // As the window runs it: the decision is taken once, and a preference turned on later opens nothing.
  let handled = false;
  const decide = (state: { openAtStartup: boolean; declared: boolean; elapsedMilliseconds?: number }) => {
    const decision = opensLandingAtStartup({ ready: true, handled, elapsedMilliseconds: 0, ...state });
    if (decision !== "wait") handled = true;
    return decision;
  };
  assert.equal(decide({ openAtStartup: false, declared: true }), "done");
  assert.equal(decide({ openAtStartup: true, declared: true }), "done");
  handled = false;
  assert.equal(decide({ openAtStartup: true, declared: false }), "wait");
  assert.equal(decide({ openAtStartup: true, declared: true, elapsedMilliseconds: 900 }), "open");
  assert.equal(decide({ openAtStartup: true, declared: true }), "done", "a tab the user closed is not opened again");
});

test("the page lists the projects that are not archived", () => {
  assert.deepEqual(landingProjects(snapshot([project("one"), project("old", true), project("two")], [])),
    [{ id: "one", name: "ONE", path: "/code/one" }, { id: "two", name: "TWO", path: "/code/two" }]);
  assert.deepEqual(landingProjects(snapshot([], [])), []);
  assert.deepEqual(landingProjects(snapshot([project("old", true)], [])), []);
});

test("the page lists the sessions with their project, marks the sub-agents, and leaves out those of an archived project", () => {
  const sessions = landingSessions(snapshot([project("one"), project("old", true)], [
    session("a", { scopeKind: "project", projectId: "one", updatedAt: "2026-10-08T09:00:00Z" }),
    session("b", { scopeKind: "project", projectId: "old" }),
    session("c", { scopeKind: "project", projectId: "one", parentSessionId: "a" }),
    session("d"),
    // A chat keeps no project, whatever its record says.
    session("e", { scopeKind: "global", projectId: "old" }),
    session("f", { scopeKind: "project", projectId: null }),
    session("g", { scopeKind: null, projectId: "one", parentSessionId: "d" }),
  ]));
  assert.deepEqual(sessions, [
    { id: "a", title: "Session a", projectId: "one", updatedAt: "2026-10-08T09:00:00Z", child: false },
    { id: "c", title: "Session c", projectId: "one", updatedAt: "2026-10-09T10:00:00Z", child: true },
    { id: "d", title: "Session d", projectId: null, updatedAt: "2026-10-09T10:00:00Z", child: false },
    { id: "e", title: "Session e", projectId: null, updatedAt: "2026-10-09T10:00:00Z", child: false },
    { id: "f", title: "Session f", projectId: null, updatedAt: "2026-10-09T10:00:00Z", child: false },
    { id: "g", title: "Session g", projectId: null, updatedAt: "2026-10-09T10:00:00Z", child: true },
  ]);
  assert.deepEqual(landingSessions(snapshot([], [])), []);
});
