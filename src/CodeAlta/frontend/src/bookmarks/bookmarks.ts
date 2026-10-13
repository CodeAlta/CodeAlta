// What the window shows of the bookmarks: how one is named, how the manager groups and searches them, and which rows
// of a timeline they mark. Nothing here reads the document or asks the host.
import type { BookmarkRow } from "#neoastra";

export type Bookmark = BookmarkRow;
/** What the manager lists: everything, the bookmarks of messages or those of sessions. */
export type BookmarkFilter = "all" | "message" | "session";
export const bookmarkFilters: readonly BookmarkFilter[] = ["all", "message", "session"];

/** A project as the manager names it. */
export type BookmarkProject = Readonly<{ id: string; name: string }>;

/** The longest name a bookmark is given, as the host keeps it. */
export const maximumBookmarkLabel = 200;
const titleLength = 96;

/**
 * The beginning of a message as a line of plain text: its first line that says something, without the marks of
 * Markdown around it. It is a name to read, not the text: the message keeps its own.
 */
export function plainExcerpt(text: string | null | undefined): string {
  if (!text) return "";
  return text
    .replace(/```[\w-]*|~~~[\w-]*/g, " ")
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/(\*\*|__|~~|`)/g, "")
    // The marks that start a line: a heading, a quote, an item of a list.
    .replace(/^[ \t]*(#{1,6}|>+|[-*+]|\d+[.)])[ \t]+/gm, "")
    .replace(/\s+/g, " ").trim();
}

/** Where a text that is longer than `length` is cut to fit it with the mark that says it goes on; its own length when it fits. */
function cutAt(text: string, length: number): number {
  if (text.length <= length) return text.length;
  const last = text.charCodeAt(length - 2);
  // Never half of a pair of surrogates.
  const end = last >= 0xd800 && last <= 0xdbff ? length - 2 : length - 1;
  // Nor the beginning of a word, when a whole word ends close by.
  const space = /\s/.test(text[end]) ? end : text.lastIndexOf(" ", end);
  return space > end - 16 ? space : end;
}

function cut(text: string, length: number): string {
  const end = cutAt(text, length);
  return end === text.length ? text : text.slice(0, end).trimEnd() + "…";
}

/** The name a bookmark is listed under: the one the user gave, else the beginning of its message, else the title of its session. */
export function bookmarkTitle(bookmark: Bookmark): string {
  const label = bookmark.label?.trim();
  if (label) return label;
  const excerpt = bookmark.kind === "message" ? plainExcerpt(bookmark.excerpt) : "";
  return cut(excerpt || bookmark.sessionTitle || bookmark.sessionId, titleLength);
}

/**
 * What is said under the name of a bookmark of a message: the beginning of the message when the user gave another
 * name, and what follows the name when the name is the beginning of a longer message.
 */
export function bookmarkDetail(bookmark: Bookmark): string | null {
  if (bookmark.kind !== "message") return null;
  const excerpt = plainExcerpt(bookmark.excerpt);
  if (!excerpt) return null;
  if (bookmark.label?.trim()) return excerpt;
  const end = cutAt(excerpt, titleLength);
  return end === excerpt.length ? null : "…" + excerpt.slice(end).trimStart();
}

/** Whether every word of a search is in what the manager shows of a bookmark: its name, its message, its session, its project. */
export function matchesBookmark(bookmark: Bookmark, projectName: string, query: string): boolean {
  const terms = query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) return true;
  const text = `${bookmark.label ?? ""} ${bookmark.excerpt ?? ""} ${bookmark.sessionTitle} ${projectName}`.toLocaleLowerCase();
  return terms.every(term => text.includes(term));
}

/** The bookmarks of one session in the manager: the one of the session itself, and those of its messages. */
export type BookmarkSessionGroup = Readonly<{
  sessionId: string; title: string; missing: boolean;
  /** The bookmark of the whole session, when it has one. */
  session: Bookmark | null;
  /** The bookmarks of its messages, in the order of the conversation. */
  messages: readonly Bookmark[];
  /** When the newest of them was added: the sessions of a project are listed by it. */
  latest: string;
}>;
/** The bookmarks of one project; `project` is null for the chats, which have none. */
export type BookmarkProjectGroup = Readonly<{ projectId: string | null; name: string | null; sessions: readonly BookmarkSessionGroup[]; count: number; latest: string }>;

const byTime = (left: string | null | undefined, right: string | null | undefined) => (left ?? "") < (right ?? "") ? -1 : (left ?? "") > (right ?? "") ? 1 : 0;

/** The bookmarks of the messages of a session in the order of the conversation: by when each message was written. */
export function conversationOrder(bookmarks: readonly Bookmark[]): Bookmark[] {
  return [...bookmarks].sort((left, right) => byTime(left.messageAt, right.messageAt) || byTime(left.createdAt, right.createdAt) || byTime(left.id, right.id));
}

