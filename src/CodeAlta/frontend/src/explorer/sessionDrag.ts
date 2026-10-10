import { useEffect, useRef, useState, type PointerEvent } from "react";
import type { WorkspaceProject } from "#neoastra";
import type { MoveScope } from "../sessionMove";

/** The session being dragged, with the project it was in when the drag started. */
export type SessionDrag = Readonly<{ id: string; title: string; from: MoveScope }>;

/** What makes the row of a session draggable; empty for a row that cannot move. */
export type SessionRowDrag = Readonly<{ "data-movable"?: "true"; onPointerDown?: (event: PointerEvent<HTMLElement>) => void }>;

/** Whether a dragged session can be dropped on the row of a project: another project, not archived. */
export function sessionDropAllowed(drag: SessionDrag | null, project: Pick<WorkspaceProject, "id" | "archived"> | undefined): boolean {
  return !!drag && !!project && !project.archived && project.id !== drag.from.projectId;
}

/** The project whose row is at a point of the page, by the scope its row button carries. */
export function projectRowAt(element: Element | null): string | null {
  const scope = element?.closest(".project-action-row")?.querySelector<HTMLElement>(":scope > button:first-child")?.dataset.scope;
  return scope ? scope : null;
}

/** Where a session is being dragged: the label under the pointer, and the project row it can be dropped on. */
export type SessionDragView = Readonly<{ title: string; x: number; y: number; over: string | null }>;

const threshold = 5;

/**
 * Drags the row of a session onto the row of a project with the pointer. Native HTML5 drags go through the drop
 * handling of the window's host (WebView2/OLE), which ends them at once: the drag stays in the page, as the drag
 * of a session tab does. A press that does not move is a click; a drag swallows the click that ends it, and Escape
 * or leaving the window cancels it.
 */
export function useSessionRowDrag(projectOf: (id: string) => WorkspaceProject | undefined,
  drop: (drag: SessionDrag, project: WorkspaceProject) => void) {
  const [view, setView] = useState<SessionDragView | null>(null);
  const gesture = useRef<{ pointerId: number; drag: SessionDrag; x: number; y: number; dragging: boolean; stop: () => void } | null>(null);
  const latest = useRef({ projectOf, drop }); latest.current = { projectOf, drop };
  useEffect(() => () => gesture.current?.stop(), []);

  function target(x: number, y: number, drag: SessionDrag): WorkspaceProject | null {
    const id = projectRowAt(document.elementFromPoint(x, y));
    const project = id ? latest.current.projectOf(id) : undefined;
    return project && sessionDropAllowed(drag, project) ? project : null;
  }
  function start(event: PointerEvent<HTMLElement>, drag: SessionDrag) {
    if (gesture.current || !event.isPrimary || event.button !== 0 || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey
      || (event.target as HTMLElement).closest(".session-actions-trigger, input, textarea, [contenteditable='true']")) return;
    const pointerId = event.pointerId;
    const owner = event.currentTarget.ownerDocument;
    const move = (moved: globalThis.PointerEvent) => {
      const pending = gesture.current;
      if (!pending || moved.pointerId !== pointerId) return;
      if (!(moved.buttons & 1)) { stop(); return; }
      if (!pending.dragging && Math.hypot(moved.clientX - pending.x, moved.clientY - pending.y) < threshold) return;
      if (!pending.dragging) { pending.dragging = true; owner.getSelection()?.removeAllRanges(); owner.body.classList.add("session-row-dragging"); }
      moved.preventDefault();
      setView({ title: drag.title, x: moved.clientX, y: moved.clientY, over: target(moved.clientX, moved.clientY, drag)?.id ?? null });
    };
    const up = (ended: globalThis.PointerEvent) => {
      const pending = gesture.current;
      if (!pending || ended.pointerId !== pointerId) return;
      const project = pending.dragging ? target(ended.clientX, ended.clientY, drag) : null;
      if (pending.dragging) swallowClick();
      stop();
      if (project) latest.current.drop(drag, project);
    };
    const cancel = (ended: globalThis.PointerEvent) => { if (ended.pointerId === pointerId) stop(); };
    const key = (pressed: KeyboardEvent) => {
      if (pressed.key !== "Escape" || !gesture.current?.dragging) return;
      pressed.preventDefault(); pressed.stopPropagation();
      swallowClick();
      stop();
    };
    const blur = () => stop();
    // The click that ends a drag would open the session that was dragged: the next one is dropped.
    const swallowClick = () => {
      const click = (clicked: MouseEvent) => { clicked.preventDefault(); clicked.stopPropagation(); };
      owner.addEventListener("click", click, { capture: true, once: true });
      setTimeout(() => owner.removeEventListener("click", click, { capture: true }), 0);
    };
    const stop = () => {
      if (gesture.current?.pointerId !== pointerId) return;
      gesture.current = null;
      owner.removeEventListener("pointermove", move, true);
      owner.removeEventListener("pointerup", up, true);
      owner.removeEventListener("pointercancel", cancel, true);
      owner.removeEventListener("keydown", key, true);
      owner.defaultView?.removeEventListener("blur", blur);
      owner.body.classList.remove("session-row-dragging");
      setView(null);
    };
    gesture.current = { pointerId, drag, x: event.clientX, y: event.clientY, dragging: false, stop };
    owner.addEventListener("pointermove", move, true);
    owner.addEventListener("pointerup", up, true);
    owner.addEventListener("pointercancel", cancel, true);
    owner.addEventListener("keydown", key, true);
    owner.defaultView?.addEventListener("blur", blur);
  }
  /** What makes a row draggable; nothing for a session that cannot move now. */
  const source = (drag: SessionDrag | null): SessionRowDrag => drag ? { "data-movable": "true", onPointerDown: event => start(event, drag) } : {};
  return { view, source };
}
