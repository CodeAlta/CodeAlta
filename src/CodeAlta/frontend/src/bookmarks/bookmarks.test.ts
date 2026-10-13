import assert from "node:assert/strict";
import test from "node:test";
import { bookmarkDetail, bookmarkedRows, bookmarkStep, bookmarkTitle, canBookmark, conversationOrder, groupBookmarks, matchesBookmark, plainExcerpt, sessionBookmarks, type Bookmark } from "./bookmarks";

let serial = 0;
function bookmark(values: Partial<Bookmark> = {}): Bookmark {
  const id = values.id ?? `b${++serial}`;
  return { id, kind: "message", sessionId: "s1", projectId: "p1", sessionTitle: "Session one", sessionMissing: false, role: "assistant", label: null,
    excerpt: "The answer.", runId: "run", contentId: `content-${id}`, offset: "100", messageAt: "2026-10-09T11:54:00Z", createdAt: "2026-10-10T08:00:00Z", createdBy: null, ...values };
}

test("the beginning of a message is read as a line of plain text, without the marks of Markdown", () => {
  assert.equal(plainExcerpt("**Les deux entrées sont implémentées** et `cochées`."), "Les deux entrées sont implémentées et cochées.");
  assert.equal(plainExcerpt("## Plan\n\n- first\n- second"), "Plan first second");
  assert.equal(plainExcerpt("> quoted\n1. one\n2) two"), "quoted one two");
  assert.equal(plainExcerpt("See [the guide](https://example.test/a) and ![a picture](p.png)."), "See the guide and a picture.");
  assert.equal(plainExcerpt("```ts\nconst a = 1;\n```"), "const a = 1;");
  assert.equal(plainExcerpt("a - b * c"), "a - b * c", "marks inside a line are text");
  assert.equal(plainExcerpt("   \n\t "), "");
  assert.equal(plainExcerpt(null), "");
});

test("a bookmark is named by the user, else by the beginning of its message, else by its session", () => {
  assert.equal(bookmarkTitle(bookmark({ label: "  The decision  " })), "The decision");
  assert.equal(bookmarkTitle(bookmark({ excerpt: "**Done**: both entries." })), "Done: both entries.");
  assert.equal(bookmarkTitle(bookmark({ excerpt: null })), "Session one");
  assert.equal(bookmarkTitle(bookmark({ kind: "session", role: null, excerpt: "ignored" })), "Session one");
  assert.equal(bookmarkTitle(bookmark({ kind: "session", sessionTitle: "", sessionId: "the-id" })), "the-id");
  const long = bookmarkTitle(bookmark({ excerpt: "word ".repeat(60) }));
  assert.ok(long.length <= 96 && long.endsWith("…"), long);
  // Never half of a pair of surrogates at the cut.
  const pairs = bookmarkTitle(bookmark({ excerpt: "😀".repeat(80) }));
  assert.ok(!/[\ud800-\udbff]…$/u.test(pairs) && !/[\ud800-\udbff]$/.test(pairs.slice(0, -1)), pairs);
});

test("the message is said under the name only when the name is not it already", () => {
  assert.equal(bookmarkDetail(bookmark({ excerpt: "Short." })), null);
  assert.equal(bookmarkDetail(bookmark({ label: "Mine", excerpt: "**Short.**" })), "Short.");
  // A long message goes on under its name, from where the name was cut.
  const long = bookmark({ excerpt: Array.from({ length: 40 }, (_, index) => `word${index}`).join(" ") });
  const name = bookmarkTitle(long), rest = bookmarkDetail(long)!;
  assert.ok(name.endsWith("…") && rest.startsWith("…"), `${name} | ${rest}`);
  assert.equal(`${name.slice(0, -1)} ${rest.slice(1)}`, long.excerpt);
  assert.equal(bookmarkDetail(bookmark({ kind: "session", label: "Mine" })), null);
});

