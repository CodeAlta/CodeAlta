import { useEffect, useRef, useState, type ReactNode } from "react";
import type { CanvasItem, WorkspaceProject } from "#neoastra";
import { canvasRef } from "../canvases/canvasPages";
import { AppIcon } from "../AppIcon";
import { ProjectDetailsDialog } from "../ProjectDetailsEntry";
import { projectRowAccess, projectRowCurrent, type ProjectRowContext } from "./projectRowActionAccess";
import { isSessionContextKey } from "../sessionRowActions";
import { SessionTabMenu } from "../SessionTabMenu";
import { usePluginMenuEntries } from "../pluginButtons/pluginMenu";
import { useShellLanguage } from "../shellLanguage";
import { modalDialogOpen } from "../modalDialogs";

/** Session actions of one scope (a project, or the global "Global sessions" scope when the id is null). */
export type ScopeSessionActions = {
  /** Whether a session can be created in this scope right now. */
  canCreate: (projectId: string | null) => boolean;
  create: (projectId: string | null) => void;
  search: (projectId: string | null) => void;
  browse: (projectId: string | null) => void;
};
export type ProjectRowAuthority = {
  current: () => ProjectRowContext;
  open: (id: string) => void;
  rename: () => void;
  archive: () => void;
  /** Whether archiving and unarchiving ask first, which the menu says; they do when this is absent. */
  archiveAsks?: boolean;
  sessions?: ScopeSessionActions;
  /**
   * The canvases of plugins that are about a project: the first few are lines of the menu, and `more` says the page has others.
   * Absent where no canvas can be opened.
   */
  canvases?: Readonly<{ list: () => Readonly<{ items: readonly CanvasItem[]; more: boolean }>; open: (item: CanvasItem, project: WorkspaceProject) => void; all: () => void }>;
};
type Review = { project: WorkspaceProject; context: ProjectRowContext; origin: HTMLButtonElement;
  row: HTMLLIElement; details: boolean; opening: boolean };

