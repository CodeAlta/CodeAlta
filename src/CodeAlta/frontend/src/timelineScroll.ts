import { useEffect, useRef, useState } from "react";

export type ScrollMetrics = Readonly<{ scrollTop: number; scrollHeight: number; clientHeight: number }>;

export const timelineFollowThreshold = 72;

export function distanceFromBottom(metrics: ScrollMetrics): number {
  return Math.max(0, metrics.scrollHeight - metrics.clientHeight - metrics.scrollTop);
}

export function shouldFollowTimeline(metrics: ScrollMetrics, threshold = timelineFollowThreshold): boolean {
  return distanceFromBottom(metrics) <= threshold;
}

export function bottomScrollTop(metrics: Pick<ScrollMetrics, "scrollHeight" | "clientHeight">): number {
  return Math.max(0, metrics.scrollHeight - metrics.clientHeight);
}

export function preservePrependScrollTop(previous: ScrollMetrics, nextScrollHeight: number): number {
  return Math.max(0, previous.scrollTop + nextScrollHeight - previous.scrollHeight);
}

// App-owned, bounded in-memory preferences. A new mounted session owns its own selection;
// restoring an old position must wait for its journal (or an explicit read error), not the loading skeleton.
export function createTimelineScrollMemory() {
  const positions = new Map<string, { top: number; following: boolean }>();
  function remember(id: string, top: number, following: boolean) {
    positions.delete(id);
    positions.set(id, { top, following });
    if (positions.size > 64) positions.delete(positions.keys().next().value!);
  }
  return {
    open(id: string) {
      const saved = positions.get(id) ?? { top: 0, following: true };
      let following = saved.following;
      let restoring = true; // Ignore loading/layout scroll events even for a new following session.
      let pendingFinish = false;
      let suppressedTop: number | null = null;
      return {
        following: () => following,
        scroll(metrics: ScrollMetrics) {
          if (restoring) return following;
          if (suppressedTop !== null && metrics.scrollTop === suppressedTop) {
            suppressedTop = null; // Browser may report the programmatic restoration as a scroll.
            return following;
          }
          suppressedTop = null;
          // A read error/short journal can clamp the DOM to zero without a user following the tail.
          if (!following && bottomScrollTop(metrics) === 0) return false;
          following = shouldFollowTimeline(metrics);
          remember(id, metrics.scrollTop, following);
          return following;
        },
        settle(metrics: ScrollMetrics) {
          if (!restoring || pendingFinish) return null;
          pendingFinish = true;
          suppressedTop = following ? bottomScrollTop(metrics) : Math.min(saved.top, bottomScrollTop(metrics));
          return suppressedTop;
        },
        finishRestore() { if (pendingFinish) restoring = pendingFinish = false; },
        jump(metrics: ScrollMetrics) {
          restoring = pendingFinish = false;
          suppressedTop = null;
          following = true;
          remember(id, bottomScrollTop(metrics), true);
        },
      };
    },
  };
}

// Keep scroller, history settlement and follow wiring together so mounted layout changes
// receive the same per-session scroll policy as content mutations.
export function useTimelinePosition(sessionId: string, memory: ReturnType<typeof createTimelineScrollMemory>) {
  const elementRef = useRef<HTMLDivElement>(null);
  const [selection] = useState(() => memory.open(sessionId));
  const [following, setFollowing] = useState(selection.following);
  const restoreFrame = useRef(0);
  useEffect(() => {
    const element = elementRef.current;
    if (!element) return;
    let frame = 0;
    const scrollBottom = () => {
      if (!selection.following()) return;
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => { element.scrollTop = bottomScrollTop(element); });
    };
    // DOM mutations do not report layout growth from images, fonts or CSS. Observe the
    // scroller's own viewport and each direct panel, including panels mounted later.
    const sizes = new ResizeObserver(scrollBottom);
    sizes.observe(element);
    const changed = () => {
      for (let index = 0; index < element.children.length; index++) sizes.observe(element.children.item(index)!);
      scrollBottom();
    };
    const observer = new MutationObserver(changed);
    observer.observe(element, { childList: true, subtree: true, characterData: true });
    changed();
    return () => { observer.disconnect(); sizes.disconnect(); cancelAnimationFrame(frame); cancelAnimationFrame(restoreFrame.current); };
  }, [selection]);
  function settled() {
    const element = elementRef.current;
    if (!element) return;
    const top = selection.settle(element);
    if (top === null) return;
    element.scrollTop = top;
    restoreFrame.current = requestAnimationFrame(() => selection.finishRestore());
  }
  function scroll(element: HTMLDivElement) { setFollowing(selection.scroll(element)); }
  function jump() {
    const element = elementRef.current;
    if (element) {
      selection.jump(element);
      element.scrollTop = bottomScrollTop(element);
    }
    setFollowing(true);
  }
  return { elementRef, following, settled, scroll, jump };
}
