import assert from "node:assert/strict";
import test from "node:test";
import type { Live, Session, SessionPage } from "./contract";
import { createDrafts } from "./drafts";
import { memoryStore } from "./api";
import { ago, createSessionStore, dotOf, hour, readSizes } from "./sessionStore";

const now = Date.parse("2026-10-24T12:00:00Z");
const iso = (minutesAgo: number) => new Date(now - minutesAgo * 60_000).toISOString();
const live = (id: string, state: Live["live"], at: string | null, extra: Partial<Live> = {}): Live =>
  ({ id, projectId: "p1", live: state, kind: "assistant", line: `line of ${id}`, at, activity: null, canSteer: false, ...extra });
const session = (id: string, minutesAgo: number, state: Live["live"] = "idle", projectId = "p1"): Session =>
  ({ id, title: `Session ${id}`, projectId, project: projectId, summary: null, lastActivity: iso(minutesAgo), live: live(id, state, iso(minutesAgo), { projectId }) });
const page = (sessions: Session[], hasMore = false, next = sessions.length): SessionPage => ({ sessions, next, hasMore, project: null, machine: "PC", error: null });

test("the first page replaces the list and a later one adds to it, without twice the same session", () => {
  const store = createSessionStore();
  store.applyPage(page([session("a", 1), session("b", 2)], true), store.beginRead(), false);
  store.applyPage(page([session("b", 2), session("c", 3)], false, 4), store.beginRead(), true);
  assert.deepEqual(store.list().map(v => v.id), ["a", "b", "c"]);
  assert.equal(store.next, 4);
  assert.equal(store.hasMore, false);
  store.applyPage(page([session("d", 0)]), store.beginRead(), false);
  assert.deepEqual(store.list().map(v => v.id), ["d"]);
});

test("a read of the whole list asks for what is shown, so the pages already shown stay", () => {
  assert.deepEqual(readSizes(0), [6]);
  assert.deepEqual(readSizes(18), [18]);
  assert.deepEqual(readSizes(120), [50, 50, 20]);
});

test("a live update newer than a page read wins over that page", () => {
  const store = createSessionStore();
  store.applyPage(page([session("a", 10)]), store.beginRead(), false);
  const startedAt = store.beginRead();
  store.applyLive(live("a", "running", iso(0)));
  // The page was read before the update arrived: its idle state is older.
  store.applyPage(page([session("a", 10)]), startedAt, false);
  assert.equal(store.view("a")?.live?.live, "running");
  // A page read after the update is the server's latest word.
  store.applyPage(page([session("a", 0, "idle")]), store.beginRead(), false);
  assert.equal(store.view("a")?.live?.live, "idle");
});

test("a live state without a line keeps the line the card had", () => {
  const store = createSessionStore();
  store.applyPage(page([session("a", 1)]), store.beginRead(), false);
  assert.equal(store.applyLive(live("a", "idle", null, { line: null })), "listed");
  assert.equal(store.view("a")?.live?.line, "line of a");
  assert.equal(store.view("a")?.live?.at, iso(1));
});

test("an update of a session out of the list reads the list once for it, not at each update", () => {
  const store = createSessionStore();
  store.applyPage(page([session("a", 5), session("b", 10)], true), store.beginRead(), false);
  assert.equal(store.applyLive(live("new", "running", iso(0))), "reload");
  assert.equal(store.applyLive(live("new", "tools", iso(0))), "none");
  assert.equal(store.applyLive(live("new", "idle", iso(0))), "none");
  // Its state is kept: its page shows it while it is not listed.
  assert.equal(store.view("new")?.live?.live, "idle");
});

