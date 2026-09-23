import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { SessionInfoDialog } from "./SessionInfoDialog";
import { copySessionId, dismissSessionInfoOnKey, restoreSessionInfoFocus, sessionInfoCopyFeedback, sessionInfoView } from "./sessionInfo";

const session: WorkspaceSession = {
  id: "session-1", title: "Recorded title", fullTitle: "Recorded title", fullTitleTruncated: false,
  parentSessionId: null, scopeKind: "project", projectId: "p", lineageIssue: null,
  workspacePath: "/exact/p", providerKey: "recorded-provider", updatedAt: "2026-09-23T01:02:03+00:00",
};
const snapshot: WorkspaceSnapshot = {
  configured: true, projects: [{ id: "p", name: "Recorded project", path: "/exact/p", archived: false }],
  sessions: [session], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
};

test("exact selected project identity/path renders only recorded snapshot metadata, not inferred runtime status", () => {
  const info = sessionInfoView(snapshot, session, "p");
  assert.deepEqual(info, {
    id: "session-1", title: "Recorded title", titleTruncated: false, scope: "Project: Recorded project",
    scopeWarning: null, path: "/exact/p", provider: "recorded-provider",
    updatedAt: "2026-09-23T01:02:03+00:00", canCopyId: true,
  });
  const html = renderToStaticMarkup(createElement(SessionInfoDialog, { info, demo: false, onClose: () => {} }));
  assert.match(html, /<dialog[^>]*aria-labelledby="session-info-title"[^>]*aria-describedby="session-info-description"/);
  assert.match(html, /Saved catalog metadata, not live runtime status/);
  assert.match(html, /Project: Recorded project/);
  assert.match(html, /<time dateTime="2026-09-23T01:02:03\+00:00"/);
  assert.match(html, /Copy session ID/);
  assert.doesNotMatch(html, /\bRunning\b|\bUsage\b|\bPrompt\b|\bModel\b/);
});

test("global persists even when its working path equals a project; unknown or mismatched scope is not inferred", () => {
  const global = { ...session, scopeKind: "global" as const, projectId: null };
  assert.equal(sessionInfoView({ ...snapshot, sessions: [global] }, global, null).scope, "Global session");
  assert.match(sessionInfoView({ ...snapshot, sessions: [global] }, global, "p").scopeWarning!, /does not belong/);
  const unknown = { ...session, scopeKind: null, projectId: null };
  assert.equal(sessionInfoView({ ...snapshot, sessions: [unknown] }, unknown, "p").scope, "Unverified / unmatched scope");
  const wrongPath = { ...session, workspacePath: "/other" };
  assert.equal(sessionInfoView({ ...snapshot, sessions: [wrongPath] }, wrongPath, "p").scope, "Unverified / unmatched scope");
  const wrongId = { ...session, projectId: "another" };
  assert.equal(sessionInfoView({ ...snapshot, sessions: [wrongId] }, wrongId, "p").scope, "Unverified / unmatched scope");
  assert.match(sessionInfoView(snapshot, session, "another").scopeWarning!, /selected project does not match/);
});

test("missing or duplicate identity fails closed, missing fields are explicit and copy is disabled on ambiguity", () => {
  const missing = { ...session, workspacePath: null, providerKey: null, updatedAt: "invalid" };
  const info = sessionInfoView({ ...snapshot, sessions: [missing] }, missing, null);
  assert.equal(info.scope, "Unverified / unmatched scope");
  assert.equal(info.path, null);
  assert.equal(info.provider, null);
  assert.equal(info.updatedAt, null);
  const duplicate = sessionInfoView({ ...snapshot, sessions: [session, { ...session, title: "another row" }] }, session, "p");
  assert.equal(duplicate.canCopyId, false);
  assert.equal(duplicate.title, "Unverified");
  assert.equal(duplicate.path, null);
  assert.match(renderToStaticMarkup(createElement(SessionInfoDialog, { info: duplicate, demo: true, onClose: () => {} })),
    /Copy session ID<\/button>/);
  assert.match(renderToStaticMarkup(createElement(SessionInfoDialog, { info: duplicate, demo: true, onClose: () => {} })),
    /disabled=""/);
  const stale = sessionInfoView({ ...snapshot, sessions: [{ ...session }] }, session, "p");
  assert.equal(stale.canCopyId, false); // stale object from a replaced snapshot cannot claim selection
  for (const updatedAt of ["0001-01-01T00:00:00Z", "2026-02-30T00:00:00Z"]) {
    const invalid = { ...session, updatedAt };
    assert.equal(sessionInfoView({ ...snapshot, sessions: [invalid] }, invalid, "p").updatedAt, null);
  }
});

