import { SubAgentBadge } from "../SessionReference";
import type { WorkspaceSession } from "#neoastra";
import { Fragment, useContext, useId, useState, type KeyboardEvent, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { usePluginMenuEntries } from "../pluginButtons/pluginMenu";
import { ProviderBrandsContext, ProviderIcon, useProviderBrand } from "../ProviderIcon";
import { SessionTabMenu, type SessionMenuEntry } from "../SessionTabMenu";
import type { SessionHierarchyRow } from "../sessionHierarchy";
import { isSessionContextKey, isSessionDeleteKey, sessionRowShortcut, type SessionAction } from "../sessionRowActions";
import { KeyGesture } from "../KeyGesture";
import { plainTitle } from "../sessionTitle";
import { providerHasRemoteControl } from "../remoteControl";
import { openElsewhereEntries } from "../claudeCodeOpen/openElsewhereEntries";
import { useShellLanguage } from "../shellLanguage";
import { sessionEntries, type SessionListEntry } from "./sessionTree";
import type { SessionRowDrag } from "./sessionDrag";

/** How far a row is pushed in for each level of its session under its parents. */
export const sessionRowIndent = (depth: number) => 14 + Math.min(depth, 8) * 12;

/** What hides the sub-agents of a session and shows them again; absent for a session that has none. */
export type SessionTwist = Readonly<{ collapsed: boolean; toggle: () => void }>;

/** The twist of the row of a session entry, when it has sub-agents. */
export const sessionTwist = (entry: Extract<SessionListEntry, { kind: "session" }>, toggle: (session: WorkspaceSession, collapsed: boolean) => void): SessionTwist | undefined =>
  entry.parent ? { collapsed: entry.collapsed, toggle: () => toggle(entry.row.session, !entry.collapsed) } : undefined;

/**
 * The arrow keys of a row that has sub-agents: left hides them and right shows them, as in any tree. Left on a
 * row that has nothing to hide is left to the Explorer, which goes to the project.
 */
export function sessionTwistKey(event: KeyboardEvent, twist: SessionTwist | undefined) {
  if (!twist || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
  if (event.key !== (twist.collapsed ? "ArrowRight" : "ArrowLeft")) return;
  event.preventDefault();
  event.stopPropagation();
  twist.toggle();
}

/**
 * The icon and the title of a session row: a session started by another one has an icon of its own, and so has a
 * session started by an automation. A session that has sub-agents starts with the twist that hides them.
 */
export function SessionRowTitle({ session, depth, diagnostic, subAgents, twist, parentElsewhere }: Pick<SessionHierarchyRow, "session" | "depth" | "diagnostic" | "subAgents" | "parentElsewhere"> & { twist?: SessionTwist }) {
  const { t } = useShellLanguage();
  const automated = !!session.automationId;
  const logo = useProviderBrand(session.providerKey);
  const provider = useContext(ProviderBrandsContext).get(session.providerKey?.toLowerCase() ?? "")?.name ?? session.providerKey;
  // The logo of its provider tells the sessions apart at a glance. A session of a provider of no known brand
  // keeps the icon of a session.
  return <>{twist && <span className="tree-twist session-twist" title={t(twist.collapsed ? "Show the sub-agents" : "Hide the sub-agents")}
    onClick={event => { event.stopPropagation(); twist.toggle(); }} onDoubleClick={event => event.stopPropagation()}>
    <AppIcon name="chevronDown" size={12} className={twist.collapsed ? "tree-chevron" : "tree-chevron expanded"} /></span>}
    {(depth > 0 || parentElsewhere || automated) && <span className="session-icon" data-file-tone={depth > 0 || parentElsewhere ? "teal" : "gold"}
      title={depth === 0 && parentElsewhere ? t("Child of {title}", { title: plainTitle(parentElsewhere.title) }) : automated && depth === 0 ? t("Started by an automation") : undefined}>
    <AppIcon name={depth > 0 || parentElsewhere ? "childSession" : "automation"} size={13} /></span>}
    <span className="session-icon session-provider" data-file-tone={logo.icon || logo.symbol ? undefined : "purple"} title={provider ?? undefined}>
      <ProviderIcon providerKey={session.providerKey} size={13} fallback="assistant" /></span>
    <span className="session-title">{diagnostic && <span aria-hidden="true">⚠ </span>}{plainTitle(session.title)}</span><SubAgentBadge count={subAgents ?? 0} /></>;
}

/** What follows the listed sub-agents of a session when it has more, or lists more than at first. */
export function SubAgentDisclosure({ entry, onMore, onFewer }: { entry: Extract<SessionListEntry, { kind: "more" }>; onMore: () => void; onFewer: () => void }) {
  const { t } = useShellLanguage();
  return <div className="session-list-disclosure sub-agent-disclosure" style={{ paddingLeft: sessionRowIndent(entry.depth) }}>
    {entry.hidden > 0 && <button type="button" className="quiet-button" onClick={onMore}>{t("Show more…")} <span className="muted-text">({entry.hidden})</span></button>}
    {entry.extended && <button type="button" className="quiet-button" onClick={onFewer}>{t("Show fewer")}</button>}
  </div>;
}

/**
 * The sessions of an open scope of the Explorer that is not the selected one. A row opens its session, which
 * makes its scope the selected one; renaming and deleting do the same first, as they act on the selected session.
 * The lines that plugins add to the menu of a row are those of that row: they are read and they act for its session
 * and the project of its scope, and they select nothing. So do the lines the window adds for a row (`menuEntries`: the
 * canvases about a session).
 */
export function ExplorerSessions({ entries, global, projectId, more, extended, access, marks, onAction, onKeep, menuEntries, actionEntries, deleteAsks = true, archiveAsks, spaces = false, onMore, onFewer, tree, drag, claudeCode }: {
  /** The sessions listed, what stands for the sub-agents that are not, and the place of the Claude Code sessions started elsewhere. */
  entries: readonly SessionListEntry[];
  /** Whether the scope is the global sessions, and not a project. */
  global: boolean;
  /** The project of the scope, which the lines of plugins in the menu of a row are asked about; null for the global sessions. */
  projectId: string | null;
  /** How many more sessions the scope has than the ones listed. */
  more: number;
  /** Whether more rows than at first are shown. */
  extended: boolean;
  /** Whether a session can be renamed and deleted. */
  access: (session: WorkspaceSession) => Readonly<{ rename: boolean; delete: boolean; archive?: boolean; fork?: boolean; "remote-control"?: boolean; move?: boolean; space?: boolean;
    "open-vs-code"?: boolean; "copy-resume-command"?: boolean; "run-in-terminal"?: boolean }>;
  /** What follows the title of a row: its marks and when it was updated. */
  marks: (session: WorkspaceSession) => ReactNode;
  onAction: (session: WorkspaceSession, action: SessionAction) => void;
  /** A row was double-clicked: its session is opened in a tab of its own, which stays. */
  onKeep?: (session: WorkspaceSession) => void;
  /** What the menu of a row has after its own lines and before those of plugins, for the session of that row. */
  menuEntries?: (session: WorkspaceSession) => SessionMenuEntry[];
  /** What the menu of a row does with its session besides its own lines, listed before the line that deletes it. */
  actionEntries?: (session: WorkspaceSession) => SessionMenuEntry[];
  /** Whether deleting a session asks first, which its menu says. */
  deleteAsks?: boolean;
  /** Whether archiving a session asks first (it has child sessions), which its menu says. */
  archiveAsks?: (session: WorkspaceSession) => boolean;
  /** Whether there are spaces other than the default one, which a session can be moved to. */
  spaces?: boolean;
  onMore: () => void;
  onFewer: () => void;
  /** The sub-agents of a session: hidden or shown, and more or fewer of them listed. */
  tree: Readonly<{ toggle: (session: WorkspaceSession, collapsed: boolean) => void; more: (sessionId: string) => void; fewer: (sessionId: string) => void }>;
  /** What makes the row of a session draggable onto another project. */
  drag?: (session: WorkspaceSession) => SessionRowDrag;
  /** The group of the Claude Code sessions started in VS Code or a terminal in the folder of a project, last in its list. */
  claudeCode?: (projectId: string) => ReactNode;
}) {
  const { t } = useShellLanguage();
  const brands = useContext(ProviderBrandsContext);
  const [menu, setMenu] = useState<{ id: string; anchor: HTMLElement } | null>(null);
  // What a row says besides its title (whose session started it, what is wrong with it), for a screen reader.
  const tipId = useId();
  const show = (id: string, anchor: HTMLElement | null) => setMenu(current => !anchor || current?.id === id ? null : { id, anchor });
  // One menu is open at a time: its row is what plugins are asked about, whatever project and session are selected.
  const pluginEntries = usePluginMenuEntries("SessionMenu", menu ? { projectId, sessionId: menu.id } : null);
  return <div className="session-rail"><div className="session-list">
    {sessionEntries(entries).map((entry, index) => {
      if (entry.kind === "more") return <SubAgentDisclosure key={`more:${entry.parentId}`} entry={entry}
        onMore={() => tree.more(entry.parentId)} onFewer={() => tree.fewer(entry.parentId)} />;
      const { session, depth, diagnostic, tooltip, subAgents, parentElsewhere } = entry.row;
      const twist = sessionTwist(entry, tree.toggle);
      const shown = menu?.id === session.id ? menu : null;
      const allowed = shown ? access(session) : null;
      return <div className={`session-row${shown ? " menu-open" : ""}`} key={session.id} data-rename-keys {...drag?.(session)}
        onContextMenu={event => {
          if ((event.target as HTMLElement).closest("input, textarea, select, [contenteditable='true']")) return;
          event.preventDefault();
          show(session.id, event.currentTarget.querySelector<HTMLElement>(".session-actions-trigger"));
        }}
        onKeyDown={event => {
          const composing = event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229;
          // Delete, Backspace, F2, F and A on a row do what the same entries of its menu do; the row keeps F2 from
          // Rename Project (data-rename-keys).
          if (isSessionDeleteKey(event, composing) && event.target === event.currentTarget.firstElementChild) {
            event.preventDefault(); event.stopPropagation();
            if (access(session).delete) onAction(session, "delete");
            return;
          }
          const shortcut = sessionRowShortcut(event, composing);
          if (shortcut && event.target === event.currentTarget.firstElementChild) {
            event.preventDefault(); event.stopPropagation();
            if (access(session)[shortcut]) onAction(session, shortcut);
            return;
          }
          if (!isSessionContextKey(event.key, event.shiftKey, composing, false)) return;
          event.preventDefault(); event.stopPropagation();
          show(session.id, event.currentTarget.querySelector<HTMLElement>(".session-actions-trigger"));
        }}>
        <button type="button" aria-pressed={false} aria-expanded={twist && !twist.collapsed} aria-describedby={`${tipId}-${index}`} title={tooltip} data-full-text={plainTitle(session.fullTitle || session.title)} style={{ paddingLeft: sessionRowIndent(depth) }}
          onClick={() => onAction(session, "open")} onDoubleClick={() => onKeep?.(session)} onKeyDown={event => sessionTwistKey(event, twist)}>
          <SessionRowTitle session={session} depth={depth} diagnostic={diagnostic} subAgents={subAgents} twist={twist} parentElsewhere={parentElsewhere} />{marks(session)}
        </button>
        <span id={`${tipId}-${index}`} role="tooltip" className="session-tooltip">{tooltip}</span>
        <button type="button" className="icon-button session-actions-trigger" aria-label={t("Actions for {title} (ID: {id})", { title: session.title, id: session.id })}
          aria-haspopup="menu" aria-expanded={!!shown} onClick={event => show(session.id, event.currentTarget)}><AppIcon name="ellipsis" size={16} /></button>
        {shown && allowed && <SessionTabMenu anchor={shown.anchor} container={document.body} title={t("Session actions for {title}", { title: session.title })}
          current={() => true} onClose={() => queueMicrotask(() => setMenu(value => value === shown ? null : value))}
          items={[
            { key: "open", label: t("Open session"), icon: "open", onSelect: () => onAction(session, "open") },
            { key: "rename", label: t("Rename…"), icon: "edit", endNode: <KeyGesture gesture="F2" />, disabled: !allowed.rename, onSelect: () => onAction(session, "rename") },
            { key: "fork", label: t("Fork…"), icon: "branch", endNode: <KeyGesture gesture="F" />, disabled: !allowed.fork, onSelect: () => onAction(session, "fork") },
            ...(providerHasRemoteControl(brands.get(session.providerKey?.toLowerCase() ?? "")?.type)
              ? [{ key: "remote-control", label: t("Remote Control…"), icon: "remote" as const, disabled: !allowed["remote-control"], onSelect: () => onAction(session, "remote-control") }] : []),
            ...openElsewhereEntries(t, session, brands.get(session.providerKey?.toLowerCase() ?? "")?.type, allowed, action => onAction(session, action)),
            ...(actionEntries?.(session) ?? []),
            ...(global ? [] : [{ key: "move", label: t("Move to project…"), icon: "folderInput" as const, disabled: !allowed.move, onSelect: () => onAction(session, "move") }]),
            ...(spaces ? [{ key: "space", label: t("Move to space…"), icon: "space" as const, disabled: !allowed.space, onSelect: () => onAction(session, "space") }] : []),
            { key: "archive", label: archiveAsks?.(session) ? t("Archive…") : t("Archive"), icon: "archive", endNode: <KeyGesture gesture="A" />, disabled: !allowed.archive, onSelect: () => onAction(session, "archive") },
            { key: "delete", label: deleteAsks ? `${t("Delete")}…` : t("Delete"), icon: "trash", danger: true, endNode: <KeyGesture gesture="Delete" />, disabled: !allowed.delete, onSelect: () => onAction(session, "delete") },
            ...(menuEntries?.(session) ?? []),
            ...pluginEntries,
          ]} />}
      </div>;
    })}
    {sessionEntries(entries).length === 0 && <div className="sidebar-empty">{t(global ? "No chats." : "No sessions in this project.")}</div>}
    <div className="session-list-disclosure">
      {more > 0 && <button type="button" className="quiet-button" onClick={onMore}>{t("Show more…")} <span className="muted-text">({more})</span></button>}
      {extended && <button type="button" className="quiet-button" onClick={onFewer}>{t("Show fewer")}</button>}
    </div>
    {entries.map(entry => entry.kind === "claude-code" && <Fragment key={`claude-code:${entry.projectId}`}>{claudeCode?.(entry.projectId)}</Fragment>)}
  </div></div>;
}