test("an update out of the list is ignored when the filter would not show it or when it is older than the list", () => {
  const store = createSessionStore();
  store.setFilter("p1");
  store.applyPage(page([session("a", 5), session("b", 10)], true), store.beginRead(), false);
  assert.equal(store.applyLive(live("other", "running", iso(0), { projectId: "p2" })), "none", "another project");
  assert.equal(store.applyLive(live("unknown", "running", iso(0), { projectId: null })), "none", "a project the page cannot tell");
  // What the stream tells on each connection: every session seen since the plugin started, most of them long idle.
  assert.equal(store.applyLive(live("old", "idle", iso(600))), "none");
  assert.equal(store.applyLive(live("recent", "idle", iso(1))), "reload", "newer than the oldest card");
  // A list that is not full shows any session of the project.
  const short = createSessionStore();
  short.applyPage(page([session("a", 5)]), short.beginRead(), false);
  assert.equal(short.applyLive(live("old", "idle", iso(600))), "reload");
});

test("a new filter forgets which sessions already made the list be read", () => {
  const store = createSessionStore();
  store.applyPage(page([session("a", 5)], true), store.beginRead(), false);
  assert.equal(store.applyLive(live("x", "running", iso(0))), "reload");
  store.setFilter("p1");
  assert.equal(store.count, 0);
  store.applyPage(page([session("a", 5)], true), store.beginRead(), false);
  assert.equal(store.applyLive(live("x", "running", iso(0))), "reload");
});

test("a swiped session is hidden while its archive settles, until a read no longer lists it, and back after a restore", () => {
  const store = createSessionStore();
  store.applyPage(page([session("a", 1), session("b", 2)]), store.beginRead(), false);
  store.hide("a");
  assert.deepEqual(store.list().map(v => v.id), ["b"]);
  // A read while the archive is asked for still lists it: it stays hidden.
  store.applyPage(page([session("a", 1), session("b", 2)]), store.beginRead(), false);
  assert.equal(store.hidden("a"), true);
  store.settled("a");
  store.applyPage(page([session("b", 2)]), store.beginRead(), false);
  assert.equal(store.hidden("a"), false, "gone from the server's list: no longer kept hidden");
  store.hide("b");
  store.bringBack("b");
  assert.deepEqual(store.list().map(v => v.id), ["b"]);
  assert.equal(store.hidden("b"), false);
});

test("a session read alone is known without joining the list", () => {
  const store = createSessionStore();
  store.applySession(session("solo", 3, "running"));
  assert.equal(store.view("solo")?.title, "Session solo");
  assert.equal(store.view("solo")?.live?.live, "running");
  assert.equal(store.listed("solo"), false);
  assert.deepEqual(store.list(), []);
});

test("the dot works while the session works, says done for an hour, then inactive", () => {
  const store = createSessionStore();
  store.applyPage(page([session("w", 0, "tools"), session("d", 30), session("i", 61)]), store.beginRead(), false);
  assert.equal(dotOf(store.view("w")!, now), "tools");
  assert.equal(dotOf(store.view("d")!, now), "answered");
  assert.equal(dotOf(store.view("i")!, now), "inactive");
  assert.equal(dotOf(store.view("d")!, now + hour), "inactive");
});

test("ago says how long ago in a few words", () => {
  assert.equal(ago(iso(0.5), now), "just now");
  assert.equal(ago(iso(5), now), "5 min ago");
  assert.equal(ago(iso(180), now), "3 h ago");
  assert.equal(ago(iso(60 * 50), now), "2 d ago");
  assert.equal(ago(null, now), "");
});

test("drafts are kept per session, cleared by a send, and a failed text goes back only where nothing was typed since", () => {
  const drafts = createDrafts(memoryStore());
  drafts.set("a", "for a");
  drafts.set("b", "for b");
  assert.equal(drafts.get("a"), "for a");
  assert.equal(drafts.get("c"), "");
  drafts.set("a", "  ");
  assert.equal(drafts.get("a"), "", "a blank draft is no draft");
  drafts.clear("b");
  assert.equal(drafts.giveBack("b", "sent to b"), true);
  assert.equal(drafts.get("b"), "sent to b");
  drafts.set("a", "typed since");
  assert.equal(drafts.giveBack("a", "sent to a"), false);
  assert.equal(drafts.get("a"), "typed since");
});
