import { closeDraftTab, closeSessionTab, draftKey, tabKey, type DraftTab, type SessionTab, type SessionTabs } from "./sessionTabs";
import { fileNodeId, fileTabKey, type FileTab, type FileTabs } from "./fileTabs";
import { draftNodeId, sessionDraftNodeId, sessionNodeId } from "./sessionTabLayout";

/** What a closing command of the menu of a tab closes: the other tabs of its pane, the ones after it, or all of them. */
export type TabCloseCommand = "others" | "right" | "all";
/** Tabs of the dock by what they show: sessions, prompts of new sessions that have a tab of their own, tabs of projects. */
export type DockTabs = Readonly<{ sessions: readonly SessionTab[]; drafts: readonly DraftTab[]; files: readonly FileTab[] }>;
/** One tab of the dock. */
export type DockTab = Readonly<{ session: SessionTab }> | Readonly<{ draft: DraftTab }> | Readonly<{ file: FileTab }>;

/**
 * The tabs of a pane that a command closes, in their order. `pane` lists the tabs of the pane as it shows them and
 * `tab` is the one the command was asked from; `all` needs none. The transient prompt of a new session is never among
 * them: it is what a pane shows when nothing else is, not a tab that was opened.
 */
export function tabsToClose(pane: readonly string[], tab: string | null, command: TabCloseCommand): string[] {
  const at = tab === null ? -1 : pane.indexOf(tab);
  const ids = command === "all" ? pane : at < 0 ? [] : command === "others" ? pane.filter(id => id !== tab) : pane.slice(at + 1);
  return ids.filter(id => id !== sessionDraftNodeId);
}

/** The tab of the dock a node shows, or null for the transient prompt and for a node the window does not know. */
export function dockTab(id: string, state: SessionTabs, files: FileTabs): DockTab | null {
  const session = state.open.find(tab => sessionNodeId(tab) === id);
  if (session) return { session };
  const draft = state.drafts?.find(value => draftNodeId(value) === id);
  if (draft) return { draft };
  const file = files.open.find(value => fileNodeId(value) === id);
  return file ? { file } : null;
}

/** The tabs that nodes show, by what they are. */
export function dockTabs(ids: readonly string[], state: SessionTabs, files: FileTabs): DockTabs {
  const tabs = ids.map(id => dockTab(id, state, files));
  return { sessions: tabs.flatMap(tab => tab && "session" in tab ? [tab.session] : []),
    drafts: tabs.flatMap(tab => tab && "draft" in tab ? [tab.draft] : []),
    files: tabs.flatMap(tab => tab && "file" in tab ? [tab.file] : []) };
}

/**
 * Closes several session tabs and prompts at once. `show` is the session shown afterwards, when it is one that stays
 * open; without it the session that was shown still is, or the first that is left when it closed.
 */
export function closeSessionTabs(state: SessionTabs, sessions: readonly SessionTab[], drafts: readonly DraftTab[] = [], show: SessionTab | null = null): SessionTabs {
  let next = state;
  for (const tab of sessions) next = closeSessionTab(next, tab);
  for (const draft of drafts) next = closeDraftTab(next, draft);
  const shown = show ? next.open.find(tab => tabKey(tab) === tabKey(show)) : undefined;
  return shown && (!next.active || tabKey(next.active) !== tabKey(shown)) ? { ...next, active: shown } : next;
}

/**
 * What a closing command leaves in front of the window. `front` is what is there now: a tab of a project, else the
 * session shown, else the prompt of a new session of `scope`. When the command closes it, the tab it was asked from
 * (`from`) comes in front; null when what is in front stays, or when nothing was named to take its place.
 */
export function shownAfterClosing(closing: DockTabs, front: Readonly<{ file: FileTab | null; session: SessionTab | null; scope: DraftTab | null }>, from: DockTab | null): DockTab | null {
  const gone = front.file ? closing.files.some(file => fileTabKey(file) === fileTabKey(front.file!))
    : front.session ? closing.sessions.some(tab => tabKey(tab) === tabKey(front.session!))
    : !!front.scope && closing.drafts.some(draft => draftKey(draft) === draftKey(front.scope!));
  return gone ? from : null;
}
