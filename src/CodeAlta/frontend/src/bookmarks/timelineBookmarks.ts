// The bookmarks of the messages of one timeline: which rows of the window that is loaded are bookmarked. The pane of the
// session gives it the bookmarks of the session, the history gives it the records of its window, and each row follows
// its own mark: adding a bookmark renders one row again, not the timeline.
import { createContext, useContext, useSyncExternalStore } from "react";
import { bookmarkedRows, type Bookmark, type BookmarkableEntry } from "./bookmarks";

const none: ReadonlyMap<string, string> = new Map();

/**
 * @param toggle Adds the bookmark of the message a row shows, given the offset that keys the row, or removes the
 *               bookmark it has, given its id.
 */
export function createTimelineBookmarks(toggle: (rowKey: string, bookmarkId: string | null) => void) {
  let messages: readonly Bookmark[] = [], entries: readonly BookmarkableEntry[] = [];
  let rows = none;
  const listeners = new Set<() => void>();
  function update() {
    const next = bookmarkedRows(entries, messages);
    if (next.size === rows.size && [...next].every(([key, id]) => rows.get(key) === id)) return;
    rows = next.size ? next : none;
    for (const listener of [...listeners]) listener();
  }
  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    /** The bookmarked rows of the window: the id of the bookmark of each, by the key of the row. The same map until one changes. */
    rows: () => rows,
    /** The id of the bookmark of a row; null when it has none. */
    bookmarkOf: (rowKey: string): string | null => rows.get(rowKey) ?? null,
    /** The bookmarks of the messages of the session, as the host lists them. */
    setMessages(next: readonly Bookmark[]) { if (next !== messages) { messages = next; update(); } },
    /** The records of the window of the history. */
    setEntries(next: readonly BookmarkableEntry[]) { if (next !== entries) { entries = next; update(); } },
    toggle: (rowKey: string) => toggle(rowKey, rows.get(rowKey) ?? null),
  };
}

export type TimelineBookmarks = ReturnType<typeof createTimelineBookmarks>;

/** The bookmarks of the timeline a row is in; null where messages cannot be bookmarked (a fixture, a host that keeps none). */
export const TimelineBookmarksContext = createContext<TimelineBookmarks | null>(null);

const noSubscription = () => () => { };

/** The id of the bookmark of a row, followed: null when the row has none or cannot have one here. */
export function useRowBookmark(rowKey: string | null): Readonly<{ marks: TimelineBookmarks | null; bookmarkId: string | null }> {
  const marks = useContext(TimelineBookmarksContext);
  const read = () => marks && rowKey !== null ? marks.bookmarkOf(rowKey) : null;
  const bookmarkId = useSyncExternalStore(marks?.subscribe ?? noSubscription, read, read);
  return { marks, bookmarkId };
}