/**
 * The bookmarks as the manager shows them: by project, then by session. The project and the session with the newest
 * bookmark come first, so that what was just marked is at the top; inside a session the messages follow the conversation.
 * A project the window does not know keeps its place under its id, and the chats are a group of their own.
 */
export function groupBookmarks(bookmarks: readonly Bookmark[], projects: readonly BookmarkProject[], filter: BookmarkFilter = "all", query = ""): BookmarkProjectGroup[] {
  const names = new Map(projects.map(project => [project.id, project.name]));
  const nameOf = (id: string | null) => id === null ? "" : names.get(id) ?? id;
  const groups = new Map<string, Map<string, Bookmark[]>>();
  for (const bookmark of bookmarks) {
    if (filter !== "all" && bookmark.kind !== filter) continue;
    if (!matchesBookmark(bookmark, nameOf(bookmark.projectId ?? null), query)) continue;
    const project = bookmark.projectId ?? "";
    let sessions = groups.get(project);
    if (!sessions) groups.set(project, sessions = new Map());
    const rows = sessions.get(bookmark.sessionId);
    if (rows) rows.push(bookmark); else sessions.set(bookmark.sessionId, [bookmark]);
  }
  const result: BookmarkProjectGroup[] = [];
  for (const [project, sessions] of groups) {
    const rows: BookmarkSessionGroup[] = [];
    for (const [sessionId, held] of sessions) {
      const first = held[0];
      rows.push({ sessionId, title: first.sessionTitle || sessionId, missing: held.some(bookmark => bookmark.sessionMissing),
        session: held.find(bookmark => bookmark.kind === "session") ?? null,
        messages: conversationOrder(held.filter(bookmark => bookmark.kind === "message")),
        latest: held.reduce((latest, bookmark) => bookmark.createdAt > latest ? bookmark.createdAt : latest, "") });
    }
    rows.sort((left, right) => byTime(right.latest, left.latest) || byTime(left.sessionId, right.sessionId));
    result.push({ projectId: project || null, name: project ? nameOf(project) : null, sessions: rows,
      count: rows.reduce((count, row) => count + row.messages.length + (row.session ? 1 : 0), 0), latest: rows[0].latest });
  }
  return result.sort((left, right) => byTime(right.latest, left.latest) || byTime(left.projectId, right.projectId));
}

/** The bookmarks of one session: the one of the session itself, and those of its messages in the order of the conversation. */
export function sessionBookmarks(bookmarks: readonly Bookmark[], sessionId: string): Readonly<{ session: Bookmark | null; messages: readonly Bookmark[] }> {
  const held = bookmarks.filter(bookmark => bookmark.sessionId === sessionId);
  return { session: held.find(bookmark => bookmark.kind === "session") ?? null, messages: conversationOrder(held.filter(bookmark => bookmark.kind === "message")) };
}

/** A record of a timeline, as much of it as names a message. */
export type BookmarkableEntry = Readonly<{ offset: string; eventType: string; kind?: string | null; runId?: string | null; contentId?: string | null }>;

const messageKey = (runId: string | null | undefined, contentId: string | null | undefined) => runId && contentId ? `${runId}\n${contentId}` : null;

/**
 * The rows of a window that are bookmarked: the id of the bookmark of each, by the offset that keys the row. A message
 * is the same one wherever its journal holds it, so it is found by its turn and its id, never by the offset it had.
 */
export function bookmarkedRows(entries: readonly BookmarkableEntry[], messages: readonly Bookmark[]): ReadonlyMap<string, string> {
  const rows = new Map<string, string>();
  if (messages.length === 0) return rows;
  const wanted = new Map<string, string>();
  for (const bookmark of messages) { const key = messageKey(bookmark.runId, bookmark.contentId); if (key) wanted.set(key, bookmark.id); }
  if (wanted.size === 0) return rows;
  for (const entry of entries) {
    if (entry.eventType !== "contentCompleted") continue;
    const id = wanted.get(messageKey(entry.runId, entry.contentId) ?? "");
    if (id !== undefined) rows.set(entry.offset, id);
  }
  return rows;
}

/** Whether a row of the timeline can be bookmarked: a message of the user or of the assistant that the journal holds whole. */
export function canBookmark(item: Readonly<{ key: string; eventType: string; category: string }>): boolean {
  return (item.category === "user" || item.category === "assistant") && item.eventType === "contentCompleted" && /^\d+$/u.test(item.key);
}

/**
 * The bookmark a step leads to from where the reader is. `positions` says where each bookmark of the session is, in the
 * order of the conversation: before the rows in view (`-1`, which is also where a message older than the window is),
 * in view (`0`) or after them (`1`). Null when there is none that way.
 */
export function bookmarkStep<T>(bookmarks: readonly T[], positions: readonly (-1 | 0 | 1)[], direction: -1 | 1): T | null {
  if (direction === 1) {
    const index = positions.findIndex(position => position === 1);
    return index < 0 ? null : bookmarks[index];
  }
  for (let index = positions.length - 1; index >= 0; index--) if (positions[index] === -1) return bookmarks[index];
  return null;
}
