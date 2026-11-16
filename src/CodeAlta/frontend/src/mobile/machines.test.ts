import assert from "node:assert/strict";
import test from "node:test";
import type { Live, Machine, Session } from "./contract";
import {
  abilityOf, defaultSelection, eventsUrl, keyIn, localKey, machineLimit, machineOf, machineStatus, newRequestId, parseSessionHash, qualifyLive, qualifyPage, qualifySession,
  remoteBaseline, sessionBase, sessionHash, sessionKey, splitKey, validSelection, waitingWords,
} from "./machines";
import { createSessionStore } from "./sessionStore";
import { sessionTree } from "./sessionTree";

const now = Date.parse("2026-11-16T12:00:00Z");
const machine = (id: string, extra: Partial<Machine> = {}): Machine => ({
  id, name: id.toUpperCase(), this: id === "local", state: "online", path: id === "local" ? null : "direct", lastSeen: null,
  can: { view: true, drive: true, start: true }, features: id === "local" ? localFeatures : remoteBaseline, waiting: 0, ...extra,
});
// What CodeAlta Desktop offers the phone of its own sessions.
const localFeatures = [...remoteBaseline, "attachments", "linked-files", "reviews", "takeover", "worktree-choice"];
const session = (id: string, extra: Partial<Session> = {}): Session =>
  ({ id, title: id, projectId: "p1", project: "p1", summary: null, lastActivity: null, live: null, ...extra });

test("a session of this computer keeps its plain id, one of another machine is named by its machine and its id", () => {
  assert.equal(sessionKey(null, "s1"), "s1");
  assert.equal(sessionKey("local", "s1"), "s1");
  assert.equal(sessionKey("m2", "s1"), "@m2/s1");
  assert.notEqual(sessionKey("m2", "s1"), sessionKey("m3", "s1"), "two machines never share a name for the same id");
  assert.deepEqual(splitKey("@m2/s1"), { machineId: "m2", id: "s1" });
  assert.deepEqual(splitKey(sessionKey("a/b c", "x")), { machineId: "a/b c", id: "x" }, "a machine id with a slash is encoded");
  // An id of this computer that holds a slash is still its own.
  assert.deepEqual(splitKey("a/b"), { machineId: "local", id: "a/b" });
  assert.equal(machineOf("@m2/s1"), "m2");
  assert.equal(machineOf(null), "local");
  assert.equal(keyIn("@m2/s1", "s9"), "@m2/s9", "a session an answer links is of the machine of the session that links it");
  assert.equal(keyIn("s1", "s9"), "s9");
});

test("the routes of a session are this computer's own, or those of its machine", () => {
  assert.equal(sessionBase("s 1"), "/api/sessions/s%201");
  assert.equal(sessionBase("@m2/s1"), "/api/machines/m2/sessions/s1");
  assert.equal(sessionBase(sessionKey("a/b", "x/y")), "/api/machines/a%2Fb/sessions/x%2Fy");
  assert.equal(sessionHash("s1"), "#/session/s1", "the old links keep working");
  assert.equal(sessionHash("@m2/s1"), "#/machine/m2/session/s1");
  for (const key of ["s1", "a/b", "@m2/s1", sessionKey("a/b", "x/y")]) assert.equal(parseSessionHash(sessionHash(key)), key, key);
  assert.equal(parseSessionHash("#/session/s%201"), "s 1");
  assert.equal(parseSessionHash("#/new"), null);
  assert.equal(parseSessionHash("#/machine/m2"), null);
  assert.equal(parseSessionHash("#/session/%E0%A4%A"), null, "a link that cannot be read opens nothing");
});

test("the stream follows the session of another machine that is open, and only that one", () => {
  assert.equal(eventsUrl(null), "/api/events");
  assert.equal(eventsUrl("s1"), "/api/events");
  assert.equal(eventsUrl("@m2/s1"), "/api/events?machine=m2&session=s1");
  assert.equal(eventsUrl(sessionKey("a b", "x")), "/api/events?machine=a+b&session=x");
});

