import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { SpaceItem, SpaceSessionActivity, WorkspaceProject, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { SpacePage } from "./SpacePage";
import { dropChange, movedOrder, SpaceSettings } from "./SpaceSettings";
import { SpaceActivityBar, SpaceSwitch } from "./SpaceViews";
import { defaultSpace, defaultSpaceId, findSpace, neighborSpace, persistShownSpace, placeProject, readSessionActivity, readSpaces, waitingSubSessions, restoreShownSpace, sameSpaces, scopeSnapshot,
  sessionHome, sessionSpaces, spaceActivities, spaceBrand, spaceCalls, spaceChatCounts, spaceNameProblem, spaceQuiet, spaceShows, spaceStorageKey, spaceView, spaceViewKey, viewLists } from "./spaces";
import { activityMilliseconds, createSpacesHub, type SpacesApi, type SpacesHub } from "./spacesHub";
import { SpaceScopeChip, spaceNames, toggledSpaces } from "./SpaceChips";
import { inSpace, noAssignments, readAssignments, resourceSpaces, spacesWith, spacesWithout } from "./spaces";

const item = (id: string, projectIds: string[] = [], changes: Partial<SpaceItem> = {}): SpaceItem =>
  ({ id, name: id[0].toUpperCase() + id.slice(1), description: null, icon: null, color: null, isDefault: id === "default", projectIds, file: id === "default" ? null : `C:\\alta\\spaces\\${id}.md`, agentPrompt: null, disabledTools: [], ...changes });
const project = (id: string, archived = false): WorkspaceProject => ({ id, name: id.toUpperCase(), path: `C:\\code\\${id}`, archived });
const session = (id: string, scopeKind: string | null, projectId: string | null, workspacePath: string | null): WorkspaceSession => ({ id, title: id, workspacePath, providerKey: "codex",
  updatedAt: "2026-01-01T00:00:00Z", fullTitle: id, fullTitleTruncated: false, parentSessionId: null, scopeKind, projectId, lineageIssue: null, createdAt: "2026-01-01T00:00:00Z",
  messageCount: 1, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null });
const doing = (sessionId: string, projectId: string | null, changes: Partial<SpaceSessionActivity> = {}): SpaceSessionActivity =>
  ({ sessionId, projectId, title: `Title of ${sessionId}`, running: false, backgroundTasks: 0, failed: false, waiting: false, ancestors: [], spaceId: null, fallback: null, ...changes });

test("the spaces are read as the host lists them, the default one first, and what is not a space is left out", () => {
  const spaces = readSpaces([item("work", ["a", "b"], { icon: "briefcase", color: "#2D72D2", description: "Paid work" }), item("default", ["a", "b", "c"]),
    item("work", ["z"]), { ...item("bad"), id: "Not An Id" }, { ...item("noname"), name: "" }, null as unknown as SpaceItem,
    item("odd", ["a", 7 as unknown as string, ""], { color: "blue", icon: "" })]);
  assert.deepEqual(spaces.map(space => space.id), ["default", "work", "odd"]);
  assert.deepEqual(spaces[1], { id: "work", name: "Work", description: "Paid work", icon: "briefcase", color: "#2D72D2", isDefault: false, projectIds: ["a", "b"], file: "C:\\alta\\spaces\\work.md", agentPrompt: null, disabledTools: [] });
  assert.deepEqual([spaces[2].projectIds, spaces[2].color, spaces[2].icon], [["a"], null, null]);
  assert.equal(spaces[0].isDefault, true);
  // A list without a default space gets one: the window always has a space that holds every project.
  assert.deepEqual(readSpaces([item("work")]).map(space => space.id), ["default", "work"]);
  assert.deepEqual(readSpaces(null), [defaultSpace]);
  assert.equal(sameSpaces(spaces, readSpaces([item("default", ["a", "b", "c"]), item("work", ["a", "b"], { icon: "briefcase", color: "#2D72D2", description: "Paid work" }),
    item("odd", ["a"])])), true);
  assert.equal(sameSpaces(spaces, readSpaces([item("default", ["a", "b", "c"]), item("work", ["a"]), item("odd", ["a"])])), false);
  assert.equal(findSpace(spaces, "work").name, "Work");
  assert.equal(findSpace(spaces, "gone").id, defaultSpaceId);
  assert.equal(neighborSpace(spaces, "odd", 1).id, "default");
  assert.equal(neighborSpace(spaces, "default", -1).id, "odd");
  assert.equal(neighborSpace(spaces, "work", 1).id, "odd");
});

test("a session is listed in its own space, an older one in every space of its project, and the default space hides what belongs elsewhere", () => {
  const inSpace = (id: string, scopeKind: string | null, projectId: string | null, path: string | null, spaceId: string | null) => ({ ...session(id, scopeKind, projectId, path), spaceId });
  const snapshot: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
    projects: [project("a"), project("b"), project("c"), project("d")],
    sessions: [session("in-a", "project", "a", "C:\\code\\a"), session("in-b", "project", "b", "C:\\code\\b"), session("in-d", "project", "d", "C:\\code\\d"),
      session("chat", "global", null, null), inSpace("chat-work", "global", null, null, "work"), inSpace("chat-gone", "global", null, null, "gone"),
      inSpace("c-work", "project", "c", "C:\\code\\c", "work"), inSpace("a-personal", "project", "a", "C:\\code\\a", "personal"),
      inSpace("d-work", "project", "d", "C:\\code\\d", "work"), session("old-a", null, null, "C:\\code\\a"), session("lost", "project", "gone", "C:\\code\\gone"),
      session("nowhere", null, null, "C:\\other"), inSpace("b-default", "project", "b", "C:\\code\\b", "default")] };
  const spaces = readSpaces([item("default"), item("work", ["a", "c"]), item("personal", ["b"]), item("empty")]);
  const ids = (id: string, showOthers = false) => scopeSnapshot(snapshot, spaceView(spaces, id, showOthers)).sessions.map(value => value.id);
  const projects = (id: string, showOthers = false) => scopeSnapshot(snapshot, spaceView(spaces, id, showOthers)).projects.map(value => value.id);

  // Work: its projects; their older sessions, the sessions that record Work, and its own chats. A session never
  // becomes unreachable: one that records Work is listed there under its project, also when the project is not (or no
  // longer) in Work, and the project is then shown in Work with that session alone.
  assert.deepEqual(projects("work"), ["a", "c", "d"]);
  assert.deepEqual(ids("work"), ["in-a", "chat-work", "c-work", "d-work", "old-a"]);
  assert.deepEqual(projects("personal"), ["a", "b"]);
  assert.deepEqual(ids("personal"), ["in-b", "a-personal"]);
  // The default space: the projects of no other space, the chats of no other space, and what records it or a space
  // that is gone, also a session of a project that is in other spaces now.
  assert.deepEqual(projects("default"), ["b", "d"]);
  assert.deepEqual(ids("default"), ["in-d", "chat", "chat-gone", "lost", "nowhere", "b-default"]);
  // Asked to, it shows everything again: the catalog as it is, made again for nothing.
  assert.equal(scopeSnapshot(snapshot, spaceView(spaces, "default", true)), snapshot);
  assert.deepEqual(ids("empty"), []);
  // With no space but the default one, the default space is the whole catalog.
  assert.equal(scopeSnapshot(snapshot, spaceView(readSpaces([item("default")]), "default", false)), snapshot);

  assert.deepEqual(sessionSpaces(spaces, { spaceId: null, projectId: "a" }), ["work"]);
  assert.deepEqual(sessionSpaces(spaces, { spaceId: null, projectId: "d" }), ["default"]);
  assert.deepEqual(sessionSpaces(spaces, { spaceId: null, projectId: null }), ["default"]);
  assert.deepEqual(sessionSpaces(spaces, { spaceId: "personal", projectId: "a" }), ["personal"]);
  assert.deepEqual(sessionSpaces(spaces, { spaceId: "gone", projectId: "a" }), ["default"], "A space that is gone is read as the default one.");
  assert.equal(sessionHome(spaces, { spaceId: null, projectId: "b" }).id, "personal");
  assert.equal(viewLists(spaceView(spaces, "work", false), { spaceId: "work", projectId: "b" }), true, "A session that records Work is listed there, wherever its project is.");
  assert.equal(viewLists(spaceView(spaces, "work", false), { spaceId: "personal", projectId: "a" }), false);
  assert.equal(viewLists(spaceView(spaces, "default", false), { spaceId: "default", projectId: "b" }), true);
  assert.equal(viewLists(spaceView(spaces, "default", false), { spaceId: null, projectId: "b" }), false, "An older session of a project of another space is in that space.");
  assert.equal(viewLists(spaceView(spaces, "default", true), { spaceId: "work", projectId: "b" }), true);
  assert.notEqual(spaceViewKey(spaceView(spaces, "default", false)), spaceViewKey(spaceView(spaces, "default", true)));
  assert.equal(spaceViewKey(spaceView(spaces, "work", false)), spaceViewKey(spaceView(readSpaces([item("default"), item("work", ["a", "c"]), item("personal", ["b"]), item("empty")]), "work", false)));
  assert.deepEqual([...spaceChatCounts(snapshot.sessions)], [["work", 1], ["gone", 1]]);
});

