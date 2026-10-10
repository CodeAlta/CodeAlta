import { SubAgentBadge } from "../SessionReference";
import type { WorkspaceSession } from "#neoastra";
import { useContext, useState, type KeyboardEvent, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { ProviderBrandsContext, ProviderIcon, useProviderBrand } from "../ProviderIcon";
import { SessionTabMenu } from "../SessionTabMenu";
import type { SessionHierarchyRow } from "../sessionHierarchy";
import { isSessionContextKey, isSessionDeleteKey, type SessionAction } from "../sessionRowActions";
import { plainTitle } from "../sessionTitle";
import { providerHasRemoteControl } from "../remoteControl";
import { useShellLanguage } from "../shellLanguage";
import type { SessionListEntry } from "./sessionTree";

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
export function SessionRowTitle({ session, depth, diagnostic, subAgents, twist }: Pick<SessionHierarchyRow, "session" | "depth" | "diagnostic" | "subAgents"> & { twist?: SessionTwist }) {
  const { t } = useShellLanguage();
  const automated = !!session.automationId;
  const logo = useProviderBrand(session.providerKey);
  const provider = useContext(ProviderBrandsContext).get(session.providerKey?.toLowerCase() ?? "")?.name ?? session.providerKey;
  // The logo of its provider tells the sessions apart at a glance. A session of a provider of no known brand
  // keeps the icon of a session.
  return <>{twist && <span className="tree-twist session-twist" title={t(twist.collapsed ? "Show the sub-agents" : "Hide the sub-agents")}
    onClick={event => { event.stopPropagation(); twist.toggle(); }}>
    <AppIcon name="chevronDown" size={12} className={twist.collapsed ? "tree-chevron" : "tree-chevron expanded"} /></span>}
    {(depth > 0 || automated) && <span className="session-icon" data-file-tone={depth > 0 ? "teal" : "gold"} title={automated && depth === 0 ? t("Started by an automation") : undefined}>
    <AppIcon name={depth > 0 ? "childSession" : "automation"} size={13} /></span>}
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
 */
export function ExplorerSessions({ entries, global, more, extended, access, marks, onAction, deleteAsks = true, onMore, onFewer, tree }: {
  /** The sessions listed, and what stands for the sub-agents that are not. */
  entries: readonly SessionListEntry[];
  /** Whether the scope is the global sessions, and not a project. */
  global: boolean;
  /** How many more sessions the scope has than the ones listed. */
  more: number;
  /** Whether more rows than at first are shown. */
  extended: boolean;
  /** Whether a session can be renamed and deleted. */
  access: (session: WorkspaceSession) => Readonly<{ rename: boolean; delete: boolean; "remote-control"?: boolean }>;
  /** What follows the title of a row: its marks and when it was updated. */
  marks: (session: WorkspaceSession) => ReactNode;
  onAction: (session: WorkspaceSession, action: SessionAction) => void;
  /** Whether deleting a session asks first, which its menu says. */
  deleteAsks?: boolean;
  onMore: () => void;
  onFewer: () => void;
  /** The sub-agents of a session: hidden or shown, and more or fewer of them listed. */
  tree: Readonly<{ toggle: (session: WorkspaceSession, collapsed: boolean) => void; more: (sessionId: string) => void; fewer: (sessionId: string) => void }>;
}) {
  const { t } = useShellLanguage();
  const brands = useContext(ProviderBrandsContext);
  const [menu, setMenu] = useState<{ id: string; anchor: HTMLElement } | null>(null);
  const show = (id: string, anchor: HTMLElement | null) => setMenu(current => !anchor || current?.id === id ? null : { id, anchor });
  return <div className="session-rail"><div className="session-list">
    {entries.map(entry => {
      if (entry.kind === "more") return <SubAgentDisclosure key={`more:${entry.parentId}`} entry={entry}
        onMore={() => tree.more(entry.parentId)} onFewer={() => tree.fewer(entry.parentId)} />;
      const { session, depth, diagnostic, tooltip, subAgents } = entry.row;
      const twist = sessionTwist(entry, tree.toggle);
      const shown = menu?.id === session.id ? menu : null;
      const allowed = shown ? access(session) : null;
      return <div className={`session-row${shown ? " menu-open" : ""}`} key={session.id}
        onContextMenu={event => {
          if ((event.target as HTMLElement).closest("input, textarea, select, [contenteditable='true']")) return;
          event.preventDefault();
          show(session.id, event.currentTarget.querySelector<HTMLElement>(".session-actions-trigger"));
        }}
        onKeyDown={event => {
          const composing = event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229;
          // Delete on a row does what Delete of its menu does.
          if (isSessionDeleteKey(event, composing) && event.target === event.currentTarget.firstElementChild) {
            event.preventDefault(); event.stopPropagation();
            if (access(session).delete) onAction(session, "delete");
            return;
          }
          if (!isSessionContextKey(event.key, event.shiftKey, composing, false)) return;
          event.preventDefault(); event.stopPropagation();
          show(session.id, event.currentTarget.querySelector<HTMLElement>(".session-actions-trigger"));
        }}>
        <button type="button" aria-pressed={false} aria-expanded={twist && !twist.collapsed} title={tooltip} style={{ paddingLeft: sessionRowIndent(depth) }}
          onClick={() => onAction(session, "open")} onKeyDown={event => sessionTwistKey(event, twist)}>
          <SessionRowTitle session={session} depth={depth} diagnostic={diagnostic} subAgents={subAgents} twist={twist} />{marks(session)}
        </button>
        <button type="button" className="icon-button session-actions-trigger" aria-label={t("Actions for {title} (ID: {id})", { title: session.title, id: session.id })}
          aria-haspopup="menu" aria-expanded={!!shown} onClick={event => show(session.id, event.currentTarget)}><AppIcon name="ellipsis" size={16} /></button>
        {shown && allowed && <SessionTabMenu anchor={shown.anchor} container={document.body} title={t("Session actions for {title}", { title: session.title })}
          current={() => true} onClose={() => queueMicrotask(() => setMenu(value => value === shown ? null : value))}
          items={[
            { key: "open", label: t("Open session"), icon: "open", onSelect: () => onAction(session, "open") },
            { key: "rename", label: t("Rename…"), icon: "edit", disabled: !allowed.rename, onSelect: () => onAction(session, "rename") },
            ...(providerHasRemoteControl(brands.get(session.providerKey?.toLowerCase() ?? "")?.type)
              ? [{ key: "remote-control", label: t("Remote Control…"), icon: "remote" as const, disabled: !allowed["remote-control"], onSelect: () => onAction(session, "remote-control") }] : []),
            { key: "delete", label: deleteAsks ? `${t("Delete")}…` : t("Delete"), icon: "trash", danger: true, disabled: !allowed.delete, onSelect: () => onAction(session, "delete") },
          ]} />}
      </div>;
    })}
    {entries.length === 0 && <div className="sidebar-empty">{t(global ? "No chats." : "No sessions in this project.")}</div>}
    <div className="session-list-disclosure">
      {more > 0 && <button type="button" className="quiet-button" onClick={onMore}>{t("Show more…")} <span className="muted-text">({more})</span></button>}
      {extended && <button type="button" className="quiet-button" onClick={onFewer}>{t("Show fewer")}</button>}
    </div>
  </div></div>;
}