test("a session/project switch never reuses the previous selection's info or authorizes a stale copy", () => {
  const next = { ...session, id: "session-2", title: "New selection", projectId: null, scopeKind: "global" as const };
  const changed = { ...snapshot, sessions: [next] };
  assert.equal(sessionInfoView(changed, session, "p").canCopyId, false);
  assert.equal(sessionInfoView(changed, next, null).title, "New selection");
  assert.equal(sessionInfoView(changed, next, null).scope, "Global session");
  assert.equal(sessionInfoView(snapshot, session, "another").scopeWarning !== null, true);
});

test("bounded long text wraps in a scrollable modal without revealing longer summary-derived text", () => {
  const boundedTitle = "T".repeat(256);
  const long = { ...session, id: "I".repeat(256), title: boundedTitle, fullTitle: `hidden-longer-${"P".repeat(3900)}`,
    fullTitleTruncated: true, workspacePath: `/root/${"x".repeat(2000)}` };
  const info = sessionInfoView({ ...snapshot, sessions: [long] }, long, "p");
  assert.equal(info.title, boundedTitle);
  assert.equal(info.titleTruncated, true);
  assert.equal(info.scope, "Unverified / unmatched scope"); // path does not match catalog
  const html = renderToStaticMarkup(createElement(SessionInfoDialog, { info, demo: false, onClose: () => {} }));
  assert.match(html, /Title shortened in the bounded snapshot/);
  assert.match(html, /Recorded working directory/);
  assert.match(html, /I{256}/);
  assert.doesNotMatch(html, /hidden-longer-|P{100}/);
  const css = readFileSync(new URL("./style.css", import.meta.url), "utf8");
  assert.match(css, /\.session-header h1 \{[^}]*text-overflow: ellipsis; white-space: nowrap;/);
  assert.match(css, /\.session-info-fields \{[^}]*display: block;[^}]*overflow-y: auto;/);
  assert.match(css, /\.session-info-fields dd \{[^}]*overflow-wrap: anywhere; white-space: pre-wrap;/);
});

test("copy is explicit, exact, and reports success, denied clipboard or missing clipboard without retry", async () => {
  let calls = 0;
  assert.equal(await copySessionId("s", () => async id => { calls++; assert.equal(id, "s"); }), "copied");
  assert.equal(await copySessionId("s", () => { calls++; return undefined; }), "unavailable");
  assert.equal(await copySessionId("s", () => { throw Error("clipboard getter blocked"); }), "failed");
  assert.equal(await copySessionId("s", () => async () => { calls++; throw Error("denied"); }), "failed");
  assert.equal(await copySessionId("", () => { assert.fail("invalid ID must not access clipboard"); }), "failed");
  assert.equal(calls, 3);
  assert.equal(sessionInfoCopyFeedback("copied"), "Session ID copied.");
  assert.equal(sessionInfoCopyFeedback("unavailable"), "Clipboard unavailable; nothing copied.");
  assert.equal(sessionInfoCopyFeedback("failed"), "Could not copy session ID.");
});

test("Escape guards IME, native modal markup labels the close control, and focus only returns to a connected trigger", () => {
  assert.equal(dismissSessionInfoOnKey({ key: "Escape" }), true);
  assert.equal(dismissSessionInfoOnKey({ key: "Escape", isComposing: true }), false);
  assert.equal(dismissSessionInfoOnKey({ key: "Escape", keyCode: 229 }), false);
  assert.equal(dismissSessionInfoOnKey({ key: "o" }), false);
  let focused = 0;
  assert.equal(restoreSessionInfoFocus({ isConnected: false, focus: () => focused++ }), false);
  assert.equal(restoreSessionInfoFocus({ isConnected: true, focus: () => focused++ }), true);
  assert.equal(focused, 1);
  const html = renderToStaticMarkup(createElement(SessionInfoDialog, { info: sessionInfoView(snapshot, session, "p"), demo: true, onClose: () => {} }));
  assert.match(html, /aria-modal="true"/);
  assert.match(html, /autofocus=""/);
  assert.match(html, /Close session info/);
  assert.match(html, /Demo snapshot/);
});
