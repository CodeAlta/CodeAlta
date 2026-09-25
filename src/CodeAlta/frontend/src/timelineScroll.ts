import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { NewestHistoryRequest, NewestHistoryResult } from "./HistoryPanel";
import { historyMessage } from "./history";

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

export type MessageNavigation = "messagePrevious" | "messageNext" | "messageFirst";
export type MessageNavigationResult = Readonly<{ status: "moved" | "boundary" | "unavailable"; label?: string }>;

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
      let pausedTop: number | null = null;
      return {
        following: () => following,
        scroll(metrics: ScrollMetrics) {
          if (restoring) return following;
          if (pausedTop !== null && metrics.scrollTop === pausedTop) return false;
          pausedTop = null;
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
          pausedTop = null;
          following = true;
          remember(id, bottomScrollTop(metrics), true);
        },
        pauseAt(top: number) {
          following = false;
          suppressedTop = top; // A keyboard/older-page programmatic scroll must not re-enable follow at the bottom.
          pausedTop = top;
          remember(id, top, false);
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
  const prependMetrics = useRef<{ metrics: ScrollMetrics; anchor: HTMLElement | null; top: number } | null>(null);
  const messageAnchor = useRef<{ row: HTMLElement; top: number } | null>(null);
  const resetMessageNavigation = useCallback(() => { messageAnchor.current = null; }, []);
  const pauseIfUnfollowed = useCallback(() => {
    if (selection.following()) return;
    const element = elementRef.current;
    if (element) selection.pauseAt(element.scrollTop);
    setFollowing(false);
  }, [selection]);
  useEffect(() => {
    const element = elementRef.current;
    if (!element) return;
    let frame = 0;
    const scrollBottom = () => {
      if (!selection.following()) return;
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => { if (selection.following()) element.scrollTop = bottomScrollTop(element); });
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
  function beforeOlderPage() {
    const element = elementRef.current;
    if (!element) return;
    const viewport = element.getBoundingClientRect();
    const anchor = Array.from(element.querySelectorAll<HTMLElement>(".timeline-message"))
      .find(row => row.getBoundingClientRect().bottom > viewport.top) ?? null;
    prependMetrics.current = { metrics: { scrollTop: element.scrollTop, scrollHeight: element.scrollHeight, clientHeight: element.clientHeight },
      anchor, top: anchor?.getBoundingClientRect().top ?? 0 };
    selection.pauseAt(element.scrollTop);
    setFollowing(false);
  }
  function afterOlderPage() {
    const element = elementRef.current;
    const previous = prependMetrics.current;
    prependMetrics.current = null;
    if (!element || !previous) return;
    element.scrollTop = previous.anchor?.isConnected && element.contains(previous.anchor)
      ? element.scrollTop + previous.anchor.getBoundingClientRect().top - previous.top
      : preservePrependScrollTop(previous.metrics, element.scrollHeight);
    selection.pauseAt(element.scrollTop);
  }
  function jump() {
    const element = elementRef.current;
    if (element) {
      selection.jump(element);
      element.scrollTop = bottomScrollTop(element);
    }
    setFollowing(true);
  }
  function pause() {
    const element = elementRef.current;
    if (element) selection.pauseAt(element.scrollTop);
    setFollowing(false);
  }
  function messageReady() {
    const element = elementRef.current;
    return !!element?.querySelector('.history[data-window-ready="true"] .timeline-message.message-user, .history[data-window-ready="true"] .timeline-message.message-assistant');
  }
  function navigateMessage(action: MessageNavigation): MessageNavigationResult {
    const element = elementRef.current;
    if (!element || !messageReady()) return { status: "unavailable" };
    const rows = Array.from(element.querySelectorAll<HTMLElement>(
      '.history[data-window-ready="true"] .timeline-message.message-user, .history[data-window-ready="true"] .timeline-message.message-assistant'));
    const viewport = element.getBoundingClientRect();
    const anchor = messageAnchor.current;
    const anchoredIndex = anchor?.top === element.scrollTop ? rows.indexOf(anchor.row) : -1;
    const firstAtOrBelow = rows.findIndex(row => row.getBoundingClientRect().top >= viewport.top - 1);
    const index = action === "messageFirst" ? 0 : action === "messageNext"
      ? anchoredIndex >= 0 ? anchoredIndex + 1 : rows.findIndex(row => row.getBoundingClientRect().top > viewport.top + 1)
      : anchoredIndex >= 0 ? anchoredIndex - 1 : firstAtOrBelow < 0 ? rows.length - 1 : firstAtOrBelow - 1;
    if (index < 0 || index >= rows.length) {
      selection.pauseAt(element.scrollTop);
      setFollowing(false);
      return { status: "boundary" };
    }
    const row = rows[index];
    element.scrollTop += row.getBoundingClientRect().top - viewport.top;
    messageAnchor.current = { row, top: element.scrollTop };
    selection.pauseAt(element.scrollTop);
    setFollowing(false);
    return { status: "moved", label: row.querySelector(".message-body")?.textContent?.trim().slice(0, 120) };
  }
  return { elementRef, following, settled, scroll, jump, pause, beforeOlderPage, afterOlderPage,
    messageReady, navigateMessage, resetMessageNavigation, pauseIfUnfollowed };
}

// Only a user-requested, generation-matched settled tail can opt a reader into follow.
// Pending intent belongs to the mounted selection, never to a global history request.
export function useExplicitNewestHistory(sessionId: string, projectId: string | null, epoch: string | null,
  position: ReturnType<typeof useTimelinePosition>, onNotice: (message: string) => void) {
  const requestRef = useRef<NewestHistoryRequest | null>(null);
  const pending = useRef<{ generation: number; sessionId: string; projectId: string | null; epoch: string | null;
    wasFollowing: boolean; anchor: HTMLElement | null; anchorTop: number } | null>(null);
  useLayoutEffect(() => {
    if (pending.current && (pending.current.sessionId !== sessionId || pending.current.projectId !== projectId ||
      pending.current.epoch !== epoch)) {
      pending.current = null;
      onNotice("");
    }
  }, [sessionId, projectId, epoch, onNotice]);
  function cancel() {
    if (!pending.current) return;
    pending.current = null;
    onNotice("Newest history is still loading; automatic follow canceled by newer user navigation.");
  }
  const onTarget = useCallback((generation: number): boolean => {
    if (pending.current?.generation === generation) return true;
    pending.current = null;
    return false;
  }, []);
  function latest() {
    const generation = requestRef.current?.();
    if (generation === undefined) return;
    const element = position.elementRef.current;
    const anchor = element ? Array.from(element.querySelectorAll<HTMLElement>(".timeline-message"))
      .find(row => row.getBoundingClientRect().bottom > element.getBoundingClientRect().top) ?? null : null;
    pending.current = { generation, sessionId, projectId, epoch, wasFollowing: position.following,
      anchor, anchorTop: anchor?.getBoundingClientRect().top ?? 0 };
    onNotice("Refreshing the newest persisted history window…");
  }
  function onScroll() {
    const intent = pending.current;
    if (intent?.anchor?.isConnected && Math.abs(intent.anchor.getBoundingClientRect().top - intent.anchorTop) > 2)
      cancel(); // Layout anchoring keeps the row still; an independently moved viewport does not.
  }
  function onResult(result: NewestHistoryResult) {
    const intent = pending.current;
    if (!intent || intent.generation !== result.generation || intent.sessionId !== result.sessionId ||
      intent.sessionId !== sessionId || intent.projectId !== projectId || intent.epoch !== epoch) return;
    pending.current = null;
    if (result.error) {
      if (!intent.wasFollowing) position.pause();
      onNotice(`Newest history refresh failed: ${historyMessage(result.error)} Follow preference unchanged.`);
    }
    else {
      position.jump();
      onNotice("Newest persisted history window loaded; following visible content.");
    }
  }
  return { requestRef, available: () => requestRef.current !== null, pending: () => pending.current !== null,
    latest, cancel, onScroll, onTarget, onResult };
}