test("a search finds every word in the name, the message, the session or the project", () => {
  const row = bookmark({ label: "Release notes", excerpt: "The tag triggers the build.", sessionTitle: "Ship 1.8" });
  for (const query of ["", "  ", "release", "TAG build", "ship notes", "codealta tag"]) assert.equal(matchesBookmark(row, "CodeAlta", query), true, query);
  for (const query of ["missing", "release missing"]) assert.equal(matchesBookmark(row, "CodeAlta", query), false, query);
});

test("the messages of a session follow the conversation, whenever they were bookmarked", () => {
  const rows = [bookmark({ id: "late", messageAt: "2026-10-09T12:00:00Z", createdAt: "2026-10-01T00:00:00Z" }),
    bookmark({ id: "early", messageAt: "2026-10-09T10:00:00Z", createdAt: "2026-10-11T00:00:00Z" }),
    bookmark({ id: "same-b", messageAt: "2026-10-09T11:00:00Z", createdAt: "2026-10-05T00:00:00Z" }),
    bookmark({ id: "same-a", messageAt: "2026-10-09T11:00:00Z", createdAt: "2026-10-04T00:00:00Z" })];
  assert.deepEqual(conversationOrder(rows).map(row => row.id), ["early", "same-a", "same-b", "late"]);
  assert.deepEqual(rows.map(row => row.id), ["late", "early", "same-b", "same-a"], "the list it was given is left as it was");
});

test("the manager groups by project and session, the newest bookmark first, and names the projects", () => {
  const rows = [
    bookmark({ id: "a1", sessionId: "a", projectId: "alpha", sessionTitle: "Alpha session", createdAt: "2026-10-02T00:00:00Z", messageAt: "2026-10-01T10:00:00Z" }),
    bookmark({ id: "a2", sessionId: "a", projectId: "alpha", sessionTitle: "Alpha session", createdAt: "2026-10-03T00:00:00Z", messageAt: "2026-10-01T09:00:00Z" }),
    bookmark({ id: "a0", kind: "session", role: null, excerpt: null, sessionId: "a", projectId: "alpha", sessionTitle: "Alpha session", createdAt: "2026-10-01T00:00:00Z", messageAt: null }),
    bookmark({ id: "b1", sessionId: "b", projectId: "alpha", sessionTitle: "Other alpha", createdAt: "2026-10-09T00:00:00Z" }),
    bookmark({ id: "c1", sessionId: "c", projectId: null, sessionTitle: "A chat", createdAt: "2026-10-12T00:00:00Z" }),
    bookmark({ id: "d1", sessionId: "d", projectId: "gone", sessionTitle: "Kept", sessionMissing: true, createdAt: "2026-09-01T00:00:00Z" }),
  ];
  const groups = groupBookmarks(rows, [{ id: "alpha", name: "Alpha" }]);
  assert.deepEqual(groups.map(group => [group.projectId, group.name, group.count]), [[null, null, 1], ["alpha", "Alpha", 4], ["gone", "gone", 1]]);
  const alpha = groups[1];
  assert.deepEqual(alpha.sessions.map(session => session.sessionId), ["b", "a"], "the session with the newest bookmark first");
  assert.equal(alpha.sessions[1].session?.id, "a0");
  assert.deepEqual(alpha.sessions[1].messages.map(row => row.id), ["a2", "a1"], "in the order of the conversation");
  assert.equal(groups[2].sessions[0].missing, true);

  // A filter and a search narrow it; a group with nothing left is not listed.
  assert.deepEqual(groupBookmarks(rows, [], "session").map(group => [group.projectId, group.count]), [["alpha", 1]]);
  assert.deepEqual(groupBookmarks(rows, [], "message").map(group => group.count), [1, 3, 1]);
  assert.deepEqual(groupBookmarks(rows, [{ id: "alpha", name: "Alpha" }], "all", "other").map(group => group.sessions.map(session => session.sessionId)), [["b"]]);
  assert.deepEqual(groupBookmarks(rows, [{ id: "alpha", name: "Zeta" }], "all", "zeta").map(group => group.count), [4], "the name of the project is searched");
  assert.deepEqual(groupBookmarks(rows, [], "all", "nothing"), []);
  assert.deepEqual(groupBookmarks([], []), []);
});