test("a session of another machine is named with its machine, its parent on its own machine unless it says otherwise", () => {
  const same = session("c1", { machineId: "m2", parentId: "p9" });
  assert.deepEqual([qualifySession(same).id, qualifySession(same).parentId], ["@m2/c1", "@m2/p9"]);
  const across = session("c1", { machineId: "m2", parentId: "p9", parentMachineId: "m3" });
  assert.equal(qualifySession(across).parentId, "@m3/p9");
  const local = session("l1", { parentId: "l0" });
  assert.equal(qualifySession(local), local, "a session of this computer is left as it is");
  assert.equal(qualifySession(session("l1", { machineId: "local", parentId: "l0" })).id, "l1");
  const live: Live = { id: "s1", machineId: "m2", live: "idle", kind: null, line: null, at: null, activity: null, canSteer: false };
  assert.equal(qualifyLive(live).id, "@m2/s1");
  assert.equal(qualifyLive({ ...live, machineId: "local" }).id, "s1");
  const page = qualifyPage({ sessions: [same], parents: [session("p9", { machineId: "m2" })] });
  assert.deepEqual([page.sessions[0].id, page.parents![0].id], ["@m2/c1", "@m2/p9"]);
});

test("the id of this computer in the fleet is this computer: a remote sub-agent folds under its parent here", () => {
  const device = "device-local";
  const child = session("c1", { machineId: "m2", parentId: "root", parentMachineId: device });
  const qualified = qualifySession(child, device);
  assert.equal(qualified.id, "@m2/c1");
  assert.equal(qualified.parentId, "root", "the parent is the plain session of this computer");
  assert.equal(qualified.parentMachineId, "local");
  // A session or a frame that names this computer by its device is its own.
  assert.equal(qualifySession(session("l1", { machineId: device }), device).id, "l1");
  assert.equal(qualifySession(session("l1", { machineId: device }), device).machineId, "local");
  assert.deepEqual([qualifyLive({ id: "l1", machineId: device } as Live, device).id, qualifyLive({ id: "l1", machineId: device } as Live, device).machineId], ["l1", "local"]);
  assert.equal(localKey(`@${device}/s1`, device), "s1");
  assert.equal(localKey("@m2/s1", device), "@m2/s1");
  assert.equal(localKey(`@${device}/s1`), `@${device}/s1`, "without a device nothing is renamed");

  const store = createSessionStore();
  store.applyPage({ sessions: [session("root", { lastActivity: new Date(now).toISOString() }), qualified], next: 2, hasMore: false, projects: [], machine: "PC", error: null }, 0, false);
  const rows = sessionTree(store.list(), { open: () => true });
  assert.deepEqual(rows.map(row => [row.view.id, row.depth]), [["root", 0], ["@m2/c1", 1]]);
});

test("two machines that share a session id do not share a card, a tree or a live state", () => {
  const store = createSessionStore();
  const a = qualifySession(session("same", { machineId: "m2", title: "On the mini" })), b = qualifySession(session("same", { machineId: "m3", title: "On the box" })), c = session("same", { title: "Here" });
  store.setFilter([], "all");
  store.applyPage({ sessions: [a, b, c], next: 3, hasMore: false, projects: [], machine: "PC", error: null }, 0, false);
  assert.deepEqual(store.list().map(view => view.title).sort(), ["Here", "On the box", "On the mini"]);
  store.applyLive(qualifyLive({ id: "same", machineId: "m2", live: "running", kind: null, line: "mini", at: null, activity: null, canSteer: false }));
  assert.equal(store.view("@m2/same")!.live!.line, "mini");
  assert.equal(store.view("@m3/same")!.live?.line ?? null, null, "the other machine's session is not touched");
  assert.equal(store.view("same")!.machineId, null, "this computer is no machine of its own to the view");
  assert.equal(store.view("@m2/same")!.machineId, "m2");
  assert.equal(store.view("@m2/same")!.machine, null, "the name comes from the session or the machines");
});

