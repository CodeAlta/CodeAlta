import { useCallback, useEffect, useMemo, useRef, useState } from "react";

// A long list draws only the rows in view, and a margin around them: the others are two spacers of their height. Rows may
// have different heights: each row is measured once drawn, and a row not drawn yet counts as the average of those that were.
// The part in view is the part of the list that the list and the elements above it that clip it let be seen, so it works
// whether the list scrolls itself or a page around it does.

/** The number of rows from which a list draws only those in view. */
export const rowWindowThreshold = 150;

/** The height a row is given before any row is measured, in pixels. */
const defaultRowHeight = 60;

/** The rows drawn beyond the part in view, in pixels on each side. */
const overscan = 600;

/** What a list draws: its rows from `start` to `end`, both included, and the space of the rows before and after them. */
export type RowSlice = Readonly<{ start: number; end: number; before: number; after: number }>;

/**
 * The rows to draw for the part of a list in view, from the top of the list to its bottom in pixels: the rows that touch
 * that part widened by the margin, with the space of the rows before and after. A list with no row draws nothing.
 */
export function sliceRows(heights: readonly number[], top: number, bottom: number, margin = overscan): RowSlice {
  if (heights.length === 0) return { start: 0, end: -1, before: 0, after: 0 };
  let offset = 0, start = -1, end = heights.length - 1, before = 0;
  for (let index = 0; index < heights.length; index++) {
    const next = offset + heights[index];
    if (start < 0 && next > top - margin) { start = index; before = offset; }
    if (offset >= bottom + margin) { end = index - 1; break; }
    offset = next;
  }
  if (start < 0) { start = heights.length - 1; before = offset - heights[start]; }
  end = Math.max(start, end);
  let total = before;
  for (let index = start; index < heights.length; index++) total += heights[index];
  let drawn = 0;
  for (let index = start; index <= end; index++) drawn += heights[index];
  return { start, end, before, after: Math.max(0, total - before - drawn) };
}

/** The part of an element that can be seen: its box cut by the window and by every element above it that clips what overflows. */
export function visibleSpan(element: HTMLElement): { top: number; bottom: number } {
  let top = 0, bottom = window.innerHeight;
  for (let current: HTMLElement | null = element; current; current = current.parentElement) {
    const overflow = getComputedStyle(current).overflowY;
    if (current !== element && overflow === "visible") continue;
    const box = current.getBoundingClientRect();
    top = Math.max(top, box.top);
    bottom = Math.min(bottom, box.bottom);
  }
  return { top, bottom };
}

/**
 * Draws a long list a window at a time. Below `rowWindowThreshold` rows, or when `enabled` is false, every row is drawn. The
 * list puts `anchor` on an element at the very top of its content (the spacer before the rows), and `measure` on each row,
 * with the key of the row in `data-row-key`.
 */
export function useRowWindow(keys: readonly string[], enabled = true) {
  const windowed = enabled && keys.length >= rowWindowThreshold;
  const heights = useRef(new Map<string, number>());
  const [revision, setRevision] = useState(0);
  const [view, setView] = useState({ top: 0, bottom: 1200 });
  const anchor = useRef<HTMLElement | null>(null);

  const update = useCallback(() => {
    const element = anchor.current;
    if (!element) return;
    // The top of the content of the list is the anchor: the part in view is measured from it.
    const origin = element.getBoundingClientRect().top;
    const span = visibleSpan(element.parentElement ?? element);
    const next = { top: Math.round(span.top - origin), bottom: Math.round(span.bottom - origin) };
    setView(current => Math.abs(current.top - next.top) < 40 && Math.abs(current.bottom - next.bottom) < 40 ? current : next);
  }, []);

  const observer = useMemo(() => typeof ResizeObserver === "undefined" ? null : new ResizeObserver(entries => {
    let changed = false;
    for (const entry of entries) {
      const element = entry.target as HTMLElement, key = element.dataset.rowKey;
      // A row that left the document is not one of zero height.
      if (!key || !element.isConnected) continue;
      const height = Math.round(element.getBoundingClientRect().height);
      if (height > 0 && heights.current.get(key) !== height) { heights.current.set(key, height); changed = true; }
    }
    if (changed) setRevision(value => value + 1);
  }), []);
  useEffect(() => () => observer?.disconnect(), [observer]);

  useEffect(() => {
    if (!windowed) return;
    update();
    // A scroll of the list or of anything around it moves the part in view; so does a change of the size of the window.
    document.addEventListener("scroll", update, { capture: true, passive: true });
    window.addEventListener("resize", update);
    const resized = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(update);
    if (anchor.current?.parentElement) resized?.observe(anchor.current.parentElement);
    return () => { document.removeEventListener("scroll", update, { capture: true }); window.removeEventListener("resize", update); resized?.disconnect(); };
  }, [windowed, update]);

  const slice = useMemo<RowSlice>(() => {
    if (!windowed) return { start: 0, end: keys.length - 1, before: 0, after: 0 };
    const known = [...heights.current.values()];
    const estimate = known.length ? known.reduce((sum, value) => sum + value, 0) / known.length : defaultRowHeight;
    return sliceRows(keys.map(key => heights.current.get(key) ?? estimate), view.top, view.bottom);
    // The heights are read again when a row was measured: `revision` says so.
  }, [windowed, keys, view, revision]);

  const measure = useCallback((element: HTMLElement | null) => { if (element && windowed) observer?.observe(element); }, [observer, windowed]);
  const anchorRef = useCallback((element: HTMLElement | null) => { anchor.current = element; }, []);
  return { windowed, slice, measure, anchor: anchorRef, update };
}
