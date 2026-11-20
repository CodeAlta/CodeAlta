import type { WorkspaceRequest, WorkspaceWantedList, WorkspaceWantedSearch } from "#neoastra";
import { sessionTabsKey, restoreSessionTabs } from "./sessionTabs";

/**
 * What a window needs of the sessions beyond what every list shows at first. The host lists only the newest sessions of
 * each project, so that a large catalog stays quick to read; the window names the older ones it shows, and they come with
 * the list: a session is never out of reach because it is old.
 */
export type WorkspaceWants = Readonly<{
  /** The sessions shown whatever their age: those of the tabs, open or closed (a closed one is opened again), and the selected one. */
  sessions: readonly (string | null | undefined)[];
  /** The lists of the Explorer that are open, with the rows each shows. */
  lists: readonly WorkspaceWantedList[];
  /** The texts typed in the filter of the sidebar, the search of the window and the saved sessions. */
  searches: readonly (WorkspaceWantedSearch | null)[];
  /** Whether the archived sessions are shown. */
  archived: boolean;
  /** The space shown, or null when the sessions of every space are: what is asked for is counted and chosen in it. */
  space: string | null;
}>;

/** The request of a window that needs nothing more than the lists as they are at first. */
export const nothingWanted: WorkspaceRequest = { sessions: null, lists: null, searches: null, archived: false, space: null };

// The bounds of the host: what is beyond them would be left out there.
const mostSessions = 256, mostLists = 64, mostSearches = 4;

/**
 * The request that asks the host for what a window needs. What is asked for is in a fixed order and without
 * repetition, so that two requests for the same things are the same text (see `wantsKey`).
 */
export function wantedRequest(wants: WorkspaceWants): WorkspaceRequest {
  const sessions = [...new Set(wants.sessions.filter((id): id is string => !!id))].slice(0, mostSessions).sort();
  const lists = new Map<string | null, number>();
  for (const list of wants.lists) if (list.count > 0) lists.set(list.projectId, Math.max(lists.get(list.projectId) ?? 0, Math.floor(list.count)));
  const searches = new Map<string, WorkspaceWantedSearch>();
  for (const search of wants.searches) {
    const text = search?.text.trim().slice(0, 256) ?? "";
    if (!search || !text) continue;
    const value = { text, scoped: search.scoped, projectId: search.scoped ? search.projectId : null };
    searches.set(JSON.stringify(value), value);
  }
  return {
    sessions: sessions.length ? sessions : null,
    lists: lists.size ? [...lists].map(([projectId, count]) => ({ projectId, count }))
      .sort((left, right) => (left.projectId ?? "").localeCompare(right.projectId ?? "")).slice(0, mostLists) : null,
    searches: searches.size ? [...searches.keys()].sort().slice(0, mostSearches).map(key => searches.get(key)!) : null,
    archived: wants.archived,
    space: wants.space,
  };
}

/** A request with one more session, the one that is being opened. */
export function withWantedSession(request: WorkspaceRequest, sessionId: string | null): WorkspaceRequest {
  return !sessionId || request.sessions?.includes(sessionId) ? request : { ...request, sessions: [sessionId, ...request.sessions ?? []].slice(0, mostSessions) };
}

/** What tells two requests apart: the same key asks the host for the same sessions. */
export const wantsKey = (request: WorkspaceRequest): string => JSON.stringify([request.sessions, request.lists, request.searches, request.archived, request.space]);

/**
 * The sessions of the tabs the page keeps, of every space: a space that is shown again has its tabs back, whatever the
 * age of their sessions.
 */
export function storedTabSessions(storage: Pick<Storage, "length" | "key" | "getItem">): string[] {
  const ids: string[] = [];
  try {
    for (let index = 0; index < storage.length; index++) {
      const key = storage.key(index);
      if (key !== sessionTabsKey && !key?.startsWith(`${sessionTabsKey}.`)) continue;
      for (const tab of restoreSessionTabs(() => storage.getItem(key!))?.open ?? []) ids.push(tab.sessionId);
    }
  } catch { /* A storage that cannot be read keeps no tab. */ }
  return ids;
}
