import type { WorkspaceSession } from "#neoastra";
import { useState, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { SessionTabMenu } from "../SessionTabMenu";
import type { SessionHierarchyRow } from "../sessionHierarchy";
import { isSessionContextKey, type SessionAction } from "../sessionRowActions";
import { plainTitle } from "../sessionTitle";
import { useShellLanguage } from "../shellLanguage";

/** How far a row is pushed in for each level of its session under its parents. */
export const sessionRowIndent = (depth: number) => 11 + Math.min(depth, 8) * 12;

/**
 * The icon and the title of a session row: a session started by another one has an icon of its own, and so has a
 * session started by an automation.
 */
export function SessionRowTitle({ session, depth, diagnostic }: Pick<SessionHierarchyRow, "session" | "depth" | "diagnostic">) {
  const { t } = useShellLanguage();
  const automated = !!session.automationId;
  return <><span className="session-icon" data-file-tone={depth > 0 ? "teal" : automated ? "gold" : "purple"} title={automated && depth === 0 ? t("Started by an automation") : undefined}>
    <AppIcon name={depth > 0 ? "childSession" : automated ? "automation" : "assistant"} size={13} /></span>
    <span className="session-title">{diagnostic && <span aria-hidden="true">⚠ </span>}{plainTitle(session.title)}</span></>;
}

/**
 * The sessions of an open scope of the Explorer that is not the selected one. A row opens its session, which
 * makes its scope the selected one; renaming and deleting do the same first, as they act on the selected session.
 */
export function ExplorerSessions({ rows, global, more, extended, access, marks, onAction, onMore, onFewer }: {
  rows: readonly SessionHierarchyRow[];
  /** Whether the scope is the global sessions, and not a project. */
  global: boolean;
  /** How many more sessions the scope has than the rows shown. */
  more: number;
  /** Whether more rows than at first are shown. */
  extended: boolean;
  /** Whether a session can be renamed and deleted. */
  access: (session: WorkspaceSession) => Readonly<{ rename: boolean; delete: boolean }>;
  /** What follows the title of a row: its marks and when it was updated. */
  marks: (session: WorkspaceSession) => ReactNode;
  onAction: (session: WorkspaceSession, action: SessionAction) => void;
  onMore: () => void;
  onFewer: () => void;
}) {
  const { t } = useShellLanguage();
  const [menu, setMenu] = useState<{ id: string; anchor: HTMLElement } | null>(null);
  const show = (id: string, anchor: HTMLElement | null) => setMenu(current => !anchor || current?.id === id ? null : { id, anchor });
  return <div className="session-rail"><div className="session-list">
    {rows.map(({ session, depth, diagnostic, tooltip }) => {
      const shown = menu?.id === session.id ? menu : null;
      const allowed = shown ? access(session) : null;
      return <div className={`session-row${shown ? " menu-open" : ""}`} key={session.id}
        onContextMenu={event => {
          if ((event.target as HTMLElement).closest("input, textarea, select, [contenteditable='true']")) return;
          event.preventDefault();
          show(session.id, event.currentTarget.querySelector<HTMLElement>(".session-actions-trigger"));
        }}
        onKeyDown={event => {
          if (!isSessionContextKey(event.key, event.shiftKey, event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229, false)) return;
          event.preventDefault(); event.stopPropagation();
          show(session.id, event.currentTarget.querySelector<HTMLElement>(".session-actions-trigger"));
        }}>
        <button type="button" aria-pressed={false} title={tooltip} style={{ paddingLeft: sessionRowIndent(depth) }} onClick={() => onAction(session, "open")}>
          <SessionRowTitle session={session} depth={depth} diagnostic={diagnostic} />{marks(session)}
        </button>
        <button type="button" className="icon-button session-actions-trigger" aria-label={t("Actions for {title} (ID: {id})", { title: session.title, id: session.id })}
          aria-haspopup="menu" aria-expanded={!!shown} onClick={event => show(session.id, event.currentTarget)}><AppIcon name="ellipsis" size={16} /></button>
        {shown && allowed && <SessionTabMenu anchor={shown.anchor} container={document.body} title={t("Session actions for {title}", { title: session.title })}
          current={() => true} onClose={() => queueMicrotask(() => setMenu(value => value === shown ? null : value))}
          items={[
            { key: "open", label: t("Open session"), icon: "open", onSelect: () => onAction(session, "open") },
            { key: "rename", label: t("Rename…"), icon: "edit", disabled: !allowed.rename, onSelect: () => onAction(session, "rename") },
            { key: "delete", label: t("Delete… (confirmation required)"), icon: "trash", danger: true, disabled: !allowed.delete, onSelect: () => onAction(session, "delete") },
          ]} />}
      </div>;
    })}
    {rows.length === 0 && <div className="sidebar-empty">{t(global ? "No chats." : "No sessions in this project.")}</div>}
    <div className="session-list-disclosure">
      {more > 0 && <button type="button" className="quiet-button" onClick={onMore}>{t("Show more…")} <span className="muted-text">({more})</span></button>}
      {extended && <button type="button" className="quiet-button" onClick={onFewer}>{t("Show fewer")}</button>}
    </div>
  </div></div>;
}