test("the bookmarks of one session: the one of the session, and those of its messages", () => {
  const rows = [bookmark({ id: "m2", sessionId: "s", messageAt: "2026-10-09T12:00:00Z" }), bookmark({ id: "other", sessionId: "t" }),
    bookmark({ id: "whole", sessionId: "s", kind: "session", role: null }), bookmark({ id: "m1", sessionId: "s", messageAt: "2026-10-09T11:00:00Z" })];
  const held = sessionBookmarks(rows, "s");
  assert.equal(held.session?.id, "whole");
  assert.deepEqual(held.messages.map(row => row.id), ["m1", "m2"]);
  assert.deepEqual(sessionBookmarks(rows, "none"), { session: null, messages: [] });
});

test("a row is bookmarked when its record is the message of a bookmark, wherever the journal holds it", () => {
  const entries = [
    { offset: "10", eventType: "contentCompleted", kind: "User", runId: "r1", contentId: "user:1" },
    { offset: "20", eventType: "contentDelta", kind: "Assistant", runId: "r1", contentId: "msg:1" },
    { offset: "30", eventType: "contentCompleted", kind: "Assistant", runId: "r1", contentId: "msg:1" },
    { offset: "40", eventType: "activity", kind: "Tool", runId: "r1", contentId: null },
    { offset: "50", eventType: "contentCompleted", kind: "Assistant", runId: "r2", contentId: "msg:1" },
  ];
  // The offset a bookmark kept is where its message was: the records moved since.
  const marks = [bookmark({ id: "answer", runId: "r1", contentId: "msg:1", offset: "999" }), bookmark({ id: "prompt", runId: "r1", contentId: "user:1", offset: "10" }),
    bookmark({ id: "elsewhere", runId: "r9", contentId: "msg:1" }), bookmark({ id: "unnamed", runId: null, contentId: null })];
  assert.deepEqual([...bookmarkedRows(entries, marks)], [["10", "prompt"], ["30", "answer"]]);
  assert.equal(bookmarkedRows(entries, []).size, 0);
  assert.equal(bookmarkedRows([], marks).size, 0);
});

test("a message the journal holds whole can be bookmarked; a row that is being written or is no message cannot", () => {
  assert.equal(canBookmark({ key: "1200", eventType: "contentCompleted", category: "user" }), true);
  assert.equal(canBookmark({ key: "1200", eventType: "contentCompleted", category: "assistant" }), true);
  assert.equal(canBookmark({ key: "1200", eventType: "contentDelta", category: "assistant" }), false);
  assert.equal(canBookmark({ key: "outgoing:abc", eventType: "contentCompleted", category: "user" }), false);
  assert.equal(canBookmark({ key: "1200", eventType: "contentCompleted", category: "reasoning" }), false);
  assert.equal(canBookmark({ key: "1200", eventType: "activity", category: "tool" }), false);
});

test("a step goes to the nearest bookmark before or after the rows in view", () => {
  const rows = ["a", "b", "c", "d", "e"];
  assert.equal(bookmarkStep(rows, [-1, -1, 0, 1, 1], 1), "d");
  assert.equal(bookmarkStep(rows, [-1, -1, 0, 1, 1], -1), "b");
  assert.equal(bookmarkStep(rows, [-1, -1, -1, -1, -1], 1), null, "nothing after the last one");
  assert.equal(bookmarkStep(rows, [-1, -1, -1, -1, -1], -1), "e");
  assert.equal(bookmarkStep(rows, [0, 0, 1, 1, 1], -1), null, "nothing before the first one");
  assert.equal(bookmarkStep([], [], 1), null);
});
