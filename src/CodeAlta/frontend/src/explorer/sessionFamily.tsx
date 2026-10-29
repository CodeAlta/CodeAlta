import type { WorkspaceSession } from "#neoastra";
import { createContext, useContext, type PointerEvent, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { plainTitle } from "../sessionTitle";
import { useShellLanguage } from "../shellLanguage";
import type { SessionListEntry } from "./sessionTree";
import { sessionRowIndent } from "./ExplorerSessions";

/**
 * What the Explorer knows of the family of a session listed in another scope than its parent or its children: the
 * name of that scope, how that session is opened, and the marks of its row.
 */
export type SessionFamily = Readonly<{
  /** The name of the project of a session, or of the chats. */
  scopeName: (session: WorkspaceSession) => string;
  /** Opens a session in its own scope, which is selected. */
  open: (session: WorkspaceSession) => void;
  /** What follows the title of the row of a session in its own list. */
  marks: (session: WorkspaceSession) => ReactNode;
}>;

/** Given by the window. Without it (a fixture) the links and the origins of sessions are not shown. */
export const SessionFamilyContext = createContext<SessionFamily | null>(null);

/**
 * The link, under a session, to one of its sub-agents that another project or the chats list: its arrow says so, its
 * tooltip names that scope, it shows the marks of the sub-agent, and it opens it there.
 */
export function SessionLinkRow({ entry }: { entry: Extract<SessionListEntry, { kind: "link" }> }) {
  const { t } = useShellLanguage();
  const family = useContext(SessionFamilyContext);
  if (!family) return null;
  const { session, parentId, depth } = entry;
  const scope = family.scopeName(session);
  const label = t("Open {title}, listed in {scope}", { title: plainTitle(session.title), scope });
  return <div className="session-row session-link-row" data-link-id={session.id} data-parent-id={parentId}>
    <button type="button" aria-pressed={false} title={label} aria-label={label} data-full-text={plainTitle(session.fullTitle || session.title)}
      style={{ paddingLeft: sessionRowIndent(depth) }} onClick={() => family.open(session)}>
      <span className="session-icon" data-file-tone="teal"><AppIcon name="openExternal" size={12} /></span>
      <span className="session-title">{plainTitle(session.title)}</span>
      {family.marks(session)}
    </button>
  </div>;
}

/**
 * The line under a session that names the session that started it, when that one is listed in another project or
 * among the chats: it opens it.
 */
export function SessionOriginLine({ parent, depth }: { parent: WorkspaceSession | undefined; depth: number }) {
  const { t } = useShellLanguage();
  const family = useContext(SessionFamilyContext);
  if (!family || !parent) return null;
  const scope = family.scopeName(parent);
  const title = plainTitle(parent.title);
  const content = <><AppIcon name="arrowUp" size={11} /><span className="session-origin-scope">{scope}</span><span aria-hidden="true">›</span>
    <span className="session-origin-title">{title}</span></>;
  const style = { paddingLeft: sessionRowIndent(depth) + 19 };
  // An archived parent is not listed: it is named, and opens nothing.
  if (parent.archived) {
    const label = t("Started by {title}, archived, in {scope}", { title, scope });
    return <span className="session-origin" data-archived="" title={label} style={style}>{content}</span>;
  }
  const label = t("Started by {title}, in {scope}", { title, scope });
  return <button type="button" className="session-origin" title={label} aria-label={label} style={style}
    onClick={event => { event.stopPropagation(); family.open(parent); }}>{content}</button>;
}

/** The sessions related to a session in the Explorer, and the scopes they are listed in: its parent and its children, with itself. */
export function sessionFamilyOf(sessions: readonly WorkspaceSession[], id: string): { ids: Set<string>; scopes: Map<string, string> } {
  const ids = new Set<string>();
  const scopes = new Map<string, string>();
  const self = sessions.find(session => session.id === id);
  if (!self) return { ids, scopes };
  const add = (session: WorkspaceSession) => {
    if (session.archived) return;
    ids.add(session.id);
    scopes.set(session.id, session.scopeKind === "project" ? session.projectId ?? "" : "");
  };
  const parent = self.parentSessionId ? sessions.find(session => session.id === self.parentSessionId) : undefined;
  if (parent) add(parent);
  for (const session of sessions) if (session.parentSessionId === id) add(session);
  if (ids.size === 0) return { ids, scopes };
  add(self);
  return { ids, scopes };
}

const familyAttribute = "data-family";

/** Removes the marks that `markSessionFamily` left. */
export function clearSessionFamily(container: Element) {
  for (const marked of Array.from(container.querySelectorAll(`[${familyAttribute}]`))) marked.removeAttribute(familyAttribute);
}

/**
 * Marks, in the Explorer, the rows related to the session of a row: its parent, its children, and the other rows of
 * the same session (its link under its parent). A project whose rows of them are not shown (it is closed) is marked
 * instead. Returns the id of the session, or null for a row of no session.
 */
export function markSessionFamily(container: Element, sessions: readonly WorkspaceSession[], row: Element): string | null {
  clearSessionFamily(container);
  const id = row.getAttribute("data-family-id") ?? row.getAttribute("data-link-id");
  if (!id) return null;
  const { ids, scopes } = sessionFamilyOf(sessions, id);
  for (const related of ids) {
    const key = CSS.escape(related);
    for (const value of Array.from(container.querySelectorAll(`.session-row[data-family-id="${key}"], .session-link-row[data-link-id="${key}"]`)))
      if (value !== row) value.setAttribute(familyAttribute, "");
    // Its own row, in its own list: a link under its parent does not say where it is.
    const own = Array.from(container.querySelectorAll<HTMLElement>(`.session-row[data-family-id="${key}"]`));
    if (related !== id && !own.some(value => value.offsetParent !== null))
      container.querySelector(`.project-action-row > button[data-scope="${CSS.escape(scopes.get(related) ?? "")}"]`)?.setAttribute(familyAttribute, "");
  }
  return id;
}

/** The handlers that mark the family of the session row under the pointer, in the element that holds the Explorer. */
export function sessionFamilyHover(sessions: () => readonly WorkspaceSession[]) {
  let shown: Element | null = null;
  return {
    onPointerOver: (event: PointerEvent<HTMLElement>) => {
      const row = (event.target as Element).closest(".session-row");
      if (row === shown) return;
      shown = row;
      if (row) markSessionFamily(event.currentTarget, sessions(), row);
      else clearSessionFamily(event.currentTarget);
    },
    onPointerLeave: (event: PointerEvent<HTMLElement>) => { shown = null; clearSessionFamily(event.currentTarget); },
  };
}
