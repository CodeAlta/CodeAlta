import { useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import type { WorkspaceProject } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { ProjectDetailsDialog } from "./ProjectDetailsEntry";
import { projectRowAccess, projectRowCurrent, type ProjectRowContext } from "./projectRowActionAccess";
import { isSessionContextKey, menuFocusIndex } from "./sessionRowActions";
import { useShellLanguage } from "./shellLanguage";

export type ProjectRowAuthority = {
  current: () => ProjectRowContext;
  open: (id: string) => void;
  rename: () => void;
  archive: () => void;
};
type Review = { project: WorkspaceProject; context: ProjectRowContext; origin: HTMLButtonElement;
  row: HTMLLIElement; details: boolean; opening: boolean };

export function ProjectRowActions({ project, authority, children }: {
  project: WorkspaceProject; authority?: ProjectRowAuthority; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const row = useRef<HTMLLIElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const latest = useRef(authority); latest.current = authority;
  const review = useRef<Review | null>(null);
  const [shown, setShown] = useState<Review | null>(null);
  function current(value: Review) {
    const context = latest.current?.current();
    return review.current === value && !!context && value.row === row.current && value.row.isConnected
      && value.origin === trigger.current && value.origin.isConnected && !value.row.closest("[hidden]")
      && projectRowCurrent(value.project, value.context, context);
  }
  function dismiss(restore = false) {
    const value = review.current;
    const focus = restore && value && current(value);
    review.current = null; setShown(null);
    if (focus) requestAnimationFrame(() => {
      const context = latest.current?.current();
      if (value.origin.isConnected && context && projectRowCurrent(value.project, value.context, context)
        && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) value.origin.focus();
    });
  }
  function open() {
    if (!latest.current || !row.current || !trigger.current || !row.current.isConnected
      || row.current.closest("[hidden]") || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    const context = latest.current.current();
    if (!projectRowAccess(project, context).open) return;
    const value: Review = { project: { ...project }, context, origin: trigger.current, row: row.current, details: false, opening: false };
    review.current = value; setShown(value);
  }
  const visible = shown && current(shown) ? shown : null;
  useLayoutEffect(() => { if (visible && !visible.details) menu.current?.querySelector<HTMLButtonElement>("button:not(:disabled)")?.focus(); }, [visible]);
  useEffect(() => { if (shown && !visible) dismiss(); }, [shown, visible]);
  useEffect(() => {
    const modal = (event: Event) => {
      const value = review.current;
      if (!value || !(event.target instanceof HTMLDialogElement)) return;
      if (value.details && value.opening && (event as ToggleEvent).newState === "open"
        && event.target === value.row.querySelector(".project-details-dialog")) {
        // App publishes this exact opening's generation before the row observer.
        // Accept only that transition, never mask subsequent generation changes.
        value.context = { ...value.context, modalGeneration: latest.current!.current().modalGeneration };
        value.opening = false; return;
      }
      dismiss();
    };
    const pointer = (event: PointerEvent) => { if (review.current && !review.current.row.contains(event.target as Node)) dismiss(); };
    document.addEventListener("beforetoggle", modal, true);
    document.addEventListener("pointerdown", pointer);
    return () => { review.current = null; document.removeEventListener("beforetoggle", modal, true); document.removeEventListener("pointerdown", pointer); };
  }, []);
  function action(kind: "open" | "details" | "rename" | "archive", original: Review) {
    const value = review.current;
    const owner = latest.current;
    if (!value || value !== original || value.details || !owner || !current(value) || !menu.current?.isConnected
      || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')
      || !projectRowAccess(value.project, value.context)[kind] || !projectRowAccess(value.project, owner.current())[kind]) return;
    if (kind === "details") {
      const details = { ...value, details: true, opening: true };
      review.current = details; setShown(details); return;
    }
    dismiss();
    if (kind === "open") owner.open(value.project.id);
    else if (kind === "rename") owner.rename();
    else owner.archive();
  }
  const access = visible && authority ? projectRowAccess(visible.project, authority.current()) : null;
  return <li ref={row} className="project-action-row" onContextMenu={event => {
    if ((event.target as HTMLElement).closest('input, textarea, select, [contenteditable="true"], [role="menu"], dialog')) return;
    event.preventDefault(); open();
  }} onKeyDown={event => {
    if (event.repeat || !isSessionContextKey(event.key, event.shiftKey, event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229,
      !!(event.target as HTMLElement).closest('input, textarea, select, [contenteditable="true"], dialog'))) return;
    event.preventDefault(); event.stopPropagation(); open();
  }}>{children}{authority && <button ref={trigger} type="button" className="icon-button project-actions-trigger"
    aria-label={t("Actions for {title} (ID: {id})", { title: project.name, id: project.id })}
    aria-haspopup="menu" aria-expanded={!!visible && !visible.details} aria-controls={visible && !visible.details ? id : undefined}
    onClick={open}><AppIcon name="ellipsis" size={16} /></button>}
    {visible && !visible.details && <div ref={menu} id={id} role="menu" className="project-actions-menu" aria-label={t("Project actions")}
      onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null) && !review.current?.details) dismiss(); }}
      onKeyDown={event => {
        if (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) {
          if (["Enter", " ", "Escape"].includes(event.key)) { event.preventDefault(); event.stopPropagation(); } return;
        }
        if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); dismiss(true); return; }
        const items = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('button[role="menuitem"]:not(:disabled)'));
        const next = menuFocusIndex(event.key, items.indexOf(document.activeElement as HTMLButtonElement), items.length);
        if (next !== null) { event.preventDefault(); event.stopPropagation(); items[next].focus(); }
      }}>
      <button type="button" role="menuitem" onClick={() => action("open", visible)}>{t("Open")}</button>
      <button type="button" role="menuitem" onClick={() => action("details", visible)}>{t("Details")}</button>
      <button type="button" role="menuitem" disabled={!access?.rename} onClick={() => action("rename", visible)}>{t("Rename project…")}</button>
      <button type="button" role="menuitem" disabled={!access?.archive} onClick={() => action("archive", visible)}>{t(project.archived ? "Unarchive project…" : "Archive project…")}</button>
    </div>}
    {visible?.details && visible.context.snapshot && <ProjectDetailsDialog project={visible.project} snapshot={visible.context.snapshot}
      isCurrent={() => current(visible)} onClose={() => dismiss(true)} />}
  </li>;
}
