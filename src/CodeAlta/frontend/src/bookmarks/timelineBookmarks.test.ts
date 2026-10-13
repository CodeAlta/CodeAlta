import assert from "node:assert/strict";
import test from "node:test";
import type { Bookmark } from "./bookmarks";
import { createTimelineBookmarks } from "./timelineBookmarks";

const mark = (id: string, contentId: string): Bookmark => ({ id, kind: "message", sessionId: "s", projectId: null, sessionTitle: "S", sessionMissing: false, role: "assistant", label: null,
  excerpt: "text", runId: "run", contentId, offset: null, messageAt: "2026-10-09T11:00:00Z", createdAt: "2026-10-10T00:00:00Z", createdBy: null });
const entry = (offset: string, contentId: string) => ({ offset, eventType: "contentCompleted", kind: "Assistant", runId: "run", contentId });

test("the rows of the window follow the bookmarks of the session and the records that are loaded", () => {
  const toggled: [string, string | null][] = [];
  const marks = createTimelineBookmarks((rowKey, bookmarkId) => { toggled.push([rowKey, bookmarkId]); });
  let told = 0;
  marks.subscribe(() => { told++; });
  const empty = marks.rows();
  assert.equal(empty.size, 0);
  assert.equal(marks.bookmarkOf("10"), null);

  // The bookmarks are known before the history is read: nothing is marked yet.
  marks.setMessages([mark("b1", "m1"), mark("b2", "m2")]);
  assert.equal(marks.rows(), empty, "the same map while nothing changed");
  assert.equal(told, 0);

  const window = [entry("10", "m1"), entry("20", "m9")];
  marks.setEntries(window);
  assert.equal(marks.bookmarkOf("10"), "b1");
  assert.equal(marks.bookmarkOf("20"), null);
  assert.equal(told, 1);
  const held = marks.rows();

  // The same records again, or a list of bookmarks that marks the same rows, tells no one.
  marks.setEntries(window);
  marks.setMessages([mark("b1", "m1"), mark("b3", "m3")]);
  assert.equal(marks.rows(), held);
  assert.equal(told, 1);

  // Older history brings in the message of the other bookmark.
  marks.setMessages([mark("b1", "m1"), mark("b2", "m2")]);
  marks.setEntries([entry("4", "m2"), ...window]);
  assert.deepEqual([...marks.rows()], [["4", "b2"], ["10", "b1"]]);
  assert.equal(told, 2);

  // A row adds its bookmark, or removes the one it has.
  marks.toggle("20");
  marks.toggle("10");
  assert.deepEqual(toggled, [["20", null], ["10", "b1"]]);

  marks.setMessages([]);
  assert.equal(marks.rows().size, 0);
  assert.equal(told, 3);
});