test("each space keeps its tabs under its own key, and the page remembers the space it shows", () => {
  assert.equal(spaceStorageKey("codealta.desktop.sessionTabs.v1", "default"), "codealta.desktop.sessionTabs.v1", "The default space keeps the tabs the window had before spaces.");
  assert.equal(spaceStorageKey("codealta.desktop.sessionTabs.v1", "work"), "codealta.desktop.sessionTabs.v1.work");
  let stored: string | null = null;
  assert.equal(persistShownSpace(value => { stored = value; }, "open-source"), true);
  assert.equal(restoreShownSpace(() => stored), "open-source");
  for (const value of [null, "", "Not An Id", "x", "a".repeat(80)]) assert.equal(restoreShownSpace(() => value), "default");
  assert.equal(restoreShownSpace(() => { throw new Error("denied"); }), "default");
  assert.equal(persistShownSpace(() => { throw new Error("denied"); }, "work"), false);
});

test("what each space is doing is worked out from what the sessions do", () => {
  const spaces = readSpaces([item("default", ["a", "b", "c"]), item("work", ["a", "b"]), item("personal", ["b"]), item("quiet", ["c"])]);
  const sessions = [doing("s1", "a", { running: true }), doing("s2", "b", { waiting: true, running: true }), doing("s3", "b", { failed: true }),
    doing("s4", "a", { backgroundTasks: 2 }), doing("chat", null, { running: true }), doing("s5", "z", { waiting: true })];
  const activity = spaceActivities(spaces, sessions);
  // The default space counts what is listed there: the chat, and the session of a project of no other space.
  assert.deepEqual(activity.get("default"), { running: 1, background: 0, waiting: 1, failed: 0, attention: { sessionId: "s5", projectId: "z", title: "Title of s5" } });
  // A session created before sessions had a space counts in every space of its project; one that records its space, there alone.
  assert.equal(spaceActivities(spaces, [doing("chat-work", null, { running: true, spaceId: "work" })]).get("work")?.running, 1);
  assert.equal(spaceQuiet(spaceActivities(spaces, [doing("chat-work", null, { running: true, spaceId: "work" })]).get("default")), true);
  assert.deepEqual(activity.get("work"), { running: 2, background: 1, waiting: 1, failed: 1, attention: { sessionId: "s2", projectId: "b", title: "Title of s2" } });
  assert.deepEqual(activity.get("personal"), { running: 1, background: 0, waiting: 1, failed: 1, attention: { sessionId: "s2", projectId: "b", title: "Title of s2" } });
  assert.equal(spaceQuiet(activity.get("quiet")), true);
  assert.equal(spaceQuiet(activity.get("work")), false);
  assert.equal(spaceQuiet(undefined), true);
  // The session that waits is the one to open, before one that only failed.
  assert.equal(spaceActivities(spaces, [doing("f", "b", { failed: true }), doing("w", "b", { waiting: true })]).get("personal")?.attention?.sessionId, "w");
  assert.deepEqual(readSessionActivity({ ...doing("s", "a"), backgroundTasks: 500, title: "x".repeat(400) })?.backgroundTasks, 99);
  assert.equal(readSessionActivity({ ...doing("s", "a"), sessionId: "" }), null);
  assert.equal(readSessionActivity(null), null);
  // A session that waits names the sessions above it; what is not an id is left out, and a session that does not wait names none.
  assert.deepEqual(readSessionActivity({ ...doing("s", "a", { waiting: true }), ancestors: ["p", "", 7 as unknown as string, "g"] })?.ancestors, ["p", "g"]);
  assert.deepEqual(readSessionActivity({ ...doing("s", "a"), ancestors: ["p"] })?.ancestors, []);
  assert.deepEqual(readSessionActivity({ ...doing("s", "a", { waiting: true }), ancestors: undefined as unknown as string[] })?.ancestors, []);
});

