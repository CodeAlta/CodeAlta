import assert from "node:assert/strict";
import test from "node:test";
import { projectOf, sessionOf, statisticsContext } from "./canvasContext";
import { createFixtureApi } from "./fixtureApi";
import { decodeFrame, defaultFrame, encodeFrame, frameReducer, initialFrame, requestOf, sessionScoped, type Frame } from "./frame";
import type { StatisticsDirectory } from "./rpcApi";
import { pluginEventItem } from "../pluginEvents";

// The canvas of one session and its sub-agents: what its key says, what its requests carry, and what the fixture answers.

const directory: StatisticsDirectory = {
  weekStart: "Monday",
  spaces: [{ id: "default", name: "Default", isDefault: true, projectIds: ["p1", "p2"] }, { id: "work", name: "Work", isDefault: false, projectIds: ["p1"] }],
  projects: [{ id: "p1", name: "CodeAlta" }, { id: "p2", name: "NeoAstra" }],
  providers: [],
};
const alta = (context: Readonly<{ key?: string | null; spaceId?: string | null; input?: unknown }>) => ({
  context: { pluginKey: "builtin:statistics", canvasId: "statistics", instanceId: "i-1", spaceId: context.spaceId === undefined ? "work" : context.spaceId, projectId: null, sessionId: null,
    key: context.key ?? null, input: context.input ?? null },
  host: { openSession: () => { } },
}) as unknown as Parameters<typeof statisticsContext>[0];

test("the key of a canvas names its session, and a session key is not a project key", () => {
  assert.equal(sessionOf({ key: "session:01a124b3-fcbd" }), "01a124b3-fcbd");
  assert.equal(sessionOf({ key: "session:" }), null);
  assert.equal(sessionOf({ key: "project:p1" }), null);
  assert.equal(sessionOf({ key: "sessions:x" }), null);
  assert.equal(sessionOf({ key: null }), null);
  assert.equal(projectOf({ key: "session:p1", input: null }), null);
});

test("a canvas of a session is limited to it and starts with no filter of a space or of a project", () => {
  const context = statisticsContext(alta({ key: "session:s-1", spaceId: "work" }), true, directory);
  assert.equal(context.sessionId, "s-1");
  assert.equal(context.spaceId, null, "the named space the window shows is not a filter of the session");
  assert.equal(context.projectId, null);
  assert.deepEqual(initialFrame(context).filters, {});
  assert.deepEqual(initialFrame(context).period, { kind: "preset", preset: "all" }, "the life of the session, not the last 30 days");
  assert.deepEqual(initialFrame(statisticsContext(alta({ spaceId: "work" }), true, directory)).period, defaultFrame.period);
  // Whatever names a space or a project beside the session, the canvas of a session opens without a filter.
  assert.deepEqual(initialFrame({ sessionId: "s-1", spaceId: "work", spaceName: "Work", projectId: "p1", projectName: "CodeAlta" }).filters, {});
  assert.deepEqual(initialFrame({ sessionId: null, spaceId: "work", spaceName: "Work" }).filters, { space: { value: "work", label: "Work" } });
  // An input that names a project does not turn a canvas of a session into a canvas of a project.
  const withInput = statisticsContext(alta({ key: "session:s-1", input: { project: "p2" } }), true, directory);
  assert.equal(withInput.sessionId, "s-1");
  assert.equal(withInput.projectId, null);
  // The other canvases are as they were.
  const plain = statisticsContext(alta({ spaceId: "work" }), true, directory);
  assert.equal(plain.sessionId, null);
  assert.equal(plain.spaceId, "work");
  const project = statisticsContext(alta({ key: "project:p2" }), true, directory);
  assert.equal(project.sessionId, null);
  assert.equal(project.projectId, "p2");
});

test("every request of a canvas of a session carries the session and its sub-agents, whatever the frame and the page add", () => {
  const scope = { session: "s-1", withChildren: true };
  assert.deepEqual(sessionScoped(requestOf(defaultFrame, 1), "s-1"), { period: "30d", frequency: "auto", comparison: "none", weekStart: "Monday", filter: scope });
  // The filters of the frame stay beside it.
  let frame: Frame = frameReducer(defaultFrame, { type: "filter", key: "provider", entry: { value: "codex" } });
  frame = frameReducer(frame, { type: "filter", key: "project", entry: { value: "p1", label: "CodeAlta" } });
  assert.deepEqual(sessionScoped(requestOf(frame, 1), "s-1").filter, { project: "p1", provider: "codex", ...scope });
  // A page that empties the filter to list every model or project still asks inside the session.
  assert.deepEqual(sessionScoped(requestOf(frame, 1, { period: "all", filter: {}, limit: 500 }), "s-1").filter, scope);
  // Nothing a page adds names another session.
  assert.deepEqual(sessionScoped(requestOf(frame, 1, { filter: { session: "other", withChildren: false } }), "s-1").filter, scope);
  // A canvas that is of no session asks as before, with the very same request.
  const plain = requestOf(frame, 1);
  assert.equal(sessionScoped(plain, null), plain);
  assert.equal(sessionScoped(plain, undefined), plain);
  assert.equal(sessionScoped(plain, ""), plain);
});

