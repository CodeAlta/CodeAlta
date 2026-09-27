import type { SessionReferenceSearchResponse } from "#neoastra";
import { activeProjectReference, insertProjectReference } from "./projectReferences";

type LifetimeState = { generation: number; revision: number; key: string; available: boolean };
// Only the explicitly bracketed native open and close may advance the modal fence.
export function createReferencePopupLifetime(read: () => LifetimeState) {
  let expected = read();
  let phase: "captured" | "open" | "closed" | "retired" = "captured";
  const sameScope = (value: LifetimeState) => value.available && value.revision === expected.revision && value.key === expected.key;
  const current = () => phase !== "retired" && sameScope(read()) && read().generation === expected.generation;
  function transition(kind: "open" | "closed", action: () => void) {
    if (!current() || (kind === "open" ? phase !== "captured" : phase !== "open")) return false;
    const before = expected.generation;
    action();
    const after = read();
    if (!sameScope(after) || after.generation !== before + 1) { phase = "retired"; return false; }
    expected = after; phase = kind; return true;
  }
  return { current, open: (action: () => void) => transition("open", action),
    close: (action: () => void) => transition("closed", action), retire: () => { phase = "retired"; } };
}
export type ReferencePopupLifetime = ReturnType<typeof createReferencePopupLifetime>;
export function referencePopupReadiness(sourceCurrent: () => boolean, composing: () => boolean) {
  // Composition pauses actions, not the durable captured-source lifetime.
  const current = sourceCurrent;
  return { current, ready: () => current() && !composing() };
}
export function closeReferencePopup(lifetime: ReferencePopupLifetime, current: () => boolean, close: () => void) {
  // Native close can synchronously run beforetoggle/focus handlers. Admission
  // before that callback is not permission to edit or schedule focus afterwards.
  if (!current() || !lifetime.close(close) || !current()) {
    lifetime.retire(); return false;
  }
  return true;
}
export function createReferenceSearchFence() {
  let revision = 0;
  return { cancel: () => { revision++; }, capture: (current: () => boolean) => {
    const expected = ++revision;
    return () => revision === expected && current();
  } };
}
export type ReferenceInputState = { node: object | null; connected: boolean; disabled: boolean; text: string;
  start: number; end: number; revision: number };

export function captureReferenceInput(original: ReferenceInputState, read: () => ReferenceInputState, lifetime: ReferencePopupLifetime) {
  const span = original.start === original.end ? activeProjectReference(original.text, original.start) : null;
  if (!span || !original.node || !original.connected || original.disabled || !lifetime.current()) return null;
  let consumed = false;
  const current = () => {
    const value = read();
    return lifetime.current() && value.node === original.node && value.connected && !value.disabled
      && value.text === original.text && value.start === original.start && value.end === original.end && value.revision === original.revision;
  };
  return { span, original, current, choose: (path: string, directory: boolean) => {
    if (consumed || !current()) return null;
    const next = insertProjectReference(original.text, span.start, span.end, path, directory);
    if (next) consumed = true;
    return next;
  } };
}

export function validReferenceSearch(value: SessionReferenceSearchResponse, epoch: string) {
  return value.epoch === epoch && typeof value.status === "string" && typeof value.omitted === "boolean"
    && Array.isArray(value.items) && value.items.length <= 64
    && value.items.every(row => row && typeof row.path === "string" && typeof row.directory === "boolean"
      && typeof row.recent === "boolean" && !!insertProjectReference("@", 0, 1, row.path, row.directory))
    && new Set(value.items.map(row => row.path)).size === value.items.length;
}

export function referencePopupKey(event: { key: string; isComposing?: boolean; keyCode?: number; repeat?: boolean;
  ctrlKey?: boolean; altKey?: boolean; metaKey?: boolean; shiftKey?: boolean; defaultPrevented?: boolean }) {
  if (event.isComposing || event.keyCode === 229 || event.repeat || event.defaultPrevented
    || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return "none";
  return event.key === "ArrowDown" ? "next" : event.key === "ArrowUp" ? "previous"
    : event.key === "Enter" ? "choose" : event.key === "Escape" ? "cancel" : "none";
}