test("the list that shows one machine ignores the live states of the others", () => {
  const store = createSessionStore();
  const frame = (machineId: string | undefined, id = "x"): Live => qualifyLive({ id, machineId, projectId: "p1", live: "running", kind: null, line: null, at: new Date(now).toISOString(), activity: null, canSteer: false });
  store.setFilter([], "m2");
  assert.equal(store.applyLive(frame("m3")), "none");
  assert.equal(store.applyLive(frame(undefined, "y")), "none", "this computer is not m2");
  assert.equal(store.applyLive(frame("m2")), "reload");
  store.setFilter([], "all");
  assert.equal(store.applyLive(frame("m3")), "reload");
  store.setFilter([], "local");
  assert.equal(store.applyLive(frame(undefined, "z")), "reload");
  assert.equal(store.applyLive(frame("local", "w")), "reload");
  store.setFilter([], null);
  assert.equal(store.scope, null);
  assert.equal(store.applyLive(frame("m3", "q")), "reload", "an older CodeAlta lists this computer's own: nothing is filtered");
});

test("the machines are told with how they are reached, when they were last seen, and how many sessions wait", () => {
  assert.equal(machineStatus(machine("m2"), now), "Online · direct");
  assert.equal(machineStatus(machine("m2", { path: "relayed" }), now), "Online · relayed");
  assert.equal(machineStatus(machine("local"), now), "Online");
  assert.equal(machineStatus(machine("m3", { state: "offline", lastSeen: new Date(now - 25 * 60_000).toISOString() }), now), "Offline · last seen 25 min ago");
  assert.equal(machineStatus(machine("m3", { state: "offline" }), now), "Offline");
  assert.equal(machineStatus(machine("m4", { state: "connecting" }), now), "Connecting…");
  assert.equal(machineStatus(machine("m5", { state: "unauthorized" }), now), "Not allowed");
  assert.deepEqual([waitingWords(0), waitingWords(1), waitingWords(3)], ["", "1 waits for you", "3 wait for you"]);
});

test("every machine is the list when the computer reaches several, and the choice kept is used while its machine is there", () => {
  const several = [machine("local"), machine("m2"), machine("m3")];
  assert.equal(defaultSelection(several), "all");
  assert.equal(defaultSelection([machine("local")]), "local");
  assert.equal(validSelection(null, several), "all");
  assert.equal(validSelection("m2", several), "m2");
  assert.equal(validSelection("local", several), "local");
  assert.equal(validSelection("gone", several), "all", "a machine that is gone: every machine");
  assert.equal(validSelection("m2", [machine("local")]), "local", "alone, the computer shows its own");
});

test("what a session of another machine offers is what its machine says, and its machine's state keeps the phone from driving it", () => {
  const machines = [machine("local"), machine("m2"), machine("m3", { state: "offline", lastSeen: new Date(now - 90 * 60_000).toISOString() }),
    machine("m4", { can: { view: true, drive: false, start: false } }), machine("m5", { state: "unauthorized" }), machine("m6", { features: ["provider"] }),
    machine("m7", { features: [...remoteBaseline, "attachments"] }), machine("m8", { state: "connecting" })];
  const local = abilityOf("s1", null, machines, now);
  assert.deepEqual([local.local, local.canDrive, local.canStart, local.offers("attachments"), local.readOnly, local.outdated], [true, true, true, true, null, null]);
  const mini = abilityOf("@m2/s1", null, machines, now);
  assert.deepEqual([mini.local, mini.name, mini.canDrive, mini.canStart, mini.readOnly, mini.outdated], [false, "M2", true, true, null, null]);
  for (const left of ["attachments", "linked-files", "reviews", "takeover", "worktree-choice"]) assert.equal(mini.offers(left), false, `${left} is not offered`);
  for (const kept of ["sessions.read", "provider", "archive", "jobs", "questions", "permissions"]) assert.equal(mini.offers(kept), true, `${kept} is offered`);
  assert.equal(abilityOf("@m7/s1", null, machines, now).offers("attachments"), true, "a feature a machine says it offers is offered");
  const offline = abilityOf("@m3/s1", null, machines, now);
  assert.equal(offline.canDrive, false);
  assert.equal(offline.canStart, false);
  assert.match(offline.readOnly!, /^M3 is offline \(last seen 1 h ago\): you can read this session, not drive it\.$/);
  assert.match(abilityOf("@m4/s1", null, machines, now).readOnly!, /^You can read this session on M4, not drive it\.$/);
  assert.match(abilityOf("@m5/s1", null, machines, now).readOnly!, /does not let this computer in/);
  assert.match(abilityOf("@m8/s1", null, machines, now).readOnly!, /^Connecting to M8/);
  const old = abilityOf("@m6/s1", null, machines, now);
  assert.match(old.outdated!, /Update CodeAlta on M6/, "a machine without sessions.read says to update it");
  assert.equal(old.offers("provider"), true);
});