test("the session is no filter of the frame: removing every filter, Reset and a reload keep it", () => {
  const context = statisticsContext(alta({ key: "session:s-1" }), true, directory);
  const open = initialFrame(context);
  let frame = frameReducer(open, { type: "filter", key: "model", entry: { value: "gpt-6.1-sol" } });
  frame = frameReducer(frame, { type: "filter", key: "model", entry: null });
  assert.deepEqual(sessionScoped(requestOf(frame, 1), context.sessionId).filter, { session: "s-1", withChildren: true });
  const reset = frameReducer(frame, { type: "reset", frame: open });
  assert.deepEqual(sessionScoped(requestOf(reset, 1), context.sessionId).filter, { session: "s-1", withChildren: true });
  assert.equal(requestOf(reset, 1).period, "all");
  // What a reload reads back has no session in it to lose or to forge: the key of the canvas says it.
  assert.ok(!encodeFrame(frame).includes("session"));
  const forged = decodeFrame("page=overview&session=other&session.label=Other", open);
  assert.deepEqual(forged.filters, {});
});

test("the fixture answers a session filter with the session and its sub-agents, and nothing for a session it does not know", async () => {
  const api = createFixtureApi({ today: "2026-10-09", sessionCount: 240 });
  const sessions = api.control.data.sessions;
  const parent = sessions.find(candidate => sessions.some(other => other.parent === candidate.id))!;
  assert.ok(parent, "the fixture has a session with sub-agents");
  const tree = new Set([parent.id]);
  for (let grown = true; grown;) {
    grown = false;
    for (const item of sessions) if (item.parent && tree.has(item.parent) && !tree.has(item.id)) { tree.add(item.id); grown = true; }
  }
  const all = { period: "all" } as const;

  const everything = await api.sessions({ ...all, limit: 500 }, "recent");
  const scoped = await api.sessions({ ...all, limit: 500, filter: { session: parent.id, withChildren: true } }, "recent");
  const alone = await api.sessions({ ...all, limit: 500, filter: { session: parent.id } }, "recent");
  const unknown = await api.sessions({ ...all, limit: 500, filter: { session: "no-such-session", withChildren: true } }, "recent");

  assert.ok(scoped.rows.length >= 2 && scoped.rows.length < everything.rows.length);
  assert.ok(scoped.rows.every(row => tree.has(row.sessionId)));
  assert.ok(scoped.rows.some(row => row.sessionId !== parent.id), "its sub-agents are with it");
  assert.deepEqual(alone.rows.map(row => row.sessionId), [parent.id]);
  assert.deepEqual(unknown.rows, [], "never the sessions of everyone");
  const total = (await api.summary({ ...all })).tiles.find(tile => tile.id === "tokens")!.value;
  const inside = (await api.summary({ ...all, filter: { session: parent.id, withChildren: true } })).tiles.find(tile => tile.id === "tokens")!.value;
  assert.ok(inside > 0 && inside < total);
  assert.equal((await api.summary({ ...all, filter: { session: "no-such-session" } })).tiles.find(tile => tile.id === "tokens")!.value, 0);
});

test("the card of a turn keeps its row and its copy, and its details are the section with the button of the session", () => {
  const markdown = "| Metric | Value |\n| --- | ---: |";
  const html = `<div class="alta-column"><div class="alta-markdown">${markdown}</div><div class="alta-row"><button type="button" data-alta-command="statistics-session">Session statistics</button></div></div>`;
  const item = pluginEventItem({ eventId: "statistics:s:run-1", pluginId: "statistics", timestamp: "2026-10-09T10:00:04Z", markdown: "**Turn statistics** · 4.0s · tools 2 calls / 1.5s",
    details: [{ header: "Detailed statistics", markdown, html }], html: null, script: null, scriptProblem: null });
  assert.equal(item.title, "Turn statistics");
  assert.equal(item.summary, "4.0s · tools 2 calls / 1.5s", "the row is the summary, as before");
  assert.equal(item.html, null);
  assert.deepEqual(item.detailSections, [{ header: "Detailed statistics", html, markdown: null }]);
  assert.equal(item.detailMarkdown, null, "the details are drawn once, from the section");
  assert.equal(item.copyMarkdown, `**Turn statistics** · 4.0s · tools 2 calls / 1.5s\n\n${markdown}`, "Copy takes the Markdown, without the button");
  assert.equal(item.pluginKey, "statistics");
});
