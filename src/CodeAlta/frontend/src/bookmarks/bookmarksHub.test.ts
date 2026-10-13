import assert from "node:assert/strict";
import test from "node:test";
import type { BookmarkRow } from "#neoastra";
import { createBookmarksHub, reconnectMilliseconds, type BookmarksApi } from "./bookmarksHub";

const row = (id: string, values: Partial<BookmarkRow> = {}): BookmarkRow => ({ id, kind: "message", sessionId: "s", projectId: "p", sessionTitle: "Session", sessionMissing: false, role: "user",
  label: null, excerpt: "text", runId: "run", contentId: id, offset: "10", messageAt: "2026-10-09T11:00:00Z", createdAt: "2026-10-10T00:00:00Z", createdBy: null, ...values });
const settle = () => new Promise<void>(resolve => setTimeout(resolve, 0));

/** A host that keeps bookmarks, tells of each change, and can be made to stop telling. */
function host(initial: BookmarkRow[] = []) {
  const state = { rows: [...initial], status: "ok", lists: 0, watches: 0, calls: [] as unknown[], notify: [] as (() => void)[], end: [] as (() => void)[], serial: 0 };
  const changed = () => { for (const tell of state.notify) tell(); };
  const api = {
    list: async () => { state.lists++; return state.status === "ok" ? { status: "ok", bookmarks: [...state.rows], truncated: false } : { status: state.status, bookmarks: [], truncated: false }; },
    add: async (request: { sessionId: string; offset?: string | null; label?: string | null }) => {
      state.calls.push(["add", request.sessionId, request.offset ?? null, request.label ?? null]);
      if (request.offset === "0") return { status: "not_bookmarkable", bookmark: null };
      const bookmark = row(`new${++state.serial}`, { sessionId: request.sessionId, offset: request.offset ?? null, kind: request.offset ? "message" : "session", label: request.label ?? null });
      state.rows.unshift(bookmark); changed();
      return { status: "ok", bookmark };
    },
    rename: async (request: { id: string; label?: string | null }) => {
      const index = state.rows.findIndex(candidate => candidate.id === request.id);
      if (index < 0) return { status: "not_found", bookmark: null };
      state.rows[index] = { ...state.rows[index], label: request.label ?? null }; changed();
      return { status: "ok", bookmark: state.rows[index] };
    },
    remove: async (request: { id: string }) => {
      const found = state.rows.find(candidate => candidate.id === request.id);
      if (!found) return { status: "not_found", bookmark: null };
      state.rows = state.rows.filter(candidate => candidate !== found); changed();
      return { status: "ok", bookmark: found };
    },
    resolve: async (request: { id: string }) => {
      const found = state.rows.find(candidate => candidate.id === request.id);
      return found ? { status: "ok", sessionId: found.sessionId, projectId: found.projectId, offset: "77" } : { status: "not_found", sessionId: null, projectId: null, offset: null };
    },
    watch: async (_request: unknown, options?: { signal?: AbortSignal }) => {
      state.watches++;
      const pending: number[] = [0];
      let wake: (() => void) | null = null, ended = false, revision = 0;
      const tell = () => { pending.push(++revision); wake?.(); };
      state.notify.push(tell);
      state.end.push(() => { ended = true; wake?.(); });
      options?.signal?.addEventListener("abort", () => { ended = true; wake?.(); });
      return (async function* () {
        try {
          for (;;) {
            while (pending.length) yield { revision: pending.shift()! };
            if (ended) return;
            await new Promise<void>(resolve => { wake = resolve; });
          }
        } finally { state.notify = state.notify.filter(other => other !== tell); }
      })();
    },
  };
  return { state, api: api as unknown as BookmarksApi, changed };
}

