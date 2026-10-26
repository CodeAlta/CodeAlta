import { useEffect, useRef, useState, type PointerEvent } from "react";

/**
 * What is dragged in the Explorer: a label, which goes before, after or under another one, or a project, which is
 * given a label (and leaves the one it is dragged out of, unless Ctrl or Alt is held).
 */
export type RailDrag = Readonly<{ kind: "label"; labelId: string } | { kind: "project"; projectId: string; from: string | null }>;

/** Where a drag would drop: on a label (`into`), before or after it, or on the projects of no label (`labelId` empty). */
export type RailDrop = Readonly<{ labelId: string; place: "before" | "after" | "into" }>;

/**
 * The drag under way: its name by the pointer, and where it would drop. The name is drawn in `layer`, the dialog of
 * the row (a window of Settings moves by a transform) or the body of the page.
 */
export type RailDragView = Readonly<{ name: string; x: number; y: number; over: RailDrop | null; copy: boolean; layer: HTMLElement }>;

/** The drop target at a point of the page, by the `data-label-drop` of its element: before, after or into a label for a label. */
export function railDropAt(element: Element | null, y: number, kind: RailDrag["kind"]): RailDrop | null {
  const target = element?.closest<HTMLElement>("[data-label-drop]");
  if (!target) return null;
  const labelId = target.dataset.labelDrop ?? "";
  if (kind === "project" || !labelId) return { labelId, place: "into" };
  const box = target.getBoundingClientRect();
  const at = box.height > 0 ? (y - box.top) / box.height : 0.5;
  return { labelId, place: at < 0.3 ? "before" : at > 0.7 ? "after" : "into" };
}

const threshold = 5;
// What a press on a row starts nothing from: its buttons, its chevron, its forms.
const ignored = ".project-row-action, .project-actions-trigger, .label-row-action, .tree-twist, input, textarea, select, a, [role=menu], dialog";

/**
 * Drags labels and projects in the Explorer with the pointer, as the drags of sessions and of spaces do: native
 * HTML5 drags end at once in the window's host. A press that does not move is a click; a drag swallows the click
 * that ends it, and Escape or leaving the window cancels it. Ctrl or Alt, held when the button is released, gives a
 * project the label without taking the one it was dragged out of.
 */
export function useRailDrag(accepts: (drag: RailDrag, drop: RailDrop) => boolean, drop: (drag: RailDrag, target: RailDrop, copy: boolean) => void) {
  const [view, setView] = useState<RailDragView | null>(null);
  const gesture = useRef<{ pointerId: number; stop: () => void } | null>(null);
  const latest = useRef({ accepts, drop }); latest.current = { accepts, drop };
  useEffect(() => () => gesture.current?.stop(), []);

  function start(event: PointerEvent<HTMLElement>, drag: RailDrag, name: string) {
    if (gesture.current || !event.isPrimary || event.button !== 0 || event.metaKey || event.shiftKey || (event.target as HTMLElement).closest(ignored)) return;
    const pointerId = event.pointerId;
    const owner = event.currentTarget.ownerDocument;
    const origin = { x: event.clientX, y: event.clientY };
    const layer = event.currentTarget.closest("dialog") ?? owner.body;
    let dragging = false, x = origin.x, y = origin.y, copy = event.ctrlKey || event.altKey;
    const target = () => {
      const at = railDropAt(owner.elementFromPoint(x, y), y, drag.kind);
      return at && latest.current.accepts(drag, at) ? at : null;
    };
    const show = () => setView({ name, x, y, over: target(), copy, layer });
    const move = (moved: globalThis.PointerEvent) => {
      if (moved.pointerId !== pointerId) return;
      if (!(moved.buttons & 1)) { stop(); return; }
      x = moved.clientX; y = moved.clientY; copy = moved.ctrlKey || moved.altKey;
      if (!dragging && Math.hypot(x - origin.x, y - origin.y) < threshold) return;
      if (!dragging) { dragging = true; owner.getSelection()?.removeAllRanges(); owner.body.classList.add("rail-dragging"); }
      moved.preventDefault();
      show();
    };
    const up = (ended: globalThis.PointerEvent) => {
      if (ended.pointerId !== pointerId) return;
      x = ended.clientX; y = ended.clientY; copy = ended.ctrlKey || ended.altKey;
      const at = dragging ? target() : null;
      if (dragging) swallowClick();
      stop();
      if (at) latest.current.drop(drag, at, copy);
    };
    const cancel = (ended: globalThis.PointerEvent) => { if (ended.pointerId === pointerId) stop(); };
    const key = (pressed: KeyboardEvent) => {
      if (!dragging) return;
      if (pressed.key === "Control" || pressed.key === "Alt") { copy = pressed.ctrlKey || pressed.altKey; show(); return; }
      if (pressed.key !== "Escape" || pressed.type !== "keydown") return;
      pressed.preventDefault(); pressed.stopPropagation();
      stop();
      // The button is still held: the click that comes when it is released is swallowed.
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
    // The click that ends a drag would select the row it is released on: the next one is dropped.
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
      owner.body.classList.remove("rail-dragging");
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
  /** What makes a row draggable; nothing when it cannot move. */
  const source = (drag: RailDrag | null, name: string) => drag ? { onPointerDown: (event: PointerEvent<HTMLElement>) => start(event, drag, name) } : {};
  return { view, source };
}

/**
 * Where a label goes when it is dropped on another one: under it (`into`), or before or after it, among its siblings.
 * `index` counts the siblings without the label moved. Null when it cannot go there.
 */
export function labelMoveTarget(labels: readonly Readonly<{ id: string; parent: string | null }>[], moved: string, drop: RailDrop):
  Readonly<{ parent: string | null; index: number }> | null {
  if (!drop.labelId || drop.labelId === moved) return null;
  const target = labels.find(label => label.id === drop.labelId);
  if (!target) return null;
  if (drop.place === "into") return { parent: target.id, index: -1 };
  const siblings = labels.filter(label => label.parent === target.parent && label.id !== moved);
  const at = siblings.findIndex(label => label.id === target.id);
  return { parent: target.parent, index: at + (drop.place === "after" ? 1 : 0) };
}