test("the space that is shown has the sessions of its projects and the chats, and the default one has them all", () => {
  const spaces = readSpaces([item("default"), item("work", ["a"])]);
  assert.equal(spaceShows(spaces, "default", "b"), true);
  assert.equal(spaceShows(spaces, "work", "a"), true);
  assert.equal(spaceShows(spaces, "work", "b"), false);
  assert.equal(spaceShows(spaces, "work", null), true, "A chat is shown in every space.");
});

test("a session that needs the user is called out only where the user is not looking", () => {
  const spaces = readSpaces([item("default", ["a", "b", "c", "d"]), item("work", ["a", "b"]), item("personal", ["b", "c"])]);
  const sessions = [doing("in-a", "a", { waiting: true }), doing("in-c-failed", "c", { failed: true }), doing("in-c", "c", { waiting: true }), doing("in-d", "d", { failed: true }),
    doing("chat", null, { waiting: true }), doing("busy", "c", { running: true })];
  // From the default space that shows everything, everything is on screen: nothing is called out.
  assert.deepEqual(spaceCalls(spaces, "default", sessions, true), []);
  // Otherwise it shows what belongs to no other space: what waits in Work and Personal is called out.
  assert.deepEqual(spaceCalls(spaces, "default", sessions).map(call => [call.space.id, call.sessionId, call.waiting]), [["work", "in-a", true], ["personal", "in-c", true]]);
  // From Work: its own project is on screen; the chat is the default space's. What waits before what failed, one call for each space.
  assert.deepEqual(spaceCalls(spaces, "work", sessions).map(call => [call.space.id, call.sessionId, call.waiting]), [["default", "chat", true], ["personal", "in-c", true]]);
  assert.deepEqual(spaceCalls(spaces, "personal", sessions).map(call => [call.space.id, call.sessionId]), [["default", "chat"], ["work", "in-a"]]);
  assert.deepEqual(spaceCalls(spaces, "work", [doing("quiet", "c", { running: true })]), []);
});

