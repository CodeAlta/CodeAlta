import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { WorkspaceProject, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { persistProjectSort, projectRailProjection, restoreProjectSort } from "./projectRail";
import { ProjectRailRows } from "./ProjectRailRows";

const project = (id: string, name: string, path = `/${id}`, archived = false): WorkspaceProject => ({ id, name, path, archived });
const session = (id: string, projectId: string, workspacePath: string, updatedAt: string): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, createdAt: null, id, title: id, fullTitle: id, fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId,
  lineageIssue: null, workspacePath, providerKey: null, updatedAt,
});
const snapshot = (projects: WorkspaceProject[], sessions: readonly WorkspaceSession[] = []): WorkspaceSnapshot => ({
  configured: true, projects, sessions, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
});
const ids = (value: WorkspaceSnapshot, sort: "name" | "recent") =>
  projectRailProjection(value, sort).projects.map(project => project.id);

test("name order ignores case, breaks equal-name ties by identity and retains archived rows", () => {
  const value = snapshot([project("z", "ALPHA", "/Repos/z", true), project("a", "Alpha", "/Repos/a"), project("b", "beta", "/other/b")]);
  assert.deepEqual(ids(value, "name"), ["a", "z", "b"]);
  assert.equal(projectRailProjection(value, "name").projects[1].archived, true);
  assert.deepEqual(value.projects.map(project => project.id), ["z", "a", "b"]); // no in-place mutation
});

test("recent order uses only exact unique project ID/path scoped sessions, stable name/ID ties, unknown last", () => {
  const projects = [project("c", "Zed"), project("b", "Alpha"), project("a", "Alpha"), project("x", "Xeno", "/x", true)];
  const old = "2026-01-01T00:00:00Z";
  const latest = "2026-02-01T00:00:00+00:00";
  const value = snapshot(projects, [session("old", "c", "/c", old), session("equal", "a", "/a", latest),
    session("equal2", "b", "/b", latest), session("wrong-path", "x", "/elsewhere", "2027-01-01T00:00:00Z"),
    { ...session("global", "x", "/x", "2028-01-01T00:00:00Z"), scopeKind: "global" },
    { ...session("unverified", "x", "/x", "2029-01-01T00:00:00Z"), scopeKind: null },
    session("duplicate", "c", "/c", "2030-01-01T00:00:00Z"),
    session("duplicate", "x", "/x", "2030-01-01T00:00:00Z")]);
  assert.deepEqual(ids(value, "recent"), ["a", "b", "c", "x"]);
  assert.match(projectRailProjection(value, "recent").evidenceNotice!, /1 shown project.*no dated evidence/i);
});

test("invalid, absent, and sentinel dates cannot establish recency, including in truncated snapshots", () => {
  const projects = [project("z", "Zed"), project("a", "Alpha")];
  const invalid = ["not-a-date", "2026-02-30T00:00:00Z", "0001-01-01T00:00:00+00:00", "2026-01-01", "2026-01-01T00:00:00"];
  for (const updatedAt of invalid) {
    const value = { ...snapshot(projects, [session("bad", "z", "/z", updatedAt)]), sessionsTruncated: true, projectsTruncated: true };
    assert.deepEqual(ids(value, "recent"), ["a", "z"]);
    assert.match(projectRailProjection(value, "recent").evidenceNotice!, /no verified.*name order used.*truncated/i);
  }
  const partial = { ...snapshot(projects, [session("dated", "z", "/z", "2026-01-01T00:00:00Z")]), sessionsTruncated: true };
  assert.deepEqual(ids(partial, "recent"), ["z", "a"]);
  assert.match(projectRailProjection(partial, "recent").evidenceNotice!, /may be incomplete/i);
  assert.deepEqual(ids(snapshot(projects, [{ ...session("missing", "z", "/z", ""), updatedAt: undefined as unknown as string }]), "recent"), ["a", "z"]);
});

test("a rename refresh never changes the selection, and the project is listed under its new name", () => {
  const selected = { projectId: "p", sessionId: "s" };
  const original = snapshot([project("p", "Zeta"), project("q", "Other")], [session("s", "p", "/p", "2026-01-01T00:00:00Z")]);
  const renamed = snapshot([project("p", "Alpha"), project("q", "Other")], original.sessions);
  assert.deepEqual(ids(original, "name"), ["q", "p"]);
  assert.deepEqual(ids(renamed, "name"), ["p", "q"]);
  assert.deepEqual(selected, { projectId: "p", sessionId: "s" });
  assert.equal(renamed.sessions[0].id, selected.sessionId);
});

test("the rail renders a fixed accessible Chats root, the archived label and tooltip, and one selected row", () => {
  const value = snapshot([project("arch", "Archived", "/full/private/path", true), project("selected", "Shown")]);
  const render = (projects: WorkspaceProject[], selectedId: string | null) => renderToStaticMarkup(createElement(ProjectRailRows, {
    projects, selectedId, onSelect: () => {}, canRename: true, renameBusy: false, onRename: () => {},
  }));
  const listed = projectRailProjection(value, "name").projects;
  const archived = render(listed, "arch");
  assert.match(archived, /title="Archived\n\/full\/private\/path"/);
  assert.match(archived, /Archived<\/small>/);
  assert.doesNotMatch(archived, /Rename project/);
  assert.match(archived, /aria-label="Projects"/);
  assert.match(archived, /aria-label="Chats"/);
  assert.match(archived, /<strong>Chats<\/strong>/);
  assert.ok(archived.indexOf('aria-label="Chats"') < archived.indexOf('aria-label="Projects"'), "The chats come before the projects.");
  assert.equal(archived.split('aria-pressed="true"').length, 2, "One row is the selected one.");
  // A selected project that is not listed leaves no row pressed: the selection is not moved to the root.
  const absent = render(listed.filter(row => row.id !== "selected"), "selected");
  assert.doesNotMatch(absent, /Shown/);
  assert.doesNotMatch(absent, /aria-pressed="true"/);
  assert.match(render(listed, null), /aria-pressed="true"/);
});

test("sort preference falls back on missing, malformed or inaccessible WebView storage", () => {
  assert.equal(restoreProjectSort(() => "recent"), "recent");
  for (const value of [null, "date", "RECENT", "{malformed}"]) assert.equal(restoreProjectSort(() => value), "name");
  assert.equal(restoreProjectSort(() => { throw Error("unavailable"); }), "name");
  let saved = "";
  assert.equal(persistProjectSort(value => { saved = value; }, "recent"), true);
  assert.equal(saved, "recent");
  assert.equal(persistProjectSort(() => { throw Error("denied"); }, "name"), false);
});
