import { useEffect, useRef, useState, type PointerEvent } from "react";

/** What a drag of a project carries: the project, and the space it is dragged out of (null from the list of every project). */
export type ProjectDrag = Readonly<{ projectId: string; from: string | null }>;

/**
 * Where a project is being dragged: its name by the pointer, the drop target under it (the identifier of a space,
 * the empty string for the list of every project, null where nothing takes it), and whether a drop there adds it to
 * a space without taking it out of another.
 * The label is drawn in `layer`, the dialog of the row (a window of Settings moves by a transform, which would
 * place a fixed label from its own corner) or the body of the page.
 */
export type ProjectDragView = Readonly<{ name: string; x: number; y: number; over: string | null; copy: boolean; layer: HTMLElement }>;

/** What makes the row of a project draggable; empty for a row that cannot move. */
export type ProjectRowDrag = Readonly<{ "data-movable"?: "true"; onPointerDown?: (event: PointerEvent<HTMLElement>) => void }>;

/** The drop target at a point of the page, by the `data-space-drop` of its element: a space, or the empty string for the list. */
export function spaceDropAt(element: Element | null): string | null {
  return element?.closest<HTMLElement>("[data-space-drop]")?.dataset.spaceDrop ?? null;
}

const threshold = 5;

/**
 * Drags the row of a project onto a space with the pointer. Native HTML5 drags go through the drop handling of the
 * window's host (WebView2/OLE), which ends them at once: the drag stays in the page, as the drag of a session does.
 * Ctrl or Alt, held when the button is released, copies; a press that does not move is a click; a drag swallows the
 * click that ends it, and Escape or leaving the window cancels it.
 */
export function useProjectDrag(accepts: (drag: ProjectDrag, target: string, copy: boolean) => boolean,
  drop: (drag: ProjectDrag, target: string, copy: boolean) => void) {
  const [view, setView] = useState<ProjectDragView | null>(null);
  const gesture = useRef<{ pointerId: number; stop: () => void } | null>(null);
  const latest = useRef({ accepts, drop }); latest.current = { accepts, drop };
  useEffect(() => () => gesture.current?.stop(), []);

  function start(event: PointerEvent<HTMLElement>, drag: ProjectDrag, name: string) {
    if (gesture.current || !event.isPrimary || event.button !== 0 || event.metaKey || event.shiftKey
      || (event.target as HTMLElement).closest("button, a, input, textarea, select")) return;
    const pointerId = event.pointerId;
    const owner = event.currentTarget.ownerDocument;
    const origin = { x: event.clientX, y: event.clientY };
    const layer = event.currentTarget.closest("dialog") ?? owner.body;
    let dragging = false, x = origin.x, y = origin.y, copy = event.ctrlKey || event.altKey;
    const target = () => {
      const at = spaceDropAt(owner.elementFromPoint(x, y));
      return at !== null && latest.current.accepts(drag, at, copy) ? at : null;
    };
    const show = () => { const over = target(); setView({ name, x, y, over, copy: !!over && (drag.from === null || copy), layer }); };
    const move = (moved: globalThis.PointerEvent) => {
      if (moved.pointerId !== pointerId) return;
      if (!(moved.buttons & 1)) { stop(); return; }
      x = moved.clientX; y = moved.clientY; copy = moved.ctrlKey || moved.altKey;
      if (!dragging && Math.hypot(x - origin.x, y - origin.y) < threshold) return;
      if (!dragging) { dragging = true; owner.getSelection()?.removeAllRanges(); owner.body.classList.add("space-project-dragging"); }
      moved.preventDefault();
      show();
    };
    const up = (ended: globalThis.PointerEvent) => {
      if (ended.pointerId !== pointerId) return;
      x = ended.clientX; y = ended.clientY; copy = ended.ctrlKey || ended.altKey;
      const at = dragging ? target() : null;
      if (dragging) swallowClick();
      stop();
      if (at !== null) latest.current.drop(drag, at, copy);
    };
    const cancel = (ended: globalThis.PointerEvent) => { if (ended.pointerId === pointerId) stop(); };
    const key = (pressed: KeyboardEvent) => {
      if (!dragging) return;
      if (pressed.key === "Control" || pressed.key === "Alt") { copy = pressed.ctrlKey || pressed.altKey; show(); return; }
      if (pressed.key !== "Escape" || pressed.type !== "keydown") return;
      // Escape ends the drag, not Settings.
      pressed.preventDefault(); pressed.stopPropagation();
      stop();
      // The button is still held: the click comes when it is released, and is swallowed then. A release outside the
      // window leaves no click: the next press forgets it.
      const release = (ended: globalThis.PointerEvent) => {
        if (ended.type === "pointerup" && ended.pointerId !== pointerId) return;
        owner.removeEventListener("pointerup", release, true);
        owner.removeEventListener("pointerdown", release, true);
        if (ended.type === "pointerup") swallowClick();
      };
      owner.addEventListener("pointerup", release, true);
      owner.addEventListener("pointerdown", release, true);
    };
    const blur = () => stop();
    // The click that ends a drag would open the space it is released on: the next one is dropped.
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
      owner.removeEventListener("keyup", key, true);
      owner.defaultView?.removeEventListener("blur", blur);
      owner.body.classList.remove("space-project-dragging");
      setView(null);
    };
    gesture.current = { pointerId, stop };
    owner.addEventListener("pointermove", move, true);
    owner.addEventListener("pointerup", up, true);
    owner.addEventListener("pointercancel", cancel, true);
    owner.addEventListener("keydown", key, true);
    owner.addEventListener("keyup", key, true);
    owner.defaultView?.addEventListener("blur", blur);
  }
  /** What makes the row of a project draggable; nothing when spaces cannot be changed. */
  const source = (drag: ProjectDrag | null, name: string): ProjectRowDrag => drag ? { "data-movable": "true", onPointerDown: event => start(event, drag, name) } : {};
  return { view, source };
}