test("what is asked of a project is shown where the user is only when the shown space has the project, and its space is offered otherwise", () => {
  const spaces = readSpaces([item("default", ["a", "b", "c", "old"]), item("work", ["a", "b"]), item("personal", ["b", "c"])]);
  const catalog = [project("a"), project("b"), project("c"), project("d"), project("old", true)];
  const shownIn = (id: string, showOthers: boolean) => scopeSnapshot({ configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false, projects: catalog, sessions: [] },
    spaceView(spaces, id, showOthers)).projects;
  const place = (shownId: string, projectId: string, showOthers = false) => {
    const found = placeProject(shownIn(shownId, showOthers), catalog, spaces, shownId, projectId);
    return found && [found.project.id, found.space?.id ?? null];
  };
  // `alta editor open` from a session of Work while the window shows Personal: nothing opens there, and Work is the space to show.
  assert.deepEqual(place("personal", "a"), ["a", "work"]);
  assert.deepEqual(place("work", "c"), ["c", "personal"]);
  // A project the shown space has is shown at once, in whatever other space it also is.
  assert.deepEqual(place("work", "a"), ["a", null]);
  assert.deepEqual(place("personal", "b"), ["b", null]);
  // A project of no space is in the default one. The default one sends a project of another space there, unless it shows everything.
  assert.deepEqual(place("work", "d"), ["d", "default"]);
  assert.deepEqual(place("default", "d"), ["d", null]);
  assert.deepEqual(place("default", "a"), ["a", "work"]);
  assert.deepEqual(place("default", "c"), ["c", "personal"]);
  for (const id of ["a", "c", "d"]) assert.deepEqual(place("default", id, true), [id, null]);
  // A space that is gone shows what the default one does.
  assert.deepEqual(place("gone", "a"), ["a", "work"]);
  // Not in this space is not the same as no such project: nothing is offered for a project the catalog does not have, or an archived one.
  assert.equal(place("work", "missing"), null);
  assert.equal(place("work", "old"), null);
  assert.equal(place("default", "old"), null);
  // The space offered is never the shown one: a project that just joined it, which the window does not list yet, is offered in another.
  assert.equal(placeProject([], catalog, spaces, "work", "a")?.space?.id, "default");
});

test("a name must be given, short enough, and not the name of another space", () => {
  const spaces = readSpaces([item("default"), item("work")]);
  assert.equal(spaceNameProblem("  ", spaces, null), "empty");
  assert.equal(spaceNameProblem("x".repeat(65), spaces, null), "long");
  assert.equal(spaceNameProblem(" work ", spaces, null), "taken");
  assert.equal(spaceNameProblem("DEFAULT", spaces, null), "taken");
  assert.equal(spaceNameProblem("Work", spaces, "work"), null, "A space keeps its own name.");
  assert.equal(spaceNameProblem("Personal", spaces, null), null);
  assert.deepEqual(spaceBrand({ icon: "briefcase", color: "#2d72d2" }), { icon: null, symbol: "briefcase", color: "#2d72d2" });
  assert.deepEqual(spaceBrand({ icon: "github", color: null }), { icon: "github", color: undefined });
  assert.deepEqual(spaceBrand({ icon: "no-such-icon", color: "red" }), { icon: null, symbol: undefined, color: undefined });
});