test("the list is read when the hub connects and each time the host says it changed", async () => {
  const { state, api, changed } = host([row("a")]);
  const hub = createBookmarksHub(api);
  let told = 0;
  hub.subscribe(() => { told++; });
  assert.deepEqual(hub.getSnapshot(), { loaded: false, available: false, bookmarks: [], truncated: false });
  const disconnect = hub.connect("epoch");
  await settle(); await settle();
  assert.equal(hub.getSnapshot().loaded && hub.getSnapshot().available, true);
  assert.deepEqual(hub.getSnapshot().bookmarks.map(bookmark => bookmark.id), ["a"]);
  const shown = hub.getSnapshot();

  // The same answer again changes nothing on the page.
  changed();
  await settle(); await settle();
  assert.equal(hub.getSnapshot(), shown);

  // A bookmark an agent added is listed like one of the user.
  state.rows.unshift(row("agent", { createdBy: "another-session" }));
  const before = told;
  changed();
  await settle(); await settle();
  assert.deepEqual(hub.getSnapshot().bookmarks.map(bookmark => bookmark.id), ["agent", "a"]);
  assert.ok(told > before);

  disconnect();
  assert.deepEqual(hub.getSnapshot(), { loaded: false, available: false, bookmarks: [], truncated: false }, "nothing of a host that is gone is shown");
  state.rows = [];
  changed();
  await settle();
  assert.equal(hub.getSnapshot().loaded, false);
});

test("a change is shown at once and answers with what the host said", async () => {
  const { state, api } = host([row("a"), row("b")]);
  const hub = createBookmarksHub(api);
  hub.connect("epoch");
  await settle(); await settle();

  const added = await hub.add("s2", "4000", "Mine");
  assert.equal(added.ok, true);
  assert.equal(added.bookmark?.label, "Mine");
  assert.deepEqual(hub.getSnapshot().bookmarks.map(bookmark => bookmark.id), [added.bookmark!.id, "a", "b"]);
  assert.deepEqual(state.calls.at(-1), ["add", "s2", "4000", "Mine"]);
  const session = await hub.add("s3");
  assert.equal(session.bookmark?.kind, "session");
  assert.deepEqual(state.calls.at(-1), ["add", "s3", null, null]);

  const refused = await hub.add("s2", "0");
  assert.deepEqual(refused, { ok: false, status: "not_bookmarkable", bookmark: null });

  const renamed = await hub.rename("a", "The decision");
  assert.equal(renamed.ok, true);
  assert.equal(hub.getSnapshot().bookmarks.find(bookmark => bookmark.id === "a")?.label, "The decision");
  assert.equal((await hub.rename("a", null)).bookmark?.label, null);
  assert.equal((await hub.rename("none", "x")).status, "not_found");

  const removed = await hub.remove("b");
  assert.equal(removed.ok, true);
  assert.equal(hub.getSnapshot().bookmarks.some(bookmark => bookmark.id === "b"), false);
  assert.equal((await hub.remove("b")).status, "not_found");
  await settle(); await settle();
  assert.deepEqual(hub.getSnapshot().bookmarks.map(bookmark => bookmark.id), state.rows.map(bookmark => bookmark.id), "the list the host keeps is what stays shown");

  assert.deepEqual(await hub.resolve("a"), { status: "ok", sessionId: "s", projectId: "p", offset: "77" });
  assert.equal((await hub.resolve("none")).status, "not_found");
});

test("a host that keeps no bookmarks is said to have none, and a host that stops telling is listened to again", async () => {
  const { state, api } = host([row("a")]);
  state.status = "unavailable";
  const timers: (() => void)[] = [];
  const hub = createBookmarksHub(api, { set: (run, milliseconds) => { assert.equal(milliseconds, reconnectMilliseconds); timers.push(run); return run; }, clear: timer => { timers.splice(timers.indexOf(timer as () => void), 1); } });
  const disconnect = hub.connect("epoch");
  await settle(); await settle();
  assert.deepEqual(hub.getSnapshot(), { loaded: true, available: false, bookmarks: [], truncated: false });
  assert.deepEqual(await hub.add("s"), { ok: true, status: "ok", bookmark: state.rows[0] }, "a change is still asked of the host");

  // The host stops telling: the hub listens again after a moment, and reads what there is.
  state.status = "ok";
  assert.equal(state.watches, 1);
  for (const end of state.end) end();
  await settle(); await settle();
  assert.equal(timers.length, 1);
  timers.shift()!();
  await settle(); await settle();
  assert.equal(state.watches, 2);
  assert.equal(hub.getSnapshot().available, true);

  disconnect();
  await settle();
  assert.equal(timers.length, 0, "nothing listens again after the hub left");
  assert.deepEqual(await hub.add("s"), { ok: false, status: "failed", bookmark: null });
  assert.equal((await hub.resolve("a")).status, "unavailable");
});
