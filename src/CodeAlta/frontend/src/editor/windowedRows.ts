import { useCallback, useLayoutEffect, useState, type RefObject } from "react";

/** Which rows of a long list are drawn: those in view and a few around them, with the heights that stand for the rest. */
export type RowWindow = Readonly<{ first: number; last: number; before: number; after: number }>;

/** The rows to draw for a scroll position; `overscan` rows are kept on each side so that scrolling shows no gap. */
export function rowWindow(count: number, rowHeight: number, scrollTop: number, viewHeight: number, overscan = 12): RowWindow {
  if (count <= 0 || rowHeight <= 0) return { first: 0, last: 0, before: 0, after: 0 };
  const first = Math.max(0, Math.min(count, Math.floor(scrollTop / rowHeight)) - overscan);
  const last = Math.min(count, Math.ceil((scrollTop + Math.max(viewHeight, rowHeight)) / rowHeight) + overscan);
  return { first, last, before: first * rowHeight, after: (count - last) * rowHeight };
}

/** The scroll position that brings a row into view, or null when it is already in view. */
export function rowScrollTop(index: number, rowHeight: number, scrollTop: number, viewHeight: number): number | null {
  if (index < 0) return null;
  const top = index * rowHeight;
  if (top < scrollTop) return top;
  return top + rowHeight > scrollTop + viewHeight ? top + rowHeight - viewHeight : null;
}

/**
 * Draws only the rows of a list of equal-height rows that are in view. The scrolling element is `container`:
 * give it the returned `onScroll`, and put a spacer of `before` pixels above the drawn rows and one of `after` below.
 */
export function useWindowedRows(container: RefObject<HTMLElement | null>, count: number, rowHeight: number) {
  const [view, setView] = useState({ top: 0, height: 0 });
  const measure = useCallback(() => {
    const node = container.current;
    if (node) setView(current => current.top === node.scrollTop && current.height === node.clientHeight ? current : { top: node.scrollTop, height: node.clientHeight });
  }, [container]);
  useLayoutEffect(() => {
    const node = container.current;
    if (!node) return;
    measure();
    const resized = new ResizeObserver(measure);
    resized.observe(node);
    return () => resized.disconnect();
  }, [container, measure]);
  const reveal = useCallback((index: number) => {
    const node = container.current;
    const top = node ? rowScrollTop(index, rowHeight, node.scrollTop, node.clientHeight) : null;
    if (node && top !== null) node.scrollTop = top;
  }, [container, rowHeight]);
  return { ...rowWindow(count, rowHeight, view.top, view.height), onScroll: measure, reveal };
}