test("dropping a project on a space adds it, moves it or copies it, and dropping it on the list removes it", () => {
  const members = (id: string) => id === "work" ? ["a", "b"] : id === "personal" ? ["b"] : [];
  assert.deepEqual(dropChange({ projectId: "c", from: null }, "work", members, false), { join: ["work"], leave: [] });
  assert.equal(dropChange({ projectId: "a", from: null }, "work", members, false), null, "It is in that space already.");
  assert.deepEqual(dropChange({ projectId: "a", from: "work" }, "personal", members, false), { join: ["personal"], leave: ["work"] });
  assert.deepEqual(dropChange({ projectId: "a", from: "work" }, "personal", members, true), { join: ["personal"], leave: [] });
  // Moved onto a space that has it already: it only leaves the one it came from.
  assert.deepEqual(dropChange({ projectId: "b", from: "work" }, "personal", members, false), { join: [], leave: ["work"] });
  assert.equal(dropChange({ projectId: "b", from: "work" }, "personal", members, true), null);
  assert.equal(dropChange({ projectId: "a", from: "work" }, "work", members, false), null);
  assert.deepEqual(dropChange({ projectId: "a", from: "work" }, null, members, false), { join: [], leave: ["work"] });
  assert.equal(dropChange({ projectId: "a", from: null }, null, members, false), null);
  const spaces = readSpaces([item("default"), item("one"), item("two"), item("three")]);
  assert.deepEqual(movedOrder(spaces, "two", -1), ["two", "one", "three"]);
  assert.deepEqual(movedOrder(spaces, "two", 1), ["one", "three", "two"]);
  assert.equal(movedOrder(spaces, "one", -1), null);
  assert.equal(movedOrder(spaces, "three", 1), null);
  assert.equal(movedOrder(spaces, "default", 1), null);
});

