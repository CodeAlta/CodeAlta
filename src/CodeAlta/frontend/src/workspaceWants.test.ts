import assert from "node:assert/strict";
import test from "node:test";
import { sessionTabsKey } from "./sessionTabs";
import { nothingWanted, storedTabSessions, wantedRequest, wantsKey, withWantedSession } from "./workspaceWants";

test("a window that shows nothing old asks for nothing", () => {
  assert.deepEqual(wantedRequest({ sessions: [null, undefined, ""], lists: [{ projectId: "p", count: 0 }], searches: [null, { text: "  ", scoped: false, projectId: null }], archived: false, space: null }), nothingWanted);
});

test("what a window needs is asked once, in a fixed order", () => {
  const request = wantedRequest({
    sessions: ["b", "a", "b", null],
    lists: [{ projectId: "p", count: 10 }, { projectId: null, count: 4 }, { projectId: "p", count: 30 }],
    searches: [{ text: " old  bug ", scoped: false, projectId: "ignored" }, { text: "old  bug", scoped: false, projectId: null },
      { text: "plan", scoped: true, projectId: "p" }],
    archived: true, space: "work",
  });
  assert.deepEqual(request.sessions, ["a", "b"]);
  // A list asked for twice is asked for its longest: the saved sessions show more rows of it than the Explorer.
  assert.deepEqual(request.lists, [{ projectId: null, count: 4 }, { projectId: "p", count: 30 }]);
  // A search of every list names no project.
  assert.deepEqual(request.searches, [{ text: "old  bug", scoped: false, projectId: null }, { text: "plan", scoped: true, projectId: "p" }]);
  assert.equal(request.archived, true);
  const same = wantedRequest({ sessions: ["a", "b"], lists: [{ projectId: "p", count: 30 }, { projectId: null, count: 4 }],
    searches: [{ text: "plan", scoped: true, projectId: "p" }, { text: "old  bug", scoped: false, projectId: null }], archived: true, space: "work" });
  assert.equal(wantsKey(same), wantsKey(request));
  // Another space shown is another request: its rows are counted and chosen there.
  assert.notEqual(wantsKey({ ...same, space: null }), wantsKey(request));
  assert.notEqual(wantsKey(nothingWanted), wantsKey(request));
});

test("the session that is being opened is asked for with the others", () => {
  const request = wantedRequest({ sessions: ["a"], lists: [], searches: [], archived: false, space: null });
  assert.deepEqual(withWantedSession(request, "old").sessions, ["old", "a"]);
  assert.equal(withWantedSession(request, "a"), request);
  assert.equal(withWantedSession(request, null), request);
  assert.deepEqual(withWantedSession(nothingWanted, "old").sessions, ["old"]);
});

test("the tabs of every space are read from what the page keeps", () => {
  const tabs = (id: string) => JSON.stringify({ version: 1, open: [{ sessionId: id, projectId: null, path: "/chats" }], active: null, closed: [] });
  const items = new Map([[sessionTabsKey, tabs("default-tab")], [`${sessionTabsKey}.work`, tabs("work-tab")], [`${sessionTabsKey}x`, tabs("other")], ["unrelated", "{"],
    [`${sessionTabsKey}.broken`, "{"]]);
  const storage = { length: items.size, key: (index: number) => [...items.keys()][index] ?? null, getItem: (key: string) => items.get(key) ?? null };
  assert.deepEqual(storedTabSessions(storage).sort(), ["default-tab", "work-tab"]);
  assert.deepEqual(storedTabSessions({ length: 1, key: () => { throw new Error("denied"); }, getItem: () => null }), []);
});