export function ProjectRowActions({ project, authority, favorite, children }: {
  project: WorkspaceProject; authority?: ProjectRowAuthority;
  /** Whether the project is a favorite, and how that is changed. */
  favorite?: Readonly<{ value: boolean; set: (value: boolean) => void }>;
  children: ReactNode;
}) {
  const { t } = useShellLanguage();
  const row = useRef<HTMLLIElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
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
        && !modalDialogOpen()) value.origin.focus();
    });
  }
  function open() {
    if (!latest.current || !row.current || !trigger.current || !row.current.isConnected
      || row.current.closest("[hidden]") || modalDialogOpen()) return;
    const context = latest.current.current();
    if (!projectRowAccess(project, context).open) return;
    const value: Review = { project: { ...project }, context, origin: trigger.current, row: row.current, details: false, opening: false };
    review.current = value; setShown(value);
  }
  const visible = shown && current(shown) ? shown : null;
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
    // The floating menu renders in a portal outside the row; its own overlay handles outside clicks.
    const pointer = (event: PointerEvent) => {
      if (review.current && !review.current.row.contains(event.target as Node)
        && !(event.target instanceof Element && event.target.closest(".session-tab-popup"))) dismiss();
    };
    document.addEventListener("beforetoggle", modal, true);
    document.addEventListener("pointerdown", pointer);
    return () => { review.current = null; document.removeEventListener("beforetoggle", modal, true); document.removeEventListener("pointerdown", pointer); };
  }, []);
  function sessionAction(kind: "create" | "search" | "browse", original: Review) {
    const value = review.current;
    const owner = latest.current;
    if (!value || value !== original || value.details || !owner?.sessions || !current(value)
      || modalDialogOpen()
      || !projectRowAccess(value.project, owner.current()).open || kind === "create" && !owner.sessions.canCreate(value.project.id)) return;
    dismiss();
    owner.sessions[kind](value.project.id);
  }
  // A canvas of a plugin about this project, or the page that lists them all (item null).
  function canvasAction(item: CanvasItem | null, original: Review) {
    const value = review.current;
    const owner = latest.current;
    if (!value || value !== original || value.details || !owner?.canvases || !current(value) || modalDialogOpen() || !projectRowAccess(value.project, owner.current()).open) return;
    dismiss();
    if (item) owner.canvases.open(item, value.project); else owner.canvases.all();
  }
  function action(kind: "open" | "details" | "rename" | "archive", original: Review) {
    const value = review.current;
    const owner = latest.current;
    if (!value || value !== original || value.details || !owner || !current(value)
      || modalDialogOpen()
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
  // A favorite is a preference of this window: it changes nothing of the project, so it asks for no access to it.
  function setFavorite(original: Review, value: boolean) {
    if (review.current !== original || original.details || !current(original)) return;
    dismiss();
    favorite?.set(value);
  }
  const access = visible && authority ? projectRowAccess(visible.project, authority.current()) : null;
  // What plugins add to the menu of this row: read for this project, not for the selected one.
  const pluginEntries = usePluginMenuEntries("ProjectMenu", visible && !visible.details ? { projectId: visible.project.id, sessionId: null } : null);
  // Beside the actions of the project, what the plugins offer for it: a few lines, then the page of the canvases.
  const canvases = visible && authority?.canvases && !visible.project.archived ? authority.canvases.list() : null;
  return <li ref={row} className="project-action-row" onContextMenu={event => {
    if ((event.target as HTMLElement).closest('input, textarea, select, [contenteditable="true"], [role="menu"], dialog')) return;
    event.preventDefault(); open();
  }} onKeyDown={event => {
    if (event.repeat || !isSessionContextKey(event.key, event.shiftKey, event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229,
      !!(event.target as HTMLElement).closest('input, textarea, select, [contenteditable="true"], dialog'))) return;
    event.preventDefault(); event.stopPropagation(); open();
  }}>{children}{authority && <button ref={trigger} type="button" className="icon-button project-actions-trigger"
    aria-label={t("Actions for {title} (ID: {id})", { title: project.name, id: project.id })}
    aria-haspopup="menu" aria-expanded={!!visible && !visible.details}
    onClick={open}><AppIcon name="ellipsis" size={16} /></button>}
    {visible && !visible.details && <SessionTabMenu anchor={visible.origin} title={t("Project actions")} container={document.body}
      current={() => review.current === visible && current(visible)}
      // The menu closes before it runs the chosen entry; dismiss afterwards so the entry still sees its review.
      onClose={() => queueMicrotask(() => { if (review.current === visible) dismiss(); })}
      items={[
        ...(authority?.sessions ? [
          { key: "create", label: t("New session"), icon: "newSession" as const, disabled: !authority.sessions.canCreate(project.id), onSelect: () => sessionAction("create", visible) },
          { key: "search", label: `${t("Search sessions")}…`, icon: "search" as const, onSelect: () => sessionAction("search", visible) },
          { key: "browse", label: t("Browse saved sessions"), icon: "browse" as const, onSelect: () => sessionAction("browse", visible) },
          { key: "project", divider: true as const },
        ] : []),
        { key: "open", label: t("Open"), icon: "open", onSelect: () => action("open", visible) },
        ...(favorite ? [{ key: "favorite", label: t(favorite.value ? "Remove from favorites" : "Add to favorites"), icon: "star" as const,
          onSelect: () => setFavorite(visible, !favorite.value) }] : []),
        { key: "details", label: t("Details"), icon: "info", onSelect: () => action("details", visible) },
        { key: "rename", label: t("Rename project…"), icon: "edit", disabled: !access?.rename, onSelect: () => action("rename", visible) },
        { key: "archive", label: t(authority?.archiveAsks === false ? project.archived ? "Unarchive project" : "Archive project" : project.archived ? "Unarchive project…" : "Archive project…"),
          icon: "archive", disabled: !access?.archive, onSelect: () => action("archive", visible) },
        ...(canvases && canvases.items.length > 0 ? [{ key: "canvases", divider: true as const },
          ...canvases.items.map(item => ({ key: `canvas:${canvasRef(item)}`, label: t("Open {title}", { title: item.title }), icon: "canvases" as const, onSelect: () => canvasAction(item, visible) })),
          ...(canvases.more ? [{ key: "canvases-more", label: t("More…"), icon: "canvases" as const, onSelect: () => canvasAction(null, visible) }] : [])] : []),
        ...pluginEntries,
      ]} />}
    {visible?.details && visible.context.snapshot && <ProjectDetailsDialog project={visible.project} snapshot={visible.context.snapshot}
      isCurrent={() => current(visible)} onClose={() => dismiss(true)} />}
  </li>;
}