test("the hub lists the spaces once, follows what the host says, and says what each space does only with more than one space", async () => {
  let listed = [item("default", ["a"])];
  let sessions = [doing("s1", "a", { running: true })];
  const calls: string[] = [];
  const events: ((event: { kind: string; spaceId: string | null }) => void)[] = [];
  const ok = async () => ({ status: "ok", message: null, space: null, movedSessions: 0 });
  const api: SpacesApi = {
    list: async () => { calls.push("list"); return { status: "ok", spaces: listed }; },
    activity: async () => { calls.push("activity"); return { status: "ok", sessions, truncated: false }; },
    watch: async () => { calls.push("watch"); return (async function* () { while (true) yield await new Promise<{ kind: string; spaceId: string | null }>(resolve => events.push(resolve)); })(); },
    create: async request => { calls.push(`create ${request.name}`); listed = [...listed, item("work", [...(request.projectIds ?? [])])]; return { status: "ok", message: null, space: item("work"), movedSessions: 0 }; },
    update: ok, delete: async () => ({ status: "invalid", message: "The default space holds every project and cannot be deleted.", space: null, movedSessions: 0 }),
    assign: async request => { calls.push(`assign ${request.projectId} +${request.join} -${request.leave}`); return ok(); }, reorder: ok,
    shown: async request => { calls.push(`shown ${request.id}`); return ok(); },
    assignments: async () => ({ status: "ok", providers: [], mcpServers: [{ key: "Atlassian", spaces: ["work"] }], plugins: [], skills: [], prompts: [] }),
    assignResource: async request => { calls.push(`assignResource ${request.kind} ${request.key} ${request.spaces}`); return ok(); },
    setAgentPrompt: async request => { calls.push(`setAgentPrompt ${request.id} ${request.agentPrompt}`); return ok(); },
    sessionResources: async () => ({ status: "ok", spaceId: "work", mcpServers: [], skills: [], prompts: [], plugins: [] }),
    setDisabledTools: async request => { calls.push(`setDisabledTools ${request.id} ${request.tools}`); return ok(); },
  };
  const timers: { run: () => void; milliseconds: number }[] = [];
  const hub = createSpacesHub(api, { set: (run, milliseconds) => { const timer = { run, milliseconds }; timers.push(timer); return timer; }, clear: timer => { timers.splice(timers.indexOf(timer as never), 1); } });
  const settle = async () => { for (let turn = 0; turn < 12; turn++) await Promise.resolve(); };
  assert.deepEqual(hub.getSnapshot().spaces, [defaultSpace]);
  assert.equal(hub.getSnapshot().loaded, false);
  const waited = hub.whenLoaded(5000);
  const requests: string[] = [];
  hub.onRequest((kind, id) => requests.push(`${kind} ${id}`));
  const disconnect = hub.connect("epoch");
  await waited;
  await settle();
  assert.deepEqual([hub.getSnapshot().loaded, hub.getSnapshot().available, hub.getSnapshot().spaces.map(space => space.id)], [true, true, ["default"]]);
  // With the default space alone the sessions are known, for the ones that wait for the user, and no space has an activity.
  assert.deepEqual(calls, ["list", "watch", "activity"]);
  await hub.refreshActivity();
  assert.deepEqual(hub.getSnapshot().sessions.map(session => session.sessionId), ["s1"]);
  assert.equal(hub.getSnapshot().activity.size, 0);

  const created = await hub.create("Work", null, "briefcase", null, ["a"]);
  assert.deepEqual([created.ok, created.space?.id], [true, "work"]);
  assert.deepEqual(hub.getSnapshot().spaces.map(space => [space.id, space.projectIds]), [["default", ["a"]], ["work", ["a"]]]);
  await hub.refreshActivity();
  assert.deepEqual(hub.getSnapshot().activity.get("work"), { running: 1, background: 0, waiting: 0, failed: 0, attention: null });
  // A reading that says the same changes nothing for those who follow the hub.
  let published = 0;
  const unsubscribe = hub.subscribe(() => { published++; });
  await hub.refreshActivity();
  await hub.refresh();
  assert.equal(published, 0);
  sessions = [doing("s1", "a", { waiting: true })];
  assert.equal(timers.some(timer => timer.milliseconds === activityMilliseconds), true);
  timers.find(timer => timer.milliseconds === activityMilliseconds)!.run();
  await settle();
  assert.equal(published, 1);
  assert.equal(hub.getSnapshot().activity.get("work")?.waiting, 1);

  // A command asks for a space, or changed the spaces: the hub lists again before it tells the page.
  listed = [...listed, item("personal")];
  events.shift()!({ kind: "show", spaceId: "personal" });
  await settle();
  assert.deepEqual(requests, ["show personal"]);
  assert.deepEqual(hub.getSnapshot().spaces.map(space => space.id), ["default", "work", "personal"]);
  events.shift()!({ kind: "changed", spaceId: null });
  await settle();
  assert.deepEqual(requests, ["show personal", "changed null"]);

  const refused = await hub.remove("default");
  assert.deepEqual([refused.ok, refused.status, refused.message], [false, "invalid", "The default space holds every project and cannot be deleted."]);
  await hub.assign("a", ["personal"], ["work"]);
  hub.shown("work");
  assert.equal(calls.includes("assign a +personal -work"), true);
  // The hub keeps the spaces of the resources with the spaces, and changes them through the host.
  assert.deepEqual(resourceSpaces(hub.getSnapshot().assignments, "mcp", "atlassian"), ["work"]);
  assert.equal((await hub.assignResource("skill", "terraform", ["work", "default"])).ok, true);
  assert.equal(calls.includes("assignResource skill terraform work,default"), true);
  assert.equal((await hub.setAgentPrompt("work", null)).ok, true);
  assert.equal(calls.includes("setAgentPrompt work null"), true);
  assert.equal((await hub.setDisabledTools("work", ["shell_command"])).ok, true);
  assert.equal(calls.includes("setDisabledTools work shell_command"), true);
  assert.equal((await hub.sessionResources("s1"))?.spaceId, "work");
  assert.equal(calls.includes("shown work"), true);
  unsubscribe();
  disconnect();
  assert.deepEqual(hub.getSnapshot().spaces, [defaultSpace]);
  assert.equal((await hub.create("Later", null, null, null)).status, "unavailable");
});

test("a host that keeps no spaces leaves the window with the default one, and nobody waits for more", async () => {
  const unavailable = async () => { throw new Error("no host"); };
  const hub = createSpacesHub({ list: async () => ({ status: "unavailable", spaces: [] }), activity: unavailable, watch: unavailable, create: unavailable, update: unavailable,
    delete: unavailable, assign: unavailable, reorder: unavailable, shown: unavailable, assignments: unavailable, assignResource: unavailable, setAgentPrompt: unavailable,
    sessionResources: unavailable, setDisabledTools: unavailable });
  let done = false;
  const waited = hub.whenLoaded(60_000).then(() => { done = true; });
  hub.unavailable();
  await waited;
  assert.equal(done, true);
  assert.deepEqual([hub.getSnapshot().loaded, hub.getSnapshot().available], [true, false]);
  const other = createSpacesHub({ list: async () => ({ status: "unavailable", spaces: [] }), activity: unavailable, watch: unavailable, create: unavailable, update: unavailable,
    delete: unavailable, assign: unavailable, reorder: unavailable, shown: unavailable, assignments: unavailable, assignResource: unavailable, setAgentPrompt: unavailable,
    sessionResources: unavailable, setDisabledTools: unavailable });
  const disconnect = other.connect("epoch");
  await other.whenLoaded(60_000);
  assert.deepEqual([other.getSnapshot().loaded, other.getSnapshot().available], [true, false]);
  disconnect();
});

