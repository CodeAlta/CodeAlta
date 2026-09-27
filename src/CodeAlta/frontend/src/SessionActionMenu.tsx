import type { Ref } from "react";
import { menuFocusIndex, type SessionAction } from "./sessionRowActions";
import { useShellLanguage } from "./shellLanguage";

export function SessionActionMenu({ id, label, rename, deleteAllowed, menuRef, onAction, onDismiss }: {
  id: string; label: string; rename: boolean; deleteAllowed: boolean; menuRef: Ref<HTMLDivElement>;
  onAction: (action: SessionAction) => void; onDismiss: (restoreFocus: boolean) => void;
}) {
  const { t } = useShellLanguage();
  return <div id={id} ref={menuRef} className="session-actions-menu" role="menu" aria-label={t("Session actions for {title}", { title: label })}
    onKeyDown={event => {
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); onDismiss(true); return; }
      const items = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('button[role="menuitem"]:not(:disabled)'));
      const next = menuFocusIndex(event.key, items.indexOf(document.activeElement as HTMLButtonElement), items.length);
      if (next !== null) { event.preventDefault(); event.stopPropagation(); items[next].focus(); }
    }}
    onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) onDismiss(false); }}>
    <button type="button" role="menuitem" onClick={() => onAction("open")}>{t("Open session")}</button>
    <button type="button" role="menuitem" disabled={!rename} onClick={() => onAction("rename")}>{t("Rename…")}</button>
    <button type="button" role="menuitem" disabled={!deleteAllowed} onClick={() => onAction("delete")}>{t("Delete… (confirmation required)")}</button>
  </div>;
}
