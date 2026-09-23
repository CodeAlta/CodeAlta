import type { Ref } from "react";
import { menuFocusIndex, type SessionAction } from "./sessionRowActions";

export function SessionActionMenu({ id, label, rename, deleteAllowed, menuRef, onAction, onDismiss }: {
  id: string; label: string; rename: boolean; deleteAllowed: boolean; menuRef: Ref<HTMLDivElement>;
  onAction: (action: SessionAction) => void; onDismiss: (restoreFocus: boolean) => void;
}) {
  return <div id={id} ref={menuRef} className="session-actions-menu" role="menu" aria-label={`Session actions for ${label}`}
    onKeyDown={event => {
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); onDismiss(true); return; }
      const items = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('button[role="menuitem"]:not(:disabled)'));
      const next = menuFocusIndex(event.key, items.indexOf(document.activeElement as HTMLButtonElement), items.length);
      if (next !== null) { event.preventDefault(); event.stopPropagation(); items[next].focus(); }
    }}
    onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) onDismiss(false); }}>
    <button type="button" role="menuitem" onClick={() => onAction("open")}>Open session</button>
    <button type="button" role="menuitem" disabled={!rename} onClick={() => onAction("rename")}>Rename…</button>
    <button type="button" role="menuitem" disabled={!deleteAllowed} onClick={() => onAction("delete")}>Delete… (confirmation required)</button>
  </div>;
}