test("the title bar shows the space as an icon while there is one, and by its name once there are several", () => {
  const never = () => assert.fail("rendering must not act");
  const one = readSpaces([item("default", ["a"])]);
  const alone = renderToStaticMarkup(<SpaceSwitch spaces={one} shownId="default" activity={new Map()} canEdit request={0} onShow={never} onCreate={never} onOrganize={never} />);
  assert.match(alone, /class="[^"]*space-switch[^"]*"[^>]*data-alone="true"/);
  assert.match(alone, /aria-label="Space: Default"/);
  assert.doesNotMatch(alone, /space-switch-name/);
  const spaces = readSpaces([item("default", ["a", "b"]), item("work", ["a"], { icon: "briefcase", color: "#2d72d2" }), item("personal", ["b"])]);
  const activity = spaceActivities(spaces, [doing("s", "b", { waiting: true })]);
  const named = renderToStaticMarkup(<SpaceSwitch spaces={spaces} shownId="work" activity={activity} canEdit request={0} onShow={never} onCreate={never} onOrganize={never} />);
  assert.match(named, /<span class="space-switch-name">Work<\/span>/);
  assert.doesNotMatch(named, /data-alone/);
  // A space that is not shown has a session that waits: the switch says so.
  assert.match(named, /data-attention="true"/);
  assert.doesNotMatch(renderToStaticMarkup(<SpaceSwitch spaces={spaces} shownId="personal" activity={activity} canEdit request={0} onShow={never} onCreate={never} onOrganize={never} />),
    /data-attention/, "What waits in the space that is shown is on screen already.");
});

test("the foot of the Explorer lists the spaces once there are several, with what needs the user elsewhere", () => {
  const never = () => assert.fail("rendering must not act");
  assert.equal(renderToStaticMarkup(<SpaceActivityBar spaces={readSpaces([item("default")])} shownId="default" activity={new Map()} calls={[]} onShow={never} onOpen={never} />), "");
  const spaces = readSpaces([item("default", ["a", "b"]), item("work", ["a"]), item("personal", ["b"])]);
  const sessions = [doing("s1", "b", { waiting: true }), doing("s2", "a", { running: true })];
  const activity = spaceActivities(spaces, sessions);
  const bar = renderToStaticMarkup(<SpaceActivityBar spaces={spaces} shownId="work" activity={activity} calls={spaceCalls(spaces, "work", sessions)} onShow={never} onOpen={never} />);
  assert.equal((bar.match(/class="space-chip"/g) ?? []).length, 3);
  assert.match(bar, /aria-pressed="true" data-activity="running" aria-label="Work: 1 session running"/);
  assert.match(bar, /aria-pressed="false" data-activity="waiting" aria-label="Personal: 1 session waits for you"/);
  assert.match(bar, /class="space-call" data-kind="waiting" title="Title of s1 waits for you in Personal"/);
  assert.equal((bar.match(/class="space-call"/g) ?? []).length, 1);
});

test("the sessions that wait are listed under every session above them, the nearest first", () => {
  const below = waitingSubSessions([
    doing("grandchild", "a", { waiting: true, ancestors: ["child", "root"] }),
    doing("child", "a", { running: true }),
    doing("other", "a", { waiting: true, ancestors: ["root"] }),
    doing("busy", "a", { running: true, ancestors: ["root"] }),
    doing("self", "a", { waiting: true, ancestors: ["self", "root"] }),
  ]);
  assert.deepEqual(below.get("child")?.map(session => session.sessionId), ["grandchild"]);
  assert.deepEqual(below.get("root")?.map(session => session.sessionId), ["other", "grandchild", "self"]);
  assert.deepEqual(below.get("root")?.[0], { sessionId: "other", title: "Title of other" });
  assert.equal(below.has("self"), false);
  assert.equal(below.has("busy"), false);
});