test("this computer offers what it says once it lists machines: a node leaves out what it does not do, an older CodeAlta does all it did", () => {
  const older = abilityOf("s1", null, [], now);
  assert.deepEqual(["attachments", "reviews", "worktree-choice", "anything"].map(older.offers), [true, true, true, true], "no list of machines: as before");
  const desktop = abilityOf("s1", null, [machine("local"), machine("m2")], now);
  assert.deepEqual(["attachments", "linked-files", "reviews", "takeover", "worktree-choice", "jobs.stop"].map(desktop.offers), [true, true, true, true, true, false]);
  assert.deepEqual([desktop.local, desktop.canDrive, desktop.canStart, desktop.readOnly, desktop.outdated], [true, true, true, null, null]);
  // A node is the only machine of its page: it takes no review and no question, and always works in a worktree of its own.
  const node = abilityOf("s1", null, [machine("local", { features: ["sessions.read", "provider", "archive", "jobs", "permissions", "attachments"] })], now);
  assert.deepEqual(["permissions", "attachments", "questions", "reviews", "worktree-choice", "takeover"].map(node.offers), [true, true, false, false, false, false]);
  assert.equal(node.local, true);
  const looked = abilityOf("s1", null, [machine("local", { can: { view: true, drive: false, start: false } })], now);
  assert.deepEqual([looked.canDrive, looked.canStart, looked.readOnly], [false, false, "You can read this session, not drive it."]);
});

test("a machine of the list says what keeps the phone out of its sessions", () => {
  assert.equal(machineLimit(machine("m2")), "");
  assert.equal(machineLimit(machine("m2", { features: ["provider"] })), "Update CodeAlta on this machine");
  assert.equal(machineLimit(machine("m2", { can: { view: true, drive: false, start: false } })), "View only");
  assert.equal(machineLimit(machine("m2", { state: "unauthorized", features: [] })), "", "its state says it");
  assert.equal(machineLimit(machine("local", { features: [] })), "", "this computer is never out");
});

test("a machine the page has not read yet is taken from what the session said of it, else for a remote machine that offers the baseline", () => {
  const hinted = abilityOf("@mx/s1", { machine: "Lab", state: "offline", can: { drive: true }, features: ["sessions.read", "jobs"] }, [], now);
  assert.deepEqual([hinted.name, hinted.canDrive, hinted.offers("jobs"), hinted.offers("archive")], ["Lab", false, true, false]);
  assert.match(hinted.readOnly!, /^Lab is offline: you can read/);
  const unknown = abilityOf("@mx/s1", null, [], now);
  assert.deepEqual([unknown.name, unknown.canDrive, unknown.offers("sessions.read"), unknown.offers("attachments")], ["mx", true, true, false]);
});

test("a request id is a random UUID, also where the browser gives no randomUUID", () => {
  assert.match(newRequestId(), /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  assert.notEqual(newRequestId(), newRequestId());
  const without = newRequestId({ getRandomValues: (array: ArrayBufferView) => { (array as Uint8Array).fill(255); return array; } } as Pick<Crypto, "randomUUID" | "getRandomValues">);
  assert.match(without, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
});