test("the spaces of a resource: none is every space, the default space only when listed, and one that leaves a shared resource lists the others", () => {
  const assignments = readAssignments({ status: "ok", providers: [], mcpServers: [{ key: "Atlassian", spaces: ["work", "work", "Not An Id", "default"] }],
    plugins: [{ key: "jira", spaces: [] }], skills: [], prompts: [{ key: "reviewer", spaces: ["work"] }] });
  assert.deepEqual(resourceSpaces(assignments, "mcp", "atlassian"), ["work", "default"], "keys without case; each space once; what is no space is left out");
  assert.deepEqual(resourceSpaces(assignments, "plugin", "jira"), [], "an empty list is every space");
  assert.equal(readAssignments({ status: "unavailable", providers: [], mcpServers: [], plugins: [], skills: [], prompts: [] }), noAssignments);
  assert.equal(inSpace([], "personal"), true);
  assert.equal(inSpace(["work"], "default"), false);
  assert.equal(inSpace(["work", "default"], "default"), true);
  assert.deepEqual(spacesWith([], "work"), [], "a shared resource stays shared");
  assert.deepEqual(spacesWith(["work"], "personal"), ["work", "personal"]);
  assert.deepEqual(spacesWithout([], "work", ["default", "work", "personal"]), ["default", "personal"]);
  assert.equal(spacesWithout(["work"], "work", ["default", "work"]), null, "no list says 'in no space'");
  // The popover of a chip: from every space, a space alone; the last one going leaves every space again.
  assert.deepEqual(toggledSpaces([], "work"), ["work"]);
  assert.deepEqual(toggledSpaces(["work"], "default"), ["work", "default"]);
  assert.deepEqual(toggledSpaces(["work", "default"], "work"), ["default"]);
  assert.deepEqual(toggledSpaces(["work"], "work"), []);
  const spaces = readSpaces([item("default"), item("work", [], { icon: "briefcase" })]);
  assert.deepEqual(spaceNames(spaces, ["gone", "work"]), ["Work", "gone"]);
  const all = renderToStaticMarkup(<SpaceScopeChip spaces={spaces} assigned={[]} />);
  assert.match(all, /data-all="true"/);
  assert.match(all, /All spaces/);
  assert.match(renderToStaticMarkup(<SpaceScopeChip spaces={spaces} assigned={["work"]} />), /class="space-scope-name">Work</);
  assert.match(renderToStaticMarkup(<SpaceScopeChip spaces={spaces} assigned={["work", "default"]} />), /aria-label="Default, Work"/);
});

test("a card of the overview opens the page of its space, which shows what it is, its projects, what is available there and its new sessions", () => {
  const never = () => assert.fail("rendering must not act");
  const spaces = readSpaces([item("default"), item("work", ["alpha"], { description: "The projects of my job.", agentPrompt: "reviewer" })]);
  const state = { loaded: true, available: true, spaces, sessions: [], activity: new Map(), assignments: noAssignments };
  const hub = { subscribe: () => () => { }, getSnapshot: () => state, hostEpoch: () => null } as unknown as SpacesHub;
  const projects = [project("alpha"), project("beta")];

  const overview = renderToStaticMarkup(<SpaceSettings hub={hub} spaces={spaces} projects={projects} chats={new Map()} shownId="default" activity={new Map()} canEdit showOthers={false}
    onShowOthers={never} onShow={never} onCreate={never} />);
  assert.match(overview, /class="space-open" title="Open Work"/);
  assert.match(overview, /class="space-open" title="Open Default"/);
  assert.match(overview, /The projects of my job\./);
  assert.doesNotMatch(overview, /space-name/, "A card is no longer where a space is named: its page is.");

  const page = renderToStaticMarkup(<SpacePage hub={hub} space={spaces[1]} spaces={spaces} projects={projects} chats={2} shown={false} activity={undefined} canEdit
    onBack={never} onShow={never} onDeleted={never} />);
  assert.match(page, /<h1>Work<\/h1>/);
  assert.match(page, />Spaces</, "Back leads to the spaces.");
  assert.match(page, /<h2[^>]*>General</);
  assert.match(page, /<h2[^>]*>Projects<span class="space-section-count">1</);
  assert.match(page, /value="Work"/);
  assert.match(page, />Delete</);
  assert.match(page, /<h2[^>]*>Available here</);
  for (const tab of ["MCP Servers", "Skills", "Agent prompts", "Plugins"]) assert.match(page, new RegExp(`>${tab}<`));
  assert.match(page, /<h2[^>]*>New sessions</);
  assert.match(page, /<option value="reviewer" selected="">reviewer<\/option>/, "The agent prompt of the space is shown while the prompts are read.");
  assert.match(page, /Model tiers/, "New sessions has the model tiers of the space.");
  assert.match(page, /<h2[^>]*>Tools</);
  assert.doesNotMatch(page, /Identity/, "Identity has nothing to show yet.");

  const pool = renderToStaticMarkup(<SpacePage hub={hub} space={spaces[0]} spaces={spaces} projects={projects} chats={0} shown activity={undefined} canEdit
    onBack={never} onShow={never} onDeleted={never} />);
  assert.match(pool, /Every project is in this space\./);
  assert.match(pool, /<h2[^>]*>Projects<span class="space-section-count">2</);
  assert.doesNotMatch(pool, />Delete</, "The default space cannot be deleted.");
  assert.match(pool, />Shown</);
});
